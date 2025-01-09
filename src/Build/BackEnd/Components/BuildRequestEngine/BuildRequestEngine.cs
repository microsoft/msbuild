// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.Debugging;
using BuildAbortedException = Microsoft.Build.Exceptions.BuildAbortedException;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// The BuildRequestEngine is responsible for managing the building of projects on a given node.  It
    /// receives build requests, reports results and deals with BuildRequestConfiguration transactions.
    /// As it runs on its own thread, all BuildRequestEngine operations are asynchronous.
    /// </summary>
    /// <remarks>
    /// Internally, the BuildRequestEngine manages the requests in the form of BuildRequestEntry objects.
    /// Each of these maintains the complete state of a build request, accumulating results until completion.
    /// The EngineLoop method is the separate thread proc which handles state changes for BuildRequestEntries
    /// and shutting down.  However, each RequestBuilder can call back into the BuildRequestEngine (via events)
    /// to cause new requests to be submitted.  See <seealso cref="IssueBuildRequest"/>.
    /// </remarks>
    internal class BuildRequestEngine : IBuildRequestEngine, IBuildComponent
    {
        /// <summary>
        /// The starting unresolved configuration id assigned by the engine.
        /// </summary>
        private const int StartingUnresolvedConfigId = -1;

        /// <summary>
        /// The starting build request id
        /// </summary>
        private const int StartingBuildRequestId = 1;

        /// <summary>
        /// The current engine status
        /// </summary>
        private BuildRequestEngineStatus _status;

        /// <summary>
        /// Ths component host
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The work queue.
        /// </summary>
        private ActionBlock<Action> _workQueue;

        /// <summary>
        /// The list of current requests the engine is working on.
        /// </summary>
        private readonly List<BuildRequestEntry> _requests;

        /// <summary>
        /// Mapping of global request ids to the request entries.
        /// </summary>
        private readonly Dictionary<int, BuildRequestEntry> _requestsByGlobalRequestId;

        /// <summary>
        /// The list of requests currently waiting to be submitted from RequestBuilders.
        /// </summary>
        private readonly Queue<PendingUnsubmittedBuildRequests> _unsubmittedRequests;

        /// <summary>
        /// The next available local unresolved configuration Id
        /// </summary>
        private int _nextUnresolvedConfigurationId;

        /// <summary>
        /// The next available build request Id
        /// </summary>
        private int _nextBuildRequestId;

        /// <summary>
        /// The global configuration cache
        /// </summary>
        private IConfigCache _configCache;

        /// <summary>
        /// The list of unresolved configurations
        /// </summary>
        private IConfigCache _unresolvedConfigurations;

        /// <summary>
        /// The logging context for the node
        /// </summary>
        private NodeLoggingContext _nodeLoggingContext;

        /// <summary>
        /// Flag indicating if we should trace.
        /// </summary>
        private readonly bool _debugDumpState;

        /// <summary>
        /// The path where we will store debug files
        /// </summary>
        private readonly string _debugDumpPath;

        /// <summary>
        /// Forces caching of all configurations and results.
        /// </summary>
        private readonly bool _debugForceCaching;

        /// <summary>
        /// Constructor
        /// </summary>
        internal BuildRequestEngine()
        {
            _debugDumpState = Traits.Instance.DebugScheduler;
            _debugDumpPath = DebugUtils.DebugPath;
            _debugForceCaching = Environment.GetEnvironmentVariable("MSBUILDDEBUGFORCECACHING") == "1";

            if (String.IsNullOrEmpty(_debugDumpPath))
            {
                _debugDumpPath = FileUtilities.TempFileDirectory;
            }

            _status = BuildRequestEngineStatus.Uninitialized;
            _nextUnresolvedConfigurationId = -1;
            _nextBuildRequestId = 0;
            _requests = new List<BuildRequestEntry>();
            _unsubmittedRequests = new Queue<PendingUnsubmittedBuildRequests>();
            _requestsByGlobalRequestId = new Dictionary<int, BuildRequestEntry>();
        }

        #region IBuildRequestEngine Members

        /// <summary>
        /// Raised when a request has completed.
        /// </summary>
        public event RequestCompleteDelegate OnRequestComplete;

        /// <summary>
        /// Raised when a request is resumed by the engine itself.
        /// </summary>
        public event RequestResumedDelegate OnRequestResumed;

        /// <summary>
        /// Raised when a new request is generated.
        /// </summary>
        public event RequestBlockedDelegate OnRequestBlocked;

        /// <summary>
        /// Raised when the engine's status has changed.
        /// </summary>
        public event EngineStatusChangedDelegate OnStatusChanged;

        /// <summary>
        /// Raised when a configuration needs its ID resolved.
        /// </summary>
        public event NewConfigurationRequestDelegate OnNewConfigurationRequest;

        /// <summary>
        /// Raised when an unexpected exception occurs.
        /// </summary>
        public event EngineExceptionDelegate OnEngineException;

        /// <summary>
        /// Raised when resources are requested.
        /// </summary>
        public event ResourceRequestDelegate OnResourceRequest;

        /// <summary>
        /// Returns the current engine status.
        /// </summary>
        public BuildRequestEngineStatus Status => _status;

        /// <summary>
        /// Prepares the build request engine to run a build.
        /// </summary>
        /// <param name="loggingContext">The logging context to use.</param>
        /// <remarks>
        /// Called by the Node.  Non-overlapping with other calls from the Node.</remarks>
        public void InitializeForBuild(NodeLoggingContext loggingContext)
        {
            ErrorUtilities.VerifyThrow(_componentHost != null, "BuildRequestEngine not initialized by component host.");
            ErrorUtilities.VerifyThrow(_status == BuildRequestEngineStatus.Uninitialized, "Engine must be in the Uninitiailzed state, but is {0}", _status);

            _nodeLoggingContext = loggingContext;

            // Create a work queue that will take an action and invoke it.  The generic parameter is the type which ActionBlock.Post() will
            // take (an Action in this case) and the parameter to this constructor is a function which takes that parameter of type Action
            // (which we have named action) and does something with it (in this case calls invoke on it.)
            _workQueue = new ActionBlock<Action>(action => action.Invoke());
            ChangeStatus(BuildRequestEngineStatus.Idle);
        }

        /// <summary>
        /// Cleans up after a build but leaves the engine thread running.  Aborts
        /// any outstanding requests.  Blocks until the engine has cleaned up
        /// everything.  After this method is called, InitializeForBuild may be
        /// called to start a new build, or the component may be shut down.
        /// </summary>
        /// <remarks>
        /// Called by the Node.  Non-overlapping with other calls from the Node.
        /// </remarks>
        public void CleanupForBuild()
        {
            QueueAction(
                () =>
                {
                    ErrorUtilities.VerifyThrow(_status == BuildRequestEngineStatus.Active || _status == BuildRequestEngineStatus.Idle || _status == BuildRequestEngineStatus.Waiting, "Engine must be Active, Idle or Waiting to clean up, but is {0}.", _status);
                    TraceEngine("CFB: Cleaning up build.  Requests Count {0}  Status {1}", _requests.Count, _status);
                    ErrorUtilities.VerifyThrow(_nodeLoggingContext != null, "Node logging context not set.");

                    // Determine how many requests there are to shut down, then terminate all of their builders.
                    // We will capture the exceptions which happen here (but continue shutting down gracefully.)
                    var requestsToShutdown = new List<BuildRequestEntry>(_requests);
                    var deactivateExceptions = new List<Exception>(_requests.Count);

                    // VC observed their tasks (e.g. "CL") received the "cancel" event in several seconds after CTRL+C was captured;
                    // and the reason was we signaled the "cancel" event to the build request and wait for its completion one by one.
                    // So we made this minor optimization to signal the "cancel" events to all the build requests and then wait for all of them to be completed.
                    // From the experiments on a big VC solution, this optimization showed slightly better result consistently.
                    // For the extremely bad case, say, it takes 10 seconds to "cancel" the build; this optimization could save 2 to 4 seconds.
                    var requestsToWait = new List<BuildRequestEntry>(_requests.Count);
                    foreach (BuildRequestEntry entry in requestsToShutdown)
                    {
                        try
                        {
                            BeginDeactivateBuildRequest(entry);
                            requestsToWait.Add(entry);
                        }
                        catch (Exception e)
                        {
                            TraceEngine("CFB: Shutting down request {0}({1}) (nr {2}) failed due to exception: {3}", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId, e.ToString());
                            if (ExceptionHandling.IsCriticalException(e))
                            {
                                throw;
                            }

                            TraceEngine("CFB: Aggregating last shutdown exception");
                            deactivateExceptions.Add(e);
                        }
                    }

                    foreach (BuildRequestEntry entry in requestsToWait)
                    {
                        try
                        {
                            WaitForDeactivateCompletion(entry);
                        }
                        catch (Exception e)
                        {
                            TraceEngine("CFB: Shutting down request {0}({1}) (nr {2}) failed due to exception: {3}", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId, e.ToString());
                            if (ExceptionHandling.IsCriticalException(e))
                            {
                                throw;
                            }

                            TraceEngine("CFB: Aggregating last shutdown exception");
                            deactivateExceptions.Add(e);
                        }
                    }

                    // Report our results.
                    foreach (BuildRequestEntry entry in requestsToShutdown)
                    {
                        BuildResult result = entry.Result ?? new BuildResult(entry.Request, new BuildAbortedException());
                        TraceEngine("CFB: Request is now {0}({1}) (nr {2}) has been deactivated.", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId);
                        RaiseRequestComplete(entry.Request, result);
                    }

                    // Any exceptions which occurred while we are shutting down request builders should be thrown now so the outer handler
                    // can report them.
                    if (deactivateExceptions.Count > 0)
                    {
                        TraceEngine("CFB: Rethrowing shutdown exceptions");
                        throw new AggregateException(deactivateExceptions);
                    }

                    IBuildCheckManagerProvider buildCheckProvider = (_componentHost.GetComponent(BuildComponentType.BuildCheckManagerProvider) as IBuildCheckManagerProvider);
                    var buildCheckManager = buildCheckProvider!.Instance;
                    buildCheckManager.FinalizeProcessing(_nodeLoggingContext);
                    // Clears the instance so that next call (on node reuse) to 'GetComponent' leads to reinitialization.
                    buildCheckProvider.ShutdownComponent();
                },
                isLastTask: true);

            // Wait for the task to finish
            try
            {
                _workQueue.Completion.Wait();
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                // If we caught an exception during cleanup, we need to log that
                ErrorUtilities.ThrowInternalError("Failure during engine shutdown.  Exception: {0}", e.ToString());
            }
            finally
            {
                // Now all requests have been deactivated.  Any requests which got placed in the queue while we were waiting
                // for builders to shut down will be discarded, so when we return from this function there will be no work
                // to do.
                _workQueue = null;
                _requests.Clear();
                _requestsByGlobalRequestId.Clear();
                _unsubmittedRequests.Clear();
                _unresolvedConfigurations.ClearConfigurations();
                ChangeStatus(BuildRequestEngineStatus.Uninitialized);
            }
        }

        /// <summary>
        /// Adds a new build request to the request queue.
        /// </summary>
        /// <param name="request">The request to be added.</param>
        /// <remarks>
        /// Called by the Node.  Non-overlapping with other calls from the Node.
        /// </remarks>
        public void SubmitBuildRequest(BuildRequest request)
        {
            QueueAction(
                () =>
                {
                    ErrorUtilities.VerifyThrow(_status != BuildRequestEngineStatus.Shutdown && _status != BuildRequestEngineStatus.Uninitialized, "Engine loop not yet started, status is {0}.", _status);
                    TraceEngine("Request {0}({1}) (nr {2}) received and activated.", request.GlobalRequestId, request.ConfigurationId, request.NodeRequestId);

                    ErrorUtilities.VerifyThrow(!_requestsByGlobalRequestId.ContainsKey(request.GlobalRequestId), "Request {0} is already known to the engine.", request.GlobalRequestId);
                    ErrorUtilities.VerifyThrow(_configCache.HasConfiguration(request.ConfigurationId), "Request {0} refers to configuration {1} which is not known to the engine.", request.GlobalRequestId, request.ConfigurationId);

                    if (request.NodeRequestId == BuildRequest.ResultsTransferNodeRequestId)
                    {
                        // Grab the results from the requested configuration
                        IResultsCache cache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
                        BuildResult result = cache.GetResultsForConfiguration(request.ConfigurationId);
                        BuildResult resultToReport = new BuildResult(request, result, null);
                        BuildRequestConfiguration config = ((IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache))[request.ConfigurationId];

                        // Retrieve the config if it has been cached, since this would contain our instance data.  It is safe to do this outside of a lock
                        // since only one thread can run in the BuildRequestEngine at a time, and it is EvaluateRequestStates which would cause us to
                        // cache configurations if we are running out of memory.
                        config.RetrieveFromCache();
                        ((IBuildResults)resultToReport).SavedCurrentDirectory = config.SavedCurrentDirectory;
                        ((IBuildResults)resultToReport).SavedEnvironmentVariables = config.SavedEnvironmentVariables;
                        if (!request.BuildRequestDataFlags.HasFlag(BuildRequestDataFlags.IgnoreExistingProjectState))
                        {
                            resultToReport.ProjectStateAfterBuild = config.Project;
                        }

                        TraceEngine("Request {0}({1}) (nr {2}) retrieved results for configuration {3} from node {4} for transfer.", request.GlobalRequestId, request.ConfigurationId, request.NodeRequestId, request.ConfigurationId, _componentHost.BuildParameters.NodeId);

                        // If this is the inproc node, we've already set the configuration's ResultsNodeId to the correct value in
                        // HandleRequestBlockedOnResultsTransfer, and don't want to set it again, because we actually have less
                        // information available to us now.
                        //
                        // On the other hand, if this is not the inproc node, we want to make sure that our copy of this configuration
                        // knows that its results are no longer on this node.  Since we don't know enough here to know where the
                        // results are going, we satisfy ourselves with marking that they are simply "not here".
                        if (_componentHost.BuildParameters.NodeId != Scheduler.InProcNodeId)
                        {
                            config.ResultsNodeId = Scheduler.ResultsTransferredId;
                        }

                        RaiseRequestComplete(request, resultToReport);
                    }
                    else
                    {
                        BuildRequestEntry entry = new BuildRequestEntry(request, _configCache[request.ConfigurationId]);

                        entry.OnStateChanged += BuildRequestEntry_StateChanged;

                        _requests.Add(entry);
                        _requestsByGlobalRequestId[request.GlobalRequestId] = entry;
                        ActivateBuildRequest(entry);
                        EvaluateRequestStates();
                    }
                },
                isLastTask: false);
        }

        /// <summary>
        /// Reports a build result to the engine, allowing it to satisfy outstanding requests.  This result
        /// is reported to each entry, allowing it the opportunity to determine for itself if the
        /// result applies.
        /// </summary>
        /// <param name="unblocker">Information needed to unblock the engine.</param>
        /// <remarks>
        /// Called by the Node.  Non-overlapping with other calls from the Node.
        /// </remarks>
        public void UnblockBuildRequest(BuildRequestUnblocker unblocker)
        {
            QueueAction(
                () =>
                {
                    ErrorUtilities.VerifyThrow(_status != BuildRequestEngineStatus.Shutdown && _status != BuildRequestEngineStatus.Uninitialized, "Engine loop not yet started, status is {0}.", _status);
                    ErrorUtilities.VerifyThrow(_requestsByGlobalRequestId.ContainsKey(unblocker.BlockedRequestId), "Request {0} is not known to the engine.", unblocker.BlockedRequestId);
                    BuildRequestEntry entry = _requestsByGlobalRequestId[unblocker.BlockedRequestId];

                    // Are we resuming execution or reporting results?
                    if (unblocker.Result == null)
                    {
                        // We are resuming execution.
                        TraceEngine("Request {0}({1}) (nr {2}) is now proceeding from current state {3}.", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId, entry.State);

                        // UNDONE: (Refactor) This is a bit icky because we still have the concept of blocking on an in-progress request
                        // versus blocking on requests waiting for results.  They come to the same thing, and its been rationalized correctly in
                        // the scheduler, but we should remove the dichotomy in the BuildRequestEntry so that that entry directly tracks in the same
                        // way as the SchedulableRequest does.  Alternately, it could just not track at all, and assume that when the scheduler tells it
                        // to resume it is able to do so (it has no other way of knowing anyhow.)
                        if (entry.State == BuildRequestEntryState.Waiting)
                        {
                            entry.Unblock();
                        }

                        ActivateBuildRequest(entry);
                    }
                    else
                    {
                        // We must be reporting results.
                        BuildResult result = unblocker.Result;

                        if (result.NodeRequestId == BuildRequest.ResultsTransferNodeRequestId)
                        {
                            TraceEngine("Request {0}({1}) (nr {2}) has retrieved the results for configuration {3} and cached them on node {4} (UBR).", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId, entry.Request.ConfigurationId, _componentHost.BuildParameters.NodeId);

                            IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
                            IConfigCache configCache = (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache);
                            BuildRequestConfiguration config = configCache[result.ConfigurationId];

                            config.RetrieveFromCache();
                            config.SavedEnvironmentVariables = ((IBuildResults)result).SavedEnvironmentVariables;
                            config.SavedCurrentDirectory = ((IBuildResults)result).SavedCurrentDirectory;
                            config.ApplyTransferredState(result.ProjectStateAfterBuild);

                            // Don't need them anymore on the result since they were just piggybacking to get accross the wire.
                            ((IBuildResults)result).SavedEnvironmentVariables = null;
                            ((IBuildResults)result).SavedCurrentDirectory = null;

                            // Our results node is now this node, since we've just cached those results
                            resultsCache.AddResult(result);
                            config.ResultsNodeId = _componentHost.BuildParameters.NodeId;

                            entry.Unblock();
                            ActivateBuildRequest(entry);
                        }
                        else
                        {
                            TraceEngine("Request {0}({1}) (nr {2}) is no longer waiting on nr {3} (UBR).  Results are {4}.", entry.Request.GlobalRequestId, entry.Request.ConfigurationId, entry.Request.NodeRequestId, result.NodeRequestId, result.OverallResult);

                            // Update the configuration with targets information, if we received any and didn't already have it.
                            if (result.DefaultTargets != null)
                            {
                                BuildRequestConfiguration configuration = _configCache[result.ConfigurationId];
                                if (configuration.ProjectDefaultTargets == null)
                                {
                                    configuration.ProjectDefaultTargets = result.DefaultTargets;
                                    configuration.ProjectInitialTargets = result.InitialTargets;
                                }
                            }

                            entry.ReportResult(result);
                        }
                    }
                },
                isLastTask: false);
        }

        /// <summary>
        /// Notifies the engine of a resource response granting the node resources.
        /// </summary>
        /// <param name="response">The resource response.</param>
        public void GrantResources(ResourceResponse response)
        {
            QueueAction(
                () =>
                {
                    BuildRequestEntry entry = _requestsByGlobalRequestId[response.GlobalRequestId];
                    entry.Builder.ContinueRequestWithResources(response);
                },
                isLastTask: false);
        }

        /// <summary>
        /// Reports a configuration response to the request, allowing it to satisfy outstanding requests.
        /// <seealso cref="BuildRequestConfigurationResponse"/>
        /// </summary>
        /// <param name="response">The configuration response.</param>
        /// <remarks>
        /// Called by the Node.  Non-overlapping with other calls from the Node.
        /// </remarks>
        public void ReportConfigurationResponse(BuildRequestConfigurationResponse response)
        {
            QueueAction(
                () =>
                {
                    ErrorUtilities.VerifyThrow(_status != BuildRequestEngineStatus.Shutdown && _status != BuildRequestEngineStatus.Uninitialized, "Engine loop not yet started, status is {0}.", _status);

                    TraceEngine("Received configuration response for node config {0}, now global config {1}.", response.NodeConfigurationId, response.GlobalConfigurationId);
                    ErrorUtilities.VerifyThrow(_componentHost != null, "No host object set");

                    // Remove the unresolved configuration entry from the unresolved cache.
                    BuildRequestConfiguration config = _unresolvedConfigurations[response.NodeConfigurationId];
                    _unresolvedConfigurations.RemoveConfiguration(response.NodeConfigurationId);

                    // Add the configuration to the resolved cache unless it already exists there.  This will be
                    // the case in single-proc mode as we share the global cache with the Build Manager.
                    IConfigCache globalConfigurations = (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache);
                    if (!globalConfigurations.HasConfiguration(response.GlobalConfigurationId))
                    {
                        config.ConfigurationId = response.GlobalConfigurationId;
                        config.ResultsNodeId = response.ResultsNodeId;
                        globalConfigurations.AddConfiguration(config);
                    }

                    // Evaluate the current list of requests and tell any waiting requests about our new configuration update.
                    // If any requests can now issue build requests, do so.
                    IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);

                    var blockersToIssue = new List<BuildRequestBlocker>();
                    foreach (BuildRequestEntry currentEntry in _requests)
                    {
                        var requestsToIssue = new List<BuildRequest>();
                        if (currentEntry.State == BuildRequestEntryState.Waiting)
                        {
                            // Resolve the configuration id and get the list of requests to be issued, if any.
                            bool issueRequests = currentEntry.ResolveConfigurationRequest(response.NodeConfigurationId, response.GlobalConfigurationId);

                            // If we had any requests which are now ready to be issued, do so.
                            if (issueRequests)
                            {
                                IEnumerable<BuildRequest> resolvedRequests = currentEntry.GetRequestsToIssueIfReady();
                                foreach (BuildRequest request in resolvedRequests)
                                {
                                    // If we have results already in the cache for this request, give them to the
                                    // entry now.
                                    var cacheResponse = resultsCache.SatisfyRequest(
                                        request: request,
                                        configInitialTargets: config.ProjectInitialTargets,
                                        configDefaultTargets: config.ProjectDefaultTargets,
                                        skippedResultsDoNotCauseCacheMiss: _componentHost.BuildParameters.SkippedResultsDoNotCauseCacheMiss());

                                    if (cacheResponse.Type == ResultsCacheResponseType.Satisfied)
                                    {
                                        // We have a result, give it back to this request.
                                        currentEntry.ReportResult(cacheResponse.Results);

                                        TraceEngine(
                                            "Request {0} (node request {1}) with targets ({2}) satisfied from cache",
                                            request.GlobalRequestId,
                                            request.NodeRequestId,
                                            string.Join(";", request.Targets));
                                    }
                                    else
                                    {
                                        requestsToIssue.Add(request);
                                    }
                                }
                            }
                        }

                        if (requestsToIssue.Count != 0)
                        {
                            BuildRequestBlocker blocker = new BuildRequestBlocker(currentEntry.Request.GlobalRequestId, currentEntry.GetActiveTargets(), requestsToIssue.ToArray());
                            blockersToIssue.Add(blocker);
                        }
                    }

                    // Issue all of the outstanding build requests.
                    foreach (BuildRequestBlocker blocker in blockersToIssue)
                    {
                        // Issue the build request
                        IssueBuildRequest(blocker);
                    }
                },
                isLastTask: false);
        }

        #endregion

        #region IBuildComponent Members

        /// <summary>
        /// Sets the build component host for this object.
        /// </summary>
        /// <param name="host">The host.</param>
        public void InitializeComponent(IBuildComponentHost host)
        {
            ErrorUtilities.VerifyThrowArgumentNull(host, nameof(host));
            ErrorUtilities.VerifyThrow(_componentHost == null, "BuildRequestEngine already initialized!");
            _componentHost = host;
            _configCache = (IConfigCache)host.GetComponent(BuildComponentType.ConfigCache);

            // Create a local configuration cache which is used to temporarily hold configurations which don't have
            // proper IDs yet.  We don't get this from the global config cache because that singleton shouldn't be polluted
            // with our temporaries.
            // NOTE: Because we don't get this from the component host, we cannot override it.
            ConfigCache unresolvedConfigCache = new ConfigCache();
            unresolvedConfigCache.InitializeComponent(host);
            _unresolvedConfigurations = unresolvedConfigCache;
        }

        /// <summary>
        /// Called to terminate the functions of this component
        /// </summary>
        public void ShutdownComponent()
        {
            ErrorUtilities.VerifyThrow(_status == BuildRequestEngineStatus.Uninitialized, "Cleanup wasn't called, status is {0}", _status);
            _componentHost = null;

            ChangeStatus(BuildRequestEngineStatus.Shutdown);
        }

        #endregion

        /// <summary>
        /// Class factory for component creation.
        /// </summary>
        internal static IBuildComponent CreateComponent(BuildComponentType type)
        {
            ErrorUtilities.VerifyThrow(type == BuildComponentType.RequestEngine, "Cannot create component of type {0}", type);
            return new BuildRequestEngine();
        }

        /// <summary>
        /// Called when a build request entry has a state change.  We should re-evaluate our requests when this happens.
        /// </summary>
        /// <param name="entry">The entry raising the event.</param>
        /// <param name="newState">The event's new state.</param>
        private void BuildRequestEntry_StateChanged(BuildRequestEntry entry, BuildRequestEntryState newState)
        {
            QueueAction(() => { EvaluateRequestStates(); }, isLastTask: false);
        }

        #region RaiseEvents

        /// <summary>
        /// Raises the OnRequestComplete event.
        /// </summary>
        /// <param name="request">The request which completed.</param>
        /// <param name="result">The result for the request</param>
        private void RaiseRequestComplete(BuildRequest request, BuildResult result)
        {
            RequestCompleteDelegate requestComplete = OnRequestComplete;
            if (requestComplete != null)
            {
                TraceEngine("RRC: Reporting result for request {0}({1}) (nr {2}).", request.GlobalRequestId, request.ConfigurationId, request.NodeRequestId);
                requestComplete(request, result);
            }
        }

        /// <summary>
        /// Raises the OnRequestResumed event.
        /// </summary>
        /// <param name="request">The request being resumed.</param>
        private void RaiseRequestResumed(BuildRequest request)
        {
            OnRequestResumed?.Invoke(request);
        }

        /// <summary>
        /// Raises the OnEngineException event.
        /// </summary>
        /// <param name="e">The exception being thrown.</param>
        private void RaiseEngineException(Exception e)
        {
            OnEngineException?.Invoke(e);
        }

        /// <summary>
        /// Raises the OnNewRequest event.
        /// </summary>
        /// <param name="blocker">Information about what is blocking the current request.</param>
        private void RaiseRequestBlocked(BuildRequestBlocker blocker)
        {
            OnRequestBlocked?.Invoke(blocker);
        }

        /// <summary>
        /// Raises the OnStatusChanged event.
        /// </summary>
        /// <param name="newStatus">The new engine status.</param>
        private void RaiseEngineStatusChanged(BuildRequestEngineStatus newStatus)
        {
            OnStatusChanged?.Invoke(newStatus);
        }

        /// <summary>
        /// Raises the OnNewConfigurationRequest event.
        /// </summary>
        /// <param name="config">The configuration which needs resolving.</param>
        private void RaiseNewConfigurationRequest(BuildRequestConfiguration config)
        {
            OnNewConfigurationRequest?.Invoke(config);
        }

        #endregion

        /// <summary>
        /// Changes the engine's status and raises the OnStatsChanged event.
        /// </summary>
        /// <param name="newStatus">The new engine status.</param>
        private void ChangeStatus(BuildRequestEngineStatus newStatus)
        {
            if (_status != newStatus)
            {
                _status = newStatus;
                RaiseEngineStatusChanged(newStatus);
            }
        }

        /// <summary>
        /// This method examines the current list of requests to determine if any requests should change
        /// state, possibly reactivating a previously inactive request or removing a now-completed
        /// request from the list.
        /// </summary>
        private void EvaluateRequestStates()
        {
            BuildRequestEntry activeEntry = null;
            BuildRequestEntry firstReadyEntry = null;
            int waitingRequests = 0;
            var completedEntries = new List<BuildRequestEntry>();

            foreach (BuildRequestEntry currentEntry in _requests)
            {
                switch (currentEntry.State)
                {
                    // This request is currently being built
                    case BuildRequestEntryState.Active:
                        ErrorUtilities.VerifyThrow(activeEntry == null, "Multiple active requests");
                        activeEntry = currentEntry;
                        TraceEngine("ERS: Active request is now {0}({1}) (nr {2}).", currentEntry.Request.GlobalRequestId, currentEntry.Request.ConfigurationId, currentEntry.Request.NodeRequestId);
                        break;

                    // This request is now complete.
                    case BuildRequestEntryState.Complete:
                        completedEntries.Add(currentEntry);
                        TraceEngine("ERS: Request {0}({1}) (nr {2}) is marked as complete.", currentEntry.Request.GlobalRequestId, currentEntry.Request.ConfigurationId, currentEntry.Request.NodeRequestId);
                        break;

                    // This request is waiting for configurations or results
                    case BuildRequestEntryState.Waiting:
                        waitingRequests++;
                        break;

                    // This request is ready to be built
                    case BuildRequestEntryState.Ready:
                        if (firstReadyEntry == null)
                        {
                            firstReadyEntry = currentEntry;
                        }

                        break;

                    default:
                        ErrorUtilities.ThrowInternalError("Unexpected BuildRequestEntry state " + currentEntry.State);
                        break;
                }
            }

            // Remove completed requests
            foreach (BuildRequestEntry completedEntry in completedEntries)
            {
                TraceEngine("ERS: Request {0}({1}) (nr {2}) is being removed from the requests list.", completedEntry.Request.GlobalRequestId, completedEntry.Request.ConfigurationId, completedEntry.Request.NodeRequestId);
                _requests.Remove(completedEntry);
                _requestsByGlobalRequestId.Remove(completedEntry.Request.GlobalRequestId);
            }

            // If we completed a request, that means we may be able to unload the configuration if there is memory pressure.  Further we
            // will also cache any result items we can find since they are rarely used.
            if (completedEntries.Count > 0)
            {
                CheckMemoryUsage();
            }

            // Update current engine status and start the next request, if applicable.
            if (activeEntry == null)
            {
                if (firstReadyEntry != null)
                {
                    // We are now active because we have an entry which is building.
                    ChangeStatus(BuildRequestEngineStatus.Active);
                }
                else
                {
                    ChangeStatus(
                        waitingRequests == 0 ? BuildRequestEngineStatus.Idle : BuildRequestEngineStatus.Waiting);
                }
            }
            else
            {
                ChangeStatus(BuildRequestEngineStatus.Active);
            }

            // Finally, raise the completed events so they occur AFTER the state of the engine has changed,
            // otherwise the client might observe the engine as being active after having received
            // completed notifications for all requests, which would be odd.
            foreach (BuildRequestEntry completedEntry in completedEntries)
            {
                // Shut it down because we already have enough in reserve.
                completedEntry.Builder.OnNewBuildRequests -= Builder_OnNewBuildRequests;
                completedEntry.Builder.OnBuildRequestBlocked -= Builder_OnBlockedRequest;
                completedEntry.Builder.OnResourceRequest -= Builder_OnResourceRequest;
                ((IBuildComponent)completedEntry.Builder).ShutdownComponent();

                BuildRequestConfiguration configuration = _configCache[completedEntry.Request.ConfigurationId];

                // Update the default targets.  Note that if the project failed to load, Project will be null so we can't do this.
                if (configuration.IsLoaded)
                {
                    // Now update this result so that the Build Manager can correctly match results from its
                    // own cache.
                    completedEntry.Result.DefaultTargets = configuration.ProjectDefaultTargets;
                    completedEntry.Result.InitialTargets = configuration.ProjectInitialTargets;
                    completedEntry.Result.ProjectTargets = configuration.ProjectTargets;
                }

                TraceEngine("ERS: Request is now {0}({1}) (nr {2}) has had its builder cleaned up.", completedEntry.Request.GlobalRequestId, completedEntry.Request.ConfigurationId, completedEntry.Request.NodeRequestId);
                RaiseRequestComplete(completedEntry.Request, completedEntry.Result);
            }
        }

        /// <summary>
        /// Check the amount of memory we are using and, if we exceed the threshold, unload cacheable items.
        /// </summary>
        /// <remarks>
        /// Since this causes synchronous I/O and a stop-the-world GC, it can be very expensive. If
        /// something other than build results is taking up the bulk of the memory space, it may not
        /// free any space. That's caused customer reports of VS hangs resulting from build requests
        /// that are very slow because something in VS is taking all of the memory, but every
        /// project build is slowed down by this codepath. To mitigate this, don't perform these
        /// checks in devenv.exe. On the command line, 32-bit MSBuild may still need to cache build
        /// results on very large builds, but build results are much more likely to be the bulk of
        /// memory usage there.
        /// </remarks>
        [SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect", Justification = "We're trying to get rid of memory because we're running low, so we need to collect NOW in order to free it up ASAP")]
        private void CheckMemoryUsage()
        {
            if (!NativeMethodsShared.IsWindows || BuildEnvironmentHelper.Instance.RunningInVisualStudio)
            {
                // Since this causes synchronous I/O and a stop-the-world GC, it can be very expensive. If
                // something other than build results is taking up the bulk of the memory space, it may not
                // free any space. That's caused customer reports of VS hangs resulting from build requests
                // that are very slow because something in VS is taking all of the memory, but every
                // project build is slowed down by this codepath. To mitigate this, don't perform these
                // checks in devenv.exe. On the command line, 32-bit MSBuild may still need to cache build
                // results on very large builds, but build results are much more likely to be the bulk of
                // memory usage there.
                return;
            }

            // Jeffrey Richter suggests that when the memory load in the system exceeds 80% it is a good
            // idea to start finding ways to unload unnecessary data to prevent memory starvation.  We use this metric in
            // our calculations below.
            NativeMethodsShared.MemoryStatus memoryStatus = NativeMethodsShared.GetMemoryStatus();
            if (memoryStatus != null)
            {
                try
                {
                    // The minimum limit must be no more than 80% of the virtual memory limit to reduce the chances of a single unfortunately
                    // large project resulting in allocations which exceed available VM space between calls to this function.  This situation
                    // is more likely on 32-bit machines where VM space is only 2 gigs.
                    ulong memoryUseLimit = Convert.ToUInt64(memoryStatus.TotalVirtual * 0.8);

                    // See how much memory we are using and compart that to our limit.
                    ulong memoryInUse = memoryStatus.TotalVirtual - memoryStatus.AvailableVirtual;
                    while ((memoryInUse > memoryUseLimit) || _debugForceCaching)
                    {
                        TraceEngine(
                            "Memory usage at {0}, limit is {1}.  Caching configurations and results cache and collecting.",
                            memoryInUse,
                            memoryUseLimit);
                        IResultsCache resultsCache =
                            _componentHost.GetComponent(BuildComponentType.ResultsCache) as IResultsCache;

                        resultsCache.WriteResultsToDisk();
                        if (_configCache.WriteConfigurationsToDisk())
                        {
                            // We have to collect here because WriteConfigurationsToDisk only collects 10% of the configurations.  It is entirely possible
                            // that those 10% don't constitute enough collected memory to reduce our usage below the threshold.  The only way to know is to
                            // force the collection then re-test the memory usage.  We repeat until we have reduced our use below the threshold or
                            // we failed to write any more configurations to disk.
                            GC.Collect();
                        }
                        else
                        {
                            break;
                        }

                        memoryStatus = NativeMethodsShared.GetMemoryStatus();
                        memoryInUse = memoryStatus.TotalVirtual - memoryStatus.AvailableVirtual;
                        TraceEngine("Memory usage now at {0}", memoryInUse);
                    }
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    _nodeLoggingContext.LogFatalBuildError(
                        e,
                        new BuildEventFileInfo(Construction.ElementLocation.EmptyLocation));
                    throw new BuildAbortedException(e.Message, e);
                }
            }
        }

        /// <summary>
        /// Makes the specified build request entry the active one, loading the project if necessary.
        /// </summary>
        /// <param name="entry">The entry to activate.</param>
        private void ActivateBuildRequest(BuildRequestEntry entry)
        {
            ErrorUtilities.VerifyThrow(_componentHost != null, "No host object set");

            entry.RequestConfiguration.RetrieveFromCache();

            // First, determine if we have a loaded project for this entry
            if (entry.Builder == null)
            {
                // This is the first time this project has been activated.
                // Set the request builder.
                entry.Builder = GetRequestBuilder();

                // Now call into the request builder to do the building
                entry.Builder.BuildRequest(_nodeLoggingContext, entry);
            }
            else
            {
                // We are resuming the build request
                entry.Builder.ContinueRequest();
            }

            RaiseRequestResumed(entry.Request);
        }

        /// <summary>
        /// Returns an unused request builder if there are any, or creates a new one.
        /// </summary>
        /// <returns>An IRequestBuilder to use.</returns>
        private IRequestBuilder GetRequestBuilder()
        {
            IRequestBuilder builder = (IRequestBuilder)_componentHost.GetComponent(BuildComponentType.RequestBuilder);

            // NOTE: We do NOT need to register for the OnBuildRequestCompleted because we already watch the BuildRequestEntry
            // state changes.
            builder.OnNewBuildRequests += Builder_OnNewBuildRequests;
            builder.OnBuildRequestBlocked += Builder_OnBlockedRequest;
            builder.OnResourceRequest += Builder_OnResourceRequest;

            return builder;
        }

        /// <summary>
        /// Starts to terminate any builder associated with the entry and clean it up in preparation for removal.
        /// </summary>
        /// <param name="entry">The entry to be deactivated</param>
        private static void BeginDeactivateBuildRequest(BuildRequestEntry entry)
        {
            if (entry.Builder != null)
            {
                entry.BeginCancel();
            }
        }

        /// <summary>
        /// Waits for the builders until they are terminated.
        /// </summary>
        /// <param name="entry">The entry to be deactivated</param>
        private static void WaitForDeactivateCompletion(BuildRequestEntry entry)
        {
            if (entry.Builder != null)
            {
                entry.WaitForCancelCompletion();
            }
        }

        #region RequestBuilder Event Handlers

        /// <summary>
        /// Raised when the active request needs to build new requests.
        /// </summary>
        /// <param name="issuingEntry">The request issuing the requests.</param>
        /// <param name="newRequests">The requests being issued.</param>
        /// <remarks>Called by the RequestBuilder (implicitly through an event).  Non-overlapping with other RequestBuilders.</remarks>
        private void Builder_OnNewBuildRequests(BuildRequestEntry issuingEntry, FullyQualifiedBuildRequest[] newRequests)
        {
            QueueAction(
                () =>
                {
                    _unsubmittedRequests.Enqueue(new PendingUnsubmittedBuildRequests(issuingEntry, newRequests));
                    IssueUnsubmittedRequests();
                    EvaluateRequestStates();
                },
                isLastTask: false);
        }

        /// <summary>
        /// Called when the request builder needs to block on another request.
        /// </summary>
        /// <remarks>
        /// Called by the RequestBuilder (implicitly through an event).  Non-overlapping with other RequestBuilders.</remarks>
        private void Builder_OnBlockedRequest(BuildRequestEntry issuingEntry, int blockingGlobalRequestId, string blockingTarget, BuildResult partialBuildResult = null)
        {
            QueueAction(
                () =>
                {
                    _unsubmittedRequests.Enqueue(new PendingUnsubmittedBuildRequests(issuingEntry, blockingGlobalRequestId, blockingTarget, partialBuildResult));
                    IssueUnsubmittedRequests();
                    EvaluateRequestStates();
                },
                isLastTask: false);
        }

        /// <summary>
        /// Called when the request builder needs to request resources.
        /// </summary>
        private void Builder_OnResourceRequest(ResourceRequest request)
        {
            OnResourceRequest?.Invoke(request);
        }

        #endregion

        /// <summary>
        /// Dequeue some requests from the unsubmitted request queue and submit them.
        /// </summary>
        private void IssueUnsubmittedRequests()
        {
            // We will only submit as many items as were in the queue at the time this method was called.
            // This prevents us from a) having to lock the queue for the whole loop or b) getting into
            // an endless loop where another thread pushes requests into the queue as fast as we can
            // discharge them.
            int countToSubmit = _unsubmittedRequests.Count;
            while (countToSubmit != 0)
            {
                PendingUnsubmittedBuildRequests unsubmittedRequest = _unsubmittedRequests.Dequeue();

                BuildRequestEntry issuingEntry = unsubmittedRequest.IssuingEntry;

                if (unsubmittedRequest.BlockingGlobalRequestId == issuingEntry.Request.GlobalRequestId)
                {
                    if (unsubmittedRequest.BlockingTarget == null)
                    {
                        // We are yielding
                        IssueBuildRequest(new BuildRequestBlocker(issuingEntry.Request.GlobalRequestId, issuingEntry.GetActiveTargets(), YieldAction.Yield));
                        lock (issuingEntry.GlobalLock)
                        {
                            issuingEntry.WaitForBlockingRequest(issuingEntry.Request.GlobalRequestId);
                        }
                    }
                    else
                    {
                        // We are ready to continue
                        IssueBuildRequest(new BuildRequestBlocker(issuingEntry.Request.GlobalRequestId, issuingEntry.GetActiveTargets(), YieldAction.Reacquire));
                    }
                }
                else if (unsubmittedRequest.BlockingGlobalRequestId == BuildRequest.InvalidGlobalRequestId)
                {
                    if (unsubmittedRequest.NewRequests != null)
                    {
                        // We aren't blocked on another request, we are blocked on new requests
                        IssueBuildRequests(issuingEntry, unsubmittedRequest.NewRequests);
                    }
                    else
                    {
                        // We are blocked waiting for our results to transfer
                        lock (issuingEntry.GlobalLock)
                        {
                            issuingEntry.WaitForBlockingRequest(issuingEntry.Request.GlobalRequestId);
                        }

                        IssueBuildRequest(new BuildRequestBlocker(issuingEntry.Request.GlobalRequestId));
                    }
                }
                else
                {
                    // We are blocked on an existing build request.
                    lock (issuingEntry.GlobalLock)
                    {
                        issuingEntry.WaitForBlockingRequest(unsubmittedRequest.BlockingGlobalRequestId);
                    }

                    IssueBuildRequest(new BuildRequestBlocker(issuingEntry.Request.GlobalRequestId, issuingEntry.GetActiveTargets(), unsubmittedRequest.BlockingGlobalRequestId, unsubmittedRequest.BlockingTarget, unsubmittedRequest.PartialBuildResult));
                }

                countToSubmit--;
            }
        }

        /// <summary>
        /// This method is responsible for evaluating whether we have enough information to make the request of the Build Manager,
        /// or if we need to obtain additional configuration information.  It then issues either configuration
        /// requests or build requests, or both as needed.
        /// </summary>
        /// <param name="issuingEntry">The BuildRequestEntry which is making the request</param>
        /// <param name="newRequests">The array of "child" build requests to be issued.</param>
        /// <remarks>
        /// When we receive a build request, we first have to determine if we already have a configuration which matches the
        /// one used by the request.  We do this because everywhere we deal with requests and results beyond this function, we
        /// use configuration ids, which are assigned once by the Build Manager and are global to the system.  If we do
        /// not have a global configuration id, we can't check to see if we already have build results for the request, so we
        /// cannot send the request out.  Thus, first we determine the configuration id.
        ///
        /// Assuming we don't have the global configuration id locally, we will send the configuration to the Build Manager.
        /// It will look up or assign the global configuration id and send it back to us.
        ///
        /// Once we have the global configuration id, we can then look up results locally.  If we have enough results to fulfill
        /// the request, we give them back to the request, otherwise we have to forward the request to the Build Mangager
        /// for scheduling.
        /// </remarks>
        private void IssueBuildRequests(BuildRequestEntry issuingEntry, FullyQualifiedBuildRequest[] newRequests)
        {
            ErrorUtilities.VerifyThrow(_componentHost != null, "No host object set");

            // For each request, we need to determine if we have a local configuration in the
            // configuration cache.  If we do, we can issue the build request immediately.
            // Otherwise, we need to ask the Build Manager for configuration IDs and issue those requests
            // later.
            IConfigCache globalConfigCache = (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache);

            // We are going to potentially issue several requests.  We don't want the state of the request being modified by
            // other threads while this occurs, so we lock the request for the duration.  This lock is the same lock
            // used by the BuildRequestEntry itself to lock each of its data-modifying methods, effectively preventing
            // any other thread from modifying the BuildRequestEntry while we hold it.  This mechanism also means that it
            // is not necessary for other threads to take the global lock explicitly if they are just doing single operations
            // to the entry rather than a series of them.
            lock (issuingEntry.GlobalLock)
            {
                var existingResultsToReport = new List<BuildResult>();
                var unresolvedConfigurationsAdded = new HashSet<int>();

                foreach (FullyQualifiedBuildRequest request in newRequests)
                {
                    // Do we have a matching configuration?
                    BuildRequestConfiguration matchingConfig = globalConfigCache.GetMatchingConfiguration(request.Config);
                    BuildRequest newRequest;

                    BuildRequestDataFlags buildRequestDataFlags = request.BuildRequestDataFlags;

                    if (issuingEntry.Request.BuildRequestDataFlags.HasFlag(BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports))
                    {
                        // If the issuing build requested to ignore missing, empty, and invalid imports, this entry should also
                        buildRequestDataFlags |= BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports;
                    }

                    if (matchingConfig == null)
                    {
                        // No configuration locally, are we already waiting for it?
                        matchingConfig = _unresolvedConfigurations.GetMatchingConfiguration(request.Config);
                        if (matchingConfig == null)
                        {
                            // Not waiting for it
                            request.Config.ConfigurationId = GetNextUnresolvedConfigurationId();
                            _unresolvedConfigurations.AddConfiguration(request.Config);
                            unresolvedConfigurationsAdded.Add(request.Config.ConfigurationId);
                        }
                        else
                        {
                            request.Config.ConfigurationId = matchingConfig.ConfigurationId;
                        }

                        // Whether we are already waiting for a configuration or we need to wait for another one
                        // we will add this request as waiting for a configuration.  As new configuration resolutions
                        // come in, we will check our requests which are waiting for configurations move them to
                        // waiting for results.  It is important that we tell the issuing request to wait for a result
                        // prior to issuing any necessary configuration request so that we don't get into a state where
                        // we receive the configuration response before we enter the wait state.
                        newRequest = new BuildRequest(
                            submissionId: issuingEntry.Request.SubmissionId,
                            nodeRequestId: GetNextBuildRequestId(),
                            configurationId: request.Config.ConfigurationId,
                            escapedTargets: request.Targets,
                            hostServices: issuingEntry.Request.HostServices,
                            parentBuildEventContext: issuingEntry.Request.CurrentTaskContext ?? issuingEntry.Request.BuildEventContext,
                            parentRequest: issuingEntry.Request,
                            buildRequestDataFlags: buildRequestDataFlags,
                            requestedProjectState: null,
                            skipStaticGraphIsolationConstraints: request.SkipStaticGraphIsolationConstraints);

                        issuingEntry.WaitForResult(newRequest);

                        if (matchingConfig == null)
                        {
                            // Issue the config resolution request
                            TraceEngine(
                                "Request {0}({1}) (nr {2}) is waiting on configuration {3} (IBR)",
                                issuingEntry.Request.GlobalRequestId,
                                issuingEntry.Request.ConfigurationId,
                                issuingEntry.Request.NodeRequestId,
                                request.Config.ConfigurationId);
                            issuingEntry.WaitForConfiguration(request.Config);
                        }
                    }
                    else
                    {
                        // We have a configuration, see if we already have results locally.
                        newRequest = new BuildRequest(
                            submissionId: issuingEntry.Request.SubmissionId,
                            nodeRequestId: GetNextBuildRequestId(),
                            configurationId: matchingConfig.ConfigurationId,
                            escapedTargets: request.Targets,
                            hostServices: issuingEntry.Request.HostServices,
                            parentBuildEventContext: issuingEntry.Request.CurrentTaskContext ?? issuingEntry.Request.BuildEventContext,
                            parentRequest: issuingEntry.Request,
                            buildRequestDataFlags: buildRequestDataFlags,
                            requestedProjectState: null,
                            skipStaticGraphIsolationConstraints: request.SkipStaticGraphIsolationConstraints);

                        IResultsCache resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);

                        var response = resultsCache.SatisfyRequest(
                            request: newRequest,
                            configInitialTargets: matchingConfig.ProjectInitialTargets,
                            configDefaultTargets: matchingConfig.ProjectDefaultTargets,
                            skippedResultsDoNotCauseCacheMiss: _componentHost.BuildParameters.SkippedResultsDoNotCauseCacheMiss());

                        if (response.Type == ResultsCacheResponseType.Satisfied)
                        {
                            // We have a result, give it back to this request.
                            issuingEntry.WaitForResult(newRequest);

                            // Log the fact that we handled this from the cache.
                            _nodeLoggingContext.LogRequestHandledFromCache(newRequest, _configCache[newRequest.ConfigurationId], response.Results);

                            TraceEngine(
                                "Request {0} (node request {1}) with targets ({2}) satisfied from cache",
                                newRequest.GlobalRequestId,
                                newRequest.NodeRequestId,
                                string.Join(",", request.Targets));

                            // Can't report the result directly here, because that could cause the request to go from
                            // Waiting to Ready.
                            existingResultsToReport.Add(response.Results);
                        }
                        else
                        {
                            // No result, to wait for it.
                            issuingEntry.WaitForResult(newRequest);
                        }
                    }
                }

                // If we have any results we had to report, do so now.
                foreach (BuildResult existingResult in existingResultsToReport)
                {
                    issuingEntry.ReportResult(existingResult);
                }

                // Issue any configuration requests we may still need.
                List<BuildRequestConfiguration> unresolvedConfigurationsToIssue = issuingEntry.GetUnresolvedConfigurationsToIssue();
                if (unresolvedConfigurationsToIssue != null)
                {
                    foreach (BuildRequestConfiguration unresolvedConfigurationToIssue in unresolvedConfigurationsToIssue)
                    {
                        unresolvedConfigurationsAdded.Remove(unresolvedConfigurationToIssue.ConfigurationId);
                        IssueConfigurationRequest(unresolvedConfigurationToIssue);
                    }
                }

                // Remove any configurations we ended up not waiting for, otherwise future requests will think we are still waiting for them
                // and will never get submitted.
                foreach (int unresolvedConfigurationId in unresolvedConfigurationsAdded)
                {
                    _unresolvedConfigurations.RemoveConfiguration(unresolvedConfigurationId);
                }

                // Finally, if we can issue build requests, do so.
                List<BuildRequest> requestsToIssue = issuingEntry.GetRequestsToIssueIfReady();
                if (requestsToIssue != null)
                {
                    BuildRequestBlocker blocker = new BuildRequestBlocker(issuingEntry.Request.GlobalRequestId, issuingEntry.GetActiveTargets(), requestsToIssue.ToArray());
                    IssueBuildRequest(blocker);
                }

                if (issuingEntry.State == BuildRequestEntryState.Ready)
                {
                    ErrorUtilities.VerifyThrow((requestsToIssue == null) || (requestsToIssue.Count == 0), "Entry shouldn't be ready if we also issued requests.");
                    ActivateBuildRequest(issuingEntry);
                }
            }
        }

        /// <summary>
        /// Retrieves a new configuration ID
        /// </summary>
        /// <returns>The next unused local configuration ID.</returns>
        private int GetNextUnresolvedConfigurationId()
        {
            unchecked
            {
                do
                {
                    _nextUnresolvedConfigurationId--;
                    if (_nextUnresolvedConfigurationId >= 0)
                    {
                        _nextUnresolvedConfigurationId = StartingUnresolvedConfigId;
                    }
                }
                while (_unresolvedConfigurations.HasConfiguration(_nextUnresolvedConfigurationId));
            }

            return _nextUnresolvedConfigurationId;
        }

        /// <summary>
        /// Retrieves a new build request ID
        /// </summary>
        /// <returns>The next build request ID.</returns>
        private int GetNextBuildRequestId()
        {
            unchecked
            {
                _nextBuildRequestId++;
                if (_nextBuildRequestId < 0)
                {
                    _nextBuildRequestId = StartingBuildRequestId;
                }
            }

            return _nextBuildRequestId;
        }

        /// <summary>
        /// This method forms a configuration request from an unresolved configuration and posts it to the
        /// Build Manager.
        /// </summary>
        /// <param name="config">The configuration to be mapped.</param>
        private void IssueConfigurationRequest(BuildRequestConfiguration config)
        {
            ErrorUtilities.VerifyThrow(config.WasGeneratedByNode, "InvalidConfigurationId");
            ErrorUtilities.VerifyThrowArgumentNull(config, nameof(config));
            ErrorUtilities.VerifyThrow(_unresolvedConfigurations.HasConfiguration(config.ConfigurationId), "NoUnresolvedConfiguration");
            TraceEngine("Issuing configuration request for node config {0}", config.ConfigurationId);
            RaiseNewConfigurationRequest(config);
        }

        /// <summary>
        /// Sends a build request to the Build Manager for scheduling
        /// </summary>
        /// <param name="blocker">The information about why the request is blocked.</param>
        private void IssueBuildRequest(BuildRequestBlocker blocker)
        {
            ErrorUtilities.VerifyThrowArgumentNull(blocker, nameof(blocker));

            if (blocker.BuildRequests == null)
            {
                // This is the case when we aren't blocking on new requests, but rather an in-progress request which is executing a target for which we need results.
                TraceEngine("Blocking global request {0} on global request {1} because it is already executing target {2}", blocker.BlockedRequestId, blocker.BlockingRequestId, blocker.BlockingTarget);
            }
            else
            {
                foreach (BuildRequest blockingRequest in blocker.BuildRequests)
                {
                    TraceEngine("Sending node request {0} (configuration {1}) with parent {2} to Build Manager", blockingRequest.NodeRequestId, blockingRequest.ConfigurationId, blocker.BlockedRequestId);
                }
            }

            RaiseRequestBlocked(blocker);
        }

        /// <summary>
        /// Queue an action to be run in the engine.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="isLastTask"><code>true</code> if this is the last task for this queue, otherwise <code>false</code>.</param>
        /// <remarks>This method will return false if an attempt is made to schedule an action after the queue has been shut down.</remarks>
        private void QueueAction(Action action, bool isLastTask)
        {
            ActionBlock<Action> queue = _workQueue;
            if (queue != null)
            {
                lock (queue)
                {
                    queue.Post(
                        () =>
                        {
                            try
                            {
                                action.Invoke();
                            }
                            catch (Exception e)
                            {
                                TraceEngine("EL: EXCEPTION caught in engine: {0} - {1}", e.GetType(), e.Message);

                                // Dump all engine exceptions to a temp file
                                // so that we have something to go on in the
                                // event of a failure
                                ExceptionHandling.DumpExceptionToFile(e);

                                // Raise the exception to the host, so that it can signal termination of the build.
                                RaiseEngineException(e);

                                TraceEngine("EL: Deactivating requests due to exception.");

                                // Let the critical ones melt down the system.
                                if (ExceptionHandling.IsCriticalException(e))
                                {
                                    ErrorUtilities.ThrowInternalError(e.Message, e);
                                }

                                // This is fatal to the execution of the ActionBlock.  No more messages will be processed, and the
                                // build will be terminated.
                                throw;
                            }
                        });

                    if (isLastTask)
                    {
                        // No more tasks will be allowed to post to this queue.
                        queue.Complete();
                    }
                }
            }
        }

        /// <summary>
        /// Method used for debugging purposes.
        /// </summary>
        private void TraceEngine(string format, params object[] stuff)
        {
            if (_debugDumpState)
            {
                lock (this)
                {
                    FileUtilities.EnsureDirectoryExists(_debugDumpPath);

                    using (StreamWriter file = FileUtilities.OpenWrite(string.Format(CultureInfo.CurrentCulture, Path.Combine(_debugDumpPath, @"EngineTrace_{0}.txt"), EnvironmentUtilities.CurrentProcessId), append: true))
                    {
                        string message = String.Format(CultureInfo.CurrentCulture, format, stuff);
                        file.WriteLine("{0}({1})-{2}: {3}", Thread.CurrentThread.Name, Thread.CurrentThread.ManagedThreadId, DateTime.UtcNow.Ticks, message);
                        file.Flush();
                    }
                }
            }
        }

        /// <summary>
        /// Struct used to contain information about requests submitted by the RequestBuilder.
        /// </summary>
        private struct PendingUnsubmittedBuildRequests
        {
            public BuildResult PartialBuildResult { get; }

            /// <summary>
            /// The global request id on which we are blocking
            /// </summary>
            public readonly int BlockingGlobalRequestId;

            /// <summary>
            /// The target on which we are blocking
            /// </summary>
            public readonly string BlockingTarget;

            /// <summary>
            /// The issuing request
            /// </summary>
            public readonly BuildRequestEntry IssuingEntry;

            /// <summary>
            /// The new requests to issue
            /// </summary>
            public readonly FullyQualifiedBuildRequest[] NewRequests;

            /// <summary>
            /// Create a new unsubmitted request entry
            /// </summary>
            /// <param name="issuingEntry">The build request originating these requests.</param>
            /// <param name="newRequests">The new requests to be issued.</param>
            public PendingUnsubmittedBuildRequests(BuildRequestEntry issuingEntry, FullyQualifiedBuildRequest[] newRequests)
            {
                IssuingEntry = issuingEntry;
                NewRequests = newRequests;
                BlockingGlobalRequestId = BuildRequest.InvalidGlobalRequestId;
                BlockingTarget = null;
                PartialBuildResult = null;
            }

            /// <summary>
            /// Create a new unsubmitted request entry
            /// </summary>
            /// <param name="issuingEntry">The build request originating these requests.</param>
            /// <param name="blockingGlobalRequestId">The request on which we are blocked.</param>
            /// <param name="blockingTarget">The target on which we are blocked.</param>
            private PendingUnsubmittedBuildRequests(BuildRequestEntry issuingEntry, int blockingGlobalRequestId, string blockingTarget)
            {
                IssuingEntry = issuingEntry;
                NewRequests = null;
                BlockingGlobalRequestId = blockingGlobalRequestId;
                BlockingTarget = blockingTarget;
                PartialBuildResult = null;
            }

            public PendingUnsubmittedBuildRequests(BuildRequestEntry issuingEntry, int blockingGlobalRequestId, string blockingTarget, BuildResult partialBuildResult)
                : this(issuingEntry, blockingGlobalRequestId, blockingTarget)
            {
                PartialBuildResult = partialBuildResult;
            }
        }
    }
}
