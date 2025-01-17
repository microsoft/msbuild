// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Build.BackEnd.Components.RequestBuilder;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using InternalLoggerException = Microsoft.Build.Exceptions.InternalLoggerException;
using LoggerDescription = Microsoft.Build.Logging.LoggerDescription;
using Microsoft.Build.Experimental.BuildCheck;

#nullable disable

namespace Microsoft.Build.BackEnd.Logging
{
    /// <summary>
    /// What is the mode of the logger, should there be a thread
    /// processing the buildEvents and raising them on the filters and sinks
    /// or should they be done synchronously
    /// </summary>
    internal enum LoggerMode
    {
        /// <summary>
        /// Events are processed synchronously
        /// </summary>
        Synchronous,

        /// <summary>
        /// A thread is started which will process build events by raising them on a filter event source
        /// or on the correct sink.
        /// </summary>
        Asynchronous
    }

    /// <summary>
    /// What is the current state of the logging service
    /// </summary>
    internal enum LoggingServiceState
    {
        /// <summary>
        /// When the logging service has been instantiated but not yet initialized through a call
        /// to initializecomponent
        /// </summary>
        Instantiated,

        /// <summary>
        /// The logging service has been initialized through a call to initialize component
        /// </summary>
        Initialized,

        /// <summary>
        ///  The logging service is in the process of starting to shutdown.
        /// </summary>
        ShuttingDown,

        /// <summary>
        /// The logging service completly shutdown
        /// </summary>
        Shutdown
    }

    /// <summary>
    /// Logging services is used as a helper class to assist logging messages in getting to the correct loggers.
    /// </summary>
    internal partial class LoggingService : ILoggingService, INodePacketHandler
    {
        /// <summary>
        /// The default maximum size for the logging event queue.
        /// </summary>
        private const uint DefaultQueueCapacity = 200000;

        /// <summary>
        /// Lock for the nextProjectId
        /// </summary>
        private readonly object _lockObject = new Object();

        /// <summary>
        /// A cached reflection accessor for an internal member.
        /// </summary>
        /// <remarks>
        /// We use a BindingFlags.Public flag here because the getter is public, so although the setter is internal,
        /// it is only discoverable with Reflection using the Public flag (go figure!)
        /// </remarks>
        private static Lazy<PropertyInfo> s_projectStartedEventArgsGlobalProperties = new Lazy<PropertyInfo>(() => typeof(ProjectStartedEventArgs).GetProperty("GlobalProperties", BindingFlags.Public | BindingFlags.Instance), LazyThreadSafetyMode.PublicationOnly);

        /// <summary>
        /// A cached reflection accessor for an internal member.
        /// </summary>
        /// <remarks>
        /// We use a BindingFlags.Public flag here because the getter is public, so although the setter is internal,
        /// it is only discoverable with Reflection using the Public flag (go figure!)
        /// </remarks>
        private static Lazy<PropertyInfo> s_projectStartedEventArgsToolsVersion = new Lazy<PropertyInfo>(() => typeof(ProjectStartedEventArgs).GetProperty("ToolsVersion", BindingFlags.Public | BindingFlags.Instance), LazyThreadSafetyMode.PublicationOnly);

        #region Data

        /// <summary>
        /// The mapping of build request configuration ids to project file names.
        /// </summary>
        private ConcurrentDictionary<int, string> _projectFileMap;

        /// <summary>
        /// The current state of the logging service
        /// </summary>
        private LoggingServiceState _serviceState;

        /// <summary>
        /// Use to optimize away status messages. When this is set to true, only "critical"
        /// events like errors are logged. Default is false
        /// </summary>
        private bool _onlyLogCriticalEvents;

        /// <summary>
        /// Contains a dictionary of loggerId's and the sink which the logger (of the given Id) is expecting to consume its messages
        /// </summary>
        private Dictionary<int, IBuildEventSink> _eventSinkDictionary;

        /// <summary>
        /// A list of ILoggers registered with the LoggingService
        /// </summary>
        private List<ILogger> _loggers;

        /// <summary>
        /// A list of LoggerDescriptions which describe how to create a forwarding logger on a node. These are
        /// passed to each node as they are created so that the forwarding loggers can be registered on them.
        /// </summary>
        private List<LoggerDescription> _loggerDescriptions;

        /// <summary>
        /// The event source to which filters will listen to get the build events which are logged to the logging service through the
        /// logging helper methods. Ie LogMessage and LogMessageEvent
        /// </summary>
        private EventSourceSink _filterEventSource;

        /// <summary>
        /// Index into the eventSinkDictionary which indicates which sink is the sink for any logger registered through RegisterLogger
        /// </summary>
        private int _centralForwardingLoggerSinkId = -1;

        /// <summary>
        /// What is the Id for the next logger registered with the logging service.
        /// This Id is unique for this instance of the loggingService.
        /// </summary>
        private int _nextSinkId = 0;

        /// <summary>
        /// The number of nodes in the system. Loggers may take different action depending on how many nodes are in the system.
        /// </summary>
        private int _maxCPUCount = 1;

        /// <summary>
        /// Component host for this component which is used to get system parameters and other initialization information.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The IConfigCache instance obtained from componentHost (stored here to avoid repeated dictionary lookups).
        /// </summary>
        private Lazy<IConfigCache> _configCache;

        /// <summary>
        /// The next project ID to assign when a project evaluation started event is received.
        /// </summary>
        private int _nextEvaluationId;

        /// <summary>
        /// The next project ID to assign when a project started event is received.
        /// </summary>
        private int _nextProjectId;

        /// <summary>
        /// The next target ID to assign when a target started event is received.
        /// </summary>
        private int _nextTargetId = 1;

        /// <summary>
        /// The next task ID to assign when a task started event is received.
        /// </summary>
        private int _nextTaskId = 1;

        /// <summary>
        /// What node is this logging service running on
        /// </summary>
        private int _nodeId = 0;

        /// <summary>
        /// Whether to include evaluation metaprojects in events.
        /// </summary>
        private bool? _includeEvaluationMetaprojects;

        /// <summary>
        /// Whether to include evaluation profiles in events.
        /// </summary>
        private bool? _includeEvaluationProfile;

        /// <summary>
        /// Whether to include task inputs in task events.
        /// </summary>
        private bool? _includeTaskInputs;

        /// <summary>
        /// A list of build submission IDs that have logged errors.  If an error is logged outside of a submission, the submission ID is <see cref="BuildEventContext.InvalidSubmissionId"/>.
        /// </summary>
        private readonly ISet<int> _buildSubmissionIdsThatHaveLoggedErrors = new HashSet<int>();

        /// <summary>
        /// A list of build submission IDs that have logged errors through buildcheck.  If an error is logged outside of a submission, the submission ID is <see cref="BuildEventContext.InvalidSubmissionId"/>.
        /// </summary>
        private readonly ISet<int> _buildSubmissionIdsThatHaveLoggedBuildcheckErrors = new HashSet<int>();

        /// <summary>
        /// A list of warnings to treat as errors for an associated <see cref="BuildEventContext"/>.  If an empty set, all warnings are treated as errors.
        /// </summary>
        private IDictionary<WarningsConfigKey, ISet<string>> _warningsAsErrorsByProject;

        /// <summary>
        /// A list of warnings to not to be promoted to errors for an associated <see cref="BuildEventContext"/>.
        /// </summary>
        private IDictionary<WarningsConfigKey, ISet<string>> _warningsNotAsErrorsByProject;

        /// <summary>
        /// A list of warnings to treat as messages for an associated <see cref="BuildEventContext"/>.
        /// </summary>
        private IDictionary<WarningsConfigKey, ISet<string>> _warningsAsMessagesByProject;

        /// <summary>
        /// The minimum message importance that must be logged because there is a possibility that a logger consumes it.
        /// Null means that the optimization is disabled or no relevant logger has been registered.
        /// </summary>
        private MessageImportance? _minimumRequiredMessageImportance;

        #region LoggingThread Data

        /// <summary>
        /// Queue for asynchronous event processing.
        /// </summary>
        private ConcurrentQueue<object> _eventQueue;

        /// <summary>
        /// Event set when message is consumed from queue.
        /// </summary>
        private AutoResetEvent _dequeueEvent;
        /// <summary>
        /// Event set when queue become empty.
        /// </summary>
        private ManualResetEvent _emptyQueueEvent;
        /// <summary>
        /// Even set when message is added into queue.
        /// </summary>
        private AutoResetEvent _enqueueEvent;

        /// <summary>
        /// CTS for stopping logging event processing.
        /// </summary>
        private CancellationTokenSource _loggingEventProcessingCancellation;

        /// <summary>
        /// Task which pump/process messages from <see cref="_eventQueue"/>
        /// </summary>
        private Thread _loggingEventProcessingThread;

        /// <summary>
        /// The queue size above which the queue will close to messages from remote nodes.
        /// This value should be selected such that during normal builds it is never reached.
        /// It should also be low enough that we do not accumulate enough messages to cause
        /// virtual memory exhaustion in extremely large builds.
        /// </summary>
        private uint _queueCapacity = DefaultQueueCapacity;

        /// <summary>
        /// By default our logMode is Asynchronous. We do this
        /// because we are hoping it will make the system
        /// more responsive when there are a large number of logging messages
        /// </summary>
        private LoggerMode _logMode = LoggerMode.Asynchronous;

        #endregion

        #endregion

        #region Constructors

        /// <summary>
        /// Initialize an instance of a loggingService.
        /// </summary>
        /// <param name="loggerMode">Should the events be processed synchronously or asynchronously</param>
        /// <param name="nodeId">The node identifier.</param>
        protected LoggingService(LoggerMode loggerMode, int nodeId)
        {
            _projectFileMap = new ConcurrentDictionary<int, string>();
            _logMode = loggerMode;
            _loggers = new List<ILogger>();
            _loggerDescriptions = new List<LoggerDescription>();
            _eventSinkDictionary = new Dictionary<int, IBuildEventSink>();
            _nodeId = nodeId;
            _configCache = new Lazy<IConfigCache>(() => (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache), LazyThreadSafetyMode.PublicationOnly);

            // Start the project context id count at the nodeId
            _nextProjectId = nodeId;
            _nextEvaluationId = nodeId;

            string queueCapacityEnvironment = Environment.GetEnvironmentVariable("MSBUILDLOGGINGQUEUECAPACITY");
            if (!String.IsNullOrEmpty(queueCapacityEnvironment))
            {
                if (UInt32.TryParse(queueCapacityEnvironment, out uint localQueueCapacity))
                {
                    _queueCapacity = localQueueCapacity;
                }

                _queueCapacity = Math.Max(0, _queueCapacity);
            }

            if (_logMode == LoggerMode.Asynchronous)
            {
                StartLoggingEventProcessing();
            }

            // Ensure the static constructor of ItemGroupLoggingHelper runs.
            // It is important to ensure the Message delegate on TaskParameterEventArgs is set.
            _ = ItemGroupLoggingHelper.ItemGroupIncludeLogMessagePrefix;

            _serviceState = LoggingServiceState.Instantiated;
        }

        #endregion

        #region Events

        /// <summary>
        /// When there is an exception on the logging thread, we do not want to throw the exception from there
        /// instead we would like the exception to be thrown on the engine thread as this is where hosts expect
        /// to see the exception. This event will transport the exception from the loggingService to the engine
        /// which will register on this event.
        /// </summary>
        public event LoggingExceptionDelegate OnLoggingThreadException;

        /// <summary>
        /// Raised when a ProjectStarted event is about to be sent to the loggers.
        /// </summary>
        public event ProjectStartedEventHandler OnProjectStarted;

        /// <summary>
        /// Raised when a ProjectFinished event has just been sent to the loggers.
        /// </summary>
        public event ProjectFinishedEventHandler OnProjectFinished;

        #endregion

        #region Properties

        /// <summary>
        /// Router of the build engine runtime execution information.
        /// </summary>
        public IBuildEngineDataRouter BuildEngineDataRouter => this;

        /// <summary>
        /// Properties we need to serialize from the child node
        /// </summary>
        public string[] PropertiesToSerialize
        {
            get;
            set;
        }

        /// <summary>
        /// Should all properties be serialized from the child to the parent node
        /// </summary>
        public bool SerializeAllProperties
        {
            get;
            set;
        }

        /// <summary>
        /// Is the logging running on a remote node
        /// </summary>
        public bool RunningOnRemoteNode
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the next project evaluation id.
        /// </summary>
        /// <remarks>This property is thread-safe</remarks>
        public int NextEvaluationId
        {
            get
            {
                // We can create one node more than the maxCPU count (this can happen if either the inproc or out of proc node has not been created yet and the project collection needs to be counted also)
                return Interlocked.Add(ref _nextEvaluationId, MaxCPUCount + 2);
            }
        }

        /// <summary>
        /// Gets the next project id.
        /// </summary>
        /// <remarks>This property is thread-safe</remarks>
        public int NextProjectId
        {
            get
            {
                // We can create one node more than the maxCPU count (this can happen if either the inproc or out of proc node has not been created yet and the project collection needs to be counted also)
                return Interlocked.Add(ref _nextProjectId, MaxCPUCount + 2);
            }
        }

        /// <summary>
        /// Gets the next target id.
        /// </summary>
        /// <remarks>This property is thread-safe</remarks>
        public int NextTargetId
        {
            get
            {
                return Interlocked.Increment(ref _nextTargetId);
            }
        }

        /// <summary>
        /// Gets the next task id.
        /// </summary>
        /// <remarks>This property is thread-safe</remarks>
        public int NextTaskId
        {
            get
            {
                return Interlocked.Increment(ref _nextTaskId);
            }
        }

        /// <summary>
        /// Provide the current state of the loggingService.
        /// Is it Inistantiated
        /// Has it been Initialized
        /// Is it starting to shutdown
        /// Has it shutdown
        /// </summary>
        public LoggingServiceState ServiceState => _serviceState;

        /// <summary>
        /// Use to optimize away status messages. When this is set to true, only "critical"
        /// events like errors are logged.
        /// </summary>
        public bool OnlyLogCriticalEvents
        {
            get => _onlyLogCriticalEvents;

            set => _onlyLogCriticalEvents = value;
        }

        /// <summary>
        /// Number of nodes in the system when the system is initially started
        /// </summary>
        public int MaxCPUCount
        {
            get => _maxCPUCount;

            set => _maxCPUCount = value;
        }

        /// <summary>
        /// Gets or sets a value that will error when the build process fails an up-to-date check.
        /// </summary>
        public bool Question { get; set; }

        /// <summary>
        /// The list of descriptions which describe how to create forwarding loggers on a node.
        /// This is used by the node provider to get a list of registered descriptions so that
        /// they can be transmitted to child nodes.
        /// </summary>
        public ICollection<LoggerDescription> LoggerDescriptions => _loggerDescriptions;

        /// <summary>
        /// Enumerator over all registered loggers.
        /// </summary>
        public ICollection<ILogger> Loggers => _loggers;

        /// <summary>
        /// What type of logging mode is the logger running under.
        /// Is it Synchronous or Asynchronous
        /// </summary>
        public LoggerMode LoggingMode => _logMode;

        /// <summary>
        /// Get of warnings to treat as errors.  An empty non-null set will treat all warnings as errors.
        /// </summary>
        public ISet<string> WarningsAsErrors
        {
            get;
            set;
        } = null;

        /// <summary>
        /// Get of warnings to not treat as errors. Only has any effect if WarningsAsErrors is empty but not null.
        /// </summary>
        public ISet<string> WarningsNotAsErrors
        {
            get;
            set;
        } = null;

        /// <summary>
        /// A list of warnings to treat as low importance messages.
        /// </summary>
        public ISet<string> WarningsAsMessages
        {
            get;
            set;
        } = null;

        /// <summary>
        /// Should evaluation events include generated metaprojects?
        /// </summary>
        public bool IncludeEvaluationMetaprojects
        {
            get => _includeEvaluationMetaprojects ??= _eventSinkDictionary.Values.OfType<EventSourceSink>().Any(sink => sink.IncludeEvaluationMetaprojects);
            set => _includeEvaluationMetaprojects = value;
        }

        /// <summary>
        /// Should evaluation events include profiling information?
        /// </summary>
        public bool IncludeEvaluationProfile
        {
            get => _includeEvaluationProfile ??= _eventSinkDictionary.Values.OfType<EventSourceSink>().Any(sink => sink.IncludeEvaluationProfiles);
            set => _includeEvaluationProfile = value;
        }

        /// <summary>
        /// Should task events include task inputs?
        /// </summary>
        public bool IncludeTaskInputs
        {
            get => _includeTaskInputs ??= _eventSinkDictionary.Values.OfType<EventSourceSink>().Any(sink => sink.IncludeTaskInputs);
            set => _includeTaskInputs = value;
        }

        /// <inheritdoc cref="ILoggingService.SetIncludeEvaluationPropertiesAndItemsInEvents"/>
        public void SetIncludeEvaluationPropertiesAndItemsInEvents(bool inProjectStartedEvent, bool inEvaluationFinishedEvent)
        {
            _evalDataBehaviorSet = true;
            IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent = inEvaluationFinishedEvent;
            IncludeEvaluationPropertiesAndItemsInProjectStartedEvent = inProjectStartedEvent;
        }

        private bool _evalDataBehaviorSet;
        private bool _includeEvaluationPropertiesAndItemsInProjectStartedEvent;
        private bool _includeEvaluationPropertiesAndItemsInEvaluationFinishedEvent;
        private void InferEvalDataBehavior()
        {
            if (_evalDataBehaviorSet)
            {
                return;
            }
            // Set this right away - to prevent SO exception in case of any future refactoring
            //  that would refer to the IncludeEvaluation... properties here
            _evalDataBehaviorSet = true;

            bool? escapeHatch = Traits.Instance.EscapeHatches.LogPropertiesAndItemsAfterEvaluation;
            if (escapeHatch.HasValue)
            {
                IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent = escapeHatch.Value;
                IncludeEvaluationPropertiesAndItemsInProjectStartedEvent = !escapeHatch.Value;
            }
            else
            {
                var sinks = _eventSinkDictionary.Values.OfType<EventSourceSink>().ToList();

                if (ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave17_12))
                {
                    // If any logger requested the data - we need to emit them
                    IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent =
                        sinks.Any(sink => sink.IncludeEvaluationPropertiesAndItems);
                    // If any logger didn't request the data - hence it's likely legacy logger
                    //  - we need to populate the data in legacy way
                    IncludeEvaluationPropertiesAndItemsInProjectStartedEvent =
                        sinks.Any(sink => !sink.IncludeEvaluationPropertiesAndItems);
                }
                else
                {
                    bool allSinksIncludeEvalData = sinks.Any() && sinks.All(sink => sink.IncludeEvaluationPropertiesAndItems);

                    IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent = allSinksIncludeEvalData;
                    IncludeEvaluationPropertiesAndItemsInProjectStartedEvent = !allSinksIncludeEvalData;
                }
            }
        }

        /// <inheritdoc cref="ILoggingService.IncludeEvaluationPropertiesAndItemsInProjectStartedEvent"/>
        public bool IncludeEvaluationPropertiesAndItemsInProjectStartedEvent
        {
            get
            {
                InferEvalDataBehavior();
                return _includeEvaluationPropertiesAndItemsInProjectStartedEvent;
            }
            private set => _includeEvaluationPropertiesAndItemsInProjectStartedEvent = value;
        }

        /// <inheritdoc cref="ILoggingService.IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent"/>
        public bool IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent
        {
            get
            {
                InferEvalDataBehavior();
                return _includeEvaluationPropertiesAndItemsInEvaluationFinishedEvent;
            }
            private set => _includeEvaluationPropertiesAndItemsInEvaluationFinishedEvent = value;
        }

        /// <summary>
        /// Determines if the specified submission has logged an errors.
        /// </summary>
        /// <param name="submissionId">The ID of the build submission.  A value of "0" means that an error was logged outside of any build submission.</param>
        /// <returns><code>true</code> if the build submission logged an errors, otherwise <code>false</code>.</returns>
        public bool HasBuildSubmissionLoggedErrors(int submissionId)
        {
            if (_buildSubmissionIdsThatHaveLoggedBuildcheckErrors.Contains(submissionId))
            {
                return true;
            }

            // Warnings as errors are not tracked if the user did not specify to do so
            if (WarningsAsErrors == null && _warningsAsErrorsByProject == null)
            {
                return false;
            }

            // Determine if any of the event sinks have logged an error with this submission ID
            return _buildSubmissionIdsThatHaveLoggedErrors?.Contains(submissionId) == true;
        }

        /// <summary>
        /// Returns a collection of warnings to be logged as errors for the specified build context.
        /// </summary>
        /// <param name="context">The build context through which warnings will be logged as errors.</param>
        /// <returns>
        /// </returns>
        public ICollection<string> GetWarningsAsErrors(BuildEventContext context)
        {
            return GetWarningsForProject(context, _warningsAsErrorsByProject, WarningsAsErrors);
        }

        /// <summary>
        /// Returns a collection of warnings not to be logged as errors for the specified build context.
        /// </summary>
        /// <param name="context">The build context through which warnings will be kept as warnings.</param>
        /// <returns>
        /// </returns>
        public ICollection<string> GetWarningsNotAsErrors(BuildEventContext context)
        {
            return GetWarningsForProject(context, _warningsNotAsErrorsByProject, WarningsNotAsErrors);
        }

        /// <summary>
        /// Returns a collection of warnings to be demoted to messages for the specified build context.
        /// </summary>
        /// <param name="context">The build context through which warnings will be logged as messages.</param>
        /// <returns>
        /// </returns>
        public ICollection<string> GetWarningsAsMessages(BuildEventContext context)
        {
            return GetWarningsForProject(context, _warningsAsMessagesByProject, WarningsAsMessages);
        }

        /// <summary>
        /// Helper method that unifies the logic for GetWarningsAsErrors, GetWarningsNotAsErrors, and GetWarningsAsMessages.
        /// Specifically, this method returns a collection of codes that, within the context of a particular project, should
        /// be treated specially. These tend to come from setting the associated properties in the project file. These are
        /// added to previously known codes as necessary.
        /// </summary>
        /// <param name="context">The specific context in which to consider special treatment for warnings.</param>
        /// <param name="warningsByProject">A dictionary of all warnings to be treated special by for which projects.</param>
        /// <param name="warnings">Warning codes we already know should be promoted, demoted, or not promoted as relevant.</param>
        /// <returns></returns>
        private ICollection<string> GetWarningsForProject(BuildEventContext context, IDictionary<WarningsConfigKey, ISet<string>> warningsByProject, ISet<string> warnings)
        {
            WarningsConfigKey key = GetWarningsConfigKey(context);

            if (warningsByProject != null && warningsByProject.TryGetValue(key, out ISet<string> newWarnings))
            {
                if (warnings != null)
                {
                    newWarnings.UnionWith(warnings);
                }

                return newWarnings;
            }
            else
            {
                return warnings;
            }
        }

        /// <summary>
        /// Adds warning codes that should be treated as errors to the known set.
        /// </summary>
        /// <param name="buildEventContext">The context in which to consider possible warnings to be promoted.</param>
        /// <param name="codes">Codes to promote</param>
        public void AddWarningsAsErrors(BuildEventContext buildEventContext, ISet<string> codes)
        {
            AddWarningsAsMessagesOrErrors(ref _warningsAsErrorsByProject, buildEventContext, codes);
        }

        /// <summary>
        /// Adds warning codes that should not be treated as errors even if WarnAsError is empty (specifying that all warnings should be promoted).
        /// </summary>
        /// <param name="buildEventContext">The context in which to consider warnings not to be promoted.</param>
        /// <param name="codes">Codes not to promote</param>
        public void AddWarningsNotAsErrors(BuildEventContext buildEventContext, ISet<string> codes)
        {
            AddWarningsAsMessagesOrErrors(ref _warningsNotAsErrorsByProject, buildEventContext, codes);
        }

        /// <summary>
        /// Adds warning codes that should be treated as messages.
        /// </summary>
        /// <param name="buildEventContext">The context in which to consider warnings to be demoted.</param>
        /// <param name="codes">Codes to demote</param>
        public void AddWarningsAsMessages(BuildEventContext buildEventContext, ISet<string> codes)
        {
            AddWarningsAsMessagesOrErrors(ref _warningsAsMessagesByProject, buildEventContext, codes);
        }

        /// <summary>
        /// Adds warning codes to be treated or not treated as warnings or errors to the set of project-specific codes.
        /// </summary>
        /// <param name="warningsByProject">Dictionary with what warnings are currently known (by project) that we will add to.</param>
        /// <param name="buildEventContext">Context for the project to be added</param>
        /// <param name="codes">Codes to add</param>
        private void AddWarningsAsMessagesOrErrors(ref IDictionary<WarningsConfigKey, ISet<string>> warningsByProject, BuildEventContext buildEventContext, ISet<string> codes)
        {
            if (codes == null)
            {
                return;
            }

            lock (_lockObject)
            {
                WarningsConfigKey key = GetWarningsConfigKey(buildEventContext);

                warningsByProject ??= new ConcurrentDictionary<WarningsConfigKey, ISet<string>>();

                if (!warningsByProject.ContainsKey(key))
                {
                    // The same project instance can be built multiple times with different targets.  In this case the codes have already been added
                    warningsByProject[key] = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Return an array which contains the logger type names
        /// this can be used to display which loggers are registered on the node
        /// </summary>
        public ICollection<string> RegisteredLoggerTypeNames
        {
            get
            {
                lock (_lockObject)
                {
                    if (_loggers == null)
                    {
                        return null;
                    }

                    List<string> loggerTypes = new List<string>();
                    foreach (ILogger logger in _loggers)
                    {
                        loggerTypes.Add(logger.GetType().FullName);
                    }

                    return loggerTypes;
                }
            }
        }

        /// <summary>
        /// Return an array which contains the sink names
        /// this can be used to display which sinks are on the node
        /// </summary>
        public ICollection<string> RegisteredSinkNames
        {
            get
            {
                lock (_lockObject)
                {
                    if (_eventSinkDictionary == null)
                    {
                        return null;
                    }

                    List<string> eventSinkNames = new List<string>();
                    foreach (KeyValuePair<int, IBuildEventSink> kvp in _eventSinkDictionary)
                    {
                        eventSinkNames.Add(kvp.Value.Name);
                    }

                    return eventSinkNames;
                }
            }
        }

        /// <summary>
        /// Returns the minimum logging importance that must be logged because there is a possibility that
        /// at least one registered logger consumes it.
        /// </summary>
        public MessageImportance MinimumRequiredMessageImportance
        {
            get
            {
                // If we haven't set the field return the default of "all messages must be logged".
                return _minimumRequiredMessageImportance ?? MessageImportance.Low;
            }
        }

        #endregion

        #region Members

        #region Public methods

        /// <summary>
        /// Create an instance of a LoggingService using the specified mode.
        /// This method is used by the object factories to create instances of components.
        /// </summary>
        /// <param name="mode">Should the logger component created be synchronous or asynchronous</param>
        /// <param name="node">The identifier of the node.</param>
        /// <returns>An instantiated LoggingService as a IBuildComponent</returns>
        public static ILoggingService CreateLoggingService(LoggerMode mode, int node)
        {
            return new LoggingService(mode, node);
        }

        /// <summary>
        /// NotThreadSafe, this method should only be called from the component host thread
        /// Called by the build component host when a component is first initialized.
        /// </summary>
        /// <param name="buildComponentHost">The component host for this object</param>
        /// <exception cref="InternalErrorException">When buildComponentHost is null</exception>
        /// <exception cref="InternalErrorException">Service has already shutdown</exception>
        public void InitializeComponent(IBuildComponentHost buildComponentHost)
        {
            lock (_lockObject)
            {
                ErrorUtilities.VerifyThrow(_serviceState != LoggingServiceState.Shutdown, " The object is shutdown, should not do any operations on a shutdown component");
                ErrorUtilities.VerifyThrow(buildComponentHost != null, "BuildComponentHost was null");

                _componentHost = buildComponentHost;

                // Get the number of initial nodes the host is running with, if the component host does not have
                // this information default to 1
                _maxCPUCount = buildComponentHost.BuildParameters.MaxNodeCount;

                Question = buildComponentHost.BuildParameters.Question;

                // Ask the component host if onlyLogCriticalEvents is true or false. If the host does
                // not have this information default to false.
                _onlyLogCriticalEvents = buildComponentHost.BuildParameters.OnlyLogCriticalEvents;

                _serviceState = LoggingServiceState.Initialized;

                _buildEngineDataRouter = (buildComponentHost.GetComponent(BuildComponentType.BuildCheckManagerProvider) as IBuildCheckManagerProvider)?.BuildEngineDataRouter;
            }
        }

        /// <summary>
        /// NotThreadSafe, this method should only be called from the component host thread
        /// Called by the build component host when the component host is about to shutdown.
        /// 1. Shutdown forwarding loggers so that any events they have left to forward can get into the queue
        /// 2. Terminate the logging thread
        /// 3. Null out sinks and the filter event source so that no more events can get to the central loggers
        /// 4. Shutdown the central loggers
        /// </summary>
        /// <exception cref="InternalErrorException">Service has already shutdown</exception>
        /// <exception cref="LoggerException"> A logger may throw a logger exception when shutting down</exception>
        /// <exception cref="InternalLoggerException">A logger will wrap other exceptions (except ExceptionHandling.IsCriticalException exceptions) in a InternalLoggerException if it crashes during shutdown</exception>
        public void ShutdownComponent()
        {
            lock (_lockObject)
            {
                ErrorUtilities.VerifyThrow(_serviceState != LoggingServiceState.Shutdown, " The object is shutdown, should not do any operations on a shutdown component");

                // Set the state to indicate we are starting the shutdown process.
                _serviceState = LoggingServiceState.ShuttingDown;

                try
                {
                    try
                    {
                        // 1. Shutdown forwarding loggers so that any events they have left to forward can get into the queue
                        foreach (ILogger logger in _loggers)
                        {
                            if (logger is IForwardingLogger)
                            {
                                ShutdownLogger(logger);
                            }
                        }
                    }
                    finally
                    {
                        // 2. Terminate the logging event queue
                        if (_logMode == LoggerMode.Asynchronous)
                        {
                            TerminateLoggingEventProcessing();
                        }
                    }

                    // 3. Null out sinks and the filter event source so that no more events can get to the central loggers
                    _filterEventSource?.ShutDown();

                    foreach (IBuildEventSink sink in _eventSinkDictionary.Values)
                    {
                        sink.ShutDown();
                    }

                    // 4. Shutdown the central loggers
                    foreach (ILogger logger in _loggers)
                    {
                        ShutdownLogger(logger);
                    }
                }
                finally
                {
                    // Revert the centralLogger sinId back to -1 so that when another central logger is registered, it will generate a new
                    // sink for the central loggers.
                    _centralForwardingLoggerSinkId = -1;

                    CleanLoggingEventProcessing();

                    _loggers = new List<ILogger>();
                    _loggerDescriptions = null;
                    _eventSinkDictionary = null;
                    _filterEventSource = null;
                    _serviceState = LoggingServiceState.Shutdown;
                }
            }
        }

        /// <summary>
        /// Will receive a logging packet and send it to the correct
        /// sink which is registered to the LoggingServices.
        /// PacketReceived should be called from a single thread.
        /// </summary>
        /// <param name="node">The node from which the packet was received.</param>
        /// <param name="packet">A LogMessagePacket</param>
        /// <exception cref="InternalErrorException">Packet is null</exception>
        /// <exception cref="InternalErrorException">Packet is not a NodePacketType.LogMessage</exception>
        public void PacketReceived(int node, INodePacket packet)
        {
            // The packet cannot be null
            ErrorUtilities.VerifyThrow(packet != null, "packet was null");

            // Expected the packet type to be a logging message packet
            // PERF: Not using VerifyThrow to avoid allocations for enum.ToString (boxing of NodePacketType) in the non-error case.
            if (packet.Type != NodePacketType.LogMessage)
            {
                ErrorUtilities.ThrowInternalError("Expected packet type \"{0}\" but instead got packet type \"{1}\".", nameof(NodePacketType.LogMessage), packet.Type.ToString());
            }

            LogMessagePacket loggingPacket = (LogMessagePacket)packet;
            InjectNonSerializedData(loggingPacket);

            WarnOnDeprecatedCustomArgsSerialization(loggingPacket);

            ProcessLoggingEvent(loggingPacket.NodeBuildEvent);
        }

        /// <summary>
        /// Serializing unknown CustomEvent which has to use unsecure BinaryFormatter by TranslateDotNet.
        /// Since BinaryFormatter is going to be deprecated, log warning so users can use new Extended*EventArgs instead of custom
        /// EventArgs derived from existing EventArgs.
        /// </summary>
        private void WarnOnDeprecatedCustomArgsSerialization(LogMessagePacket loggingPacket)
        {
            if (loggingPacket.EventType == LoggingEventType.CustomEvent
                && Traits.Instance.EscapeHatches.EnableWarningOnCustomBuildEvent)
            {
                BuildEventArgs buildEvent = loggingPacket.NodeBuildEvent.Value.Value;
                BuildEventContext buildEventContext = buildEvent?.BuildEventContext ?? BuildEventContext.Invalid;

                string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(
                    out string warningCode,
                    out string helpKeyword,
                    "DeprecatedEventSerialization",
                    buildEvent?.GetType().Name ?? string.Empty);

                BuildWarningEventArgs warning = new(
                    null,
                    warningCode,
                    BuildEventFileInfo.Empty.File,
                    BuildEventFileInfo.Empty.Line,
                    BuildEventFileInfo.Empty.Column,
                    BuildEventFileInfo.Empty.EndLine,
                    BuildEventFileInfo.Empty.EndColumn,
                    message,
                    helpKeyword,
                    "MSBuild");

                warning.BuildEventContext = buildEventContext;
                if (warning.ProjectFile == null && buildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
                {
                    warning.ProjectFile = buildEvent switch
                    {
                        BuildMessageEventArgs buildMessageEvent => buildMessageEvent.ProjectFile,
                        BuildErrorEventArgs buildErrorEvent => buildErrorEvent.ProjectFile,
                        BuildWarningEventArgs buildWarningEvent => buildWarningEvent.ProjectFile,
                        _ => null,
                    };
                }

                ProcessLoggingEvent(warning);
            }
        }

        /// <summary>
        /// Register an instantiated logger which implements the ILogger interface. This logger will be registered to a specific event
        /// source (the central logger event source) which will receive all logging messages for a given build.
        /// This should not be used on a node, Loggers are not to be registered on a child node.
        /// </summary>
        /// <param name="logger">ILogger</param>
        /// <returns>True if the logger has been registered successfully. False if the logger was not registered due to it already being registered before</returns>
        /// <exception cref="InternalErrorException">If logger is null</exception>
        public bool RegisterLogger(ILogger logger)
        {
            lock (_lockObject)
            {
                ErrorUtilities.VerifyThrow(_serviceState != LoggingServiceState.Shutdown, " The object is shutdown, should not do any operations on a shutdown component");
                ErrorUtilities.VerifyThrow(logger != null, "logger was null");

                // If the logger is already in the list it should not be registered again.
                if (_loggers.Contains(logger))
                {
                    return false;
                }

                // If we have not created a distributed logger to forward all events to the central loggers and
                // a sink which will consume the events and send them to each of the central loggers, we
                // should do that now
                if (_centralForwardingLoggerSinkId == -1)
                {
                    // Create a forwarding logger which forwards all events to an eventSourceSink
                    Assembly engineAssembly = typeof(LoggingService).GetTypeInfo().Assembly;
                    string loggerClassName = "Microsoft.Build.BackEnd.Logging.CentralForwardingLogger";
                    string loggerAssemblyName = engineAssembly.GetName().FullName;
                    LoggerDescription centralForwardingLoggerDescription = new LoggerDescription(
                                                                                      loggerClassName,
                                                                                      loggerAssemblyName,
                                                                                      null /*Not needed as we are loading from current assembly*/,
                                                                                      string.Empty /*No parameters needed as we are forwarding all events*/,
                                                                                      LoggerVerbosity.Diagnostic); /*Not used, but the spirit of the logger is to forward everything so this is the most appropriate verbosity */

                    // Registering a distributed logger will initialize the logger, and create and initialize the forwarding logger.
                    // In addition it will register the logging description so that it can be instantiated on a node.
                    RegisterDistributedLogger(logger, centralForwardingLoggerDescription);

                    // Get the Id of the eventSourceSink which was created for the first logger.
                    // We keep a reference to this Id so that all other central loggers registered on this logging service (from registerLogger)
                    // will be attached to that eventSource sink so that they get all of the events forwarded by
                    // forwarded by the CentralForwardingLogger
                    _centralForwardingLoggerSinkId = centralForwardingLoggerDescription.LoggerId;
                }
                else
                {
                    // We have already create a forwarding logger and have a single eventSink which
                    // a logger can listen to inorder to get all events in the system
                    EventSourceSink eventSource = (EventSourceSink)_eventSinkDictionary[_centralForwardingLoggerSinkId];

                    // Initialize and register the logger
                    InitializeLogger(logger, eventSource);
                }

                // Logger has been registered successfully
                return true;
            }
        }

        /// <summary>
        /// Clear out all registered loggers so that none are registered.
        /// If no loggers are registered, does nothing.
        /// </summary>
        /// <remarks>
        /// UNDONE: (Logging) I don't like the semantics of this. Why should unregistering imply shutting down? VS actually calls it before registering any loggers.
        /// Also, why not just have ShutdownComponent? Or call this Shutdown or Dispose?
        /// </remarks>
        public void UnregisterAllLoggers()
        {
            lock (_lockObject)
            {
                if (_loggers.Count > 0)
                {
                    ShutdownComponent();
                }
            }

            // UNDONE: (Logging) This should re-initialize this logging service.
        }

        /// <summary>
        /// Register a distributed logger. This involves creating a new eventsource sink
        /// and associating this with the central logger. In addition the sinkId needs
        /// to be put in the loggerDescription so that nodes know what they need to
        /// tag onto the event so that the message goes to the correct logger.
        ///
        /// The central logger is initialized before the distributed logger
        /// </summary>
        /// <param name="centralLogger">Central logger to receive messages from the forwarding logger, This logger cannot have been registered before</param>
        /// <param name="forwardingLogger">Logger description which describes how to create the forwarding logger, the logger description cannot have been used before</param>
        /// <returns value="bool">True if the distributed and central logger were registered, false if they either were already registered</returns>
        /// <exception cref="InternalErrorException">If forwardingLogger is null</exception>
        /// <exception cref="LoggerException">If a logger exception is thrown while creating or initializing the distributed or central logger</exception>
        /// <exception cref="InternalLoggerException">If any exception (other than a loggerException)is thrown while creating or initializing the distributed or central logger, we will wrap these exceptions in an InternalLoggerException</exception>
        public bool RegisterDistributedLogger(ILogger centralLogger, LoggerDescription forwardingLogger)
        {
            lock (_lockObject)
            {
                ErrorUtilities.VerifyThrow(_serviceState != LoggingServiceState.Shutdown, " The object is shutdown, should not do any operations on a shutdown component");
                ErrorUtilities.VerifyThrow(forwardingLogger != null, "forwardingLogger was null");
                if (centralLogger == null)
                {
                    centralLogger = new NullCentralLogger();
                }

                IForwardingLogger localForwardingLogger = null;

                // create an eventSourceSink which the central logger will register with to receive the events from the forwarding logger
                EventSourceSink eventSourceSink = new EventSourceSink();

                // If the logger is already in the list it should not be registered again.
                if (_loggers.Contains(centralLogger))
                {
                    return false;
                }

                // Assign a unique logger Id to this distributed logger
                int sinkId = _nextSinkId++;
                forwardingLogger.LoggerId = sinkId;
                eventSourceSink.Name = $"Sink for forwarding logger \"{sinkId}\".";

                // Initialize and register the central logger
                InitializeLogger(centralLogger, eventSourceSink);

                localForwardingLogger = forwardingLogger.CreateForwardingLogger();
                EventRedirectorToSink newRedirector = new EventRedirectorToSink(sinkId, eventSourceSink);
                localForwardingLogger.BuildEventRedirector = newRedirector;
                localForwardingLogger.Parameters = forwardingLogger.LoggerSwitchParameters;
                localForwardingLogger.Verbosity = forwardingLogger.Verbosity;

                // Give the forwarding logger registered on the inproc node the correct ID.
                localForwardingLogger.NodeId = 1;

                // Convert the path to the logger DLL to full path before passing it to the node provider
                forwardingLogger.ConvertPathsToFullPaths();

                CreateFilterEventSource();

                // Initialize and register the forwarding logger
                InitializeLogger(localForwardingLogger, _filterEventSource);

                _loggerDescriptions.Add(forwardingLogger);

                _eventSinkDictionary.Add(sinkId, eventSourceSink);

                return true;
            }
        }

        /// <summary>
        /// In order to setup the forwarding loggers on a node, we need to take in the logger descriptions and initialize them.
        /// The method will create a forwarding logger, an eventRedirector which will redirect all forwarded messages to the forwardingLoggerSink.
        /// All forwarding loggers will use the same forwardingLoggerSink.
        /// </summary>
        /// <param name="descriptions">Collection of logger descriptions which we would like to use to create a set of forwarding loggers on a node</param>
        /// <param name="forwardingLoggerSink">The buildEventSink which the fowarding loggers will forward their events to</param>
        /// <param name="nodeId">The id of the node the logging services is on</param>
        /// <exception cref="InternalErrorException">When forwardingLoggerSink is null</exception>
        /// <exception cref="InternalErrorException">When loggerDescriptions is null</exception>
        public void InitializeNodeLoggers(ICollection<LoggerDescription> descriptions, IBuildEventSink forwardingLoggerSink, int nodeId)
        {
            lock (_lockObject)
            {
                ErrorUtilities.VerifyThrow(_serviceState != LoggingServiceState.Shutdown, " The object is shutdown, should not do any operations on a shutdown component");
                ErrorUtilities.VerifyThrow(forwardingLoggerSink != null, "forwardingLoggerSink was null");
                ErrorUtilities.VerifyThrow(descriptions != null, "loggerDescriptions was null");
                ErrorUtilities.VerifyThrow(descriptions.Count > 0, "loggerDescriptions was null");

                bool sinkAlreadyRegistered = false;
                int sinkId = -1;

                // Check to see if the forwardingLoggerSink has been registered before
                foreach (KeyValuePair<int, IBuildEventSink> sinkPair in _eventSinkDictionary)
                {
                    if (sinkPair.Value == forwardingLoggerSink)
                    {
                        sinkId = sinkPair.Key;
                        sinkAlreadyRegistered = true;
                    }
                }

                if (!sinkAlreadyRegistered)
                {
                    sinkId = _nextSinkId++;
                    _eventSinkDictionary.Add(sinkId, forwardingLoggerSink);
                }

                CreateFilterEventSource();

                foreach (LoggerDescription description in descriptions)
                {
                    IForwardingLogger forwardingLogger = description.CreateForwardingLogger();
                    forwardingLogger.Verbosity = description.Verbosity;
                    forwardingLogger.Parameters = description.LoggerSwitchParameters;
                    forwardingLogger.NodeId = nodeId;
                    forwardingLogger.BuildEventRedirector = new EventRedirectorToSink(description.LoggerId, forwardingLoggerSink);

                    // Initialize and register the forwarding logger
                    InitializeLogger(forwardingLogger, _filterEventSource);
                }
            }
        }

        #region Event based logging methods

        /// <summary>
        /// Will Log a build Event. Will also take into account OnlyLogCriticalEvents when determining
        /// if to drop the event or to log it.
        ///
        /// Only the following events will be logged if OnlyLogCriticalEvents is true:
        /// CustomEventArgs
        /// BuildErrorEventArgs
        /// BuildWarningEventArgs
        /// </summary>
        /// <param name="buildEvent">BuildEvent to log</param>
        /// <exception cref="InternalErrorException">buildEvent is null</exception>
        public void LogBuildEvent(BuildEventArgs buildEvent)
        {
            ErrorUtilities.VerifyThrow(buildEvent != null, "buildEvent is null");

            BuildWarningEventArgs warningEvent = null;
            BuildErrorEventArgs errorEvent = null;
            BuildMessageEventArgs messageEvent = null;

            if ((warningEvent = buildEvent as BuildWarningEventArgs) != null && warningEvent.BuildEventContext != null && warningEvent.BuildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
            {
                warningEvent.ProjectFile = GetAndVerifyProjectFileFromContext(warningEvent, false);
            }
            else if ((errorEvent = buildEvent as BuildErrorEventArgs) != null && errorEvent.BuildEventContext != null && errorEvent.BuildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
            {
                errorEvent.ProjectFile = GetAndVerifyProjectFileFromContext(errorEvent, false);
            }
            else if ((messageEvent = buildEvent as BuildMessageEventArgs) != null && messageEvent.BuildEventContext != null && messageEvent.BuildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
            {
                // The AssemblyLoadBuildEventArgs are logged asynchronously, and build doesn't wait for those,
                //  so it can happen that ProjectFinishedEventArgs occured first - removing the id->file mapping from map,
                //  but AssemblyLoadsTracker still uses the BuildEventContext for that project
                messageEvent.ProjectFile = GetAndVerifyProjectFileFromContext(messageEvent, buildEvent is AssemblyLoadBuildEventArgs);
            }

            if (OnlyLogCriticalEvents)
            {
                // Only log certain events if OnlyLogCriticalEvents is true
                if (
                    (warningEvent != null)
                    || (errorEvent != null)
                    || (buildEvent is CustomBuildEventArgs)
                    || (buildEvent is CriticalBuildMessageEventArgs))
                {
                    ProcessLoggingEvent(buildEvent);
                }
            }
            else
            {
                // Log all events if OnlyLogCriticalEvents is false
                ProcessLoggingEvent(buildEvent);
            }
        }

        #endregion

        /// <summary>
        /// This method will be called from multiple threads in asynchronous mode.
        ///
        /// Determine where to send the buildevent either to the filters or to a specific sink.
        /// When in Asynchronous mode the event should to into the logging queue (as long as we are initialized).
        /// In Synchronous mode the event should be routed to the correct sink or logger right away
        /// </summary>
        /// <param name="buildEvent">BuildEventArgs to process</param>
        /// <exception cref="InternalErrorException">buildEvent is null</exception>
        protected internal virtual void ProcessLoggingEvent(object buildEvent)
        {
            ErrorUtilities.VerifyThrow(buildEvent != null, "buildEvent is null");
            if (_logMode == LoggerMode.Asynchronous)
            {
                // Block until queue is not full.
                while (_eventQueue.Count >= _queueCapacity)
                {
                    // Block and wait for dequeue event.
                    _dequeueEvent.WaitOne();
                }

                _eventQueue.Enqueue(buildEvent);
                _enqueueEvent.Set();
            }
            else
            {
                lock (_lockObject)
                {
                    RouteBuildEvent(buildEvent);
                }
            }
        }

        /// <summary>
        /// Wait for the logging messages in the logging queue to be completely processed.
        /// This is required because for Logging build finished or when the component is to shutdown
        /// we need to make sure we process all of the events before the build finished event is raised
        /// and we need to make sure we process all of the logging events before we shutdown the component.
        /// </summary>
        public void WaitForLoggingToProcessEvents()
        {
            while (_eventQueue?.IsEmpty == false)
            {
                _emptyQueueEvent?.WaitOne();
            }
            // To avoid race condition when last message has been removed from queue but
            //   not yet fully processed (handled by loggers), we need to make sure _emptyQueueEvent
            //   is set as it is guaranteed to be in set state no sooner than after event has been processed.
            _emptyQueueEvent?.WaitOne();
        }

        /// <summary>
        /// Adds data to the EventArgs of the log packet that the main node is aware of, but doesn't
        /// get serialized for perf reasons.
        /// </summary>
        internal void InjectNonSerializedData(LogMessagePacket loggingPacket)
        {
            if (loggingPacket?.NodeBuildEvent != null && _componentHost != null)
            {
                if (loggingPacket.NodeBuildEvent.Value.Value is ProjectStartedEventArgs projectStartedEventArgs && _configCache.Value != null)
                {
                    ErrorUtilities.VerifyThrow(_configCache.Value.HasConfiguration(projectStartedEventArgs.ProjectId), "Cannot find the project configuration while injecting non-serialized data from out-of-proc node.");
                    BuildRequestConfiguration buildRequestConfiguration = _configCache.Value[projectStartedEventArgs.ProjectId];

                    // Always log GlobalProperties on ProjectStarted for compatibility.
                    // There are loggers that depend on it being not-null and always set.
                    // See https://github.com/dotnet/msbuild/issues/6341 for details.
                    s_projectStartedEventArgsGlobalProperties.Value.SetValue(projectStartedEventArgs, buildRequestConfiguration.GlobalProperties.ToDictionary(), index: null);

                    s_projectStartedEventArgsToolsVersion.Value.SetValue(projectStartedEventArgs, buildRequestConfiguration.ToolsVersion, null);

                    // When logging happens out of process, we need to map the project context id to the project file on the receiving side.
                    if (!_projectFileMap.ContainsKey(projectStartedEventArgs.BuildEventContext.ProjectContextId))
                    {
                        _projectFileMap[projectStartedEventArgs.BuildEventContext.ProjectContextId] = projectStartedEventArgs.ProjectFile;
                    }
                }
            }
        }

        #endregion

        #region Private Methods
        private static WarningsConfigKey GetWarningsConfigKey(BuildEventContext buildEventContext)
        {
            return new WarningsConfigKey(buildEventContext.ProjectInstanceId, buildEventContext.ProjectContextId);
        }

        private static WarningsConfigKey GetWarningsConfigKey(BuildEventArgs buildEventArgs)
        {
            return GetWarningsConfigKey(buildEventArgs.BuildEventContext);
        }

        private readonly record struct WarningsConfigKey(int InstanceId, int ContextId);

        /// <summary>
        /// Create a logging thread to process the logging queue.
        /// </summary>
        private void StartLoggingEventProcessing()
        {
            _eventQueue = new ConcurrentQueue<object>();
            _dequeueEvent = new AutoResetEvent(false);
            _emptyQueueEvent = new ManualResetEvent(false);
            _enqueueEvent = new AutoResetEvent(false);
            _loggingEventProcessingCancellation = new CancellationTokenSource();

            _loggingEventProcessingThread = new Thread(LoggingEventProc);
            _loggingEventProcessingThread.Name = $"MSBuild LoggingService events queue pump: {this.GetHashCode()}";
            _loggingEventProcessingThread.IsBackground = true;
            _loggingEventProcessingThread.Start();

            void LoggingEventProc()
            {
                var completeAdding = _loggingEventProcessingCancellation.Token;
                WaitHandle[] waitHandlesForNextEvent = { completeAdding.WaitHandle, _enqueueEvent };

                do
                {
                    if (_eventQueue.TryDequeue(out object ev))
                    {
                        LoggingEventProcessor(ev);
                        _dequeueEvent.Set();
                    }
                    else
                    {
                        _emptyQueueEvent.Set();

                        // Wait for next event, or finish.
                        if (!completeAdding.IsCancellationRequested && _eventQueue.IsEmpty)
                        {
                            WaitHandle.WaitAny(waitHandlesForNextEvent);
                        }

                        _emptyQueueEvent.Reset();
                    }
                } while (!_eventQueue.IsEmpty || !completeAdding.IsCancellationRequested);

                _emptyQueueEvent.Set();
            }
        }


        /// <summary>
        /// Clean resources used for logging event processing queue.
        /// </summary>
        private void CleanLoggingEventProcessing()
        {
            _loggingEventProcessingCancellation?.Cancel();
            _dequeueEvent?.Dispose();
            _enqueueEvent?.Dispose();
            _emptyQueueEvent?.Dispose();
            _loggingEventProcessingCancellation?.Dispose();

            _eventQueue = null;
            _dequeueEvent = null;
            _enqueueEvent = null;
            _emptyQueueEvent = null;
            _loggingEventProcessingCancellation = null;
            _loggingEventProcessingThread = null;
        }

        /// <summary>
        /// Create a logging thread to process the logging queue
        /// </summary>
        private void TerminateLoggingEventProcessing()
        {
            // Capture pump task in local variable as cancelling event processing is nulling _loggingEventProcessingThread.
            var pumpTask = _loggingEventProcessingThread;
            _loggingEventProcessingCancellation.Cancel();
            pumpTask.Join();
        }

        /// <summary>
        /// Shutdown an ILogger
        /// Rethrow LoggerExceptions
        /// Wrap all other exceptions in an InternalLoggerException
        /// </summary>
        /// <param name="logger">Logger to shutdown</param>
        /// <exception cref="InternalLoggerException">Any exception comming from a logger during shutdown that is not a LoggerException is wrapped in an InternalLoggerException and thrown</exception>
        /// <exception cref="LoggerException">Errors during logger shutdown may throw a LoggerException, in this case the exception is re-thrown</exception>
        private void ShutdownLogger(ILogger logger)
        {
            try
            {
                logger?.Shutdown();
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e) && e is not LoggerException)
            {
                InternalLoggerException.Throw(e, null, "FatalErrorDuringLoggerShutdown", false, logger.GetType().Name);
            }
        }

        /// <summary>
        /// Create an event source to which the distributed (filter loggers) can attach to and listen
        /// for logging events. This event source will consume events which are logged against
        /// the logging service and raise them on itself.
        /// </summary>
        private void CreateFilterEventSource()
        {
            if (_filterEventSource == null)
            {
                _filterEventSource = new EventSourceSink
                {
                    Name = "Sink for Distributed/Filter loggers",
                };
            }
        }

        /// <summary>
        /// The logging services thread loop. This loop will wait until the logging queue has build events.
        /// When this happens the thread will start to process the queue items by raising the build event
        /// on either a filter event source or a sink depending on where the message is supposed to go.
        /// </summary>
        /// <exception cref="InternalErrorException">WaitHandle returns something other than 0 or 1</exception>
        private void LoggingEventProcessor(object loggingEvent)
        {
            // Save the culture so at the end of the threadproc if something else reuses this thread then it will not have a culture which it was not expecting.
            CultureInfo originalCultureInfo = null;
            CultureInfo originalUICultureInfo = null;

            bool cultureSet = false;
            try
            {
                // If we have a componenthost then set the culture on the first message we receive
                if (_componentHost != null)
                {
                    originalCultureInfo = CultureInfo.CurrentCulture;
                    originalUICultureInfo = CultureInfo.CurrentUICulture;
                    CultureInfo.CurrentCulture = _componentHost.BuildParameters.Culture;
                    CultureInfo.CurrentUICulture = _componentHost.BuildParameters.UICulture;
                    cultureSet = true;
                }

                RouteBuildEvent(loggingEvent);
            }
            catch (Exception e)
            {
                // Dump all engine exceptions to a temp file
                // so that we have something to go on in the
                // event of a failure
                ExceptionHandling.DumpExceptionToFile(e);

                // Catch all exceptions in order to pass them over to the engine thread. Due to
                // hosts expecting to get logger exceptions on the same thread the engine was called from.
                if (OnLoggingThreadException == null)
                {
                    throw;
                }

                RaiseLoggingExceptionEvent(e);
            }
            finally
            {
                if (cultureSet)
                {
                    // Set the culture back to the original one so that if something else reuses this thread then it will not have a culture which it was not expecting.
                    CultureInfo.CurrentCulture = originalCultureInfo;
                    CultureInfo.CurrentUICulture = originalUICultureInfo;
                }
            }
        }

        /// <summary>
        /// Route the event to the correct location, this is mostly used by the logging thread since it may have a buildevent or a tuple.
        /// </summary>
        private void RouteBuildEvent(object loggingEvent)
        {
            BuildEventArgs buildEventArgs = loggingEvent as BuildEventArgs ?? (loggingEvent as KeyValuePair<int, BuildEventArgs>?)?.Value;
            if (buildEventArgs is null)
            {
                ErrorUtilities.ThrowInternalError("Unknown logging item in queue:" + loggingEvent.GetType().FullName);
            }

            if (buildEventArgs is BuildWarningEventArgs warningEvent)
            {
                if (ShouldTreatWarningAsMessage(warningEvent))
                {
                    if (buildEventArgs is ExtendedBuildWarningEventArgs extWarningEvent)
                    {
                        buildEventArgs = new ExtendedBuildMessageEventArgs(
                                extWarningEvent.ExtendedType,
                                extWarningEvent.Subcategory,
                                extWarningEvent.Code,
                                extWarningEvent.File,
                                extWarningEvent.LineNumber,
                                extWarningEvent.ColumnNumber,
                                extWarningEvent.EndLineNumber,
                                extWarningEvent.EndColumnNumber,
                                extWarningEvent.Message,
                                extWarningEvent.HelpKeyword,
                                extWarningEvent.SenderName,
                                MessageImportance.Low,
                                extWarningEvent.Timestamp)
                        {
                            BuildEventContext = warningEvent.BuildEventContext,
                            ProjectFile = warningEvent.ProjectFile,
                            ExtendedMetadata = extWarningEvent.ExtendedMetadata,
                            ExtendedData = extWarningEvent.ExtendedData,
                        };
                    }
                    else
                    {
                        buildEventArgs = new BuildMessageEventArgs(
                            warningEvent.Subcategory,
                            warningEvent.Code,
                            warningEvent.File,
                            warningEvent.LineNumber,
                            warningEvent.ColumnNumber,
                            warningEvent.EndLineNumber,
                            warningEvent.EndColumnNumber,
                            warningEvent.Message,
                            warningEvent.HelpKeyword,
                            warningEvent.SenderName,
                            MessageImportance.Low,
                            warningEvent.Timestamp)
                        {
                            BuildEventContext = warningEvent.BuildEventContext,
                            ProjectFile = warningEvent.ProjectFile,
                        };
                    }
                }
                else if (ShouldTreatWarningAsError(warningEvent))
                {
                    if (warningEvent is ExtendedBuildWarningEventArgs extWarningEvent)
                    {
                        buildEventArgs = new ExtendedBuildErrorEventArgs(
                            extWarningEvent.ExtendedType,
                            extWarningEvent.Subcategory,
                            extWarningEvent.Code,
                            extWarningEvent.File,
                            extWarningEvent.LineNumber,
                            extWarningEvent.ColumnNumber,
                            extWarningEvent.EndLineNumber,
                            extWarningEvent.EndColumnNumber,
                            extWarningEvent.Message,
                            extWarningEvent.HelpKeyword,
                            extWarningEvent.SenderName,
                            extWarningEvent.Timestamp)
                        {
                            BuildEventContext = warningEvent.BuildEventContext,
                            ProjectFile = warningEvent.ProjectFile,
                            ExtendedMetadata = extWarningEvent.ExtendedMetadata,
                            ExtendedData = extWarningEvent.ExtendedData,
                        };
                    }
                    else
                    {
                        buildEventArgs = new BuildErrorEventArgs(
                            warningEvent.Subcategory,
                            warningEvent.Code,
                            warningEvent.File,
                            warningEvent.LineNumber,
                            warningEvent.ColumnNumber,
                            warningEvent.EndLineNumber,
                            warningEvent.EndColumnNumber,
                            warningEvent.Message,
                            warningEvent.HelpKeyword,
                            warningEvent.SenderName,
                            warningEvent.Timestamp)
                        {
                            BuildEventContext = warningEvent.BuildEventContext,
                            ProjectFile = warningEvent.ProjectFile,
                        };
                    }
                }
            }

            if (buildEventArgs is BuildErrorEventArgs errorEvent)
            {
                int submissionId = errorEvent.BuildEventContext?.SubmissionId ?? BuildEventContext.InvalidSubmissionId;

                if (buildEventArgs is BuildCheckResultError)
                {
                    _buildSubmissionIdsThatHaveLoggedBuildcheckErrors.Add(submissionId);
                }
                else
                {
                    // Keep track of build submissions that have logged errors.  If there is no build context, add BuildEventContext.InvalidSubmissionId.
                    _buildSubmissionIdsThatHaveLoggedErrors.Add(submissionId);
                }
            }

            // Respect warning-promotion properties from the remote project
            if (buildEventArgs is ProjectStartedEventArgs projectStartedEvent)
            {
                AddWarningsAsErrors(projectStartedEvent.BuildEventContext, projectStartedEvent.WarningsAsErrors);
                AddWarningsAsMessages(projectStartedEvent.BuildEventContext, projectStartedEvent.WarningsAsMessages);
                AddWarningsNotAsErrors(projectStartedEvent.BuildEventContext, projectStartedEvent.WarningsNotAsErrors);
            }

            if (buildEventArgs is ProjectFinishedEventArgs projectFinishedEvent && projectFinishedEvent.BuildEventContext != null)
            {
                WarningsConfigKey key = GetWarningsConfigKey(projectFinishedEvent);
                _warningsAsErrorsByProject?.Remove(key);
                _warningsNotAsErrorsByProject?.Remove(key);
                _warningsAsMessagesByProject?.Remove(key);
            }

            if (loggingEvent is BuildEventArgs)
            {
                RouteBuildEvent(buildEventArgs);
            }
            else if (loggingEvent is KeyValuePair<int, BuildEventArgs> loggingEventKeyValuePair)
            {
                if (loggingEventKeyValuePair.Value != buildEventArgs)
                {
                    // buildEventArgs has been altered, lets use that new one
                    loggingEventKeyValuePair = new KeyValuePair<int, BuildEventArgs>(loggingEventKeyValuePair.Key, buildEventArgs);
                }

                RouteBuildEvent(loggingEventKeyValuePair);
            }
        }

        /// <summary>
        /// Route the build event to the correct filter or sink depending on what the sinId is in the build event.
        /// </summary>
        private void RouteBuildEvent(KeyValuePair<int, BuildEventArgs> nodeEvent)
        {
            TryRaiseProjectStartedEvent(nodeEvent.Value);

            // Get the sink which will handle the build event, then send the event to that sink
            bool gotSink = _eventSinkDictionary.TryGetValue(nodeEvent.Key, out IBuildEventSink sink);
            if (gotSink && sink != null)
            {
                // Sinks in the eventSinkDictionary are expected to not be null.
                sink.Consume(nodeEvent.Value, nodeEvent.Key);
            }

            TryRaiseProjectFinishedEvent(nodeEvent.Value);
        }

        /// <summary>
        /// Route the build event to the filter
        /// </summary>
        /// <param name="eventArg">Build event that needs to be routed to the correct filter or sink.</param>
        private void RouteBuildEvent(BuildEventArgs eventArg)
        {
            TryRaiseProjectStartedEvent(eventArg);

            // The event has not been through a filter yet. All events must go through a filter before they make it to a logger
            if (_filterEventSource != null) // Loggers may not be registered
            {
                // Send the event to the filter, the Consume will not return until all of the loggers which have registered to the event have process
                // them.
                _filterEventSource.Consume(eventArg);

                // Now that the forwarding loggers have been given the chance to log the build started and finished events we need to check the
                // central logger sinks to see if they have received the events or not. If the sink has not received the event we need to send it to the
                // logger for backwards compatibility with orcas.
                // In addition we need to make sure we manually forward the events because in orcas the forwarding loggers were not allowed to
                // forward build started or build finished events. In the new OM we allow the loggers to forward the events. However since orcas did not forward them
                // we need to support loggers which cannot forward the events.
                if (eventArg is BuildStartedEventArgs)
                {
                    foreach (KeyValuePair<int, IBuildEventSink> pair in _eventSinkDictionary)
                    {
                        IBuildEventSink sink = pair.Value;
                        if (sink != null)
                        {
                            if (!sink.HaveLoggedBuildStartedEvent)
                            {
                                sink.Consume(eventArg, pair.Key);
                            }

                            // Reset the HaveLoggedBuildStarted event because no one else will be sending a build started event to any loggers at this time.
                            sink.HaveLoggedBuildStartedEvent = false;
                        }
                    }
                }
                else if (eventArg is BuildFinishedEventArgs)
                {
                    foreach (KeyValuePair<int, IBuildEventSink> pair in _eventSinkDictionary)
                    {
                        IBuildEventSink sink = pair.Value;

                        if (sink != null)
                        {
                            if (!sink.HaveLoggedBuildFinishedEvent)
                            {
                                sink.Consume(eventArg, (int)pair.Key);
                            }

                            // Reset the HaveLoggedBuildFinished event because no one else will be sending a build finished event to any loggers at this time.
                            sink.HaveLoggedBuildFinishedEvent = false;
                        }
                    }
                }
            }

            TryRaiseProjectFinishedEvent(eventArg);
        }

        /// <summary>
        /// Initializes the logger and adds it to the list of loggers maintained by the engine.
        /// This method is not expected to be called from multiple threads
        /// </summary>
        /// <exception cref="LoggerException">A logger exception thrown by a logger when its initialize call is made</exception>
        /// <exception cref="InternalLoggerException">Any exceptions from initializing the logger which are not loggerExceptions are caught and wrapped in a InternalLoggerException</exception>
        /// <exception cref="Exception">Any exception which is a ExceptionHandling.IsCriticalException will not be wrapped</exception>
        private void InitializeLogger(ILogger logger, IEventSource sourceForLogger)
        {
            ILogger UnwrapLoggerType(ILogger log)
            {
                while (log is ProjectCollection.ReusableLogger reusableLogger)
                {
                    log = reusableLogger.OriginalLogger;
                }

                return log;
            }

            try
            {
                using var assemblyLoadTracker = AssemblyLoadsTracker.StartTracking(this, AssemblyLoadingContext.LoggerInitialization, UnwrapLoggerType(logger).GetType());

                INodeLogger nodeLogger = logger as INodeLogger;
                if (nodeLogger != null)
                {
                    nodeLogger.Initialize(sourceForLogger, _maxCPUCount);
                }
                else
                {
                    logger.Initialize(sourceForLogger);
                }
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e) && e is not LoggerException)
            {
                InternalLoggerException.Throw(e, null, "FatalErrorWhileInitializingLogger", true, logger.GetType().Name);
            }

            // Update the minimum guaranteed message importance based on the newly added logger.
            UpdateMinimumMessageImportance(logger);

            // Keep track of the loggers so they can be unregistered later on
            _loggers.Add(logger);
        }

        /// <summary>
        /// Updates <see cref="_minimumRequiredMessageImportance"/> based on the given <paramref name="logger"/>.
        /// </summary>
        /// <param name="logger">The newly registered logger.</param>
        /// <remarks>
        /// This method contains knowledge about several logger classes used by MSBuild. The goal is to optimize common scenarios,
        /// such as building on the command line with normal or minimum verbosity. If the user registers an external custom logger,
        /// we will fall back to "minimum importance" == Low because we don't know how the logger processes messages, therefore we
        /// must feed it everything.
        /// </remarks>
        private void UpdateMinimumMessageImportance(ILogger logger)
        {
            var innerLogger = (logger is ProjectCollection.ReusableLogger reusableLogger) ? reusableLogger.OriginalLogger : logger;

            MessageImportance? minimumImportance = innerLogger switch
            {
                Build.Logging.ConsoleLogger consoleLogger => consoleLogger.GetMinimumMessageImportance(),
                Build.Logging.ConfigurableForwardingLogger forwardingLogger => forwardingLogger.GetMinimumMessageImportance(),

                // The BuildCheck connector logger consumes only high priority messages.
                BuildCheckForwardingLogger => MessageImportance.High,
                BuildCheckConnectorLogger => MessageImportance.High,

                // If this is not an OutOfProc node, we can ignore the central forwarding logger, because we'll be
                // setting the message importance to accurate level based on the exact needs of the central logger.
                // That will happen in separate call to this method, with the central logger itself. This is because in the
                // inproc case, we register the central loggers (that are the destination for the logging),
                // wherease in the out-of-proc case, we register the forwarding loggers only - and those need to make sure
                // to forward at minimum what is required by the central logger.
                CentralForwardingLogger => (_nodeId > 1 ? MessageImportance.Low : null),

                // The null logger has no effect on minimum verbosity.
                Execution.BuildManager.NullLogger => null,

                // The terminal logger consumes only high priority messages.
                _ => innerLogger.GetType().FullName == "Microsoft.Build.Logging.TerminalLogger.TerminalLogger"
                    ? MessageImportance.High
                    // If the logger is not on our allow list, there are no importance guarantees. Fall back to "any importance".
                    : MessageImportance.Low,
            };

            if (minimumImportance != null)
            {
                if (_minimumRequiredMessageImportance == null)
                {
                    _minimumRequiredMessageImportance = minimumImportance;
                }
                else
                {
                    int newMinImportance = Math.Max((int)_minimumRequiredMessageImportance, (int)minimumImportance);
                    _minimumRequiredMessageImportance = (MessageImportance)newMinImportance;
                }
            }
        }

        /// <summary>
        /// When an exception is raised in the logging thread, we do not want the application to terminate right away.
        /// Whidbey and orcas msbuild have the logger exceptions occurring on the engine thread so that the host can
        /// catch and deal with these exceptions as they may occur somewhat frequently due to user generated loggers.
        /// This method will raise the exception on a delegate to which the engine is registered to. This delegate will
        /// send the exception to the engine so that it can be raised on the engine thread.
        /// </summary>
        /// <param name="ex">Exception to raise to event handlers</param>
        private void RaiseLoggingExceptionEvent(Exception ex)
        {
            OnLoggingThreadException?.Invoke(ex);
        }

        /// <summary>
        /// Raise the project started event, if necessary.
        /// </summary>
        private void TryRaiseProjectStartedEvent(BuildEventArgs args)
        {
            ProjectStartedEventHandler eventHandler = OnProjectStarted;

            if (eventHandler != null)
            {
                ProjectStartedEventArgs startedEventArgs = args as ProjectStartedEventArgs;
                if (startedEventArgs != null)
                {
                    eventHandler(this, startedEventArgs);
                }
            }
        }

        /// <summary>
        /// Raise the project finished event, if necessary.
        /// </summary>
        private void TryRaiseProjectFinishedEvent(BuildEventArgs args)
        {
            ProjectFinishedEventHandler eventHandler = OnProjectFinished;

            if (eventHandler != null)
            {
                ProjectFinishedEventArgs finishedEventArgs = args as ProjectFinishedEventArgs;
                if (finishedEventArgs != null)
                {
                    eventHandler(this, finishedEventArgs);
                }
            }
        }

        /// <summary>
        /// Get the project name from a context ID. Throw an exception if it's not found.
        /// </summary>
        private string GetAndVerifyProjectFileFromContext(BuildEventArgs eventArgs, bool allowCacheMiss)
        {
            BuildEventContext context = eventArgs.BuildEventContext!;
            _projectFileMap.TryGetValue(context.ProjectContextId, out string projectFile);

            // PERF: Not using VerifyThrow to avoid boxing an int in the non-error case.
            if (projectFile == null && !allowCacheMiss)
            {
                ErrorUtilities.ThrowInternalError(
                    "ContextID {0} should have been in the ID-to-project file mapping but wasn't! Encountered during logging message: '{1}'",
                    context.ProjectContextId, eventArgs.Message);
            }

            return projectFile;
        }

        /// <summary>
        /// Determines if the specified warning should be treated as a low importance message.
        /// </summary>
        /// <param name="warningEvent">A <see cref="BuildWarningEventArgs"/> that specifies the warning.</param>
        /// <returns><code>true</code> if the warning should be treated as a low importance message, otherwise <code>false</code>.</returns>
        private bool ShouldTreatWarningAsMessage(BuildWarningEventArgs warningEvent)
        {
            // This only applies if the user specified /nowarn at the command-line or added the warning code through the object model
            if (WarningsAsMessages?.Contains(warningEvent.Code) == true)
            {
                return true;
            }

            // This only applies if the user specified <MSBuildWarningsAsMessages /> and there is a valid ProjectInstanceId
            if (_warningsAsMessagesByProject != null && warningEvent.BuildEventContext != null && warningEvent.BuildEventContext.ProjectInstanceId != BuildEventContext.InvalidProjectInstanceId)
            {
                if (_warningsAsMessagesByProject.TryGetValue(GetWarningsConfigKey(warningEvent), out ISet<string> codesByProject))
                {
                    return codesByProject?.Contains(warningEvent.Code) == true;
                }
            }

            return false;
        }

        private bool WarningAsErrorNotOverriden(BuildWarningEventArgs warningEvent)
        {
            WarningsConfigKey key = GetWarningsConfigKey(warningEvent);

            return WarningsNotAsErrors?.Contains(warningEvent.Code) != true && !(_warningsNotAsErrorsByProject?.TryGetValue(key, out ISet<string> notToError) == true && notToError.Contains(warningEvent.Code));
        }

        /// <summary>
        /// Determines if the specified warning should be treated as an error.
        /// </summary>
        /// <param name="warningEvent">A <see cref="BuildWarningEventArgs"/> that specifies the warning.</param>
        /// <returns><code>true</code> if the warning should be treated as an error, otherwise <code>false</code>.</returns>
        private bool ShouldTreatWarningAsError(BuildWarningEventArgs warningEvent)
        {
            // This only applies if the user specified /warnaserror from the command-line or added an empty set through the object model
            if (WarningsAsErrors != null)
            {
                // Global warnings as errors apply to all projects.  If the list is empty or contains the code, the warning should be treated as an error
                if ((WarningsAsErrors.Count == 0 && WarningAsErrorNotOverriden(warningEvent)) || WarningsAsErrors.Contains(warningEvent.Code))
                {
                    return true;
                }
            }

            // This only applies if the user specified <MSBuildTreatWarningsAsErrors>true</MSBuildTreatWarningsAsErrors or <MSBuildWarningsAsErrors />
            // and there is a valid ProjectInstanceId for the warning.
            if (_warningsAsErrorsByProject != null && warningEvent.BuildEventContext != null && warningEvent.BuildEventContext.ProjectInstanceId != BuildEventContext.InvalidProjectInstanceId)
            {
                // Attempt to get the list of warnings to treat as errors for the current project
                WarningsConfigKey key = GetWarningsConfigKey(warningEvent);
                if (_warningsAsErrorsByProject.TryGetValue(key, out ISet<string> codesByProject))
                {
                    // We create an empty set if all warnings should be treated as errors so that should be checked first.
                    // If the set is not empty, check the specific code.
                    ISet<string> codesToIgnoreByProject = null;
                    _warningsNotAsErrorsByProject?.TryGetValue(key, out codesToIgnoreByProject);
                    return codesByProject != null &&
                        ((codesByProject.Count == 0 && (codesToIgnoreByProject is null || !codesToIgnoreByProject.Contains(warningEvent.Code)))
                        || codesByProject.Contains(warningEvent.Code));
                }
            }

            return false;
        }
        #endregion
        #endregion
    }
}
