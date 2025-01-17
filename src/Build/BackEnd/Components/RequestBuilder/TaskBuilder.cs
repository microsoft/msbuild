// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
#if FEATURE_APARTMENT_STATE
using System.Diagnostics.CodeAnalysis;
#endif
using System.Linq;
using System.Reflection;
#if FEATURE_APARTMENT_STATE
using System.Runtime.ExceptionServices;
#endif
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd.Components.RequestBuilder;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using ElementLocation = Microsoft.Build.Construction.ElementLocation;
using ProjectItemInstanceFactory = Microsoft.Build.Execution.ProjectItemInstance.TaskItem.ProjectItemInstanceFactory;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;
using TargetLoggingContext = Microsoft.Build.BackEnd.Logging.TargetLoggingContext;
using TaskLoggingContext = Microsoft.Build.BackEnd.Logging.TaskLoggingContext;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// The possible values for a task's ContinueOnError attribute.
    /// </summary>
    internal enum ContinueOnError
    {
        /// <summary>
        /// If the task fails, error and stop.
        /// </summary>
        ErrorAndStop,

        /// <summary>
        /// If the task fails, error and continue.
        /// </summary>
        ErrorAndContinue,

        /// <summary>
        /// If the task fails, warn and continue.
        /// </summary>
        WarnAndContinue
    }

    /// <summary>
    /// The TaskBuilder is one of two components related to building tasks, the other being the TaskExecutionHost.  The TaskBuilder is
    /// responsible for all parts dealing with the XML/task declaration.  It determines if the task is intrinsic or extrinsic,
    /// looks up the task in the task registry, determines the task parameters and requests them to be set, and requests outputs
    /// when task execution has been completed.  It is not responsible for reflection over the task instance or anything which
    /// requires dealing with the task instance directly - those actions are handled by the TaskExecutionHost.
    /// </summary>
    internal class TaskBuilder : ITaskBuilder, IBuildComponent
    {
        /// <summary>
        /// The Build Request Entry for which this task is executing.
        /// </summary>
        private BuildRequestEntry _buildRequestEntry;

        /// <summary>
        /// The cancellation token
        /// </summary>
        private CancellationToken _cancellationToken;

        /// <summary>
        /// The build component host.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The original target child instance
        /// </summary>
        private ProjectTargetInstanceChild _targetChildInstance;

        /// <summary>
        /// The task instance for extrinsic tasks
        /// </summary>
        private ProjectTaskInstance _taskNode;

        /// <summary>
        /// Host callback for host-aware tasks.
        /// </summary>
        private ITaskHost _taskHostObject;

        /// <summary>
        /// indicates whether to ignore task execution failures
        /// </summary>
        private ContinueOnError _continueOnError;

        /// <summary>
        /// The logging context for the target in which we are executing.
        /// </summary>
        private TargetLoggingContext _targetLoggingContext;

        /// <summary>
        /// Full path to the project, for errors
        /// </summary>
        private string _projectFullPath;

        /// <summary>
        /// The target builder callback.
        /// </summary>
        private ITargetBuilderCallback _targetBuilderCallback;

        /// <summary>
        /// The task execution host for in-proc tasks.
        /// </summary>
        private TaskExecutionHost _taskExecutionHost;

        /// <summary>
        /// The object used to synchronize access to the task execution host.
        /// </summary>
        private Object _taskExecutionHostSync = new Object();

        /// <summary>
        /// Constructor
        /// </summary>
        internal TaskBuilder()
        {
        }

        /// <summary>
        /// Builds the task specified by the XML.
        /// </summary>
        /// <param name="loggingContext">The logging context of the target</param>
        /// <param name="requestEntry">The build request entry being built</param>
        /// <param name="targetBuilderCallback">The target builder callback.</param>
        /// <param name="taskInstance">The task instance.</param>
        /// <param name="mode">The mode in which to execute tasks.</param>
        /// <param name="inferLookup">The lookup to be used for inference.</param>
        /// <param name="executeLookup">The lookup to be used during execution.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to use when executing the task.</param>
        /// <returns>The result of running the task batch.</returns>
        /// <remarks>
        /// The ExecuteTask method takes a task as specified by XML and executes it.  This procedure is comprised
        /// of the following steps:
        /// 1. Loading the Task from its containing assembly by looking it up in the task registry
        /// 2. Determining if the task is batched.  If it is, create the batches and execute each as if it were a non-batched task
        /// 3. If the task is not batched, execute it.
        /// 4. If the task was batched, hold on to its Lookup until all of the natches are done, then merge them.
        /// </remarks>
        public async Task<WorkUnitResult> ExecuteTask(TargetLoggingContext loggingContext, BuildRequestEntry requestEntry, ITargetBuilderCallback targetBuilderCallback, ProjectTargetInstanceChild taskInstance, TaskExecutionMode mode, Lookup inferLookup, Lookup executeLookup, CancellationToken cancellationToken)
        {
            ErrorUtilities.VerifyThrow(taskInstance != null, "Need to specify the task instance.");

            _buildRequestEntry = requestEntry;
            _targetBuilderCallback = targetBuilderCallback;
            _cancellationToken = cancellationToken;
            _targetChildInstance = taskInstance;

            // In the case of Intrinsic tasks, taskNode will end up null.  Currently this is how we distinguish
            // intrinsic from extrinsic tasks.
            _taskNode = taskInstance as ProjectTaskInstance;

            if (_taskNode != null && requestEntry.Request.HostServices != null)
            {
                _taskHostObject = requestEntry.Request.HostServices.GetHostObject(requestEntry.RequestConfiguration.Project.FullPath, loggingContext.Target.Name, _taskNode.Name);
            }

            _projectFullPath = requestEntry.RequestConfiguration.Project.FullPath;

            // this.handleId = handleId; No handles
            // this.parentModule = parentModule; No task execution module
            _continueOnError = ContinueOnError.ErrorAndStop;

            _targetLoggingContext = loggingContext;

            WorkUnitResult taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            if ((mode & TaskExecutionMode.InferOutputsOnly) == TaskExecutionMode.InferOutputsOnly)
            {
                taskResult = await ExecuteTask(TaskExecutionMode.InferOutputsOnly, inferLookup);
            }

            if ((mode & TaskExecutionMode.ExecuteTaskAndGatherOutputs) == TaskExecutionMode.ExecuteTaskAndGatherOutputs)
            {
                taskResult = await ExecuteTask(TaskExecutionMode.ExecuteTaskAndGatherOutputs, executeLookup);
            }

            return taskResult;
        }

        #region IBuildComponent Members

        /// <summary>
        /// Sets the build component host.
        /// </summary>
        /// <param name="host">The component host.</param>
        public void InitializeComponent(IBuildComponentHost host)
        {
            _componentHost = host;
            _taskExecutionHost = new TaskExecutionHost(host);
        }

        /// <summary>
        /// Shuts down the component.
        /// </summary>
        public void ShutdownComponent()
        {
            lock (_taskExecutionHostSync)
            {
                ErrorUtilities.VerifyThrow(_taskExecutionHost != null, "taskExecutionHost not initialized.");
                _componentHost = null;

                IDisposable disposable = _taskExecutionHost as IDisposable;
                disposable?.Dispose();

                _taskExecutionHost = null;
            }
        }

        #endregion

        /// <summary>
        /// Class factory for component creation.
        /// </summary>
        internal static IBuildComponent CreateComponent(BuildComponentType type)
        {
            ErrorUtilities.VerifyThrow(type == BuildComponentType.TaskBuilder, "Cannot create components of type {0}", type);
            return new TaskBuilder();
        }

        #region Methods

        /// <summary>
        /// Build up a list of all parameters on the task, including those in any Output tags,
        /// in order to find batchable metadata references
        /// </summary>
        /// <returns>The list of parameter values</returns>
        private List<string> CreateListOfParameterValues()
        {
            if (_taskNode == null)
            {
                // This is an intrinsic task.  Batching is not handled here.
                return new List<string>();
            }

            List<string> taskParameters = new List<string>(_taskNode.ParametersForBuild.Count + _taskNode.Outputs.Count);

            foreach (KeyValuePair<string, (string, ElementLocation)> taskParameter in _taskNode.ParametersForBuild)
            {
                taskParameters.Add(taskParameter.Value.Item1);
            }

            // Add parameters on any output tags
            foreach (ProjectTaskInstanceChild taskOutputSpecification in _taskNode.Outputs)
            {
                ProjectTaskOutputItemInstance outputItemInstance = taskOutputSpecification as ProjectTaskOutputItemInstance;
                if (outputItemInstance != null)
                {
                    taskParameters.Add(outputItemInstance.TaskParameter);
                    taskParameters.Add(outputItemInstance.ItemType);
                }

                ProjectTaskOutputPropertyInstance outputPropertyInstance = taskOutputSpecification as ProjectTaskOutputPropertyInstance;
                if (outputPropertyInstance != null)
                {
                    taskParameters.Add(outputPropertyInstance.TaskParameter);
                    taskParameters.Add(outputPropertyInstance.PropertyName);
                }

                if (!String.IsNullOrEmpty(taskOutputSpecification.Condition))
                {
                    taskParameters.Add(taskOutputSpecification.Condition);
                }
            }

            if (!String.IsNullOrEmpty(_taskNode.Condition))
            {
                taskParameters.Add(_taskNode.Condition);
            }

            if (!String.IsNullOrEmpty(_taskNode.ContinueOnError))
            {
                taskParameters.Add(_taskNode.ContinueOnError);
            }

            return taskParameters;
        }

        /// <summary>
        /// Called to execute a task within a target. This method instantiates the task, sets its parameters, and executes it.
        /// </summary>
        /// <returns>true, if successful</returns>
        private async Task<WorkUnitResult> ExecuteTask(TaskExecutionMode mode, Lookup lookup)
        {
            ErrorUtilities.VerifyThrowArgumentNull(lookup);

            WorkUnitResult taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            TaskHost taskHost = null;

            List<ItemBucket> buckets = null;

            try
            {
                if (_taskNode != null)
                {
                    taskHost = new TaskHost(_componentHost, _buildRequestEntry, _targetChildInstance.Location, _targetBuilderCallback);
                    _taskExecutionHost.InitializeForTask(taskHost, _targetLoggingContext, _buildRequestEntry.RequestConfiguration.Project, _taskNode.Name, _taskNode.Location, _taskHostObject, _continueOnError != ContinueOnError.ErrorAndStop,
#if FEATURE_APPDOMAIN
                        taskHost.AppDomainSetup,
#endif
                        taskHost.IsOutOfProc, _cancellationToken);
                }

                List<string> taskParameterValues = CreateListOfParameterValues();
                buckets = BatchingEngine.PrepareBatchingBuckets(taskParameterValues, lookup, _targetChildInstance.Location, _targetLoggingContext);

                Dictionary<string, string> lookupHash = null;

                // Only create a hash table if there are more than one bucket as this is the only time a property can be overridden
                if (buckets.Count > 1)
                {
                    lookupHash ??= new Dictionary<string, string>(MSBuildNameIgnoreCaseComparer.Default);
                }

                WorkUnitResult aggregateResult = new WorkUnitResult();

                // Loop through each of the batch buckets and execute them one at a time
                for (int i = 0; i < buckets.Count; i++)
                {
                    // Execute the batch bucket, pass in which bucket we are executing so that we know when to get a new taskId for the bucket.
                    taskResult = await ExecuteBucket(taskHost, (ItemBucket)buckets[i], mode, lookupHash);

                    aggregateResult = aggregateResult.AggregateResult(taskResult);

                    if (aggregateResult.ActionCode == WorkUnitActionCode.Stop)
                    {
                        break;
                    }
                }

                taskResult = aggregateResult;
            }
            finally
            {
                _taskExecutionHost.CleanupForTask();

                taskHost?.MarkAsInactive();

                // Now all task batches are done, apply all item adds to the outer
                // target batch; we do this even if the task wasn't found (in that case,
                // no items or properties will have been added to the scope)
                if (buckets != null)
                {
                    foreach (ItemBucket bucket in buckets)
                    {
                        bucket.LeaveScope();
                    }
                }
            }

            return taskResult;
        }

        /// <summary>
        /// Execute a single bucket
        /// </summary>
        /// <returns>true if execution succeeded</returns>
        private async Task<WorkUnitResult> ExecuteBucket(TaskHost taskHost, ItemBucket bucket, TaskExecutionMode howToExecuteTask, Dictionary<string, string> lookupHash)
        {
            // On Intrinsic tasks, we do not allow batchable params, therefore metadata is excluded.
            ParserOptions parserOptions = (_taskNode == null) ? ParserOptions.AllowPropertiesAndItemLists : ParserOptions.AllowAll;
            WorkUnitResult taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);

            bool condition = ConditionEvaluator.EvaluateCondition(
                _targetChildInstance.Condition,
                parserOptions,
                bucket.Expander,
                ExpanderOptions.ExpandAll,
                _buildRequestEntry.ProjectRootDirectory,
                _targetChildInstance.ConditionLocation,
                FileSystems.Default,
                loggingContext: _targetLoggingContext);

            if (!condition)
            {
                LogSkippedTask(bucket, howToExecuteTask);
                return new WorkUnitResult(WorkUnitResultCode.Skipped, WorkUnitActionCode.Continue, null);
            }

            // Some tests do not provide an actual taskNode; checking if _taskNode == null prevents those tests from failing.
            // If this is an Intrinsic task, it gets handled in a special fashion.
            if (_taskNode == null)
            {
                try
                {
                    ExecuteIntrinsicTask(bucket);
                    taskResult = new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null);
                }
                catch (InvalidProjectFileException e)
                {
                    // Make sure the Invalid Project error gets logged *before* TaskFinished.  Otherwise,
                    // the log is confusing.
                    _targetLoggingContext.LogInvalidProjectFileError(e);
                    _continueOnError = ContinueOnError.ErrorAndStop;
                    taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, e);
                }
            }
            else
            {
                if (_componentHost.BuildParameters.SaveOperatingEnvironment)
                {
                    // Change to the project root directory.
                    // If that directory does not exist, do nothing. (Do not check first as it is almost always there and it is slow)
                    // This is because if the project has not been saved, this directory may not exist, yet it is often useful to still be able to build the project.
                    // No errors are masked by doing this: errors loading the project from disk are reported at load time, if necessary.
                    NativeMethodsShared.SetCurrentDirectory(_buildRequestEntry.ProjectRootDirectory);
                }

                if (howToExecuteTask == TaskExecutionMode.ExecuteTaskAndGatherOutputs)
                {
                    // We need to find the task before logging the task started event so that the using task statement comes before the task started event
                    IDictionary<string, string> taskIdentityParameters = GatherTaskIdentityParameters(bucket.Expander);
                    (TaskRequirements? requirements, TaskFactoryWrapper taskFactoryWrapper) = _taskExecutionHost.FindTask(taskIdentityParameters);
                    string taskAssemblyLocation = taskFactoryWrapper?.TaskFactoryLoadedType?.Path;

                    if (requirements != null)
                    {
                        TaskLoggingContext taskLoggingContext = _targetLoggingContext.LogTaskBatchStarted(_projectFullPath, _targetChildInstance, taskAssemblyLocation);
                        MSBuildEventSource.Log.ExecuteTaskStart(_taskNode?.Name, taskLoggingContext.BuildEventContext.TaskId);
                        // Can be condition with _componentHost.BuildParameters.IsTelemetryEnabled) - but it's a cheap call
                        taskFactoryWrapper?.Statistics?.ExecutionStarted();

                        _buildRequestEntry.Request.CurrentTaskContext = taskLoggingContext.BuildEventContext;

                        try
                        {
                            if (
                                (requirements.Value & TaskRequirements.RequireSTAThread) == TaskRequirements.RequireSTAThread
#if FEATURE_APARTMENT_STATE
                                && (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
#endif
                                )
                            {
#if FEATURE_APARTMENT_STATE
                                taskResult = ExecuteTaskInSTAThread(bucket, taskLoggingContext, taskIdentityParameters, taskHost, howToExecuteTask);
#else
                                throw new PlatformNotSupportedException(TaskRequirements.RequireSTAThread.ToString());
#endif
                            }
                            else
                            {
                                taskResult = await InitializeAndExecuteTask(taskLoggingContext, bucket, taskIdentityParameters, taskHost, howToExecuteTask);
                            }

                            if (lookupHash != null)
                            {
                                List<string> overrideMessages = bucket.Lookup.GetPropertyOverrideMessages(lookupHash);
                                if (overrideMessages != null)
                                {
                                    foreach (string s in overrideMessages)
                                    {
                                        taskLoggingContext.LogCommentFromText(MessageImportance.Low, s);
                                    }
                                }
                            }
                        }
                        catch (InvalidProjectFileException e)
                        {
                            // Make sure the Invalid Project error gets logged *before* TaskFinished.  Otherwise,
                            // the log is confusing.
                            taskLoggingContext.LogInvalidProjectFileError(e);
                            _continueOnError = ContinueOnError.ErrorAndStop;
                            taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, e);
                        }
                        finally
                        {
                            _buildRequestEntry.Request.CurrentTaskContext = null;

                            // Flag the completion of the task.
                            taskLoggingContext.LogTaskBatchFinished(_projectFullPath, taskResult.ResultCode == WorkUnitResultCode.Success || taskResult.ResultCode == WorkUnitResultCode.Skipped);
                            taskFactoryWrapper?.Statistics?.ExecutionStoped();

                            if (taskResult.ResultCode == WorkUnitResultCode.Failed && _continueOnError == ContinueOnError.WarnAndContinue)
                            {
                                // We coerce the failing result to a successful result.
                                taskResult = new WorkUnitResult(WorkUnitResultCode.Success, taskResult.ActionCode, taskResult.Exception);
                            }

                            MSBuildEventSource.Log.ExecuteTaskStop(_taskNode?.Name, taskLoggingContext.BuildEventContext.TaskId);
                        }
                    }
                }
                else
                {
                    ErrorUtilities.VerifyThrow(howToExecuteTask == TaskExecutionMode.InferOutputsOnly, "should be inferring");

                    ErrorUtilities.VerifyThrow(
                        GatherTaskOutputs(null, howToExecuteTask, bucket),
                        "The method GatherTaskOutputs() should never fail when inferring task outputs.");

                    if (lookupHash != null)
                    {
                        List<string> overrideMessages = bucket.Lookup.GetPropertyOverrideMessages(lookupHash);
                        if (overrideMessages != null)
                        {
                            foreach (string s in overrideMessages)
                            {
                                _targetLoggingContext.LogCommentFromText(MessageImportance.Low, s);
                            }
                        }
                    }

                    taskResult = new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null);
                }
            }

            return taskResult;
        }

        /// <summary>
        /// Returns the set of parameters that can contribute to a task's identity, and their values for this particular task.
        /// </summary>
        private IDictionary<string, string> GatherTaskIdentityParameters(Expander<ProjectPropertyInstance, ProjectItemInstance> expander)
        {
            ErrorUtilities.VerifyThrowInternalNull(_taskNode, "taskNode"); // taskNode should never be null when we're calling this method.

            string msbuildArchitecture = expander.ExpandIntoStringAndUnescape(_taskNode.MSBuildArchitecture ?? String.Empty, ExpanderOptions.ExpandAll, _taskNode.MSBuildArchitectureLocation ?? ElementLocation.EmptyLocation);
            string msbuildRuntime = expander.ExpandIntoStringAndUnescape(_taskNode.MSBuildRuntime ?? String.Empty, ExpanderOptions.ExpandAll, _taskNode.MSBuildRuntimeLocation ?? ElementLocation.EmptyLocation);

            IDictionary<string, string> taskIdentityParameters = null;

            // only bother to create a task identity parameter set if we're putting anything in there -- otherwise,
            // a null set will be treated as equivalent to all parameters being "don't care".
            if (msbuildRuntime != String.Empty || msbuildArchitecture != String.Empty)
            {
                taskIdentityParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                msbuildArchitecture = msbuildArchitecture == String.Empty ? XMakeAttributes.MSBuildArchitectureValues.any : msbuildArchitecture.Trim();
                msbuildRuntime = msbuildRuntime == String.Empty ? XMakeAttributes.MSBuildRuntimeValues.any : msbuildRuntime.Trim();

                taskIdentityParameters.Add(XMakeAttributes.runtime, msbuildRuntime);
                taskIdentityParameters.Add(XMakeAttributes.architecture, msbuildArchitecture);
            }

            return taskIdentityParameters;
        }

#if FEATURE_APARTMENT_STATE
        /// <summary>
        /// Executes the task using an STA thread.
        /// </summary>
        /// <comment>
        /// STA thread launching also being used in XMakeCommandLine\OutOfProcTaskAppDomainWrapperBase.cs, InstantiateAndExecuteTaskInSTAThread method.
        /// Any bug fixes made to this code, please ensure that you also fix that code.
        /// </comment>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exception is caught and rethrown in the correct thread.")]
        private WorkUnitResult ExecuteTaskInSTAThread(ItemBucket bucket, TaskLoggingContext taskLoggingContext, IDictionary<string, string> taskIdentityParameters, TaskHost taskHost, TaskExecutionMode howToExecuteTask)
        {
            WorkUnitResult taskResult = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            Thread staThread = null;
            ExceptionDispatchInfo exceptionFromExecution = null;
            ManualResetEvent taskRunnerFinished = new ManualResetEvent(false);
            try
            {
                ThreadStart taskRunnerDelegate = delegate ()
                {
                    Lookup.Scope scope = bucket.Lookup.EnterScope("STA Thread for Task");
                    try
                    {
                        taskResult = InitializeAndExecuteTask(taskLoggingContext, bucket, taskIdentityParameters, taskHost, howToExecuteTask).Result;
                    }
                    catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                    {
                        exceptionFromExecution = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        scope.LeaveScope();
                        taskRunnerFinished.Set();
                    }
                };

                staThread = new Thread(taskRunnerDelegate);
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Name = "MSBuild STA task runner thread";
                staThread.CurrentCulture = _componentHost.BuildParameters.Culture;
                staThread.CurrentUICulture = _componentHost.BuildParameters.UICulture;
                staThread.Start();

                // TODO: Why not just Join on the thread???
                taskRunnerFinished.WaitOne();
            }
            finally
            {
                taskRunnerFinished.Close();
                taskRunnerFinished = null;
            }

            exceptionFromExecution?.Throw();

            return taskResult;
        }
#endif

        /// <summary>
        /// Logs a task skipped message if necessary.
        /// </summary>
        private void LogSkippedTask(ItemBucket bucket, TaskExecutionMode howToExecuteTask)
        {
            // If this is an Intrinsic task, it does not log skips.
            if (_taskNode != null)
            {
                if (howToExecuteTask == TaskExecutionMode.ExecuteTaskAndGatherOutputs)
                {
                    if (!_targetLoggingContext.LoggingService.OnlyLogCriticalEvents)
                    {
                        // Expand the expression for the Log.  Since we know the condition evaluated to false, leave unexpandable properties in the condition so as not to cause an error
                        string expanded = bucket.Expander.ExpandIntoStringAndUnescape(_targetChildInstance.Condition, ExpanderOptions.ExpandAll | ExpanderOptions.LeavePropertiesUnexpandedOnError | ExpanderOptions.Truncate, _targetChildInstance.ConditionLocation);

                        // Whilst we are within the processing of the task, we haven't actually started executing it, so
                        // our skip task message needs to be in the context of the target. However any errors should be reported
                        // at the point where the task appears in the project.
                        _targetLoggingContext.LogComment(
                            MessageImportance.Low,
                            "TaskSkippedFalseCondition",
                            _taskNode.Name,
                            _targetChildInstance.Condition,
                            expanded);
                    }
                }
            }
        }

        /// <summary>
        /// Runs an intrinsic task.
        /// </summary>
        private void ExecuteIntrinsicTask(ItemBucket bucket)
        {
            IntrinsicTask task = IntrinsicTask.InstantiateTask(
                _targetChildInstance,
                _targetLoggingContext,
                _buildRequestEntry.RequestConfiguration.Project,
                _taskExecutionHost.LogTaskInputs);

            task.ExecuteTask(bucket.Lookup);
        }

        /// <summary>
        /// Initializes and executes the task.
        /// </summary>
        private async Task<WorkUnitResult> InitializeAndExecuteTask(TaskLoggingContext taskLoggingContext, ItemBucket bucket, IDictionary<string, string> taskIdentityParameters, TaskHost taskHost, TaskExecutionMode howToExecuteTask)
        {
            if (!_taskExecutionHost.InitializeForBatch(taskLoggingContext, bucket, taskIdentityParameters))
            {
                ProjectErrorUtilities.ThrowInvalidProject(_targetChildInstance.Location, "TaskDeclarationOrUsageError", _taskNode.Name);
            }

            using var assemblyLoadsTracker = AssemblyLoadsTracker.StartTracking(taskLoggingContext, AssemblyLoadingContext.TaskRun, _taskExecutionHost?.TaskInstance?.GetType());

            try
            {
                // UNDONE: Move this and the task host.
                taskHost.LoggingContext = taskLoggingContext;
                WorkUnitResult executionResult = await ExecuteInstantiatedTask(_taskExecutionHost, taskLoggingContext, taskHost, bucket, howToExecuteTask);

                ErrorUtilities.VerifyThrow(executionResult != null, "Unexpected null execution result");

                return executionResult;
            }
            finally
            {
                _taskExecutionHost.CleanupForBatch();
            }
        }

        /// <summary>
        /// Recomputes the task's "ContinueOnError" setting.
        /// </summary>
        /// <param name="bucket">The bucket being executed.</param>
        /// <param name="taskHost">The task host to use.</param>
        /// <remarks>
        /// There are four possible values:
        /// false - Error and stop if the task fails.
        /// true - Warn and continue if the task fails.
        /// ErrorAndContinue - Error and continue if the task fails.
        /// WarnAndContinue - Same as true.
        /// </remarks>
        private void UpdateContinueOnError(ItemBucket bucket, TaskHost taskHost)
        {
            string continueOnErrorAttribute = _taskNode.ContinueOnError;
            _continueOnError = ContinueOnError.ErrorAndStop;

            if (_taskNode.ContinueOnErrorLocation != null)
            {
                string expandedValue = bucket.Expander.ExpandIntoStringAndUnescape(continueOnErrorAttribute, ExpanderOptions.ExpandAll, _taskNode.ContinueOnErrorLocation); // expand embedded item vectors after expanding properties and item metadata
                try
                {
                    if (String.Equals(XMakeAttributes.ContinueOnErrorValues.errorAndContinue, expandedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        _continueOnError = ContinueOnError.ErrorAndContinue;
                    }
                    else if (String.Equals(XMakeAttributes.ContinueOnErrorValues.warnAndContinue, expandedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        _continueOnError = ContinueOnError.WarnAndContinue;
                    }
                    else if (String.Equals(XMakeAttributes.ContinueOnErrorValues.errorAndStop, expandedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        _continueOnError = ContinueOnError.ErrorAndStop;
                    }
                    else
                    {
                        // if attribute doesn't exist, default to "false"
                        // otherwise, convert its value to a boolean
                        bool value = ConversionUtilities.ConvertStringToBool(expandedValue);
                        _continueOnError = value ? ContinueOnError.WarnAndContinue : ContinueOnError.ErrorAndStop;
                    }
                }
                catch (ArgumentException e)
                {
                    // handle errors in string-->bool conversion
                    ProjectErrorUtilities.ThrowInvalidProject(_taskNode.ContinueOnErrorLocation, "InvalidContinueOnErrorAttribute", _taskNode.Name, e.Message);
                }
            }

            // We need to access an internal method of the EngineProxy in order to update the value
            // of continueOnError that will be returned to the task when the task queries IBuildEngine for it
            taskHost.ContinueOnError = (_continueOnError != ContinueOnError.ErrorAndStop);
            taskHost.ConvertErrorsToWarnings = (_continueOnError == ContinueOnError.WarnAndContinue);
        }

        /// <summary>
        /// Execute a task object for a given bucket.
        /// </summary>
        /// <param name="taskExecutionHost">The host used to execute the task.</param>
        /// <param name="taskLoggingContext">The logging context.</param>
        /// <param name="taskHost">The task host for the task.</param>
        /// <param name="bucket">The batching bucket</param>
        /// <param name="howToExecuteTask">The task execution mode</param>
        /// <returns>The result of running the task.</returns>
        private async Task<WorkUnitResult> ExecuteInstantiatedTask(TaskExecutionHost taskExecutionHost, TaskLoggingContext taskLoggingContext, TaskHost taskHost, ItemBucket bucket, TaskExecutionMode howToExecuteTask)
        {
            UpdateContinueOnError(bucket, taskHost);

            bool taskResult = false;

            WorkUnitResultCode resultCode = WorkUnitResultCode.Success;
            WorkUnitActionCode actionCode = WorkUnitActionCode.Continue;

            if (!taskExecutionHost.SetTaskParameters(_taskNode.ParametersForBuild))
            {
                // The task cannot be initialized.
                ProjectErrorUtilities.ThrowInvalidProject(_targetChildInstance.Location, "TaskParametersError", _taskNode.Name, String.Empty);
            }
            else
            {
                bool taskReturned = false;
                Exception taskException = null;

                // If this is the MSBuild task, we need to execute it's special internal method.
                try
                {
                    if (taskExecutionHost.TaskInstance is MSBuild msbuildTask)
                    {
                        var undeclaredProjects = GetUndeclaredProjects(msbuildTask);

                        if (undeclaredProjects?.Count > 0)
                        {
                            _continueOnError = ContinueOnError.ErrorAndStop;

                            taskException = new InvalidProjectFileException(
                                taskHost.ProjectFileOfTaskNode,
                                taskHost.LineNumberOfTaskNode,
                                taskHost.ColumnNumberOfTaskNode,
                                0,
                                0,
                                ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                                    "UndeclaredMSBuildTasksNotAllowedInIsolatedGraphBuilds",
                                    string.Join(";", undeclaredProjects.Select(p => $"\"{p}\"")),
                                    taskExecutionHost.ProjectInstance.FullPath),
                                null,
                                null,
                                null);
                        }
                        else
                        {
                            _targetBuilderCallback.EnterMSBuildCallbackState();

                            try
                            {
                                taskResult = await msbuildTask.ExecuteInternal();
                            }
                            finally
                            {
                                _targetBuilderCallback.ExitMSBuildCallbackState();
                            }
                        }
                    }
                    else if (taskExecutionHost.TaskInstance is CallTarget callTargetTask)
                    {
                        taskResult = await callTargetTask.ExecuteInternal();
                    }
                    else
                    {
#if FEATURE_FILE_TRACKER
                        using (FullTracking.Track(taskLoggingContext.TargetLoggingContext.Target.Name, _taskNode.Name, _buildRequestEntry.ProjectRootDirectory, _buildRequestEntry.RequestConfiguration.Project.PropertiesToBuildWith))
#endif
                        {
                            taskResult = taskExecutionHost.Execute();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ExceptionHandling.IsCriticalException(ex) || Environment.GetEnvironmentVariable("MSBUILDDONOTCATCHTASKEXCEPTIONS") == "1")
                    {
                        taskLoggingContext.LogFatalTaskError(
                            ex,
                            new BuildEventFileInfo(_targetChildInstance.Location),
                            _taskNode.Name);

                        throw new CriticalTaskException(ex);
                    }

                    taskException = ex;
                }

                if (taskException == null)
                {
                    taskReturned = true;

                    // Set the property "MSBuildLastTaskResult" to reflect whether the task succeeded or not.
                    // The main use of this is if ContinueOnError is true -- so that the next task can consult the result.
                    // So we want it to be "false" even if ContinueOnError is true.
                    // The constants "true" and "false" should NOT be localized. They become property values.
                    bucket.Lookup.SetProperty(ProjectPropertyInstance.Create(ReservedPropertyNames.lastTaskResult, taskResult ? "true" : "false", true/* may be reserved */, _buildRequestEntry.RequestConfiguration.Project.IsImmutable));
                }
                else
                {
                    var type = taskException.GetType();

                    if (type == typeof(LoggerException))
                    {
                        // if a logger has failed, abort immediately
                        // Polite logger failure
                        _continueOnError = ContinueOnError.ErrorAndStop;

                        // Rethrow wrapped in order to avoid losing the callstack
                        throw new LoggerException(taskException.Message, taskException);
                    }
                    else if (type == typeof(InternalLoggerException))
                    {
                        // Logger threw arbitrary exception
                        _continueOnError = ContinueOnError.ErrorAndStop;
                        InternalLoggerException ex = taskException as InternalLoggerException;

                        // Rethrow wrapped in order to avoid losing the callstack
                        throw new InternalLoggerException(taskException.Message, taskException, ex.BuildEventArgs, ex.ErrorCode, ex.HelpKeyword, ex.InitializationException);
                    }
                    else if (type == typeof(ThreadAbortException))
                    {
#if !NET6_0_OR_GREATER && !NET6_0 // This is redundant but works around https://github.com/dotnet/sdk/issues/20700
                        Thread.ResetAbort();
#endif
                        _continueOnError = ContinueOnError.ErrorAndStop;

                        // Cannot rethrow wrapped as ThreadAbortException is sealed and has no appropriate constructor
                        // Stack will be lost
                        throw taskException;
                    }
                    else if (type == typeof(BuildAbortedException))
                    {
                        _continueOnError = ContinueOnError.ErrorAndStop;

                        // Rethrow wrapped in order to avoid losing the callstack
                        throw new BuildAbortedException(taskException.Message, (BuildAbortedException)taskException);
                    }
                    else if (type == typeof(CircularDependencyException))
                    {
                        _continueOnError = ContinueOnError.ErrorAndStop;
                        ProjectErrorUtilities.ThrowInvalidProject(taskLoggingContext.Task.Location, "CircularDependency", taskLoggingContext.TargetLoggingContext.Target.Name);
                    }
                    else if (type == typeof(InvalidProjectFileException))
                    {
                        // Just in case this came out of a task, make sure it's not
                        // marked as having been logged.
                        InvalidProjectFileException ipex = (InvalidProjectFileException)taskException;
                        ipex.HasBeenLogged = false;

                        if (_continueOnError != ContinueOnError.ErrorAndStop)
                        {
                            taskLoggingContext.LogInvalidProjectFileError(ipex);
                            taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
                        }
                        else
                        {
                            // Rethrow wrapped in order to avoid losing the callstack
                            throw new InvalidProjectFileException(ipex.Message, ipex);
                        }
                    }
                    else if (type == typeof(Exception) || type.GetTypeInfo().IsSubclassOf(typeof(Exception)))
                    {
                        // Occasionally, when debugging a very uncommon task exception, it is useful to loop the build with
                        // a debugger attached to break on 2nd chance exceptions.
                        // That requires that there needs to be a way to not catch here, by setting an environment variable.
                        if (ExceptionHandling.IsCriticalException(taskException) || (Environment.GetEnvironmentVariable("MSBUILDDONOTCATCHTASKEXCEPTIONS") == "1"))
                        {
                            // Wrapping in an Exception will unfortunately mean that this exception would fly through any IsCriticalException above.
                            // However, we should not have any, also we should not have stashed such an exception anyway.
                            throw new Exception(taskException.Message, taskException);
                        }

                        Exception exceptionToLog = taskException;

                        if (exceptionToLog is TargetInvocationException)
                        {
                            exceptionToLog = exceptionToLog.InnerException;
                        }

                        // handle any exception thrown by the task during execution
                        // NOTE: We catch ALL exceptions here, to attempt to completely isolate the Engine
                        // from failures in the task.
                        if (_continueOnError == ContinueOnError.WarnAndContinue)
                        {
                            taskLoggingContext.LogTaskWarningFromException(
                                exceptionToLog,
                                new BuildEventFileInfo(_targetChildInstance.Location),
                                _taskNode.Name);

                            // Log a message explaining why we converted the previous error into a warning.
                            taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
                        }
                        else
                        {
                            taskLoggingContext.LogFatalTaskError(
                                exceptionToLog,
                                new BuildEventFileInfo(_targetChildInstance.Location),
                                _taskNode.Name);
                        }
                    }
                    else
                    {
                        ErrorUtilities.ThrowInternalErrorUnreachable();
                    }
                }

                // When a task fails it must log an error. If a task fails to do so,
                // that is logged as an error. MSBuild tasks are an exception because
                // errors are not logged directly from them, but the tasks spawned by them.
                IBuildEngine be = taskExecutionHost.TaskInstance.BuildEngine;
                if (taskReturned // if the task returned
                    && !taskResult // and it returned false
                    && !taskLoggingContext.HasLoggedErrors // and it didn't log any errors
                    && (be is TaskHost th ? th.BuildRequestsSucceeded : false)
                    && !(_cancellationToken.CanBeCanceled && _cancellationToken.IsCancellationRequested)) // and it wasn't cancelled
                {
                    // Then decide how to log MSB4181
                    if (be is IBuildEngine7 be7 && be7.AllowFailureWithoutError)
                    {
                        // If it's allowed to fail without error, log as a message
                        taskLoggingContext.LogComment(MessageImportance.Normal, "TaskReturnedFalseButDidNotLogError", _taskNode.Name);
                    }
                    else if (_continueOnError == ContinueOnError.WarnAndContinue)
                    {
                        taskLoggingContext.LogWarning(null,
                            new BuildEventFileInfo(_targetChildInstance.Location),
                            "TaskReturnedFalseButDidNotLogError",
                            _taskNode.Name);

                        taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
                    }
                    else
                    {
                        taskLoggingContext.LogError(new BuildEventFileInfo(_targetChildInstance.Location),
                            "TaskReturnedFalseButDidNotLogError",
                            _taskNode.Name);
                    }
                }

                // If the task returned attempt to gather its outputs.  If gathering outputs fails set the taskResults
                // to false
                if (taskReturned)
                {
                    taskResult = GatherTaskOutputs(taskExecutionHost, howToExecuteTask, bucket) && taskResult;
                }

                // If the taskResults are false look at ContinueOnError.  If ContinueOnError=false (default)
                // mark the taskExecutedSuccessfully=false.  Otherwise let the task succeed but log a normal
                // pri message that says this task is continuing because ContinueOnError=true
                resultCode = taskResult ? WorkUnitResultCode.Success : WorkUnitResultCode.Failed;
                actionCode = WorkUnitActionCode.Continue;
                if (resultCode == WorkUnitResultCode.Failed)
                {
                    if (_continueOnError == ContinueOnError.ErrorAndStop)
                    {
                        actionCode = WorkUnitActionCode.Stop;
                    }
                    else
                    {
                        // This is the ErrorAndContinue or WarnAndContinue case...
                        string settingString = "true";
                        if (_taskNode.ContinueOnErrorLocation != null)
                        {
                            settingString = bucket.Expander.ExpandIntoStringAndUnescape(_taskNode.ContinueOnError, ExpanderOptions.ExpandAll, _taskNode.ContinueOnErrorLocation); // expand embedded item vectors after expanding properties and item metadata
                        }

                        taskLoggingContext.LogComment(
                            MessageImportance.Normal,
                            "TaskContinuedDueToContinueOnError",
                            "ContinueOnError",
                            _taskNode.Name,
                            settingString);

                        actionCode = WorkUnitActionCode.Continue;
                    }
                }
            }

            WorkUnitResult result = new WorkUnitResult(resultCode, actionCode, null);

            return result;
        }

        private List<string> GetUndeclaredProjects(MSBuild msbuildTask)
        {
            ProjectIsolationMode isolateProjects = _componentHost.BuildParameters.ProjectIsolationMode;
            if (isolateProjects == ProjectIsolationMode.False || isolateProjects == ProjectIsolationMode.MessageUponIsolationViolation)
            {
                return null;
            }

            var projectReferenceItems = _buildRequestEntry.RequestConfiguration.Project.GetItems(ItemTypeNames.ProjectReference);

            var declaredProjects = new HashSet<string>(projectReferenceItems.Count + 1, FileUtilities.PathComparer);

            foreach (var projectReferenceItem in projectReferenceItems)
            {
                declaredProjects.Add(FileUtilities.NormalizePath(projectReferenceItem.EvaluatedInclude));
            }

            // allow a project to msbuild itself
            declaredProjects.Add(FileUtilities.NormalizePath(_taskExecutionHost.ProjectInstance.FullPath));

            List<string> undeclaredProjects = null;

            foreach (var msbuildProject in msbuildTask.Projects)
            {
                var normalizedMSBuildProject = FileUtilities.NormalizePath(msbuildProject.ItemSpec);

                if (
                    !(declaredProjects.Contains(normalizedMSBuildProject)
                      || _buildRequestEntry.RequestConfiguration.ShouldSkipIsolationConstraintsForReference(normalizedMSBuildProject)))
                {
                    undeclaredProjects ??= new List<string>(projectReferenceItems.Count);
                    undeclaredProjects.Add(normalizedMSBuildProject);
                }
            }

            return undeclaredProjects;
        }

        /// <summary>
        /// Gathers task outputs in two ways:
        /// 1) Given an instantiated task that has finished executing, it extracts the outputs using .NET reflection.
        /// 2) Otherwise, it parses the task's output specifications and (statically) infers the outputs.
        /// </summary>
        /// <param name="taskExecutionHost">The task execution host.</param>
        /// <param name="howToExecuteTask">The task execution mode</param>
        /// <param name="bucket">The bucket to which the task execution belongs.</param>
        /// <returns>true, if successful</returns>
        private bool GatherTaskOutputs(TaskExecutionHost taskExecutionHost, TaskExecutionMode howToExecuteTask, ItemBucket bucket)
        {
            bool gatheredTaskOutputsSuccessfully = true;

            foreach (ProjectTaskInstanceChild taskOutputSpecification in _taskNode.Outputs)
            {
                // if the task's outputs are supposed to be gathered
                bool condition = ConditionEvaluator.EvaluateCondition(
                    taskOutputSpecification.Condition,
                    ParserOptions.AllowAll,
                    bucket.Expander,
                    ExpanderOptions.ExpandAll,
                    _buildRequestEntry.ProjectRootDirectory,
                    taskOutputSpecification.ConditionLocation,
                    FileSystems.Default,
                    _targetLoggingContext);

                if (condition)
                {
                    string taskParameterName = null;
                    bool outputTargetIsItem = false;
                    string outputTargetName = null;

                    // check where the outputs are going -- into a vector, or a property?
                    ProjectTaskOutputItemInstance taskOutputItemInstance = taskOutputSpecification as ProjectTaskOutputItemInstance;

                    if (taskOutputItemInstance != null)
                    {
                        // expand all embedded properties, item metadata and item vectors in the item type name
                        outputTargetIsItem = true;
                        outputTargetName = bucket.Expander.ExpandIntoStringAndUnescape(taskOutputItemInstance.ItemType, ExpanderOptions.ExpandAll, taskOutputItemInstance.ItemTypeLocation);
                        taskParameterName = taskOutputItemInstance.TaskParameter;

                        ProjectErrorUtilities.VerifyThrowInvalidProject(
                            outputTargetName.Length > 0,
                            taskOutputItemInstance.ItemTypeLocation,
                            "InvalidEvaluatedAttributeValue",
                            outputTargetName,
                            taskOutputItemInstance.ItemType,
                            XMakeAttributes.itemName,
                            XMakeElements.output);
                    }
                    else
                    {
                        ProjectTaskOutputPropertyInstance taskOutputPropertyInstance = taskOutputSpecification as ProjectTaskOutputPropertyInstance;
                        outputTargetIsItem = false;

                        // expand all embedded properties, item metadata and item vectors in the property name
                        outputTargetName = bucket.Expander.ExpandIntoStringAndUnescape(taskOutputPropertyInstance.PropertyName, ExpanderOptions.ExpandAll, taskOutputPropertyInstance.PropertyNameLocation);
                        taskParameterName = taskOutputPropertyInstance.TaskParameter;

                        ProjectErrorUtilities.VerifyThrowInvalidProject(
                            outputTargetName.Length > 0,
                            taskOutputPropertyInstance.PropertyNameLocation,
                            "InvalidEvaluatedAttributeValue",
                            outputTargetName,
                            taskOutputPropertyInstance.PropertyName,
                            XMakeAttributes.propertyName,
                            XMakeElements.output);
                    }

                    string unexpandedTaskParameterName = taskParameterName;
                    taskParameterName = bucket.Expander.ExpandIntoStringAndUnescape(taskParameterName, ExpanderOptions.ExpandAll, taskOutputSpecification.TaskParameterLocation);

                    ProjectErrorUtilities.VerifyThrowInvalidProject(
                        taskParameterName.Length > 0,
                        taskOutputSpecification.TaskParameterLocation,
                        "InvalidEvaluatedAttributeValue",
                        taskParameterName,
                        unexpandedTaskParameterName,
                        XMakeAttributes.taskParameter,
                        XMakeElements.output);

                    // if we're gathering outputs by .NET reflection
                    if (howToExecuteTask == TaskExecutionMode.ExecuteTaskAndGatherOutputs)
                    {
                        gatheredTaskOutputsSuccessfully = taskExecutionHost.GatherTaskOutputs(taskParameterName, taskOutputSpecification.Location, outputTargetIsItem, outputTargetName);
                    }
                    else
                    {
                        // If we're inferring outputs based on information in the task and <Output> tags
                        ErrorUtilities.VerifyThrow(howToExecuteTask == TaskExecutionMode.InferOutputsOnly, "should be inferring");

                        // UNDONE: Refactor this method to use the same flag/string paradigm we use above, rather than two strings and the task output spec.
                        InferTaskOutputs(bucket.Lookup, taskOutputSpecification, taskParameterName, outputTargetName, outputTargetName, bucket);
                    }
                }

                if (!gatheredTaskOutputsSuccessfully)
                {
                    break;
                }
            }

            return gatheredTaskOutputsSuccessfully;
        }

        /// <summary>
        /// Uses the given task output specification to (statically) infer the task's outputs.
        /// </summary>
        /// <param name="lookup">The lookup</param>
        /// <param name="taskOutputSpecification">The task output specification</param>
        /// <param name="taskParameterName">The task parameter name</param>
        /// <param name="itemName">can be null</param>
        /// <param name="propertyName">can be null</param>
        /// <param name="bucket">The bucket for the batch.</param>
        private void InferTaskOutputs(
            Lookup lookup,
            ProjectTaskInstanceChild taskOutputSpecification,
            string taskParameterName,
            string itemName,
            string propertyName,
            ItemBucket bucket)
        {
            string taskParameterAttribute = _taskNode.GetParameter(taskParameterName);

            if (taskParameterAttribute != null)
            {
                ProjectTaskOutputItemInstance taskItemInstance = taskOutputSpecification as ProjectTaskOutputItemInstance;
                if (taskItemInstance != null)
                {
                    // This is an output item.
                    // Expand only with properties first, so that expressions like Include="@(foo)" will transfer the metadata of the "foo" items as well, not just their item specs.
                    var outputItemSpecs = bucket.Expander.ExpandIntoStringListLeaveEscaped(taskParameterAttribute, ExpanderOptions.ExpandPropertiesAndMetadata, taskItemInstance.TaskParameterLocation);
                    ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(_buildRequestEntry.RequestConfiguration.Project, itemName);

                    foreach (string outputItemSpec in outputItemSpecs)
                    {
                        ICollection<ProjectItemInstance> items = bucket.Expander.ExpandIntoItemsLeaveEscaped(outputItemSpec, itemFactory, ExpanderOptions.ExpandItems, taskItemInstance.TaskParameterLocation);

                        lookup.AddNewItemsOfItemType(itemName, items);
                    }
                }
                else
                {
                    // This is an output property.
                    ProjectTaskOutputPropertyInstance taskPropertyInstance = (ProjectTaskOutputPropertyInstance)taskOutputSpecification;

                    string taskParameterValue = bucket.Expander.ExpandIntoStringAndUnescape(taskParameterAttribute, ExpanderOptions.ExpandAll, taskPropertyInstance.TaskParameterLocation);

                    if (!String.IsNullOrEmpty(taskParameterValue))
                    {
                        lookup.SetProperty(ProjectPropertyInstance.Create(propertyName, taskParameterValue, taskPropertyInstance.TaskParameterLocation, _buildRequestEntry.RequestConfiguration.Project.IsImmutable));
                    }
                }
            }
        }

        #endregion
    }
}
