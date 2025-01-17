// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using NodeLoggingContext = Microsoft.Build.BackEnd.Logging.NodeLoggingContext;
using ProjectLoggingContext = Microsoft.Build.BackEnd.Logging.ProjectLoggingContext;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Implementation of IRequestBuilder
    /// </summary>
    internal class RequestBuilder : IRequestBuilder, IRequestBuilderCallback, IBuildComponent
    {
        /// <summary>
        /// The dedicated scheduler object.
        /// </summary>
        private static readonly TaskScheduler s_dedicatedScheduler = new DedicatedThreadsTaskScheduler();

        /// <summary>
        /// The event used to signal that this request should immediately terminate.
        /// </summary>
        private ManualResetEvent _terminateEvent;

        /// <summary>
        /// The event used to signal that this request should wake up from its wait state.
        /// </summary>
        private AutoResetEvent _continueEvent;

        /// <summary>
        /// The results used when a build request entry continues.
        /// </summary>
        private IDictionary<int, BuildResult> _continueResults;

        /// <summary>
        /// Queue of actions to call when a resource request is responded to.
        /// </summary>
        private ConcurrentQueue<Action<ResourceResponse>> _pendingResourceRequests;

        /// <summary>
        /// The task representing the currently-executing build request.
        /// </summary>
        private Task _requestTask;

        /// <summary>
        /// The cancellation token source for the currently-executing build request.
        /// </summary>
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// The build request entry being built.
        /// </summary>
        private BuildRequestEntry _requestEntry;

        /// <summary>
        /// The component host.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The node logging context
        /// </summary>
        private NodeLoggingContext _nodeLoggingContext;

        /// <summary>
        /// The project logging context
        /// </summary>
        private ProjectLoggingContext _projectLoggingContext;

        /// <summary>
        /// The target builder.
        /// </summary>
        private ITargetBuilder _targetBuilder;

        /// <summary>
        /// Block type
        /// </summary>
        private BlockType _blockType = BlockType.Unblocked;

        /// <summary>
        /// Flag indicating we are in an MSBuild callback
        /// </summary>
        private bool _inMSBuildCallback = false;

        /// <summary>
        /// Flag indicating whether this request builder has been zombied by a cancellation request.
        /// </summary>
        private bool _isZombie = false;

        /// <summary>
        /// Creates a new request builder.
        /// </summary>
        internal RequestBuilder()
        {
            _terminateEvent = new ManualResetEvent(false);
            _continueEvent = new AutoResetEvent(false);
            _pendingResourceRequests = new ConcurrentQueue<Action<ResourceResponse>>();
        }

        /// <summary>
        /// The event raised when a new build request should be issued.
        /// </summary>
        public event NewBuildRequestsDelegate OnNewBuildRequests;

        /// <summary>
        /// The event raised when the build request has completed.
        /// </summary>
        public event BuildRequestCompletedDelegate OnBuildRequestCompleted;

        /// <summary>
        /// The event raised when the build request has completed.
        /// </summary>
        public event BuildRequestBlockedDelegate OnBuildRequestBlocked;

        /// <summary>
        /// The event raised when resources are requested.
        /// </summary>
        public event ResourceRequestDelegate OnResourceRequest;

        /// <summary>
        /// The current block type
        /// </summary>
        private enum BlockType
        {
            /// <summary>
            /// We are blocked waiting on a target in progress.
            /// </summary>
            BlockedOnTargetInProgress,

            /// <summary>
            /// We are blocked waiting for results from child requests.
            /// </summary>
            BlockedOnChildRequests,

            /// <summary>
            /// We are blocked because we have yielded control
            /// </summary>
            Yielded,

            /// <summary>
            /// We are not blocked at all.
            /// </summary>
            Unblocked
        }

        /// <summary>
        /// Retrieves the request entry associated with this RequestBuilder.
        /// </summary>
        internal BuildRequestEntry RequestEntry
        {
            get
            {
                VerifyIsNotZombie();
                return _requestEntry;
            }
        }

        /// <summary>
        /// Returns true if this RequestBuilder has an active build request
        /// </summary>
        internal bool HasActiveBuildRequest
        {
            get
            {
                VerifyIsNotZombie();

                return (_requestTask?.IsCompleted == false) || (_componentHost.LegacyThreadingData.MainThreadSubmissionId != -1);
            }
        }

        /// <summary>
        /// Starts a build request
        /// </summary>
        /// <param name="loggingContext">The logging context for the node.</param>
        /// <param name="entry">The entry to build.</param>
        public void BuildRequest(NodeLoggingContext loggingContext, BuildRequestEntry entry)
        {
            ErrorUtilities.VerifyThrowArgumentNull(loggingContext);
            ErrorUtilities.VerifyThrowArgumentNull(entry);
            ErrorUtilities.VerifyThrow(_componentHost != null, "Host not set.");
            ErrorUtilities.VerifyThrow(_targetBuilder == null, "targetBuilder not null");
            ErrorUtilities.VerifyThrow(_nodeLoggingContext == null, "nodeLoggingContext not null");
            ErrorUtilities.VerifyThrow(_requestEntry == null, "requestEntry not null");
            ErrorUtilities.VerifyThrow(!_terminateEvent.WaitOne(0), "Cancel already called");

            _nodeLoggingContext = loggingContext;
            _blockType = BlockType.Unblocked;
            _requestEntry = entry;
            _requestEntry.Continue();
            _continueResults = null;

            _targetBuilder = (ITargetBuilder)_componentHost.GetComponent(BuildComponentType.TargetBuilder);

            VerifyEntryInActiveState();
            InitializeOperatingEnvironment();
            StartBuilderThread();
        }

        /// <summary>
        /// Continues a build request
        /// </summary>
        public void ContinueRequest()
        {
            ErrorUtilities.VerifyThrow(HasActiveBuildRequest, "Request not building");
            ErrorUtilities.VerifyThrow(!_terminateEvent.WaitOne(0), "Request already terminated");
            ErrorUtilities.VerifyThrow(!_continueEvent.WaitOne(0), "Request already continued");
            VerifyEntryInReadyState();

            _continueResults = _requestEntry.Continue();
            ErrorUtilities.VerifyThrow(_blockType == BlockType.BlockedOnTargetInProgress || _blockType == BlockType.Yielded || (_continueResults != null), "Unexpected null results for request {0} (nr {1})", _requestEntry.Request.GlobalRequestId, _requestEntry.Request.NodeRequestId);

            // Setting the continue event will wake up the build thread, which is suspended in StartNewBuildRequests.
            _continueEvent.Set();
        }

        /// <summary>
        /// Continues a build request after receiving a resource response.
        /// </summary>
        public void ContinueRequestWithResources(ResourceResponse response)
        {
            ErrorUtilities.VerifyThrow(HasActiveBuildRequest, "Request not building");
            ErrorUtilities.VerifyThrow(!_terminateEvent.WaitOne(0), "Request already terminated");
            ErrorUtilities.VerifyThrow(_pendingResourceRequests.Any(), "No pending resource requests");
            VerifyEntryInActiveOrWaitingState();

            _pendingResourceRequests.Dequeue()(response);
        }

        /// <summary>
        /// Terminates the build request
        /// </summary>
        /// <remarks>
        /// Once we have entered this method, no more methods will be invoked on this class (save ShutdownComponent)
        /// as we should no longer be receiving any messages from the BuildManager.
        /// </remarks>
        public void CancelRequest()
        {
            this.BeginCancel();
            this.WaitForCancelCompletion();
        }

        /// <summary>
        /// Starts to cancel an existing request.
        /// </summary>
        public void BeginCancel()
        {
            _terminateEvent.Set();

            // Cancel the current build.
            if (_cancellationTokenSource != null)
            {
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                _cancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Waits for the cancellation until it's completed, and cleans up the internal states.
        /// </summary>
        public void WaitForCancelCompletion()
        {
            // Wait for the request thread to terminate.
            if (_requestTask != null)
            {
                bool taskCleanedUp = false;

                try
                {
                    taskCleanedUp = _requestTask.Wait(BuildParameters.RequestBuilderShutdownTimeout);
                }
                catch (AggregateException e) when (InnerExceptionsAreAllCancelledExceptions(e))
                {
                    // ignore -- just indicates that the task finished cancelling before we got a chance to wait on it.
                    taskCleanedUp = true;
                }

                if (!taskCleanedUp)
                {
                    // This can happen when a task has locked us up.
                    _projectLoggingContext.LogError(new BuildEventFileInfo(String.Empty), "FailedToReceiveTaskThreadStatus", BuildParameters.RequestBuilderShutdownTimeout);
                    ErrorUtilities.ThrowInvalidOperation("UnableToCancel");
                }
            }

            _isZombie = true;
        }

        private bool InnerExceptionsAreAllCancelledExceptions(AggregateException e)
        {
            return e.Flatten().InnerExceptions.All(ex => ex is TaskCanceledException || ex is OperationCanceledException);
        }

        #region IRequestBuilderCallback Members

        /// <summary>
        /// This method instructs the request builder to build the specified projects using the specified parameters.  This is
        /// what is ultimately used by something like an MSBuild task which needs to invoke a project-to-project reference.  IBuildEngine
        /// and IBuildEngine2 have BuildProjectFile methods which boil down to an invocation of this method as well.
        /// </summary>
        /// <param name="projectFiles">An array of projects to be built.</param>
        /// <param name="properties">The property groups to use for each project.  Must be the same number as there are project files.</param>
        /// <param name="toolsVersions">The tools version to use for each project.  Must be the same number as there are project files.</param>
        /// <param name="targets">The targets to be built.  Each project will be built with the same targets.</param>
        /// <param name="waitForResults">True to wait for the results </param>
        /// <param name="skipNonexistentTargets">If set, skip targets that are not defined in the projects to be built.</param>
        /// <returns>True if the requests were satisfied, false if they were aborted.</returns>
        public async Task<BuildResult[]> BuildProjects(string[] projectFiles, PropertyDictionary<ProjectPropertyInstance>[] properties, string[] toolsVersions, string[] targets, bool waitForResults, bool skipNonexistentTargets = false)
        {
            VerifyIsNotZombie();
            ErrorUtilities.VerifyThrowArgumentNull(projectFiles);
            ErrorUtilities.VerifyThrowArgumentNull(properties);
            ErrorUtilities.VerifyThrowArgumentNull(targets);
            ErrorUtilities.VerifyThrowArgumentNull(toolsVersions);
            ErrorUtilities.VerifyThrow(_componentHost != null, "No host object set");
            ErrorUtilities.VerifyThrow(projectFiles.Length == properties.Length, "Properties and project counts not the same");
            ErrorUtilities.VerifyThrow(projectFiles.Length == toolsVersions.Length, "Tools versions and project counts not the same");

            FullyQualifiedBuildRequest[] requests = new FullyQualifiedBuildRequest[projectFiles.Length];

            for (int i = 0; i < projectFiles.Length; ++i)
            {
                if (!Path.IsPathRooted(projectFiles[i]))
                {
                    projectFiles[i] = Path.Combine(_requestEntry.ProjectRootDirectory, projectFiles[i]);
                }

                // Canonicalize
                projectFiles[i] = FileUtilities.NormalizePath(projectFiles[i]);

                // A tools version specified by an MSBuild task or similar has priority
                string explicitToolsVersion = toolsVersions[i];

                // Otherwise go to any explicit tools version on the project who made this callback
                if (explicitToolsVersion == null && _requestEntry.RequestConfiguration.ExplicitToolsVersionSpecified)
                {
                    explicitToolsVersion = _requestEntry.RequestConfiguration.ToolsVersion;
                }

                // Otherwise let the BuildRequestConfiguration figure out what tools version will be used
                BuildRequestData data = new BuildRequestData(projectFiles[i], properties[i].ToDictionary(), explicitToolsVersion, targets, null);

                BuildRequestConfiguration config = new BuildRequestConfiguration(data, _componentHost.BuildParameters.DefaultToolsVersion);
                ProjectIsolationMode isolateProjects = _componentHost.BuildParameters.ProjectIsolationMode;
                bool skipStaticGraphIsolationConstraints = (isolateProjects != ProjectIsolationMode.False && _requestEntry.RequestConfiguration.ShouldSkipIsolationConstraintsForReference(config.ProjectFullPath))
                    || isolateProjects == ProjectIsolationMode.MessageUponIsolationViolation;
                requests[i] = new FullyQualifiedBuildRequest(
                    config: config,
                    targets: targets,
                    resultsNeeded: waitForResults,
                    skipStaticGraphIsolationConstraints: skipStaticGraphIsolationConstraints,
                    flags: skipNonexistentTargets
                        ? BuildRequestDataFlags.SkipNonexistentTargets
                        : BuildRequestDataFlags.None);
            }

            // Send the requests off
            BuildResult[] results = await StartNewBuildRequests(requests);

            ErrorUtilities.VerifyThrow(requests.Length == results.Length, "# results != # requests");

            return results;
        }

        /// <summary>
        /// This method is called when the current request needs to build a target which is already in progress on this configuration, but which
        /// is being built by another request.
        /// </summary>
        /// <param name="blockingGlobalRequestId">The id of the request on which we are blocked.</param>
        /// <param name="blockingTarget">The target on which we are blocked.</param>
        /// <param name="partialBuildResult">A BuildResult with results from an incomplete build request.</param>
        public async Task BlockOnTargetInProgress(int blockingGlobalRequestId, string blockingTarget, BuildResult partialBuildResult = null)
        {
            VerifyIsNotZombie();
            SaveOperatingEnvironment();

            _blockType = BlockType.BlockedOnTargetInProgress;

            RaiseOnBlockedRequest(blockingGlobalRequestId, blockingTarget, partialBuildResult);

            WaitHandle[] handles = [_terminateEvent, _continueEvent];

            int handle;
            if (IsBuilderUsingLegacyThreadingSemantics(_componentHost, _requestEntry))
            {
                handle = WaitHandle.WaitAny(handles);
            }
            else
            {
                handle = await handles.ToTask();
            }

            RestoreOperatingEnvironment();

            if (handle == 0)
            {
                // We've been aborted
                throw new BuildAbortedException();
            }

            _blockType = BlockType.Unblocked;

            VerifyEntryInActiveState();
        }

        /// <summary>
        /// Yields the node.
        /// </summary>
        public void Yield()
        {
            VerifyIsNotZombie();
            SaveOperatingEnvironment();

            _blockType = BlockType.Yielded;

            RaiseOnBlockedRequest(_requestEntry.Request.GlobalRequestId, null);
        }

        /// <summary>
        /// Waits for the node to be reacquired.
        /// </summary>
        public void Reacquire()
        {
            VerifyIsNotZombie();
            RaiseOnBlockedRequest(_requestEntry.Request.GlobalRequestId, String.Empty);

            WaitHandle[] handles = [_terminateEvent, _continueEvent];

            int handle = WaitHandle.WaitAny(handles);

            RestoreOperatingEnvironment();

            if (handle == 0)
            {
                // We've been aborted
                throw new BuildAbortedException();
            }

            _blockType = BlockType.Unblocked;

            VerifyEntryInActiveState();
        }

        /// <summary>
        /// Enters the state where we are going to perform a build request callback.
        /// </summary>
        public void EnterMSBuildCallbackState()
        {
            VerifyIsNotZombie();
            ErrorUtilities.VerifyThrow(!_inMSBuildCallback, "Already in an MSBuild callback!");
            _inMSBuildCallback = true;
        }

        /// <summary>
        /// Exits the build request callback state.
        /// </summary>
        public void ExitMSBuildCallbackState()
        {
            VerifyIsNotZombie();
            ErrorUtilities.VerifyThrow(_inMSBuildCallback, "Not in an MSBuild callback!");
            _inMSBuildCallback = false;
        }

        /// <summary>
        /// Requests CPU resources from the scheduler.
        /// </summary>
        public int RequestCores(object monitorLockObject, int requestedCores, bool waitForCores)
        {
            ErrorUtilities.VerifyThrow(Monitor.IsEntered(monitorLockObject), "Not running under the given lock");
            VerifyIsNotZombie();

            // The task may be calling RequestCores from multiple threads and the call may be blocking, so in general, we have to maintain
            // a queue of pending requests.
            ResourceResponse responseObject = null;
            using AutoResetEvent responseEvent = new AutoResetEvent(false);
            _pendingResourceRequests.Enqueue((ResourceResponse response) =>
            {
                responseObject = response;
                responseEvent.Set();
            });

            RaiseResourceRequest(ResourceRequest.CreateAcquireRequest(_requestEntry.Request.GlobalRequestId, requestedCores, waitForCores));

            // Wait for one of two events to be signaled: 1) The build was canceled, 2) The response to our request was received.
            WaitHandle[] waitHandles = [_terminateEvent, responseEvent];
            int waitResult;

            // Drop the lock so that the same task can call ReleaseCores from other threads to unblock itself.
            Monitor.Exit(monitorLockObject);
            try
            {
                waitResult = WaitHandle.WaitAny(waitHandles);
            }
            finally
            {
                // Now re-take the lock before continuing.
                Monitor.Enter(monitorLockObject);
            }

            if (waitResult == 0)
            {
                // We've been aborted.
                throw new BuildAbortedException();
            }

            VerifyEntryInActiveOrWaitingState();
            return responseObject.NumCores;
        }

        /// <summary>
        /// Returns CPU resources to the scheduler.
        /// </summary>
        public void ReleaseCores(int coresToRelease)
        {
            VerifyIsNotZombie();
            RaiseResourceRequest(ResourceRequest.CreateReleaseRequest(_requestEntry.Request.GlobalRequestId, coresToRelease));
        }

        #endregion

        #region IBuildComponent Members

        /// <summary>
        /// Sets the component host.
        /// </summary>
        /// <param name="host">The component host.</param>
        public void InitializeComponent(IBuildComponentHost host)
        {
            ErrorUtilities.VerifyThrowArgumentNull(host);
            ErrorUtilities.VerifyThrow(_componentHost == null, "RequestBuilder already initialized.");
            _componentHost = host;
        }

        /// <summary>
        /// Shuts down this component
        /// </summary>
        public void ShutdownComponent()
        {
            _componentHost = null;
        }

        #endregion

        /// <summary>
        /// Returns true if this builder is using legacy threading semantics.
        /// </summary>
        internal static bool IsBuilderUsingLegacyThreadingSemantics(IBuildComponentHost host, BuildRequestEntry entry)
        {
            return host.BuildParameters.LegacyThreadingSemantics && (host.LegacyThreadingData.MainThreadSubmissionId == entry.Request.SubmissionId);
        }

        /// <summary>
        /// This method waits for the specified handles, but will also spawn a request builder "thread" if that event is set.
        /// This mechanism is used to implement running RequestBuilder threads on the main UI thread in VS.
        /// </summary>
        /// <returns>The index of the handle which was signaled.</returns>
        internal static int WaitWithBuilderThreadStart(WaitHandle[] handles, bool recursive, LegacyThreadingData threadingData, int submissionId)
        {
            WaitHandle[] allHandles = new WaitHandle[handles.Length + 1];
            allHandles[0] = threadingData.GetStartRequestBuilderMainThreadEventForSubmission(submissionId);
            Array.Copy(handles, 0, allHandles, 1, handles.Length);

            while (true)
            {
                try
                {
                    int signaledIndex = WaitHandle.WaitAny(allHandles, Timeout.Infinite);

                    if (signaledIndex == 0)
                    {
                        // Grab the request builder reserved for running on this thread.
                        RequestBuilder builder = threadingData.InstanceForMainThread;

                        // This clears out the value so we can re-enter with legacy-threading semantics on another request builder
                        // which must use this same thread.  It is safe to perform this operation because request activations cannot
                        // happen in parallel on the same thread, so there is no race.
                        threadingData.InstanceForMainThread = null;

                        // Now wait for the request to build.
                        builder.RequestThreadProc(setThreadParameters: false).Wait();
                    }
                    else
                    {
                        // We were signalled on one of the other handles.  Return control to the caller.
                        return signaledIndex - 1;
                    }
                }
                finally
                {
                    // If this was the top level submission doing the waiting, we are done with this submission and it's
                    // main thread building context
                    if (!recursive)
                    {
                        // Set the event indicating the legacy thread is no longer being used, so it is safe to clean up.
                        threadingData.SignalLegacyThreadEnd(submissionId);
                    }
                }
            }
        }

        /// <summary>
        /// Class factory for component creation.
        /// </summary>
        internal static IBuildComponent CreateComponent(BuildComponentType type)
        {
            ErrorUtilities.VerifyThrow(type == BuildComponentType.RequestBuilder, "Cannot create components of type {0}", type);
            return new RequestBuilder();
        }

        /// <summary>
        /// Starts the thread used to build
        /// </summary>
        private void StartBuilderThread()
        {
            ErrorUtilities.VerifyThrow(_requestTask == null, "Already have a task.");

            _cancellationTokenSource = new CancellationTokenSource();

            // IMPLEMENTATION NOTE: It may look strange that we are creating new tasks here which immediately turn around and create
            // more tasks that look async.  The reason for this is that while these methods are technically async, they really only
            // unwind at very specific times according to the needs of MSBuild, in particular when we are waiting for results from
            // another project or when we are Yielding the Build Engine while running certain tasks.  Essentially, the Request Builder
            // and related components form a giant state machine and the tasks are used to implement one very deep co-routine.
            if (IsBuilderUsingLegacyThreadingSemantics(_componentHost, _requestEntry))
            {
                // Create a task which completes when the legacy threading task thread is finished.
                _componentHost.LegacyThreadingData.SignalLegacyThreadStart(this);

                _requestTask = Task.Factory.StartNew(
                    () =>
                    {
                        // If this is a very quick-running request, it is possible that the request will have built and completed in
                        // the time between when StartBuilderThread is called, and when the threadpool gets around to actually servicing
                        // this request.  If that's the case, it's also possible that ShutdownComponent() could have already been called,
                        // in which case the componentHost will be null.

                        // In that circumstance, by definition we don't have anyone who will want to wait on the LegacyThreadInactiveEvent
                        // task, so we can safely just return. Take a snapshot so that we don't fall victim to componentHost being set
                        // to null between the null check and asking the LegacyThreadingData for the Task.
                        IBuildComponentHost componentHostSnapshot = _componentHost;

                        if (componentHostSnapshot?.LegacyThreadingData != null)
                        {
                            return componentHostSnapshot.LegacyThreadingData.GetLegacyThreadInactiveTask(_requestEntry.Request.SubmissionId);
                        }
                        else
                        {
                            return Task.FromResult<object>(null);
                        }
                    },
                    _cancellationTokenSource.Token,
                    TaskCreationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
            else
            {
                ErrorUtilities.VerifyThrow(_componentHost.LegacyThreadingData.MainThreadSubmissionId != _requestEntry.Request.SubmissionId, "Can't start builder thread when we are using legacy threading semantics for this request.");

                // We do not run in STA by default.  Most code does not
                // require the STA apartment and the .Net default is to
                // create threads with MTA semantics.  We provide this
                // switch so that those few tasks which may require it
                // can be made to work.
                if (Environment.GetEnvironmentVariable("MSBUILDFORCESTA") == "1")
                {
                    _requestTask = Task.Factory.StartNew(
                        () =>
                        {
                            return this.RequestThreadProc(setThreadParameters: true);
                        },
                        _cancellationTokenSource.Token,
                        TaskCreationOptions.None,
                        AwaitExtensions.OneSTAThreadPerTaskSchedulerInstance).Unwrap();
                }
                else
                {
                    // Start up the request thread.  When it starts it will begin building our current entry.
                    _requestTask = Task.Factory.StartNew(
                        () =>
                        {
                            return this.RequestThreadProc(setThreadParameters: true);
                        },
                        _cancellationTokenSource.Token,
                        TaskCreationOptions.None,
                        s_dedicatedScheduler).Unwrap();
                }
            }
        }

        /// <summary>
        /// Set some parameters common to all worker threads we use
        /// </summary>
        private void SetCommonWorkerThreadParameters()
        {
            CultureInfo.CurrentCulture = _componentHost.BuildParameters.Culture;
            CultureInfo.CurrentUICulture = _componentHost.BuildParameters.UICulture;

            Thread.CurrentThread.Priority = _componentHost.BuildParameters.BuildThreadPriority;
            Thread.CurrentThread.IsBackground = true;

            // NOTE: This is safe to do because we have specified long-running so we get our own new thread.
            string threadName = "RequestBuilder thread";

#if FEATURE_APARTMENT_STATE
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                // NOTE: This is safe to do because the STA scheduler always gives us our own new thread.
                threadName = "RequestBuilder STA thread";
            }
#endif
            if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
            {
                Thread.CurrentThread.Name = threadName;
            }
        }

        /// <summary>
        /// Asserts that the entry is in the ready state.
        /// </summary>
        private void VerifyEntryInReadyState()
        {
            ErrorUtilities.VerifyThrow(_requestEntry.State == BuildRequestEntryState.Ready, "Entry is not in the Ready state, it is in the {0} state.", _requestEntry.State);
        }

        /// <summary>
        /// Asserts that the entry is in the active state.
        /// </summary>
        private void VerifyEntryInActiveState()
        {
            ErrorUtilities.VerifyThrow(_requestEntry.State == BuildRequestEntryState.Active, "Entry is not in the Active state, it is in the {0} state.", _requestEntry.State);
        }

        /// <summary>
        /// Asserts that the entry is in the active or waiting state.
        /// </summary>
        private void VerifyEntryInActiveOrWaitingState()
        {
            ErrorUtilities.VerifyThrow(_requestEntry.State == BuildRequestEntryState.Active || _requestEntry.State == BuildRequestEntryState.Waiting,
                "Entry is not in the Active or Waiting state, it is in the {0} state.", _requestEntry.State);
        }

        /// <summary>
        /// The entry point for the request builder thread.
        /// Launch the project and gather the results, reporting them back to the BuildRequestEngine.
        /// </summary>
        private async Task RequestThreadProc(bool setThreadParameters)
        {
            Exception thrownException = null;
            BuildResult result = null;
            ProjectBuildStats stats = null;

            try
            {
                if (setThreadParameters)
                {
                    SetCommonWorkerThreadParameters();
                }
                MSBuildEventSource.Log.RequestThreadProcStart();
                VerifyEntryInActiveState();
                (result, stats) = await BuildProject();
                MSBuildEventSource.Log.RequestThreadProcStop();
            }
            catch (InvalidProjectFileException ex)
            {
                if (_projectLoggingContext != null)
                {
                    _projectLoggingContext.LogInvalidProjectFileError(ex);
                }
                else
                {
                    _nodeLoggingContext.LogInvalidProjectFileError(ex);
                }

                thrownException = ex;
            }
            // This is a workaround for https://github.com/dotnet/msbuild/issues/2064. It catches the exception case and turns it into a more understandable warning.
            catch (UnbuildableProjectTypeException ex)
            {
                thrownException = ex;
                if (_projectLoggingContext is null)
                {
                    _nodeLoggingContext.LogWarning("SolutionParseUnknownProjectType", ex.Message);
                }
                else
                {
                    _projectLoggingContext.LogWarning("SolutionParseUnknownProjectType", ex.Message);
                }
            }
            catch (Exception ex)
            {
                thrownException = ex;
                if (ex is BuildAbortedException)
                {
                    // The build was likely cancelled. We do not need to log an error in this case.
                }
                else if (ex is InternalLoggerException)
                {
                    string realMessage = TaskLoggingHelper.GetInnerExceptionMessageString(ex);
                    LoggingContext loggingContext = ((LoggingContext)_projectLoggingContext) ?? _nodeLoggingContext;
                    loggingContext.LogError(
                        BuildEventFileInfo.Empty,
                        "FatalErrorWhileLoggingWithInnerException",
                        realMessage);

                    loggingContext.LogCommentFromText(MessageImportance.Low, ex.ToString());
                }
                else if (ex is ThreadAbortException)
                {
                    // Do nothing.  This will happen when the thread is forcibly terminated because we are shutting down, for example
                    // when the unit test framework terminates.
                    throw;
                }
                else if (ex is not CriticalTaskException)
                {
                    (((LoggingContext)_projectLoggingContext) ?? _nodeLoggingContext).LogError(BuildEventFileInfo.Empty, "UnhandledMSBuildError", ex.ToString());
                }

                if (ExceptionHandling.IsCriticalException(ex))
                {
                    // Dump all engine exceptions to a temp file
                    // so that we have something to go on in the
                    // event of a failure
                    ExceptionHandling.DumpExceptionToFile(ex);

                    // This includes InternalErrorException, which we definitely want a callstack for.
                    // Fortunately the default console UnhandledExceptionHandler will log the callstack even
                    // for unhandled exceptions thrown from threads other than the main thread, like here.
                    // Less fortunately NUnit doesn't.

                    // This is fatal: process will terminate: make sure the
                    // debugger launches
                    ErrorUtilities.ThrowInternalError(ex.Message, ex);
                    throw;
                }
            }
            finally
            {
                _blockType = BlockType.Unblocked;

                if (thrownException != null)
                {
                    ErrorUtilities.VerifyThrow(result == null, "Result already set when exception was thrown.");
                    result = new BuildResult(_requestEntry.Request, thrownException);
                }

                ReportResultAndCleanUp(result, stats);
            }
        }

        /// <summary>
        /// Reports this result to the engine and cleans up.
        /// </summary>
        private void ReportResultAndCleanUp(BuildResult result, ProjectBuildStats stats)
        {
            if (_projectLoggingContext != null)
            {
                try
                {
                    _projectLoggingContext.LogProjectFinished(result.OverallResult == BuildResultCode.Success, stats);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    if (result.Exception == null)
                    {
                        result.Exception = ex;
                    }
                }
            }

            // Clear out our state now in case any of these callbacks cause the engine to try and immediately
            // reuse this builder.
            BuildRequestEntry entryToComplete = _requestEntry;
            _nodeLoggingContext = null;
            _requestEntry = null;
            if (_targetBuilder != null)
            {
                ((IBuildComponent)_targetBuilder).ShutdownComponent();
            }

            if (_componentHost.BuildParameters.SaveOperatingEnvironment)
            {
                entryToComplete.RequestConfiguration.SavedCurrentDirectory = NativeMethodsShared.GetCurrentDirectory();
                entryToComplete.RequestConfiguration.SavedEnvironmentVariables = CommunicationsUtilities.GetEnvironmentVariables();
            }

            entryToComplete.Complete(result);
            RaiseBuildRequestCompleted(entryToComplete);
        }

        /// <summary>
        /// This is called back when this request needs to issue new requests and possible wait on them.  This method will
        /// block the builder's thread if any of the requests require us to wait for their results.
        /// </summary>
        /// <param name="requests">The list of build requests to be built.</param>
        /// <returns>The results, or null if the build should terminate.</returns>
        private async Task<BuildResult[]> StartNewBuildRequests(FullyQualifiedBuildRequest[] requests)
        {
            // Determine if we need to wait for results from any of these requests.
            // UNDONE: Currently we never set ResultsNeeded to anything but true.  The purpose of this flag would be
            // to issue another top-level build request which no other request depends on, but which must finish in order for
            // the build to be considered complete.  This would be brand new semantics.
            bool waitForResults = false;
            foreach (FullyQualifiedBuildRequest request in requests)
            {
                if (request.ResultsNeeded)
                {
                    waitForResults = true;
                    break;
                }
            }

            _blockType = BlockType.BlockedOnChildRequests;

            // Save the current operating environment, if necessary
            if (waitForResults)
            {
                SaveOperatingEnvironment();
            }

            // Issue the requests to the engine
            RaiseOnNewBuildRequests(requests);

            // TODO: OPTIMIZATION: By returning null here, we commit to having to unwind the stack all the
            // way back to RequestThreadProc and then shutting down the thread before we can receive the
            // results and continue with them.  It is not always the case that this will be desirable, however,
            // particularly if the results we need are immediately available.  In those cases, it would be
            // useful to wait here for a short period in case those results become available - one second
            // might be enough.  This means we may occasionally get more than one builder thread lying around
            // waiting for something to happen, but that would be short lived.  At the same time it would
            // allow these already-available results to be utilized immediately without the unwind
            // semantics.

            // Now wait for results if we are supposed to.
            BuildResult[] results;
            if (waitForResults)
            {
                WaitHandle[] handles = [_terminateEvent, _continueEvent];

                int handle;
                if (IsBuilderUsingLegacyThreadingSemantics(_componentHost, _requestEntry))
                {
                    handle = RequestBuilder.WaitWithBuilderThreadStart(handles, true, _componentHost.LegacyThreadingData, _requestEntry.Request.SubmissionId);
                }
                else if (_inMSBuildCallback)
                {
                    CultureInfo savedCulture = CultureInfo.CurrentCulture;
                    CultureInfo savedUICulture = CultureInfo.CurrentUICulture;

                    handle = await handles.ToTask();

                    CultureInfo.CurrentCulture = savedCulture;
                    CultureInfo.CurrentUICulture = savedUICulture;
                }
                else
                {
                    handle = WaitHandle.WaitAny(handles);
                }

                // If this is not a shutdown case, then the entry should be in the active state.
                if (handle == 1)
                {
                    // Restore the operating environment.
                    RestoreOperatingEnvironment();
                    VerifyEntryInActiveState();
                }

                results = GetResultsForContinuation(requests, handle == 1);
            }
            else
            {
                results = Array.Empty<BuildResult>();
            }

            ErrorUtilities.VerifyThrow(requests.Length == results.Length, "# results != # requests");

            _blockType = BlockType.Unblocked;
            return results;
        }

        /// <summary>
        /// Gets the results uses to continue the current build request.
        /// </summary>
        private BuildResult[] GetResultsForContinuation(FullyQualifiedBuildRequest[] requests, bool isContinue)
        {
            IDictionary<int, BuildResult> results = _continueResults;
            _continueResults = null;
            if (results == null)
            {
                // This catches the case where an MSBuild call is making a series of non-parallel build requests after Cancel has been
                // invoked.  In this case, the wait above will immediately fall through and there will be no results.  We just need to be
                // sure that we return a complete set of results which indicate we are aborting.
                ErrorUtilities.VerifyThrow(!isContinue, "Unexpected null results during continue");
                results = new Dictionary<int, BuildResult>();
                for (int i = 0; i < requests.Length; i++)
                {
                    results[i] = new BuildResult(new BuildRequest(), new BuildAbortedException());
                }
            }

            // See if we got any exceptions we should throw.
            foreach (BuildResult result in results.Values)
            {
                if (result.CircularDependency)
                {
                    throw new CircularDependencyException();
                }
            }

            // The build results will have node request IDs in the same order as the requests were issued,
            // which is in the array order above.
            List<BuildResult> resultsList = new List<BuildResult>(results.Values);
            resultsList.Sort(delegate (BuildResult left, BuildResult right)
            {
                if (left.NodeRequestId < right.NodeRequestId)
                {
                    return -1;
                }
                else if (left.NodeRequestId == right.NodeRequestId)
                {
                    return 0;
                }

                return 1;
            });

            return resultsList.ToArray();
        }

        /// <summary>
        /// Invokes the OnNewBuildRequests event
        /// </summary>
        /// <param name="requests">The requests to be fulfilled.</param>
        private void RaiseOnNewBuildRequests(FullyQualifiedBuildRequest[] requests)
        {
            OnNewBuildRequests?.Invoke(_requestEntry, requests);
        }

        /// <summary>
        /// Invokes the OnBuildRequestCompleted event
        /// </summary>
        private void RaiseBuildRequestCompleted(BuildRequestEntry entryToComplete)
        {
            OnBuildRequestCompleted?.Invoke(entryToComplete);
        }

        /// <summary>
        /// Invokes the OnBlockedRequest event
        /// </summary>
        private void RaiseOnBlockedRequest(int blockingGlobalRequestId, string blockingTarget, BuildResult partialBuildResult = null)
        {
            OnBuildRequestBlocked?.Invoke(_requestEntry, blockingGlobalRequestId, blockingTarget, partialBuildResult);
        }

        /// <summary>
        /// Invokes the OnResourceRequest event
        /// </summary>
        /// <param name="request"></param>
        private void RaiseResourceRequest(ResourceRequest request)
        {
            OnResourceRequest?.Invoke(request);
        }

        /// <summary>
        /// This method is called to reset the current directory to the one appropriate for this project.  It should be called any time
        /// the project is resumed.
        /// If the directory does not exist, does nothing.
        /// This is because if the project has not been saved, this directory may not exist, yet it is often useful to still be able to build the project.
        /// No errors are masked by doing this: errors loading the project from disk are reported at load time, if necessary.
        /// </summary>
        private void SetProjectCurrentDirectory()
        {
            if (_componentHost.BuildParameters.SaveOperatingEnvironment)
            {
                NativeMethodsShared.SetCurrentDirectory(_requestEntry.ProjectRootDirectory);
            }
        }

        /// <summary>
        /// Kicks off the build of the project file.  Doesn't return until the build is complete (or aborted.)
        /// </summary>
        private async Task<(BuildResult, ProjectBuildStats)> BuildProject()
        {
            ErrorUtilities.VerifyThrow(_targetBuilder != null, "Target builder is null");

            // We consider this the entrypoint for the project build for purposes of BuildCheck processing
            bool isRestoring = _requestEntry.RequestConfiguration.GlobalProperties[MSBuildConstants.MSBuildIsRestoring] is not null;

            var buildCheckManager = isRestoring
                ? null
                : (_componentHost.GetComponent(BuildComponentType.BuildCheckManagerProvider) as IBuildCheckManagerProvider)!.Instance;

            buildCheckManager?.SetDataSource(BuildCheckDataSource.BuildExecution);

            // Make sure it is null before loading the configuration into the request, because if there is a problem
            // we do not wand to have an invalid projectLoggingContext floating around. Also if this is null the error will be
            // logged with the node logging context
            _projectLoggingContext = null;

            MSBuildEventSource.Log.BuildProjectStart(_requestEntry.RequestConfiguration.ProjectFullPath);

            try
            {
                // Load the project
                if (!_requestEntry.RequestConfiguration.IsLoaded)
                {
                    buildCheckManager?.ProjectFirstEncountered(
                        BuildCheckDataSource.BuildExecution,
                        new CheckLoggingContext(_nodeLoggingContext.LoggingService, _requestEntry.Request.BuildEventContext),
                        _requestEntry.RequestConfiguration.ProjectFullPath);

                    _requestEntry.RequestConfiguration.LoadProjectIntoConfiguration(
                        _componentHost,
                        RequestEntry.Request.BuildRequestDataFlags,
                        RequestEntry.Request.SubmissionId,
                        _nodeLoggingContext.BuildEventContext.NodeId);
                }
            }
            catch
            {
                // make sure that any errors thrown by a child project are logged in the context of their parent project: create a temporary projectLoggingContext
                _projectLoggingContext = new ProjectLoggingContext(
                    _nodeLoggingContext,
                    _requestEntry.Request,
                    _requestEntry.RequestConfiguration.ProjectFullPath,
                    _requestEntry.RequestConfiguration.ToolsVersion);

                throw;
            }
            finally
            {
                buildCheckManager?.EndProjectEvaluation(
                    _requestEntry.Request.BuildEventContext);
            }


            try
            {
                HandleProjectStarted(buildCheckManager);

                // Make sure to extract known immutable folders from properties and register them for fast up-to-date check
                ConfigureKnownImmutableFolders();

                // See comment on Microsoft.Build.Internal.Utilities.GenerateToolsVersionToUse
                _requestEntry.RequestConfiguration.RetrieveFromCache();
                if (_requestEntry.RequestConfiguration.Project.UsingDifferentToolsVersionFromProjectFile)
                {
                    _projectLoggingContext.LogComment(MessageImportance.Low,
                        "UsingDifferentToolsVersionFromProjectFile",
                        _requestEntry.RequestConfiguration.Project.OriginalProjectToolsVersion,
                        _requestEntry.RequestConfiguration.Project.ToolsVersion);
                }

                _requestEntry.Request.BuildEventContext = _projectLoggingContext.BuildEventContext;

                // Determine the set of targets we need to build
                (string name, TargetBuiltReason reason)[] allTargets = _requestEntry.RequestConfiguration
                    .GetTargetsUsedToBuildRequest(_requestEntry.Request).ToArray();

                ProjectErrorUtilities.VerifyThrowInvalidProject(allTargets.Length > 0,
                    _requestEntry.RequestConfiguration.Project.ProjectFileLocation, "NoTargetSpecified");

                // Set the current directory to that required by the project.
                SetProjectCurrentDirectory();

                // Transfer results and state from the previous node, if necessary.
                // In order for the check for target completeness for this project to be valid, all of the target results from the project must be present
                // in the results cache.  It is possible that this project has been moved from its original node and when it was its results did not come
                // with it.  This would be signified by the ResultsNode value in the configuration pointing to a different node than the current one.  In that
                // case we will need to request those results be moved from their original node to this one.
                if ((_requestEntry.RequestConfiguration.ResultsNodeId != Scheduler.InvalidNodeId) &&
                    (_requestEntry.RequestConfiguration.ResultsNodeId != _componentHost.BuildParameters.NodeId))
                {
                    // This indicates to the system that we will block waiting for a results transfer.  We will block here until those results become available.
                    await BlockOnTargetInProgress(Microsoft.Build.BackEnd.BuildRequest.InvalidGlobalRequestId, null);

                    // All of the results should now be on this node.
                    ErrorUtilities.VerifyThrow(
                        _requestEntry.RequestConfiguration.ResultsNodeId == _componentHost.BuildParameters.NodeId,
                        "Results for configuration {0} were not retrieved from node {1}",
                        _requestEntry.RequestConfiguration.ConfigurationId,
                        _requestEntry.RequestConfiguration.ResultsNodeId);
                }

                ProjectBuildStats projectBuildStats = GetPreBuildStatistics();

                // Build the targets
                BuildResult result = await _targetBuilder.BuildTargets(_projectLoggingContext, _requestEntry, this,
                    allTargets, _requestEntry.RequestConfiguration.BaseLookup, _cancellationTokenSource.Token);

                UpdateStatisticsPostBuild(projectBuildStats);

                result = _requestEntry.Request.ProxyTargets == null
                    ? result
                    : CopyTargetResultsFromProxyTargetsToRealTargets(result);

                if (MSBuildEventSource.Log.IsEnabled())
                {
                    MSBuildEventSource.Log.BuildProjectStop(_requestEntry.RequestConfiguration.ProjectFullPath,
                        string.Join(", ", allTargets));
                }

                return (result, projectBuildStats);
            }
            finally
            {
                buildCheckManager?.EndProjectRequest(
                    new CheckLoggingContext(_nodeLoggingContext.LoggingService, _projectLoggingContext.BuildEventContext),
                    _requestEntry.RequestConfiguration.ProjectFullPath);
            }


            BuildResult CopyTargetResultsFromProxyTargetsToRealTargets(BuildResult resultFromTargetBuilder)
            {
                var proxyTargetMapping = _requestEntry.Request.ProxyTargets.ProxyTargetToRealTargetMap;

                var resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
                var cachedResult = resultsCache.GetResultsForConfiguration(_requestEntry.Request.ConfigurationId);

                // Some proxy targets do not point to real targets. Exclude those.
                foreach (var proxyMapping in proxyTargetMapping.Where(kvp => kvp.Value != null))
                {
                    var proxyTarget = proxyMapping.Key;
                    var realTarget = proxyMapping.Value;

                    var proxyTargetResult = resultFromTargetBuilder.ResultsByTarget[proxyTarget];

                    // Update the results cache.
                    cachedResult.AddResultsForTarget(
                        realTarget,
                        proxyTargetResult);

                    // Update and return this one because TargetBuilder.BuildTargets did some mutations on it not present in the cached result.
                    resultFromTargetBuilder.AddResultsForTarget(
                        realTarget,
                        proxyTargetResult);
                }

                return resultFromTargetBuilder;
            }
        }

        private ProjectBuildStats GetPreBuildStatistics()
        {
            if (!_componentHost.BuildParameters.IsTelemetryEnabled)
            {
                return null;
            }

            ProjectBuildStats stats = new ProjectBuildStats(false);

            IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
            BuildResult existingBuildResult = resultsCache.GetResultsForConfiguration(_requestEntry.Request.ConfigurationId);

            stats.TotalTargetsCount = (short)_requestEntry.RequestConfiguration.Project.TargetsCount;

            stats.CustomTargetsCount = (short)
                _requestEntry.RequestConfiguration.Project.Targets.Count(t =>
                    IsCustomTargetPath(t.Value.FullPath));

            if (existingBuildResult?.ResultsByTarget != null && stats.CustomTargetsCount > 0)
            {
                stats.ExecutedCustomTargetsCount = (short)_requestEntry.RequestConfiguration.Project.Targets
                    .Where(t => IsCustomTargetPath(t.Value.FullPath))
                    .Count(t => existingBuildResult.ResultsByTarget.ContainsKey(t.Key));
            }

            stats.TotalTargetsExecutionsCount = (short)(existingBuildResult?.ResultsByTarget.Count ?? 0);

            return stats;
        }

        private void UpdateStatisticsPostBuild(ProjectBuildStats stats)
        {
            if (stats == null)
            {
                return;
            }

            IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
            // The TargetBuilder filters out results for targets not explicitly requested before returning the result.
            // Hence we need to fetch the original result from the cache - to get the data for all executed targets.
            BuildResult unfilteredResult = resultsCache.GetResultsForConfiguration(_requestEntry.Request.ConfigurationId);

            // Count only new executions
            stats.TotalTargetsExecutionsCount = (short)(unfilteredResult.ResultsByTarget.Count - stats.TotalTargetsExecutionsCount);

            if (stats.CustomTargetsCount > 0)
            {
                int executedCustomTargetsCount = _requestEntry.RequestConfiguration.Project.Targets
                    .Where(t => IsCustomTargetPath(t.Value.FullPath))
                    .Count(t => unfilteredResult.ResultsByTarget.ContainsKey(t.Key));

                // Count only new executions
                stats.ExecutedCustomTargetsCount = (short)(executedCustomTargetsCount - stats.ExecutedCustomTargetsCount);
            }

            TaskRegistry taskReg = _requestEntry.RequestConfiguration.Project.TaskRegistry;
            CollectTasksStats(taskReg, stats);

            void CollectTasksStats(TaskRegistry taskRegistry, ProjectBuildStats projectBuildStats)
            {
                if (taskRegistry == null)
                {
                    return;
                }

                foreach (TaskRegistry.RegisteredTaskRecord registeredTaskRecord in taskRegistry.TaskRegistrations.Values.SelectMany(record => record))
                {
                    projectBuildStats.AddTask(registeredTaskRecord.TaskIdentity.Name,
                        registeredTaskRecord.Statistics.ExecutedTime,
                        registeredTaskRecord.Statistics.ExecutedCount,
                        registeredTaskRecord.GetIsCustom());

                    registeredTaskRecord.Statistics.Reset();
                }

                taskRegistry.Toolset?.InspectInternalTaskRegistry(tr => CollectTasksStats(tr, projectBuildStats));
            }
        }

        private static bool IsCustomTargetPath(string targetPath)
            =>  !targetPath.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase) && !FileClassifier.Shared.IsBuiltInLogic(targetPath);

        /// <summary>
        /// Saves the current operating environment.
        /// </summary>
        private void SaveOperatingEnvironment()
        {
            if (_componentHost.BuildParameters.SaveOperatingEnvironment)
            {
                _requestEntry.RequestConfiguration.SavedCurrentDirectory = NativeMethodsShared.GetCurrentDirectory();
                _requestEntry.RequestConfiguration.SavedEnvironmentVariables = CommunicationsUtilities.GetEnvironmentVariables();
            }
        }

        private void HandleProjectStarted(IBuildCheckManager buildCheckManager)
        {
            (ProjectStartedEventArgs args, ProjectLoggingContext ctx) = _nodeLoggingContext.CreateProjectLoggingContext(_requestEntry);

            _projectLoggingContext = ctx;
            ConfigureWarningsAsErrorsAndMessages();
            ILoggingService loggingService = _projectLoggingContext?.LoggingService;
            BuildEventContext projectBuildEventContext = _projectLoggingContext?.BuildEventContext;

            // We can set the warning as errors and messages only after the project logging context has been created (as it creates the new ProjectContextId)
            if (loggingService != null && projectBuildEventContext != null)
            {
                args.WarningsAsErrors = loggingService.GetWarningsAsErrors(projectBuildEventContext).ToHashSet(StringComparer.OrdinalIgnoreCase);
                args.WarningsAsMessages = loggingService.GetWarningsAsMessages(projectBuildEventContext).ToHashSet(StringComparer.OrdinalIgnoreCase);
                args.WarningsNotAsErrors = loggingService.GetWarningsNotAsErrors(projectBuildEventContext).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            // We can log the event only after the warning as errors and messages have been set and added
            loggingService?.LogProjectStarted(args);

            buildCheckManager?.StartProjectRequest(
                new CheckLoggingContext(_nodeLoggingContext.LoggingService, _projectLoggingContext!.BuildEventContext),
                _requestEntry.RequestConfiguration.ProjectFullPath);
        }

        /// <summary>
        /// Sets the operationg environment to the initial build environment.
        /// </summary>
        private void InitializeOperatingEnvironment()
        {
            if (_requestEntry.RequestConfiguration.SavedEnvironmentVariables != null && _componentHost.BuildParameters.SaveOperatingEnvironment)
            {
                // Restore the saved environment variables.
                SetEnvironmentVariableBlock(_requestEntry.RequestConfiguration.SavedEnvironmentVariables);
            }
            else
            {
                // Restore the original build environment variables.
                SetEnvironmentVariableBlock(_componentHost.BuildParameters.BuildProcessEnvironment);
            }
        }

        /// <summary>
        /// Restores a previously saved operating environment.
        /// </summary>
        private void RestoreOperatingEnvironment()
        {
            if (_componentHost.BuildParameters.SaveOperatingEnvironment)
            {
                ErrorUtilities.VerifyThrow(_requestEntry.RequestConfiguration.SavedCurrentDirectory != null, "Current directory not previously saved.");
                ErrorUtilities.VerifyThrow(_requestEntry.RequestConfiguration.SavedEnvironmentVariables != null, "Current environment not previously saved.");

                // Restore the saved environment variables.
                SetEnvironmentVariableBlock(_requestEntry.RequestConfiguration.SavedEnvironmentVariables);
                NativeMethodsShared.SetCurrentDirectory(_requestEntry.RequestConfiguration.SavedCurrentDirectory);
            }
        }

        /// <summary>
        /// Sets the environment block to the set of saved variables.
        /// </summary>
        private void SetEnvironmentVariableBlock(IDictionary<string, string> savedEnvironment)
        {
            IDictionary<string, string> currentEnvironment = CommunicationsUtilities.GetEnvironmentVariables();
            ClearVariablesNotInEnvironment(savedEnvironment, currentEnvironment);
            UpdateEnvironmentVariables(savedEnvironment, currentEnvironment);
        }

        /// <summary>
        /// Clears from the current environment any variables which do not exist in the saved environment
        /// </summary>
        private void ClearVariablesNotInEnvironment(IDictionary<string, string> savedEnvironment, IDictionary<string, string> currentEnvironment)
        {
            foreach (KeyValuePair<string, string> entry in currentEnvironment)
            {
                if (!savedEnvironment.ContainsKey(entry.Key))
                {
                    Environment.SetEnvironmentVariable(entry.Key, null);
                }
            }
        }

        /// <summary>
        /// Updates the current environment with values in the saved environment which differ or are not yet set.
        /// </summary>
        private void UpdateEnvironmentVariables(IDictionary<string, string> savedEnvironment, IDictionary<string, string> currentEnvironment)
        {
            foreach (KeyValuePair<string, string> entry in savedEnvironment)
            {
                // If the environment doesn't have the variable set, or if its value differs from what we have saved, set it
                // to the saved value.  Doing the comparison before setting is faster than unconditionally setting it using
                // the API.
                string value;
                if (!currentEnvironment.TryGetValue(entry.Key, out value) || !String.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }
            }
        }

        /// <summary>
        /// Throws if the RequestBuilder has been zombied.
        /// </summary>
        private void VerifyIsNotZombie()
        {
            ErrorUtilities.VerifyThrow(!_isZombie, "RequestBuilder has been zombied.");
        }

        /// <summary>
        /// Configure warnings as messages and errors based on properties defined in the project.
        /// </summary>
        private void ConfigureWarningsAsErrorsAndMessages()
        {
            // Gather needed objects
            ProjectInstance project = _requestEntry?.RequestConfiguration?.Project;
            BuildEventContext buildEventContext = _projectLoggingContext?.BuildEventContext;
            ILoggingService loggingService = _projectLoggingContext?.LoggingService;

            // Ensure everything that is required is available at this time
            if (project != null && buildEventContext != null && loggingService != null && buildEventContext.ProjectInstanceId != BuildEventContext.InvalidProjectInstanceId)
            {
                if (String.Equals(project.GetEngineRequiredPropertyValue(MSBuildConstants.TreatWarningsAsErrors)?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    // If <MSBuildTreatWarningsAsErrors was specified then an empty ISet<string> signals the IEventSourceSink to treat all warnings as errors
                    loggingService.AddWarningsAsErrors(buildEventContext, new HashSet<string>());
                }
                else
                {
                    ISet<string> warningsAsErrors = ParseWarningCodes(project.GetEngineRequiredPropertyValue(MSBuildConstants.WarningsAsErrors));

                    if (warningsAsErrors?.Count > 0)
                    {
                        loggingService.AddWarningsAsErrors(buildEventContext, warningsAsErrors);
                    }
                }

                ISet<string> warningsNotAsErrors = ParseWarningCodes(project.GetEngineRequiredPropertyValue(MSBuildConstants.WarningsNotAsErrors));

                if (warningsNotAsErrors?.Count > 0)
                {
                    loggingService.AddWarningsNotAsErrors(buildEventContext, warningsNotAsErrors);
                }

                ISet<string> warningsAsMessages = ParseWarningCodes(project.GetEngineRequiredPropertyValue(MSBuildConstants.WarningsAsMessages));

                if (warningsAsMessages?.Count > 0)
                {
                    loggingService.AddWarningsAsMessages(buildEventContext, warningsAsMessages);
                }
            }
        }

        private void ConfigureKnownImmutableFolders()
        {
            ProjectInstance project = _requestEntry?.RequestConfiguration?.Project;
            if (project != null)
            {
                FileClassifier.Shared.RegisterKnownImmutableLocations(project.GetPropertyValue);
            }
        }

        private static ISet<string> ParseWarningCodes(string warnings)
        {
            if (String.IsNullOrWhiteSpace(warnings))
            {
                return null;
            }

            return new HashSet<string>(ExpressionShredder.SplitSemiColonSeparatedList(warnings), StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DedicatedThreadsTaskScheduler : TaskScheduler
        {
            private readonly BlockingCollection<Task> _tasks = new BlockingCollection<Task>();
            private int _availableThreads = 0;

            protected override void QueueTask(Task task)
            {
                RequestThread();
                _tasks.Add(task);
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

            protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

            private void RequestThread()
            {
                // Decrement available thread count; don't drop below zero
                // Prior value is stored in count
                var count = Volatile.Read(ref _availableThreads);
                while (count > 0)
                {
                    var prev = Interlocked.CompareExchange(ref _availableThreads, count - 1, count);
                    if (prev == count)
                    {
                        break;
                    }
                    count = prev;
                }

                if (count == 0)
                {
                    // No threads were available for request
                    InjectThread();
                }
            }

            private void InjectThread()
            {
                var thread = new Thread(() =>
                {
                    foreach (Task t in _tasks.GetConsumingEnumerable())
                    {
                        TryExecuteTask(t);
                        Interlocked.Increment(ref _availableThreads);
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            }
        }
    }
}
