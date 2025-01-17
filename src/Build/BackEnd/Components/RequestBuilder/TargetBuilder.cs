// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Collections;
using Microsoft.Build.Eventing;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using BuildAbortedException = Microsoft.Build.Exceptions.BuildAbortedException;
using ElementLocation = Microsoft.Build.Construction.ElementLocation;
using ProjectLoggingContext = Microsoft.Build.BackEnd.Logging.ProjectLoggingContext;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// The Target Builder is responsible for building a single target within a given project.
    /// </summary>
    /// <remarks>
    /// The Target Builder is a stack machine which builds project targets.  Each time a target needs to be built, it is
    /// pushed onto the stack.  The main loop for the Target Builder simply evaluates the top item on the stack to determine
    /// which action to take.  These actions comprise the target state machine, as represented by the states of the
    /// TargetEntry object.
    ///
    /// When a target completes, all of its outputs are available in the Lookup contained in the TargetEntry.  In fact, everything that it changed
    /// in the global state is available by virtue of its Lookup being merged with the current Target's lookup.
    ///
    /// For CallTarget tasks, this behavior is not the same.  Rather the Lookup from a CallTarget call does not get merged until the calling
    /// Target has completed.  This is considered erroneous behavior and 'normal' version of CallTarget will be implemented which does not exhibit
    /// this.
    /// </remarks>
    internal class TargetBuilder : ITargetBuilder, ITargetBuilderCallback, IBuildComponent
    {
        /// <summary>
        /// The cancellation token.
        /// </summary>
        private CancellationToken _cancellationToken;

        /// <summary>
        /// The current stack of targets and dependents.  The top-most entry on the stack is the target
        /// currently being built.
        /// </summary>
        private ConcurrentStack<TargetEntry> _targetsToBuild;

        /// <summary>
        /// The component host.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The BuildRequestEntry for which we are building targets.
        /// </summary>
        private BuildRequestEntry _requestEntry;

        /// <summary>
        /// The lookup representing the project's state.
        /// </summary>
        private Lookup _baseLookup;

        /// <summary>
        /// The callback interface used to invoke new project builds.
        /// </summary>
        private IRequestBuilderCallback _requestBuilderCallback;

        /// <summary>
        /// The project logging context
        /// </summary>
        private ProjectLoggingContext _projectLoggingContext;

        /// <summary>
        /// The aggregate build result from running the targets
        /// </summary>
        private BuildResult _buildResult;

        /// <summary>
        /// The project instance we are building
        /// </summary>
        private ProjectInstance _projectInstance;

        /// <summary>
        /// Flag indicating whether we are under the influence of the legacy CallTarget's ContinueOnError behavior.
        /// </summary>
        private bool _legacyCallTargetContinueOnError;

        /// <summary>
        /// Builds the specified targets.
        /// </summary>
        /// <param name="loggingContext">The logging context for the project.</param>
        /// <param name="entry">The BuildRequestEntry for which we are building targets.</param>
        /// <param name="callback">The callback to be used to handle new project build requests.</param>
        /// <param name="targetNames">The names of the targets to build.</param>
        /// <param name="baseLookup">The Lookup containing all current items and properties for this target.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to use when building the targets.</param>
        /// <returns>The target's outputs and result codes</returns>
        public async Task<BuildResult> BuildTargets(ProjectLoggingContext loggingContext, BuildRequestEntry entry, IRequestBuilderCallback callback, (string name, TargetBuiltReason reason)[] targetNames, Lookup baseLookup, CancellationToken cancellationToken)
        {
            ErrorUtilities.VerifyThrowArgumentNull(loggingContext, "projectLoggingContext");
            ErrorUtilities.VerifyThrowArgumentNull(entry);
            ErrorUtilities.VerifyThrowArgumentNull(callback, "requestBuilderCallback");
            ErrorUtilities.VerifyThrowArgumentNull(targetNames);
            ErrorUtilities.VerifyThrowArgumentNull(baseLookup);
            ErrorUtilities.VerifyThrow(targetNames.Length > 0, "List of targets must be non-empty");
            ErrorUtilities.VerifyThrow(_componentHost != null, "InitializeComponent must be called before building targets.");

            _requestEntry = entry;
            _requestBuilderCallback = callback;
            _projectLoggingContext = loggingContext;
            _cancellationToken = cancellationToken;

            // Clone the base lookup so that if we are re-entered by another request while this one in blocked, we don't have visibility to
            // their state, and they have no visibility into ours.
            _baseLookup = baseLookup.Clone();

            _targetsToBuild = new ConcurrentStack<TargetEntry>();

            // Get the actual target objects from the names
            BuildRequestConfiguration configuration = _requestEntry.RequestConfiguration;

            bool previousCacheableStatus = configuration.IsCacheable;
            configuration.IsCacheable = false;
            configuration.RetrieveFromCache();
            _projectInstance = configuration.Project;

            // Now get the current results cache entry.
            IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
            BuildResult existingBuildResult = resultsCache.GetResultsForConfiguration(_requestEntry.Request.ConfigurationId);

            _buildResult = new BuildResult(entry.Request, existingBuildResult, null);

            if (existingBuildResult == null)
            {
                // Add this result so that if our project gets re-entered we won't rebuild any targets we have already built.
                resultsCache.AddResult(_buildResult);
            }

            List<TargetSpecification> targets = new List<TargetSpecification>(targetNames.Length);

            foreach ((string name, TargetBuiltReason reason) targetName in targetNames)
            {
                var targetExists = _projectInstance.Targets.TryGetValue(targetName.name, out ProjectTargetInstance targetInstance);
                if (!targetExists && entry.Request.BuildRequestDataFlags.HasFlag(BuildRequestDataFlags.SkipNonexistentTargets))
                {
                    _projectLoggingContext.LogComment(Framework.MessageImportance.Low,
                        "TargetSkippedWhenSkipNonexistentTargets", targetName.name);
                }
                else
                {
                    targets.Add(new TargetSpecification(targetName.name, targetExists ? targetInstance.Location : _projectInstance.ProjectFileLocation, targetName.reason));
                }
            }

            // Push targets onto the stack.  This method will reverse their push order so that they
            // get built in the same order specified in the array.
            await PushTargets(targets, null, baseLookup, false, false, TargetBuiltReason.None);

            // Now process the targets
            ITaskBuilder taskBuilder = _componentHost.GetComponent(BuildComponentType.TaskBuilder) as ITaskBuilder;
            try
            {
                await ProcessTargetStack(taskBuilder);
            }
            finally
            {
                // If there are still targets left on the stack, they need to be removed from the 'active targets' list
                foreach (TargetEntry target in _targetsToBuild)
                {
                    configuration.ActivelyBuildingTargets.Remove(target.Name);
                }

                ((IBuildComponent)taskBuilder).ShutdownComponent();
            }

            if (_cancellationToken.IsCancellationRequested)
            {
                throw new BuildAbortedException();
            }

            // Gather up outputs for the requested targets and return those.  All of our information should be in the base lookup now.
            ComputeAfterTargetFailures(targetNames);
            BuildResult resultsToReport = new BuildResult(_buildResult, targetNames.Select(target => target.name).ToArray());

            // Return after-build project state if requested.
            if (_requestEntry.Request.BuildRequestDataFlags.HasFlag(BuildRequestDataFlags.ProvideProjectStateAfterBuild))
            {
                resultsToReport.ProjectStateAfterBuild = _projectInstance;
            }

            if (_requestEntry.Request.RequestedProjectState != null)
            {
                resultsToReport.ProjectStateAfterBuild =
                    _projectInstance.FilteredCopy(_requestEntry.Request.RequestedProjectState);
            }

            configuration.IsCacheable = previousCacheableStatus;

            return resultsToReport;
        }

        #region IBuildComponent Members

        /// <summary>
        /// Sets the component host.
        /// </summary>
        /// <param name="host">The component host.</param>
        public void InitializeComponent(IBuildComponentHost host)
        {
            ErrorUtilities.VerifyThrowArgumentNull(host);
            _componentHost = host;
        }

        /// <summary>
        /// Shuts down the component.
        /// </summary>
        public void ShutdownComponent()
        {
            _componentHost = null;
        }

        #endregion

        #region ITargetBuilderCallback Members

        /// <summary>
        /// Invokes the specified targets using Dev9 behavior.
        /// </summary>
        /// <param name="targets">The targets to build.</param>
        /// <param name="continueOnError">True to continue building the remaining targets if one fails.</param>
        /// <param name="taskLocation">The <see cref="ElementLocation"/> of the task.</param>
        /// <returns>The results for each target.</returns>
        /// <remarks>
        /// Dev9 behavior refers to the following:
        /// 1. The changes made during the calling target up to this point are NOT visible to this target.
        /// 2. The changes made by this target are NOT visible to the calling target.
        /// 3. Changes made by the calling target OVERRIDE changes made by this target.
        /// </remarks>
        async Task<ITargetResult[]> ITargetBuilderCallback.LegacyCallTarget(string[] targets, bool continueOnError, ElementLocation taskLocation)
        {
            List<TargetSpecification> targetToPush = new List<TargetSpecification>();
            ITargetResult[] results = new TargetResult[targets.Length];
            bool originalLegacyCallTargetContinueOnError = _legacyCallTargetContinueOnError;

            _legacyCallTargetContinueOnError = _legacyCallTargetContinueOnError || continueOnError;

            // Our lookup is the one used at the beginning of the calling target.
            Lookup callTargetLookup = _baseLookup;

            // We now record this lookup in the calling target's entry so that it may
            // leave the scope just before it commits its own changes to the base lookup.
            TargetEntry currentTargetEntry = _targetsToBuild.Peek();
            currentTargetEntry.EnterLegacyCallTargetScope(callTargetLookup);

            ITaskBuilder taskBuilder = _componentHost.GetComponent(BuildComponentType.TaskBuilder) as ITaskBuilder;
            try
            {
                // Flag set to true if one of the targets we call fails.
                bool errorResult = false;

                // Now walk through the list of targets, invoking each one.
                for (int i = 0; i < targets.Length; i++)
                {
                    if (_cancellationToken.IsCancellationRequested || errorResult)
                    {
                        results[i] = new TargetResult(Array.Empty<TaskItem>(), new WorkUnitResult(WorkUnitResultCode.Skipped, WorkUnitActionCode.Continue, null));
                    }
                    else
                    {
                        targetToPush.Clear();
                        targetToPush.Add(new TargetSpecification(targets[i], taskLocation));

                        // We push the targets one at a time to emulate the original CallTarget behavior.
                        bool pushed = await PushTargets(targetToPush, currentTargetEntry, callTargetLookup, false, true, TargetBuiltReason.None);
                        ErrorUtilities.VerifyThrow(pushed, "Failed to push any targets onto the stack.  Target: {0} Current Target: {1}", targets[i], currentTargetEntry.Target.Name);
                        await ProcessTargetStack(taskBuilder);

                        if (!_cancellationToken.IsCancellationRequested)
                        {
                            results[i] = _buildResult[targets[i]];
                            if (results[i].ResultCode == TargetResultCode.Failure)
                            {
                                errorResult = true;
                            }
                        }
                        else
                        {
                            results[i] = new TargetResult(Array.Empty<TaskItem>(), new WorkUnitResult(WorkUnitResultCode.Skipped, WorkUnitActionCode.Continue, null));
                        }
                    }
                }
            }
            finally
            {
                // Restore the state of the TargetBuilder to that it was prior to the CallTarget call.
                // Any targets we have pushed on at this point we need to get rid of since we aren't going to process them.
                // If there were normal task errors, standard error handling semantics would have taken care of them.
                // If there was an exception, such as a circular dependency error, items may still be on the stack so we must clear them.
                while (!Object.ReferenceEquals(_targetsToBuild.Peek(), currentTargetEntry))
                {
                    _targetsToBuild.Pop();
                }

                _legacyCallTargetContinueOnError = originalLegacyCallTargetContinueOnError;
                ((IBuildComponent)taskBuilder).ShutdownComponent();
            }

            return results;
        }

        #endregion

        #region IRequestBuilderCallback Members

        /// <summary>
        /// Forwarding implementation of BuildProjects
        /// </summary>
        async Task<BuildResult[]> IRequestBuilderCallback.BuildProjects(string[] projectFiles, Microsoft.Build.Collections.PropertyDictionary<ProjectPropertyInstance>[] properties, string[] toolsVersions, string[] targets, bool waitForResults, bool skipNonexistentTargets)
        {
            return await _requestBuilderCallback.BuildProjects(projectFiles, properties, toolsVersions, targets, waitForResults, skipNonexistentTargets);
        }

        /// <summary>
        /// Required for interface - this should never be called.
        /// </summary>
        Task IRequestBuilderCallback.BlockOnTargetInProgress(int blockingGlobalBuildRequestId, string blockingTarget, BuildResult partialBuildResult)
        {
            ErrorUtilities.ThrowInternalError("This method should never be called by anyone except the TargetBuilder.");
            return Task.FromResult(false);
        }

        /// <summary>
        /// Yields the node.
        /// </summary>
        void IRequestBuilderCallback.Yield()
        {
            _requestBuilderCallback.Yield();
        }

        /// <summary>
        /// Reacquires the node.
        /// </summary>
        void IRequestBuilderCallback.Reacquire()
        {
            _requestBuilderCallback.Reacquire();
        }

        /// <summary>
        /// Enters the MSBuild callback state for asynchronous processing of referenced projects.
        /// </summary>
        void IRequestBuilderCallback.EnterMSBuildCallbackState()
        {
            _requestBuilderCallback.EnterMSBuildCallbackState();
        }

        /// <summary>
        /// Exits the MSBuild callback state.
        /// </summary>
        void IRequestBuilderCallback.ExitMSBuildCallbackState()
        {
            _requestBuilderCallback.ExitMSBuildCallbackState();
        }

        /// <summary>
        /// Requests CPU resources from the scheduler.
        /// </summary>
        /// <remarks>This method is called from the <see cref="TaskHost"/>.</remarks>
        int IRequestBuilderCallback.RequestCores(object monitorLockObject, int requestedCores, bool waitForCores)
        {
            return _requestBuilderCallback.RequestCores(monitorLockObject, requestedCores, waitForCores);
        }

        /// <summary>
        /// Returns CPU resources to the scheduler.
        /// </summary>
        /// <remarks>This method is called from the <see cref="TaskHost"/>.</remarks>
        void IRequestBuilderCallback.ReleaseCores(int coresToRelease)
        {
            _requestBuilderCallback.ReleaseCores(coresToRelease);
        }

        #endregion

        /// <summary>
        /// Class factory for component creation.
        /// </summary>
        internal static IBuildComponent CreateComponent(BuildComponentType type)
        {
            ErrorUtilities.VerifyThrow(type == BuildComponentType.TargetBuilder, "Cannot create components of type {0}", type);
            return new TargetBuilder();
        }

        /// <summary>
        /// Processes the target stack until its empty or we hit a recursive break (due to CallTarget etc.)
        /// </summary>
        private async Task ProcessTargetStack(ITaskBuilder taskBuilder)
        {
            // Keep building while we have targets to build and haven't been canceled.
            bool stopProcessingStack = false;
            while
                (
                !_cancellationToken.IsCancellationRequested &&
                !stopProcessingStack &&
                _targetsToBuild.Any())
            {
                TargetEntry currentTargetEntry = _targetsToBuild.Peek();
                switch (currentTargetEntry.State)
                {
                    case TargetEntryState.Dependencies:
                        // Ensure we are dealing with a target which actually exists.
                        ProjectErrorUtilities.VerifyThrowInvalidProject(
                        _requestEntry.RequestConfiguration.Project.Targets.ContainsKey(currentTargetEntry.Name),
                        currentTargetEntry.ReferenceLocation,
                        "TargetDoesNotExist",
                        currentTargetEntry.Name);

                        // If we already have results for this target which were not skipped, we can ignore it.  In
                        // addition, we can also ignore its before and after targets -- if this target has already run,
                        // then so have they.
                        if (!CheckSkipTarget(ref stopProcessingStack, currentTargetEntry))
                        {
                            // Temporarily remove this entry so we can push our after targets
                            _targetsToBuild.Pop();

                            // Push our after targets, if any.  Our parent is the parent of the target after which we are running.
                            IList<TargetSpecification> afterTargets = _requestEntry.RequestConfiguration.Project.GetTargetsWhichRunAfter(currentTargetEntry.Name);
                            bool didPushTargets = await PushTargets(afterTargets, currentTargetEntry.ParentEntry, currentTargetEntry.Lookup, currentTargetEntry.ErrorTarget, currentTargetEntry.StopProcessingOnCompletion, TargetBuiltReason.AfterTargets);

                            // If we have after targets, the last one to run will inherit the stopProcessing flag and we will reset ours.  If we didn't push any targets, then we shouldn't clear the
                            // flag because it means we are still on the bottom of this CallTarget stack.
                            if ((afterTargets.Count != 0) && didPushTargets)
                            {
                                currentTargetEntry.StopProcessingOnCompletion = false;
                            }

                            // Put us back on the stack
                            _targetsToBuild.Push(currentTargetEntry);

                            // Determine which targets are dependencies.  This will also test to see if the target should be skipped due to condition.
                            // If it is determined the target should skip, the dependency list returned will be empty.
                            IList<TargetSpecification> dependencies = currentTargetEntry.GetDependencies(_projectLoggingContext);

                            // Push our before targets now, unconditionally.  If we have marked that we should stop processing the stack here, which can only
                            // happen if our current target was supposed to stop processing AND we had no after targets, then our last before target should
                            // inherit the stop processing flag and we will reset it.
                            // Our parent is the target before which we run, just like a depends-on target.
                            IList<TargetSpecification> beforeTargets = _requestEntry.RequestConfiguration.Project.GetTargetsWhichRunBefore(currentTargetEntry.Name);
                            bool pushedTargets = await PushTargets(beforeTargets, currentTargetEntry, currentTargetEntry.Lookup, currentTargetEntry.ErrorTarget, stopProcessingStack, TargetBuiltReason.BeforeTargets);
                            if (beforeTargets.Count != 0 && pushedTargets)
                            {
                                stopProcessingStack = false;
                            }

                            // And if we have dependencies to run, push them now.
                            if (dependencies != null)
                            {
                                await PushTargets(dependencies, currentTargetEntry, currentTargetEntry.Lookup, false, false, TargetBuiltReason.DependsOn);
                            }
                        }

                        break;

                    case TargetEntryState.Execution:

                        // It's possible that our target got pushed onto the stack for one build and had dependencies process, then a re-entrant build started actively building
                        // the target, encountered a legacy CallTarget, pushed new work onto the stack, and yielded back to here. Instead of starting the already-partially-
                        // built target, wait for the other one to complete. Then CheckSkipTarget will skip it here.
                        bool wasActivelyBuilding = await CompleteOutstandingActiveRequests(currentTargetEntry.Name);

                        // It's possible that our target got pushed onto the stack for one build and had its dependencies process, then a re-entrant build came in and
                        // actually built this target while we were waiting, so that by the time we get here, it's already been finished.  In this case, just blow it away.
                        if (!CheckSkipTarget(ref stopProcessingStack, currentTargetEntry))
                        {
                            ErrorUtilities.VerifyThrow(!wasActivelyBuilding, "Target {0} was actively building and waited on but we are attempting to build it again.", currentTargetEntry.Name);

                            // This target is now actively building.
                            _requestEntry.RequestConfiguration.ActivelyBuildingTargets[currentTargetEntry.Name] = _requestEntry.Request.GlobalRequestId;

                            // Execute all of the tasks on this target.
                            MSBuildEventSource.Log.TargetStart(currentTargetEntry.Name);
                            await currentTargetEntry.ExecuteTarget(taskBuilder, _requestEntry, _projectLoggingContext, _cancellationToken);
                            MSBuildEventSource.Log.TargetStop(currentTargetEntry.Name, currentTargetEntry.Result?.TargetResultCodeToString() ?? string.Empty);
                        }

                        break;

                    case TargetEntryState.ErrorExecution:
                        if (!CheckSkipTarget(ref stopProcessingStack, currentTargetEntry))
                        {
                            // Push the error targets onto the stack.  This target will now be marked as completed.
                            // When that state is processed, it will mark its parent for error execution
                            var errorTargets = currentTargetEntry.GetErrorTargets(_projectLoggingContext);
                            try
                            {
                                await PushTargets(errorTargets, currentTargetEntry, currentTargetEntry.Lookup, true,
                                    false, TargetBuiltReason.None);
                            }
                            catch
                            {
                                _requestEntry.RequestConfiguration.ActivelyBuildingTargets.Remove(currentTargetEntry.Name);
                                throw;
                            }
                        }

                        break;

                    case TargetEntryState.Completed:
                        // The target is complete, we can gather up the results and remove this target
                        // from the stack.
                        TargetResult targetResult = currentTargetEntry.GatherResults();

                        // If this result failed but we are under the influence of the legacy ContinueOnError behavior for a
                        // CallTarget, make sure we don't contribute this failure to the overall success of the build.
                        targetResult.TargetFailureDoesntCauseBuildFailure = _legacyCallTargetContinueOnError;

                        // This target is no longer actively building.
                        _requestEntry.RequestConfiguration.ActivelyBuildingTargets.Remove(currentTargetEntry.Name);

                        _buildResult.AddResultsForTarget(currentTargetEntry.Name, targetResult);

                        TargetEntry topEntry = _targetsToBuild.Pop();
                        if (topEntry.StopProcessingOnCompletion)
                        {
                            stopProcessingStack = true;
                        }

                        PopDependencyTargetsOnTargetFailure(topEntry, targetResult, ref stopProcessingStack);

                        break;

                    default:
                        ErrorUtilities.ThrowInternalError("Unexpected target state {0}", currentTargetEntry.State);
                        break;
                }
            }
        }

        /// <summary>
        /// Determines if the current target should be skipped, and logs the appropriate message.
        /// </summary>
        /// <returns>True to skip the target, false otherwise.</returns>
        private bool CheckSkipTarget(ref bool stopProcessingStack, TargetEntry currentTargetEntry)
        {
            if (_buildResult.TryGetResultsForTarget(currentTargetEntry.Name, out TargetResult targetResult))
            {
                ErrorUtilities.VerifyThrowInternalNull(targetResult, "targetResult");

                if (targetResult.ResultCode != TargetResultCode.Skipped)
                {
                    // If we've already dealt with this target and it didn't skip, let's log appropriately
                    // Otherwise we don't want anything more to do with it.
                    bool success = targetResult.ResultCode == TargetResultCode.Success;
                    var skippedTargetEventArgs = new TargetSkippedEventArgs(message: null)
                    {
                        BuildEventContext = _projectLoggingContext.BuildEventContext,
                        TargetName = currentTargetEntry.Name,
                        TargetFile = currentTargetEntry.Target.Location.File,
                        ParentTarget = currentTargetEntry.ParentEntry?.Target.Name,
                        BuildReason = currentTargetEntry.BuildReason,
                        OriginallySucceeded = success,
                        SkipReason = success ? TargetSkipReason.PreviouslyBuiltSuccessfully : TargetSkipReason.PreviouslyBuiltUnsuccessfully,
                        OriginalBuildEventContext = targetResult.OriginalBuildEventContext
                    };

                    _projectLoggingContext.LogBuildEvent(skippedTargetEventArgs);

                    if (currentTargetEntry.StopProcessingOnCompletion)
                    {
                        stopProcessingStack = true;
                    }

                    if (targetResult.ResultCode == TargetResultCode.Success)
                    {
                        _targetsToBuild.Peek().LeaveLegacyCallTargetScopes();
                        _targetsToBuild.Pop();
                    }
                    else
                    {
                        TargetEntry topEntry = _targetsToBuild.Pop();

                        // If this is a skip because of target failure, we should behave in the same way as we
                        // would if this target actually failed -- remove all its dependencies from the stack as
                        // well.  Otherwise, we could encounter a situation where a failure target happens in the
                        // middle of execution once, then exits, then a request comes through to build the same
                        // targets, reaches that target, skips-already-failed, and then continues building.
                        PopDependencyTargetsOnTargetFailure(topEntry, targetResult, ref stopProcessingStack);
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// When a target build fails, we don't just stop building that target; we also pop all of the other dependency targets of its
        /// parent target off the stack. Extract that logic into a standalone method so that it can be used when dealing with targets that
        /// are skipped-unsuccessful as well as first-time failures.
        /// </summary>
        private void PopDependencyTargetsOnTargetFailure(TargetEntry topEntry, TargetResult targetResult, ref bool stopProcessingStack)
        {
            if (targetResult.WorkUnitResult.ActionCode == WorkUnitActionCode.Stop)
            {
                // Pop down to our parent, since any other dependencies our parent had should no longer
                // execute.  If we encounter an error target on the way down, also stop since the failure
                // of one error target in a set declared in OnError should not cause the others to stop running.
                while ((_targetsToBuild.Any()) && (_targetsToBuild.Peek() != topEntry.ParentEntry) && !_targetsToBuild.Peek().ErrorTarget)
                {
                    TargetEntry entry = _targetsToBuild.Pop();
                    entry.LeaveLegacyCallTargetScopes();

                    // This target is no longer actively building (if it was).
                    _requestEntry.RequestConfiguration.ActivelyBuildingTargets.Remove(topEntry.Name);

                    // If we come across an entry which requires us to stop processing (for instance, an aftertarget of the original
                    // CallTarget target) then we need to use that flag, not the one from the top entry.
                    if (entry.StopProcessingOnCompletion)
                    {
                        stopProcessingStack = true;
                    }
                }

                // Mark our parent for error execution when it is not Completed (e.g. Executing)
                if (topEntry.ParentEntry != null && topEntry.ParentEntry.State != TargetEntryState.Completed)
                {
                    topEntry.ParentEntry.MarkForError();
                }
                // In cases where we need to indicate a failure but the ParentEntry was Skipped (due to condition) it must be
                // marked stop to prevent other targets from executing.
                else if (topEntry.ParentEntry?.Result?.ResultCode == TargetResultCode.Skipped)
                {
                    topEntry.ParentEntry.MarkForStop();
                }
            }
        }

        /// <summary>
        /// Pushes the list of targets specified onto the target stack in reverse order specified, so that
        /// they will be built in the order specified.
        /// </summary>
        /// <param name="targets">List of targets to build.</param>
        /// <param name="parentTargetEntry">The target which should be considered the parent of these targets.</param>
        /// <param name="baseLookup">The lookup to be used to build these targets.</param>
        /// <param name="addAsErrorTarget">True if this should be considered an error target.</param>
        /// <param name="stopProcessingOnCompletion">True if target stack processing should terminate when the last target in the list is processed.</param>
        /// <param name="buildReason">The reason the target is being built by the parent.</param>
        /// <returns>True if we actually pushed any targets, false otherwise.</returns>
        private async Task<bool> PushTargets(IList<TargetSpecification> targets, TargetEntry parentTargetEntry, Lookup baseLookup, bool addAsErrorTarget, bool stopProcessingOnCompletion, TargetBuiltReason buildReason)
        {
            List<TargetEntry> targetsToPush = new List<TargetEntry>(targets.Count);

            // Iterate the list in reverse order so that the first target in the list is the last pushed, and thus the first to be executed.
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                TargetSpecification targetSpecification = targets[i];

                if (buildReason == TargetBuiltReason.BeforeTargets || buildReason == TargetBuiltReason.AfterTargets)
                {
                    // Don't build any Before or After targets for which we already have results.  Unlike other targets,
                    // we don't explicitly log a skipped-with-results message because it is not interesting.
                    if (_buildResult.TryGetResultsForTarget(targetSpecification.TargetName, out TargetResult targetResult) && targetResult.ResultCode != TargetResultCode.Skipped)
                    {
                        continue;
                    }
                }

                ElementLocation targetLocation = targetSpecification.ReferenceLocation;

                // See if this target is already building under a different build request.  If so, we need to wait.
                int idOfAlreadyBuildingRequest = BuildRequest.InvalidGlobalRequestId;
                if (_requestEntry.RequestConfiguration.ActivelyBuildingTargets.TryGetValue(targetSpecification.TargetName, out idOfAlreadyBuildingRequest))
                {
                    if (idOfAlreadyBuildingRequest != _requestEntry.Request.GlobalRequestId)
                    {
                        // Another request elsewhere is building it.  We need to wait.
                        await _requestBuilderCallback.BlockOnTargetInProgress(idOfAlreadyBuildingRequest, targetSpecification.TargetName, null);

                        // If we come out of here and the target is *still* active, it means the scheduler detected a circular dependency and told us to
                        // continue so we could throw the exception.
                        if (_requestEntry.RequestConfiguration.ActivelyBuildingTargets.ContainsKey(targetSpecification.TargetName))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(targetLocation, "CircularDependency", targetSpecification.TargetName);
                        }
                    }
                    else
                    {
                        if (buildReason == TargetBuiltReason.AfterTargets)
                        {
                            // If the target we are pushing is supposed to run after the current target and it is already set to run after us then skip adding it now.
                            continue;
                        }

                        // We are already building this target on this request. That's a circular dependency.
                        ProjectErrorUtilities.ThrowInvalidProject(targetLocation, "CircularDependency", targetSpecification.TargetName);
                    }
                }
                else
                {
                    // Does this target exist in our direct parent chain, if it is a before target (since these can cause circular dependency issues)
                    if (buildReason == TargetBuiltReason.BeforeTargets || buildReason == TargetBuiltReason.DependsOn || buildReason == TargetBuiltReason.None)
                    {
                        if (HasCircularDependenceInTargets(parentTargetEntry, targetSpecification, out List<string> targetDependenceChain))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(targetLocation, "CircularDependencyInTargetGraph", targetSpecification.TargetName, parentTargetEntry.Name, buildReason, targetSpecification.TargetName, string.Join("<-", targetDependenceChain));
                        }
                    }
                    else
                    {
                        // For an after target, if it is already ANYWHERE on the stack, we don't need to push it because it is already going to run
                        // after whatever target is causing it to be pushed now.
                        bool alreadyPushed = false;
                        foreach (TargetEntry entry in _targetsToBuild)
                        {
                            if (String.Equals(entry.Name, targetSpecification.TargetName, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyPushed = true;
                                break;
                            }
                        }

                        if (alreadyPushed)
                        {
                            continue;
                        }
                    }
                }

                // The buildReason argument for this function can be BeforeTargets or AfterTargets, we don't want to override the reason when adding a new entry
                // If the reason is None, it means it does not depend on another target. So we can use the target's BuiltReason.
                TargetBuiltReason entryReason = buildReason == TargetBuiltReason.None ? targetSpecification._targetBuiltReason : buildReason;

                // Add to the list of targets to push.  We don't actually put it on the stack here because we could run into a circular dependency
                // during this loop, in which case the target stack would be out of whack.
                TargetEntry newEntry = new TargetEntry(_requestEntry, this as ITargetBuilderCallback, targetSpecification, baseLookup, parentTargetEntry, entryReason, _componentHost, _projectLoggingContext, stopProcessingOnCompletion);

                newEntry.ErrorTarget = addAsErrorTarget;
                targetsToPush.Add(newEntry);
                stopProcessingOnCompletion = false; // The first target on the stack (the last one to be run) always inherits the stopProcessing flag.
            }

            // Now push the targets since this operation cannot fail.
            foreach (TargetEntry targetToPush in targetsToPush)
            {
                _targetsToBuild.Push(targetToPush);
            }

            bool pushedTargets = (targetsToPush.Count > 0);
            return pushedTargets;
        }

        private async Task<bool> CompleteOutstandingActiveRequests(string targetName)
        {
            // See if this target is already building under a different build request.  If so, we need to wait.
            int idOfAlreadyBuildingRequest = BuildRequest.InvalidGlobalRequestId;
            if (_requestEntry.RequestConfiguration.ActivelyBuildingTargets.TryGetValue(targetName, out idOfAlreadyBuildingRequest))
            {
                if (idOfAlreadyBuildingRequest != _requestEntry.Request.GlobalRequestId)
                {
                    // Another request is building the target. Wait for that, sending partial results
                    // for this request, which may be required to unblock it.
                    await _requestBuilderCallback.BlockOnTargetInProgress(idOfAlreadyBuildingRequest, targetName, _buildResult);

                    return true;
                }
            }

            return false;
        }

        private void ComputeAfterTargetFailures((string name, TargetBuiltReason reason)[] targetNames)
        {
            foreach ((string name, TargetBuiltReason reason) targetName in targetNames)
            {
                if (_buildResult.ResultsByTarget.TryGetValue(targetName.name, out TargetResult targetBuildResult))
                {
                    // Queue of targets waiting to be processed, seeded with the specific target for which we're computing AfterTargetsHaveFailed.
                    var targetsToCheckForAfterTargets = new Queue<string>();
                    targetsToCheckForAfterTargets.Enqueue(targetName.name);

                    // Set of targets already processed, to break cycles of AfterTargets.
                    // Initialized lazily when needed below.
                    HashSet<string> targetsChecked = null;

                    while (targetsToCheckForAfterTargets?.Count > 0)
                    {
                        string targetToCheck = targetsToCheckForAfterTargets.Dequeue();
                        IList<TargetSpecification> targetsWhichRunAfter = _requestEntry.RequestConfiguration.Project.GetTargetsWhichRunAfter(targetToCheck);

                        foreach (TargetSpecification afterTarget in targetsWhichRunAfter)
                        {
                            _buildResult.ResultsByTarget.TryGetValue(afterTarget.TargetName, out TargetResult result);
                            if (result?.ResultCode == TargetResultCode.Failure && !result.TargetFailureDoesntCauseBuildFailure)
                            {
                                // Mark the target as having an after target failed, and break the loop to move to the next target.
                                targetBuildResult.AfterTargetsHaveFailed = true;
                                targetsToCheckForAfterTargets = null;
                                break;
                            }

                            targetsChecked ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default)
                                {
                                    targetName.name
                                };

                            // If we haven't seen this target yet, add it to the list to check.
                            if (targetsChecked.Add(afterTarget.TargetName))
                            {
                                targetsToCheckForAfterTargets.Enqueue(afterTarget.TargetName);
                            }
                        }
                    }
                }
            }
        }

        private bool HasCircularDependenceInTargets(TargetEntry parentTargetEntry, TargetSpecification targetSpecification, out List<string> circularDependenceChain)
        {
            TargetEntry currentParent = parentTargetEntry;
            circularDependenceChain = new List<string>();
            bool hasCircularDependence = false;

            while (currentParent != null)
            {
                if (String.Equals(currentParent.Name, targetSpecification.TargetName, StringComparison.OrdinalIgnoreCase))
                {
                    // We are already building this target on this request. That's a circular dependency.
                    hasCircularDependence = true;

                    // Cache the circular dependence chain only when circular dependency occurs.
                    currentParent = parentTargetEntry;
                    circularDependenceChain.Add(targetSpecification.TargetName);
                    while (!String.Equals(currentParent.Name, targetSpecification.TargetName, StringComparison.OrdinalIgnoreCase))
                    {
                        circularDependenceChain.Add(currentParent.Name);
                        currentParent = currentParent.ParentEntry;
                    }

                    circularDependenceChain.Add(currentParent.Name);
                    break;
                }

                currentParent = currentParent.ParentEntry;
            }

            return hasCircularDependence;
        }
    }
}
