// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;

#nullable disable

namespace Microsoft.Build.Tasks
{
    public sealed class RoslynCodeTaskFactory : ITaskFactory
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

            #if RUNTIME_TYPE_NETCORE || NETCOREAPP
            "System.Text.Json",
            "System.Memory",
            #endif
        };

        /// <summary>
        /// A set of default references to add so that the user does not have to include them.
        /// </summary>
        internal static readonly IDictionary<string, IEnumerable<string>> DefaultReferences = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Common assembly references for all code languages
            {
                String.Empty,
                new List<string>
                {
                    "Microsoft.Build.Framework",
                    "Microsoft.Build.Utilities.Core",
                    "mscorlib",
                    "netstandard",
                       #if RUNTIME_TYPE_NETCORE || NETCOREAPP
            "System.Text.Json",
            "System.Memory",
            #endif
                }
            },
            // CSharp specific assembly references
            {
                "CS",
                new List<string>()
            },
            // Visual Basic specific assembly references
            {
                "VB",
                new List<string>()
            }
        };

        internal static readonly IDictionary<string, ISet<string>> ValidCodeLanguages = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // This dictionary contains a mapping between code languages and known aliases (like "C#").  Everything is case-insensitive.
            { "CS", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CSharp", "C#" } },
            { "VB", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VisualBasic", "Visual Basic" } },
        };

        /// <summary>
        /// The name of a subdirectory that contains reference assemblies.
        /// </summary>
        private const string ReferenceAssemblyDirectoryName = "ref";

        /// <summary>
        /// A cache of <see cref="RoslynCodeTaskFactoryTaskInfo"/> objects and their corresponding compiled assembly.  This cache ensures that two of the exact same code task
        /// declarations are not compiled multiple times.
        /// </summary>
        private static readonly ConcurrentDictionary<RoslynCodeTaskFactoryTaskInfo, Assembly> CompiledAssemblyCache = new ConcurrentDictionary<RoslynCodeTaskFactoryTaskInfo, Assembly>();

        /// <summary>
        /// Stores the path to the directory that this assembly is located in.
        /// </summary>
        private static readonly Lazy<string> ThisAssemblyDirectoryLazy = new Lazy<string>(() => Path.GetDirectoryName(typeof(RoslynCodeTaskFactory).GetTypeInfo().Assembly.ManifestModule.FullyQualifiedName));

        /// <summary>
        /// Stores an instance of a <see cref="TaskLoggingHelper"/> for logging messages.
        /// </summary>
        private TaskLoggingHelper _log;

        /// <summary>
        /// Stores functions that were added to the current app domain. Should be removed once we're finished.
        /// </summary>
        private ResolveEventHandler handlerAddedToAppDomain = null;

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

        /// <summary>
        /// Gets the <see cref="Type"/> of the compiled task.
        /// </summary>
        public Type TaskType { get; private set; }

        /// <inheritdoc cref="ITaskFactory.CleanupTask(ITask)"/>
        public void CleanupTask(ITask task)
        {
            if (handlerAddedToAppDomain is not null)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= handlerAddedToAppDomain;
            }
        }

        /// <inheritdoc cref="ITaskFactory.CreateTask(IBuildEngine)"/>
        public ITask CreateTask(IBuildEngine taskFactoryLoggingHost)
        {
            // The type of the task has already been determined and the assembly is already loaded after compilation so
            // just create an instance of the type and return it.
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
            _log = new TaskLoggingHelper(taskFactoryLoggingHost, taskName)
            {
                TaskResources = AssemblyResources.PrimaryResources,
                HelpKeywordPrefix = "MSBuild."
            };

            _taskName = taskName;

            _parameters = parameterGroup.Values.ToArray();

            // Attempt to parse and extract everything from the <UsingTask />
            if (!TryLoadTaskBody(_log, _taskName, taskBody, _parameters, out RoslynCodeTaskFactoryTaskInfo taskInfo))
            {
                return false;
            }

            // Attempt to compile an assembly (or get one from the cache)
            if (!TryCompileInMemoryAssembly(taskFactoryLoggingHost, taskInfo, out Assembly assembly))
            {
                return false;
            }

            if (assembly != null)
            {
                Type[] exportedTypes = assembly.GetExportedTypes();

                // Find an exact match by class name or a partial match by full name
                TaskType = exportedTypes.FirstOrDefault(type => type.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                           ?? exportedTypes.Where(i => i.FullName != null).FirstOrDefault(type => type.FullName.Equals(taskName, StringComparison.OrdinalIgnoreCase) || type.FullName.EndsWith(taskName, StringComparison.OrdinalIgnoreCase));

                if (TaskType == null)
                {
                    _log.LogErrorWithCodeFromResources("CodeTaskFactory.CouldNotFindTaskInAssembly", taskName);
                    return false;
                }

                if (taskInfo.CodeType == RoslynCodeTaskFactoryCodeType.Class && parameterGroup.Count == 0)
                {
                    // If the user specified a whole class but nothing in <ParameterGroup />, automatically derive
                    // the task parameters from their type's properties
                    _parameters = TaskType?.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Select(i => new TaskPropertyInfo(
                            i.Name,
                            i.PropertyType,
                            i.GetCustomAttribute<OutputAttribute>() != null,
                            i.GetCustomAttribute<RequiredAttribute>() != null))
                        .ToArray();
                }
            }

            // Initialization succeeded if we found a type matching the task name from the compiled assembly
            return TaskType != null;
        }

        /// <summary>
        /// Gets the full source code by applying an appropriate template based on the current <see cref="RoslynCodeTaskFactoryCodeType"/>.
        /// </summary>
        internal static string GetSourceCode(RoslynCodeTaskFactoryTaskInfo taskInfo, ICollection<TaskPropertyInfo> parameters)
        {
            if (taskInfo.CodeType == RoslynCodeTaskFactoryCodeType.Class)
            {
                return taskInfo.SourceCode;
            }

            CodeTypeDeclaration codeTypeDeclaration = new CodeTypeDeclaration
            {
                IsClass = true,
                Name = taskInfo.Name,
                TypeAttributes = TypeAttributes.Public,
                Attributes = MemberAttributes.Final
            };
            codeTypeDeclaration.BaseTypes.Add("Microsoft.Build.Utilities.Task");

            foreach (TaskPropertyInfo propertyInfo in parameters)
            {
                CreateProperty(codeTypeDeclaration, propertyInfo.Name, propertyInfo.PropertyType);
            }

            if (taskInfo.CodeType == RoslynCodeTaskFactoryCodeType.Fragment)
            {
                CodeMemberProperty successProperty = CreateProperty(codeTypeDeclaration, "Success", typeof(bool), true);

                CodeMemberMethod executeMethod = new CodeMemberMethod
                {
                    Name = "Execute",
                    // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
                    Attributes = MemberAttributes.Override | MemberAttributes.Public,
                    ReturnType = new CodeTypeReference(typeof(Boolean))
                };
                executeMethod.Statements.Add(new CodeSnippetStatement(taskInfo.SourceCode));
                executeMethod.Statements.Add(new CodeMethodReturnStatement(new CodePropertyReferenceExpression(null, successProperty.Name)));
                codeTypeDeclaration.Members.Add(executeMethod);
            }
            else
            {
                codeTypeDeclaration.Members.Add(new CodeSnippetTypeMember(taskInfo.SourceCode));
            }

            CodeNamespace codeNamespace = new CodeNamespace("InlineCode");

            codeNamespace.Imports.AddRange(DefaultNamespaces.Union(taskInfo.Namespaces, StringComparer.OrdinalIgnoreCase).Select(i => new CodeNamespaceImport(i)).ToArray());

            codeNamespace.Types.Add(codeTypeDeclaration);

            CodeCompileUnit codeCompileUnit = new CodeCompileUnit();

            codeCompileUnit.Namespaces.Add(codeNamespace);

            using (CodeDomProvider provider = CodeDomProvider.CreateProvider(taskInfo.CodeLanguage))
            {
                using (StringWriter writer = new StringWriter(new StringBuilder(), CultureInfo.CurrentCulture))
                {
                    provider.GenerateCodeFromCompileUnit(codeCompileUnit, writer, new CodeGeneratorOptions
                    {
                        BlankLinesBetweenMembers = true,
                        VerbatimOrder = true
                    });

                    return writer.ToString();
                }
            }
        }

        /// <summary>
        /// Parses and validates the body of the &lt;UsingTask /&gt;.
        /// </summary>
        /// <param name="log">A <see cref="TaskLoggingHelper"/> used to log events during parsing.</param>
        /// <param name="taskName">The name of the task.</param>
        /// <param name="taskBody">The raw inner XML string of the &lt;UsingTask />&gt; to parse and validate.</param>
        /// <param name="parameters">An <see cref="ICollection{TaskPropertyInfo}"/> containing parameters for the task.</param>
        /// <param name="taskInfo">A <see cref="RoslynCodeTaskFactoryTaskInfo"/> object that receives the details of the parsed task.</param>
        /// <returns><c>true</c> if the task body was successfully parsed, otherwise <c>false</c>.</returns>
        /// <remarks>
        /// The <paramref name="taskBody"/> will look like this:
        /// <code>
        /// <![CDATA[
        ///
        /// <Using Namespace="Namespace" />
        /// <Reference Include="AssemblyName|AssemblyPath" />
        /// <Code Type="Fragment|Method|Class" Language="cs|vb" Source="Path">
        ///   // Source code
        /// </Code>
        ///
        /// ]]>
        /// </code>
        /// </remarks>
        internal static bool TryLoadTaskBody(TaskLoggingHelper log, string taskName, string taskBody, ICollection<TaskPropertyInfo> parameters, out RoslynCodeTaskFactoryTaskInfo taskInfo)
        {
            taskInfo = new RoslynCodeTaskFactoryTaskInfo
            {
                CodeLanguage = "CS",
                CodeType = RoslynCodeTaskFactoryCodeType.Fragment,
                Name = taskName,
            };

            XDocument document;

            try
            {
                // For legacy reasons, the inner XML of the <UsingTask /> has no document element.  So we have to add a top-level
                // element around it so it can be parsed.
                document = XDocument.Parse($"<Task>{taskBody}</Task>");
            }
            catch (Exception e)
            {
                log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidTaskXml", e.Message);
                return false;
            }

            if (document.Root == null)
            {
                log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidTaskXml", String.Empty);
                return false;
            }

            XElement codeElement = null;

            // Loop through the children, ignoring ones we don't care about, parsing valid ones, and logging an error if we
            // encounter any element that is not recognized.
            foreach (XNode node in document.Root.Nodes()
                .Where(i => i.NodeType != XmlNodeType.Comment && i.NodeType != XmlNodeType.Whitespace))
            {
                switch (node.NodeType)
                {
                    case XmlNodeType.Element:
                        XElement child = (XElement)node;

                        // Parse known elements and go to the default case if its an unknown element
                        if (child.Name.LocalName.Equals("Code"))
                        {
                            if (codeElement != null)
                            {
                                // Only one <Code /> element is allowed.
                                log.LogErrorWithCodeFromResources("CodeTaskFactory.MultipleCodeNodes");
                                return false;
                            }

                            codeElement = child;
                        }
                        else if (child.Name.LocalName.Equals("Reference"))
                        {
                            XAttribute includeAttribute = child.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Include"));

                            if (String.IsNullOrWhiteSpace(includeAttribute?.Value))
                            {
                                // A <Reference Include="" /> is not allowed.
                                log.LogErrorWithCodeFromResources("CodeTaskFactory.AttributeEmptyWithTaskElement", "Include", "Reference", taskName);
                                return false;
                            }

                            // Store the reference in the list
                            taskInfo.References.Add(includeAttribute.Value.Trim());
                        }
                        else if (child.Name.LocalName.Equals("Using"))
                        {
                            XAttribute namespaceAttribute = child.Attributes().FirstOrDefault(i => i.Name.LocalName.Equals("Namespace"));

                            if (String.IsNullOrWhiteSpace(namespaceAttribute?.Value))
                            {
                                // A <Using Namespace="" /> is not allowed
                                log.LogErrorWithCodeFromResources("CodeTaskFactory.AttributeEmptyWithElement", "Namespace", "Using");
                                return false;
                            }

                            // Store the using in the list
                            taskInfo.Namespaces.Add(namespaceAttribute.Value.Trim());
                        }
                        else
                        {
                            log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidElementLocation",
                                child.Name.LocalName,
                                document.Root.Name.LocalName);
                            return false;
                        }

                        break;

                    default:
                        log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidElementLocation",
                            node.NodeType,
                            document.Root.Name.LocalName);
                        return false;
                }
            }

            if (codeElement == null)
            {
                // <Code /> element is required so if we didn't find it then we need to error
                log.LogErrorWithCodeFromResources("CodeTaskFactory.CodeElementIsMissing", taskName);
                return false;
            }

            // Copies the source code from the inner text of the <Code /> element.  This might be override later if the user specified
            // a file instead.
            taskInfo.SourceCode = codeElement.Value;

            // Parse the attributes of the <Code /> element
            XAttribute languageAttribute = null;
            XAttribute sourceAttribute = null;
            XAttribute typeAttribute = null;

            // TODO: Unit test for this logic and the error message
            foreach (XAttribute attribute in codeElement.Attributes().Where(i => !i.IsNamespaceDeclaration))
            {
                switch (attribute.Name.LocalName)
                {
                    case "Language":
                        languageAttribute = attribute;
                        break;

                    case "Source":
                        sourceAttribute = attribute;
                        break;

                    case "Type":
                        typeAttribute = attribute;
                        break;

                    default:
                        log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidCodeElementAttribute",
                            attribute.Name.LocalName);
                        return false;
                }
            }

            if (sourceAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(sourceAttribute.Value))
                {
                    // A <Code Source="" /> is not allowed
                    log.LogErrorWithCodeFromResources("CodeTaskFactory.AttributeEmptyWithElement", "Source", "Code");
                    return false;
                }

                // Instead of using the inner text of the <Code /> element, read the specified file as source code
                taskInfo.CodeType = RoslynCodeTaskFactoryCodeType.Class;
                taskInfo.SourceCode = File.ReadAllText(sourceAttribute.Value.Trim());
            }

            if (typeAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(typeAttribute.Value))
                {
                    // A <Code Type="" /> is not allowed
                    log.LogErrorWithCodeFromResources("CodeTaskFactory.AttributeEmptyWithElement", "Type", "Code");
                    return false;
                }

                // Attempt to parse the code type as a CodeTaskFactoryCodeType
                if (!Enum.TryParse(typeAttribute.Value.Trim(), ignoreCase: true, result: out RoslynCodeTaskFactoryCodeType codeType))
                {
                    log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidCodeType", typeAttribute.Value, String.Join(", ", Enum.GetNames(typeof(RoslynCodeTaskFactoryCodeType))));
                    return false;
                }

                taskInfo.CodeType = codeType;
            }

            if (languageAttribute != null)
            {
                if (String.IsNullOrWhiteSpace(languageAttribute.Value))
                {
                    // A <Code Language="" /> is not allowed
                    log.LogErrorWithCodeFromResources("CodeTaskFactory.AttributeEmptyWithElement", "Language", "Code");
                    return false;
                }

                if (ValidCodeLanguages.ContainsKey(languageAttribute.Value))
                {
                    // The user specified one of the primary code languages using our vernacular
                    taskInfo.CodeLanguage = languageAttribute.Value.ToUpperInvariant();
                }
                else
                {
                    bool foundValidCodeLanguage = false;

                    // Attempt to map the user specified value as an alias to our vernacular for code languages
                    foreach (KeyValuePair<string, ISet<string>> validLanguage in ValidCodeLanguages)
                    {
                        if (validLanguage.Value.Contains(languageAttribute.Value))
                        {
                            taskInfo.CodeLanguage = validLanguage.Key;
                            foundValidCodeLanguage = true;
                            break;
                        }
                    }

                    if (!foundValidCodeLanguage)
                    {
                        // The user specified a code language we don't support
                        log.LogErrorWithCodeFromResources("CodeTaskFactory.InvalidCodeLanguage", languageAttribute.Value, String.Join(", ", ValidCodeLanguages.Keys));
                        return false;
                    }
                }
            }

            if (String.IsNullOrWhiteSpace(taskInfo.SourceCode))
            {
                // The user did not specify a path to source code or source code within the <Code /> element.
                log.LogErrorWithCodeFromResources("CodeTaskFactory.NoSourceCode");
                return false;
            }

            taskInfo.SourceCode = GetSourceCode(taskInfo, parameters);

            return true;
        }

        /// <summary>
        /// Attempts to resolve assembly references that were specified by the user.
        /// </summary>
        /// <param name="log">A <see cref="TaskLoggingHelper"/> used for logging.</param>
        /// <param name="taskInfo">A <see cref="RoslynCodeTaskFactoryTaskInfo"/> object containing details about the task.</param>
        /// <param name="items">Receives the list of full paths to resolved assemblies.</param>
        /// <returns><code>true</code> if all assemblies could be resolved, otherwise <code>false</code>.</returns>
        /// <remarks>The user can specify a short name like My.Assembly or My.Assembly.dll.  In this case we'll
        /// attempt to look it up in the directory containing our reference assemblies.  They can also specify a
        /// full path and we'll do no resolution.  At this time, these are the only two resolution mechanisms.
        /// Perhaps in the future this could be more powerful by using NuGet to resolve assemblies but we think
        /// that is too complicated for a simple in-line task.  If users have more complex requirements, they
        /// can compile their own task library.</remarks>
        internal bool TryResolveAssemblyReferences(TaskLoggingHelper log, RoslynCodeTaskFactoryTaskInfo taskInfo, out ITaskItem[] items)
        {
            // Store the list of resolved assemblies because a user can specify a short name or a full path
            ISet<string> resolvedAssemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keeps track if there were one or more unresolved assemblies
            bool hasInvalidReference = false;

            // Start with the user specified references and include all of the default references that are language agnostic
            IEnumerable<string> references = taskInfo.References.Union(DefaultReferences[String.Empty]);

            if (DefaultReferences.ContainsKey(taskInfo.CodeLanguage))
            {
                // Append default references for the specific language
                references = references.Union(DefaultReferences[taskInfo.CodeLanguage]);
            }

            List<string> directoriesToAddToAppDomain = new();

            // Loop through the user specified references as well as the default references
            foreach (string reference in references)
            {
                // The user specified a full path to an assembly, so there is no need to resolve
                if (FileSystems.Default.FileExists(reference))
                {
                    // The path could be relative like ..\Assembly.dll so we need to get the full path
                    string fullPath = Path.GetFullPath(reference);
                    directoriesToAddToAppDomain.Add(Path.GetDirectoryName(fullPath));
                    resolvedAssemblyReferences.Add(fullPath);
                    continue;
                }

                // Attempt to "resolve" the assembly by getting a full path to our distributed reference assemblies
                string assemblyFileName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || reference.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? reference
                    : $"{reference}.dll";

                string resolvedDir = new[]
                {
                    Path.Combine(ThisAssemblyDirectoryLazy.Value, ReferenceAssemblyDirectoryName),
                    ThisAssemblyDirectoryLazy.Value,
                }
                .FirstOrDefault(p => File.Exists(Path.Combine(p, assemblyFileName)));

                if (resolvedDir != null)
                {
                    resolvedAssemblyReferences.Add(Path.Combine(resolvedDir, assemblyFileName));
                    continue;
                }

                // Could not resolve the assembly.  We currently don't support looking things up the GAC so that in-line task
                // assemblies are portable across platforms
                log.LogErrorWithCodeFromResources("CodeTaskFactory.CouldNotFindReferenceAssembly", reference);

                hasInvalidReference = true;
            }

            // Transform the list of resolved assemblies to TaskItems if they were all resolved
            items = hasInvalidReference ? null : resolvedAssemblyReferences.Select(i => (ITaskItem)new TaskItem(i)).ToArray();

            handlerAddedToAppDomain = (_, eventArgs) => TryLoadAssembly(directoriesToAddToAppDomain, new AssemblyName(eventArgs.Name));
            AppDomain.CurrentDomain.AssemblyResolve += handlerAddedToAppDomain;

            return !hasInvalidReference;

            static Assembly TryLoadAssembly(List<string> directories, AssemblyName name)
            {
                foreach (string directory in directories)
                {
                    string path;
                    if (!string.IsNullOrEmpty(name.CultureName))
                    {
                        path = Path.Combine(directory, name.CultureName, name.Name + ".dll");
                        if (File.Exists(path))
                        {
                            return Assembly.LoadFrom(path);
                        }
                    }

                    path = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(path))
                    {
                        return Assembly.LoadFrom(path);
                    }
                }

                return null;
            }
        }

        private static CodeMemberProperty CreateProperty(CodeTypeDeclaration codeTypeDeclaration, string name, Type type, object defaultValue = null)
        {
            CodeMemberField field = new CodeMemberField(new CodeTypeReference(type), "_" + name)
            {
                Attributes = MemberAttributes.Private,
                InitExpression = defaultValue == null ? null : new CodePrimitiveExpression(defaultValue)
            };

            codeTypeDeclaration.Members.Add(field);

            CodeFieldReferenceExpression fieldReference = new CodeFieldReferenceExpression
            {
                FieldName = field.Name
            };

            CodeMemberProperty property = new CodeMemberProperty
            {
                Name = name,
                Type = new CodeTypeReference(type),
                Attributes = MemberAttributes.Public,
                HasGet = true,
                HasSet = true
            };

            property.GetStatements.Add(new CodeMethodReturnStatement(fieldReference));

            CodeAssignStatement fieldAssign = new CodeAssignStatement
            {
                Left = fieldReference,
                Right = new CodeArgumentReferenceExpression("value")
            };

            property.SetStatements.Add(fieldAssign);

            codeTypeDeclaration.Members.Add(property);

            return property;
        }

        /// <summary>
        /// Attempts to compile the current source code and load the assembly into memory.
        /// </summary>
        /// <param name="buildEngine">An <see cref="IBuildEngine"/> to use give to the compiler task so that messages can be logged.</param>
        /// <param name="taskInfo">A <see cref="RoslynCodeTaskFactoryTaskInfo"/> object containing details about the task.</param>
        /// <param name="assembly">The <see cref="Assembly"/> if the source code be compiled and loaded, otherwise <code>null</code>.</param>
        /// <returns><code>true</code> if the source code could be compiled and loaded, otherwise <code>null</code>.</returns>
        private bool TryCompileInMemoryAssembly(IBuildEngine buildEngine, RoslynCodeTaskFactoryTaskInfo taskInfo, out Assembly assembly)
        {
            // First attempt to get a compiled assembly from the cache
            if (CompiledAssemblyCache.TryGetValue(taskInfo, out assembly))
            {
                return true;
            }

            if (!TryResolveAssemblyReferences(_log, taskInfo, out ITaskItem[] references))
            {
                return false;
            }

            // The source code cannot actually be compiled "in memory" so instead the source code is written to disk in
            // the temp folder as well as the assembly.  After compilation, the source code and assembly are deleted.
            string sourceCodePath = FileUtilities.GetTemporaryFileName(".tmp");
            string assemblyPath = FileUtilities.GetTemporaryFileName(".dll");

            // Delete the code file unless compilation failed or the environment variable MSBUILDLOGCODETASKFACTORYOUTPUT
            // is set (which allows for debugging problems)
            bool deleteSourceCodeFile = Environment.GetEnvironmentVariable("MSBUILDLOGCODETASKFACTORYOUTPUT") == null;

            try
            {
                // Embed generated file in the binlog
                string fileNameInBinlog = $"{Guid.NewGuid()}-{_taskName}-compilation-file.tmp";
                _log.LogIncludeGeneratedFile(fileNameInBinlog, taskInfo.SourceCode);

                // Create the code
                File.WriteAllText(sourceCodePath, taskInfo.SourceCode);

                // Execute the compiler.  We re-use the existing build task by hosting it and giving it our IBuildEngine instance for logging
                RoslynCodeTaskFactoryCompilerBase managedCompiler = null;

                // User specified values are translated using a dictionary of known aliases and checking if the user specified
                // a valid code language is already done
                if (taskInfo.CodeLanguage.Equals("CS"))
                {
                    managedCompiler = new RoslynCodeTaskFactoryCSharpCompiler
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
                    managedCompiler = new RoslynCodeTaskFactoryVisualBasicCompiler
                    {
                        NoStandardLib = true,
                        OptionExplicit = true,
                        RootNamespace = "InlineCode",
                    };

                    string toolExe = Environment.GetEnvironmentVariable("VbcToolExe");

                    if (!String.IsNullOrEmpty(toolExe))
                    {
                        managedCompiler.ToolExe = toolExe;
                    }
                }

                if (managedCompiler != null)
                {
                    managedCompiler.BuildEngine = buildEngine;
                    managedCompiler.Deterministic = true;
                    managedCompiler.NoConfig = true;
                    managedCompiler.NoLogo = true;
                    managedCompiler.Optimize = false;
                    managedCompiler.OutputAssembly = new TaskItem(assemblyPath);
                    managedCompiler.References = references;
                    managedCompiler.Sources = [new TaskItem(sourceCodePath)];
                    managedCompiler.TargetType = "Library";
                    managedCompiler.UseSharedCompilation = false;

                    _log.LogMessageFromResources(MessageImportance.Low, "CodeTaskFactory.CompilingAssembly");

                    if (!managedCompiler.Execute())
                    {
                        deleteSourceCodeFile = false;

                        _log.LogErrorWithCodeFromResources("CodeTaskFactory.FindSourceFileAt", sourceCodePath);

                        return false;
                    }

                    if (!deleteSourceCodeFile)
                    {
                        // Log the location of the code file because MSBUILDLOGCODETASKFACTORYOUTPUT was set.
                        _log.LogMessageFromResources(MessageImportance.Low, "CodeTaskFactory.FindSourceFileAt", sourceCodePath);
                    }
                }

                // Return the assembly which is loaded into memory
                assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));

                // Attempt to cache the compiled assembly
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
                if (FileSystems.Default.FileExists(assemblyPath))
                {
                    File.Delete(assemblyPath);
                }

                if (deleteSourceCodeFile && FileSystems.Default.FileExists(sourceCodePath))
                {
                    File.Delete(sourceCodePath);
                }
            }
        }
    }
}
