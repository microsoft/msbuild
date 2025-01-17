// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// A class providing a mock implementation of ILoggingService.
    /// </summary>
    internal class MockLoggingService : ILoggingService
    {
        private Action<string> _writer;

        public MockLoggingService(Action<string> writter = null)
        {
            _writer = writter ?? Console.WriteLine;
        }

        #region ILoggingService Members

        /// <summary>
        /// Router of the build engine runtime execution information.
        /// </summary>
        public IBuildEngineDataRouter BuildEngineDataRouter => this;

        /// <summary>
        /// The event to raise when there is a logging exception
        /// </summary>
        public event LoggingExceptionDelegate OnLoggingThreadException;

        /// <summary>
        /// The event to raise when ProjectStarted is processed.
        /// </summary>
        public event ProjectStartedEventHandler OnProjectStarted;

        /// <summary>
        /// The event to raise when ProjectFinished is processed
        /// </summary>
        public event ProjectFinishedEventHandler OnProjectFinished;

        /// <summary>
        /// Enumerator over all registered loggers.
        /// </summary>
        public ICollection<ILogger> Loggers
        {
            get { throw new NotImplementedException(); }
        }

        /// <summary>
        /// The logging service state
        /// </summary>
        public LoggingServiceState ServiceState
        {
            get
            {
                OnLoggingThreadException(null);
                OnProjectStarted(null, null);
                OnProjectFinished(null, null);
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// The logging mode.
        /// </summary>
        public LoggerMode LoggingMode
        {
            get
            {
                return LoggerMode.Synchronous;
            }
        }

        /// <summary>
        /// Whether to log critical events
        /// </summary>
        public bool OnlyLogCriticalEvents
        {
            get
            {
                return false;
            }

            set
            {
            }
        }

        /// <summary>
        /// Returns the number of initial nodes.
        /// </summary>
        public int MaxCPUCount
        {
            get
            {
                throw new NotImplementedException();
            }

            set
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Gets the logger descriptions
        /// </summary>
        public ICollection<LoggerDescription> LoggerDescriptions
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Gets the registered logger type names.
        /// </summary>
        public ICollection<string> RegisteredLoggerTypeNames
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Retrieves the registered sink names.
        /// </summary>
        public ICollection<string> RegisteredSinkNames
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Properties to serialize from the child node to the parent node
        /// </summary>
        public string[] PropertiesToSerialize
        {
            get;
            set;
        }

        /// <summary>
        /// List of warnings to treat as errors.
        /// </summary>
        public ISet<string> WarningsAsErrors
        {
            get;
            set;
        }

        /// <summary>
        /// List of warnings to not treat as errors.
        /// </summary>
        public ISet<string> WarningsNotAsErrors
        {
            get;
            set;
        }

        /// <summary>
        /// List of warnings to treat as low importance messages.
        /// </summary>
        public ISet<string> WarningsAsMessages
        {
            get;
            set;
        }

        /// <summary>
        /// Is the logging service on a remote node, this is used to determine if properties need to be serialized
        /// </summary>
        public bool RunningOnRemoteNode
        {
            get;
            set;
        }

        /// <summary>
        /// Should all properties be serialized from the child to the parent process
        /// </summary>
        public bool SerializeAllProperties
        {
            get;
            set;
        }

        /// <summary>
        /// Should evaluation events include generated metaprojects?
        /// </summary>
        public bool IncludeEvaluationMetaprojects
        {
            get => false;
            set { }
        }

        /// <summary>
        /// Should evaluation events include profiling information?
        /// </summary>
        public bool IncludeEvaluationProfile
        {
            get => false;
            set { }
        }

        /// <inheritdoc cref="ILoggingService.SetIncludeEvaluationPropertiesAndItemsInEvents"/>
        public void SetIncludeEvaluationPropertiesAndItemsInEvents(bool inProjectStartedEvent,
            bool inEvaluationFinishedEvent)
        { }

        /// <inheritdoc cref="ILoggingService.IncludeEvaluationPropertiesAndItemsInProjectStartedEvent"/>
        public bool IncludeEvaluationPropertiesAndItemsInProjectStartedEvent
        {
            get => false;
        }

        /// <inheritdoc cref="ILoggingService.IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent"/>
        public bool IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent
        {
            get => false;
        }

        /// <summary>
        /// Should task events include task inputs?
        /// </summary>
        public bool IncludeTaskInputs
        {
            get => false;
            set { }
        }

        public MessageImportance MinimumRequiredMessageImportance
        {
            get => MessageImportance.Low;
        }

        public void AddWarningsAsMessages(BuildEventContext buildEventContext, ISet<string> codes)
        {
            throw new NotImplementedException();
        }

        public void AddWarningsAsErrors(BuildEventContext buildEventContext, ISet<string> codes)
        {
            throw new NotImplementedException();
        }

        public void AddWarningsNotAsErrors(BuildEventContext buildEventContext, ISet<string> codes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Registers a distributed logger.
        /// </summary>
        /// <param name="centralLogger">The central logger, which resides on the build manager.</param>
        /// <param name="forwardingLogger">The forwarding logger, which resides on the node.</param>
        /// <returns>True if successful.</returns>
        public bool RegisterDistributedLogger(ILogger centralLogger, LoggerDescription forwardingLogger)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Registers a logger
        /// </summary>
        /// <param name="logger">The logger</param>
        /// <returns>True if successful.</returns>
        public bool RegisterLogger(ILogger logger)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Clear out all registered loggers so that none are registered.
        /// </summary>
        public void UnregisterAllLoggers()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Initializes the loggers on a node
        /// </summary>
        /// <param name="loggerDescriptions">The descriptions received from the Build Manager</param>
        /// <param name="forwardingLoggerSink">The sink used to transmit messages to the manager.</param>
        /// <param name="nodeId">The id of the node.</param>
        public void InitializeNodeLoggers(ICollection<LoggerDescription> loggerDescriptions, IBuildEventSink forwardingLoggerSink, int nodeId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Logs a comment based on a message resource
        /// </summary>
        /// <param name="buildEventContext">The context</param>
        /// <param name="importance">The importance</param>
        /// <param name="messageResourceName">The resource for the message</param>
        /// <param name="messageArgs">The args for the message</param>
        public void LogComment(BuildEventContext buildEventContext, MessageImportance importance, string messageResourceName, params object[] messageArgs)
        {
            _writer(messageResourceName);
            foreach (object o in messageArgs)
            {
                _writer((string)o);
            }
        }

        /// <summary>
        /// Logs a text comment
        /// </summary>
        /// <param name="buildEventContext">The context</param>
        /// <param name="importance">The importance</param>
        /// <param name="message">The message</param>
        public void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message)
        {
            _writer(message);
        }

        /// <inheritdoc />
        public void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message, params object[] messageArgs)
        {
            if (messageArgs?.Length > 0)
            {
                message = string.Format(message, messageArgs);
            }

            _writer(message);
        }

        /// <summary>
        /// Logs a pre-formed build event
        /// </summary>
        /// <param name="buildEvent">The event to log</param>
        public void LogBuildEvent(BuildEventArgs buildEvent)
        {
        }

        /// <summary>
        /// Logs an error
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="file">The file from which the error is logged</param>
        /// <param name="messageResourceName">The message resource</param>
        /// <param name="messageArgs">The message args</param>
        public void LogError(BuildEventContext buildEventContext, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            _writer(messageResourceName);
            foreach (object o in messageArgs)
            {
                _writer((string)o);
            }
        }

        /// <summary>
        /// Logs an error with a subcategory
        /// </summary>
        /// <param name="buildEventContext">The build event context</param>
        /// <param name="subcategoryResourceName">The subcategory resource</param>
        /// <param name="file">The file</param>
        /// <param name="messageResourceName">The message resource</param>
        /// <param name="messageArgs">The message args</param>
        public void LogError(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            _writer(messageResourceName);
            foreach (object o in messageArgs)
            {
                _writer((string)o);
            }
        }

        /// <summary>
        /// Logs a text error
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="subcategoryResourceName">The subcategory resource</param>
        /// <param name="errorCode">The error code</param>
        /// <param name="helpKeyword">A help keyword</param>
        /// <param name="file">The file</param>
        /// <param name="message">The message</param>
        public void LogErrorFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string errorCode, string helpKeyword, BuildEventFileInfo file, string message)
        {
            _writer(message);
        }

        /// <summary>
        /// Logs an invalid project file error
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="invalidProjectFileException">The exception</param>
        public void LogInvalidProjectFileError(BuildEventContext buildEventContext, InvalidProjectFileException invalidProjectFileException)
        {
        }

        /// <summary>
        /// Logs a fatal build error
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="exception">The exception</param>
        /// <param name="file">The file</param>
        public void LogFatalBuildError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file)
        {
            _writer(exception.Message);
        }

        /// <summary>
        /// Logs a fatal task error
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="exception">The exception</param>
        /// <param name="file">The file</param>
        /// <param name="taskName">The name of the task</param>
        public void LogFatalTaskError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName)
        {
        }

        /// <summary>
        /// Logs a generic fatal error
        /// </summary>
        /// <param name="buildEventContext">The build context</param>
        /// <param name="exception">The exception</param>
        /// <param name="file">The file</param>
        /// <param name="messageResourceName">The message resource</param>
        /// <param name="messageArgs">The message args</param>
        public void LogFatalError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
        }

        /// <summary>
        /// Logs a task warning
        /// </summary>
        /// <param name="buildEventContext">The build context</param>
        /// <param name="exception">The exception</param>
        /// <param name="file">The file</param>
        /// <param name="taskName">The name of the task</param>
        public void LogTaskWarningFromException(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName)
        {
        }

        /// <summary>
        /// Logs a warning
        /// </summary>
        /// <param name="buildEventContext">The event context</param>
        /// <param name="subcategoryResourceName">The subcategory resource</param>
        /// <param name="file">The file</param>
        /// <param name="messageResourceName">The message resource</param>
        /// <param name="messageArgs">The message args</param>
        public void LogWarning(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            _writer(messageResourceName);
            foreach (object o in messageArgs)
            {
                _writer((string)o);
            }
        }

        /// <summary>
        /// Logs a text warning
        /// </summary>
        /// <param name="buildEventContext">The build context</param>
        /// <param name="subcategoryResourceName">The subcategory resource</param>
        /// <param name="warningCode">The warning code</param>
        /// <param name="helpKeyword">A help keyword</param>
        /// <param name="file">The file</param>
        /// <param name="message">The message</param>
        public void LogWarningFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string warningCode, string helpKeyword, BuildEventFileInfo file, string message)
        {
            _writer(message);
        }

        /// <summary>
        /// Logs a build started event
        /// </summary>
        public void LogBuildStarted()
        {
        }

        /// <summary>
        /// Logs a build finished event
        /// </summary>
        /// <param name="success">Set to true if the build was successful</param>
        public void LogBuildFinished(bool success)
        {
        }

        /// <inheritdoc />
        public void LogBuildCanceled()
        {
        }

        /// <inheritdoc />
        public BuildEventContext CreateEvaluationBuildEventContext(int nodeId, int submissionId)
            => new BuildEventContext(0, 0, 0, 0, 0, 0, 0);

        /// <inheritdoc />
        public BuildEventContext CreateProjectCacheBuildEventContext(int submissionId, int evaluationId, int projectInstanceId, string projectFile)
            => new BuildEventContext(0, 0, 0, 0, 0, 0, 0);

        /// <inheritdoc />
        public void LogProjectEvaluationStarted(BuildEventContext eventContext, string projectFile)
        {
        }

        /// <summary>
        /// Logs a project evaluation finished event
        /// </summary>
        public void LogProjectEvaluationFinished(
            BuildEventContext projectEvaluationEventContext,
            string projectFile,
            IEnumerable globalProperties,
            IEnumerable properties,
            IEnumerable items,
            ProfilerResult? profilerResult)
        {
        }

        /// <summary>
        /// Logs a project started event
        /// </summary>
        public BuildEventContext LogProjectStarted(
            BuildEventContext nodeBuildEventContext,
            int submissionId,
            int configurationId,
            BuildEventContext parentBuildEventContext,
            string projectFile,
            string targetNames,
            IEnumerable<DictionaryEntry> properties,
            IEnumerable<DictionaryEntry> items,
            int evaluationId = BuildEventContext.InvalidEvaluationId,
            int projectContextId = BuildEventContext.InvalidProjectContextId)
        {
            return new BuildEventContext(0, 0, 0, 0);
        }

        public void LogProjectStarted(ProjectStartedEventArgs args)
        { }

        public ProjectStartedEventArgs CreateProjectStarted(
            BuildEventContext nodeBuildEventContext,
            int submissionId,
            int configurationId,
            BuildEventContext parentBuildEventContext,
            string projectFile,
            string targetNames,
            IEnumerable<DictionaryEntry> properties,
            IEnumerable<DictionaryEntry> items,
            int evaluationId = BuildEventContext.InvalidEvaluationId,
            int projectContextId = BuildEventContext.InvalidProjectContextId)
        {
            return new ProjectStartedEventArgs(
                configurationId,
                message: null,
                helpKeyword: null,
                projectFile,
                targetNames,
                properties,
                items,
                parentBuildEventContext);
        }

        /// <summary>
        /// Logs a project finished event
        /// </summary>
        /// <param name="projectBuildEventContext">The project build event context</param>
        /// <param name="projectFile">The project filename</param>
        /// <param name="success">Whether it was successful or not.</param>
        public void LogProjectFinished(BuildEventContext projectBuildEventContext, string projectFile, bool success)
        {
        }

        public void LogProjectFinished(BuildEventContext projectBuildEventContext, ProjectBuildStats projectBuildStats, string projectFile, bool success)
        {
        }

        /// <summary>
        /// Logs a target started event
        /// </summary>
        /// <param name="projectBuildEventContext">The build event context of the project</param>
        /// <param name="targetName">The name of the target</param>
        /// <param name="projectFile">The project file</param>
        /// <param name="projectFileOfTargetElement">The project file containing the target element</param>
        /// <returns>The build event context for the target</returns>
        public BuildEventContext LogTargetStarted(BuildEventContext projectBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, string parentTargetName, TargetBuiltReason buildReason)
        {
            return new BuildEventContext(0, 0, 0, 0);
        }

        /// <summary>
        /// Logs a target finished event
        /// </summary>
        /// <param name="targetBuildEventContext">The target's build event context</param>
        /// <param name="targetName">The name of the target</param>
        /// <param name="projectFile">The project file</param>
        /// <param name="projectFileOfTargetElement">The project file containing the target element</param>
        /// <param name="success">Whether it was successful or not.</param>
        public void LogTargetFinished(BuildEventContext targetBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, bool success, IEnumerable<TaskItem> targetOutputs)
        {
        }

        /// <summary>
        /// Logs a task started event
        /// </summary>
        /// <param name="targetBuildEventContext">The target's build event context</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file</param>
        /// <param name="projectFileOfTaskNode">The project file containing the task node.</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        public void LogTaskStarted(BuildEventContext targetBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, string taskAssemblyLocation)
        {
        }

        /// <summary>
        /// Logs a task started event
        /// </summary>
        /// <param name="targetBuildEventContext">The target's build event context</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file</param>
        /// <param name="projectFileOfTaskNode">The project file containing the task node.</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        /// <returns>The task logging context</returns>
        public BuildEventContext LogTaskStarted2(BuildEventContext targetBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, int line, int column, string taskAssemblyLocation)
        {
            return new BuildEventContext(0, 0, 0, 0);
        }

        /// <summary>
        /// Logs a task finished event
        /// </summary>
        /// <param name="taskBuildEventContext">The task's build event context</param>
        /// <param name="taskName">The name of the task</param>
        /// <param name="projectFile">The project file</param>
        /// <param name="projectFileOfTaskNode">The project file of the task node</param>
        /// <param name="success">Whether the task was successful or not.</param>
        public void LogTaskFinished(BuildEventContext taskBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, bool success)
        {
        }

        /// <summary>
        /// Logs a telemetry event.
        /// </summary>
        /// <param name="buildEventContext">The task's build event context</param>
        /// <param name="eventName">The event name.</param>
        /// <param name="properties">The list of properties associated with the event.</param>
        public void LogTelemetry(BuildEventContext buildEventContext, string eventName, IDictionary<string, string> properties)
        {
        }

        public bool HasBuildSubmissionLoggedErrors(int submissionId)
        {
            return false;
        }

        public ICollection<string> GetWarningsAsErrors(BuildEventContext context)
        {
            throw new NotImplementedException();
        }

        public ICollection<string> GetWarningsNotAsErrors(BuildEventContext context)
        {
            throw new NotImplementedException();
        }

        public ICollection<string> GetWarningsAsMessages(BuildEventContext context)
        {
            throw new NotImplementedException();
        }

        public void LogIncludeFile(BuildEventContext buildEventContext, string filePath)
        {
            throw new NotImplementedException();
        }

        public void InitializeComponent(IBuildComponentHost host) => throw new NotImplementedException();
        public void ShutdownComponent() => throw new NotImplementedException();

        #endregion

        public void ProcessPropertyRead(PropertyReadInfo propertyReadInfo, CheckLoggingContext checkContext)
        { /* Ignore the data */ }

        public void ProcessPropertyWrite(PropertyWriteInfo propertyWriteInfo, CheckLoggingContext checkContext)
        { /* Ignore the data */ }

        public void ProcessProjectEvaluationStarted(ICheckContext analysisContext, string projectFullPath)
        { /* Ignore the data */ }
    }
}
