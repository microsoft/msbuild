// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.BackEnd.Shared;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Shared;

using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.BackEnd.Logging
{
    /// <summary>
    /// Partial class half of LoggingService which contains the Logging methods.
    /// </summary>
    internal partial class LoggingService : ILoggingService, INodePacketHandler, IBuildComponent
    {
        #region Log comments

        /// <summary>
        /// Logs a comment (BuildMessageEventArgs) with a certain MessageImportance level
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="importance">How important is the message, this will determine which verbosities the message will show up on.
        /// The higher the importance the lower the verbosity needs to be for the message to be seen</param>
        /// <param name="messageResourceName">String which identifies the message in the string resx</param>
        /// <param name="messageArgs">Arguments for the format string indexed by messageResourceName</param>
        /// <exception cref="InternalErrorException">MessageResourceName is null</exception>
        public void LogComment(BuildEventContext buildEventContext, MessageImportance importance, string messageResourceName, params object[] messageArgs)
        {
            if (!OnlyLogCriticalEvents)
            {
                ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(messageResourceName), "Need resource string for comment message.");

                LogCommentFromText(buildEventContext, importance, ResourceUtilities.GetResourceString(messageResourceName), messageArgs);
            }
        }

        /// <summary>
        /// Log a comment
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="importance">How important is the message, this will determine which verbosities the message will show up on.
        /// The higher the importance the lower the verbosity needs to be for the message to be seen</param>
        /// <param name="message">Message to log</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        /// <exception cref="InternalErrorException">Message is null</exception>
        public void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message)
        {
            this.LogCommentFromText(buildEventContext, importance, message, messageArgs: null);
        }

        /// <summary>
        /// Log a comment
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="importance">How important is the message, this will determine which verbosities the message will show up on.
        /// The higher the importance the lower the verbosity needs to be for the message to be seen</param>
        /// <param name="message">Message to log</param>
        /// <param name="messageArgs">Message formatting arguments</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        /// <exception cref="InternalErrorException">Message is null</exception>
        public void LogCommentFromText(BuildEventContext buildEventContext, MessageImportance importance, string message, params object[] messageArgs)
        {
            if (!OnlyLogCriticalEvents)
            {
                BuildMessageEventArgs buildEvent = EventsCreatorHelper.CreateMessageEventFromText(buildEventContext, importance, message, messageArgs);

                ProcessLoggingEvent(buildEvent);
            }
        }
        #endregion

        #region Log errors
        /**************************************************************************************************************************
         * WARNING: Do not add overloads that allow raising events without specifying a file. In general ALL events should have a
         * file associated with them. We've received a LOT of feedback from dogfooders about the lack of information in our
         * events. If an event TRULY does not have an associated file, then String.Empty can be passed in for the file. However,
         * that burden should lie on the caller -- these wrapper methods should NOT make it easy to skip the filename.
         *************************************************************************************************************************/

        /// <summary>
        /// Logs an error with all registered loggers using the specified resource string.
        /// </summary>
        /// <param name="location">Event context information which describes who is logging the event</param>
        /// <param name="file">File information where the error happened</param>
        /// <param name="messageResourceName">String key to find the correct string resource</param>
        /// <param name="messageArgs">Arguments for the string resource</param>
        public void LogError(BuildEventContext location, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            LogError(location, null, file, messageResourceName, messageArgs);
        }

        /// <summary>
        /// Logs an error
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="subcategoryResourceName">Can be null.</param>
        /// <param name="file">File information about where the error happened</param>
        /// <param name="messageResourceName">String index into the string.resx file</param>
        /// <param name="messageArgs">Arguments for the format string in the resource file</param>
        /// <exception cref="InternalErrorException">MessageResourceName is null</exception>
        public void LogError(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(messageResourceName), "Need resource string for error message.");

            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out string errorCode, out string helpKeyword, messageResourceName, messageArgs);

            LogErrorFromText(buildEventContext, subcategoryResourceName, errorCode, helpKeyword, file, message);
        }

        /// <summary>
        /// Logs an error with a given message
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="subcategoryResourceName">Can be null.</param>
        /// <param name="errorCode">Can be null.</param>
        /// <param name="helpKeyword">Can be null.</param>
        /// <param name="file">File information about where the error happened</param>
        /// <param name="message">Error message which will be displayed</param>
        /// <exception cref="InternalErrorException">File is null</exception>
        /// <exception cref="InternalErrorException">Message is null</exception>
        public void LogErrorFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string errorCode, string helpKeyword, BuildEventFileInfo file, string message)
        {
            BuildErrorEventArgs buildEvent = EventsCreatorHelper.CreateErrorEventFromText(buildEventContext, subcategoryResourceName, errorCode, helpKeyword, file, message);

            if (buildEvent.ProjectFile == null && buildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
            {
                _projectFileMap.TryGetValue(buildEventContext.ProjectContextId, out string projectFile);
                ErrorUtilities.VerifyThrow(projectFile != null, "ContextID {0} should have been in the ID-to-project file mapping but wasn't!", buildEventContext.ProjectContextId);
                buildEvent.ProjectFile = projectFile;
            }

            ProcessLoggingEvent(buildEvent);
        }

        /// <summary>
        /// Logs an error regarding an invalid project file . Since this method may be multiple times for the same InvalidProjectException
        /// we do not want to log the error multiple times. Once the exception has been logged we set a flag on the exception to note that
        /// it has already been logged.
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="invalidProjectFileException">Exception which is causing the error</param>
        /// <exception cref="InternalErrorException">InvalidProjectFileException is null</exception>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogInvalidProjectFileError(BuildEventContext buildEventContext, InvalidProjectFileException invalidProjectFileException)
        {
            ErrorUtilities.VerifyThrow(invalidProjectFileException != null, "Need exception context.");
            ErrorUtilities.VerifyThrow(buildEventContext != null, "buildEventContext is null");

            // Don't log the exception more than once.
            if (!invalidProjectFileException.HasBeenLogged)
            {
                BuildErrorEventArgs buildEvent =
                    new BuildErrorEventArgs(
                        invalidProjectFileException.ErrorSubcategory,
                        invalidProjectFileException.ErrorCode,
                        invalidProjectFileException.ProjectFile,
                        invalidProjectFileException.LineNumber,
                        invalidProjectFileException.ColumnNumber,
                        invalidProjectFileException.EndLineNumber,
                        invalidProjectFileException.EndColumnNumber,
                        invalidProjectFileException.BaseMessage,
                        invalidProjectFileException.HelpKeyword,
                        "MSBuild");
                buildEvent.BuildEventContext = buildEventContext;
                if (buildEvent.ProjectFile == null && buildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
                {
                    _projectFileMap.TryGetValue(buildEventContext.ProjectContextId, out string projectFile);
                    ErrorUtilities.VerifyThrow(projectFile != null, "ContextID {0} should have been in the ID-to-project file mapping but wasn't!", buildEventContext.ProjectContextId);
                    buildEvent.ProjectFile = projectFile;
                }

                ProcessLoggingEvent(buildEvent);
                invalidProjectFileException.HasBeenLogged = true;
            }
        }

        /// <summary>
        /// Logs an error regarding an unexpected build failure
        /// This will include a stack dump.
        /// </summary>
        /// <param name="buildEventContext">BuildEventContext of the error</param>
        /// <param name="exception">Exception wihch caused the build error</param>
        /// <param name="file">Provides file information about where the build error happened</param>
        public void LogFatalBuildError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file)
        {
            LogFatalError(buildEventContext, exception, file, "FatalBuildError");
        }

        /// <summary>
        /// Logs an error regarding an unexpected task failure.
        /// This will include a stack dump.
        /// </summary>
        /// <param name="buildEventContext">BuildEventContext of the error</param>
        /// <param name="exception">Exceptionm which caused the error</param>
        /// <param name="file">File information which indicates which file the error is happening in</param>
        /// <param name="taskName">Task which the error is happening in</param>
        /// <exception cref="InternalErrorException">TaskName is null</exception>
        public void LogFatalTaskError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName)
        {
            ErrorUtilities.VerifyThrow(taskName != null, "Must specify the name of the task that failed.");

            LogFatalError(buildEventContext, exception, file, "FatalTaskError", taskName);
        }

        /// <summary>
        /// Logs an error regarding an unexpected failure using the specified resource string.
        /// This will include a stack dump.
        /// </summary>
        /// <param name="buildEventContext">BuildEventContext of the error</param>
        /// <param name="exception">Exception which will be used to generate the error message</param>
        /// <param name="file">File information which describes where the error happened</param>
        /// <param name="messageResourceName">String name for the resource string to be used</param>
        /// <param name="messageArgs">Arguments for messageResourceName</param>
        /// <exception cref="InternalErrorException">MessageResourceName is null</exception>
        public void LogFatalError(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(messageResourceName), "Need resource string for error message.");

            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out string errorCode, out string helpKeyword, messageResourceName, messageArgs);
#if DEBUG
            message += Environment.NewLine + "This is an unhandled exception from a task -- PLEASE OPEN A BUG AGAINST THE TASK OWNER.";
#endif
            if (exception != null)
            {
                message += Environment.NewLine + exception.ToString();
            }

            LogErrorFromText(buildEventContext, null, errorCode, helpKeyword, file, message);
        }

        #endregion

        #region Log warnings
        /**************************************************************************************************************************
         * WARNING: Do not add overloads that allow raising events without specifying a file. In general ALL events should have a
         * file associated with them. We've received a LOT of feedback from dogfooders about the lack of information in our
         * events. If an event TRULY does not have an associated file, then String.Empty can be passed in for the file. However,
         * that burden should lie on the caller -- these wrapper methods should NOT make it easy to skip the filename.
         *************************************************************************************************************************/

        /// <summary>
        /// Logs an warning regarding an unexpected task failure
        /// This will include a stack dump.
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="exception">The exception to be used to create the warning text</param>
        /// <param name="file">The file information which indicates where the warning happened</param>
        /// <param name="taskName">Name of the task which the warning is being raised from</param>
        public void LogTaskWarningFromException(BuildEventContext buildEventContext, Exception exception, BuildEventFileInfo file, string taskName)
        {
            ErrorUtilities.VerifyThrow(!String.IsNullOrEmpty(taskName), "Must specify the name of the task that failed.");

            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out string warningCode, out string helpKeyword, "FatalTaskError", taskName);
#if DEBUG
            message += Environment.NewLine + "This is an unhandled exception from a task -- PLEASE OPEN A BUG AGAINST THE TASK OWNER.";
#endif

            if (exception != null)
            {
                message += Environment.NewLine + exception.ToString();
            }

            LogWarningFromText(buildEventContext, null, warningCode, helpKeyword, file, message);
        }

        /// <summary>
        /// Logs a warning using the specified resource string.
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="subcategoryResourceName">Can be null.</param>
        /// <param name="file">File information which describes where the warning happened</param>
        /// <param name="messageResourceName">String name for the resource string to be used</param>
        /// <param name="messageArgs">Arguments for messageResourceName</param>
        public void LogWarning(BuildEventContext buildEventContext, string subcategoryResourceName, BuildEventFileInfo file, string messageResourceName, params object[] messageArgs)
        {
            ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(messageResourceName), "Need resource string for warning message.");

            string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out string warningCode, out string helpKeyword, messageResourceName, messageArgs);
            LogWarningFromText(buildEventContext, subcategoryResourceName, warningCode, helpKeyword, file, message);
        }

        /// <summary>
        /// Logs a warning
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="subcategoryResourceName">Subcategory resource Name. Can be null.</param>
        /// <param name="warningCode">The warning code of the message. Can be null.</param>
        /// <param name="helpKeyword">Help keyword for the message. Can be null.</param>
        /// <param name="file">The file information which will describe where the warning happened</param>
        /// <param name="message">Warning message to log</param>
        public void LogWarningFromText(BuildEventContext buildEventContext, string subcategoryResourceName, string warningCode, string helpKeyword, BuildEventFileInfo file, string message)
        {
            ErrorUtilities.VerifyThrow(file != null, "Must specify the associated file.");
            ErrorUtilities.VerifyThrow(message != null, "Need warning message.");
            ErrorUtilities.VerifyThrow(buildEventContext != null, "Need a BuildEventContext");

            string subcategory = null;

            if (!string.IsNullOrWhiteSpace(subcategoryResourceName))
            {
                subcategory = AssemblyResources.GetString(subcategoryResourceName);
            }

            BuildWarningEventArgs buildEvent = new BuildWarningEventArgs(
                    subcategory,
                    warningCode,
                    file.File,
                    file.Line,
                    file.Column,
                    file.EndLine,
                    file.EndColumn,
                    message,
                    helpKeyword,
                    "MSBuild");

            buildEvent.BuildEventContext = buildEventContext;
            if (buildEvent.ProjectFile == null && buildEventContext.ProjectContextId != BuildEventContext.InvalidProjectContextId)
            {
                _projectFileMap.TryGetValue(buildEventContext.ProjectContextId, out string projectFile);
                ErrorUtilities.VerifyThrow(projectFile != null, "ContextID {0} should have been in the ID-to-project file mapping but wasn't!", buildEventContext.ProjectContextId);
                buildEvent.ProjectFile = projectFile;
            }

            ProcessLoggingEvent(buildEvent);
        }

        #endregion

        #region Log status

        /// <summary>
        /// Logs that the build has started
        /// </summary>
        public void LogBuildStarted()
        {
            // If we're only logging critical events, don't risk causing all the resources to load by formatting
            // a string that won't get emitted anyway.
            string message = String.Empty;
            if (!OnlyLogCriticalEvents)
            {
                message = ResourceUtilities.GetResourceString("BuildStarted");
            }

            IDictionary<string, string> environmentProperties = _componentHost?.BuildParameters?.BuildProcessEnvironment;

            BuildStartedEventArgs buildEvent = new(message, helpKeyword: null, environmentProperties);

            // Raise the event with the filters
            ProcessLoggingEvent(buildEvent);

            // Make sure we process this event before going any further
            WaitForLoggingToProcessEvents();
        }

        /// <summary>
        /// Logs that the build has finished
        /// </summary>
        /// <param name="success">Did the build pass or fail</param>
        public void LogBuildFinished(bool success)
        {
            // If we're only logging critical events, don't risk causing all the resources to load by formatting
            // a string that won't get emitted anyway.
            string message = String.Empty;
            if (!OnlyLogCriticalEvents)
            {
                if (Question)
                {
                    message = ResourceUtilities.GetResourceString(success ? "BuildFinishedQuestionSuccess" : "BuildFinishedQuestionFailure");
                }
                else
                {
                    message = ResourceUtilities.GetResourceString(success ? "BuildFinishedSuccess" : "BuildFinishedFailure");
                }
            }

            BuildFinishedEventArgs buildEvent = new BuildFinishedEventArgs(message, null /* no help keyword */, success);

            ProcessLoggingEvent(buildEvent);

            // Make sure we process this event before going any further
            WaitForLoggingToProcessEvents();
        }

        /// <inheritdoc />
        public void LogBuildCanceled()
        {
            string message = ResourceUtilities.GetResourceString("AbortingBuild");
            BuildCanceledEventArgs buildEvent = new BuildCanceledEventArgs(message);

            ProcessLoggingEvent(buildEvent);
        }

        /// <inheritdoc />
        public BuildEventContext CreateEvaluationBuildEventContext(int nodeId, int submissionId)
            => new BuildEventContext(submissionId, nodeId, NextEvaluationId, BuildEventContext.InvalidProjectInstanceId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);

        /// <inheritdoc />
        public BuildEventContext CreateProjectCacheBuildEventContext(
            int submissionId,
            int evaluationId,
            int projectInstanceId,
            string projectFile)
        {
            int projectContextId = NextProjectId;

            // In the future if some LogProjectCacheStarted event is created, move this there to align with evaluation and build execution.
            _projectFileMap[projectContextId] = projectFile;

            // Because the project cache runs in the BuildManager, it makes some sense to associate logging with the in-proc node.
            // If a invalid node id is used the messages become deferred in the console logger and spit out at the end.
            int nodeId = Scheduler.InProcNodeId;

            return new BuildEventContext(submissionId, nodeId, evaluationId, projectInstanceId, projectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);
        }

        /// <inheritdoc />
        public void LogProjectEvaluationStarted(BuildEventContext projectEvaluationEventContext, string projectFile)
        {
            ProjectEvaluationStartedEventArgs evaluationEvent =
                new ProjectEvaluationStartedEventArgs(ResourceUtilities.GetResourceString("EvaluationStarted"),
                    projectFile)
                {
                    BuildEventContext = projectEvaluationEventContext,
                    ProjectFile = projectFile
                };

            ProcessLoggingEvent(evaluationEvent);
        }

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
        public void LogProjectEvaluationFinished(
            BuildEventContext projectEvaluationEventContext,
            string projectFile,
            IEnumerable globalProperties,
            IEnumerable properties,
            IEnumerable items,
            ProfilerResult? profilerResult)
        {
            ErrorUtilities.VerifyThrow(projectEvaluationEventContext != null, "projectBuildEventContext");

            ProjectEvaluationFinishedEventArgs buildEvent =
                new ProjectEvaluationFinishedEventArgs(ResourceUtilities.GetResourceString("EvaluationFinished"), projectFile)
                {
                    BuildEventContext = projectEvaluationEventContext,
                    ProjectFile = projectFile,
                    ProfilerResult = profilerResult,
                    GlobalProperties = globalProperties,
                    Properties = properties,
                    Items = items
                };
            ProcessLoggingEvent(buildEvent);
        }

        /// <summary>
        /// Logs that a project build has started
        /// </summary>
        /// <param name="nodeBuildEventContext">The event context of the node which is spawning this project.</param>
        /// <param name="submissionId">The id of the submission.</param>
        /// <param name="configurationId">The id of the project configuration which is about to start</param>
        /// <param name="parentBuildEventContext">BuildEventContext of the project who is requesting "projectFile" to build</param>
        /// <param name="projectFile">Project file to build</param>
        /// <param name="targetNames">Target names to build</param>
        /// <param name="properties">Initial property list</param>
        /// <param name="items">Initial items list</param>
        /// <param name="evaluationId">EvaluationId of the project instance</param>
        /// <param name="projectContextId">The project context id</param>
        /// <returns>The build event context for the project.</returns>
        /// <exception cref="InternalErrorException">parentBuildEventContext is null</exception>
        /// <exception cref="InternalErrorException">projectBuildEventContext is null</exception>
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
            var args = CreateProjectStarted(nodeBuildEventContext,
                submissionId,
                configurationId,
                parentBuildEventContext,
                projectFile,
                targetNames,
                properties,
                items,
                evaluationId,
                projectContextId);

            this.LogProjectStarted(args);

            return args.BuildEventContext;
        }

        public void LogProjectStarted(ProjectStartedEventArgs buildEvent)
        {
            ProcessLoggingEvent(buildEvent);
        }

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
            ErrorUtilities.VerifyThrow(nodeBuildEventContext != null, "Need a nodeBuildEventContext");

            if (projectContextId == BuildEventContext.InvalidProjectContextId)
            {
                projectContextId = NextProjectId;

                // PERF: Not using VerifyThrow to avoid boxing of projectBuildEventContext.ProjectContextId in the non-error case.
                if (_projectFileMap.ContainsKey(projectContextId))
                {
                    ErrorUtilities.ThrowInternalError("ContextID {0} for project {1} should not already be in the ID-to-file mapping!", projectContextId, projectFile);
                }

                _projectFileMap[projectContextId] = projectFile;
            }
            else
            {
                // A projectContextId was provided, so use it with some sanity checks
                if (_projectFileMap.TryGetValue(projectContextId, out string existingProjectFile))
                {
                    if (!projectFile.Equals(existingProjectFile, StringComparison.OrdinalIgnoreCase))
                    {
                        ErrorUtilities.ThrowInternalError("ContextID {0} was already in the ID-to-project file mapping but the project file {1} did not match the provided one {2}!", projectContextId, existingProjectFile, projectFile);
                    }
                }
                else
                {
                    // Currently, an existing projectContextId can only be provided in the project cache scenario, which runs on the in-proc node.
                    // If there was a cache miss and the build was scheduled on a worker node, it may not have seen this projectContextId yet.
                    // So we only need this sanity check for the in-proc node.
                    if (nodeBuildEventContext.NodeId == Scheduler.InProcNodeId)
                    {
                        ErrorUtilities.ThrowInternalError("ContextID {0} should have been in the ID-to-project file mapping but wasn't!", projectContextId);
                    }

                    _projectFileMap[projectContextId] = projectFile;
                }
            }

            BuildEventContext projectBuildEventContext = new BuildEventContext(submissionId, nodeBuildEventContext.NodeId, evaluationId, configurationId, projectContextId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidTaskId);

            ErrorUtilities.VerifyThrow(parentBuildEventContext != null, "Need a parentBuildEventContext");

            ErrorUtilities.VerifyThrow(_configCache.Value.HasConfiguration(configurationId), "Cannot find the project configuration while injecting non-serialized data from out-of-proc node.");
            var buildRequestConfiguration = _configCache.Value[configurationId];

            // Always log GlobalProperties on ProjectStarted
            // See https://github.com/dotnet/msbuild/issues/6341 for details
            IDictionary<string, string> globalProperties = buildRequestConfiguration.GlobalProperties.ToDictionary();

            var buildEvent = new ProjectStartedEventArgs(
                    configurationId,
                    message: null,
                    helpKeyword: null,
                    projectFile,
                    targetNames,
                    properties,
                    items,
                    parentBuildEventContext,
                    globalProperties,
                    buildRequestConfiguration.ToolsVersion);
            buildEvent.BuildEventContext = projectBuildEventContext;

            return buildEvent;
        }

        /// <summary>
        /// Logs that a project has finished
        /// </summary>
        /// <param name="projectBuildEventContext">Event context for the project.</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="success">Did the project pass or fail</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogProjectFinished(BuildEventContext projectBuildEventContext, string projectFile, bool success)
            => LogProjectFinished(projectBuildEventContext, null, projectFile, success);

        /// <summary>
        /// Logs that a project has finished
        /// </summary>
        /// <param name="projectBuildEventContext">Event context for the project.</param>
        /// <param name="projectBuildStats"></param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="success">Did the project pass or fail</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogProjectFinished(BuildEventContext projectBuildEventContext, ProjectBuildStats projectBuildStats, string projectFile, bool success)
        {
            ErrorUtilities.VerifyThrow(projectBuildEventContext != null, "projectBuildEventContext");

            ProjectFinishedEventArgs buildEvent = new ProjectFinishedEventArgs(
                    message: null,
                    helpKeyword: null,
                    projectFile,
                    success);
            buildEvent.BuildEventContext = projectBuildEventContext;
            buildEvent.ProjectBuildStats = projectBuildStats;
            ProcessLoggingEvent(buildEvent);

            // PERF: Not using VerifyThrow to avoid boxing of projectBuildEventContext.ProjectContextId in the non-error case.
            if (!_projectFileMap.TryRemove(projectBuildEventContext.ProjectContextId, out _))
            {
                ErrorUtilities.ThrowInternalError("ContextID {0} for project {1} should be in the ID-to-file mapping!", projectBuildEventContext.ProjectContextId, projectFile);
            }
        }

        /// <summary>
        /// Logs that a target started
        /// </summary>
        /// <param name="projectBuildEventContext">Event context for the project spawning this target</param>
        /// <param name="targetName">Name of target</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="projectFileOfTargetElement">Project file which contains the target</param>
        /// <param name="parentTargetName">The name of the parent target.</param>
        /// <param name="buildReason">The reason the parent target built the target.</param>
        /// <returns>The build event context for the target.</returns>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public BuildEventContext LogTargetStarted(BuildEventContext projectBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, string parentTargetName, TargetBuiltReason buildReason)
        {
            ErrorUtilities.VerifyThrow(projectBuildEventContext != null, "projectBuildEventContext is null");
            BuildEventContext targetBuildEventContext = new BuildEventContext(
                    projectBuildEventContext.SubmissionId,
                    projectBuildEventContext.NodeId,
                    projectBuildEventContext.ProjectInstanceId,
                    projectBuildEventContext.ProjectContextId,
                    NextTargetId,
                    BuildEventContext.InvalidTaskId);

            if (!OnlyLogCriticalEvents)
            {
                TargetStartedEventArgs buildEvent = new TargetStartedEventArgs(
                        message: null,
                        helpKeyword: null,
                        targetName,
                        projectFile,
                        projectFileOfTargetElement,
                        parentTargetName,
                        buildReason,
                        DateTime.UtcNow);
                buildEvent.BuildEventContext = targetBuildEventContext;
                ProcessLoggingEvent(buildEvent);
            }

            return targetBuildEventContext;
        }

        /// <summary>
        /// Logs that a target has finished.
        /// </summary>
        /// <param name="targetBuildEventContext">Event context for the target</param>
        /// <param name="targetName">Target which has just finished</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="projectFileOfTargetElement">Project file which contains the target</param>
        /// <param name="success">Did the target pass or fail</param>
        /// <param name="targetOutputs">Target outputs for the target.</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogTargetFinished(BuildEventContext targetBuildEventContext, string targetName, string projectFile, string projectFileOfTargetElement, bool success, IEnumerable<TaskItem> targetOutputs)
        {
            if (!OnlyLogCriticalEvents)
            {
                ErrorUtilities.VerifyThrow(targetBuildEventContext != null, "targetBuildEventContext is null");

                TargetFinishedEventArgs buildEvent = new TargetFinishedEventArgs(
                        message: null,
                        helpKeyword: null,
                        targetName,
                        projectFile,
                        projectFileOfTargetElement,
                        success,
                        targetOutputs);

                buildEvent.BuildEventContext = targetBuildEventContext;
                ProcessLoggingEvent(buildEvent);
            }
        }

        /// <summary>
        /// Logs that task execution has started.
        /// </summary>
        /// <param name="taskBuildEventContext">Event context for the task</param>
        /// <param name="taskName">Task Name</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="projectFileOfTaskNode">Project file which contains the task</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogTaskStarted(BuildEventContext taskBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, string taskAssemblyLocation)
        {
            ErrorUtilities.VerifyThrow(taskBuildEventContext != null, "targetBuildEventContext is null");
            if (!OnlyLogCriticalEvents)
            {
                TaskStartedEventArgs buildEvent = new TaskStartedEventArgs(
                        message: null,
                        helpKeyword: null,
                        projectFile,
                        projectFileOfTaskNode,
                        taskName,
                        taskAssemblyLocation);
                buildEvent.BuildEventContext = taskBuildEventContext;
                ProcessLoggingEvent(buildEvent);
            }
        }

        /// <summary>
        /// Logs that task execution has started.
        /// </summary>
        /// <param name="targetBuildEventContext">Event context for the target spawning this task.</param>
        /// <param name="taskName">Task Name</param>
        /// <param name="projectFile">Project file being built</param>
        /// <param name="projectFileOfTaskNode">Project file which contains the task</param>
        /// <param name="line">The line number in the file where the task invocation is located.</param>
        /// <param name="column">The column number in the file where the task invocation is located.</param>
        /// <param name="taskAssemblyLocation">>The location of the assembly containing the implementation of the task.</param>
        /// <returns>The build event context for the task.</returns>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public BuildEventContext LogTaskStarted2(BuildEventContext targetBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, int line, int column, string taskAssemblyLocation)
        {
            ErrorUtilities.VerifyThrow(targetBuildEventContext != null, "targetBuildEventContext is null");
            BuildEventContext taskBuildEventContext = new BuildEventContext(
                    targetBuildEventContext.SubmissionId,
                    targetBuildEventContext.NodeId,
                    targetBuildEventContext.ProjectInstanceId,
                    targetBuildEventContext.ProjectContextId,
                    targetBuildEventContext.TargetId,
                    NextTaskId);

            if (!OnlyLogCriticalEvents)
            {
                TaskStartedEventArgs buildEvent = new TaskStartedEventArgs(
                        message: null,
                        helpKeyword: null,
                        projectFile,
                        projectFileOfTaskNode,
                        taskName,
                        taskAssemblyLocation);
                buildEvent.BuildEventContext = taskBuildEventContext;
                buildEvent.LineNumber = line;
                buildEvent.ColumnNumber = column;
                ProcessLoggingEvent(buildEvent);
            }

            return taskBuildEventContext;
        }

        /// <summary>
        /// Logs that a task has finished executing.
        /// </summary>
        /// <param name="taskBuildEventContext">Event context for the task</param>
        /// <param name="taskName">Name of the task</param>
        /// <param name="projectFile">Project which is being processed</param>
        /// <param name="projectFileOfTaskNode">Project file which contains the task</param>
        /// <param name="success">Did the task pass or fail</param>
        /// <exception cref="InternalErrorException">BuildEventContext is null</exception>
        public void LogTaskFinished(BuildEventContext taskBuildEventContext, string taskName, string projectFile, string projectFileOfTaskNode, bool success)
        {
            if (!OnlyLogCriticalEvents)
            {
                ErrorUtilities.VerifyThrow(taskBuildEventContext != null, "taskBuildEventContext is null");

                TaskFinishedEventArgs buildEvent = new TaskFinishedEventArgs(
                        message: null,
                        helpKeyword: null,
                        projectFile,
                        projectFileOfTaskNode,
                        taskName,
                        success);
                buildEvent.BuildEventContext = taskBuildEventContext;
                ProcessLoggingEvent(buildEvent);
            }
        }

        #endregion

        #region Log telemetry

        /// <summary>
        /// Logs a telemetry event.
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="eventName">The event name.</param>
        /// <param name="properties">The list of properties assocated with the event.</param>
        public void LogTelemetry(BuildEventContext buildEventContext, string eventName, IDictionary<string, string> properties)
        {
            ErrorUtilities.VerifyThrow(eventName != null, "eventName is null");

            TelemetryEventArgs telemetryEvent = new TelemetryEventArgs
            {
                BuildEventContext = buildEventContext,
                EventName = eventName,
                Properties = properties == null ? new Dictionary<string, string>() : new Dictionary<string, string>(properties)
            };

            ProcessLoggingEvent(telemetryEvent);
        }

        #endregion

        #region log response files
        /// <summary>
        /// Logs a file to include in the binlogs
        /// </summary>
        /// <param name="buildEventContext">Event context information which describes who is logging the event</param>
        /// <param name="filePath">Full path to response file</param>
        public void LogIncludeFile(BuildEventContext buildEventContext, string filePath)
        {
            ErrorUtilities.VerifyThrow(buildEventContext != null, "buildEventContext was null");
            ErrorUtilities.VerifyThrow(filePath != null, "response file path was null");
            ResponseFileUsedEventArgs responseFileUsedEvent = new ResponseFileUsedEventArgs(filePath);
            responseFileUsedEvent.BuildEventContext = buildEventContext;
            ProcessLoggingEvent(responseFileUsedEvent);
        }

        #endregion

#nullable enable
        private IBuildEngineDataRouter? _buildEngineDataRouter;

        public void ProcessPropertyRead(PropertyReadInfo propertyReadInfo, CheckLoggingContext checkContext)
            => _buildEngineDataRouter?.ProcessPropertyRead(propertyReadInfo, checkContext);

        public void ProcessPropertyWrite(PropertyWriteInfo propertyWriteInfo, CheckLoggingContext checkContext)
            => _buildEngineDataRouter?.ProcessPropertyWrite(propertyWriteInfo, checkContext);

        public void ProcessProjectEvaluationStarted(ICheckContext checkContext, string projectFullPath)
            => _buildEngineDataRouter?.ProcessProjectEvaluationStarted(checkContext, projectFullPath);
#nullable disable
    }
}
