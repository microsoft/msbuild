// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.ObjectModelRemoting;
using Microsoft.Build.Shared;
using ForwardingLoggerRecord = Microsoft.Build.Logging.ForwardingLoggerRecord;
using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;
using InternalLoggerException = Microsoft.Build.Exceptions.InternalLoggerException;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using LoggerMode = Microsoft.Build.BackEnd.Logging.LoggerMode;
using ObjectModel = System.Collections.ObjectModel;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    using Utilities = Microsoft.Build.Internal.Utilities;

    /// <summary>
    /// Flags for controlling the toolset initialization.
    /// </summary>
    [Flags]
    public enum ToolsetDefinitionLocations
    {
        /// <summary>
        /// Do not read toolset information from any external location.
        /// </summary>
        None = 0,

        /// <summary>
        /// Read toolset information from the exe configuration file.
        /// </summary>
        ConfigurationFile = 1,

        /// <summary>
        /// Read toolset information from the registry (HKLM\Software\Microsoft\MSBuild\ToolsVersions).
        /// </summary>
        Registry = 2,

        /// <summary>
        /// Read toolset information from the current exe path
        /// </summary>
        Local = 4,

        /// <summary>
        /// Use the default location or locations.
        /// </summary>
        Default = None
#if FEATURE_SYSTEM_CONFIGURATION
                | ConfigurationFile
#endif
#if FEATURE_REGISTRY_TOOLSETS
                | Registry
#endif
#if !FEATURE_SYSTEM_CONFIGURATION && !FEATURE_REGISTRY_TOOLSETS
                | Local
#endif
    }

    /// <summary>
    /// This class encapsulates a set of related projects, their toolsets, a default set of global properties,
    /// and the loggers that should be used to build them.
    /// A global version of this class acts as the default ProjectCollection.
    /// Multiple ProjectCollections can exist within an appdomain. However, these must not build concurrently.
    /// </summary>
    [SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix", Justification = "This is a collection of projects API review has approved this")]
    public class ProjectCollection : IToolsetProvider, IBuildComponent, IDisposable
    {
        /// <summary>
        /// The object to synchronize with when accessing certain fields.
        /// </summary>
        /// <remarks>
        /// ProjectCollection is highly reentrant - project creation, toolset and logger changes, and so on
        /// all need lock protection, but there are a lot of read cases as well, and calls to create Projects
        /// call back to the ProjectCollection under locks. Use a RW lock with recursion support to avoid
        /// adding reentrancy bugs.
        /// </remarks>
        private readonly ReaderWriterLockSlim _locker = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// The global singleton project collection used as a default for otherwise
        /// unassociated projects.
        /// </summary>
        private static ProjectCollection s_globalProjectCollection;

        /// <summary>
        /// Gets the file version of the file in which the Engine assembly lies.
        /// </summary>
        /// <remarks>
        /// This is the Windows file version (specifically the value of the ProductVersion
        /// resource), not necessarily the assembly version.
        /// If you want the assembly version, use Constants.AssemblyVersion.
        /// </remarks>
        private static Version s_engineVersion;

        /// <summary>
        /// The display version of the file in which the Engine assembly lies.
        /// </summary>
        private static string s_assemblyDisplayVersion;

        private static ProjectRootElementCacheBase s_projectRootElementCache = null;

        /// <summary>
        /// The projects loaded into this collection.
        /// </summary>
        private readonly LoadedProjectCollection _loadedProjects;

        /// <summary>
        /// External projects support
        /// </summary>
        private ExternalProjectsProvider _link;

        /// <summary>
        /// Single logging service used for all builds of projects in this project collection
        /// </summary>
        private ILoggingService _loggingService;

        /// <summary>
        /// Any object exposing host services.
        /// May be null.
        /// </summary>
        private HostServices _hostServices;

        /// <summary>
        /// A mapping of tools versions to Toolsets, which contain the public Toolsets.
        /// This is the collection we use internally.
        /// </summary>
        private Dictionary<string, Toolset> _toolsets;

        /// <summary>
        /// The default global properties.
        /// </summary>
        private readonly PropertyDictionary<ProjectPropertyInstance> _globalProperties;

        /// <summary>
        /// The properties representing the environment.
        /// </summary>
        private PropertyDictionary<ProjectPropertyInstance> _environmentProperties;

        /// <summary>
        /// The default tools version obtained by examining all of the toolsets.
        /// </summary>
        private string _defaultToolsVersion;

        /// <summary>
        /// A counter incremented every time the toolsets change which would necessitate a re-evaluation of
        /// associated projects.
        /// </summary>
        private int _toolsetsVersion;

        /// <summary>
        /// This is the default value used by newly created projects for whether or not the building
        /// of projects is enabled.  This is for security purposes in case a host wants to closely
        /// control which projects it allows to run targets/tasks.
        /// </summary>
        private bool _isBuildEnabled = true;

        /// <summary>
        /// We may only wish to log critical events, record that fact so we can apply it to build requests
        /// </summary>
        private bool _onlyLogCriticalEvents;

        /// <summary>
        /// Whether reevaluation is temporarily disabled on projects in this collection.
        /// This is useful when the host expects to make a number of reads and writes
        /// to projects, and wants to temporarily sacrifice correctness for performance.
        /// </summary>
        private bool _skipEvaluation;

        /// <summary>
        /// Whether <see cref="Project.MarkDirty()">MarkDirty()</see> is temporarily disabled on
        /// projects in this collection.
        /// This allows, for example, global properties to be set without projects getting
        /// marked dirty for reevaluation as a consequence.
        /// </summary>
        private bool _disableMarkDirty;

        /// <summary>
        /// The maximum number of nodes which can be started during the build
        /// </summary>
        private int _maxNodeCount;

        /// <summary>
        /// LoggingService Logger mode.
        /// If Asynchronous mode is used
        /// </summary>
        private LoggerMode _loggerMode;

        /// <summary>
        /// Instantiates a project collection with no global properties or loggers that reads toolset
        /// information from the configuration file and registry.
        /// </summary>
        public ProjectCollection()
            : this(null)
        {
        }

        /// <summary>
        /// Instantiates a project collection using toolsets from the specified locations,
        /// and no global properties or loggers.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="toolsetLocations">The locations from which to load toolsets.</param>
        public ProjectCollection(ToolsetDefinitionLocations toolsetLocations)
            : this(null, null, toolsetLocations)
        {
        }

        /// <summary>
        /// Instantiates a project collection with specified global properties, no loggers,
        /// and that reads toolset information from the configuration file and registry.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="globalProperties">The default global properties to use. May be null.</param>
        public ProjectCollection(IDictionary<string, string> globalProperties)
            : this(globalProperties, null, ToolsetDefinitionLocations.Default)
        {
        }

        /// <summary>
        /// Instantiates a project collection with specified global properties and loggers and using the
        /// specified toolset locations.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="globalProperties">The default global properties to use. May be null.</param>
        /// <param name="loggers">The loggers to register. May be null.</param>
        /// <param name="toolsetDefinitionLocations">The locations from which to load toolsets.</param>
        public ProjectCollection(IDictionary<string, string> globalProperties, IEnumerable<ILogger> loggers, ToolsetDefinitionLocations toolsetDefinitionLocations)
            : this(globalProperties, loggers, null, toolsetDefinitionLocations, 1 /* node count */, false /* do not only log critical events */)
        {
        }

        /// <summary>
        /// Instantiates a project collection with specified global properties and loggers and using the
        /// specified toolset locations, node count, and setting of onlyLogCriticalEvents.
        /// Global properties and loggers may be null.
        /// Throws InvalidProjectFileException if any of the global properties are reserved.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="globalProperties">The default global properties to use. May be null.</param>
        /// <param name="loggers">The loggers to register. May be null and specified to any build instead.</param>
        /// <param name="remoteLoggers">Any remote loggers to register. May be null and specified to any build instead.</param>
        /// <param name="toolsetDefinitionLocations">The locations from which to load toolsets.</param>
        /// <param name="maxNodeCount">The maximum number of nodes to use for building.</param>
        /// <param name="onlyLogCriticalEvents">If set to true, only critical events will be logged.</param>
        public ProjectCollection(IDictionary<string, string> globalProperties, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, ToolsetDefinitionLocations toolsetDefinitionLocations, int maxNodeCount, bool onlyLogCriticalEvents)
            : this(globalProperties, loggers, null, toolsetDefinitionLocations, maxNodeCount, onlyLogCriticalEvents, loadProjectsReadOnly: false)
        {
        }

        /// <summary>
        /// Instantiates a project collection with specified global properties and loggers and using the
        /// specified toolset locations, node count, and setting of onlyLogCriticalEvents.
        /// Global properties and loggers may be null.
        /// Throws InvalidProjectFileException if any of the global properties are reserved.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="globalProperties">The default global properties to use. May be null.</param>
        /// <param name="loggers">The loggers to register. May be null and specified to any build instead.</param>
        /// <param name="remoteLoggers">Any remote loggers to register. May be null and specified to any build instead.</param>
        /// <param name="toolsetDefinitionLocations">The locations from which to load toolsets.</param>
        /// <param name="maxNodeCount">The maximum number of nodes to use for building.</param>
        /// <param name="onlyLogCriticalEvents">If set to true, only critical events will be logged.</param>
        /// <param name="loadProjectsReadOnly">If set to true, load all projects as read-only.</param>
        public ProjectCollection(IDictionary<string, string> globalProperties, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, ToolsetDefinitionLocations toolsetDefinitionLocations, int maxNodeCount, bool onlyLogCriticalEvents, bool loadProjectsReadOnly)
            : this(globalProperties, loggers, remoteLoggers, toolsetDefinitionLocations, maxNodeCount, onlyLogCriticalEvents, loadProjectsReadOnly, useAsynchronousLogging: false, reuseProjectRootElementCache: false)
        {
        }


        /// <summary>
        /// Instantiates a project collection with specified global properties and loggers and using the
        /// specified toolset locations, node count, and setting of onlyLogCriticalEvents.
        /// Global properties and loggers may be null.
        /// Throws InvalidProjectFileException if any of the global properties are reserved.
        /// May throw InvalidToolsetDefinitionException.
        /// </summary>
        /// <param name="globalProperties">The default global properties to use. May be null.</param>
        /// <param name="loggers">The loggers to register. May be null and specified to any build instead.</param>
        /// <param name="remoteLoggers">Any remote loggers to register. May be null and specified to any build instead.</param>
        /// <param name="toolsetDefinitionLocations">The locations from which to load toolsets.</param>
        /// <param name="maxNodeCount">The maximum number of nodes to use for building.</param>
        /// <param name="onlyLogCriticalEvents">If set to true, only critical events will be logged.</param>
        /// <param name="loadProjectsReadOnly">If set to true, load all projects as read-only.</param>
        /// <param name="useAsynchronousLogging">If set to true, asynchronous logging will be used. <see cref="ProjectCollection.Dispose()"/> has to called to clear resources used by async logging.</param>
        /// <param name="reuseProjectRootElementCache">If set to true, it will try to reuse <see cref="ProjectRootElementCacheBase"/> singleton.</param>
        public ProjectCollection(IDictionary<string, string> globalProperties, IEnumerable<ILogger> loggers, IEnumerable<ForwardingLoggerRecord> remoteLoggers, ToolsetDefinitionLocations toolsetDefinitionLocations, int maxNodeCount, bool onlyLogCriticalEvents, bool loadProjectsReadOnly, bool useAsynchronousLogging, bool reuseProjectRootElementCache)
        {
            _loadedProjects = new LoadedProjectCollection();
            ToolsetLocations = toolsetDefinitionLocations;
            MaxNodeCount = maxNodeCount;

            if (Traits.Instance.UseSimpleProjectRootElementCacheConcurrency)
            {
                ProjectRootElementCache = new SimpleProjectRootElementCache();
            }
            else if (reuseProjectRootElementCache && s_projectRootElementCache != null)
            {
                ProjectRootElementCache = s_projectRootElementCache;
            }
            else
            {
                // When we are reusing ProjectRootElementCache we need to reload XMLs if it has changed between MSBuild Server sessions/builds.
                // If we are not reusing, cache will be released at end of build and as we do not support project files will changes during build
                // we do not need to auto reload.
                bool autoReloadFromDisk = reuseProjectRootElementCache;
                ProjectRootElementCache = new ProjectRootElementCache(autoReloadFromDisk, loadProjectsReadOnly);
                if (reuseProjectRootElementCache)
                {
                    s_projectRootElementCache = ProjectRootElementCache;
                }
            }

            OnlyLogCriticalEvents = onlyLogCriticalEvents;

            try
            {
                _loggerMode = useAsynchronousLogging ? LoggerMode.Asynchronous : LoggerMode.Synchronous;
                CreateLoggingService(maxNodeCount, onlyLogCriticalEvents);

                RegisterLoggers(loggers);
                RegisterForwardingLoggers(remoteLoggers);

                if (globalProperties != null)
                {
                    _globalProperties = new PropertyDictionary<ProjectPropertyInstance>(globalProperties.Count);

                    foreach (KeyValuePair<string, string> pair in globalProperties)
                    {
                        try
                        {
                            _globalProperties.Set(ProjectPropertyInstance.Create(pair.Key, pair.Value));
                        }
                        catch (ArgumentException ex)
                        {
                            // Reserved or invalid property name
                            try
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(ElementLocation.Create("MSBUILD"), "InvalidProperty", ex.Message);
                            }
                            catch (InvalidProjectFileException ex2)
                            {
                                BuildEventContext buildEventContext = new BuildEventContext(0 /* node ID */, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);
                                LoggingService.LogInvalidProjectFileError(buildEventContext, ex2);
                                throw;
                            }
                        }
                    }
                }
                else
                {
                    _globalProperties = new PropertyDictionary<ProjectPropertyInstance>();
                }

                InitializeToolsetCollection();
            }
            catch (Exception)
            {
                ShutDownLoggingService();
                throw;
            }

            ProjectRootElementCache.ProjectRootElementAddedHandler += ProjectRootElementCache_ProjectRootElementAddedHandler;
            ProjectRootElementCache.ProjectRootElementDirtied += ProjectRootElementCache_ProjectRootElementDirtiedHandler;
            ProjectRootElementCache.ProjectDirtied += ProjectRootElementCache_ProjectDirtiedHandler;
        }

        /// <summary>
        /// Handler to receive which project got added to the project collection.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible", Justification = "This has been API reviewed")]
        public delegate void ProjectAddedEventHandler(object sender, ProjectAddedToProjectCollectionEventArgs e);

        /// <summary>
        /// Event that is fired when a project is added to the ProjectRootElementCache of this project collection.
        /// </summary>
        public event ProjectAddedEventHandler ProjectAdded;

        /// <summary>
        /// Raised when state is changed on this instance.
        /// </summary>
        /// <remarks>
        /// This event is NOT raised for changes in individual projects.
        /// </remarks>
        public event EventHandler<ProjectCollectionChangedEventArgs> ProjectCollectionChanged;

        /// <summary>
        /// Raised when a <see cref="ProjectRootElement"/> contained by this instance is changed.
        /// </summary>
        /// <remarks>
        /// This event is NOT raised for changes to global properties, or any other change that doesn't actually dirty the XML.
        /// </remarks>
        public event EventHandler<ProjectXmlChangedEventArgs> ProjectXmlChanged;

        /// <summary>
        /// Raised when a <see cref="Project"/> contained by this instance is directly changed.
        /// </summary>
        /// <remarks>
        /// This event is NOT raised for direct project XML changes via the construction model.
        /// </remarks>
        public event EventHandler<ProjectChangedEventArgs> ProjectChanged;

        /// <summary>
        /// Retrieves the global project collection object.
        /// This is a singleton project collection with no global properties or loggers that reads toolset
        /// information from the configuration file and registry.
        /// May throw InvalidToolsetDefinitionException.
        /// Thread safe.
        /// </summary>
        public static ProjectCollection GlobalProjectCollection
        {
            get
            {
                if (s_globalProjectCollection == null)
                {
                    // Take care to ensure that there is never more than one value observed
                    // from this property even in the case of race conditions while lazily initializing.
                    var local = new ProjectCollection(null, null, null, ToolsetDefinitionLocations.Default,
                        maxNodeCount: 1, onlyLogCriticalEvents: false, loadProjectsReadOnly: false, useAsynchronousLogging: true, reuseProjectRootElementCache: false);

                    if (Interlocked.CompareExchange(ref s_globalProjectCollection, local, null) != null)
                    {
                        // Other thread beat us to it; dispose of this project collection
                        local.Dispose();
                    }
                }

                return s_globalProjectCollection;
            }
        }

        /// <summary>
        /// Gets the file version of the file in which the Engine assembly lies.
        /// </summary>
        /// <remarks>
        /// This is the Windows file version (specifically the value of the FileVersion
        /// resource), not necessarily the assembly version.
        /// If you want the assembly version, use Constants.AssemblyVersion.
        /// This is not the <see cref="Toolset.ToolsVersion"/>.
        /// </remarks>
        public static Version Version
        {
            get
            {
                if (s_engineVersion == null)
                {
                    // Get the file version from the currently executing assembly.
                    // Use .CodeBase instead of .Location, because .Location doesn't
                    // work when Microsoft.Build.dll has been shadow-copied, for example
                    // in scenarios where NUnit is loading Microsoft.Build.
                    var versionInfo = FileVersionInfo.GetVersionInfo(FileUtilities.ExecutingAssemblyPath);
                    s_engineVersion = new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart, versionInfo.FilePrivatePart);
                }

                return s_engineVersion;
            }
        }

        /// <summary>
        /// Gets a version of the Engine suitable for display to a user.
        /// </summary>
        /// <remarks>
        /// This is in the form of a SemVer v2 version, Major.Minor.Patch-prerelease+metadata.
        /// </remarks>
        public static string DisplayVersion
        {
            get
            {
                if (s_assemblyDisplayVersion == null)
                {
                    var fullInformationalVersion = typeof(Constants).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

                    // use a truncated version with only 9 digits of SHA
                    var plusIndex = fullInformationalVersion.IndexOf('+');
                    s_assemblyDisplayVersion = plusIndex < 0
                                                    ? fullInformationalVersion
                                                    : fullInformationalVersion.Substring(startIndex: 0, length: plusIndex + 10);
                }

                return s_assemblyDisplayVersion;
            }
        }

        /// <summary>
        /// The default tools version of this project collection. Projects use this tools version if they
        /// aren't otherwise told what tools version to use.
        /// This value is gotten from the .exe.config file, or else in the registry,
        /// or if neither specify a default tools version then it is hard-coded to the tools version "2.0".
        /// Setter throws InvalidOperationException if a toolset with the provided tools version has not been defined.
        /// Always defined.
        /// </summary>
        public string DefaultToolsVersion
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    ErrorUtilities.VerifyThrow(_defaultToolsVersion != null, "Should have a default");
                    return _defaultToolsVersion;
                }
            }

            set
            {
                ErrorUtilities.VerifyThrowArgumentLength(value, nameof(DefaultToolsVersion));

                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (!_toolsets.ContainsKey(value))
                    {
                        string toolsVersionList = Utilities.CreateToolsVersionListString(Toolsets);
                        ErrorUtilities.ThrowInvalidOperation("UnrecognizedToolsVersion", value, toolsVersionList);
                    }

                    if (_defaultToolsVersion != value)
                    {
                        _defaultToolsVersion = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.DefaultToolsVersion));
                }
            }
        }

        /// <summary>
        /// Returns default global properties for all projects in this collection.
        /// Read-only dead dictionary.
        /// </summary>
        /// <remarks>
        /// This is the publicly exposed getter, that translates into a read-only dead IDictionary&lt;string, string&gt;.
        ///
        /// To be consistent with Project, setting and removing global properties is done with
        /// <see cref="SetGlobalProperty">SetGlobalProperty</see> and <see cref="RemoveGlobalProperty">RemoveGlobalProperty</see>.
        /// </remarks>
        public IDictionary<string, string> GlobalProperties
        {
            get
            {
                Dictionary<string, string> dictionary;

                using (_locker.EnterDisposableReadLock())
                {
                    if (_globalProperties.Count == 0)
                    {
                        return ReadOnlyEmptyDictionary<string, string>.Instance;
                    }

                    dictionary = new Dictionary<string, string>(_globalProperties.Count, MSBuildNameIgnoreCaseComparer.Default);

                    foreach (ProjectPropertyInstance property in _globalProperties)
                    {
                        dictionary[property.Name] = ((IProperty)property).EvaluatedValueEscaped;
                    }
                }

                return new ObjectModel.ReadOnlyDictionary<string, string>(dictionary);
            }
        }

        /// <summary>
        /// All the projects currently loaded into this collection.
        /// Each has a unique combination of path, global properties, and tools version.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "This is a reasonable choice. API review approved")]
        public ICollection<Project> LoadedProjects => GetLoadedProjects(true, null);

        /// <summary>
        /// Number of projects currently loaded into this collection.
        /// </summary>
        public int Count
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _loadedProjects.Count;
                }
            }
        }

        /// <summary>
        /// Loggers that all contained projects will use for their builds.
        /// Loggers are added with the <see cref="RegisterLogger"/>.
        /// Returns an empty collection if there are no loggers.
        /// </summary>
        // UNDONE: Currently loggers cannot be removed.
        public ICollection<ILogger> Loggers
        {
            [DebuggerStepThrough]
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _loggingService.Loggers == null
                        ? (ICollection<ILogger>)ReadOnlyEmptyCollection<ILogger>.Instance
                        : new List<ILogger>(_loggingService.Loggers);
                }
            }
        }

        /// <summary>
        /// Returns the toolsets this ProjectCollection knows about.
        /// </summary>
        /// <comments>
        /// ValueCollection is already read-only
        /// </comments>
        public ICollection<Toolset> Toolsets
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return new List<Toolset>(_toolsets.Values);
                }
            }
        }

        /// <summary>
        /// Returns the locations used to find the toolsets.
        /// </summary>
        public ToolsetDefinitionLocations ToolsetLocations { get; }

        /// <summary>
        /// This is the default value used by newly created projects for whether or not the building
        /// of projects is enabled.  This is for security purposes in case a host wants to closely
        /// control which projects it allows to run targets/tasks.
        /// </summary>
        public bool IsBuildEnabled
        {
            [DebuggerStepThrough]
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _isBuildEnabled;
                }
            }

            [DebuggerStepThrough]
            set
            {
                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_isBuildEnabled != value)
                    {
                        _isBuildEnabled = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.IsBuildEnabled));
                }
            }
        }

        /// <summary>
        /// When true, only log critical events such as warnings and errors. Has to be in here for API compat
        /// </summary>
        public bool OnlyLogCriticalEvents
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _onlyLogCriticalEvents;
                }
            }

            set
            {
                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_onlyLogCriticalEvents != value)
                    {
                        _onlyLogCriticalEvents = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(
                        new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.OnlyLogCriticalEvents));
                }
            }
        }

        /// <summary>
        /// Object exposing host services to tasks during builds of projects
        /// contained by this project collection.
        /// By default, <see cref="HostServices">HostServices</see> is used.
        /// May be set to null, but the getter will create a new instance in that case.
        /// </summary>
        public HostServices HostServices
        {
            get
            {
                // Avoid write lock if possible, this getter is called a lot during Project construction.
                using (_locker.EnterDisposableReadLock())
                {
                    if (_hostServices != null)
                    {
                        return _hostServices;
                    }
                }
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_hostServices == null)
                    {
                        _hostServices = new HostServices();
                    }
                    return _hostServices;
                }
            }

            set
            {
                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_hostServices != value)
                    {
                        _hostServices = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(
                        new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.HostServices));
                }
            }
        }

        /// <summary>
        /// Whether reevaluation is temporarily disabled on projects in this collection.
        /// This is useful when the host expects to make a number of reads and writes
        /// to projects, and wants to temporarily sacrifice correctness for performance.
        /// </summary>
        public bool SkipEvaluation
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _skipEvaluation;
                }
            }

            set
            {
                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_skipEvaluation != value)
                    {
                        _skipEvaluation = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(
                        new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.SkipEvaluation));
                }
            }
        }

        /// <summary>
        /// Whether <see cref="Project.MarkDirty()">MarkDirty()</see> is temporarily disabled on
        /// projects in this collection.
        /// This allows, for example, global properties to be set without projects getting
        /// marked dirty for reevaluation as a consequence.
        /// </summary>
        public bool DisableMarkDirty
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _disableMarkDirty;
                }
            }

            set
            {
                bool sendEvent = false;
                using (_locker.EnterDisposableWriteLock())
                {
                    if (_disableMarkDirty != value)
                    {
                        _disableMarkDirty = value;
                        sendEvent = true;
                    }
                }

                if (sendEvent)
                {
                    OnProjectCollectionChanged(
                        new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.DisableMarkDirty));
                }
            }
        }

        /// <summary>
        /// Global collection id.
        /// Can be used for external providers to optimize the cross-site link exchange
        /// </summary>
        internal Guid CollectionId { get; } = Guid.NewGuid();

        /// <summary>
        /// External project support.
        /// Establish a remote project link for this collection.
        /// </summary>
        internal ExternalProjectsProvider Link
        {
            get => _link;
            set => Interlocked.Exchange(ref _link, value)?.Disconnected(this);
        }

        /// <summary>
        /// Logging service that should be used for project load and for builds
        /// </summary>
        internal ILoggingService LoggingService
        {
            [DebuggerStepThrough]
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _loggingService;
                }
            }
        }

        /// <summary>
        /// Gets default global properties for all projects in this collection.
        /// Dead copy.
        /// </summary>
        internal PropertyDictionary<ProjectPropertyInstance> GlobalPropertiesCollection
        {
            [DebuggerStepThrough]
            get
            {
                var clone = new PropertyDictionary<ProjectPropertyInstance>();

                using (_locker.EnterDisposableReadLock())
                {
                    foreach (ProjectPropertyInstance property in _globalProperties)
                    {
                        clone.Set(property.DeepClone());
                    }

                    return clone;
                }
            }
        }

        /// <summary>
        /// Returns the property dictionary containing the properties representing the environment.
        /// </summary>
        internal PropertyDictionary<ProjectPropertyInstance> EnvironmentProperties
        {
            get
            {
                // Retrieves the environment properties.
                // This is only done once, when the project collection is created. Any subsequent
                // environment changes will be ignored. Child nodes will be passed this set
                // of properties in their build parameters.
                return new PropertyDictionary<ProjectPropertyInstance>(SharedReadOnlyEnvironmentProperties);
            }
        }

        /// <summary>
        /// Returns a shared immutable property dictionary containing the properties representing the environment.
        /// </summary>
        internal PropertyDictionary<ProjectPropertyInstance> SharedReadOnlyEnvironmentProperties
        {
            get
            {
                // Retrieves the environment properties.
                // This is only done once, when the project collection is created. Any subsequent
                // environment changes will be ignored. Child nodes will be passed this set
                // of properties in their build parameters.
                using (_locker.EnterDisposableReadLock())
                {
                    if (_environmentProperties != null)
                    {
                        return _environmentProperties;
                    }
                }

                using (_locker.EnterDisposableWriteLock())
                {
                    if (_environmentProperties == null)
                    {
                        _environmentProperties = Utilities.GetEnvironmentProperties(makeReadOnly: true);
                    }
                    return _environmentProperties;
                }
            }
        }

        /// <summary>
        /// Returns the internal version for this object's state.
        /// Updated when toolsets change, indicating all contained projects are potentially invalid.
        /// </summary>
        internal int ToolsetsVersion
        {
            [DebuggerStepThrough]
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _toolsetsVersion;
                }
            }
        }

        /// <summary>
        /// The maximum number of nodes which can be started during the build
        /// </summary>
        internal int MaxNodeCount
        {
            get
            {
                using (_locker.EnterDisposableReadLock())
                {
                    return _maxNodeCount;
                }
            }

            set
            {
                using (_locker.EnterDisposableWriteLock())
                {
                    _maxNodeCount = value;
                }
            }
        }

        /// <summary>
        /// The cache of project root elements associated with this project collection.
        /// Each is associated with a specific project collection for two reasons:
        /// - To help protect one project collection from any XML edits through another one:
        /// until a reload from disk - when it's ready to accept changes - it won't see the edits;
        /// - So that the owner of this project collection can force the XML to be loaded again
        /// from disk, by doing <see cref="UnloadAllProjects"/>.
        /// </summary>
        internal ProjectRootElementCacheBase ProjectRootElementCache { get; }

        /// <summary>
        /// Escape a string using MSBuild escaping format. For example, "%3b" for ";".
        /// Only characters that are especially significant to MSBuild parsing are escaped.
        /// Callers can use this method to make a string safe to be parsed to other methods
        /// that would otherwise expand it; or to make a string safe to be written to a project file.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1720:IdentifiersShouldNotContainTypeNames", MessageId = "string", Justification = "Public API that has shipped")]
        public static string Escape(string unescapedString)
        {
            return EscapingUtilities.Escape(unescapedString);
        }

        /// <summary>
        /// Unescape a string using MSBuild escaping format. For example, "%3b" for ";".
        /// All escaped characters are unescaped.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1720:IdentifiersShouldNotContainTypeNames", MessageId = "string", Justification = "Public API that has shipped")]
        public static string Unescape(string escapedString)
        {
            return EscapingUtilities.UnescapeAll(escapedString);
        }

        /// <summary>
        /// Returns true if there is a toolset defined for the specified
        /// tools version, otherwise false.
        /// </summary>
        public bool ContainsToolset(string toolsVersion) => GetToolset(toolsVersion) != null;

        /// <summary>
        /// Add a new toolset.
        /// Replaces any existing toolset with the same tools version.
        /// </summary>
        public void AddToolset(Toolset toolset)
        {
            ErrorUtilities.VerifyThrowArgumentNull(toolset);
            using (_locker.EnterDisposableWriteLock())
            {
                _toolsets[toolset.ToolsVersion] = toolset;
                _toolsetsVersion++;
            }

            OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Toolsets));
        }

        /// <summary>
        /// Remove a toolset.
        /// Returns true if it was present, otherwise false.
        /// </summary>
        public bool RemoveToolset(string toolsVersion)
        {
            ErrorUtilities.VerifyThrowArgumentLength(toolsVersion);

            bool changed;
            using (_locker.EnterDisposableWriteLock())
            {
                changed = RemoveToolsetInternal(toolsVersion);
            }

            if (changed)
            {
                OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Toolsets));
            }

            return changed;
        }

        /// <summary>
        /// Removes all toolsets.
        /// </summary>
        public void RemoveAllToolsets()
        {
            bool changed = false;
            using (_locker.EnterDisposableWriteLock())
            {
                var toolsets = new List<Toolset>(Toolsets);

                foreach (Toolset toolset in toolsets)
                {
                    changed |= RemoveToolsetInternal(toolset.ToolsVersion);
                }
            }

            if (changed)
            {
                OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Toolsets));
            }
        }

        /// <summary>
        /// Get the toolset with the specified tools version.
        /// If it is not present, returns null.
        /// </summary>
        public Toolset GetToolset(string toolsVersion)
        {
            ErrorUtilities.VerifyThrowArgumentLength(toolsVersion);
            using (_locker.EnterDisposableWriteLock())
            {
                _toolsets.TryGetValue(toolsVersion, out var toolset);
                return toolset;
            }
        }

        /// <summary>
        /// Figure out what ToolsVersion to use to actually build the project with.
        /// </summary>
        /// <param name="explicitToolsVersion">The user-specified ToolsVersion (through e.g. /tv: on the command line). May be null</param>
        /// <param name="toolsVersionFromProject">The ToolsVersion from the project file. May be null</param>
        /// <returns>The ToolsVersion we should use to build this project.  Should never be null.</returns>
        public string GetEffectiveToolsVersion(string explicitToolsVersion, string toolsVersionFromProject)
        {
            return Utilities.GenerateToolsVersionToUse(explicitToolsVersion, toolsVersionFromProject, GetToolset, DefaultToolsVersion, out _);
        }

        /// <summary>
        /// Returns any and all loaded projects with the provided path.
        /// There may be more than one, if they are distinguished by global properties
        /// and/or tools version.
        /// </summary>
        public ICollection<Project> GetLoadedProjects(string fullPath)
        {
            return GetLoadedProjects(true, fullPath);
        }

        /// <summary>
        /// Returns any and all loaded projects with the provided path.
        /// There may be more than one, if they are distinguished by global properties
        /// and/or tools version.
        /// </summary>
        internal ICollection<Project> GetLoadedProjects(bool includeExternal, string fullPath = null)
        {
            List<Project> loaded;
            using (_locker.EnterDisposableWriteLock())
            {
                loaded = fullPath == null ? new List<Project>(_loadedProjects) : new List<Project>(_loadedProjects.GetMatchingProjectsIfAny(fullPath));
            }

            if (includeExternal)
            {
                var link = Link;
                if (link != null)
                {
                    loaded.AddRange(link.GetLoadedProjects(fullPath));
                }
            }

            return loaded;
        }

        /// <summary>
        /// Loads a project with the specified filename, using the collection's global properties and tools version.
        /// If a matching project is already loaded, it will be returned, otherwise a new project will be loaded.
        /// </summary>
        /// <param name="fileName">The project file to load</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(string fileName)
        {
            return LoadProject(fileName, null);
        }

        /// <summary>
        /// Loads a project with the specified filename and tools version, using the collection's global properties.
        /// If a matching project is already loaded, it will be returned, otherwise a new project will be loaded.
        /// </summary>
        /// <param name="fileName">The project file to load</param>
        /// <param name="toolsVersion">The tools version to use. May be null.</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(string fileName, string toolsVersion)
        {
            return LoadProject(fileName, null /* use project collection's global properties */, toolsVersion);
        }

        /// <summary>
        /// Loads a project with the specified filename, tools version and global properties.
        /// If a matching project is already loaded, it will be returned, otherwise a new project will be loaded.
        /// </summary>
        /// <param name="fileName">The project file to load</param>
        /// <param name="globalProperties">The global properties to use. May be null, in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">The tools version. May be null.</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(string fileName, IDictionary<string, string> globalProperties, string toolsVersion)
        {
            return LoadProjectWithOptions(fileName, new ProjectOptions { GlobalProperties = globalProperties, ToolsVersion = toolsVersion });
        }

        /// <summary>
        /// Loads a project with the specified filename, tools version and global properties.
        /// If a matching project is already loaded, it will be returned, otherwise a new project will be loaded.
        /// </summary>
        /// <param name="fileName">The project file to load</param>
        /// <param name="projectOptions">The <see cref="ProjectOptions"/> to use.</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProjectWithOptions(string fileName, ProjectOptions projectOptions)
        {
            ErrorUtilities.VerifyThrowArgumentLength(fileName);
            fileName = FileUtilities.NormalizePath(fileName);

            using (_locker.EnterDisposableWriteLock())
            {
                // We can only work on this Collection.
                projectOptions.ProjectCollection = this;

                if (projectOptions.GlobalProperties == null)
                {
                    projectOptions.GlobalProperties = GlobalProperties;
                }
                else
                {
                    // We need to update the set of global properties to merge in the ProjectCollection global properties --
                    // otherwise we might end up declaring "not matching" a project that actually does ... and then throw
                    // an exception when we go to actually add the newly created project to the ProjectCollection.
                    // BUT remember that project global properties win -- don't override a property that already exists.
                    foreach (KeyValuePair<string, string> globalProperty in GlobalProperties)
                    {
                        if (!projectOptions.GlobalProperties.ContainsKey(globalProperty.Key))
                        {
                            projectOptions.GlobalProperties.Add(globalProperty);
                        }
                    }
                }

                // We do not control the current directory at this point, but assume that if we were
                // passed a relative path, the caller assumes we will prepend the current directory.
                string toolsVersionFromProject = null;

                if (projectOptions.ToolsVersion == null)
                {
                    // Load the project XML to get any ToolsVersion attribute.
                    // If there isn't already an equivalent project loaded, the real load we'll do will be satisfied from the cache.
                    // If there is already an equivalent project loaded, we'll never need this XML -- but it'll already
                    // have been loaded by that project so it will have been satisfied from the ProjectRootElementCache.
                    // Either way, no time wasted.
                    try
                    {
                        ProjectRootElement xml = ProjectRootElement.OpenProjectOrSolution(fileName, projectOptions.GlobalProperties, projectOptions.ToolsVersion, ProjectRootElementCache, true /*explicitlyloaded*/);
                        toolsVersionFromProject = (xml.ToolsVersion.Length > 0) ? xml.ToolsVersion : DefaultToolsVersion;
                    }
                    catch (InvalidProjectFileException ex)
                    {
                        var buildEventContext = new BuildEventContext(0 /* node ID */, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId);
                        LoggingService.LogInvalidProjectFileError(buildEventContext, ex);
                        throw;
                    }
                }

                string effectiveToolsVersion = Utilities.GenerateToolsVersionToUse(projectOptions.ToolsVersion, toolsVersionFromProject, GetToolset, DefaultToolsVersion, out _);
                Project project = _loadedProjects.GetMatchingProjectIfAny(fileName, projectOptions.GlobalProperties, effectiveToolsVersion);

                if (project == null)
                {
                    // The Project constructor adds itself to our collection,
                    // it is not done by us
                    project = Project.FromFile(fileName, projectOptions);
                }

                return project;
            }
        }

        /// <summary>
        /// Loads a project with the specified reader, using the collection's global properties and tools version.
        /// The project will be added to this project collection when it is named.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(XmlReader xmlReader)
        {
            return LoadProject(xmlReader, null);
        }

        /// <summary>
        /// Loads a project with the specified reader and tools version, using the collection's global properties.
        /// The project will be added to this project collection when it is named.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from</param>
        /// <param name="toolsVersion">The tools version to use. May be null.</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(XmlReader xmlReader, string toolsVersion)
        {
            return LoadProject(xmlReader, null /* use project collection's global properties */, toolsVersion);
        }

        /// <summary>
        /// Loads a project with the specified reader, tools version and global properties.
        /// The project will be added to this project collection when it is named.
        /// </summary>
        /// <param name="xmlReader">Xml reader to read project from</param>
        /// <param name="globalProperties">The global properties to use. May be null in which case the containing project collection's global properties will be used.</param>
        /// <param name="toolsVersion">The tools version. May be null.</param>
        /// <returns>A loaded project.</returns>
        public Project LoadProject(XmlReader xmlReader, IDictionary<string, string> globalProperties, string toolsVersion)
        {
            return new Project(xmlReader, globalProperties, toolsVersion, this);
        }

        /// <summary>
        /// Adds a logger to the collection of loggers used for builds of projects in this collection.
        /// If the logger object is already in the collection, does nothing.
        /// </summary>
        public void RegisterLogger(ILogger logger)
        {
            using (_locker.EnterDisposableWriteLock())
            {
                RegisterLoggerInternal(logger);
            }

            OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Loggers));
        }

        /// <summary>
        /// Adds some loggers to the collection of loggers used for builds of projects in this collection.
        /// If any logger object is already in the collection, does nothing for that logger.
        /// May be null.
        /// </summary>
        public void RegisterLoggers(IEnumerable<ILogger> loggers)
        {
            bool changed = false;
            if (loggers != null)
            {
                using (_locker.EnterDisposableWriteLock())
                {
                    foreach (ILogger logger in loggers)
                    {
                        RegisterLoggerInternal(logger);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Loggers));
            }
        }

        /// <summary>
        /// Adds some remote loggers to the collection of remote loggers used for builds of projects in this collection.
        /// May be null.
        /// </summary>
        public void RegisterForwardingLoggers(IEnumerable<ForwardingLoggerRecord> remoteLoggers)
        {
            using (_locker.EnterDisposableWriteLock())
            {
                if (remoteLoggers != null)
                {
                    foreach (ForwardingLoggerRecord remoteLoggerRecord in remoteLoggers)
                    {
                        _loggingService.RegisterDistributedLogger(new ReusableLogger(remoteLoggerRecord.CentralLogger), remoteLoggerRecord.ForwardingLoggerDescription);
                    }
                }
            }

            OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Loggers));
        }

        /// <summary>
        /// Removes all loggers from the collection of loggers used for builds of projects in this collection.
        /// </summary>
        public void UnregisterAllLoggers()
        {
            using (_locker.EnterDisposableWriteLock())
            {
                _loggingService.UnregisterAllLoggers();

                // UNDONE: Logging service should not shut down when all loggers are unregistered.
                // VS unregisters all loggers on the same project collection often. To workaround this, we have to create it again now!
                CreateLoggingService(MaxNodeCount, OnlyLogCriticalEvents);
            }

            OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.Loggers));
        }

        /// <summary>
        /// Unloads the specific project specified.
        /// Host should call this when they are completely done with the project.
        /// If project was not already loaded, throws InvalidOperationException.
        /// </summary>
        public void UnloadProject(Project project)
        {
            if (project.IsLinked)
            {
                project.Zombify();
                return;
            }

            using (_locker.EnterDisposableWriteLock())
            {
                bool existed = _loadedProjects.RemoveProject(project);
                ErrorUtilities.VerifyThrowInvalidOperation(existed, "OM_ProjectWasNotLoaded");

                project.Zombify();

                // If we've removed the last entry for the given project full path
                // then unregister any and all host objects for that project
                if (_hostServices != null && _loadedProjects.GetMatchingProjectsIfAny(project.FullPath).Count == 0)
                {
                    _hostServices.UnregisterProject(project.FullPath);
                }

                // Release our own cache's strong references to try to help
                // free memory. These may be the last references to the ProjectRootElements
                // in the cache, so the cache shouldn't hold strong references to them of its own.
                ProjectRootElementCache.DiscardStrongReferences();
            }
        }

        /// <summary>
        /// Unloads a project XML root element from the weak cache.
        /// </summary>
        /// <param name="projectRootElement">The project XML root element to unload.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the project XML root element to unload is still in use by a loaded project or its imports.
        /// </exception>
        /// <remarks>
        /// This method is useful for the case where the host knows that all projects using this XML element
        /// are unloaded, and desires to discard any unsaved changes.
        /// </remarks>
        public void UnloadProject(ProjectRootElement projectRootElement)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElement);
            if (projectRootElement.Link != null)
            {
                return;
            }

            using (_locker.EnterDisposableWriteLock())
            {
                Project conflictingProject = GetLoadedProjects(false, null).FirstOrDefault(project => project.UsesProjectRootElement(projectRootElement));
                if (conflictingProject != null)
                {
                    ErrorUtilities.ThrowInvalidOperation("OM_ProjectXmlCannotBeUnloadedDueToLoadedProjects", projectRootElement.FullPath, conflictingProject.FullPath);
                }

                ProjectRootElementCache.DiscardAnyWeakReference(projectRootElement);
            }
        }

        /// <summary>
        /// Unloads all the projects contained by this ProjectCollection.
        /// Host should call this when they are completely done with all the projects.
        /// </summary>
        public void UnloadAllProjects()
        {
            using (_locker.EnterDisposableWriteLock())
            {
                foreach (Project project in _loadedProjects)
                {
                    project.Zombify();

                    // We're removing every entry from the project collection
                    // so unregister any and all host objects for each project
                    _hostServices?.UnregisterProject(project.FullPath);
                }

                _loadedProjects.RemoveAllProjects();

                ProjectRootElementCache.Clear();
            }
        }

        /// <summary>
        /// Get any global property on the project collection that has the specified name,
        /// otherwise returns null.
        /// </summary>
        public ProjectPropertyInstance GetGlobalProperty(string name)
        {
            using (_locker.EnterDisposableReadLock())
            {
                return _globalProperties[name];
            }
        }

        /// <summary>
        /// Set a global property at the collection-level,
        /// and on all projects in the project collection.
        /// </summary>
        public void SetGlobalProperty(string name, string value)
        {
            bool sendEvent = false;
            using (_locker.EnterDisposableWriteLock())
            {
                ProjectPropertyInstance propertyInGlobalProperties = _globalProperties.GetProperty(name);
                bool changed = propertyInGlobalProperties == null || !String.Equals(((IValued)propertyInGlobalProperties).EscapedValue, value, StringComparison.OrdinalIgnoreCase);
                if (changed)
                {
                    _globalProperties.Set(ProjectPropertyInstance.Create(name, value));
                    sendEvent = true;
                }

                // Copy LoadedProjectCollection as modifying a project's global properties will cause it to re-add
                var projects = new List<Project>(_loadedProjects);
                foreach (Project project in projects)
                {
                    project.SetGlobalProperty(name, value);
                }
            }

            if (sendEvent)
            {
                OnProjectCollectionChanged(
                    new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.GlobalProperties));
            }
        }

        /// <summary>
        /// Removes a global property from the collection-level set of global properties,
        /// and all projects in the project collection.
        /// If it was on this project collection, returns true.
        /// </summary>
        public bool RemoveGlobalProperty(string name)
        {
            bool set;
            using (_locker.EnterDisposableWriteLock())
            {
                set = _globalProperties.Remove(name);

                // Copy LoadedProjectCollection as modifying a project's global properties will cause it to re-add
                var projects = new List<Project>(_loadedProjects);
                foreach (Project project in projects)
                {
                    project.RemoveGlobalProperty(name);
                }
            }

            OnProjectCollectionChanged(new ProjectCollectionChangedEventArgs(ProjectCollectionChangedState.GlobalProperties));

            return set;
        }

        /// <summary>
        /// Called when a host is completely done with the project collection.
        /// </summary>
        // UNDONE: This is a hack to make sure the logging thread shuts down if the build used the logging service
        // off the ProjectCollection. After CTP we need to rationalize this and see if we can remove the logging service from
        // the project collection entirely so this isn't necessary.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region IBuildComponent Members

        /// <summary>
        /// Initializes the component with the component host.
        /// </summary>
        /// <param name="host">The component host.</param>
        void IBuildComponent.InitializeComponent(IBuildComponentHost host)
        {
        }

        /// <summary>
        /// Shuts down the component.
        /// </summary>
        void IBuildComponent.ShutdownComponent()
        {
        }

        #endregion

        /// <summary>
        /// Unloads a project XML root element from the cache entirely, if it is not
        /// in use by project loaded into this collection.
        /// Returns true if it was unloaded successfully, or was not already loaded.
        /// Returns false if it was not unloaded because it was still in use by a loaded <see cref="Project"/>.
        /// </summary>
        /// <param name="projectRootElement">The project XML root element to unload.</param>
        public bool TryUnloadProject(ProjectRootElement projectRootElement)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectRootElement);
            if (projectRootElement.Link != null)
            {
                return false;
            }

            using (_locker.EnterDisposableWriteLock())
            {
                ProjectRootElementCache.DiscardStrongReferences();

                Project conflictingProject = GetLoadedProjects(false, null).FirstOrDefault(project => project.UsesProjectRootElement(projectRootElement));
                if (conflictingProject == null)
                {
                    ProjectRootElementCache.DiscardAnyWeakReference(projectRootElement);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Logs a BuildFinished event. This is used specifically when a ProjectCollection is created but never actually built, yet a BuildFinished event
        /// is still desired. As an example, if a Project is just meant to be evaluated, but a binlog is also collected, that binlog should be able to
        /// say the build succeeded or failed. This provides a mechanism to achieve that.
        /// </summary>
        public void LogBuildFinishedEvent(bool success) => _loggingService.LogBuildFinished(success);

        /// <summary>
        /// Called by a Project object to load itself into this collection.
        /// If the project was already loaded under a different name, it is unloaded.
        /// Stores the project in the list of loaded projects if it has a name.
        /// Does not store the project if it has no name because it has not been saved to disk yet.
        /// If the project previously had a name, but was not in the collection already, throws InvalidOperationException.
        /// If the project was not previously in the collection, sets the collection's global properties on it.
        /// </summary>
        internal void OnAfterRenameLoadedProject(string oldFullPathIfAny, Project project)
        {
            if (project.FullPath == null)
            {
                return;
            }

            using (_locker.EnterDisposableWriteLock())
            {
                if (oldFullPathIfAny != null)
                {
                    bool existed = _loadedProjects.RemoveProject(oldFullPathIfAny, project);
                    ErrorUtilities.VerifyThrowInvalidOperation(existed, "OM_ProjectWasNotLoaded");
                }

                // The only time this ever gets called with a null full path is when the project is first being
                // constructed.  The mere fact that this method is being called means that this project will belong
                // to this project collection.  As such, it has already had all necessary global properties applied
                // when being constructed -- we don't need to do anything special here.
                // If we did add global properties here, we would just end up either duplicating work or possibly
                // wiping out global properties set on the project meant to override the ProjectCollection copies.
                _loadedProjects.AddProject(project);

                if (_hostServices != null)
                {
                    HostServices.OnRenameProject(oldFullPathIfAny, project.FullPath);
                }
            }
        }

        /// <summary>
        /// Called after a loaded project's global properties are changed, so we can update
        /// our loaded project table.
        /// Project need not already be in the project collection yet, but it can't be in another one.
        /// </summary>
        /// <remarks>
        /// We have to remove and re-add so that there's an error if there's already an equivalent
        /// project loaded.
        /// </remarks>
        internal void AfterUpdateLoadedProjectGlobalProperties(Project project)
        {
            using (_locker.EnterDisposableWriteLock())
            {
                ErrorUtilities.VerifyThrowInvalidOperation(ReferenceEquals(project.ProjectCollection, this), "OM_IncorrectObjectAssociation", "Project", "ProjectCollection");

                if (project.FullPath == null)
                {
                    return;
                }

                bool existed = _loadedProjects.RemoveProject(project);
                if (existed)
                {
                    _loadedProjects.AddProject(project);
                }
            }
        }

        /// <summary>
        /// Following standard framework guideline dispose pattern.
        /// Shut down logging service if the project collection owns one, in order
        /// to shut down the logger thread and loggers.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources..</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                ShutDownLoggingService();
                if (ProjectRootElementCache != null)
                {
                    ProjectRootElementCache.ProjectRootElementAddedHandler -= ProjectRootElementCache_ProjectRootElementAddedHandler;
                    ProjectRootElementCache.ProjectRootElementDirtied -= ProjectRootElementCache_ProjectRootElementDirtiedHandler;
                    ProjectRootElementCache.ProjectDirtied -= ProjectRootElementCache_ProjectDirtiedHandler;
                }
                Tracing.Dump();
            }
        }

        /// <summary>
        /// Remove a toolset and does not raise events. The caller should have acquired a write lock on this method's behalf.
        /// </summary>
        /// <param name="toolsVersion">The toolset to remove.</param>
        /// <returns><c>true</c> if the toolset was found and removed; <c>false</c> otherwise.</returns>
        private bool RemoveToolsetInternal(string toolsVersion)
        {
            Debug.Assert(_locker.IsWriteLockHeld);

            if (!_toolsets.Remove(toolsVersion))
            {
                return false;
            }

            _toolsetsVersion++;
            return true;
        }

        /// <summary>
        /// Adds a logger to the collection of loggers used for builds of projects in this collection.
        /// If the logger object is already in the collection, does nothing.
        /// </summary>
        private void RegisterLoggerInternal(ILogger logger)
        {
            ErrorUtilities.VerifyThrowArgumentNull(logger);
            Debug.Assert(_locker.IsWriteLockHeld);
            _loggingService.RegisterLogger(new ReusableLogger(logger));
        }

        /// <summary>
        /// Handler which is called when a project is added to the RootElementCache of this project collection. We then fire an event indicating that a project was added to the collection itself.
        /// </summary>
        private void ProjectRootElementCache_ProjectRootElementAddedHandler(object sender, ProjectRootElementCacheAddEntryEventArgs e)
        {
            ProjectAdded?.Invoke(this, new ProjectAddedToProjectCollectionEventArgs(e.RootElement));
        }

        /// <summary>
        /// Handler which is called when a project that is part of this collection is dirtied. We then fire an event indicating that a project has been dirtied.
        /// </summary>
        private void ProjectRootElementCache_ProjectRootElementDirtiedHandler(object sender, ProjectXmlChangedEventArgs e)
        {
            OnProjectXmlChanged(e);
        }

        /// <summary>
        /// Handler which is called when a project is dirtied.
        /// </summary>
        private void ProjectRootElementCache_ProjectDirtiedHandler(object sender, ProjectChangedEventArgs e)
        {
            OnProjectChanged(e);
        }

        /// <summary>
        /// Raises the <see cref="ProjectXmlChanged"/> event.
        /// </summary>
        /// <param name="e">The event arguments that indicate ProjectRootElement-specific details.</param>
        private void OnProjectXmlChanged(ProjectXmlChangedEventArgs e)
        {
            ProjectXmlChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="ProjectChanged"/> event.
        /// </summary>
        /// <param name="e">The event arguments that indicate Project-specific details.</param>
        private void OnProjectChanged(ProjectChangedEventArgs e)
        {
            ProjectChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="ProjectCollectionChanged"/> event.
        /// </summary>
        /// <param name="e">The event arguments that indicate details on what changed on the collection.</param>
        private void OnProjectCollectionChanged(ProjectCollectionChangedEventArgs e)
        {
            Debug.Assert(!_locker.IsWriteLockHeld, "We should never raise events while holding a private lock.");
            ProjectCollectionChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Shutdown the logging service
        /// </summary>
        private void ShutDownLoggingService()
        {
            if (_loggingService != null)
            {
                try
                {
                    (LoggingService as LoggingService)?.WaitForLoggingToProcessEvents();
                    ((IBuildComponent)LoggingService).ShutdownComponent();
                }
                catch (LoggerException)
                {
                    throw;
                }
                catch (InternalLoggerException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // According to Framework Guidelines, Dispose methods should never throw except in dire circumstances.
                    // However if we throw at all, its a bug. Throw InternalErrorException to emphasize that.
                    ErrorUtilities.ThrowInternalError("Throwing from logger shutdown", ex);
                    throw;
                }

                _loggingService = null;
            }
        }

        /// <summary>
        /// Create a new logging service
        /// </summary>
        private void CreateLoggingService(int maxCPUCount, bool onlyLogCriticalEvents)
        {
            _loggingService = BackEnd.Logging.LoggingService.CreateLoggingService(_loggerMode, 0 /*Evaluation can be done as if it was on node "0"*/);
            _loggingService.MaxCPUCount = maxCPUCount;
            _loggingService.OnlyLogCriticalEvents = onlyLogCriticalEvents;
        }

        /// <summary>
        /// Reset the toolsets using the provided toolset reader, used by unit tests
        /// </summary>
        internal void ResetToolsetsForTests(ToolsetConfigurationReader configurationReaderForTestsOnly)
        {
            InitializeToolsetCollection(configReader: configurationReaderForTestsOnly);
        }

#if FEATURE_WIN32_REGISTRY
        /// <summary>
        /// Reset the toolsets using the provided toolset reader, used by unit tests
        /// </summary>
        internal void ResetToolsetsForTests(ToolsetRegistryReader registryReaderForTestsOnly)
        {
            InitializeToolsetCollection(registryReader: registryReaderForTestsOnly);
        }
#endif

        /// <summary>
        /// Populate Toolsets with a dictionary of (toolset version, Toolset)
        /// using information from the registry and config file, if any.
        /// </summary>
        private void InitializeToolsetCollection(
#if FEATURE_WIN32_REGISTRY
                ToolsetRegistryReader registryReader = null,
#endif
                ToolsetConfigurationReader configReader = null)
        {
            _toolsets = new Dictionary<string, Toolset>(StringComparer.OrdinalIgnoreCase);

            // We only want our local toolset (as defined in MSBuild.exe.config) when we're operating locally...
            _defaultToolsVersion = ToolsetReader.ReadAllToolsets(_toolsets,
#if FEATURE_WIN32_REGISTRY
                    registryReader,
#endif
                    configReader,
                    EnvironmentProperties, _globalProperties, ToolsetLocations);

            _toolsetsVersion++;
        }

        /// <summary>
        /// Event to provide information about what project just got added to the project collection.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible", Justification = "This has been API reviewed")]
        public class ProjectAddedToProjectCollectionEventArgs : EventArgs
        {
            /// <summary>
            /// The root element which was added to the project collection.
            /// </summary>
            public ProjectAddedToProjectCollectionEventArgs(ProjectRootElement element)
            {
                ProjectRootElement = element;
            }

            /// <summary>
            /// Root element which was added to the project collection.
            /// </summary>
            public ProjectRootElement ProjectRootElement { get; }
        }

        /// <summary>
        /// The ReusableLogger wraps a logger and allows it to be used for both design-time and build-time.  It internally swaps
        /// between the design-time and build-time event sources in response to Initialize and Shutdown events.
        /// </summary>
        internal class ReusableLogger : INodeLogger, IEventSource4
        {
            /// <summary>
            /// The logger we are wrapping.
            /// </summary>
            private readonly ILogger _originalLogger;

            /// <summary>
            /// Returns the logger we are wrapping.
            /// </summary>
            internal ILogger OriginalLogger => _originalLogger;

            /// <summary>
            /// The design-time event source
            /// </summary>
            private IEventSource _designTimeEventSource;

            /// <summary>
            /// The build-time event source
            /// </summary>
            private IEventSource _buildTimeEventSource;

            /// <summary>
            /// The Any event handler
            /// </summary>
            private AnyEventHandler _anyEventHandler;

            /// <summary>
            /// The BuildFinished event handler
            /// </summary>
            private BuildFinishedEventHandler _buildFinishedEventHandler;

            /// <summary>
            /// The BuildStarted event handler
            /// </summary>
            private BuildStartedEventHandler _buildStartedEventHandler;

            /// <summary>
            /// The Custom event handler
            /// </summary>
            private CustomBuildEventHandler _customBuildEventHandler;

            /// <summary>
            /// The Error event handler
            /// </summary>
            private BuildErrorEventHandler _buildErrorEventHandler;

            /// <summary>
            /// The Message event handler
            /// </summary>
            private BuildMessageEventHandler _buildMessageEventHandler;

            /// <summary>
            /// The ProjectFinished event handler
            /// </summary>
            private ProjectFinishedEventHandler _projectFinishedEventHandler;

            /// <summary>
            /// The ProjectStarted event handler
            /// </summary>
            private ProjectStartedEventHandler _projectStartedEventHandler;

            /// <summary>
            /// The Status event handler
            /// </summary>
            private BuildStatusEventHandler _buildStatusEventHandler;

            /// <summary>
            /// The TargetFinished event handler
            /// </summary>
            private TargetFinishedEventHandler _targetFinishedEventHandler;

            /// <summary>
            /// The TargetStarted event handler
            /// </summary>
            private TargetStartedEventHandler _targetStartedEventHandler;

            /// <summary>
            /// The TaskFinished event handler
            /// </summary>
            private TaskFinishedEventHandler _taskFinishedEventHandler;

            /// <summary>
            /// The TaskStarted event handler
            /// </summary>
            private TaskStartedEventHandler _taskStartedEventHandler;

            /// <summary>
            /// The Warning event handler
            /// </summary>
            private BuildWarningEventHandler _buildWarningEventHandler;

            /// <summary>
            ///  The telemetry event handler.
            /// </summary>
            private TelemetryEventHandler _telemetryEventHandler;

            private bool _includeEvaluationMetaprojects;

            private bool _includeEvaluationProfiles;

            private bool _includeTaskInputs;

            private bool _includeEvaluationPropertiesAndItems;

            /// <summary>
            /// Constructor.
            /// </summary>
            public ReusableLogger(ILogger originalLogger)
            {
                ErrorUtilities.VerifyThrowArgumentNull(originalLogger);
                _originalLogger = originalLogger;
            }

            #region IEventSource Members

            /// <summary>
            /// The Message logging event
            /// </summary>
            public event BuildMessageEventHandler MessageRaised;

            /// <summary>
            /// The Error logging event
            /// </summary>
            public event BuildErrorEventHandler ErrorRaised;

            /// <summary>
            /// The Warning logging event
            /// </summary>
            public event BuildWarningEventHandler WarningRaised;

            /// <summary>
            /// The BuildStarted logging event
            /// </summary>
            public event BuildStartedEventHandler BuildStarted;

            /// <summary>
            /// The BuildFinished logging event
            /// </summary>
            public event BuildFinishedEventHandler BuildFinished;

            /// <summary>
            /// The BuildCanceled logging event
            /// </summary>
            public event BuildCanceledEventHandler BuildCanceled;

            /// <summary>
            /// The ProjectStarted logging event
            /// </summary>
            public event ProjectStartedEventHandler ProjectStarted;

            /// <summary>
            /// The ProjectFinished logging event
            /// </summary>
            public event ProjectFinishedEventHandler ProjectFinished;

            /// <summary>
            /// The TargetStarted logging event
            /// </summary>
            public event TargetStartedEventHandler TargetStarted;

            /// <summary>
            /// The TargetFinished logging event
            /// </summary>
            public event TargetFinishedEventHandler TargetFinished;

            /// <summary>
            /// The TashStarted logging event
            /// </summary>
            public event TaskStartedEventHandler TaskStarted;

            /// <summary>
            /// The TaskFinished logging event
            /// </summary>
            public event TaskFinishedEventHandler TaskFinished;

            /// <summary>
            /// The Custom logging event
            /// </summary>
            public event CustomBuildEventHandler CustomEventRaised;

            /// <summary>
            /// The Status logging event
            /// </summary>
            public event BuildStatusEventHandler StatusEventRaised;

            /// <summary>
            /// The Any logging event
            /// </summary>
            public event AnyEventHandler AnyEventRaised;

            /// <summary>
            /// The telemetry sent event.
            /// </summary>
            public event TelemetryEventHandler TelemetryLogged;

            /// <summary>
            /// Should evaluation events include generated metaprojects?
            /// </summary>
            public void IncludeEvaluationMetaprojects()
            {
                if (_buildTimeEventSource is IEventSource3 buildEventSource3)
                {
                    buildEventSource3.IncludeEvaluationMetaprojects();
                }

                if (_designTimeEventSource is IEventSource3 designTimeEventSource3)
                {
                    designTimeEventSource3.IncludeEvaluationMetaprojects();
                }

                _includeEvaluationMetaprojects = true;
            }

            /// <summary>
            /// Should evaluation events include profiling information?
            /// </summary>
            public void IncludeEvaluationProfiles()
            {
                if (_buildTimeEventSource is IEventSource3 buildEventSource3)
                {
                    buildEventSource3.IncludeEvaluationProfiles();
                }

                if (_designTimeEventSource is IEventSource3 designTimeEventSource3)
                {
                    designTimeEventSource3.IncludeEvaluationProfiles();
                }

                _includeEvaluationProfiles = true;
            }

            /// <summary>
            /// Should task events include task inputs?
            /// </summary>
            public void IncludeTaskInputs()
            {
                if (_buildTimeEventSource is IEventSource3 buildEventSource3)
                {
                    buildEventSource3.IncludeTaskInputs();
                }

                if (_designTimeEventSource is IEventSource3 designTimeEventSource3)
                {
                    designTimeEventSource3.IncludeTaskInputs();
                }

                _includeTaskInputs = true;
            }

            public void IncludeEvaluationPropertiesAndItems()
            {
                if (_buildTimeEventSource is IEventSource4 buildEventSource4)
                {
                    buildEventSource4.IncludeEvaluationPropertiesAndItems();
                }

                if (_designTimeEventSource is IEventSource4 designTimeEventSource4)
                {
                    designTimeEventSource4.IncludeEvaluationPropertiesAndItems();
                }

                _includeEvaluationPropertiesAndItems = true;
            }

            #endregion

            #region ILogger Members

            /// <summary>
            /// The logger verbosity
            /// </summary>
            public LoggerVerbosity Verbosity
            {
                get => _originalLogger.Verbosity;
                set => _originalLogger.Verbosity = value;
            }

            /// <summary>
            /// The logger parameters
            /// </summary>
            public string Parameters
            {
                get => _originalLogger.Parameters;

                set => _originalLogger.Parameters = value;
            }

            /// <summary>
            /// If we haven't yet been initialized, we register for design time events and initialize the logger we are holding.
            /// If we are in design-time mode
            /// </summary>
            public void Initialize(IEventSource eventSource, int nodeCount)
            {
                if (_designTimeEventSource == null)
                {
                    _designTimeEventSource = eventSource;
                    RegisterForEvents(_designTimeEventSource);

                    if (_originalLogger is INodeLogger logger)
                    {
                        logger.Initialize(this, nodeCount);
                    }
                    else
                    {
                        _originalLogger.Initialize(this);
                    }
                }
                else
                {
                    ErrorUtilities.VerifyThrow(_buildTimeEventSource == null, "Already registered for build-time.");
                    _buildTimeEventSource = eventSource;
                    UnregisterForEvents(_designTimeEventSource);
                    RegisterForEvents(_buildTimeEventSource);
                }
            }

            /// <summary>
            /// If we haven't yet been initialized, we register for design time events and initialize the logger we are holding.
            /// If we are in design-time mode
            /// </summary>
            public void Initialize(IEventSource eventSource)
            {
                Initialize(eventSource, 1);
            }

            /// <summary>
            /// If we are in build-time mode, we unregister for build-time events and re-register for design-time events.
            /// If we are in design-time mode, we unregister for design-time events and shut down the logger we are holding.
            /// </summary>
            public void Shutdown()
            {
                if (_buildTimeEventSource != null)
                {
                    UnregisterForEvents(_buildTimeEventSource);
                    RegisterForEvents(_designTimeEventSource);
                    _buildTimeEventSource = null;
                }
                else
                {
                    ErrorUtilities.VerifyThrow(_designTimeEventSource != null, "Already unregistered for design-time.");
                    UnregisterForEvents(_designTimeEventSource);
                    _originalLogger.Shutdown();
                }
            }

            #endregion

            /// <summary>
            /// Registers for all of the events on the specified event source.
            /// </summary>
            private void RegisterForEvents(IEventSource eventSource)
            {
                // Create the handlers.
                _anyEventHandler = AnyEventRaisedHandler;
                _buildFinishedEventHandler = BuildFinishedHandler;
                _buildStartedEventHandler = BuildStartedHandler;
                _customBuildEventHandler = CustomEventRaisedHandler;
                _buildErrorEventHandler = ErrorRaisedHandler;
                _buildMessageEventHandler = MessageRaisedHandler;
                _projectFinishedEventHandler = ProjectFinishedHandler;
                _projectStartedEventHandler = ProjectStartedHandler;
                _buildStatusEventHandler = StatusEventRaisedHandler;
                _targetFinishedEventHandler = TargetFinishedHandler;
                _targetStartedEventHandler = TargetStartedHandler;
                _taskFinishedEventHandler = TaskFinishedHandler;
                _taskStartedEventHandler = TaskStartedHandler;
                _buildWarningEventHandler = WarningRaisedHandler;
                _telemetryEventHandler = TelemetryLoggedHandler;

                // Register for the events.
                eventSource.AnyEventRaised += _anyEventHandler;
                eventSource.BuildFinished += _buildFinishedEventHandler;
                eventSource.BuildStarted += _buildStartedEventHandler;
                eventSource.CustomEventRaised += _customBuildEventHandler;
                eventSource.ErrorRaised += _buildErrorEventHandler;
                eventSource.MessageRaised += _buildMessageEventHandler;
                eventSource.ProjectFinished += _projectFinishedEventHandler;
                eventSource.ProjectStarted += _projectStartedEventHandler;
                eventSource.StatusEventRaised += _buildStatusEventHandler;
                eventSource.TargetFinished += _targetFinishedEventHandler;
                eventSource.TargetStarted += _targetStartedEventHandler;
                eventSource.TaskFinished += _taskFinishedEventHandler;
                eventSource.TaskStarted += _taskStartedEventHandler;
                eventSource.WarningRaised += _buildWarningEventHandler;

                if (eventSource is IEventSource2 eventSource2)
                {
                    eventSource2.TelemetryLogged += _telemetryEventHandler;
                }

                if (eventSource is IEventSource3 eventSource3)
                {
                    if (_includeEvaluationMetaprojects)
                    {
                        eventSource3.IncludeEvaluationMetaprojects();
                    }

                    if (_includeEvaluationProfiles)
                    {
                        eventSource3.IncludeEvaluationProfiles();
                    }

                    if (_includeTaskInputs)
                    {
                        eventSource3.IncludeTaskInputs();
                    }
                }

                if (eventSource is IEventSource4 eventSource4)
                {
                    if (_includeEvaluationPropertiesAndItems)
                    {
                        eventSource4.IncludeEvaluationPropertiesAndItems();
                    }
                }
            }

            /// <summary>
            /// Unregisters for all events on the specified event source.
            /// </summary>
            private void UnregisterForEvents(IEventSource eventSource)
            {
                // Unregister for the events.
                eventSource.AnyEventRaised -= _anyEventHandler;
                eventSource.BuildFinished -= _buildFinishedEventHandler;
                eventSource.BuildStarted -= _buildStartedEventHandler;
                eventSource.CustomEventRaised -= _customBuildEventHandler;
                eventSource.ErrorRaised -= _buildErrorEventHandler;
                eventSource.MessageRaised -= _buildMessageEventHandler;
                eventSource.ProjectFinished -= _projectFinishedEventHandler;
                eventSource.ProjectStarted -= _projectStartedEventHandler;
                eventSource.StatusEventRaised -= _buildStatusEventHandler;
                eventSource.TargetFinished -= _targetFinishedEventHandler;
                eventSource.TargetStarted -= _targetStartedEventHandler;
                eventSource.TaskFinished -= _taskFinishedEventHandler;
                eventSource.TaskStarted -= _taskStartedEventHandler;
                eventSource.WarningRaised -= _buildWarningEventHandler;

                if (eventSource is IEventSource2 eventSource2)
                {
                    eventSource2.TelemetryLogged -= _telemetryEventHandler;
                }

                // Null out the handlers.
                _anyEventHandler = null;
                _buildFinishedEventHandler = null;
                _buildStartedEventHandler = null;
                _customBuildEventHandler = null;
                _buildErrorEventHandler = null;
                _buildMessageEventHandler = null;
                _projectFinishedEventHandler = null;
                _projectStartedEventHandler = null;
                _buildStatusEventHandler = null;
                _targetFinishedEventHandler = null;
                _targetStartedEventHandler = null;
                _taskFinishedEventHandler = null;
                _taskStartedEventHandler = null;
                _buildWarningEventHandler = null;
                _telemetryEventHandler = null;
            }

            /// <summary>
            /// Handler for Warning events.
            /// </summary>
            private void WarningRaisedHandler(object sender, BuildWarningEventArgs e)
            {
                WarningRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for TaskStarted events.
            /// </summary>
            private void TaskStartedHandler(object sender, TaskStartedEventArgs e)
            {
                TaskStarted?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for TaskFinished events.
            /// </summary>
            private void TaskFinishedHandler(object sender, TaskFinishedEventArgs e)
            {
                TaskFinished?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for TargetStarted events.
            /// </summary>
            private void TargetStartedHandler(object sender, TargetStartedEventArgs e)
            {
                TargetStarted?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for TargetFinished events.
            /// </summary>
            private void TargetFinishedHandler(object sender, TargetFinishedEventArgs e)
            {
                TargetFinished?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for Status events.
            /// </summary>
            private void StatusEventRaisedHandler(object sender, BuildStatusEventArgs e)
            {
                StatusEventRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for ProjectStarted events.
            /// </summary>
            private void ProjectStartedHandler(object sender, ProjectStartedEventArgs e)
            {
                ProjectStarted?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for ProjectFinished events.
            /// </summary>
            private void ProjectFinishedHandler(object sender, ProjectFinishedEventArgs e)
            {
                ProjectFinished?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for Message events.
            /// </summary>
            private void MessageRaisedHandler(object sender, BuildMessageEventArgs e)
            {
                MessageRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for Error events.
            /// </summary>
            private void ErrorRaisedHandler(object sender, BuildErrorEventArgs e)
            {
                ErrorRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for Custom events.
            /// </summary>
            private void CustomEventRaisedHandler(object sender, CustomBuildEventArgs e)
            {
                CustomEventRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for BuildStarted events.
            /// </summary>
            private void BuildStartedHandler(object sender, BuildStartedEventArgs e)
            {
                BuildStarted?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for BuildFinished events.
            /// </summary>
            private void BuildFinishedHandler(object sender, BuildFinishedEventArgs e)
            {
                BuildFinished?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for BuildCanceled events.
            /// </summary>
            private void BuildCanceledHandler(object sender, BuildCanceledEventArgs e)
            {
                BuildCanceled?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for Any events.
            /// </summary>
            private void AnyEventRaisedHandler(object sender, BuildEventArgs e)
            {
                AnyEventRaised?.Invoke(sender, e);
            }

            /// <summary>
            /// Handler for telemetry events.
            /// </summary>
            private void TelemetryLoggedHandler(object sender, TelemetryEventArgs e)
            {
                TelemetryLogged?.Invoke(sender, e);
            }
        }

        /// <summary>
        /// Holder for the projects loaded into this collection.
        /// </summary>
        private class LoadedProjectCollection : IEnumerable<Project>
        {
            /// <summary>
            /// The collection of all projects already loaded into this collection.
            /// Key is the full path to the project, value is a list of projects with that path, each
            /// with different global properties and/or tools version.
            /// </summary>
            /// <remarks>
            /// If hosts tend to load lots of projects with the same path, the value will have to be
            /// changed to a more efficient type of collection.
            ///
            /// Lock on this object. Concurrent load must be thread safe.
            /// Not using ConcurrentDictionary because some of the add/update
            /// semantics would get convoluted.
            /// </remarks>
            private Dictionary<string, List<Project>> _loadedProjects = new Dictionary<string, List<Project>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Count of loaded projects
            /// </summary>
            private int _count;

            /// <summary>
            /// Returns the number of projects currently loaded
            /// </summary>
            internal int Count
            {
                get
                {
                    lock (_loadedProjects)
                    {
                        return _count;
                    }
                }
            }

            /// <summary>
            /// Enumerate all the projects
            /// </summary>
            public IEnumerator<Project> GetEnumerator()
            {
                lock (_loadedProjects)
                {
                    var projects = new List<Project>();

                    foreach (List<Project> projectList in _loadedProjects.Values)
                    {
                        foreach (Project project in projectList)
                        {
                            projects.Add(project);
                        }
                    }

                    return projects.GetEnumerator();
                }
            }

            /// <summary>
            /// Enumerate all the projects.
            /// </summary>
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            /// <summary>
            /// Get all projects with the provided path.
            /// Returns an empty list if there are none.
            /// </summary>
            internal IList<Project> GetMatchingProjectsIfAny(string fullPath)
            {
                lock (_loadedProjects)
                {
                    _loadedProjects.TryGetValue(fullPath, out List<Project> candidates);

                    return candidates ?? (IList<Project>)Array.Empty<Project>();
                }
            }

            /// <summary>
            /// Returns the project in the collection matching the path, global properties, and tools version provided.
            /// There can be no more than one match.
            /// If none is found, returns null.
            /// </summary>
            internal Project GetMatchingProjectIfAny(string fullPath, IDictionary<string, string> globalProperties, string toolsVersion)
            {
                lock (_loadedProjects)
                {
                    if (_loadedProjects.TryGetValue(fullPath, out List<Project> candidates))
                    {
                        foreach (Project candidate in candidates)
                        {
                            if (HasEquivalentGlobalPropertiesAndToolsVersion(candidate, globalProperties, toolsVersion))
                            {
                                return candidate;
                            }
                        }
                    }

                    return null;
                }
            }

            /// <summary>
            /// Adds the provided project to the collection.
            /// If there is already an equivalent project, throws InvalidOperationException.
            /// </summary>
            internal void AddProject(Project project)
            {
                lock (_loadedProjects)
                {
                    if (!_loadedProjects.TryGetValue(project.FullPath, out List<Project> projectList))
                    {
                        projectList = new List<Project>();
                        _loadedProjects.Add(project.FullPath, projectList);
                    }

                    foreach (Project existing in projectList)
                    {
                        if (HasEquivalentGlobalPropertiesAndToolsVersion(existing, project.GlobalProperties, project.ToolsVersion))
                        {
                            ErrorUtilities.ThrowInvalidOperation("OM_MatchingProjectAlreadyInCollection", existing.FullPath);
                        }
                    }

                    projectList.Add(project);
                    _count++;
                }
            }

            /// <summary>
            /// Removes the provided project from the collection.
            /// If project was not loaded, returns false.
            /// </summary>
            internal bool RemoveProject(Project project)
            {
                return RemoveProject(project.FullPath, project);
            }

            /// <summary>
            /// Removes a project, using the specified full path to use as the key to find it.
            /// This is specified separately in case the project was previously stored under a different path.
            /// </summary>
            internal bool RemoveProject(string projectFullPath, Project project)
            {
                lock (_loadedProjects)
                {
                    if (!_loadedProjects.TryGetValue(projectFullPath, out List<Project> projectList))
                    {
                        return false;
                    }

                    bool result = projectList.Remove(project);

                    if (result)
                    {
                        _count--;

                        if (projectList.Count == 0)
                        {
                            _loadedProjects.Remove(projectFullPath);
                        }
                    }

                    return result;
                }
            }

            /// <summary>
            /// Removes all projects from the collection.
            /// </summary>
            internal void RemoveAllProjects()
            {
                lock (_loadedProjects)
                {
                    _loadedProjects = new Dictionary<string, List<Project>>(StringComparer.OrdinalIgnoreCase);
                    _count = 0;
                }
            }

            /// <summary>
            /// Returns true if the global properties and tools version provided are equivalent to
            /// those in the provided project, otherwise false.
            /// </summary>
            private static bool HasEquivalentGlobalPropertiesAndToolsVersion(Project project, IDictionary<string, string> globalProperties, string toolsVersion)
            {
                if (!String.Equals(project.ToolsVersion, toolsVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (project.GlobalPropertiesCount != globalProperties.Count)
                {
                    return false;
                }

                foreach (KeyValuePair<string, string> leftProperty in project.GlobalPropertiesEnumerable)
                {
                    if (!globalProperties.TryGetValue(leftProperty.Key, out var rightValue))
                    {
                        return false;
                    }

                    if (!String.Equals(leftProperty.Value, rightValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
