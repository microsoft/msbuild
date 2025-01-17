// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Shared;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using LoggerDescription = Microsoft.Build.Logging.LoggerDescription;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.BackEnd.Logging
{
    #region Delegates
    /// <summary>
    /// Delegate for an event which will take an exception and raise it on the registered event handlers.
    /// </summary>
    /// <param name="e">Exception to be raised with registered event handlers</param>
    internal delegate void LoggingExceptionDelegate(Exception e);
    #endregion

    /// <summary>
    /// Interface representing logging services in the build system.
    /// Implementations should be thread-safe.
    /// </summary>
    internal interface ILoggingService : IBuildComponent, IBuildEngineDataRouter
    {
        #region Events
        /// <summary>
        /// When there is an exception on the logging thread, we do not want to throw the exception from there
        /// instead we would like the exception to be thrown on the engine thread as this is where hosts expect
        /// to see the exception. This event will transport the exception from the loggingService to the engine
        /// which will register on this event.
        /// </summary>
        event LoggingExceptionDelegate OnLoggingThreadException;

        /// <summary>
        /// Raised when a ProjectStarted event is about to be sent to the loggers.
        /// </summary>
        event ProjectStartedEventHandler OnProjectStarted;

        /// <summary>
        /// Raised when a ProjectFinished event has just been sent to the loggers.
        /// </summary>
        event ProjectFinishedEventHandler OnProjectFinished;

        #endregion

        #region Properties

        /// <summary>
        /// Router of the build engine runtime execution information.
        /// </summary>
        IBuildEngineDataRouter BuildEngineDataRouter { get; }

        /// <summary>
        /// Provide the current state of the loggingService.
        /// Is it Inistantiated
        /// Has it been Initialized
        /// Is it starting to shutdown
        /// Has it shutdown
        /// </summary>
        LoggingServiceState ServiceState
        {
            get;
        }

        /// <summary>
        /// Returns the synchronous/asynchronous mode for the logging service.
        /// </summary>
        LoggerMode LoggingMode
        {
            get;
        }

        /// <summary>
        /// When true, only log critical events such as warnings and errors. Has to be in here for API compat
        /// </summary>
        bool OnlyLogCriticalEvents
        {
            get;
            set;
        }

        /// <summary>
        /// Number of nodes in the system when it was initially started
        /// </summary>
        int MaxCPUCount
        {
            get;
            set;
        }

        /// <summary>
        /// Enumerator over all registered loggers.
        /// </summary>
        ICollection<ILogger> Loggers
        {
            get;
        }

        /// <summary>
        /// The list of descriptions which describe how to create forwarding loggers on a node.
        /// This is used by the node provider to get a list of registered descriptions so that
        /// they can be transmitted to child nodes.
        /// </summary>
        ICollection<LoggerDescription> LoggerDescriptions
        {
            get;
        }

        /// <summary>
        /// Return an array which contains the logger type names
        /// this can be used to display which loggers are registered on the node
        /// </summary>
        ICollection<string> RegisteredLoggerTypeNames
        {
            get;
        }

        /// <summary>
        /// Return an array which contains the sink names
        /// this can be used to display which sinks are on the node
        /// </summary>
        ICollection<string> RegisteredSinkNames
        {
            get;
        }

        /// <summary>
        /// List of properties to serialize from the child node
        /// </summary>
        string[] PropertiesToSerialize
        {
            get;
            set;
        }

        /// <summary>
        /// Should all properties be serialized from the child to the parent process
        /// </summary>
        bool SerializeAllProperties
        {
            get;
            set;
        }

        /// <summary>
        /// Is the logging running on a remote node
        /// </summary>
        bool RunningOnRemoteNode
        {
            get;
            set;
        }

        /// <summary>
        /// Set of warnings to treat as errors.  An empty non-null set will treat all warnings as errors.
        /// </summary>
        ISet<string> WarningsAsErrors
        {
            get;
            set;
        }

        /// <summary>
        /// Set of warnings to not treat as errors. Only has any effect if WarningsAsErrors is non-null but empty.
        /// </summary>
        ISet<string> WarningsNotAsErrors
        {
            get;
            set;
        }

        /// <summary>
        /// A list of warnings to treat as low importance messages.
        /// </summary>
        ISet<string> WarningsAsMessages
        {
            get;
            set;
        }

        /// <summary>
        /// Should evaluation events include generated metaprojects?
        /// </summary>
        bool IncludeEvaluationMetaprojects
        {
            get;
            set;
        }

        /// <summary>
        /// Should evaluation events include profiling information?
        /// </summary>
        bool IncludeEvaluationProfile
        {
            get;
            set;
        }

        /// <summary>
        /// Should properties and items be logged on <see cref="ProjectEvaluationFinishedEventArgs"/>
        /// or/and <see cref="ProjectStartedEventArgs"/>?
        /// </summary>
        void SetIncludeEvaluationPropertiesAndItemsInEvents(bool inProjectStartedEvent, bool inEvaluationFinishedEvent);

        /// <summary>
        /// Indicates whether properties and items should be logged on <see cref="ProjectStartedEventArgs"/>.
        /// </summary>
        bool IncludeEvaluationPropertiesAndItemsInProjectStartedEvent
        {
            get;
        }

        /// <summary>
        /// Indicates whether properties and items should be logged on <see cref="ProjectEvaluationFinishedEventArgs"/>.
        /// </summary>
        bool IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent
        {
            get;
        }

        /// <summary>
        /// Should task events include task inputs?
        /// </summary>
        bool IncludeTaskInputs
        {
            get;
            set;
        }

        /// <summary>
        /// Returns the minimum logging importance that must be logged because there is a possibility that
        /// at least one registered logger consumes it.
        /// </summary>
        MessageImportance MinimumRequiredMessageImportance
        {
            get;
        }

        #endregion

        /// <summary>
        /// Adds a set of warning codes to treat as low importance messages for the specified project instance ID.
        /// </summary>
        /// <param name="buildEventContext">A <see cref="BuildEventContext"/> to associate with the list of warning codes.</param>
        /// <param name="codes">The list of warning codes to treat as low importance messsages.</param>
        void AddWarningsAsMessages(BuildEventContext buildEventContext, ISet<string> codes);

        /// <summary>
        /// Adds a set of warning codes to treat as errors for the specified project instance ID.
        /// </summary>
        /// <param name="buildEventContext">A <see cref="BuildEventContext"/> to associate with the list of warning codes.</param>
        /// <param name="codes">The list of warning codes to treat as errors.</param>
        void AddWarningsAsErrors(BuildEventContext buildEventContext, ISet<string> codes);

        /// <summary>
        /// Adds a set of warning codes to not treat as errors for the specified project instance ID.
        /// </summary>
        /// <param name="buildEventContext">A <see cref="BuildEventContext"/> to associate with the list of warning codes.</param>
        /// <param name="codes">The list of warning codes not to treat as errors.</param>
        void AddWarningsNotAsErrors(BuildEventContext buildEventContext, ISet<string> codes);

        /// <summary>
        /// Determines if the specified submission has logged an errors.
        /// </summary>
        /// <param name="submissionId">The ID of the build submission.  A value of "0" means that an error was logged outside of any build submission.</param>
        /// <returns><code>true</code> if the build submission logged an errors, otherwise <code>false</code>.</returns>
        bool HasBuildSubmissionLoggedErrors(int submissionId);

        /// <summary>
        /// Get the warnings that will be promoted to errors for the specified context.
        /// </summary>
        /// <param name="context">The build context through which warnings will be logged as errors.</param>
        /// <returns>A collection of warning codes that should be treated as errors.</returns>
        ICollection<string> GetWarningsAsErrors(BuildEventContext context);

        /// <summary>
        /// Get the warnings that will not be promoted to error for the specified context.
        /// </summary>
        /// <param name="context">The build context through which warnings will not be logged as errors.</param>
        /// <returns>A collection of warning codes that should not be treated as errors.</returns>
        ICollection<string> GetWarningsNotAsErrors(BuildEventContext context);

        /// <summary>
        /// Get the warnings that will be demoted to messages for the specified context.
        /// </summary>
        /// <param name="context">The build context through which warnings will be logged as errors.</param>
        /// <returns>A collection of warning codes that should be treated as messages.</returns>
        ICollection<string> GetWarningsAsMessages(BuildEventContext context);

        #region Register

        /// <summary>
        /// Allows the registering of an ICentralLogger and a forwarding logger pair
        /// </summary>
        /// <param name="centralLogger">Central logger which is to receive the events created by the forwarding logger</param>
        /// <param name="forwardingLogger">A description of the forwarding logger</param>
        /// <returns value="bool">True if the central and forwarding loggers were registered. False if the central logger or the forwarding logger were already registered</returns>
        bool RegisterDistributedLogger(ILogger centralLogger, LoggerDescription forwardingLogger);

        /// <summary>
        /// Register an logger which expects all logging events from the system
        /// </summary>
        /// <param name="logger">The logger to register.</param>
        /// <returns value="bool">True if the central was registered. False if the central logger was already registered</returns>
        bool RegisterLogger(ILogger logger);

        /// <summary>
        /// Clear out all registered loggers so that none are registered.
        /// </summary>
        void UnregisterAllLoggers();

        /// <summary>
        /// In order to setup the forwarding loggers on a node, we need to take in the logger descriptions and initialize them.
        /// The method will create a forwarding logger, an eventRedirector which will redirect all forwarded messages to the forwardingLoggerSink.
        /// All forwarding loggers will use the same forwardingLoggerSink.
        /// </summary>
        /// <param name="loggerDescriptions">Collection of logger descriptions which we would like to use to create a set of forwarding loggers on a node</param>
        /// <param name="forwardingLoggerSink">The buildEventSink which the fowarding loggers will forward their events to</param>
        /// <param name="nodeId">The id of the node the logging services is on</param>
        /// <exception cref="ArgumentNullException">When forwardingLoggerSink is null</exception>
        /// <exception cref="ArgumentNullException">When loggerDescriptions is null</exception>
        void InitializeNodeLoggers(ICollection<LoggerDescription> loggerDescriptions, IBuildEventSink forwardingLoggerSink, int nodeId);

        #endregion

        #region Log comments
        /// <summary>
        ///  Helper method to create a message build event from a string resource and some parameters
        /// </summary>
        /// <param name="buildEventContext">Event context which describes where in the build the message came from</param>
        /// <param name="importance">Importance level of the message</param>
        /// <param name="messageResourceName">string within the resource which indicates the format string to use</param>
        /// <param name="messageArgs">string resource arguments</param>
        void LogComment(BuildEventContext buildEventContext, MessageImportance importance, string messageResourceName, params object[] messageArgs);

        /// <summary>
        /// Helper method to create a message build event from a string
        /// </summary>
        /// <param name="buildEventContext">Event context which describes where in the build the message came from</param>
        /// <param name="importance">Importance level of the message</param>
        /// <param name="message">message to log</param>
        void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message);

        /// <summary>
        /// Log a comment from a format string and arguments
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="importance">How important is the message, this will determine which verbosities the message will show up on.
        /// The higher the importance the lower the verbosity needs to be for the message to be seen</param>
        /// <param name="message">Message to log</param>
        /// <param name="messageArgs">Message formatting arguments</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        /// <exception cref="InternalErrorException">Message is null</exception>
        void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message, params object[] messageArgs);
        #endregion

        #region Log events
        /// <summary>
        /// Will Log a build Event. Will also take into account OnlyLogCriticalEvents when determining if to drop the event or to log it.
        /// </summary>
        void LogBuildEvent(BuildEventArgs buildEvent);

        #endregion

        #region Log errors
        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="buildEventContext">The event context information as to where the error occurred </param>
        /// <param name="file">The file in which the error occurred</param>
        /// <param name="messageResourceName">The resource name for the error</param>
        /// <param name="messageArgs">Parameters for the resource string</param>
        void LogError(BuildEventContext buildEventContext, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs);

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="subcategoryResourceName">The resource name which indicates the subCategory</param>
        /// <param name="file">The file in which the error occurred</param>
        /// <param name="messageResourceName">The resource name for the error</param>
        /// <param name="messageArgs">Parameters for the resource string</param>
        void LogError(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs);

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="subcategoryResourceName">The resource name which indicates the subCategory</param>
        /// <param name="errorCode"> Error code</param>
        /// <param name="helpKeyword">Help keyword</param>
        /// <param name="file">The file in which the error occurred</param>
        /// <param name="message">Error message</param>
        void LogErrorFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string errorCode, string helpKeyword, BuildEventFileInfo file, string message);

        /// <summary>
        /// Log an invalid project file exception
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="invalidProjectFileException">The invalid Project File Exception which is to be logged</param>
        void LogInvalidProjectFileError(BuildEventContext buildEventContext, InvalidProjectFileException invalidProjectFileException);

        /// <summary>
        /// Log an error based on an exception
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="exception">The exception wich is to be logged</param>
        /// <param name="file">The file in which the error occurred</param>
        void LogFatalBuildError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file);

        /// <summary>
        /// Log an error based on an exception during the execution of a task
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="exception">The exception wich is to be logged</param>
        /// <param name="file">The file in which the error occurred</param>
        /// <param name="taskName">The task in which the error occurred</param>
        void LogFatalTaskError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName);

        /// <summary>
        /// Log an error based on an exception
        /// </summary>
        /// <param name="buildEventContext">The event context for where the error occurred</param>
        /// <param name="exception">The exception wich is to be logged</param>
        /// <param name="file">The file in which the error occurred</param>
        /// <param name="messageResourceName">The string resource which has the formatting string for the error</param>
        /// <param name="messageArgs">The arguments for the error message</param>
        void LogFatalError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs);
        #endregion

        #region Log warnings
        /// <summary>
        /// Log a warning based on an exception
        /// </summary>
        /// <param name="buildEventContext">The event context for where the warning occurred</param>
        /// <param name="exception">The exception to be logged as a warning</param>
        /// <param name="file">The file in which the warning occurred</param>
        /// <param name="taskName">The task in which the warning occurred</param>
        void LogTaskWarningFromException(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName);

        /// <summary>
        /// Log a warning
        /// </summary>
        /// <param name="buildEventContext">The event context for where the warning occurred</param>
        /// <param name="subcategoryResourceName">The subcategory resource name</param>
        /// <param name="file">The file in which the warning occurred</param>
        /// <param name="messageResourceName">The string resource which contains the formatted warning string</param>
        /// <param name="messageArgs">parameters for the string resource</param>
        void LogWarning(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs);

        /// <summary>
        /// Log a warning based on a text message
        /// </summary>
        /// <param name="buildEventContext">The event context for where the warning occurred</param>
        /// <param name="subcategoryResourceName">The subcategory resource name</param>
        /// <param name="warningCode"> Warning code</param>
        /// <param name="helpKeyword"> Help keyword</param>
        /// <param name="file">The file in which the warning occurred</param>
        /// <param name="message">The message to be logged as a warning</param>
        void LogWarningFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string warningCode, string helpKeyword, BuildEventFileInfo file, string message);
        #endregion

        #region Log status
        /// <summary>
        /// Log the start of the build
        /// </summary>
        void LogBuildStarted();

        /// <summary>
        /// Log the completion of a build
        /// </summary>
        /// <param name="success">Did the build succeed or not</param>
        void LogBuildFinished(bool success);

        /// <summary>
        /// Logs that the build has canceled
        /// </summary>
        void LogBuildCanceled();

        /// <summary>
        /// Create an evaluation context, by generating a new evaluation id.
        /// </summary>
        /// <param name="nodeId">The node id</param>
        /// <param name="submissionId">The submission id</param>
        /// <returns></returns>
        BuildEventContext CreateEvaluationBuildEventContext(int nodeId, int submissionId);

        /// <summary>
        /// Create a project cache context, by generating a new project context id.
        /// </summary>
        /// <param name="submissionId">The submission id</param>
        /// <param name="evaluationId">The evaluation id</param>
        /// <param name="projectInstanceId">The project instance id</param>
        /// <param name="projectFile">Project file being built</param>
        /// <returns></returns>
        BuildEventContext CreateProjectCacheBuildEventContext(int submissionId, int evaluationId, int projectInstanceId, string projectFile);

        /// <summary>
        /// Logs that a project evaluation has started
        /// </summary>
        /// <param name="eventContext">The event context to use for logging</param>
        /// <param name="projectFile">Project file being built</param>
        /// <returns>The evaluation event context for the project.</returns>
        void LogProjectEvaluationStarted(BuildEventContext eventContext, string projectFile);

        /// <summary>
        /// Logs that a project evaluation has finished
        /// </summary>
        /// <param name="projectEvaluationEventContext">Event context for the project.</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="globalProperties">Global properties used for the evaluation.</param>
        /// <param name="properties">Properties produced by the evaluation.</param>
        /// <param name="items">Items produced by the evaluation.</param>
        /// <param name="profilerResult">Profiler results if evaluation profiling was enabled.</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        void LogProjectEvaluationFinished(
            BuildEventContext projectEvaluationEventContext,
            string projectFile,
            IEnumerable globalProperties,
            IEnumerable properties,
            IEnumerable items,
            ProfilerResult? profilerResult);

        /// <summary>
        /// Log that a project has started
        /// </summary>
        /// <param name="nodeBuildEventContext">The logging context of the node which is building this project.</param>
        /// <param name="submissionId">The id of the build submission.</param>
        /// <param name="configurationId">The id of the project configuration which is about to start</param>
        /// <param name="parentBuildEventContext">The build context of the parent project which asked this project to build</param>
        /// <param name="projectFile">The project file path of the project about to be built</param>
        /// <param name="targetNames">The entrypoint target names for this project</param>
        /// <param name="properties">The initial properties of the project</param>
        /// <param name="items">The initial items of the project</param>
        /// <param name="evaluationId">EvaluationId of the project instance</param>
        /// <param name="projectContextId">The project context id</param>
        /// <returns>The BuildEventContext to use for this project.</returns>
        BuildEventContext LogProjectStarted(
            BuildEventContext nodeBuildEventContext,
            int submissionId,
            int configurationId,
            BuildEventContext parentBuildEventContext,
            string projectFile,
            string targetNames,
            IEnumerable<DictionaryEntry> properties,
            IEnumerable<DictionaryEntry> items,
            int evaluationId = BuildEventContext.InvalidEvaluationId,
            int projectContextId = BuildEventContext.InvalidProjectContextId);

        void LogProjectStarted(ProjectStartedEventArgs args);

        ProjectStartedEventArgs CreateProjectStarted(
            BuildEventContext nodeBuildEventContext,
            int submissionId,
            int configurationId,
            BuildEventContext parentBuildEventContext,
            string projectFile,
            string targetNames,
            IEnumerable<DictionaryEntry> properties,
            IEnumerable<DictionaryEntry> items,
            int evaluationId = BuildEventContext.InvalidEvaluationId,
            int projectContextId = BuildEventContext.InvalidProjectContextId);

        /// <summary>
        /// Log that the project has finished
        /// </summary>
        /// <param name="projectBuildEventContext">The build context of the project which has just finished</param>
        /// <param name="projectFile">The path to the projec file which was just built</param>
        /// <param name="success">Did the build succeede or not</param>
        void LogProjectFinished(BuildEventContext projectBuildEventContext, string projectFile, bool success);

        void LogProjectFinished(BuildEventContext projectBuildEventContext, ProjectBuildStats projectBuildStats, string projectFile, bool success);

        /// <summary>
        /// Log that a target has started
        /// </summary>
        /// <param name="projectBuildEventContext">The build event context of the project spawning this target.</param>
        /// <param name="targetName">The name of the target which is about to start</param>
        /// <param name="projectFile">The project file which is being built</param>
        /// <param name="projectFileOfTargetElement">The file in which the target is defined - typically a .targets file</param>
        /// <param name="parentTargetName">The name of the parent target.</param>
        /// <param name="buildReason">The reason the parent target built the target.</param>
        /// <returns>The target build event context</returns>
        BuildEventContext LogTargetStarted(BuildEventContext projectBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, string parentTargetName, TargetBuiltReason buildReason);

        /// <summary>
        /// Log that a target has finished
        /// </summary>
        /// <param name="targetBuildEventContext">The event context of the target which has just completed</param>
        /// <param name="targetName">The name of the target which has just completed</param>
        /// <param name="projectFile">The project file which the target was being run in</param>
        /// <param name="projectFileOfTargetElement">The file in which the target is defined - typically a .targets file</param>
        /// <param name="success">Did the target finish successfully or not</param>
        /// <param name="targetOutputs">List of target outputs for the target, right now this is for all batches and only is on the last target finished event</param>
        void LogTargetFinished(BuildEventContext targetBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, bool success, IEnumerable<TaskItem> targetOutputs);

        /// <summary>
        /// Log that a task is about to start
        /// </summary>
        /// <param name="taskBuildEventContext">The event context of the task.</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file which is being built</param>
        /// <param name="projectFileOfTaskNode">The file in which the task is defined - typically a .targets file</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        void LogTaskStarted(BuildEventContext taskBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, string taskAssemblyLocation);

        /// <summary>
        /// Log that a task is about to start
        /// </summary>
        /// <param name="targetBuildEventContext">The event context of the target which is spawning this task.</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file which is being built</param>
        /// <param name="projectFileOfTaskNode">The file in which the task is defined - typically a .targets file</param>
        /// <param name="line">The line number in the file where the task invocation is located.</param>
        /// <param name="column">The column number in the file where the task invocation is located.</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        /// <returns>The task build event context</returns>
        BuildEventContext LogTaskStarted2(BuildEventContext targetBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, int line, int column, string taskAssemblyLocation);

        /// <summary>
        /// Log that a task has just completed
        /// </summary>
        /// <param name="taskBuildEventContext">The event context of the task which has just finished</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file which is being built</param>
        /// <param name="projectFileOfTaskNode">The file in which the task is defined - typically a .targets file</param>
        /// <param name="success">True of the task finished successfully, false otherwise.</param>
        void LogTaskFinished(BuildEventContext taskBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, bool success);
        #endregion

        #region Log telemetry
        /// <summary>
        /// Logs telemetry.
        /// </summary>
        /// <param name="buildEventContext">The event context of the task which sent the telemetry.</param>
        /// <param name="eventName">The event name.</param>
        /// <param name="properties">The list of properties associated with the event.</param>
        void LogTelemetry(BuildEventContext buildEventContext, string eventName, IDictionary<string, string> properties);
        #endregion

        #region Log response files
        /// <summary>
        /// Helper method to create an event for including files. Typically response files
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes where is the event getting logged</param>
        /// <param name="filePath">Full path to the response file</param>
        void LogIncludeFile(BuildEventContext buildEventContext, string filePath);
        #endregion
    }

    /// <summary>
    /// Acts as an endpoint for a buildEventArg. The objects which implement this interface are intended to consume the BuildEventArg.
    /// </summary>
    internal interface IBuildEventSink
    {
        #region Properties
        /// <summary>
        /// Provide a the sink a friendly name which can be used to distinguish sinks in memory
        /// and for display
        /// </summary>
        string Name
        {
            get;
            set;
        }

        /// <summary>
        /// Has the sink logged the BuildStartedEvent. This is important to know because we only want to log the build started event once
        /// </summary>
        bool HaveLoggedBuildStartedEvent
        {
            get;
            set;
        }

        /// <summary>
        /// Has the sink logged the BuildFinishedEvent. This is important to know because we only want to log the build finished event once
        /// </summary>
        bool HaveLoggedBuildFinishedEvent
        {
            get;
            set;
        }

        #endregion
        /// <summary>
        /// Entry point for a sink to consume an event.
        /// </summary>
        /// <param name="buildEvent">The event to be consumed by the sink.</param>
        /// <param name="sinkId"> Sink where the message should go to, this is really only used for the transport sink</param>
        void Consume(BuildEventArgs buildEvent, int sinkId);

        /// <summary>
        /// Entry point for a sink to consume an event.
        /// </summary>
        void Consume(BuildEventArgs buildEvent);

        /// <summary>
        /// Shuts down the sink and any resources it may be holding
        /// </summary>
        void ShutDown();
    }
}
