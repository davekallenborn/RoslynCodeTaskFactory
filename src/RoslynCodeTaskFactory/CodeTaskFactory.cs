﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using RoslynCodeTaskFactory.Internal;
using RoslynCodeTaskFactory.Properties;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace RoslynCodeTaskFactory
{
    /// <inheritdoc />
    /// <summary>
    /// A task factory for compiling in-line code into a <see cref="T:Microsoft.Build.Utilities.Task" />.
    /// </summary>
    /// <example>
    /// </example>
    public sealed partial class CodeTaskFactory : ITaskFactory
    {
        /// <summary>
        /// A set of default namespaces to add so that user does not have to include them.  Make sure that these are covered
        /// by the list of <see cref="DefaultReferences"/>.
        /// </summary>
        internal static readonly IList<string> DefaultNamespaces = new List<string>
        {
            "Microsoft.Build.Framework",
            "Microsoft.Build.Utilities",
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Text",
        };

        /// <summary>
        /// A set of default references to add so that the user does not have to include them.
        /// </summary>
        internal static readonly IDictionary<string, IEnumerable<string>> DefaultReferences = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Common assembly references for all code languages
            //
            {
                String.Empty,
                new List<string>
                {
                    "Microsoft.Build.Framework",
                    "Microsoft.Build.Utilities.Core",
                    "netstandard",
                }
            },
            // CSharp specific assembly references
            //
            {
                "CS",
                new List<string>
                {
                    "System.Runtime",
                }
            },
            // Visual Basic specific assembly references
            //
            {
                "VB",
                new List<string>
                {
                    "System.Diagnostics.Debug"
                }
            }
        };

        internal static readonly IDictionary<string, ISet<string>> ValidCodeLanguages = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // This dictionary contains a mapping between code languages and known aliases (like "C#").  Everything is case-insensitive.
            //
            { "CS", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CSharp", "C#" } },
            { "VB", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VisualBasic", "Visual Basic" } },
        };

        /// <summary>
        /// The name of a subdirectory that contains reference assemblies.
        /// </summary>
        private const string ReferenceAssemblyDirectoryName = "ref";

        /// <summary>
        /// A cache of <see cref="TaskInfo"/> objects and their corresponding compiled assembly.  This cache ensures that two of the exact same code task
        /// declarations are not compiled multiple times.
        /// </summary>
        private static readonly ConcurrentDictionary<TaskInfo, Assembly> CompiledAssemblyCache = new ConcurrentDictionary<TaskInfo, Assembly>();

        /// <summary>
        /// Stores a cache of loaded assemblies by the <see cref="AppDomain.AssemblyResolve"/> handler.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Assembly> LoadedAssemblyCache = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores the path to the directory that this assembly is located in.
        /// </summary>
        private static readonly Lazy<string> ThisAssemblyDirectoryLazy = new Lazy<string>(() => Path.GetDirectoryName(typeof(CodeTaskFactory).GetTypeInfo().Assembly.ManifestModule.FullyQualifiedName));

        /// <summary>
        /// Stores the parent directory of this assembly's directory.
        /// </summary>
        private static readonly Lazy<string> ThisAssemblyParentDirectoryLazy = new Lazy<string>(() => Path.GetDirectoryName(ThisAssemblyDirectoryLazy.Value));

        /// <summary>
        /// Stores an instance of a <see cref="TaskLoggingHelper"/> for logging messages.
        /// </summary>
        private TaskLoggingHelper _log;

        /// <summary>
        /// Stores the parameters parsed in the &lt;UsingTask /&gt;.
        /// </summary>
        private TaskPropertyInfo[] _parameters;

        /// <summary>
        /// Stores the task name parsed in the &lt;UsingTask /&gt;.
        /// </summary>
        private string _taskName;

        /// <inheritdoc cref="ITaskFactory.FactoryName"/>
        public string FactoryName => "Roslyn Code Task Factory";

        /// <inheritdoc />
        /// <summary>
        /// Gets the <see cref="T:System.Type" /> of the compiled task.
        /// </summary>
        public Type TaskType { get; private set; }

        /// <inheritdoc cref="ITaskFactory.CleanupTask(ITask)"/>
        public void CleanupTask(ITask task)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= AppDomain_AssemblyResolve;
        }

        /// <inheritdoc cref="ITaskFactory.CreateTask(IBuildEngine)"/>
        public ITask CreateTask(IBuildEngine taskFactoryLoggingHost)
        {
            // The type of the task has already been determined and the assembly is already loaded after compilation so
            // just create an instance of the type and return it.
            //
            return Activator.CreateInstance(TaskType) as ITask;
        }

        /// <inheritdoc cref="ITaskFactory.GetTaskParameters"/>
        public TaskPropertyInfo[] GetTaskParameters()
        {
            return _parameters;
        }

        /// <inheritdoc cref="ITaskFactory.Initialize"/>
        public bool Initialize(string taskName, IDictionary<string, TaskPropertyInfo> parameterGroup, string taskBody, IBuildEngine taskFactoryLoggingHost)
        {
            WaitForDebuggerIfConfigured();

            _log = new TaskLoggingHelper(taskFactoryLoggingHost, taskName)
            {
                TaskResources = Strings.ResourceManager,
            };

            _taskName = taskName;

            _parameters = parameterGroup.Values.ToArray();

            // Attempt to parse and extract everything from the <UsingTask />
            //
            if (!TryLoadTaskBody(_log, _taskName, taskBody, _parameters, out TaskInfo taskInfo))
            {
                return false;
            }

            // Attempt to compile an assembly (or get one from the cache)
            //
            if (!TryCompileInMemoryAssembly(taskFactoryLoggingHost, taskInfo, out Assembly assembly))
            {
                return false;
            }

            if (assembly != null)
            {
                TaskType = assembly.GetExportedTypes().FirstOrDefault(type => type.Name.Equals(taskName));
            }

            if (TaskType != null)
            {
                // Perform automatic parameter detection if the user supplied a class.
                // This reduces the burden of the developer by not requiring them to
                // manually specify <ParameterGroup/>.
                //
                if (taskInfo.CodeType == CodeTaskFactoryCodeType.Class)
                {
                    PropertyInfo[] properties = TaskType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                    _parameters = new TaskPropertyInfo[properties.Length];
                    for (int i = 0; i < properties.Length; i++)
                    {
                        PropertyInfo property = properties[i];
                        _parameters[i] = new TaskPropertyInfo(
                            property.Name,
                            property.PropertyType,
                            property.GetCustomAttribute<OutputAttribute>() != null,
                            property.GetCustomAttribute<RequiredAttribute>() != null);
                    }
                }
            }

            AppDomain.CurrentDomain.AssemblyResolve += AppDomain_AssemblyResolve;

            // Initialization succeeded if we found a type matching the task name from the compiled assembly
            //
            return TaskType != null;
        }

        /// <summary>
        /// Gets the full source code by applying an appropriate template based on the current <see cref="CodeTaskFactoryCodeType"/>.
        /// </summary>
        internal static void ApplySourceCodeTemplate(TaskInfo taskInfo, ICollection<TaskPropertyInfo> parameters)
        {
            if (taskInfo?.SourceCode != null && CodeTemplates.ContainsKey(taskInfo.CodeLanguage) && CodeTemplates[taskInfo.CodeLanguage].ContainsKey(taskInfo.CodeType))
            {
                string usingStatement = String.Join(Environment.NewLine, GetNamespaceStatements(taskInfo.CodeLanguage, DefaultNamespaces.Union(taskInfo.Namespaces, StringComparer.OrdinalIgnoreCase)));
                string properties = parameters == null ? String.Empty : String.Join(Environment.NewLine, GetPropertyStatements(taskInfo.CodeLanguage, parameters).Select(i => $"        {i}"));

                // Apply the corresponding template based on the code type
                //
                taskInfo.SourceCode = String.Format(CodeTemplates[taskInfo.CodeLanguage][taskInfo.CodeType], usingStatement, taskInfo.Name, properties, taskInfo.SourceCode);
            }
        }

        ///  <summary>
        ///  Parses and validates the body of the &lt;UsingTask /&gt;.
        ///  </summary>
        ///  <param name="log">A <see cref="TaskLoggingHelper"/> used to log events during parsing.</param>
        ///  <param name="taskName">The name of the task.</param>
        ///  <param name="taskBody">The raw inner XML string of the &lt;UsingTask />&gt; to parse and validate.</param>
        /// <param name="parameters">An <see cref="ICollection{TaskPropertyInfo}"/> containing parameters for the task.</param>
        /// <param name="taskInfo">A <see cref="TaskInfo"/> object that receives the details of the parsed task.</param>
        /// <returns><code>true</code> if the task body was successfully parsed, otherwise <code>false</code>.</returns>
        ///  <remarks>
        ///  The <paramref name="taskBody"/> will look like this:
        ///  <![CDATA[
        ///
        ///    <Using Namespace="Namespace" />
        ///    <Reference Include="AssemblyName|AssemblyPath" />
        ///    <Code Type="Fragment|Method|Class" Language="cs|vb" Source="Path">
        ///      // Source code
        ///    </Code>
        ///
        ///  ]]>
        ///  </remarks>
        internal static bool TryLoadTaskBody(TaskLoggingHelper log, string taskName, string taskBody, ICollection<TaskPropertyInfo> parameters, out TaskInfo taskInfo)
        {
            taskInfo = new TaskInfo()
            {
                CodeLanguage = "CS",
                CodeType = CodeTaskFactoryCodeType.Fragment,
                Name = taskName,
            };

            XDocument document;

            try
            {
                // For legacy reasons, the inner XML of the <UsingTask /> has no document element.  So we have to add a top-level
                // element around it so it can be parsed.
                //
                document = XDocument.Parse($"<Task>{taskBody}</Task>");
            }
            catch (Exception e)
            {
                log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidTaskXml", e.Message);
                return false;
            }

            if (document.Root == null)
            {
                log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidTaskXml");
                return false;
            }

            XElement codeElement = null;

            // Loop through the children, ignoring ones we don't care about, parsing valid ones, and logging an error if we
            // encounter any element that is not recognized.
            //
            foreach (XNode node in document.Root.Nodes()
                .Where(i => i.NodeType != XmlNodeType.Comment && i.NodeType != XmlNodeType.Whitespace))
            {
                switch (node.NodeType)
                {
                    case XmlNodeType.Element:
                        XElement child = (XElement) node;

                        // Parse known elements and go to the default case if its an unknown element
                        //
                        if (child.Name.LocalName.Equals("Code", StringComparison.OrdinalIgnoreCase))
                        {
                            if (codeElement != null)
                            {
                                // Only one <Code /> element is allowed.
                                //
                                log.LogErrorWithCodeFromResources("CodeTaskFactory_MultipleCodeNodes");
                                return false;
                            }

                            codeElement = child;
                        }
                        else if (child.Name.LocalName.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                        {
                            XAttribute includeAttribute = child.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Include", StringComparison.OrdinalIgnoreCase));

                            if (String.IsNullOrWhiteSpace(includeAttribute?.Value))
                            {
                                // A <Reference Include="" /> is not allowed.
                                //
                                log.LogErrorWithCodeFromResources("CodeTaskFactory_AttributeEmpty", "Include", "Reference");
                                return false;
                            }

                            // Store the reference in the list
                            //
                            taskInfo.References.Add(includeAttribute.Value.Trim());
                        }
                        else if (child.Name.LocalName.Equals("Using", StringComparison.OrdinalIgnoreCase))
                        {
                            XAttribute namespaceAttribute = child.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Namespace", StringComparison.OrdinalIgnoreCase));

                            if (String.IsNullOrWhiteSpace(namespaceAttribute?.Value))
                            {
                                // A <Using Namespace="" /> is not allowed
                                //
                                log.LogErrorWithCodeFromResources("CodeTaskFactory_AttributeEmpty", "Namespace", "Using");
                                return false;
                            }

                            // Store the using in the list
                            //
                            taskInfo.Namespaces.Add(namespaceAttribute.Value.Trim());
                        }
                        else
                        {
                            log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidElementLocation",
                                child.Name.LocalName,
                                document.Root.Name.LocalName,
                                "  Valid child elements are <Code>, <Reference>, and <Using>.");
                            return false;
                        }

                        break;

                    default:
                        log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidElementLocation",
                            node.NodeType,
                            document.Root.Name.LocalName,
                            "  Valid child elements are <Code>, <Reference>, and <Using>.");
                        return false;
                }
            }

            if (codeElement == null)
            {
                // <Code /> element is required so if we didn't find it then we need to error
                //
                log.LogErrorWithCodeFromResources("CodeTaskFactory_CodeElementIsMissing", taskName);
                return false;
            }

            // Copies the source code from the inner text of the <Code /> element.  This might be override later if the user specified
            // a file instead.
            //
            taskInfo.SourceCode = codeElement.Value;

            // Parse the attributes of the <Code /> element
            //
            XAttribute languageAttribute = codeElement.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Language", StringComparison.OrdinalIgnoreCase));
            XAttribute sourceAttribute = codeElement.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Source", StringComparison.OrdinalIgnoreCase));
            XAttribute typeAttribute = codeElement.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Type", StringComparison.OrdinalIgnoreCase));

            if (sourceAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(sourceAttribute.Value))
                {
                    // A <Code Source="" /> is not allowed
                    //
                    log.LogErrorWithCodeFromResources("CodeTaskFactory_AttributeEmpty", "Source", "Code");
                    return false;
                }

                // Instead of using the inner text of the <Code /> element, read the specified file as source code
                //
                taskInfo.CodeType = CodeTaskFactoryCodeType.Class;
                taskInfo.SourceCode = File.ReadAllText(sourceAttribute.Value.Trim());
            }
            else if (typeAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(typeAttribute.Value))
                {
                    // A <Code Type="" /> is not allowed
                    //
                    log.LogErrorWithCodeFromResources("CodeTaskFactory_AttributeEmpty", "Type", "Code");
                    return false;
                }

                // Attempt to parse the code type as a CodeTaskFactoryCodeType
                //
                if (!Enum.TryParse(typeAttribute.Value.Trim(), ignoreCase: true, result: out CodeTaskFactoryCodeType codeType))
                {
                    log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidCodeType", typeAttribute.Value, String.Join(", ", Enum.GetNames(typeof(CodeTaskFactoryCodeType))));
                    return false;
                }

                taskInfo.CodeType = codeType;
            }

            // Warn that <ParameterGroup/> is ignored if any parameters are supplied when Type="Class".
            //
            if (taskInfo.CodeType == CodeTaskFactoryCodeType.Class && parameters.Any())
            {
                log.LogWarningWithCodeFromResources("CodeTaskFactory_ParameterGroupIgnoredForCodeTypeClass");
            }

            if (languageAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(languageAttribute.Value))
                {
                    // A <Code Language="" /> is not allowed
                    //
                    log.LogErrorWithCodeFromResources("CodeTaskFactory_AttributeEmpty", "Language", "Code");
                    return false;
                }

                if (ValidCodeLanguages.ContainsKey(languageAttribute.Value))
                {
                    // The user specified one of the primary code languages using our vernacular
                    //
                    taskInfo.CodeLanguage = languageAttribute.Value.ToUpperInvariant();
                }
                else
                {
                    bool foundValidCodeLanguage = false;

                    // Attempt to map the user specified value as an alias to our vernacular for code languages
                    //
                    foreach (string validLanguage in ValidCodeLanguages.Keys)
                    {
                        if (ValidCodeLanguages[validLanguage].Contains(languageAttribute.Value))
                        {
                            taskInfo.CodeLanguage = validLanguage;
                            foundValidCodeLanguage = true;
                            break;
                        }
                    }

                    if (!foundValidCodeLanguage)
                    {
                        // The user specified a code language we don't support
                        //
                        log.LogErrorWithCodeFromResources("CodeTaskFactory_InvalidCodeLanguage", languageAttribute.Value, String.Join(", ", ValidCodeLanguages.Keys));
                        return false;
                    }
                }
            }

            if (String.IsNullOrWhiteSpace(taskInfo.SourceCode))
            {
                // The user did not specify a path to source code or source code within the <Code /> element.
                //
                log.LogErrorWithCodeFromResources("CodeTaskFactory_NoSourceCode");
                return false;
            }

            ApplySourceCodeTemplate(taskInfo, parameters);

            return true;
        }

        /// <summary>
        /// Attempts to resolve assembly references that were specified by the user.
        /// </summary>
        /// <param name="log">A <see cref="TaskLoggingHelper"/> used for logging.</param>
        /// <param name="taskInfo">A <see cref="TaskInfo"/> object containing details about the task.</param>
        /// <param name="items">Receives the list of full paths to resolved assemblies.</param>
        /// <returns><code>true</code> if all assemblies could be resolved, otherwise <code>false</code>.</returns>
        /// <remarks>The user can specify a short name like My.Assembly or My.Assembly.dll.  In this case we'll
        /// attempt to look it up in the directory containing our reference assemblies.  They can also specify a
        /// full path and we'll do no resolution.  At this time, these are the only two resolution mechanisms.
        /// Perhaps in the future this could be more powerful by using NuGet to resolve assemblies but we think
        /// that is too complicated for a simple in-line task.  If users have more complex requirements, they
        /// can compile their own task library.</remarks>
        internal static bool TryResolveAssemblyReferences(TaskLoggingHelper log, TaskInfo taskInfo, out ITaskItem[] items)
        {
            // Store the list of resolved assemblies because a user can specify a short name or a full path
            //
            ISet<string> resolvedAssemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keeps track if there were one or more unresolved assemblies
            //
            bool hasInvalidReference = false;

            // Start with the user specified references and include all of the default references that are language agnostic
            //
            IEnumerable<string> references = taskInfo.References.Union(DefaultReferences[String.Empty]);

            if (DefaultReferences.ContainsKey(taskInfo.CodeLanguage))
            {
                // Append default references for the specific language
                //
                references = references.Union(DefaultReferences[taskInfo.CodeLanguage]);
            }

            // Loop through the user specified references as well as the default references
            //
            foreach (string reference in references)
            {
                // The user specified a full path to an assembly, so there is no need to resolve
                //
                if (File.Exists(reference))
                {
                    // The path could be relative like ..\Assembly.dll so we need to get the full path
                    //
                    resolvedAssemblyReferences.Add(Path.GetFullPath(reference));
                    continue;
                }

                // Attempt to "resolve" the assembly by getting a full path to our distributed reference assemblies
                //
                string assemblyFileName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || reference.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? reference
                    : $"{reference}.dll";

                string possiblePath = Path.Combine(ThisAssemblyParentDirectoryLazy.Value, ReferenceAssemblyDirectoryName, assemblyFileName);

                if (File.Exists(possiblePath))
                {
                    resolvedAssemblyReferences.Add(possiblePath);
                    continue;
                }

                // Could not resolve the assembly.  We currently don't support looking things up the GAC so that in-line task
                // assemblies are portable across platforms
                //
                log.LogErrorWithCodeFromResources("CodeTaskFactory_CouldNotFindReferenceAssembly", reference);
                hasInvalidReference = true;
            }

            // Transform the list of resolved assemblies to TaskItems if they were all resolved
            //
            items = hasInvalidReference ? null : resolvedAssemblyReferences.Select(i => (ITaskItem) new TaskItem(i)).ToArray();

            return !hasInvalidReference;
        }

        /// <summary>
        /// Gets the namespace statements for the specified code language.
        /// </summary>
        /// <param name="codeLanguage">The code language to use.</param>
        /// <param name="namespaces">An <see cref="IEnumerable{String}"/> containing namespaces to generate statements for.</param>
        /// <returns>An <see cref="IEnumerable{String}"/> containing namespace statements for the specified code language.</returns>
        private static IEnumerable<string> GetNamespaceStatements(string codeLanguage, IEnumerable<string> namespaces)
        {
            foreach (string @namespace in namespaces.Union(DefaultNamespaces, StringComparer.OrdinalIgnoreCase))
            {
                switch (codeLanguage)
                {
                    case "CS":
                        yield return $"using {@namespace};";
                        break;

                    case "VB":
                        yield return $"Imports {@namespace}";
                        break;
                }
            }
        }

        /// <summary>
        /// Gets the property statements for the specified language.
        /// </summary>
        /// <param name="codeLanguage">The code language to use.</param>
        /// <param name="parameters">An <see cref="IEnumerable{TaskPropertyInfo}"/> containing information about the property statements to generate.</param>
        /// <returns>An <see cref="IEnumerable{String}"/> containing property statements for the specified code language.</returns>
        private static IEnumerable<string> GetPropertyStatements(string codeLanguage, IEnumerable<TaskPropertyInfo> parameters)
        {
            foreach (TaskPropertyInfo taskPropertyInfo in parameters)
            {
                switch (codeLanguage)
                {
                    case "CS":
                        if (taskPropertyInfo.Output)
                        {
                            yield return "[Microsoft.Build.Framework.OutputAttribute]";
                        }

                        if (taskPropertyInfo.Required)
                        {
                            yield return "[Microsoft.Build.Framework.RequiredAttribute]";
                        }

                        yield return $"public {taskPropertyInfo.PropertyType.FullName} {taskPropertyInfo.Name} {{ get; set; }}";
                        break;

                    case "VB":
                        if (taskPropertyInfo.Output)
                        {
                            yield return "<Microsoft.Build.Framework.OutputAttribute>";
                        }

                        if (taskPropertyInfo.Required)
                        {
                            yield return "<Microsoft.Build.Framework.RequiredAttribute>";
                        }

                        yield return $"Public Property {taskPropertyInfo.Name} As {taskPropertyInfo.PropertyType.FullName}";
                        break;
                }
            }
        }

        /// <summary>
        /// A custom <see cref="AppDomain.AssemblyResolve"/> handler which loads assemblies needed for the CodeTaskFactory to work.
        /// </summary>
        /// <returns></returns>
        private Assembly AppDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName assemblyName = new AssemblyName(args.Name);

            // Guess the path based on the name
            //
            string candidateAssemblyPath = Path.Combine(ThisAssemblyDirectoryLazy.Value, $"{assemblyName.Name}.dll");

            // See if the assembly has already been loaded from this path
            //
            if (LoadedAssemblyCache.TryGetValue(candidateAssemblyPath, out Assembly loadedAssembly))
            {
                return loadedAssembly;
            }

            if (File.Exists(candidateAssemblyPath))
            {
                loadedAssembly = Assembly.LoadFrom(candidateAssemblyPath);

                // Cache the loaded assembly for later if necessary
                //
                LoadedAssemblyCache.TryAdd(candidateAssemblyPath, loadedAssembly);

                return loadedAssembly;
            }

            return null;
        }

        /// <summary>
        /// Loads an assembly from the specified path.
        /// </summary>
        /// <param name="path">The full path to the assembly.</param>
        /// <returns>An <see cref="Assembly"/> object for the loaded assembly.</returns>
        private Assembly LoadAssembly(string path)
        {
            // This method must use reflection so that this task will work on .NET Framework 4.6 (System.Reflection.Assembly)
            // and .NET Core 1.0 (System.Runtime.Loader.AssemblyLoadContext)
            //
            MethodInfo loadMethodInfo = Type.GetType("System.Reflection.Assembly")?.GetMethod("Load", new[] { typeof(byte[]) });

            if (loadMethodInfo != null)
            {
                return loadMethodInfo.Invoke(null, new object[] { File.ReadAllBytes(path) }) as Assembly;
            }

            Type assemblyLoadContextType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext");

            PropertyInfo defaultPropertyInfo = assemblyLoadContextType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);

            if (defaultPropertyInfo != null)
            {
                object defaultAssemblyLoadContext = defaultPropertyInfo.GetValue(null);

                MethodInfo loadFromStreamMethodInfo = assemblyLoadContextType.GetMethod("LoadFromStream", new[] { typeof(Stream) });

                if (loadFromStreamMethodInfo != null)
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        return loadFromStreamMethodInfo.Invoke(defaultAssemblyLoadContext, new object[] { stream }) as Assembly;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to compile the current source code and load the assembly into memory.
        /// </summary>
        /// <param name="buildEngine">An <see cref="IBuildEngine"/> to use give to the compiler task so that messages can be logged.</param>
        /// <param name="taskInfo">A <see cref="TaskInfo"/> object containing details about the task.</param>
        /// <param name="assembly">The <see cref="Assembly"/> if the source code be compiled and loaded, otherwise <code>null</code>.</param>
        /// <returns><code>true</code> if the source code could be compiled and loaded, otherwise <code>null</code>.</returns>
        private bool TryCompileInMemoryAssembly(IBuildEngine buildEngine, TaskInfo taskInfo, out Assembly assembly)
        {
            // First attempt to get a compiled assembly from the cache
            //
            if (CompiledAssemblyCache.TryGetValue(taskInfo, out assembly))
            {
                return true;
            }

            // The source code cannot actually be compiled "in memory" so instead the source code is written to disk in
            // the temp folder as well as the assembly.  After compilation, the source code and assembly are deleted.
            //
            string sourceCodePath = Path.GetTempFileName();
            string assemblyPath = Path.GetTempFileName();

            // Delete the code file unless compilation failed or the environment variable MSBUILDLOGCODETASKFACTORYOUTPUT
            // is set (which allows for debugging problems)
            //
            bool deleteSourceCodeFile = Environment.GetEnvironmentVariable("MSBUILDLOGCODETASKFACTORYOUTPUT") == null;

            try
            {
                // Create the code
                //
                File.WriteAllText(sourceCodePath, taskInfo.SourceCode);

                // Execute the compiler.  We re-use the existing build task by hosting it and giving it our IBuildEngine instance for logging
                //
                ManagedCompiler managedCompiler = null;

                // User specified values are translated using a dictionary of known aliases and checking if the user specified
                // a valid code language is already done
                //
                if (taskInfo.CodeLanguage.Equals("CS"))
                {
                    managedCompiler = new Csc
                    {
                        NoStandardLib = true,
                    };

                    string toolExe = Environment.GetEnvironmentVariable("CscToolExe");

                    if (!String.IsNullOrEmpty(toolExe))
                    {
                        managedCompiler.ToolExe = toolExe;
                    }
                }
                else if (taskInfo.CodeLanguage.Equals("VB"))
                {
                    managedCompiler = new Vbc
                    {
                        //NoStandardLib = true,
                        //RootNamespace = "InlineCode",
                        //OptionCompare = "Binary",
                        //OptionExplicit = true,
                        //OptionInfer = true,
                        //OptionStrict = false,
                        //Verbosity = "Verbose",
                    };

                    string toolExe = Environment.GetEnvironmentVariable("VbcToolExe");

                    if (!String.IsNullOrEmpty(toolExe))
                    {
                        managedCompiler.ToolExe = toolExe;
                    }
                }

                if (!TryResolveAssemblyReferences(_log, taskInfo, out ITaskItem[] references))
                {
                    return false;
                }

                if (managedCompiler != null)
                {
                    // Pass a wrapped BuildEngine which will lower the message importance so it doesn't clutter up the build output
                    //
                    managedCompiler.BuildEngine = new BuildEngineWithLowImportance(buildEngine);
                    managedCompiler.Deterministic = true;
                    managedCompiler.NoConfig = true;
                    managedCompiler.NoLogo = true;
                    managedCompiler.Optimize = false;
                    managedCompiler.OutputAssembly = new TaskItem(assemblyPath);
                    managedCompiler.References = references.ToArray();
                    managedCompiler.Sources = new ITaskItem[] { new TaskItem(sourceCodePath) };
                    managedCompiler.TargetType = "Library";
                    managedCompiler.UseSharedCompilation = false;

                    _log.LogMessageFromResources(MessageImportance.Low, "CodeTaskFactory_CompilingAssembly");

                    if (!managedCompiler.Execute())
                    {
                        deleteSourceCodeFile = false;

                        _log.LogErrorWithCodeFromResources("CodeTaskFactory_FindSourceFileAt", sourceCodePath);

                        return false;
                    }
                }

                // Return the assembly which is loaded into memory
                //
                assembly = LoadAssembly(assemblyPath);

                // Attempt to cache the compiled assembly
                //
                CompiledAssemblyCache.TryAdd(taskInfo, assembly);

                return true;
            }
            catch (Exception e)
            {
                _log.LogErrorFromException(e);
                return false;
            }
            finally
            {
                if (File.Exists(assemblyPath))
                {
                    File.Delete(assemblyPath);
                }

                if (deleteSourceCodeFile && File.Exists(sourceCodePath))
                {
                    File.Delete(sourceCodePath);
                }
            }
        }

        /// <summary>
        /// Waits for a user to attach a debugger.
        /// </summary>
        private void WaitForDebuggerIfConfigured()
        {
            if (!String.Equals(Environment.GetEnvironmentVariable("ROSLYNCODETASKFACTORY_DEBUG"), "1"))
            {
                return;
            }

            Process currentProcess = Process.GetCurrentProcess();

            Console.WriteLine(Strings.CodeTaskFactory_WaitingForDebugger, currentProcess.MainModule.FileName, currentProcess.Id);

            while (!Debugger.IsAttached)
            {
                Thread.Sleep(200);
            }

            Debugger.Break();
        }
    }
}