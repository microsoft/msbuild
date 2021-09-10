// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using ElementLocation = Microsoft.Build.Construction.ElementLocation;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using ProjectLoggingContext = Microsoft.Build.BackEnd.Logging.ProjectLoggingContext;
using TargetLoggingContext = Microsoft.Build.BackEnd.Logging.TargetLoggingContext;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#if MSBUILDENABLEVSPROFILING 
using Microsoft.VisualStudio.Profiler;
#endif
namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Represents which state the target entry is currently in.
    /// </summary>
    internal enum TargetEntryState
    {
        /// <summary>
        /// The target's dependencies need to be evaluated and pushed onto the target stack.
        ///
        /// Transitions:
        /// Execution, ErrorExecution
        /// </summary>
        Dependencies,

        /// <summary>
        /// The target is ready to execute its tasks, batched as needed.
        ///
        /// Transitions:
        /// ErrorExecution, Completed
        /// </summary>
        Execution,

        /// <summary>
        /// The target is ready to provide error tasks.
        ///
        /// Transitions:
        /// None
        /// </summary>
        ErrorExecution,

        /// <summary>
        /// The target has finished building.  All of the results are in the Lookup.
        ///
        /// Transitions:
        /// None
        /// </summary>
        Completed
    }

    /// <summary>
    /// This class represents a single target in the TargetBuilder.  It maintains the state machine for a particular target as well as
    /// relevant information on outputs generated while a target is running.
    /// </summary>
    [DebuggerDisplay("Name={_targetSpecification.TargetName} State={_state} Result={_targetResult.ResultCode}")]
    internal class TargetEntry : IEquatable<TargetEntry>
    {
        /// <summary>
        /// The BuildRequestEntry to which this target invocation belongs
        /// </summary>
        private BuildRequestEntry _requestEntry;

        /// <summary>
        /// The specification of the target being built.
        /// </summary>
        private TargetSpecification _targetSpecification;

        /// <summary>
        /// The Target being built.  This will be null until the GetTargetInstance() is called, which
        /// will cause us to attempt to resolve the actual project instance.  At that point
        /// if the target doesn't exist, we will throw an InvalidProjectFileException.  We do this lazy
        /// evaluation because the 'target doesn't exist' message is not supposed to be emitted until
        /// the target is actually needed, as opposed to when it is specified, such as in an OnError
        /// clause, DependsOnTargets or on the command-line.
        /// </summary>
        private ProjectTargetInstance _target;

        /// <summary>
        /// The current state of this entry
        /// </summary>
        private TargetEntryState _state;

        /// <summary>
        /// The completion state of the target.
        /// </summary>
        private TargetResult _targetResult;

        /// <summary>
        /// The parent entry, which is waiting for us to complete before proceeding.
        /// </summary>
        private TargetEntry _parentTarget;

        /// <summary>
        /// Why the parent target built this target.
        /// </summary>
        private TargetBuiltReason _buildReason;

        /// <summary>
        /// The expander used to expand item and property markup to evaluated values.
        /// </summary>
        private Expander<ProjectPropertyInstance, ProjectItemInstance> _expander;

        /// <summary>
        /// The lookup containing our environment.
        /// </summary>
        private Lookup _baseLookup;

        /// <summary>
        /// The build component host.
        /// </summary>
        private IBuildComponentHost _host;

        /// <summary>
        /// The target builder callback
        /// </summary>
        private ITargetBuilderCallback _targetBuilderCallback;

        /// <summary>
        /// A queue of legacy CallTarget lookup scopes to leave when this target is finished.
        /// </summary>
        private Stack<Lookup.Scope> _legacyCallTargetScopes;

        /// <summary>
        /// The cancellation token.
        /// </summary>
        private CancellationToken _cancellationToken;

        /// <summary>
        /// Flag indicating whether we are currently executing this target.  Used for assertions.
        /// </summary>
        private bool _isExecuting;

        /// <summary>
        /// The current task builder.
        /// </summary>
        private ITaskBuilder _currentTaskBuilder;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="requestEntry">The build request entry for the target.</param>
        /// <param name="targetBuilderCallback">The target builder callback.</param>
        /// <param name="targetSpecification">The specification for the target to build.</param>
        /// <param name="baseLookup">The lookup to use.</param>
        /// <param name="parentTarget">The parent of this entry, if any.</param>
        /// <param name="buildReason">The reason the parent built this target.</param>
        /// <param name="host">The Build Component Host to use.</param>
        /// <param name="stopProcessingOnCompletion">True if the target builder should stop processing the current target stack when this target is complete.</param>
        internal TargetEntry(BuildRequestEntry requestEntry, ITargetBuilderCallback targetBuilderCallback, TargetSpecification targetSpecification, Lookup baseLookup, TargetEntry parentTarget, TargetBuiltReason buildReason, IBuildComponentHost host, bool stopProcessingOnCompletion)
        {
            ErrorUtilities.VerifyThrowArgumentNull(requestEntry, nameof(requestEntry));
            ErrorUtilities.VerifyThrowArgumentNull(targetBuilderCallback, nameof(targetBuilderCallback));
            ErrorUtilities.VerifyThrowArgumentNull(targetSpecification, "targetName");
            ErrorUtilities.VerifyThrowArgumentNull(baseLookup, "lookup");
            ErrorUtilities.VerifyThrowArgumentNull(host, nameof(host));

            _requestEntry = requestEntry;
            _targetBuilderCallback = targetBuilderCallback;
            _targetSpecification = targetSpecification;
            _parentTarget = parentTarget;
            _buildReason = buildReason;
            _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(baseLookup, baseLookup);
            _state = TargetEntryState.Dependencies;
            _baseLookup = baseLookup;
            _host = host;
            this.StopProcessingOnCompletion = stopProcessingOnCompletion;
        }

        /// <summary>
        /// Gets or sets a flag indicating if this entry is the result of being listed as an error target in
        /// an OnError clause.
        /// </summary>
        internal bool ErrorTarget
        {
            get;
            set;
        }

        /// <summary>
        /// Sets or sets the location from which this target was referred.
        /// </summary>
        internal ElementLocation ReferenceLocation
        {
            get { return _targetSpecification.ReferenceLocation; }
        }

        /// <summary>
        /// Gets or sets a flag indicating that the target builder should stop processing the target
        /// stack when this target completes.
        /// </summary>
        internal bool StopProcessingOnCompletion
        {
            get;
            set;
        }

        /// <summary>
        /// Retrieves the name of the target.
        /// </summary>
        internal string Name
        {
            get
            {
                return _targetSpecification.TargetName;
            }
        }

        /// <summary>
        /// Gets the current state of the target
        /// </summary>
        internal TargetEntryState State
        {
            get
            {
                return _state;
            }
        }

        /// <summary>
        /// The result of this target.
        /// </summary>
        internal TargetResult Result
        {
            get
            {
                return _targetResult;
            }
        }

        /// <summary>
        /// Retrieves the Lookup this target was initialized with, including any modifications which have
        /// been made to it while running.
        /// </summary>
        internal Lookup Lookup
        {
            get
            {
                return _baseLookup;
            }
        }

        /// <summary>
        /// The target contained by the entry.
        /// </summary>
        internal ProjectTargetInstance Target
        {
            get
            {
                if (_target == null)
                {
                    GetTargetInstance();
                }

                return _target;
            }
        }

        /// <summary>
        /// The build request entry to which this target belongs.
        /// </summary>
        internal BuildRequestEntry RequestEntry
        {
            get
            {
                return _requestEntry;
            }
        }

        /// <summary>
        /// The target entry for which we are a dependency.
        /// </summary>
        internal TargetEntry ParentEntry
        {
            get
            {
                return _parentTarget;
            }
        }

        /// <summary>
        /// Why the parent target built this target.
        /// </summary>
        internal TargetBuiltReason BuildReason
        {
            get
            {
                return _buildReason;
            }
        }

        #region IEquatable<TargetEntry> Members

        /// <summary>
        /// Determines equivalence of two target entries.  They are considered the same
        /// if their names are the same.
        /// </summary>
        /// <param name="other">The entry to which we compare this one.</param>
        /// <returns>True if they are equivalent, false otherwise.</returns>
        public bool Equals(TargetEntry other)
        {
            return String.Equals(this.Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        /// <summary>
        /// Retrieves the list of dependencies this target needs to have built and moves the target to the next state.
        /// Never returns null.
        /// </summary>
        /// <returns>A collection of targets on which this target depends.</returns>
        internal List<TargetSpecification> GetDependencies(ProjectLoggingContext projectLoggingContext)
        {
            VerifyState(_state, TargetEntryState.Dependencies);

            // Resolve the target now, since from this point on we are going to be doing work with the actual instance.
            GetTargetInstance();

            // We first make sure no batching was attempted with the target's condition.
            // UNDONE: (Improvement) We want to allow this actually.  In order to do this we need to determine what the
            // batching buckets are, and if there are any which aren't empty, return our list of dependencies.
            // Only in the case where all bucket conditions fail do we want to skip the target entirely (and
            // this skip building the dependencies.)
            if (ExpressionShredder.ContainsMetadataExpressionOutsideTransform(_target.Condition))
            {
                ProjectErrorUtilities.ThrowInvalidProject(_target.ConditionLocation, "TargetConditionHasInvalidMetadataReference", _target.Name, _target.Condition);
            }

            // If condition is false (based on propertyBag), set this target's state to
            // "Skipped" since we won't actually build it.
            bool condition = ConditionEvaluator.EvaluateCondition
                (
                _target.Condition,
                ParserOptions.AllowPropertiesAndItemLists,
                _expander,
                ExpanderOptions.ExpandPropertiesAndItems,
                _requestEntry.ProjectRootDirectory,
                _target.ConditionLocation,
                projectLoggingContext.LoggingService,
                projectLoggingContext.BuildEventContext,
                FileSystems.Default);

            if (!condition)
            {
                _targetResult = new TargetResult(
                    Array.Empty<TaskItem>(),
                    new WorkUnitResult(WorkUnitResultCode.Skipped, WorkUnitActionCode.Continue, null),
                    projectLoggingContext.BuildEventContext);
                _state = TargetEntryState.Completed;

                if (!projectLoggingContext.LoggingService.OnlyLogCriticalEvents)
                {
                    // Expand the expression for the Log.  Since we know the condition evaluated to false, leave unexpandable properties in the condition so as not to cause an error
                    string expanded = _expander.ExpandIntoStringAndUnescape(_target.Condition, ExpanderOptions.ExpandPropertiesAndItems | ExpanderOptions.LeavePropertiesUnexpandedOnError | ExpanderOptions.Truncate, _target.ConditionLocation);

                    // By design: Not building dependencies. This is what NAnt does too.
                    // NOTE: In the original code, this was logged from the target logging context.  However, the target
                    // hadn't been "started" by then, so you'd get a target message outside the context of a started
                    // target.  In the Task builder (and original Task Engine), a Task Skipped message would be logged in
                    // the context of the target, not the task.  This should be the same, especially given that we
                    // wish to allow batching on the condition of a target.
                    var skippedTargetEventArgs = new TargetSkippedEventArgs(message: null)
                    {
                        BuildEventContext = projectLoggingContext.BuildEventContext,
                        TargetName = _target.Name,
                        TargetFile = _target.Location.File,
                        ParentTarget = ParentEntry?.Target?.Name,
                        BuildReason = BuildReason,
                        SkipReason = TargetSkipReason.ConditionWasFalse,
                        Condition = _target.Condition,
                        EvaluatedCondition = expanded
                    };

                    projectLoggingContext.LogBuildEvent(skippedTargetEventArgs);
                }

                return new List<TargetSpecification>();
            }

            var dependencies = _expander.ExpandIntoStringListLeaveEscaped(_target.DependsOnTargets, ExpanderOptions.ExpandPropertiesAndItems, _target.DependsOnTargetsLocation);
            List<TargetSpecification> dependencyTargets = new List<TargetSpecification>();
            foreach (string escapedDependency in dependencies)
            {
                string dependencyTargetName = EscapingUtilities.UnescapeAll(escapedDependency);
                dependencyTargets.Add(new TargetSpecification(dependencyTargetName, _target.DependsOnTargetsLocation));
            }

            _state = TargetEntryState.Execution;

            return dependencyTargets;
        }

        /// <summary>
        /// Runs all of the tasks for this target, batched as necessary.
        /// </summary>
        internal async Task ExecuteTarget(ITaskBuilder taskBuilder, BuildRequestEntry requestEntry, ProjectLoggingContext projectLoggingContext, CancellationToken cancellationToken)
        {
#if MSBUILDENABLEVSPROFILING 
            try
            {
                string beginTargetBuild = String.Format(CultureInfo.CurrentCulture, "Build Target {0} in Project {1} - Start", this.Name, projectFullPath);
                DataCollection.CommentMarkProfile(8800, beginTargetBuild);
#endif 

            try
            {
                VerifyState(_state, TargetEntryState.Execution);
                ErrorUtilities.VerifyThrow(!_isExecuting, "Target {0} is already executing", _target.Name);
                _cancellationToken = cancellationToken;
                _isExecuting = true;

                // Generate the batching buckets.  Note that each bucket will get a lookup based on the baseLookup.  This lookup will be in its
                // own scope, which we will collapse back down into the baseLookup at the bottom of the function.
                List<ItemBucket> buckets = BatchingEngine.PrepareBatchingBuckets(GetBatchableParametersForTarget(), _baseLookup, _target.Location);

                WorkUnitResult aggregateResult = new WorkUnitResult();
                TargetLoggingContext targetLoggingContext = null;
                bool targetSuccess = false;
                int numberOfBuckets = buckets.Count;
                string projectFullPath = requestEntry.RequestConfiguration.ProjectFullPath;

                string parentTargetName = null;
                if (ParentEntry?.Target != null)
                {
                    parentTargetName = ParentEntry.Target.Name;
                }

                for (int i = 0; i < numberOfBuckets; i++)
                {
                    ItemBucket bucket = buckets[i];

                    // If one of the buckets failed, stop building.
                    if (aggregateResult.ActionCode == WorkUnitActionCode.Stop)
                    {
                        break;
                    }

                    targetLoggingContext = projectLoggingContext.LogTargetBatchStarted(projectFullPath, _target, parentTargetName, _buildReason);
                    WorkUnitResult bucketResult = null;
                    targetSuccess = false;

                    Lookup.Scope entryForInference = null;
                    Lookup.Scope entryForExecution = null;

                    try
                    {
                        // This isn't really dependency analysis.  This is up-to-date checking.  Based on this we will be able to determine if we should
                        // run tasks in inference or execution mode (or both) or just skip them altogether.
                        ItemDictionary<ProjectItemInstance> changedTargetInputs;
                        ItemDictionary<ProjectItemInstance> upToDateTargetInputs;
                        Lookup lookupForInference;
                        Lookup lookupForExecution;

                        // UNDONE: (Refactor) Refactor TargetUpToDateChecker to take a logging context, not a logging service.
                        MSBuildEventSource.Log.TargetUpToDateStart();
                        TargetUpToDateChecker dependencyAnalyzer = new TargetUpToDateChecker(requestEntry.RequestConfiguration.Project, _target, targetLoggingContext.LoggingService, targetLoggingContext.BuildEventContext);
                        DependencyAnalysisResult dependencyResult = dependencyAnalyzer.PerformDependencyAnalysis(bucket, out changedTargetInputs, out upToDateTargetInputs);
                        MSBuildEventSource.Log.TargetUpToDateStop((int)dependencyResult);

                        switch (dependencyResult)
                        {
                            // UNDONE: Need to enter/leave debugger scope properly for the <Target> element.
                            case DependencyAnalysisResult.FullBuild:
                            case DependencyAnalysisResult.IncrementalBuild:
                            case DependencyAnalysisResult.SkipUpToDate:
                                // Create the lookups used to hold the current set of properties and items
                                lookupForInference = bucket.Lookup;
                                lookupForExecution = bucket.Lookup.Clone();

                                // Push the lookup stack up one so that we are only modifying items and properties in that scope.
                                entryForInference = lookupForInference.EnterScope("ExecuteTarget() Inference");
                                entryForExecution = lookupForExecution.EnterScope("ExecuteTarget() Execution");

                                // if we're doing an incremental build, we need to effectively run the task twice -- once
                                // to infer the outputs for up-to-date input items, and once to actually execute the task;
                                // as a result we need separate sets of item and property collections to track changes
                                if (dependencyResult == DependencyAnalysisResult.IncrementalBuild)
                                {
                                    // subset the relevant items to those that are up-to-date
                                    foreach (string itemType in upToDateTargetInputs.ItemTypes)
                                    {
                                        lookupForInference.PopulateWithItems(itemType, upToDateTargetInputs[itemType]);
                                    }

                                    // subset the relevant items to those that have changed
                                    foreach (string itemType in changedTargetInputs.ItemTypes)
                                    {
                                        lookupForExecution.PopulateWithItems(itemType, changedTargetInputs[itemType]);
                                    }
                                }

                                // We either have some work to do or at least we need to infer outputs from inputs.
                                bucketResult = await ProcessBucket(taskBuilder, targetLoggingContext, GetTaskExecutionMode(dependencyResult), lookupForInference, lookupForExecution);

                                // Now aggregate the result with the existing known results.  There are four rules, assuming the target was not 
                                // skipped due to being up-to-date:
                                // 1. If this bucket failed or was cancelled, the aggregate result is failure.
                                // 2. If this bucket Succeeded and we have not previously failed, the aggregate result is a success.
                                // 3. Otherwise, the bucket was skipped, which has no effect on the aggregate result.
                                // 4. If the bucket's action code says to stop, then we stop, regardless of the success or failure state.
                                if (dependencyResult != DependencyAnalysisResult.SkipUpToDate)
                                {
                                    aggregateResult = aggregateResult.AggregateResult(bucketResult);
                                }
                                else
                                {
                                    if (aggregateResult.ResultCode == WorkUnitResultCode.Skipped)
                                    {
                                        aggregateResult = aggregateResult.AggregateResult(new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null));
                                    }
                                }

                                // Pop the lookup scopes, causing them to collapse their values back down into the 
                                // bucket's lookup.
                                // NOTE: this order is important because when we infer outputs, we are trying
                                // to produce the same results as would be produced from a full build; as such
                                // if we're doing both the infer and execute steps, we want the outputs from
                                // the execute step to override the outputs of the infer step -- this models
                                // the full build scenario more correctly than if the steps were reversed
                                entryForInference.LeaveScope();
                                entryForInference = null;
                                entryForExecution.LeaveScope();
                                entryForExecution = null;
                                targetSuccess = (bucketResult?.ResultCode == WorkUnitResultCode.Success);
                                break;

                            case DependencyAnalysisResult.SkipNoInputs:
                            case DependencyAnalysisResult.SkipNoOutputs:
                                // We have either no inputs or no outputs, so there is nothing to do.
                                targetSuccess = true;
                                break;
                        }
                    }
                    catch (InvalidProjectFileException e)
                    {
                        // Make sure the Invalid Project error gets logged *before* TargetFinished.  Otherwise,
                        // the log is confusing.
                        targetLoggingContext.LogInvalidProjectFileError(e);
                        entryForInference?.LeaveScope();
                        entryForExecution?.LeaveScope();
                        aggregateResult = aggregateResult.AggregateResult(new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null));
                    }
                    finally
                    {
                        // Don't log the last target finished event until we can process the target outputs as we want to attach them to the 
                        // last target batch.
                        if (targetLoggingContext != null && i < numberOfBuckets - 1)
                        {
                            targetLoggingContext.LogTargetBatchFinished(projectFullPath, targetSuccess, null);
                            targetLoggingContext = null;
                        }
                    }
                }

                // Produce the final results.
                List<TaskItem> targetOutputItems = new List<TaskItem>();

                try
                {
                    // If any legacy CallTarget operations took place, integrate them back in to the main lookup now.
                    LeaveLegacyCallTargetScopes();

                    // Publish the items for each bucket back into the baseLookup.  Note that EnterScope() was actually called on each
                    // bucket inside of the ItemBucket constructor, which is why you don't see it anywhere around here.
                    foreach (ItemBucket bucket in buckets)
                    {
                        bucket.LeaveScope();
                    }

                    string targetReturns = _target.Returns;
                    ElementLocation targetReturnsLocation = _target.ReturnsLocation;

                    // If there are no targets in the project file that use the "Returns" attribute, that means that we 
                    // revert to the legacy behavior in the case where Returns is not specified (null, rather
                    // than the empty string, which indicates no returns).  Legacy behavior is for all 
                    // of the target's Outputs to be returned. 
                    // On the other hand, if there is at least one target in the file that uses the Returns attribute, 
                    // then all targets in the file are run according to the new behaviour (return nothing unless otherwise
                    // specified by the Returns attribute). 
                    if (targetReturns == null)
                    {
                        if (!_target.ParentProjectSupportsReturnsAttribute)
                        {
                            targetReturns = _target.Outputs;
                            targetReturnsLocation = _target.OutputsLocation;
                        }
                    }

                    if (!String.IsNullOrEmpty(targetReturns))
                    {
                        // Determine if we should keep duplicates.
                        bool keepDupes = ConditionEvaluator.EvaluateCondition
                                 (
                                 _target.KeepDuplicateOutputs,
                                 ParserOptions.AllowPropertiesAndItemLists,
                                 _expander,
                                 ExpanderOptions.ExpandPropertiesAndItems,
                                 requestEntry.ProjectRootDirectory,
                                 _target.KeepDuplicateOutputsLocation,
                                 projectLoggingContext.LoggingService,
                                 projectLoggingContext.BuildEventContext, FileSystems.Default);

                        // NOTE: we need to gather the outputs in batches, because the output specification may reference item metadata
                        // Also, we are using the baseLookup, which has possibly had changes made to it since the project started.  Because of this, the
                        // set of outputs calculated here may differ from those which would have been calculated at the beginning of the target.  It is 
                        // assumed the user intended this.
                        List<ItemBucket> batchingBuckets = BatchingEngine.PrepareBatchingBuckets(GetBatchableParametersForTarget(), _baseLookup, _target.Location);

                        if (keepDupes)
                        {
                            foreach (ItemBucket bucket in batchingBuckets)
                            {
                                targetOutputItems.AddRange(bucket.Expander.ExpandIntoTaskItemsLeaveEscaped(targetReturns, ExpanderOptions.ExpandAll, targetReturnsLocation));
                            }
                        }
                        else
                        {
                            HashSet<TaskItem> addedItems = new HashSet<TaskItem>();
                            foreach (ItemBucket bucket in batchingBuckets)
                            {
                                IList<TaskItem> itemsToAdd = bucket.Expander.ExpandIntoTaskItemsLeaveEscaped(targetReturns, ExpanderOptions.ExpandAll, targetReturnsLocation);

                                foreach (TaskItem item in itemsToAdd)
                                {
                                    if (!addedItems.Contains(item))
                                    {
                                        targetOutputItems.Add(item);
                                        addedItems.Add(item);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // log the last target finished since we now have the target outputs. 
                    targetLoggingContext?.LogTargetBatchFinished(projectFullPath, targetSuccess, targetOutputItems?.Count > 0 ? targetOutputItems : null);
                }

                _targetResult = new TargetResult(targetOutputItems.ToArray(), aggregateResult, targetLoggingContext?.BuildEventContext);

                if (aggregateResult.ResultCode == WorkUnitResultCode.Failed && aggregateResult.ActionCode == WorkUnitActionCode.Stop)
                {
                    _state = TargetEntryState.ErrorExecution;
                }
                else
                {
                    _state = TargetEntryState.Completed;
                }
            }
            finally
            {
                _isExecuting = false;
            }
#if MSBUILDENABLEVSPROFILING 
            }
            finally
            {
                string endTargetBuild = String.Format(CultureInfo.CurrentCulture, "Build Target {0} in Project {1} - End", this.Name, projectFullPath);
                DataCollection.CommentMarkProfile(8801, endTargetBuild);
            }
#endif
        }

        /// <summary>
        /// Retrieves the error targets for this target.
        /// </summary>
        /// <param name="projectLoggingContext">The project logging context.</param>
        /// <returns>A list of error targets.</returns>
        internal List<TargetSpecification> GetErrorTargets(ProjectLoggingContext projectLoggingContext)
        {
            VerifyState(_state, TargetEntryState.ErrorExecution);
            ErrorUtilities.VerifyThrow(_legacyCallTargetScopes == null, "We should have already left any legacy call target scopes.");

            List<TargetSpecification> allErrorTargets = new List<TargetSpecification>(_target.OnErrorChildren.Count);

            foreach (ProjectOnErrorInstance errorTargetInstance in _target.OnErrorChildren)
            {
                bool condition = ConditionEvaluator.EvaluateCondition
                (
                    errorTargetInstance.Condition,
                    ParserOptions.AllowPropertiesAndItemLists,
                    _expander,
                    ExpanderOptions.ExpandPropertiesAndItems,
                    _requestEntry.ProjectRootDirectory,
                    errorTargetInstance.ConditionLocation,
                    projectLoggingContext.LoggingService,
                    projectLoggingContext.BuildEventContext, FileSystems.Default);

                if (condition)
                {
                    var errorTargets = _expander.ExpandIntoStringListLeaveEscaped(errorTargetInstance.ExecuteTargets, ExpanderOptions.ExpandPropertiesAndItems, errorTargetInstance.ExecuteTargetsLocation);

                    foreach (string escapedErrorTarget in errorTargets)
                    {
                        string errorTargetName = EscapingUtilities.UnescapeAll(escapedErrorTarget);
                        allErrorTargets.Add(new TargetSpecification(errorTargetName, errorTargetInstance.ExecuteTargetsLocation));
                    }
                }
            }

            // If this target never executed (for instance, because one of its dependencies errored) then we need to
            // create a result for this target to report when it gets to the Completed state.
            if (_targetResult == null)
            {
                _targetResult = new TargetResult(Array.Empty<TaskItem>(), new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null));
            }

            _state = TargetEntryState.Completed;

            return allErrorTargets;
        }

        /// <summary>
        /// Gathers the results from the target into the base lookup of the target.
        /// </summary>
        /// <returns>The base lookup for this target.</returns>
        internal TargetResult GatherResults()
        {
            VerifyState(_state, TargetEntryState.Completed);
            ErrorUtilities.VerifyThrow(_legacyCallTargetScopes == null, "We should have already left any legacy call target scopes.");

            // By now all of the bucket lookups have been collapsed into this lookup, which we can return.
            return _targetResult;
        }

        /// <summary>
        /// Enters a legacy calltarget scope.
        /// </summary>
        /// <param name="lookup">The lookup to enter with.</param>
        internal void EnterLegacyCallTargetScope(Lookup lookup)
        {
            if (_legacyCallTargetScopes == null)
            {
                _legacyCallTargetScopes = new Stack<Lookup.Scope>();
            }

            _legacyCallTargetScopes.Push(lookup.EnterScope("EnterLegacyCallTargetScope()"));
        }

        /// <summary>
        /// This method is used by the Target Builder to indicate that the target should run in error mode rather than normal mode.
        /// </summary>
        internal void MarkForError()
        {
            ErrorUtilities.VerifyThrow(_state != TargetEntryState.Completed, "State must not be Completed. State is {0}.", _state);
            _state = TargetEntryState.ErrorExecution;
        }

        /// <summary>
        /// This method is used by the Target Builder to indicate that a child of this target has failed and that work should not
        /// continue in Completed / Skipped mode. We do not want to mark the state to run in ErrorExecution mode so that the
        /// OnError targets do not run (the target was skipped due to condition so OnError targets should not run).
        /// </summary>
        internal void MarkForStop()
        {
            ErrorUtilities.VerifyThrow(_state == TargetEntryState.Completed, "State must be Completed. State is {0}.", _state);
            ErrorUtilities.VerifyThrow(_targetResult.ResultCode == TargetResultCode.Skipped, "ResultCode must be Skipped. ResultCode is {0}.", _state);
            ErrorUtilities.VerifyThrow(_targetResult.WorkUnitResult.ActionCode == WorkUnitActionCode.Continue, "ActionCode must be Continue. ActionCode is {0}.", _state);

            _targetResult.WorkUnitResult.ActionCode = WorkUnitActionCode.Stop;
        }

        /// <summary>
        /// Leaves all the call target scopes in the order they were entered.
        /// </summary>
        internal void LeaveLegacyCallTargetScopes()
        {
            if (_legacyCallTargetScopes != null)
            {
                while (_legacyCallTargetScopes.Count != 0)
                {
                    Lookup.Scope entry = _legacyCallTargetScopes.Pop();
                    entry.LeaveScope();
                }

                _legacyCallTargetScopes = null;
            }
        }

        /// <summary>
        /// Walks through the set of tasks for this target and processes them by handing them off to the TaskBuilder.
        /// </summary>
        /// <returns>
        /// The result of the tasks, based on the last task which ran.
        /// </returns>
        private async Task<WorkUnitResult> ProcessBucket(ITaskBuilder taskBuilder, TargetLoggingContext targetLoggingContext, TaskExecutionMode mode, Lookup lookupForInference, Lookup lookupForExecution)
        {
            WorkUnitResultCode aggregatedTaskResult = WorkUnitResultCode.Success;
            WorkUnitActionCode finalActionCode = WorkUnitActionCode.Continue;
            WorkUnitResult lastResult = new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null);

            try
            {
                // Grab the task builder so if cancel is called it will have something to operate on.
                _currentTaskBuilder = taskBuilder;

                int currentTask = 0;

                // Walk through all of the tasks and execute them in order.
                for (; (currentTask < _target.Children.Count) && !_cancellationToken.IsCancellationRequested; ++currentTask)
                {
                    ProjectTargetInstanceChild targetChildInstance = _target.Children[currentTask];

                    // Execute the task.
                    lastResult = await taskBuilder.ExecuteTask(targetLoggingContext, _requestEntry, _targetBuilderCallback, targetChildInstance, mode, lookupForInference, lookupForExecution, _cancellationToken);

                    if (lastResult.ResultCode == WorkUnitResultCode.Failed)
                    {
                        aggregatedTaskResult = WorkUnitResultCode.Failed;
                    }
                    else if (lastResult.ResultCode == WorkUnitResultCode.Success && aggregatedTaskResult != WorkUnitResultCode.Failed)
                    {
                        aggregatedTaskResult = WorkUnitResultCode.Success;
                    }

                    if (lastResult.ActionCode == WorkUnitActionCode.Stop)
                    {
                        finalActionCode = WorkUnitActionCode.Stop;
                        break;
                    }
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    aggregatedTaskResult = WorkUnitResultCode.Canceled;
                    finalActionCode = WorkUnitActionCode.Stop;
                }
            }
            finally
            {
                _currentTaskBuilder = null;
            }

            return new WorkUnitResult(aggregatedTaskResult, finalActionCode, lastResult.Exception);
        }

        /// <summary>
        /// Gets the task execution mode based
        /// </summary>
        /// <param name="analysis">The result of the up-to-date check.</param>
        /// <returns>The mode to be used to execute tasks.</returns>
        private TaskExecutionMode GetTaskExecutionMode(DependencyAnalysisResult analysis)
        {
            TaskExecutionMode executionMode;
            if ((analysis == DependencyAnalysisResult.SkipUpToDate) ||
                (analysis == DependencyAnalysisResult.IncrementalBuild))
            {
                executionMode = TaskExecutionMode.InferOutputsOnly;
            }
            else
            {
                executionMode = TaskExecutionMode.ExecuteTaskAndGatherOutputs;
            }

            // Execute the task using the items that need to be (re)built
            if ((analysis == DependencyAnalysisResult.FullBuild) ||
                (analysis == DependencyAnalysisResult.IncrementalBuild))
            {
                executionMode |= TaskExecutionMode.ExecuteTaskAndGatherOutputs;
            }

            return executionMode;
        }

        /// <summary>
        /// Verifies that the target's state is as expected.
        /// </summary>
        /// <param name="actual">The actual value</param>
        /// <param name="expected">The expected value</param>
        private void VerifyState(TargetEntryState actual, TargetEntryState expected)
        {
            ErrorUtilities.VerifyThrow(actual == expected, "Expected state {1}.  Got {0}", actual, expected);
        }

        /// <summary>
        /// Gets the list of parameters which are batchable for a target
        /// PERF: (Refactor) This used to be a method on the target, and it would
        /// cache its values so this would only be computed once for each
        /// target.  We should consider doing something similar for perf reasons.
        /// </summary>
        /// <returns>A list of batchable parameters</returns>
        private List<string> GetBatchableParametersForTarget()
        {
            List<string> batchableTargetParameters = new List<string>();

            if (_target.Inputs.Length > 0)
            {
                batchableTargetParameters.Add(_target.Inputs);
            }

            if (_target.Outputs.Length > 0)
            {
                batchableTargetParameters.Add(_target.Outputs);
            }

            if (!string.IsNullOrEmpty(_target.Returns))
            {
                batchableTargetParameters.Add(_target.Returns);
            }

            return batchableTargetParameters;
        }

        /// <summary>
        /// Resolves the target.  If it doesn't exist in the project, throws an InvalidProjectFileException.
        /// </summary>
        private void GetTargetInstance()
        {
            _requestEntry.RequestConfiguration.Project.Targets.TryGetValue(_targetSpecification.TargetName, out _target);

            ProjectErrorUtilities.VerifyThrowInvalidProject
                (
                _target != null,
                _targetSpecification.ReferenceLocation ?? _requestEntry.RequestConfiguration.Project.ProjectFileLocation,
                "TargetDoesNotExist",
                _targetSpecification.TargetName
                );
        }
    }
}
