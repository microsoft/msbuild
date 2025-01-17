// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Experimental;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.FileAccesses;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Graph;
using Microsoft.Build.Internal;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.Debugging;
using Microsoft.NET.StringTools;
using ExceptionHandling = Microsoft.Build.Shared.ExceptionHandling;
using ForwardingLoggerRecord = Microsoft.Build.Logging.ForwardingLoggerRecord;
using LoggerDescription = Microsoft.Build.Logging.LoggerDescription;

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// This class is the public entry point for executing builds.
    /// </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Refactoring at the end of Beta1 is not appropriate.")]
    public class BuildManager : INodePacketHandler, IBuildComponentHost, IDisposable
    {
        // TODO: Figure out a more elegant way to do this.
        //       The rationale for this is that we can detect during design-time builds in the Evaluator (which populates this) that the project cache will be used so that we don't
        //       need to evaluate the project at build time just to figure that out, which would regress perf for scenarios which don't use the project cache.
        internal static ConcurrentDictionary<ProjectCacheDescriptor, ProjectCacheDescriptor> ProjectCacheDescriptors { get; } = new(ProjectCacheDescriptorEqualityComparer.Instance);

        /// <summary>
        /// The object used for thread-safe synchronization of static members.
        /// </summary>
        private static readonly Object s_staticSyncLock = new Object();

        /// <summary>
        /// The object used for thread-safe synchronization of BuildManager shared data and the Scheduler.
        /// </summary>
        private readonly Object _syncLock = new Object();

        /// <summary>
        /// The singleton instance for the BuildManager.
        /// </summary>
        private static BuildManager? s_singletonInstance;

        /// <summary>
        /// The next build id;
        /// </summary>
        private static int s_nextBuildId;

        /// <summary>
        /// The next build request configuration ID to use.
        /// These must be unique across build managers, as they
        /// are used as part of cache file names, for example.
        /// </summary>
        private static int s_nextBuildRequestConfigurationId;

        /// <summary>
        /// The cache for build request configurations.
        /// </summary>
        private IConfigCache? _configCache;

        /// <summary>
        /// The cache for build results.
        /// </summary>
        private IResultsCache? _resultsCache;

        /// <summary>
        /// The object responsible for creating and managing nodes.
        /// </summary>
        private INodeManager? _nodeManager;

        /// <summary>
        /// The object responsible for creating and managing task host nodes.
        /// </summary>
        private INodeManager? _taskHostNodeManager;

        /// <summary>
        /// The object which determines which projects to build, and where.
        /// </summary>
        private IScheduler? _scheduler;

        /// <summary>
        /// The node configuration to use for spawning new nodes.
        /// </summary>
        private NodeConfiguration? _nodeConfiguration;

        /// <summary>
        /// Any exception which occurs on a logging thread will go here.
        /// </summary>
        private ExceptionDispatchInfo? _threadException;

        /// <summary>
        /// Set of active nodes in the system.
        /// </summary>
        private readonly HashSet<int> _activeNodes;

        /// <summary>
        /// Event signalled when all nodes have shutdown.
        /// </summary>
        private AutoResetEvent? _noNodesActiveEvent;

        /// <summary>
        /// Mapping of nodes to the configurations they know about.
        /// </summary>
        private readonly Dictionary<int, HashSet<int>> _nodeIdToKnownConfigurations;

        /// <summary>
        /// Flag indicating if we are currently shutting down.  When set, we stop processing packets other than NodeShutdown.
        /// </summary>
        private bool _shuttingDown;

        /// <summary>
        /// CancellationTokenSource to use for async operations. This will be cancelled when we are shutting down to cancel any async operations.
        /// </summary>
        private CancellationTokenSource? _executionCancellationTokenSource;

        /// <summary>
        /// The current state of the BuildManager.
        /// </summary>
        private BuildManagerState _buildManagerState;

        /// <summary>
        /// The name given to this BuildManager as the component host.
        /// </summary>
        private readonly string _hostName;

        /// <summary>
        /// The parameters with which the build was started.
        /// </summary>
        private BuildParameters? _buildParameters;

        /// <summary>
        /// The current pending and active submissions.
        /// </summary>
        /// <remarks>
        /// { submissionId, BuildSubmission }
        /// </remarks>
        private readonly Dictionary<int, BuildSubmissionBase> _buildSubmissions;

        /// <summary>
        /// Event signalled when all build submissions are complete.
        /// </summary>
        private AutoResetEvent? _noActiveSubmissionsEvent;

        /// <summary>
        /// The overall success of the build.
        /// </summary>
        private bool _overallBuildSuccess;

        /// <summary>
        /// The next build submission id.
        /// </summary>
        private int _nextBuildSubmissionId;

        /// <summary>
        /// The last BuildParameters used for building.
        /// </summary>
        private bool? _previousLowPriority = null;

        /// <summary>
        /// Mapping of unnamed project instances to the file names assigned to them.
        /// </summary>
        private readonly Dictionary<ProjectInstance, string> _unnamedProjectInstanceToNames;

        /// <summary>
        /// The next ID to assign to a project which has no name.
        /// </summary>
        private int _nextUnnamedProjectId;

        /// <summary>
        /// The build component factories.
        /// </summary>
        private readonly BuildComponentFactoryCollection _componentFactories;

        /// <summary>
        /// Mapping of submission IDs to their first project started events.
        /// </summary>
        private readonly Dictionary<int, BuildEventArgs> _projectStartedEvents;

        /// <summary>
        /// Whether a cache has been provided by a project instance, meaning
        /// we've acquired at least one build submission that included a project instance.
        /// Once that has happened, we use the provided one, rather than our default.
        /// </summary>
        private bool _acquiredProjectRootElementCacheFromProjectInstance;

        /// <summary>
        /// The project started event handler
        /// </summary>
        private readonly ProjectStartedEventHandler _projectStartedEventHandler;

        /// <summary>
        /// The project finished event handler
        /// </summary>
        private readonly ProjectFinishedEventHandler _projectFinishedEventHandler;

        /// <summary>
        /// The logging exception event handler
        /// </summary>
        private readonly LoggingExceptionDelegate _loggingThreadExceptionEventHandler;

        /// <summary>
        /// Legacy threading semantic data associated with this build manager.
        /// </summary>
        private readonly LegacyThreadingData _legacyThreadingData;

        /// <summary>
        /// The worker queue.
        /// </summary>
        private ActionBlock<Action>? _workQueue;

        /// <summary>
        /// Flag indicating we have disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// When the BuildManager was created.
        /// </summary>
        private DateTime _instantiationTimeUtc;

        /// <summary>
        /// Messages to be logged
        /// </summary>
        private IEnumerable<DeferredBuildMessage>? _deferredBuildMessages;

        /// <summary>
        /// Build telemetry to be send when this build ends.
        /// <remarks>Could be null</remarks>
        /// </summary>
        private BuildTelemetry? _buildTelemetry;

        private ProjectCacheService? _projectCacheService;

        private bool _hasProjectCacheServiceInitializedVsScenario;

#if DEBUG
        /// <summary>
        /// <code>true</code> to wait for a debugger to be attached, otherwise <code>false</code>.
        /// </summary>
        [SuppressMessage("ApiDesign",
            "RS0016:Add public types and members to the declared API",
            Justification = "Only available in the Debug configuration.")]
        public static bool WaitForDebugger { get; set; }
#endif

        /// <summary>
        /// Creates a new unnamed build manager.
        /// Normally there is only one build manager in a process, and it is the default build manager.
        /// Access it with <see cref="BuildManager.DefaultBuildManager"/>
        /// </summary>
        public BuildManager()
            : this("Unnamed")
        {
        }

        /// <summary>
        /// Creates a new build manager with an arbitrary distinct name.
        /// Normally there is only one build manager in a process, and it is the default build manager.
        /// Access it with <see cref="BuildManager.DefaultBuildManager"/>
        /// </summary>
        public BuildManager(string hostName)
        {
            ErrorUtilities.VerifyThrowArgumentNull(hostName);
            _hostName = hostName;
            _buildManagerState = BuildManagerState.Idle;
            _buildSubmissions = new Dictionary<int, BuildSubmissionBase>();
            _noActiveSubmissionsEvent = new AutoResetEvent(true);
            _activeNodes = new HashSet<int>();
            _noNodesActiveEvent = new AutoResetEvent(true);
            _nodeIdToKnownConfigurations = new Dictionary<int, HashSet<int>>();
            _unnamedProjectInstanceToNames = new Dictionary<ProjectInstance, string>();
            _nextUnnamedProjectId = 1;
            _componentFactories = new BuildComponentFactoryCollection(this);
            _componentFactories.RegisterDefaultFactories();
            SerializationContractInitializer.Initialize();
            _projectStartedEvents = new Dictionary<int, BuildEventArgs>();

            _projectStartedEventHandler = OnProjectStarted;
            _projectFinishedEventHandler = OnProjectFinished;
            _loggingThreadExceptionEventHandler = OnLoggingThreadException;
            _legacyThreadingData = new LegacyThreadingData();
            _instantiationTimeUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="BuildManager"/> class.
        /// </summary>
        ~BuildManager()
        {
            Dispose(false /* disposing */);
        }

        /// <summary>
        /// Enumeration describing the current state of the build manager.
        /// </summary>
        private enum BuildManagerState
        {
            /// <summary>
            /// This is the default state.  <see cref="BeginBuild(BuildParameters)"/> may be called in this state.  All other methods raise InvalidOperationException
            /// </summary>
            Idle,

            /// <summary>
            /// This is the state the BuildManager is in after <see cref="BeginBuild(BuildParameters)"/> has been called but before <see cref="EndBuild()"/> has been called.
            /// <see cref="BuildManager.PendBuildRequest(Microsoft.Build.Execution.BuildRequestData)"/>, <see cref="BuildManager.BuildRequest(Microsoft.Build.Execution.BuildRequestData)"/>, <see cref="BuildManager.PendBuildRequest(GraphBuildRequestData)"/>, <see cref="BuildManager.BuildRequest(GraphBuildRequestData)"/>, and <see cref="BuildManager.EndBuild()"/> may be called in this state.
            /// </summary>
            Building,

            /// <summary>
            /// This is the state the BuildManager is in after <see cref="BuildManager.EndBuild()"/> has been called but before all existing submissions have completed.
            /// </summary>
            WaitingForBuildToComplete
        }

        /// <summary>
        /// Gets the singleton instance of the Build Manager.
        /// </summary>
        public static BuildManager DefaultBuildManager
        {
            get
            {
                if (s_singletonInstance == null)
                {
                    lock (s_staticSyncLock)
                    {
                        if (s_singletonInstance == null)
                        {
                            s_singletonInstance = new BuildManager("Default");
                        }
                    }
                }

                return s_singletonInstance;
            }
        }

        /// <summary>
        /// Retrieves a hosted<see cref="ISdkResolverService"/> instance for resolving SDKs.
        /// </summary>
        private ISdkResolverService SdkResolverService => ((this as IBuildComponentHost).GetComponent(BuildComponentType.SdkResolverService) as ISdkResolverService)!;

        /// <summary>
        /// Retrieves the logging service associated with a particular build
        /// </summary>
        /// <returns>The logging service.</returns>
        ILoggingService IBuildComponentHost.LoggingService => _componentFactories.GetComponent<ILoggingService>(BuildComponentType.LoggingService);

        /// <summary>
        /// Retrieves the name of the component host.
        /// </summary>
        string IBuildComponentHost.Name => _hostName;

        /// <summary>
        /// Retrieves the build parameters associated with this build.
        /// </summary>
        /// <returns>The build parameters.</returns>
        BuildParameters? IBuildComponentHost.BuildParameters => _buildParameters;

        /// <summary>
        /// Retrieves the LegacyThreadingData associated with a particular build manager
        /// </summary>
        LegacyThreadingData IBuildComponentHost.LegacyThreadingData => _legacyThreadingData;

        /// <summary>
        /// <see cref="BuildManager.BeginBuild(BuildParameters,IEnumerable{DeferredBuildMessage})"/>
        /// </summary>
        public readonly struct DeferredBuildMessage
        {
            public MessageImportance Importance { get; }

            public string Text { get; }

            public string? FilePath { get; }

            public DeferredBuildMessage(string text, MessageImportance importance)
            {
                Importance = importance;
                Text = text;
                FilePath = null;
            }

            public DeferredBuildMessage(string text, MessageImportance importance, string filePath)
            {
                Importance = importance;
                Text = text;
                FilePath = filePath;
            }
        }

        /// <summary>
        /// Prepares the BuildManager to receive build requests.
        /// </summary>
        /// <param name="parameters">The build parameters.  May be null.</param>
        /// <param name="deferredBuildMessages"> Build messages to be logged before the build begins. </param>
        /// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
        public void BeginBuild(BuildParameters parameters, IEnumerable<DeferredBuildMessage> deferredBuildMessages)
        {
            // TEMP can be modified from the environment. Most of Traits is lasts for the duration of the process (with a manual reset for tests)
            // and environment variables we use as properties are stored in a dictionary at the beginning of the build, so they also cannot be
            // changed during a build. Some of our older stuff uses live environment variable checks. The TEMP directory previously used a live
            // environment variable check, but it now uses a cached value. Nevertheless, we should support changing it between builds, so reset
            // it here in case the user is using Visual Studio or the MSBuild server, as those each last for multiple builds without changing
            // BuildManager.
            FileUtilities.ClearTempFileDirectory();

            // deferredBuildMessages cannot be an optional parameter on a single BeginBuild method because it would break binary compatibility.
            _deferredBuildMessages = deferredBuildMessages;
            BeginBuild(parameters);
            _deferredBuildMessages = null;
        }

        private void UpdatePriority(Process p, ProcessPriorityClass priority)
        {
            try
            {
                p.PriorityClass = priority;
            }
            catch (Win32Exception) { }
        }

        /// <summary>
        /// Prepares the BuildManager to receive build requests.
        /// </summary>
        /// <param name="parameters">The build parameters.  May be null.</param>
        /// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
        public void BeginBuild(BuildParameters parameters)
        {
            if (_previousLowPriority != null)
            {
                if (parameters.LowPriority != _previousLowPriority)
                {
                    if (NativeMethodsShared.IsWindows || parameters.LowPriority)
                    {
                        ProcessPriorityClass priority = parameters.LowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                        IEnumerable<Process>? processes = _nodeManager?.GetProcesses();
                        if (processes is not null)
                        {
                            foreach (Process p in processes)
                            {
                                UpdatePriority(p, priority);
                            }
                        }

                        processes = _taskHostNodeManager?.GetProcesses();
                        if (processes is not null)
                        {
                            foreach (Process p in processes)
                            {
                                UpdatePriority(p, priority);
                            }
                        }
                    }
                    else
                    {
                        _nodeManager?.ShutdownAllNodes();
                        _taskHostNodeManager?.ShutdownAllNodes();
                    }
                }
            }

            _previousLowPriority = parameters.LowPriority;

            if (Traits.Instance.DebugEngine)
            {
                parameters.DetailedSummary = true;
                parameters.LogTaskInputs = true;
            }
            OpenTelemetryManager.Initialize(false);

            lock (_syncLock)
            {
                AttachDebugger();

                // Check for build in progress.
                RequireState(BuildManagerState.Idle, "BuildInProgress");

                MSBuildEventSource.Log.BuildStart();

                // Initiate build telemetry data
                DateTime now = DateTime.UtcNow;

                // Acquire it from static variable so we can apply data collected up to this moment
                _buildTelemetry = KnownTelemetry.PartialBuildTelemetry;
                if (_buildTelemetry != null)
                {
                    KnownTelemetry.PartialBuildTelemetry = null;
                }
                else
                {
                    _buildTelemetry = new()
                    {
                        StartAt = now,
                    };
                }

                _buildTelemetry.InnerStartAt = now;

                if (BuildParameters.DumpOpportunisticInternStats)
                {
                    Strings.EnableDiagnostics();
                }

                _executionCancellationTokenSource = new CancellationTokenSource();

                _overallBuildSuccess = true;

                // Clone off the build parameters.
                _buildParameters = parameters?.Clone() ?? new BuildParameters();

                // Initialize additional build parameters.
                _buildParameters.BuildId = GetNextBuildId();

                if (_buildParameters.UsesCachedResults() && _buildParameters.ProjectIsolationMode == ProjectIsolationMode.False)
                {
                    // If input or output caches are used and the project isolation mode is set to
                    // ProjectIsolationMode.False, then set it to ProjectIsolationMode.True. The explicit
                    // condition on ProjectIsolationMode is necessary to ensure that, if we're using input
                    // or output caches and ProjectIsolationMode is set to ProjectIsolationMode.MessageUponIsolationViolation,
                    // ProjectIsolationMode isn't changed to ProjectIsolationMode.True.
                    _buildParameters.ProjectIsolationMode = ProjectIsolationMode.True;
                }

                if (_buildParameters.UsesOutputCache() && string.IsNullOrWhiteSpace(_buildParameters.OutputResultsCacheFile))
                {
                    _buildParameters.OutputResultsCacheFile = FileUtilities.NormalizePath("msbuild-cache");
                }

#if FEATURE_REPORTFILEACCESSES
                if (_buildParameters.ReportFileAccesses)
                {
                    EnableDetouredNodeLauncher();
                }
#endif

                // Initialize components.
                _nodeManager = ((IBuildComponentHost)this).GetComponent(BuildComponentType.NodeManager) as INodeManager;

                var loggingService = InitializeLoggingService();

                // Log deferred messages and response files
                LogDeferredMessages(loggingService, _deferredBuildMessages);

                // Log if BuildCheck is enabled
                if (_buildParameters.IsBuildCheckEnabled)
                {
                    loggingService.LogComment(buildEventContext: BuildEventContext.Invalid, MessageImportance.Normal, "BuildCheckEnabled");
                }

                // Log known deferred telemetry
                loggingService.LogTelemetry(buildEventContext: null, KnownTelemetry.LoggingConfigurationTelemetry.EventName, KnownTelemetry.LoggingConfigurationTelemetry.GetProperties());

                InitializeCaches();

#if FEATURE_REPORTFILEACCESSES
                var fileAccessManager = ((IBuildComponentHost)this).GetComponent<IFileAccessManager>(BuildComponentType.FileAccessManager);
#endif

                _projectCacheService = new ProjectCacheService(
                    this,
                    loggingService,
#if FEATURE_REPORTFILEACCESSES
                    fileAccessManager,
#endif
                    _configCache!,
                    _buildParameters.ProjectCacheDescriptor);

                _taskHostNodeManager = ((IBuildComponentHost)this).GetComponent<INodeManager>(BuildComponentType.TaskHostNodeManager);
                _scheduler = ((IBuildComponentHost)this).GetComponent<IScheduler>(BuildComponentType.Scheduler);

                _nodeManager!.RegisterPacketHandler(NodePacketType.BuildRequestBlocker, BuildRequestBlocker.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.BuildRequestConfiguration, BuildRequestConfiguration.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.BuildRequestConfigurationResponse, BuildRequestConfigurationResponse.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.BuildResult, BuildResult.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.FileAccessReport, FileAccessReport.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.NodeShutdown, NodeShutdown.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.ProcessReport, ProcessReport.FactoryForDeserialization, this);
                _nodeManager.RegisterPacketHandler(NodePacketType.ResolveSdkRequest, SdkResolverRequest.FactoryForDeserialization, SdkResolverService as INodePacketHandler);
                _nodeManager.RegisterPacketHandler(NodePacketType.ResourceRequest, ResourceRequest.FactoryForDeserialization, this);

                if (_threadException != null)
                {
                    ShutdownLoggingService(loggingService);

                    _threadException.Throw();
                }

                if (_workQueue == null)
                {
                    _workQueue = new ActionBlock<Action>(action => ProcessWorkQueue(action));
                }

                _buildManagerState = BuildManagerState.Building;

                _noActiveSubmissionsEvent!.Set();
                _noNodesActiveEvent!.Set();
            }

            ILoggingService InitializeLoggingService()
            {
                ILoggingService loggingService = CreateLoggingService(
                    AppendDebuggingLoggers(_buildParameters.Loggers),
                    _buildParameters.ForwardingLoggers,
                    _buildParameters.WarningsAsErrors,
                    _buildParameters.WarningsNotAsErrors,
                    _buildParameters.WarningsAsMessages);

                _nodeManager!.RegisterPacketHandler(NodePacketType.LogMessage, LogMessagePacket.FactoryForDeserialization, loggingService as INodePacketHandler);

                try
                {
                    loggingService.LogBuildStarted();

                    if (_buildParameters.UsesInputCaches())
                    {
                        loggingService.LogComment(BuildEventContext.Invalid, MessageImportance.Normal, "UsingInputCaches", string.Join(";", _buildParameters.InputResultsCacheFiles));
                    }

                    if (_buildParameters.UsesOutputCache())
                    {
                        loggingService.LogComment(BuildEventContext.Invalid, MessageImportance.Normal, "WritingToOutputCache", _buildParameters.OutputResultsCacheFile);
                    }
                }
                catch (Exception)
                {
                    ShutdownLoggingService(loggingService);
                    throw;
                }

                return loggingService;
            }

            // VS builds discard many msbuild events so attach a binlogger to capture them all.
            IEnumerable<ILogger> AppendDebuggingLoggers(IEnumerable<ILogger> loggers)
            {
                if (DebugUtils.ShouldDebugCurrentProcess is false ||
                    Traits.Instance.DebugEngine is false)
                {
                    return loggers;
                }

                var binlogPath = DebugUtils.FindNextAvailableDebugFilePath($"{DebugUtils.ProcessInfoString}_BuildManager_{_hostName}.binlog");

                var logger = new BinaryLogger { Parameters = binlogPath };

                return (loggers ?? [logger]);
            }

            void InitializeCaches()
            {
                Debug.Assert(Monitor.IsEntered(_syncLock));

                var usesInputCaches = _buildParameters.UsesInputCaches();

                if (usesInputCaches)
                {
                    ReuseOldCaches(_buildParameters.InputResultsCacheFiles);
                }

                _configCache = ((IBuildComponentHost)this).GetComponent<IConfigCache>(BuildComponentType.ConfigCache);
                _resultsCache = ((IBuildComponentHost)this).GetComponent<IResultsCache>(BuildComponentType.ResultsCache);

                if (!usesInputCaches && (_buildParameters.ResetCaches || _configCache!.IsConfigCacheSizeLargerThanThreshold()))
                {
                    ResetCaches();
                }
                else
                {
                    if (!usesInputCaches)
                    {
                        List<int> configurationsCleared = _configCache!.ClearNonExplicitlyLoadedConfigurations();

                        if (configurationsCleared != null)
                        {
                            foreach (int configurationId in configurationsCleared)
                            {
                                _resultsCache!.ClearResultsForConfiguration(configurationId);
                            }
                        }
                    }

                    foreach (var config in _configCache!)
                    {
                        config.ResultsNodeId = Scheduler.InvalidNodeId;
                    }

                    _buildParameters.ProjectRootElementCache.DiscardImplicitReferences();
                }
            }
        }

#if FEATURE_REPORTFILEACCESSES
        /// <summary>
        /// Configure the build to use I/O tracking for nodes.
        /// </summary>
        /// <remarks>
        /// Must be a separate non-inlinable method to avoid loading the BuildXL assembly when not opted in.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnableDetouredNodeLauncher()
        {
            // Currently BuildXL only supports x64. Once this feature moves out of the experimental phase, this will need to be addressed.
            ErrorUtilities.VerifyThrowInvalidOperation(NativeMethodsShared.ProcessorArchitecture == NativeMethodsShared.ProcessorArchitectures.X64, "ReportFileAccessesX64Only");

            // To properly report file access, we need to disable the in-proc node which won't be detoured.
            _buildParameters!.DisableInProcNode = true;

            // Node reuse must be disabled as future builds will not be able to listen to events raised by detours.
            _buildParameters.EnableNodeReuse = false;

            _componentFactories.ReplaceFactory(BuildComponentType.NodeLauncher, DetouredNodeLauncher.CreateComponent);
        }
#endif

        private static void AttachDebugger()
        {
            if (Debugger.IsAttached)
            {
                return;
            }

            if (!DebugUtils.ShouldDebugCurrentProcess)
            {
                return;
            }

            switch (Environment.GetEnvironmentVariable("MSBuildDebugBuildManagerOnStart"))
            {
#if FEATURE_DEBUG_LAUNCH
                case "1":
                    Debugger.Launch();
                    break;
#endif
                case "2":
                    // Sometimes easier to attach rather than deal with JIT prompt
                    Process currentProcess = Process.GetCurrentProcess();
                    Console.WriteLine($"Waiting for debugger to attach ({currentProcess.MainModule!.FileName} PID {currentProcess.Id}).  Press enter to continue...");
                    Console.ReadLine();
                    break;
            }
        }

        /// <summary>
        /// Cancels all outstanding submissions asynchronously.
        /// </summary>
        public void CancelAllSubmissions()
        {
            CancelAllSubmissions(true);
        }

        private void CancelAllSubmissions(bool async)
        {
            ILoggingService loggingService = ((IBuildComponentHost)this).LoggingService;
            loggingService.LogBuildCanceled();

            var parentThreadCulture = _buildParameters != null
                ? _buildParameters.Culture
                : CultureInfo.CurrentCulture;
            var parentThreadUICulture = _buildParameters != null
                ? _buildParameters.UICulture
                : CultureInfo.CurrentUICulture;

            void Callback(object? state)
            {
                lock (_syncLock)
                {
                    // If the state is Idle - then there is yet or already nothing to cancel
                    // If state is WaitingForBuildToComplete - we might be already waiting gracefully - but CancelAllSubmissions
                    //  is a request for quick abort - so it's fine to resubmit the request
                    if (_buildManagerState == BuildManagerState.Idle)
                    {
                        return;
                    }

                    _overallBuildSuccess = false;

                    foreach (BuildSubmissionBase submission in _buildSubmissions.Values)
                    {
                        if (submission.IsStarted)
                        {
                            BuildResultBase buildResult = submission.CompleteResultsWithException(new BuildAbortedException());
                            if (buildResult is BuildResult result)
                            {
                                _resultsCache!.AddResult(result);
                            }
                        }
                    }

                    ShutdownConnectedNodes(true /* abort */);
                    CheckForActiveNodesAndCleanUpSubmissions();
                }
            }

            ThreadPoolExtensions.QueueThreadPoolWorkItemWithCulture(Callback, parentThreadCulture, parentThreadUICulture);
        }

        /// <summary>
        /// Point in time snapshot of all worker processes leveraged by this BuildManager.
        /// This is meant to be used by VS. External users should not this is only best-effort, point-in-time functionality
        ///  without guarantee of 100% correctness and safety.
        /// </summary>
        /// <returns>Enumeration of <see cref="Process"/> objects that were valid during the time of call to this function.</returns>
        public IEnumerable<Process> GetWorkerProcesses()
            => (_nodeManager?.GetProcesses() ?? []).Concat(_taskHostNodeManager?.GetProcesses() ?? []);

        /// <summary>
        /// Clears out all of the cached information.
        /// </summary>
        public void ResetCaches()
        {
            lock (_syncLock)
            {
                ErrorIfState(BuildManagerState.WaitingForBuildToComplete, "WaitingForEndOfBuild");
                ErrorIfState(BuildManagerState.Building, "BuildInProgress");

                _configCache = ((IBuildComponentHost)this).GetComponent<IConfigCache>(BuildComponentType.ConfigCache);
                _resultsCache = ((IBuildComponentHost)this).GetComponent<IResultsCache>(BuildComponentType.ResultsCache);
                _resultsCache!.ClearResults();

                // This call clears out the directory.
                _configCache!.ClearConfigurations();

                _buildParameters?.ProjectRootElementCache.DiscardImplicitReferences();
            }
        }

        /// <summary>
        /// This methods requests the BuildManager to find a matching ProjectInstance in its cache of previously-built projects.
        /// If none exist, a new instance will be created from the specified project.
        /// </summary>
        /// <param name="project">The Project for which an instance should be retrieved.</param>
        /// <returns>The instance.</returns>
        public ProjectInstance GetProjectInstanceForBuild(Project project)
        {
            lock (_syncLock)
            {
                _configCache = ((IBuildComponentHost)this).GetComponent(BuildComponentType.ConfigCache) as IConfigCache;
                BuildRequestConfiguration configuration = _configCache!.GetMatchingConfiguration(
                    new ConfigurationMetadata(project),
                    (config, loadProject) => CreateConfiguration(project, config),
                    loadProject: true);
                ErrorUtilities.VerifyThrow(configuration.Project != null, "Configuration should have been loaded.");
                return configuration.Project!;
            }
        }

        /// <summary>
        /// Submits a build request to the current build but does not start it immediately.  Allows the user to
        /// perform asynchronous execution or access the submission ID prior to executing the request.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if StartBuild has not been called or if EndBuild has been called.</exception>
        public BuildSubmission PendBuildRequest(BuildRequestData requestData)
            => (BuildSubmission)PendBuildRequest<BuildRequestData, BuildResult>(requestData);

        /// <summary>
        /// Submits a graph build request to the current build but does not start it immediately.  Allows the user to
        /// perform asynchronous execution or access the submission ID prior to executing the request.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if StartBuild has not been called or if EndBuild has been called.</exception>
        public GraphBuildSubmission PendBuildRequest(GraphBuildRequestData requestData)
            => (GraphBuildSubmission)PendBuildRequest<GraphBuildRequestData, GraphBuildResult>(requestData);

        /// <summary>
        /// Submits a build request to the current build but does not start it immediately.  Allows the user to
        /// perform asynchronous execution or access the submission ID prior to executing the request.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if StartBuild has not been called or if EndBuild has been called.</exception>
        private BuildSubmissionBase<TRequestData, TResultData> PendBuildRequest<TRequestData, TResultData>(
            TRequestData requestData)
            where TRequestData : BuildRequestData<TRequestData, TResultData>
            where TResultData : BuildResultBase
        {
            lock (_syncLock)
            {
                ErrorUtilities.VerifyThrowArgumentNull(requestData);
                ErrorIfState(BuildManagerState.WaitingForBuildToComplete, "WaitingForEndOfBuild");
                ErrorIfState(BuildManagerState.Idle, "NoBuildInProgress");
                VerifyStateInternal(BuildManagerState.Building);

                var newSubmission = requestData.CreateSubmission(this, GetNextSubmissionId(), requestData,
                    _buildParameters!.LegacyThreadingSemantics);

                if (_buildTelemetry != null)
                {
                    // Project graph can have multiple entry points, for purposes of identifying event for same build project,
                    // we believe that including only one entry point will provide enough precision.
                    _buildTelemetry.Project ??= requestData.EntryProjectsFullPath.FirstOrDefault();
                    _buildTelemetry.Target ??= string.Join(",", requestData.TargetNames);
                }

                _buildSubmissions.Add(newSubmission.SubmissionId, newSubmission);
                _noActiveSubmissionsEvent!.Reset();
                return newSubmission;
            }
        }

        private TResultData BuildRequest<TRequestData, TResultData>(TRequestData requestData)
            where TRequestData : BuildRequestData<TRequestData, TResultData>
            where TResultData : BuildResultBase
            => PendBuildRequest<TRequestData, TResultData>(requestData).Execute();

        /// <summary>
        /// Convenience method. Submits a build request and blocks until the results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if StartBuild has not been called or if EndBuild has been called.</exception>
        public BuildResult BuildRequest(BuildRequestData requestData)
            => BuildRequest<BuildRequestData, BuildResult>(requestData);

        /// <summary>
        /// Convenience method. Submits a graph build request and blocks until the results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if StartBuild has not been called or if EndBuild has been called.</exception>
        public GraphBuildResult BuildRequest(GraphBuildRequestData requestData)
            => BuildRequest<GraphBuildRequestData, GraphBuildResult>(requestData);

        /// <summary>
        /// Signals that no more build requests are expected (or allowed) and the BuildManager may clean up.
        /// </summary>
        /// <remarks>
        /// This call blocks until all currently pending requests are complete.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if there is no build in progress.</exception>
        public void EndBuild()
        {
            lock (_syncLock)
            {
                ErrorIfState(BuildManagerState.WaitingForBuildToComplete, "WaitingForEndOfBuild");
                ErrorIfState(BuildManagerState.Idle, "NoBuildInProgress");
                VerifyStateInternal(BuildManagerState.Building);

                _buildManagerState = BuildManagerState.WaitingForBuildToComplete;
            }

            var exceptionsThrownInEndBuild = false;

            try
            {
                lock (_syncLock)
                {
                    // If there are any submissions which never started, remove them now.
                    var submissionsToCheck = new List<BuildSubmissionBase>(_buildSubmissions.Values);
                    foreach (BuildSubmissionBase submission in submissionsToCheck)
                    {
                        CheckSubmissionCompletenessAndRemove(submission);
                    }
                }

                _noActiveSubmissionsEvent!.WaitOne();
                ShutdownConnectedNodes(false /* normal termination */);
                _noNodesActiveEvent!.WaitOne();

                // Wait for all of the actions in the work queue to drain.
                // _workQueue.Completion.Wait() could throw here if there was an unhandled exception in the work queue,
                // but the top level exception handler there should catch everything and have forwarded it to the
                // OnThreadException method in this class already.
                _workQueue!.Complete();
                _workQueue.Completion.Wait();

                Task projectCacheDispose = _projectCacheService!.DisposeAsync().AsTask();

                ErrorUtilities.VerifyThrow(_buildSubmissions.Count == 0, "All submissions not yet complete.");
                ErrorUtilities.VerifyThrow(_activeNodes.Count == 0, "All nodes not yet shut down.");

                if (_buildParameters!.UsesOutputCache())
                {
                    SerializeCaches();
                }

                projectCacheDispose.Wait();

#if DEBUG
                if (_projectStartedEvents.Count != 0)
                {
                    bool allMismatchedProjectStartedEventsDueToLoggerErrors = true;

                    foreach (KeyValuePair<int, BuildEventArgs> projectStartedEvent in _projectStartedEvents)
                    {
                        BuildResult result = _resultsCache!.GetResultsForConfiguration(projectStartedEvent.Value.BuildEventContext!.ProjectInstanceId);

                        // It's valid to have a mismatched project started event IFF that particular
                        // project had some sort of unhandled exception.  If there is no result, we
                        // can't tell for sure one way or the other, so err on the side of throwing
                        // the assert, but if there is a result, make sure that it actually has an
                        // exception attached.
                        if (result?.Exception == null)
                        {
                            allMismatchedProjectStartedEventsDueToLoggerErrors = false;
                            break;
                        }
                    }

                    Debug.Assert(allMismatchedProjectStartedEventsDueToLoggerErrors, "There was a mismatched project started event not caused by an exception result");
                }
#endif

                if (_buildParameters.DiscardBuildResults)
                {
                    _resultsCache!.ClearResults();
                }
            }
            catch (Exception e)
            {
                exceptionsThrownInEndBuild = true;

                if (e is AggregateException ae && ae.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
                }

                throw;
            }
            finally
            {
                try
                {
                    ILoggingService? loggingService = ((IBuildComponentHost)this).LoggingService;

                    if (loggingService != null)
                    {
                        // Override the build success if the user specified /warnaserror and any errors were logged outside of a build submission.
                        if (exceptionsThrownInEndBuild ||
                            (_overallBuildSuccess && loggingService.HasBuildSubmissionLoggedErrors(BuildEventContext.InvalidSubmissionId)))
                        {
                            _overallBuildSuccess = false;
                        }

                        loggingService.LogBuildFinished(_overallBuildSuccess);

                        if (_buildTelemetry != null)
                        {
                            _buildTelemetry.FinishedAt = DateTime.UtcNow;
                            _buildTelemetry.Success = _overallBuildSuccess;
                            _buildTelemetry.Version = ProjectCollection.Version;
                            _buildTelemetry.DisplayVersion = ProjectCollection.DisplayVersion;
                            _buildTelemetry.FrameworkName = NativeMethodsShared.FrameworkName;

                            string? host = null;
                            if (BuildEnvironmentState.s_runningInVisualStudio)
                            {
                                host = "VS";
                            }
                            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSBUILD_HOST_NAME")))
                            {
                                host = Environment.GetEnvironmentVariable("MSBUILD_HOST_NAME");
                            }
                            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSCODE_CWD")) || Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
                            {
                                host = "VSCode";
                            }
                            _buildTelemetry.Host = host;

                            _buildTelemetry.BuildCheckEnabled = _buildParameters!.IsBuildCheckEnabled;
                            var sacState = NativeMethodsShared.GetSACState();
                            // The Enforcement would lead to build crash - but let's have the check for completeness sake.
                            _buildTelemetry.SACEnabled = sacState == NativeMethodsShared.SAC_State.Evaluation || sacState == NativeMethodsShared.SAC_State.Enforcement;

                            loggingService.LogTelemetry(buildEventContext: null, _buildTelemetry.EventName, _buildTelemetry.GetProperties());
                            Activity? endOfBuildTelemetry = OpenTelemetryManager.DefaultActivitySource?
                                .StartActivity("Build")?
                                .WithTags(_buildTelemetry)
                                .WithStartTime(_buildTelemetry.InnerStartAt);

                            endOfBuildTelemetry?.Dispose();
                            OpenTelemetryManager.ForceFlush();

                            // Clean telemetry to make it ready for next build submission.
                            _buildTelemetry = null;
                        }
                    }

                    ShutdownLoggingService(loggingService);
                }
                finally
                {
                    if (_buildParameters!.LegacyThreadingSemantics)
                    {
                        _legacyThreadingData.MainThreadSubmissionId = -1;
                    }

                    Reset();
                    _buildManagerState = BuildManagerState.Idle;

                    MSBuildEventSource.Log.BuildStop();

                    _threadException?.Throw();

                    if (BuildParameters.DumpOpportunisticInternStats)
                    {
                        Console.WriteLine(Strings.CreateDiagnosticReport());
                    }
                }
            }

            void SerializeCaches()
            {
                string errorMessage = CacheSerialization.SerializeCaches(
                    _configCache,
                    _resultsCache,
                    _buildParameters.OutputResultsCacheFile,
                    _buildParameters.ProjectIsolationMode);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    LogErrorAndShutdown(errorMessage);
                }
            }
        }

        /// <summary>
        /// Convenience method.  Submits a lone build request and blocks until results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
        private TResultData Build<TRequestData, TResultData>(BuildParameters parameters, TRequestData requestData)
            where TRequestData : BuildRequestData<TRequestData, TResultData>
            where TResultData : BuildResultBase
        {
            TResultData result;
            BeginBuild(parameters);
            try
            {
                result = BuildRequest<TRequestData, TResultData>(requestData);
                if (result.Exception == null && _threadException != null)
                {
                    result.Exception = _threadException.SourceException;
                    _threadException = null;
                }
            }
            finally
            {
                EndBuild();
            }

            return result;
        }

        /// <summary>
        /// Convenience method.  Submits a lone build request and blocks until results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
        public BuildResult Build(BuildParameters parameters, BuildRequestData requestData)
            => Build<BuildRequestData, BuildResult>(parameters, requestData);

        /// <summary>
        /// Convenience method.  Submits a lone graph build request and blocks until results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
        public GraphBuildResult Build(BuildParameters parameters, GraphBuildRequestData requestData)
            => Build<GraphBuildRequestData, GraphBuildResult>(parameters, requestData);

        /// <summary>
        /// Shuts down all idle MSBuild nodes on the machine
        /// </summary>
        public void ShutdownAllNodes()
        {
            MSBuildClient.ShutdownServer(CancellationToken.None);

            _nodeManager ??= (INodeManager)((IBuildComponentHost)this).GetComponent(BuildComponentType.NodeManager);
            _nodeManager.ShutdownAllNodes();
        }

        /// <summary>
        /// Dispose of the build manager.
        /// </summary>
        public void Dispose()
        {
            Dispose(true /* disposing */);
            GC.SuppressFinalize(this);
        }

        #region INodePacketHandler Members

        /// <summary>
        /// This method is invoked by the NodePacketRouter when a packet is received and is intended for
        /// this recipient.
        /// </summary>
        /// <param name="node">The node from which the packet was received.</param>
        /// <param name="packet">The packet.</param>
        void INodePacketHandler.PacketReceived(int node, INodePacket packet)
        {
            _workQueue!.Post(() => ProcessPacket(node, packet));
        }

        #endregion

        #region IBuildComponentHost Members

        /// <summary>
        /// Registers a factory which will be used to create the necessary components of the build
        /// system.
        /// </summary>
        /// <param name="componentType">The type which is created by this factory.</param>
        /// <param name="factory">The factory to be registered.</param>
        /// <remarks>
        /// It is not necessary to register any factories.  If no factory is registered for a specific kind
        /// of object, the system will use the default factory.
        /// </remarks>
        void IBuildComponentHost.RegisterFactory(BuildComponentType componentType, BuildComponentFactoryDelegate factory)
        {
            _componentFactories.ReplaceFactory(componentType, factory);
        }

        /// <summary>
        /// Gets an instance of the specified component type from the host.
        /// </summary>
        /// <param name="type">The component type to be retrieved</param>
        /// <returns>The component</returns>
        IBuildComponent IBuildComponentHost.GetComponent(BuildComponentType type)
        {
            return _componentFactories.GetComponent(type);
        }

        TComponent IBuildComponentHost.GetComponent<TComponent>(BuildComponentType type)
        {
            return _componentFactories.GetComponent<TComponent>(type);
        }

        #endregion

        /// <summary>
        /// This method adds the request in the specified submission to the set of requests being handled by the scheduler.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Standard ExpectedException pattern used")]
        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Complex class might need refactoring to separate scheduling elements from submission elements.")]
        private void ExecuteSubmission(BuildSubmission submission, bool allowMainThreadBuild)
        {
            ErrorUtilities.VerifyThrowArgumentNull(submission);
            ErrorUtilities.VerifyThrow(!submission.IsCompleted, "Submission already complete.");

            BuildRequestConfiguration? resolvedConfiguration = null;
            bool shuttingDown = false;

            try
            {
                lock (_syncLock)
                {
                    submission.IsStarted = true;

                    ProjectInstance? projectInstance = submission.BuildRequestData.ProjectInstance;
                    if (projectInstance != null)
                    {
                        if (_acquiredProjectRootElementCacheFromProjectInstance)
                        {
                            ErrorUtilities.VerifyThrowArgument(
                                _buildParameters!.ProjectRootElementCache == projectInstance.ProjectRootElementCache,
                                "OM_BuildSubmissionsMultipleProjectCollections");
                        }
                        else
                        {
                            _buildParameters!.ProjectRootElementCache = projectInstance.ProjectRootElementCache;
                            _acquiredProjectRootElementCacheFromProjectInstance = true;
                        }
                    }
                    else if (_buildParameters!.ProjectRootElementCache == null)
                    {
                        // Create our own cache; if we subsequently get a build submission with a project instance attached,
                        // we'll dump our cache and use that one.
                        _buildParameters!.ProjectRootElementCache =
                            new ProjectRootElementCache(false /* do not automatically reload from disk */);
                    }

                    VerifyStateInternal(BuildManagerState.Building);

                    // If we have an unnamed project, assign it a temporary name.
                    if (string.IsNullOrEmpty(submission.BuildRequestData.ProjectFullPath))
                    {
                        ErrorUtilities.VerifyThrow(
                            submission.BuildRequestData.ProjectInstance != null,
                            "Unexpected null path for a submission with no ProjectInstance.");

                        // If we have already named this instance when it was submitted previously during this build, use the same
                        // name so that we get the same configuration (and thus don't cause it to rebuild.)
                        if (!_unnamedProjectInstanceToNames.TryGetValue(submission.BuildRequestData.ProjectInstance!, out var tempName))
                        {
                            tempName = "Unnamed_" + _nextUnnamedProjectId++;
                            _unnamedProjectInstanceToNames[submission.BuildRequestData.ProjectInstance!] = tempName;
                        }

                        submission.BuildRequestData.ProjectFullPath = Path.Combine(
                            submission.BuildRequestData.ProjectInstance!.GetProperty(ReservedPropertyNames.projectDirectory)!.EvaluatedValue,
                            tempName);
                    }

                    // Create/Retrieve a configuration for each request
                    var buildRequestConfiguration = new BuildRequestConfiguration(submission.BuildRequestData, _buildParameters.DefaultToolsVersion);
                    var matchingConfiguration = _configCache!.GetMatchingConfiguration(buildRequestConfiguration);
                    resolvedConfiguration = ResolveConfiguration(
                        buildRequestConfiguration,
                        matchingConfiguration,
                        submission.BuildRequestData.Flags.HasFlag(BuildRequestDataFlags.ReplaceExistingProjectInstance));

                    resolvedConfiguration.ExplicitlyLoaded = true;

                    // assign shutting down to local variable to avoid race condition: "setting _shuttingDown after this point during this method execution"
                    shuttingDown = _shuttingDown;
                    if (!shuttingDown)
                    {
                        if (!_hasProjectCacheServiceInitializedVsScenario
                            && BuildEnvironmentHelper.Instance.RunningInVisualStudio
                            && !ProjectCacheDescriptors.IsEmpty)
                        {
                            // Only initialize once as it should be the same for all projects.
                            _hasProjectCacheServiceInitializedVsScenario = true;

                            _projectCacheService!.InitializePluginsForVsScenario(
                                ProjectCacheDescriptors.Values,
                                resolvedConfiguration,
                                submission.BuildRequestData.TargetNames,
                                _executionCancellationTokenSource!.Token);
                        }

                        if (_projectCacheService!.ShouldUseCache(resolvedConfiguration))
                        {
                            IssueCacheRequestForBuildSubmission(new CacheRequest(submission, resolvedConfiguration));
                        }
                        else
                        {
                            AddBuildRequestToSubmission(submission, resolvedConfiguration.ConfigurationId);
                            IssueBuildRequestForBuildSubmission(submission, resolvedConfiguration, allowMainThreadBuild);
                        }
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                if (resolvedConfiguration is not null)
                {
                    CompleteSubmissionWithException(submission, resolvedConfiguration, ex);
                }
                else
                {
                    HandleSubmissionException(submission, ex);
                    throw;
                }
            }

            // We are shutting down so submission has to be completed with BuildAbortedException
            Debug.Assert(!Monitor.IsEntered(_syncLock));
            if (shuttingDown)
            {
                ErrorUtilities.VerifyThrow(resolvedConfiguration is not null, "Cannot call project cache without having BuildRequestConfiguration");
                // We were already canceled!
                CompleteSubmissionWithException(submission, resolvedConfiguration!, new BuildAbortedException());
            }
        }

        // Cache requests on configuration N do not block future build submissions depending on configuration N.
        // It is assumed that the higher level build orchestrator (static graph scheduler, VS, quickbuild) submits a
        // project build request only when its references have finished building.
        private void IssueCacheRequestForBuildSubmission(CacheRequest cacheRequest)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            _workQueue!.Post(() =>
            {
                try
                {
                    _projectCacheService!.PostCacheRequest(cacheRequest, _executionCancellationTokenSource!.Token);
                }
                catch (Exception e)
                {
                    CompleteSubmissionWithException(cacheRequest.Submission, cacheRequest.Configuration, e);
                }
            });
        }

        internal void ExecuteSubmission<TRequestData, TResultData>(
            BuildSubmissionBase<TRequestData, TResultData> submission, bool allowMainThreadBuild)
            where TRequestData : BuildRequestDataBase
            where TResultData : BuildResultBase
        {
            // For the current submission we only know the SubmissionId and that it happened on scheduler node - all other BuildEventContext dimensions are unknown now.
            BuildEventContext buildEventContext = new BuildEventContext(
                submission.SubmissionId,
                nodeId: 1,
                BuildEventContext.InvalidProjectInstanceId,
                BuildEventContext.InvalidProjectContextId,
                BuildEventContext.InvalidTargetId,
                BuildEventContext.InvalidTaskId);

            BuildSubmissionStartedEventArgs submissionStartedEvent = new(
                submission.BuildRequestDataBase.GlobalPropertiesLookup,
                submission.BuildRequestDataBase.EntryProjectsFullPath,
                submission.BuildRequestDataBase.TargetNames,
                submission.BuildRequestDataBase.Flags,
                submission.SubmissionId);
            submissionStartedEvent.BuildEventContext = buildEventContext;

            ((IBuildComponentHost)this).LoggingService.LogBuildEvent(submissionStartedEvent);

            if (submission is BuildSubmission buildSubmission)
            {
                ExecuteSubmission(buildSubmission, allowMainThreadBuild);
            }
            else if (submission is GraphBuildSubmission graphBuildSubmission)
            {
                ExecuteSubmission(graphBuildSubmission);
            }
        }

        /// <summary>
        /// This method adds the graph build request in the specified submission to the set of requests being handled by the scheduler.
        /// </summary>
        private void ExecuteSubmission(GraphBuildSubmission submission)
        {
            VerifyStateInternal(BuildManagerState.Building);

            try
            {
                lock (_syncLock)
                {
                    submission.IsStarted = true;

                    if (_shuttingDown)
                    {
                        // We were already canceled!
                        var result = new GraphBuildResult(submission.SubmissionId, new BuildAbortedException());
                        submission.CompleteResults(result);
                        CheckSubmissionCompletenessAndRemove(submission);
                        return;
                    }

                    // Do the scheduling in a separate thread to unblock the calling thread
                    Task.Factory.StartNew(
                        () =>
                        {
                            try
                            {
                                ExecuteGraphBuildScheduler(submission);
                            }
                            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                            {
                                HandleSubmissionException(submission, ex);
                            }
                        },
                        _executionCancellationTokenSource!.Token,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                }
            }
            // The handling of submission exception needs to be done outside of the lock
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                HandleSubmissionException(submission, ex);
                throw;
            }
        }

        /// <summary>
        /// Creates the traversal and metaproject instances necessary to represent the solution and populates new configurations with them.
        /// </summary>
        private void LoadSolutionIntoConfiguration(BuildRequestConfiguration config, BuildRequest request)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            if (config.IsLoaded)
            {
                // We've already processed it, nothing to do.
                return;
            }

            ErrorUtilities.VerifyThrow(FileUtilities.IsSolutionFilename(config.ProjectFullPath), "{0} is not a solution", config.ProjectFullPath);

            var buildEventContext = request.BuildEventContext;
            if (buildEventContext == BuildEventContext.Invalid)
            {
                buildEventContext = new BuildEventContext(request.SubmissionId, 0, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
            }

            var instances = ProjectInstance.LoadSolutionForBuild(
                config.ProjectFullPath,
                config.GlobalProperties,
                config.ExplicitToolsVersionSpecified ? config.ToolsVersion : null,
                _buildParameters,
                ((IBuildComponentHost)this).LoggingService,
                buildEventContext,
                false /* loaded by solution parser*/,
                config.RequestedTargets,
                SdkResolverService,
                request.SubmissionId);

            // The first instance is the traversal project, which goes into this configuration
            config.Project = instances[0];

            // The remaining instances are the metaprojects which describe the dependencies for each project as well as how to invoke the project itself.
            for (int i = 1; i < instances.Length; i++)
            {
                // Create new configurations for each of these if they don't already exist.  That could happen if there are multiple
                // solutions in this build which refer to the same project, in which case we want them to refer to the same
                // metaproject as well.
                var newConfig = new BuildRequestConfiguration(
                    GetNewConfigurationId(),
                    instances[i])
                { ExplicitlyLoaded = config.ExplicitlyLoaded };
                if (_configCache!.GetMatchingConfiguration(newConfig) == null)
                {
                    _configCache.AddConfiguration(newConfig);
                }
            }
        }

        /// <summary>
        /// Gets the next build id.
        /// </summary>
        private static int GetNextBuildId()
        {
            return Interlocked.Increment(ref s_nextBuildId);
        }

        /// <summary>
        /// Creates and optionally populates a new configuration.
        /// </summary>
        private BuildRequestConfiguration CreateConfiguration(Project project, BuildRequestConfiguration? existingConfiguration)
        {
            ProjectInstance newInstance = project.CreateProjectInstance();

            if (existingConfiguration == null)
            {
                existingConfiguration = new BuildRequestConfiguration(GetNewConfigurationId(), new BuildRequestData(newInstance, []), null /* use the instance's tools version */);
            }
            else
            {
                existingConfiguration.Project = newInstance;
            }

            return existingConfiguration;
        }

        /// <summary>
        /// Processes the next action in the work queue.
        /// </summary>
        /// <param name="action">The action to be processed.</param>
        private void ProcessWorkQueue(Action action)
        {
            try
            {
                var oldCulture = CultureInfo.CurrentCulture;
                var oldUICulture = CultureInfo.CurrentUICulture;

                try
                {
                    if (!Equals(CultureInfo.CurrentCulture, _buildParameters!.Culture))
                    {
                        CultureInfo.CurrentCulture = _buildParameters.Culture;
                    }

                    if (!Equals(CultureInfo.CurrentUICulture, _buildParameters.UICulture))
                    {
                        CultureInfo.CurrentUICulture = _buildParameters.UICulture;
                    }

                    action();
                }
                catch (Exception ex)
                {
                    // These need to go to the main thread exception handler.  We can't rethrow here because that will just silently stop the
                    // action block.  Instead, send them over to the main handler for the BuildManager.
                    OnThreadException(ex);
                }
                finally
                {
                    // Set the culture back to the original one so that if something else reuses this thread then it will not have a culture which it was not expecting.
                    if (!Equals(CultureInfo.CurrentCulture, oldCulture))
                    {
                        CultureInfo.CurrentCulture = oldCulture;
                    }

                    if (!Equals(CultureInfo.CurrentUICulture, oldUICulture))
                    {
                        CultureInfo.CurrentUICulture = oldUICulture;
                    }
                }
            }
            catch (Exception e)
            {
                // On the off chance we get an exception from our exception handler (oh, the irony!), we want to know about it (and still not kill this block
                // which could lead to a somewhat mysterious hang.)
                ExceptionHandling.DumpExceptionToFile(e);
            }
        }

        /// <summary>
        /// Processes a packet
        /// </summary>
        private void ProcessPacket(int node, INodePacket packet)
        {
            lock (_syncLock)
            {
                if (_shuttingDown && packet.Type != NodePacketType.NodeShutdown)
                {
                    // Console.WriteLine("Discarding packet {0} from node {1} because we are shutting down.", packet.Type, node);
                    return;
                }

                switch (packet.Type)
                {
                    case NodePacketType.BuildRequestBlocker:
                        BuildRequestBlocker blocker = ExpectPacketType<BuildRequestBlocker>(packet, NodePacketType.BuildRequestBlocker);
                        HandleNewRequest(node, blocker);
                        break;

                    case NodePacketType.BuildRequestConfiguration:
                        BuildRequestConfiguration requestConfiguration = ExpectPacketType<BuildRequestConfiguration>(packet, NodePacketType.BuildRequestConfiguration);
                        HandleConfigurationRequest(node, requestConfiguration);
                        break;

                    case NodePacketType.BuildResult:
                        BuildResult result = ExpectPacketType<BuildResult>(packet, NodePacketType.BuildResult);
                        HandleResult(node, result);
                        break;

                    case NodePacketType.ResourceRequest:
                        ResourceRequest request = ExpectPacketType<ResourceRequest>(packet, NodePacketType.ResourceRequest);
                        HandleResourceRequest(node, request);
                        break;

                    case NodePacketType.NodeShutdown:
                        // Remove the node from the list of active nodes.  When they are all done, we have shut down fully
                        NodeShutdown shutdownPacket = ExpectPacketType<NodeShutdown>(packet, NodePacketType.NodeShutdown);
                        HandleNodeShutdown(node, shutdownPacket);
                        break;

                    case NodePacketType.FileAccessReport:
                        FileAccessReport fileAccessReport = ExpectPacketType<FileAccessReport>(packet, NodePacketType.FileAccessReport);
                        HandleFileAccessReport(node, fileAccessReport);
                        break;

                    case NodePacketType.ProcessReport:
                        ProcessReport processReport = ExpectPacketType<ProcessReport>(packet, NodePacketType.ProcessReport);
                        HandleProcessReport(node, processReport);
                        break;

                    default:
                        ErrorUtilities.ThrowInternalError("Unexpected packet received by BuildManager: {0}", packet.Type);
                        break;
                }
            }
        }

        /// <remarks>
        /// To avoid deadlock possibility, this method MUST NOT be called inside of 'lock (_syncLock)'
        /// </remarks>
        private void CompleteSubmissionWithException(BuildSubmission submission, BuildRequestConfiguration configuration, Exception exception)
        {
            Debug.Assert(!Monitor.IsEntered(_syncLock));

            lock (_syncLock)
            {
                if (submission.BuildRequest is null)
                {
                    AddBuildRequestToSubmission(submission, configuration.ConfigurationId);
                }
            }

            HandleSubmissionException(submission, exception);
        }

        /// <summary>
        /// Deals with exceptions that may be thrown when handling a submission.
        /// </summary>
        /// <remarks>
        /// To avoid deadlock possibility, this method MUST NOT be called inside of 'lock (_syncLock)'
        /// </remarks>
        private void HandleSubmissionException(BuildSubmissionBase submission, Exception ex)
        {
            Debug.Assert(!Monitor.IsEntered(_syncLock));

            if (ex is AggregateException ae)
            {
                // If there's exactly 1, just flatten it
                if (ae.InnerExceptions.Count == 1)
                {
                    ex = ae.InnerExceptions[0];
                }
                else
                {
                    // Log each InvalidProjectFileException encountered
                    foreach (Exception innerException in ae.InnerExceptions)
                    {
                        if (innerException is InvalidProjectFileException innerProjectException)
                        {
                            LogInvalidProjectFileError(innerProjectException);
                        }
                    }
                }
            }

            if (ex is InvalidProjectFileException projectException)
            {
                LogInvalidProjectFileError(projectException);
            }

            if (ex is CircularDependencyException)
            {
                LogInvalidProjectFileError(new InvalidProjectFileException(ex.Message, ex));
            }

            bool submissionNeedsCompletion;
            lock (_syncLock)
            {
                // BuildRequest may be null if the submission fails early on.
                submissionNeedsCompletion = submission.IsStarted;
                if (submissionNeedsCompletion)
                {
                    submission.CompleteResultsWithException(ex);
                }
            }

            if (submissionNeedsCompletion)
            {
                WaitForAllLoggingServiceEventsToBeProcessed();
            }

            lock (_syncLock)
            {
                if (submissionNeedsCompletion)
                {
                    submission.CompleteLogging();
                }

                _overallBuildSuccess = false;
                CheckSubmissionCompletenessAndRemove(submission);
            }

            void LogInvalidProjectFileError(InvalidProjectFileException projectException)
            {
                if (!projectException.HasBeenLogged)
                {
                    BuildEventContext buildEventContext = new BuildEventContext(submission.SubmissionId, 1, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                    ((IBuildComponentHost)this).LoggingService.LogInvalidProjectFileError(buildEventContext, projectException);
                    projectException.HasBeenLogged = true;
                }
            }
        }

        /// <summary>
        /// Waits to drain all events of logging service.
        /// This method shall be used carefully because during draining, LoggingService will block all incoming events.
        /// </summary>
        /// <remarks>
        /// To avoid deadlock possibility, this method MUST NOT be called inside of 'lock (_syncLock)'
        /// </remarks>
        private void WaitForAllLoggingServiceEventsToBeProcessed()
        {
            // this has to be called out of the lock (_syncLock)
            // because processing events can callback to 'this' instance and cause deadlock
            Debug.Assert(!Monitor.IsEntered(_syncLock));
            ((LoggingService)((IBuildComponentHost)this).LoggingService).WaitForLoggingToProcessEvents();
        }

        private static void AddBuildRequestToSubmission(BuildSubmission submission, int configurationId, int projectContextId = BuildEventContext.InvalidProjectContextId)
        {
            submission.BuildRequest = new BuildRequest(
                submission.SubmissionId,
                BackEnd.BuildRequest.InvalidNodeRequestId,
                configurationId,
                submission.BuildRequestData.TargetNames,
                submission.BuildRequestData.HostServices,
                parentBuildEventContext: BuildEventContext.Invalid,
                parentRequest: null,
                submission.BuildRequestData.Flags,
                submission.BuildRequestData.RequestedProjectState,
                projectContextId: projectContextId);
        }

        private static void AddProxyBuildRequestToSubmission(
            BuildSubmission submission,
            int configurationId,
            ProxyTargets proxyTargets,
            int projectContextId)
        {
            submission.BuildRequest = new BuildRequest(
                submission.SubmissionId,
                BackEnd.BuildRequest.InvalidNodeRequestId,
                configurationId,
                proxyTargets,
                submission.BuildRequestData.HostServices,
                submission.BuildRequestData.Flags,
                submission.BuildRequestData.RequestedProjectState,
                projectContextId);
        }

        /// <summary>
        /// The submission is a top level build request entering the BuildManager.
        /// Sends the request to the scheduler with optional legacy threading semantics behavior.
        /// </summary>
        private void IssueBuildRequestForBuildSubmission(BuildSubmission submission, BuildRequestConfiguration configuration, bool allowMainThreadBuild = false)
        {
            _workQueue!.Post(
                () =>
                {
                    try
                    {
                        IssueBuildSubmissionToSchedulerImpl(submission, allowMainThreadBuild);
                    }
                    catch (BuildAbortedException bae)
                    {
                        CompleteSubmissionWithException(submission, configuration, bae);
                    }
                    catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                    {
                        HandleSubmissionException(submission, ex);
                    }
                });

            void IssueBuildSubmissionToSchedulerImpl(BuildSubmission submission, bool allowMainThreadBuild)
            {
                var resetMainThreadOnFailure = false;
                try
                {
                    lock (_syncLock)
                    {
                        if (_shuttingDown)
                        {
                            throw new BuildAbortedException();
                        }

                        if (allowMainThreadBuild && _buildParameters!.LegacyThreadingSemantics)
                        {
                            if (_legacyThreadingData.MainThreadSubmissionId == -1)
                            {
                                resetMainThreadOnFailure = true;
                                _legacyThreadingData.MainThreadSubmissionId = submission.SubmissionId;
                            }
                        }

                        BuildRequestBlocker blocker = new BuildRequestBlocker(-1, [], [submission.BuildRequest]);

                        HandleNewRequest(Scheduler.VirtualNode, blocker);
                    }
                }
                catch (Exception ex) when (IsInvalidProjectOrIORelatedException(ex))
                {
                    if (ex is InvalidProjectFileException projectException)
                    {
                        if (!projectException.HasBeenLogged)
                        {
                            BuildEventContext projectBuildEventContext = new BuildEventContext(submission.SubmissionId, 1, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                            ((IBuildComponentHost)this).LoggingService.LogInvalidProjectFileError(projectBuildEventContext, projectException);
                            projectException.HasBeenLogged = true;
                        }
                    }

                    lock (_syncLock)
                    {

                        if (resetMainThreadOnFailure)
                        {
                            _legacyThreadingData.MainThreadSubmissionId = -1;
                        }

                        if (ex is not InvalidProjectFileException)
                        {
                            var buildEventContext = new BuildEventContext(submission.SubmissionId, 1, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                            ((IBuildComponentHost)this).LoggingService.LogFatalBuildError(buildEventContext, ex, new BuildEventFileInfo(submission.BuildRequestData.ProjectFullPath));
                        }
                    }

                    WaitForAllLoggingServiceEventsToBeProcessed();

                    lock (_syncLock)
                    {
                        submission.CompleteLogging();
                        ReportResultsToSubmission<BuildRequestData, BuildResult>(new BuildResult(submission.BuildRequest!, ex));
                        _overallBuildSuccess = false;
                    }
                }
            }
        }

        private bool IsInvalidProjectOrIORelatedException(Exception e)
        {
            return !ExceptionHandling.IsCriticalException(e) && !ExceptionHandling.NotExpectedException(e) && e is not BuildAbortedException;
        }

        private void ExecuteGraphBuildScheduler(GraphBuildSubmission submission)
        {
            if (_shuttingDown)
            {
                throw new BuildAbortedException();
            }

            var projectGraph = submission.BuildRequestData.ProjectGraph;
            if (projectGraph == null)
            {
                projectGraph = new ProjectGraph(
                    submission.BuildRequestData.ProjectGraphEntryPoints,
                    ProjectCollection.GlobalProjectCollection,
                    (path, properties, collection) =>
                    {
                        ProjectLoadSettings projectLoadSettings = _buildParameters!.ProjectLoadSettings;
                        if (submission.BuildRequestData.Flags.HasFlag(BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports))
                        {
                            projectLoadSettings |= ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.IgnoreEmptyImports;
                        }

                        if (submission.BuildRequestData.Flags.HasFlag(BuildRequestDataFlags.FailOnUnresolvedSdk))
                        {
                            projectLoadSettings |= ProjectLoadSettings.FailOnUnresolvedSdk;
                        }

                        return new ProjectInstance(
                            path,
                            properties,
                            null,
                            _buildParameters,
                            ((IBuildComponentHost)this).LoggingService,
                            new BuildEventContext(
                                submission.SubmissionId,
                                _buildParameters.NodeId,
                                BuildEventContext.InvalidEvaluationId,
                                BuildEventContext.InvalidProjectInstanceId,
                                BuildEventContext.InvalidProjectContextId,
                                BuildEventContext.InvalidTargetId,
                                BuildEventContext.InvalidTaskId),
                            SdkResolverService,
                            submission.SubmissionId,
                            projectLoadSettings);
                    });
            }

            LogMessage(
                ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                    "StaticGraphConstructionMetrics",
                    Math.Round(projectGraph.ConstructionMetrics.ConstructionTime.TotalSeconds, 3),
                    projectGraph.ConstructionMetrics.NodeCount,
                    projectGraph.ConstructionMetrics.EdgeCount));

            Dictionary<ProjectGraphNode, BuildResult>? resultsPerNode = null;

            if (submission.BuildRequestData.GraphBuildOptions.Build)
            {
                _projectCacheService!.InitializePluginsForGraph(projectGraph, submission.BuildRequestData.TargetNames, _executionCancellationTokenSource!.Token);

                IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetsPerNode = projectGraph.GetTargetLists(submission.BuildRequestData.TargetNames);

                DumpGraph(projectGraph, targetsPerNode);

                // Non-graph builds verify this in RequestBuilder, but for graph builds we need to disambiguate
                // between entry nodes and other nodes in the graph since only entry nodes should error. Just do
                // the verification explicitly before the build even starts.
                foreach (ProjectGraphNode entryPointNode in projectGraph.EntryPointNodes)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(entryPointNode.ProjectInstance.Targets.Count > 0, entryPointNode.ProjectInstance.ProjectFileLocation, "NoTargetSpecified");
                }

                resultsPerNode = BuildGraph(projectGraph, targetsPerNode, submission.BuildRequestData);
            }
            else
            {
                DumpGraph(projectGraph);
            }

            ErrorUtilities.VerifyThrow(
                submission.BuildResult?.Exception == null,
                "Exceptions only get set when the graph submission gets completed with an exception in OnThreadException. That should not happen during graph builds.");

            // The overall submission is complete, so report it as complete
            ReportResultsToSubmission<GraphBuildRequestData, GraphBuildResult>(
                new GraphBuildResult(
                    submission.SubmissionId,
                    new ReadOnlyDictionary<ProjectGraphNode, BuildResult>(resultsPerNode ?? new Dictionary<ProjectGraphNode, BuildResult>())));

            static void DumpGraph(ProjectGraph graph, IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>>? targetList = null)
            {
                if (Traits.Instance.DebugEngine is false)
                {
                    return;
                }

                var logPath = DebugUtils.FindNextAvailableDebugFilePath($"{DebugUtils.ProcessInfoString}_ProjectGraph.dot");

                File.WriteAllText(logPath, graph.ToDot(targetList));
            }
        }

        private Dictionary<ProjectGraphNode, BuildResult> BuildGraph(
            ProjectGraph projectGraph,
            IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetsPerNode,
            GraphBuildRequestData graphBuildRequestData)
        {
            // The handle is used within captured async scope. If error occurs during the build
            //  and we return from the function before async call signals - it causes unhandled ObjectDisposedException
            //  upon attempt to signal the handle (and hence unfinished logs).
#pragma warning disable CA2000
            var waitHandle = new AutoResetEvent(true);
#pragma warning restore CA2000
            var graphBuildStateLock = new object();

            var blockedNodes = new HashSet<ProjectGraphNode>(projectGraph.ProjectNodes);
            var finishedNodes = new HashSet<ProjectGraphNode>(projectGraph.ProjectNodes.Count);
            var buildingNodes = new Dictionary<BuildSubmissionBase, ProjectGraphNode>();
            var resultsPerNode = new Dictionary<ProjectGraphNode, BuildResult>(projectGraph.ProjectNodes.Count);
            ExceptionDispatchInfo? submissionException = null;

            while (blockedNodes.Count > 0 || buildingNodes.Count > 0)
            {
                waitHandle.WaitOne();

                // When a cache plugin is present, ExecuteSubmission(BuildSubmission) executes on a separate thread whose exceptions do not get observed.
                // Observe them here to keep the same exception flow with the case when there's no plugins and ExecuteSubmission(BuildSubmission) does not run on a separate thread.
                if (submissionException != null)
                {
                    submissionException.Throw();
                }

                lock (graphBuildStateLock)
                {
                    var unblockedNodes = blockedNodes
                        .Where(node => node.ProjectReferences.All(projectReference => finishedNodes.Contains(projectReference)))
                        .ToList();
                    foreach (var node in unblockedNodes)
                    {
                        var targetList = targetsPerNode[node];
                        if (targetList.Count == 0)
                        {
                            // An empty target list here means "no targets" instead of "default targets", so don't even build it.
                            finishedNodes.Add(node);
                            blockedNodes.Remove(node);

                            waitHandle.Set();

                            continue;
                        }

                        var request = new BuildRequestData(
                            node.ProjectInstance,
                            targetList.ToArray(),
                            graphBuildRequestData.HostServices,
                            graphBuildRequestData.Flags);

                        // TODO Tack onto the existing submission instead of pending a whole new submission for every node
                        // Among other things, this makes BuildParameters.DetailedSummary produce a summary for each node, which is not desirable.
                        // We basically want to submit all requests to the scheduler all at once and describe dependencies by requests being blocked by other requests.
                        // However today the scheduler only keeps track of MSBuild nodes being blocked by other MSBuild nodes, and MSBuild nodes haven't been assigned to the graph nodes yet.
                        var innerBuildSubmission = PendBuildRequest(request);
                        buildingNodes.Add(innerBuildSubmission, node);
                        blockedNodes.Remove(node);
                        innerBuildSubmission.ExecuteAsync(finishedBuildSubmission =>
                        {
                            lock (graphBuildStateLock)
                            {
                                if (submissionException == null && finishedBuildSubmission.BuildResult?.Exception != null)
                                {
                                    // Preserve the original stack.
                                    submissionException = ExceptionDispatchInfo.Capture(finishedBuildSubmission.BuildResult.Exception);
                                }

                                ProjectGraphNode finishedNode = buildingNodes[finishedBuildSubmission];

                                finishedNodes.Add(finishedNode);
                                buildingNodes.Remove(finishedBuildSubmission);

                                resultsPerNode.Add(finishedNode, finishedBuildSubmission.BuildResult!);
                            }

                            waitHandle.Set();
                        }, null);
                    }
                }
            }

            return resultsPerNode;
        }

        /// <summary>
        /// Asks the nodeManager to tell the currently connected nodes to shut down and sets a flag preventing all non-shutdown-related packets from
        /// being processed.
        /// </summary>
        private void ShutdownConnectedNodes(bool abort)
        {
            lock (_syncLock)
            {
                _shuttingDown = true;
                _executionCancellationTokenSource?.Cancel();

                // If we are aborting, we will NOT reuse the nodes because their state may be compromised by attempts to shut down while the build is in-progress.
                _nodeManager?.ShutdownConnectedNodes(!abort && _buildParameters!.EnableNodeReuse);

                // if we are aborting, the task host will hear about it in time through the task building infrastructure;
                // so only shut down the task host nodes if we're shutting down tidily (in which case, it is assumed that all
                // tasks are finished building and thus that there's no risk of a race between the two shutdown pathways).
                if (!abort)
                {
                    _taskHostNodeManager?.ShutdownConnectedNodes(_buildParameters!.EnableNodeReuse);
                }
            }
        }

        /// <summary>
        /// Retrieves the next build submission id.
        /// </summary>
        private int GetNextSubmissionId()
        {
            return _nextBuildSubmissionId++;
        }

        /// <summary>
        /// Errors if the BuildManager is in the specified state.
        /// </summary>
        private void ErrorIfState(BuildManagerState disallowedState, string exceptionResouorce)
        {
            if (_buildManagerState == disallowedState)
            {
                ErrorUtilities.ThrowInvalidOperation(exceptionResouorce);
            }
        }

        /// <summary>
        /// Verifies the BuildManager is in the required state, and throws a <see cref="System.InvalidOperationException"/> if it is not.
        /// </summary>
        private void RequireState(BuildManagerState requiredState, string exceptionResouorce)
        {
            ErrorUtilities.VerifyThrowInvalidOperation(_buildManagerState == requiredState, exceptionResouorce);
        }

        /// <summary>
        /// Verifies the BuildManager is in the required state, and throws a <see cref="System.InvalidOperationException"/> if it is not.
        /// </summary>
        private void VerifyStateInternal(BuildManagerState requiredState)
        {
            if (_buildManagerState != requiredState)
            {
                ErrorUtilities.ThrowInternalError("Expected state {0}, actual state {1}", requiredState, _buildManagerState);
            }
        }

        /// <summary>
        /// Method called to reset the state of the system after a build.
        /// </summary>
        private void Reset()
        {
            _nodeManager?.UnregisterPacketHandler(NodePacketType.BuildRequestBlocker);
            _nodeManager?.UnregisterPacketHandler(NodePacketType.BuildRequestConfiguration);
            _nodeManager?.UnregisterPacketHandler(NodePacketType.BuildRequestConfigurationResponse);
            _nodeManager?.UnregisterPacketHandler(NodePacketType.BuildResult);
            _nodeManager?.UnregisterPacketHandler(NodePacketType.NodeShutdown);

            _nodeManager?.ClearPerBuildState();
            _nodeManager = null;

            _shuttingDown = false;
            _executionCancellationTokenSource?.Dispose();
            _executionCancellationTokenSource = null;
            _nodeConfiguration = null;
            _buildSubmissions.Clear();

            _scheduler?.Reset();
            _scheduler = null;
            _workQueue = null;
            _projectCacheService = null;
            _hasProjectCacheServiceInitializedVsScenario = false;
            _acquiredProjectRootElementCacheFromProjectInstance = false;

            _unnamedProjectInstanceToNames.Clear();
            _projectStartedEvents.Clear();
            _nodeIdToKnownConfigurations.Clear();
            _nextUnnamedProjectId = 1;

            if (_configCache != null)
            {
                foreach (BuildRequestConfiguration config in _configCache)
                {
                    config.ActivelyBuildingTargets.Clear();
                }
            }

            if (Environment.GetEnvironmentVariable("MSBUILDCLEARXMLCACHEONBUILDMANAGER") == "1")
            {
                // Optionally clear out the cache. This has the advantage of releasing memory,
                // but the disadvantage of causing the next build to repeat the load and parse.
                // We'll experiment here and ship with the best default.
                _buildParameters?.ProjectRootElementCache.Clear();
            }
        }

        /// <summary>
        /// Returns a new, valid configuration id.
        /// </summary>
        private int GetNewConfigurationId()
        {
            int newId = Interlocked.Increment(ref s_nextBuildRequestConfigurationId);

            if (_scheduler != null)
            {
                // Minimum configuration id is always the lowest valid configuration id available, so increment after returning.
                while (newId <= _scheduler.MinimumAssignableConfigurationId) // Currently this minimum is one
                {
                    newId = Interlocked.Increment(ref s_nextBuildRequestConfigurationId);
                }
            }

            return newId;
        }

        /// <summary>
        /// Finds a matching configuration in the cache and returns it, or stores the configuration passed in.
        /// </summary>
        private BuildRequestConfiguration ResolveConfiguration(BuildRequestConfiguration unresolvedConfiguration, BuildRequestConfiguration? matchingConfigurationFromCache, bool replaceProjectInstance)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            BuildRequestConfiguration resolvedConfiguration = matchingConfigurationFromCache ?? _configCache!.GetMatchingConfiguration(unresolvedConfiguration);
            if (resolvedConfiguration == null)
            {
                resolvedConfiguration = AddNewConfiguration(unresolvedConfiguration);
            }
            else if (unresolvedConfiguration.Project != null && replaceProjectInstance)
            {
                ReplaceExistingProjectInstance(unresolvedConfiguration, resolvedConfiguration);
            }
            else if (unresolvedConfiguration.Project != null && resolvedConfiguration.Project != null && !ReferenceEquals(unresolvedConfiguration.Project, resolvedConfiguration.Project))
            {
                // The user passed in a different instance than the one we already had. Throw away any corresponding results.
                ReplaceExistingProjectInstance(unresolvedConfiguration, resolvedConfiguration);
            }
            else if (unresolvedConfiguration.Project != null && resolvedConfiguration.Project == null)
            {
                // Workaround for https://github.com/dotnet/msbuild/issues/1748
                // If the submission has a project instance but the existing configuration does not, it probably means that the project was
                // built on another node (e.g. the project was encountered as a p2p reference and scheduled to a node).
                // Add a dummy property to force cache invalidation in the scheduler and the nodes.
                // TODO find a better solution than a dummy property
                unresolvedConfiguration.CreateUniqueGlobalProperty();

                resolvedConfiguration = AddNewConfiguration(unresolvedConfiguration);
            }

            return resolvedConfiguration;
        }

        private void ReplaceExistingProjectInstance(BuildRequestConfiguration newConfiguration, BuildRequestConfiguration existingConfiguration)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            existingConfiguration.Project = newConfiguration.Project;
            _resultsCache!.ClearResultsForConfiguration(existingConfiguration.ConfigurationId);
        }

        private BuildRequestConfiguration AddNewConfiguration(BuildRequestConfiguration unresolvedConfiguration)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            var newConfigurationId = _scheduler!.GetConfigurationIdFromPlan(unresolvedConfiguration.ProjectFullPath);

            if (_configCache!.HasConfiguration(newConfigurationId) || (newConfigurationId == BuildRequestConfiguration.InvalidConfigurationId))
            {
                // There is already a configuration like this one or one didn't exist in a plan, so generate a new ID.
                newConfigurationId = GetNewConfigurationId();
            }

            var newConfiguration = unresolvedConfiguration.ShallowCloneWithNewId(newConfigurationId);

            _configCache.AddConfiguration(newConfiguration);

            return newConfiguration;
        }

        internal void PostCacheResult(CacheRequest cacheRequest, CacheResult cacheResult, int projectContextId)
        {
            _workQueue!.Post(() =>
            {
                if (cacheResult.Exception is not null)
                {
                    CompleteSubmissionWithException(cacheRequest.Submission, cacheRequest.Configuration, cacheResult.Exception);
                    return;
                }

                HandleCacheResult();
            });

            void HandleCacheResult()
            {
                lock (_syncLock)
                {
                    try
                    {
                        var submission = cacheRequest.Submission;
                        var configuration = cacheRequest.Configuration;

                        if (cacheResult.ResultType != CacheResultType.CacheHit)
                        {
                            // Issue the real build request.
                            AddBuildRequestToSubmission(submission, configuration.ConfigurationId, projectContextId);
                            IssueBuildRequestForBuildSubmission(submission, configuration, allowMainThreadBuild: false);
                        }
                        else if (cacheResult.ResultType == CacheResultType.CacheHit && cacheResult.ProxyTargets != null)
                        {
                            // Setup submission.BuildRequest with proxy targets. The proxy request is built on the inproc node (to avoid
                            // ProjectInstance serialization). The proxy target results are used as results for the real targets.
                            AddProxyBuildRequestToSubmission(submission, configuration.ConfigurationId, cacheResult.ProxyTargets, projectContextId);
                            IssueBuildRequestForBuildSubmission(submission, configuration, allowMainThreadBuild: false);
                        }
                        else if (cacheResult.ResultType == CacheResultType.CacheHit && cacheResult.BuildResult != null)
                        {
                            // Mark the build submission as complete with the provided results and return.

                            // There must be a build request for the results, so fake one.
                            AddBuildRequestToSubmission(submission, configuration.ConfigurationId, projectContextId);
                            var result = new BuildResult(submission.BuildRequest!);

                            foreach (var cacheResultInner in cacheResult.BuildResult?.ResultsByTarget ?? Enumerable.Empty<KeyValuePair<string, TargetResult>>())
                            {
                                result.AddResultsForTarget(cacheResultInner.Key, cacheResultInner.Value);
                            }

                            _resultsCache!.AddResult(result);
                            submission.CompleteLogging();
                            ReportResultsToSubmission<BuildRequestData, BuildResult>(result);
                        }
                    }
                    catch (Exception e)
                    {
                        CompleteSubmissionWithException(cacheRequest.Submission, cacheRequest.Configuration, e);
                    }
                }
            }
        }

        /// <summary>
        /// Handles a new request coming from a node.
        /// </summary>
        private void HandleNewRequest(int node, BuildRequestBlocker blocker)
        {
            // If we received any solution files, populate their configurations now.
            if (blocker.BuildRequests != null)
            {
                foreach (BuildRequest request in blocker.BuildRequests)
                {
                    BuildRequestConfiguration config = _configCache![request.ConfigurationId];
                    if (FileUtilities.IsSolutionFilename(config.ProjectFullPath))
                    {
                        try
                        {
                            LoadSolutionIntoConfiguration(config, request);
                        }
                        catch (InvalidProjectFileException e)
                        {
                            // Throw the error in the cache.  The Scheduler will pick it up and return the results correctly.
                            _resultsCache!.AddResult(new BuildResult(request, e));
                            if (node == Scheduler.VirtualNode)
                            {
                                throw;
                            }
                        }
                    }
                }
            }

            IEnumerable<ScheduleResponse> response = _scheduler!.ReportRequestBlocked(node, blocker);
            PerformSchedulingActions(response);
        }

        /// <summary>
        /// Handles a resource request coming from a node.
        /// </summary>
        private void HandleResourceRequest(int node, ResourceRequest request)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            if (request.IsResourceAcquire)
            {
                // Resource request requires a response and may be blocking. Our continuation is effectively a callback
                // to be called once at least one core becomes available.
                _scheduler!.RequestCores(request.GlobalRequestId, request.NumCores, request.IsBlocking).ContinueWith((Task<int> task) =>
                {
                    var response = new ResourceResponse(request.GlobalRequestId, task.Result);
                    _nodeManager!.SendData(node, response);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            else
            {
                // Resource release is a one-way call, no response is expected. We release the cores as instructed
                // and kick the scheduler because there may be work waiting for cores to become available.
                IEnumerable<ScheduleResponse> response = _scheduler!.ReleaseCores(request.GlobalRequestId, request.NumCores);
                PerformSchedulingActions(response);
            }
        }

        /// <summary>
        /// Handles a configuration request coming from a node.
        /// </summary>
        private void HandleConfigurationRequest(int node, BuildRequestConfiguration unresolvedConfiguration)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            BuildRequestConfiguration resolvedConfiguration = ResolveConfiguration(unresolvedConfiguration, null, false);

            var response = new BuildRequestConfigurationResponse(unresolvedConfiguration.ConfigurationId, resolvedConfiguration.ConfigurationId, resolvedConfiguration.ResultsNodeId);

            if (!_nodeIdToKnownConfigurations.TryGetValue(node, out HashSet<int>? configurationsOnNode))
            {
                configurationsOnNode = new HashSet<int>();
                _nodeIdToKnownConfigurations[node] = configurationsOnNode;
            }

            configurationsOnNode.Add(resolvedConfiguration.ConfigurationId);

            _nodeManager!.SendData(node, response);
        }

        /// <summary>
        /// Handles a build result coming from a node.
        /// </summary>
        private void HandleResult(int node, BuildResult result)
        {
            // Update cache with the default, initial, and project targets, as needed.
            BuildRequestConfiguration configuration = _configCache![result.ConfigurationId];
            if (result.DefaultTargets != null)
            {
                // If the result has Default, Initial, and project targets, we populate the configuration cache with them if it
                // doesn't already have entries.  This can happen if we created a configuration based on a request from
                // an external node, but hadn't yet received a result since we may not have loaded the Project locally
                // and thus wouldn't know what the default, initial, and project targets were.
                configuration.ProjectDefaultTargets ??= result.DefaultTargets;
                configuration.ProjectInitialTargets ??= result.InitialTargets;
                configuration.ProjectTargets ??= result.ProjectTargets;
            }

            // Only report results to the project cache services if it's the result for a build submission.
            // Note that graph builds create a submission for each node in the graph, so each node in the graph will be
            // handled here. This intentionally mirrors the behavior for cache requests, as it doesn't make sense to
            // report for projects which aren't going to be requested. Ideally, *any* request could be handled, but that
            // would require moving the cache service interactions to the Scheduler.
            if (_buildSubmissions.TryGetValue(result.SubmissionId, out BuildSubmissionBase? buildSubmissionBase) && buildSubmissionBase is BuildSubmission buildSubmission)
            {
                // The result may be associated with the build submission due to it being the submission which
                // caused the build, but not the actual request which was originally used with the build submission.
                // ie. it may be a dependency of the "root-level" project which is associated with this submission, which
                // isn't what we're looking for. Ensure only the actual submission's request is considered.
                if (buildSubmission.BuildRequest != null
                    && buildSubmission.BuildRequest.ConfigurationId == configuration.ConfigurationId
                    && _projectCacheService!.ShouldUseCache(configuration))
                {
                    BuildEventContext buildEventContext = _projectStartedEvents.TryGetValue(result.SubmissionId, out BuildEventArgs? buildEventArgs)
                        ? buildEventArgs.BuildEventContext!
                        : new BuildEventContext(result.SubmissionId, node, configuration.Project?.EvaluationId ?? BuildEventContext.InvalidEvaluationId, configuration.ConfigurationId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                    try
                    {
                        _projectCacheService.HandleBuildResultAsync(configuration, result, buildEventContext, _executionCancellationTokenSource!.Token).Wait();
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
                    {
                        // The build is being cancelled. Swallow any exceptions related specifically to cancellation.
                    }
                    catch (OperationCanceledException)
                    {
                        // The build is being cancelled. Swallow any exceptions related specifically to cancellation.
                    }
                }
            }

            IEnumerable<ScheduleResponse> response = _scheduler!.ReportResult(node, result);
            PerformSchedulingActions(response);
        }

        /// <summary>
        /// Handles the NodeShutdown packet
        /// </summary>
        private void HandleNodeShutdown(int node, NodeShutdown shutdownPacket)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            _shuttingDown = true;
            _executionCancellationTokenSource?.Cancel();
            ErrorUtilities.VerifyThrow(_activeNodes.Contains(node), "Unexpected shutdown from node {0} which shouldn't exist.", node);
            _activeNodes.Remove(node);

            if (shutdownPacket.Reason != NodeShutdownReason.Requested)
            {
                if (shutdownPacket.Reason == NodeShutdownReason.ConnectionFailed)
                {
                    ILoggingService loggingService = ((IBuildComponentHost)this).GetComponent<ILoggingService>(BuildComponentType.LoggingService);
                    foreach (BuildSubmissionBase submission in _buildSubmissions.Values)
                    {
                        BuildEventContext buildEventContext = new BuildEventContext(submission.SubmissionId, BuildEventContext.InvalidNodeId, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                        string exception = ExceptionHandling.ReadAnyExceptionFromFile(_instantiationTimeUtc);
                        loggingService?.LogError(buildEventContext, new BuildEventFileInfo(string.Empty) /* no project file */, "ChildExitedPrematurely", node, ExceptionHandling.DebugDumpPath, exception);
                    }
                }
                else if (shutdownPacket.Reason == NodeShutdownReason.Error && _buildSubmissions.Values.Count == 0)
                {
                    // We have no submissions to attach any exceptions to, lets just log it here.
                    if (shutdownPacket.Exception != null)
                    {
                        ILoggingService loggingService = ((IBuildComponentHost)this).GetComponent<ILoggingService>(BuildComponentType.LoggingService);
                        loggingService?.LogError(BuildEventContext.Invalid, new BuildEventFileInfo(string.Empty) /* no project file */, "ChildExitedPrematurely", node, ExceptionHandling.DebugDumpPath, shutdownPacket.Exception.ToString());
                        OnThreadException(shutdownPacket.Exception);
                    }
                }

                _nodeManager!.ShutdownConnectedNodes(_buildParameters!.EnableNodeReuse);
                _taskHostNodeManager!.ShutdownConnectedNodes(_buildParameters.EnableNodeReuse);

                foreach (BuildSubmissionBase submission in _buildSubmissions.Values)
                {
                    // The submission has not started
                    if (!submission.IsStarted)
                    {
                        continue;
                    }

                    if (submission is BuildSubmission buildSubmission && buildSubmission.BuildRequest != null)
                    {
                        _resultsCache!.AddResult(new BuildResult(buildSubmission.BuildRequest,
                            shutdownPacket.Exception ?? new BuildAbortedException()));
                    }
                }

                _scheduler!.ReportBuildAborted(node);
            }

            CheckForActiveNodesAndCleanUpSubmissions();
        }

        /// <summary>
        /// Report the received <paramref name="fileAccessReport"/> to the file access manager.
        /// </summary>
        /// <param name="nodeId">The id of the node from which the <paramref name="fileAccessReport"/> was received.</param>
        /// <param name="fileAccessReport">The file access report.</param>
        private void HandleFileAccessReport(int nodeId, FileAccessReport fileAccessReport)
        {
#if FEATURE_REPORTFILEACCESSES
            if (_buildParameters!.ReportFileAccesses)
            {
                ((FileAccessManager)((IBuildComponentHost)this).GetComponent(BuildComponentType.FileAccessManager)).ReportFileAccess(fileAccessReport.FileAccessData, nodeId);
            }
#endif
        }

        /// <summary>
        /// Report the received <paramref name="processReport"/> to the file access manager.
        /// </summary>
        /// <param name="nodeId">The id of the node from which the <paramref name="processReport"/> was received.</param>
        /// <param name="processReport">The process data report.</param>
        private void HandleProcessReport(int nodeId, ProcessReport processReport)
        {
#if FEATURE_REPORTFILEACCESSES
            if (_buildParameters!.ReportFileAccesses)
            {
                ((FileAccessManager)((IBuildComponentHost)this).GetComponent(BuildComponentType.FileAccessManager)).ReportProcess(processReport.ProcessData, nodeId);
            }
#endif
        }

        /// <summary>
        /// If there are no more active nodes, cleans up any remaining submissions.
        /// </summary>
        /// <remarks>
        /// Must only be called from within the sync lock.
        /// </remarks>
        private void CheckForActiveNodesAndCleanUpSubmissions()
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            if (_activeNodes.Count == 0)
            {
                var submissions = new List<BuildSubmissionBase>(_buildSubmissions.Values);
                foreach (BuildSubmissionBase submission in submissions)
                {
                    // The submission has not started do not add it to the results cache
                    if (!submission.IsStarted)
                    {
                        continue;
                    }

                    if (!CompleteSubmissionFromCache(submission))
                    {
                        submission.CompleteResultsWithException(new BuildAbortedException());
                    }

                    // If we never received a project started event, consider logging complete anyhow, since the nodes have
                    // shut down.
                    submission.CompleteLogging();

                    CheckSubmissionCompletenessAndRemove(submission);
                }

                _noNodesActiveEvent?.Set();
            }
        }

        private bool CompleteSubmissionFromCache(BuildSubmissionBase submissionBase)
        {
            if (submissionBase is BuildSubmission submission)
            {
                BuildResult? result = submission.BuildRequest == null ? null : _resultsCache?.GetResultsForConfiguration(submission.BuildRequest.ConfigurationId);
                if (result != null)
                {
                    submission.CompleteResults(result);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Carries out the actions specified by the scheduler.
        /// </summary>
        private void PerformSchedulingActions(IEnumerable<ScheduleResponse> responses)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            foreach (ScheduleResponse response in responses)
            {
                switch (response.Action)
                {
                    case ScheduleActionType.NoAction:
                        break;

                    case ScheduleActionType.SubmissionComplete:
                        if (_buildParameters!.DetailedSummary)
                        {
                            _scheduler!.WriteDetailedSummary(response.BuildResult.SubmissionId);
                        }

                        ReportResultsToSubmission<BuildRequestData, BuildResult>(response.BuildResult);
                        break;

                    case ScheduleActionType.CircularDependency:
                    case ScheduleActionType.ResumeExecution:
                    case ScheduleActionType.ReportResults:
                        _nodeManager!.SendData(response.NodeId, response.Unblocker);
                        break;

                    case ScheduleActionType.CreateNode:
                        IList<NodeInfo> newNodes = _nodeManager!.CreateNodes(GetNodeConfiguration(), response.RequiredNodeType, response.NumberOfNodesToCreate);

                        if (newNodes?.Count != response.NumberOfNodesToCreate || newNodes.Any(n => n == null))
                        {
                            BuildEventContext buildEventContext = new BuildEventContext(0, Scheduler.VirtualNode, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
                            ((IBuildComponentHost)this).LoggingService.LogError(buildEventContext, new BuildEventFileInfo(String.Empty), "UnableToCreateNode", response.RequiredNodeType.ToString("G"));

                            throw new BuildAbortedException(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("UnableToCreateNode", response.RequiredNodeType.ToString("G")));
                        }

                        foreach (var node in newNodes)
                        {
                            _noNodesActiveEvent?.Reset();
                            _activeNodes.Add(node.NodeId);
                        }

                        IEnumerable<ScheduleResponse> newResponses = _scheduler!.ReportNodesCreated(newNodes);
                        PerformSchedulingActions(newResponses);

                        break;

                    case ScheduleActionType.Schedule:
                    case ScheduleActionType.ScheduleWithConfiguration:
                        if (response.Action == ScheduleActionType.ScheduleWithConfiguration)
                        {
                            // Only actually send the configuration if the node doesn't know about it.  The scheduler only keeps track
                            // of which nodes have had configurations specifically assigned to them for building.  However, a node may
                            // have created a configuration based on a build request it needs to wait on.  In this
                            // case we need not send the configuration since it will already have been mapped earlier.
                            if (!_nodeIdToKnownConfigurations.TryGetValue(response.NodeId, out HashSet<int>? configurationsOnNode) ||
                               !configurationsOnNode.Contains(response.BuildRequest.ConfigurationId))
                            {
                                IConfigCache configCache = _componentFactories.GetComponent<IConfigCache>(BuildComponentType.ConfigCache);
                                _nodeManager!.SendData(response.NodeId, configCache[response.BuildRequest.ConfigurationId]);
                            }
                        }

                        _nodeManager!.SendData(response.NodeId, response.BuildRequest);
                        break;

                    default:
                        ErrorUtilities.ThrowInternalError("Scheduling action {0} not handled.", response.Action);
                        break;
                }
            }
        }

        internal void ReportResultsToSubmission<TRequestData, TResultData>(TResultData result)
            where TRequestData : BuildRequestDataBase
            where TResultData : BuildResultBase
        {
            lock (_syncLock)
            {
                // The build submission has not already been completed.
                if (_buildSubmissions.TryGetValue(result.SubmissionId, out BuildSubmissionBase? submissionBase) &&
                    submissionBase is BuildSubmissionBase<TRequestData, TResultData> submission)
                {
                    /* If the request failed because we caught an exception from the loggers, we can assume we will receive no more logging messages for
                     * this submission, therefore set the logging as complete. InternalLoggerExceptions are unhandled exceptions from the logger. If the logger author does
                     * not handle an exception the eventsource wraps all exceptions (except a logging exception) into an internal logging exception.
                     * These exceptions will have their stack logged on the commandline as an unexpected failure. If a logger author wants the logger
                     * to fail gracefully then can catch an exception and log a LoggerException. This has the same effect of stopping the build but it logs only
                     * the exception error message rather than the whole stack trace.
                     *
                     * If any other exception happened and logging is not completed, then go ahead and complete it now since this is the last place to do it.
                     * Otherwise the submission would remain uncompleted, potentially causing hangs (EndBuild waiting on all BuildSubmissions, users waiting on BuildSubmission, or expecting a callback, etc)
                     */
                    if (!submission.LoggingCompleted && result.Exception != null)
                    {
                        submission.CompleteLogging();
                    }

                    submission.CompleteResults(result);
                    CheckSubmissionCompletenessAndRemove(submission);
                }
            }
        }

        /// <summary>
        /// Determines if the submission is fully completed.
        /// </summary>
        private void CheckSubmissionCompletenessAndRemove(BuildSubmissionBase submission)
        {
            lock (_syncLock)
            {
                // If the submission has completed or never started, remove it.
                if (submission.IsCompleted || !submission.IsStarted)
                {
                    _overallBuildSuccess &= (submission.BuildResultBase?.OverallResult == BuildResultCode.Success);
                    _buildSubmissions.Remove(submission.SubmissionId);

                    // Clear all cached SDKs for the submission
                    SdkResolverService.ClearCache(submission.SubmissionId);
                }

                CheckAllSubmissionsComplete(submission.BuildRequestDataBase.Flags);
            }
        }

        private void CheckAllSubmissionsComplete(BuildRequestDataFlags? flags)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            if (_buildSubmissions.Count == 0)
            {
                if (flags.HasValue && flags.Value.HasFlag(BuildRequestDataFlags.ClearCachesAfterBuild))
                {
                    // Reset the project root element cache if specified which ensures that projects will be re-loaded from disk.  We do not need to reset the
                    // cache on child nodes because the OutOfProcNode class sets "autoReloadFromDisk" to "true" which handles the case when a restore modifies
                    // part of the import graph.
                    _buildParameters?.ProjectRootElementCache?.Clear();

                    FileMatcher.ClearFileEnumerationsCache();
#if !CLR2COMPATIBILITY
                    FileUtilities.ClearFileExistenceCache();
#endif
                }

                _noActiveSubmissionsEvent?.Set();
            }
        }

        /// <summary>
        /// Retrieves the configuration structure for a node.
        /// </summary>
        private NodeConfiguration GetNodeConfiguration()
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            if (_nodeConfiguration == null)
            {
                // Get the remote loggers
                ILoggingService loggingService = ((IBuildComponentHost)this).GetComponent<ILoggingService>(BuildComponentType.LoggingService);
                var remoteLoggers = new List<LoggerDescription>(loggingService.LoggerDescriptions);

                _nodeConfiguration = new NodeConfiguration(
                -1, /* must be assigned by the NodeManager */
                _buildParameters,
                remoteLoggers.ToArray()
#if FEATURE_APPDOMAIN
                , AppDomain.CurrentDomain.SetupInformation
#endif
                , new LoggingNodeConfiguration(
                    loggingService.IncludeEvaluationMetaprojects,
                    loggingService.IncludeEvaluationProfile,
                    loggingService.IncludeEvaluationPropertiesAndItemsInProjectStartedEvent,
                    loggingService.IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent,
                    loggingService.IncludeTaskInputs));
            }

            return _nodeConfiguration;
        }

        /// <summary>
        /// Handler for thread exceptions. This handler will only get called if the exception did not previously
        /// get handled by a node exception handlers (for instance because the build is complete for the node.)  In this case we
        /// get the exception and will put it into the OverallBuildResult so that the host can see what happened.
        /// </summary>
        private void OnThreadException(Exception e)
        {
            lock (_syncLock)
            {
                if (_threadException == null)
                {
                    if (e is AggregateException ae && ae.InnerExceptions.Count == 1)
                    {
                        e = ae.InnerExceptions.First();
                    }

                    _threadException = ExceptionDispatchInfo.Capture(e);
                    var submissions = new List<BuildSubmissionBase>(_buildSubmissions.Values);
                    foreach (BuildSubmissionBase submission in submissions)
                    {
                        // Submission has not started
                        if (!submission.IsStarted)
                        {
                            continue;
                        }

                        // Attach the exception to this submission if it does not already have an exception associated with it
                        if (!submission.IsCompleted && submission.BuildResultBase != null && submission.BuildResultBase.Exception == null)
                        {
                            submission.BuildResultBase.Exception = e;
                        }
                        submission.CompleteLogging();

                        if (submission.BuildResultBase != null)
                        {
                            submission.CheckForCompletion();
                        }
                        else
                        {
                            submission.CompleteResultsWithException(e);
                        }

                        CheckSubmissionCompletenessAndRemove(submission);
                    }
                }
            }
        }

        /// <summary>
        /// Handler for LoggingService thread exceptions.
        /// </summary>
        private void OnLoggingThreadException(Exception e)
        {
            _workQueue!.Post(() => OnThreadException(e));
        }

        /// <summary>
        /// Raised when a project finished logging message has been processed.
        /// </summary>
        private void OnProjectFinished(object sender, ProjectFinishedEventArgs e)
        {
            _workQueue!.Post(() =>
            {
                lock (_syncLock)
                {
                    if (_projectStartedEvents.TryGetValue(e.BuildEventContext!.SubmissionId, out var originalArgs))
                    {
                        if (originalArgs.BuildEventContext!.Equals(e.BuildEventContext))
                        {
                            _projectStartedEvents.Remove(e.BuildEventContext.SubmissionId);
                            if (_buildSubmissions.TryGetValue(e.BuildEventContext.SubmissionId, out var submission))
                            {
                                submission.CompleteLogging();
                                CheckSubmissionCompletenessAndRemove(submission);
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Raised when a project started logging message is about to be processed.
        /// </summary>
        private void OnProjectStarted(object sender, ProjectStartedEventArgs e)
        {
            _workQueue!.Post(() =>
            {
                lock (_syncLock)
                {
                    if (!_projectStartedEvents.ContainsKey(e.BuildEventContext!.SubmissionId))
                    {
                        _projectStartedEvents[e.BuildEventContext.SubmissionId] = e;
                    }
                }
            });
        }

        /// <summary>
        /// Sets <see cref="BuildParameters.IsBuildCheckEnabled"/> to true. Used for BuildCheck Replay Mode.
        /// </summary>
        internal void EnableBuildCheck()
        {
            _buildParameters ??= new BuildParameters();

            _buildParameters.IsBuildCheckEnabled = true;
        }

        /// <summary>
        /// Creates a logging service around the specified set of loggers.
        /// </summary>
        private ILoggingService CreateLoggingService(
            IEnumerable<ILogger>? loggers,
            IEnumerable<ForwardingLoggerRecord>? forwardingLoggers,
            ISet<string> warningsAsErrors,
            ISet<string> warningsNotAsErrors,
            ISet<string> warningsAsMessages)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            int cpuCount = _buildParameters!.MaxNodeCount;

            LoggerMode loggerMode = cpuCount == 1 && _buildParameters.UseSynchronousLogging
                                        ? LoggerMode.Synchronous
                                        : LoggerMode.Asynchronous;

            ILoggingService loggingService = LoggingService.CreateLoggingService(loggerMode,
                1 /*This logging service is used for the build manager and the inproc node, therefore it should have the first nodeId*/);

            ((IBuildComponent)loggingService).InitializeComponent(this);
            _componentFactories.ReplaceFactory(BuildComponentType.LoggingService, loggingService as IBuildComponent);

            _threadException = null;
            loggingService.OnLoggingThreadException += _loggingThreadExceptionEventHandler;
            loggingService.OnProjectStarted += _projectStartedEventHandler;
            loggingService.OnProjectFinished += _projectFinishedEventHandler;
            loggingService.WarningsAsErrors = warningsAsErrors;
            loggingService.WarningsNotAsErrors = warningsNotAsErrors;
            loggingService.WarningsAsMessages = warningsAsMessages;

            if (_buildParameters.IsBuildCheckEnabled)
            {
                var buildCheckManagerProvider =
                    ((IBuildComponentHost)this).GetComponent(BuildComponentType.BuildCheckManagerProvider) as IBuildCheckManagerProvider;
                buildCheckManagerProvider!.Instance.SetDataSource(BuildCheckDataSource.EventArgs);

                // We do want to dictate our own forwarding logger (otherwise CentralForwardingLogger with minimum transferred importance MessageImportnace.Low is used)
                // In the future we might optimize for single, in-node build scenario - where forwarding logger is not needed (but it's just quick pass-through)
                LoggerDescription forwardingLoggerDescription = new LoggerDescription(
                    loggerClassName: typeof(BuildCheckForwardingLogger).FullName,
                    loggerAssemblyName: typeof(BuildCheckForwardingLogger).GetTypeInfo().Assembly.GetName().FullName,
                    loggerAssemblyFile: null,
                    loggerSwitchParameters: null,
                    verbosity: LoggerVerbosity.Quiet);

                ILogger buildCheckLogger =
                    new BuildCheckConnectorLogger(new CheckLoggingContextFactory(loggingService),
                        buildCheckManagerProvider.Instance);

                ForwardingLoggerRecord[] forwardingLogger = { new ForwardingLoggerRecord(buildCheckLogger, forwardingLoggerDescription) };

                forwardingLoggers = forwardingLoggers?.Concat(forwardingLogger) ?? forwardingLogger;
            }

            try
            {
                if (loggers != null)
                {
                    foreach (ILogger logger in loggers)
                    {
                        loggingService.RegisterLogger(logger);
                    }
                }

                if (loggingService.Loggers.Count == 0)
                {
                    // if no loggers have been registered - let's make sure that at least on forwarding logger
                    //  will forward events we need (project started and finished events)
                    forwardingLoggers = ProcessForwardingLoggers(forwardingLoggers);
                }

                if (forwardingLoggers != null)
                {
                    foreach (ForwardingLoggerRecord forwardingLoggerRecord in forwardingLoggers)
                    {
                        loggingService.RegisterDistributedLogger(forwardingLoggerRecord.CentralLogger, forwardingLoggerRecord.ForwardingLoggerDescription);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                ShutdownLoggingService(loggingService);
                throw;
            }

            return loggingService;

            // We need to register SOME logger if we don't have any. This ensures the out of proc nodes will still send us message,
            // ensuring we receive project started and finished events.
            static List<ForwardingLoggerRecord> ProcessForwardingLoggers(IEnumerable<ForwardingLoggerRecord>? forwarders)
            {
                Type configurableLoggerType = typeof(ConfigurableForwardingLogger);
                string engineAssemblyName = configurableLoggerType.GetTypeInfo().Assembly.GetName().FullName;
                string configurableLoggerName = configurableLoggerType.FullName!;

                if (forwarders == null)
                {
                    return [CreateMinimalForwarder()];
                }

                List<ForwardingLoggerRecord> result = forwarders.ToList();

                // The forwarding loggers that are registered are unknown to us - we cannot make any assumptions.
                // So to be on a sure side - we need to add ours.
                if (!result.Any(l => l.ForwardingLoggerDescription.Name.Contains(engineAssemblyName)))
                {
                    result.Add(CreateMinimalForwarder());
                    return result;
                }

                // Those are the cases where we are sure that we have the forwarding setup as need.
                if (result.Any(l =>
                        l.ForwardingLoggerDescription.Name.Contains(typeof(CentralForwardingLogger).FullName!)
                        ||
                        (l.ForwardingLoggerDescription.Name.Contains(configurableLoggerName)
                         &&
                         l.ForwardingLoggerDescription.LoggerSwitchParameters.Contains("PROJECTSTARTEDEVENT")
                         &&
                         l.ForwardingLoggerDescription.LoggerSwitchParameters.Contains("PROJECTFINISHEDEVENT")
                         &&
                         l.ForwardingLoggerDescription.LoggerSwitchParameters.Contains("FORWARDPROJECTCONTEXTEVENTS")
                        )))
                {
                    return result;
                }

                // In case there is a ConfigurableForwardingLogger, that is not configured as we'd need - we can adjust the config
                ForwardingLoggerRecord? configurableLogger = result.FirstOrDefault(l =>
                    l.ForwardingLoggerDescription.Name.Contains(configurableLoggerName));

                // If there is not - we need to add our own.
                if (configurableLogger == null)
                {
                    result.Add(CreateMinimalForwarder());
                    return result;
                }

                configurableLogger.ForwardingLoggerDescription.LoggerSwitchParameters += ";PROJECTSTARTEDEVENT;PROJECTFINISHEDEVENT;FORWARDPROJECTCONTEXTEVENTS;RESPECTVERBOSITY";

                return result;

                ForwardingLoggerRecord CreateMinimalForwarder()
                {
                    // We need to register SOME logger if we don't have any. This ensures the out of proc nodes will still send us message,
                    // ensuring we receive project started and finished events.
                    LoggerDescription forwardingLoggerDescription = new LoggerDescription(
                        loggerClassName: configurableLoggerName,
                        loggerAssemblyName: engineAssemblyName,
                        loggerAssemblyFile: null,
                        loggerSwitchParameters: "PROJECTSTARTEDEVENT;PROJECTFINISHEDEVENT;FORWARDPROJECTCONTEXTEVENTS",
                        verbosity: LoggerVerbosity.Quiet);

                    return new ForwardingLoggerRecord(new NullLogger(), forwardingLoggerDescription);
                }
            }
        }

        private static void LogDeferredMessages(ILoggingService loggingService, IEnumerable<DeferredBuildMessage>? deferredBuildMessages)
        {
            if (deferredBuildMessages == null)
            {
                return;
            }

            foreach (var message in deferredBuildMessages)
            {
                loggingService.LogCommentFromText(BuildEventContext.Invalid, message.Importance, message.Text);

                // If message includes a file path, include that file
                if (message.FilePath is not null)
                {
                    loggingService.LogIncludeFile(BuildEventContext.Invalid, message.FilePath);
                }
            }
        }

        /// <summary>
        /// Ensures that the packet type matches the expected type
        /// </summary>
        /// <typeparam name="I">The instance-type of packet being expected</typeparam>
        private static I ExpectPacketType<I>(INodePacket packet, NodePacketType expectedType) where I : class, INodePacket
        {
            I? castPacket = packet as I;

            // PERF: Not using VerifyThrow here to avoid boxing of expectedType.
            if (castPacket == null)
            {
                ErrorUtilities.ThrowInternalError("Incorrect packet type: {0} should have been {1}", packet.Type, expectedType);
            }

            return castPacket!;
        }

        /// <summary>
        ///  Shutdown the logging service
        /// </summary>
        private void ShutdownLoggingService(ILoggingService? loggingService)
        {
            try
            {
                if (loggingService != null)
                {
                    loggingService.OnLoggingThreadException -= _loggingThreadExceptionEventHandler;
                    loggingService.OnProjectFinished -= _projectFinishedEventHandler;
                    loggingService.OnProjectStarted -= _projectStartedEventHandler;
                    _componentFactories.ShutdownComponent(BuildComponentType.LoggingService);
                }
            }
            finally
            {
                // Even if an exception is thrown, we want to make sure we null out the logging service so that
                // we don't try to shut it down again in some other cleanup code.
                _componentFactories.ReplaceFactory(BuildComponentType.LoggingService, (IBuildComponent?)null);
            }
        }

        /// <summary>
        /// Dispose implementation
        /// </summary>
        private void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                lock (_syncLock)
                {
                    if (_disposed)
                    {
                        // Multiple caller raced for enter into the lock
                        return;
                    }

                    // We should always have finished cleaning up before calling Dispose.
                    RequireState(BuildManagerState.Idle, "ShouldNotDisposeWhenBuildManagerActive");

                    _componentFactories?.ShutdownComponents();

                    if (_workQueue != null)
                    {
                        _workQueue.Complete();
                        _workQueue = null;
                    }

                    if (_executionCancellationTokenSource != null)
                    {
                        _executionCancellationTokenSource.Cancel();
                        _executionCancellationTokenSource = null;
                    }

                    if (_noActiveSubmissionsEvent != null)
                    {
                        _noActiveSubmissionsEvent.Dispose();
                        _noActiveSubmissionsEvent = null;
                    }

                    if (_noNodesActiveEvent != null)
                    {
                        _noNodesActiveEvent.Dispose();
                        _noNodesActiveEvent = null;
                    }

                    if (ReferenceEquals(this, s_singletonInstance))
                    {
                        s_singletonInstance = null;
                    }

                    _disposed = true;
                }
            }
        }

        private bool ReuseOldCaches(string[] inputCacheFiles)
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            ErrorUtilities.VerifyThrowInternalNull(inputCacheFiles);
            ErrorUtilities.VerifyThrow(_configCache == null, "caches must not be set at this point");
            ErrorUtilities.VerifyThrow(_resultsCache == null, "caches must not be set at this point");

            try
            {
                if (inputCacheFiles.Length == 0)
                {
                    return false;
                }

                if (inputCacheFiles.Any(f => !File.Exists(f)))
                {
                    LogErrorAndShutdown(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("InputCacheFilesDoNotExist", string.Join(";", inputCacheFiles.Where(f => !File.Exists(f)))));
                    return false;
                }

                var cacheAggregator = new CacheAggregator(() => GetNewConfigurationId());

                foreach (var inputCacheFile in inputCacheFiles)
                {
                    var (configCache, resultsCache, exception) = CacheSerialization.DeserializeCaches(inputCacheFile);

                    if (exception != null)
                    {
                        LogErrorAndShutdown(ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("ErrorReadingCacheFile", inputCacheFile, exception.Message));
                        return false;
                    }

                    cacheAggregator.Add(configCache, resultsCache);
                }

                var cacheAggregation = cacheAggregator.Aggregate();

                // using caches with override (override queried first before current cache) based on the assumption that during single project cached builds
                // there's many old results, but just one single actively building project.

                _componentFactories.ReplaceFactory(BuildComponentType.ConfigCache, new ConfigCacheWithOverride(cacheAggregation.ConfigCache));
                _componentFactories.ReplaceFactory(BuildComponentType.ResultsCache, new ResultsCacheWithOverride(cacheAggregation.ResultsCache));

                return true;
            }
            catch
            {
                CancelAndMarkAsFailure();
                throw;
            }
        }

        private void LogMessage(string message)
        {
            var loggingService = ((IBuildComponentHost)this).LoggingService;

            loggingService?.LogCommentFromText(BuildEventContext.Invalid, MessageImportance.High, message);
        }

        private void LogErrorAndShutdown(string message)
        {
            var loggingService = ((IBuildComponentHost)this).LoggingService;

            loggingService?.LogErrorFromText(
                BuildEventContext.Invalid,
                null,
                null,
                null,
                BuildEventFileInfo.Empty,
                message);

            CancelAndMarkAsFailure();

            if (loggingService == null)
            {
                // todo should we write this to temp file instead (like failing nodes do)
                throw new Exception(message);
            }
        }

        private void CancelAndMarkAsFailure()
        {
            Debug.Assert(Monitor.IsEntered(_syncLock));

            CancelAllSubmissions();

            // CancelAllSubmissions also ends up setting _shuttingDown and _overallBuildSuccess but it does so in a separate thread to avoid deadlocks.
            // This might cause a race with the first builds which might miss the shutdown update and succeed instead of fail.
            _shuttingDown = true;
            _executionCancellationTokenSource?.Cancel();
            _overallBuildSuccess = false;
        }

        /// <summary>
        /// The logger registered to the logging service when no other one is.
        /// </summary>
        internal class NullLogger : ILogger
        {
            #region ILogger Members

            /// <summary>
            /// The logger verbosity.
            /// </summary>
            public LoggerVerbosity Verbosity
            {
                get => LoggerVerbosity.Normal;
                set { }
            }

            /// <summary>
            /// The logger parameters.
            /// </summary>
            public string? Parameters
            {
                get => String.Empty;
                set { }
            }

            /// <summary>
            /// Initialize.
            /// </summary>
            public void Initialize(IEventSource eventSource)
            {
                // Most checks in LoggingService are "does any attached logger
                // specifically opt into this new behavior?". As such, the
                // NullLogger shouldn't opt into them explicitly and should
                // let other loggers opt in.

                // IncludeEvaluationPropertiesAndItems was different,
                // because it checked "do ALL attached loggers opt into
                // the new behavior?".
                // It was fixed and hence we need to be careful not to opt in
                // the behavior as it was done before - but let the other loggers choose.
                //
                // For this reason NullLogger MUST NOT call
                // ((IEventSource4)eventSource).IncludeEvaluationPropertiesAndItems();
            }

            /// <summary>
            /// Shutdown.
            /// </summary>
            public void Shutdown()
            {
            }

            #endregion
        }
    }
}
