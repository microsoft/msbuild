// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.Debugging;
using BuildAbortedException = Microsoft.Build.Exceptions.BuildAbortedException;
using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;
using NodeLoggingContext = Microsoft.Build.BackEnd.Logging.NodeLoggingContext;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// The MSBuild Scheduler
    /// </summary>
    internal class Scheduler : IScheduler
    {
        /// <summary>
        /// The invalid node id
        /// </summary>
        internal const int InvalidNodeId = -1;

        /// <summary>
        /// ID used to indicate that the results for a particular configuration may at one point
        /// have resided on this node, but currently do not and will need to be transferred back
        /// in order to be used.
        /// </summary>
        internal const int ResultsTransferredId = -2;

        /// <summary>
        /// The in-proc node id
        /// </summary>
        internal const int InProcNodeId = 1;

        /// <summary>
        /// The virtual node, used when a request is initially given to the scheduler.
        /// </summary>
        internal const int VirtualNode = 0;

        /// <summary>
        /// If MSBUILDCUSTOMSCHEDULER = CustomSchedulerForSQL, the default multiplier for the amount by which
        /// the count of configurations on any one node can exceed the average configuration count is 1.1 --
        /// + 10%.
        /// </summary>
        private const double DefaultCustomSchedulerForSQLConfigurationLimitMultiplier = 1.1;

        #region Scheduler Data

        /// <summary>
        /// Content of the environment variable  MSBUILDSCHEDULINGUNLIMITED
        /// </summary>
        private string _schedulingUnlimitedVariable;

        /// <summary>
        /// If MSBUILDSCHEDULINGUNLIMITED is set, this flag will make AtSchedulingLimit() always return false
        /// </summary>
        private bool _schedulingUnlimited;

        /// <summary>
        /// If MSBUILDNODELIMITOFFSET is set, this will add an offset to the limit used in AtSchedulingLimit()
        /// </summary>
        private int _nodeLimitOffset;

        /// <summary>
        /// The result of calling NativeMethodsShared.GetLogicalCoreCount() unless overriden with MSBUILDCORELIMIT.
        /// </summary>
        private int _coreLimit;

        /// <summary>
        /// The weight of busy nodes in GetAvailableCoresForExplicitRequests().
        /// </summary>
        private int _nodeCoreAllocationWeight;

        /// <summary>
        /// { nodeId -> NodeInfo }
        /// A list of nodes we know about.  For the non-distributed case, there will be no more nodes than the
        /// maximum specified on the command-line.
        /// </summary>
        private Dictionary<int, NodeInfo> _availableNodes;

        /// <summary>
        /// The number of inproc nodes that can be created without hitting the
        /// node limit.
        /// </summary>
        private int _currentInProcNodeCount = 0;

        /// <summary>
        /// The number of out-of-proc nodes that can be created without hitting the
        /// node limit.
        /// </summary>
        private int _currentOutOfProcNodeCount = 0;

        /// <summary>
        /// The collection of all requests currently known to the system.
        /// </summary>
        private SchedulingData _schedulingData;

        /// <summary>
        /// A queue of RequestCores requests waiting for at least one core to become available.
        /// </summary>
        private Queue<TaskCompletionSource<int>> _pendingRequestCoresCallbacks;

        #endregion

        /// <summary>
        /// The component host.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// The configuration cache.
        /// </summary>
        private IConfigCache _configCache;

        /// <summary>
        /// The results cache.
        /// </summary>
        private IResultsCache _resultsCache;

        /// <summary>
        ///  The next ID to assign for a global request id.
        /// </summary>
        private int _nextGlobalRequestId;

        /// <summary>
        /// Flag indicating that we are supposed to dump the scheduler state to the disk periodically.
        /// </summary>
        private bool _debugDumpState;

        /// <summary>
        /// Flag used for debugging by forcing all scheduling to go out-of-proc.
        /// </summary>
        internal bool ForceAffinityOutOfProc
            => Traits.Instance.InProcNodeDisabled || _componentHost.BuildParameters.DisableInProcNode;

        /// <summary>
        /// The path into which debug files will be written.
        /// </summary>
        private string _debugDumpPath;

        /// <summary>
        /// If MSBUILDCUSTOMSCHEDULER = CustomSchedulerForSQL, the user may also choose to set
        /// MSBUILDCUSTOMSCHEDULERFORSQLCONFIGURATIONLIMITMULTIPLIER to the value by which they want
        /// the max configuration count for any one node to exceed the average configuration count.
        /// If that env var is not set, or is set to an invalid value (negative, less than 1, non-numeric)
        /// then we use the default value instead.
        /// </summary>
        private double _customSchedulerForSQLConfigurationLimitMultiplier;

        /// <summary>
        /// The plan.
        /// </summary>
        private SchedulingPlan _schedulingPlan;

        /// <summary>
        /// If MSBUILDCUSTOMSCHEDULER is set, contains the requested scheduling algorithm
        /// </summary>
        private AssignUnscheduledRequestsDelegate _customRequestSchedulingAlgorithm;

        private NodeLoggingContext _inprocNodeContext;

        private int _loggedWarningsForProxyBuildsOnOutOfProcNodes = 0;

        /// <summary>
        /// Constructor.
        /// </summary>
        public Scheduler()
        {
            // Be careful moving these to Traits, changing the timing of reading environment variables has a breaking potential.
            _debugDumpState = Traits.Instance.DebugScheduler;
            _debugDumpPath = DebugUtils.DebugPath;
            _schedulingUnlimitedVariable = Environment.GetEnvironmentVariable("MSBUILDSCHEDULINGUNLIMITED");
            _nodeLimitOffset = 0;

            if (!String.IsNullOrEmpty(_schedulingUnlimitedVariable))
            {
                _schedulingUnlimited = true;
            }
            else
            {
                _schedulingUnlimited = false;

                string strNodeLimitOffset = Environment.GetEnvironmentVariable("MSBUILDNODELIMITOFFSET");
                if (!String.IsNullOrEmpty(strNodeLimitOffset))
                {
                    _nodeLimitOffset = Int16.Parse(strNodeLimitOffset, CultureInfo.InvariantCulture);

                    if (_nodeLimitOffset < 0)
                    {
                        _nodeLimitOffset = 0;
                    }
                }
            }

            // Resource management tuning knobs:
            // 1) MSBUILDCORELIMIT is the maximum number of cores we hand out via IBuildEngine9.RequestCores.
            //    Note that it is independent of build parallelism as given by /m on the command line.
            if (!int.TryParse(Environment.GetEnvironmentVariable("MSBUILDCORELIMIT"), out _coreLimit) || _coreLimit <= 0)
            {
                _coreLimit = NativeMethodsShared.GetLogicalCoreCount();
            }
            // 1) MSBUILDNODECOREALLOCATIONWEIGHT is the weight with which executing nodes reduce the number of available cores.
            //    Example: If the weight is 50, _coreLimit is 8, and there are 4 nodes that are busy executing build requests,
            //    then the number of cores available via IBuildEngine9.RequestCores is 8 - (0.5 * 4) = 6.
            if (!int.TryParse(Environment.GetEnvironmentVariable("MSBUILDNODECOREALLOCATIONWEIGHT"), out _nodeCoreAllocationWeight)
                || _nodeCoreAllocationWeight <= 0
                || _nodeCoreAllocationWeight > 100)
            {
                _nodeCoreAllocationWeight = 0;
            }

            if (String.IsNullOrEmpty(_debugDumpPath))
            {
                _debugDumpPath = FileUtilities.TempFileDirectory;
            }

            Reset();
        }

        #region Delegates

        /// <summary>
        /// In the circumstance where we want to specify the scheduling algorithm via the secret environment variable
        /// MSBUILDCUSTOMSCHEDULING, the scheduling algorithm used will be assigned to a delegate of this type.
        /// </summary>
        internal delegate void AssignUnscheduledRequestsDelegate(List<ScheduleResponse> responses, HashSet<int> idleNodes);

        #endregion

        #region IScheduler Members

        /// <summary>
        /// Retrieves the minimum configuration id which can be assigned that won't conflict with those in the scheduling plan.
        /// </summary>
        public int MinimumAssignableConfigurationId
        {
            get
            {
                if (_schedulingPlan == null)
                {
                    return 1;
                }

                return _schedulingPlan.MaximumConfigurationId + 1;
            }
        }

        /// <summary>
        /// Returns true if the specified configuration is currently in the scheduler.
        /// </summary>
        /// <param name="configurationId">The configuration id</param>
        /// <returns>True if the specified configuration is already building.</returns>
        public bool IsCurrentlyBuildingConfiguration(int configurationId)
        {
            return _schedulingData.GetRequestsAssignedToConfigurationCount(configurationId) > 0;
        }

        /// <summary>
        /// Gets a configuration id from the plan which matches the specified path.
        /// </summary>
        /// <param name="configPath">The path.</param>
        /// <returns>The configuration id which has been assigned to this path.</returns>
        public int GetConfigurationIdFromPlan(string configPath)
        {
            if (_schedulingPlan == null)
            {
                return BuildRequestConfiguration.InvalidConfigurationId;
            }

            return _schedulingPlan.GetConfigIdForPath(configPath);
        }

        /// <summary>
        /// Retrieves the request executing on a node.
        /// </summary>
        public BuildRequest GetExecutingRequestByNode(int nodeId)
        {
            if (!_schedulingData.IsNodeWorking(nodeId))
            {
                return null;
            }

            SchedulableRequest request = _schedulingData.GetExecutingRequestByNode(nodeId);
            return request.BuildRequest;
        }

        /// <summary>
        /// Reports that the specified request has become blocked and cannot proceed.
        /// </summary>
        public IEnumerable<ScheduleResponse> ReportRequestBlocked(int nodeId, BuildRequestBlocker blocker)
        {
            _schedulingData.EventTime = DateTime.UtcNow;
            List<ScheduleResponse> responses = new List<ScheduleResponse>();

            // Get the parent, if any
            SchedulableRequest parentRequest = null;
            if (blocker.BlockedRequestId != BuildRequest.InvalidGlobalRequestId)
            {
                if (blocker.YieldAction == YieldAction.Reacquire)
                {
                    parentRequest = _schedulingData.GetYieldingRequest(blocker.BlockedRequestId);
                }
                else
                {
                    parentRequest = _schedulingData.GetExecutingRequest(blocker.BlockedRequestId);
                }
            }

            try
            {
                // We are blocked either on new requests (top-level or MSBuild task) or on an in-progress request that is
                // building a target we want to build.
                if (blocker.YieldAction != YieldAction.None)
                {
                    TraceScheduler("Request {0} on node {1} is performing yield action {2}.", blocker.BlockedRequestId, nodeId, blocker.YieldAction);
                    ErrorUtilities.VerifyThrow(string.IsNullOrEmpty(blocker.BlockingTarget), "Blocking target should be null because this is not a request blocking on a target");
                    HandleYieldAction(parentRequest, blocker);
                }
                else if ((blocker.BlockingRequestId == blocker.BlockedRequestId) && blocker.BlockingRequestId != BuildRequest.InvalidGlobalRequestId)
                {
                    ErrorUtilities.VerifyThrow(string.IsNullOrEmpty(blocker.BlockingTarget), "Blocking target should be null because this is not a request blocking on a target");
                    // We are blocked waiting for a transfer of results.
                    HandleRequestBlockedOnResultsTransfer(parentRequest, responses);
                }
                else if (blocker.BlockingRequestId != BuildRequest.InvalidGlobalRequestId)
                {
                    // We are blocked by a request executing a target for which we need results.
                    try
                    {
                        ErrorUtilities.VerifyThrow(!string.IsNullOrEmpty(blocker.BlockingTarget), "Blocking target should exist");

                        HandleRequestBlockedOnInProgressTarget(parentRequest, blocker);
                    }
                    catch (SchedulerCircularDependencyException ex)
                    {
                        TraceScheduler("Circular dependency caused by request {0}({1}) (nr {2}), parent {3}({4}) (nr {5})", ex.Request.GlobalRequestId, ex.Request.ConfigurationId, ex.Request.NodeRequestId, parentRequest.BuildRequest.GlobalRequestId, parentRequest.BuildRequest.ConfigurationId, parentRequest.BuildRequest.NodeRequestId);
                        responses.Add(ScheduleResponse.CreateCircularDependencyResponse(nodeId, parentRequest.BuildRequest, ex.Request));
                    }
                }
                else
                {
                    ErrorUtilities.VerifyThrow(string.IsNullOrEmpty(blocker.BlockingTarget), "Blocking target should be null because this is not a request blocking on a target");
                    // We are blocked by new requests, either top-level or MSBuild task.
                    HandleRequestBlockedByNewRequests(parentRequest, blocker, responses);
                }
            }
            catch (SchedulerCircularDependencyException ex)
            {
                TraceScheduler("Circular dependency caused by request {0}({1}) (nr {2}), parent {3}({4}) (nr {5})", ex.Request.GlobalRequestId, ex.Request.ConfigurationId, ex.Request.NodeRequestId, parentRequest.BuildRequest.GlobalRequestId, parentRequest.BuildRequest.ConfigurationId, parentRequest.BuildRequest.NodeRequestId);
                responses.Add(ScheduleResponse.CreateCircularDependencyResponse(nodeId, parentRequest.BuildRequest, ex.Request));
            }

            // Now see if we can schedule requests somewhere since we
            // a) have a new request; and
            // b) have a node which is now waiting and not doing anything.
            ScheduleUnassignedRequests(responses);
            return responses;
        }

        /// <summary>
        /// Informs the scheduler of a specific result.
        /// </summary>
        public IEnumerable<ScheduleResponse> ReportResult(int nodeId, BuildResult result)
        {
            _schedulingData.EventTime = DateTime.UtcNow;
            List<ScheduleResponse> responses = new List<ScheduleResponse>();
            TraceScheduler("Reporting result from node {0} for request {1}, parent {2}.", nodeId, result.GlobalRequestId, result.ParentGlobalRequestId);
            RecordResultToCurrentCacheIfConfigNotInOverrideCache(result);

            if (result.NodeRequestId == BuildRequest.ResultsTransferNodeRequestId)
            {
                // We are transferring results.  The node to which they should be sent has already been recorded by the
                // HandleRequestBlockedOnResultsTransfer method in the configuration.
                BuildRequestConfiguration config = _configCache[result.ConfigurationId];
                ScheduleResponse response = ScheduleResponse.CreateReportResultResponse(config.ResultsNodeId, result);
                responses.Add(response);
            }
            else
            {
                // Tell the request to which this result belongs than it is done.
                SchedulableRequest request = _schedulingData.GetExecutingRequest(result.GlobalRequestId);
                request.Complete(result);

                // Report results to our parent, or report submission complete as necessary.
                if (request.Parent != null)
                {
                    // responses.Add(new ScheduleResponse(request.Parent.AssignedNode, new BuildRequestUnblocker(request.Parent.BuildRequest.GlobalRequestId, result)));
                    ErrorUtilities.VerifyThrow(result.ParentGlobalRequestId == request.Parent.BuildRequest.GlobalRequestId, "Result's parent doesn't match request's parent.");

                    // When adding the result to the cache we merge the result with what ever is already in the cache this may cause
                    // the result to have more target outputs in it than was was requested.  To fix this we can ask the cache itself for the result we just added.
                    // When results are returned from the cache we filter them based on the targets we requested. This causes our result to only
                    // include the targets we requested rather than the merged result.

                    // Note: In this case we do not need to log that we got the results from the cache because we are only using the cache
                    // for filtering the targets for the result instead rather than using the cache as the location where this result came from.
                    ScheduleResponse response = TrySatisfyRequestFromCache(request.Parent.AssignedNode, request.BuildRequest, skippedResultsDoNotCauseCacheMiss: _componentHost.BuildParameters.SkippedResultsDoNotCauseCacheMiss());

                    // response may be null if the result was never added to the cache. This can happen if the result has an exception in it
                    // or the results could not be satisfied because the initial or default targets have been skipped. If that is the case
                    // we need to report the result directly since it contains an exception
                    if (response == null)
                    {
                        response = ScheduleResponse.CreateReportResultResponse(request.Parent.AssignedNode, result.Clone());
                    }

                    responses.Add(response);
                }
                else
                {
                    // This was root request, we can report submission complete.
                    // responses.Add(new ScheduleResponse(result));
                    responses.Add(ScheduleResponse.CreateSubmissionCompleteResponse(result));
                    if (result.OverallResult != BuildResultCode.Failure)
                    {
                        WriteSchedulingPlan(result.SubmissionId);
                    }
                }

                // This result may apply to a number of other unscheduled requests which are blocking active requests.  Report to them as well.
                List<SchedulableRequest> unscheduledRequests = new List<SchedulableRequest>(_schedulingData.UnscheduledRequests);
                foreach (SchedulableRequest unscheduledRequest in unscheduledRequests)
                {
                    if (unscheduledRequest.BuildRequest.GlobalRequestId == result.GlobalRequestId)
                    {
                        TraceScheduler("Request {0} (node request {1}) also satisfied by result.", unscheduledRequest.BuildRequest.GlobalRequestId, unscheduledRequest.BuildRequest.NodeRequestId);
                        BuildResult newResult = new BuildResult(unscheduledRequest.BuildRequest, result, null);

                        // Report results to the parent.
                        int parentNode = (unscheduledRequest.Parent == null) ? InvalidNodeId : unscheduledRequest.Parent.AssignedNode;

                        // There are other requests which we can satisfy based on this result, lets pull the result out of the cache
                        // and satisfy those requests.  Normally a skipped result would lead to the cache refusing to satisfy the
                        // request, because the correct response in that case would be to attempt to rebuild the target in case there
                        // are state changes that would cause it to now excute.  At this point, however, we already know that the parent
                        // request has completed, and we already know that this request has the same global request ID, which means that
                        // its configuration and set of targets are identical -- from MSBuild's perspective, it's the same.  So since
                        // we're not going to attempt to re-execute it, if there are skipped targets in the result, that's fine. We just
                        // need to know what the target results are so that we can log them.
                        ScheduleResponse response = TrySatisfyRequestFromCache(parentNode, unscheduledRequest.BuildRequest, skippedResultsDoNotCauseCacheMiss: true);

                        // If we have a response we need to tell the loggers that we satisified that request from the cache.
                        if (response != null)
                        {
                            LogRequestHandledFromCache(unscheduledRequest.BuildRequest, response.BuildResult);
                        }
                        else
                        {
                            // Response may be null if the result was never added to the cache. This can happen if the result has
                            // an exception in it. If that is the case, we should report the result directly so that the
                            // build manager knows that it needs to shut down logging manually.
                            response = GetResponseForResult(parentNode, unscheduledRequest.BuildRequest, newResult.Clone());
                        }

                        responses.Add(response);

                        // Mark the request as complete (and the parent is no longer blocked by this request.)
                        unscheduledRequest.Complete(newResult);
                    }
                }
            }

            // This node may now be free, so run the scheduler.
            ScheduleUnassignedRequests(responses);
            return responses;
        }

        /// <summary>
        /// Signals that a node has been created.
        /// </summary>
        /// <param name="nodeInfos">Information about the nodes which were created.</param>
        /// <returns>A new set of scheduling actions to take.</returns>
        public IEnumerable<ScheduleResponse> ReportNodesCreated(IEnumerable<NodeInfo> nodeInfos)
        {
            _schedulingData.EventTime = DateTime.UtcNow;

            foreach (NodeInfo nodeInfo in nodeInfos)
            {
                _availableNodes[nodeInfo.NodeId] = nodeInfo;
                TraceScheduler("Node {0} created", nodeInfo.NodeId);

                switch (nodeInfo.ProviderType)
                {
                    case NodeProviderType.InProc:
                        _currentInProcNodeCount++;
                        break;
                    case NodeProviderType.OutOfProc:
                        _currentOutOfProcNodeCount++;
                        break;
                    case NodeProviderType.Remote:
                    default:
                        // this should never happen in the current MSBuild.
                        ErrorUtilities.ThrowInternalErrorUnreachable();
                        break;
                }
            }

            List<ScheduleResponse> responses = new List<ScheduleResponse>();
            ScheduleUnassignedRequests(responses);
            return responses;
        }

        /// <summary>
        /// Signals that the build has been aborted by the specified node.
        /// </summary>
        /// <param name="nodeId">The node which reported the failure.</param>
        public void ReportBuildAborted(int nodeId)
        {
            _schedulingData.EventTime = DateTime.UtcNow;

            // Get the list of build requests currently assigned to the node and report aborted results for them.
            TraceScheduler("Build aborted by node {0}", nodeId);

            foreach (SchedulableRequest request in _schedulingData.GetScheduledRequestsByNode(nodeId))
            {
                MarkRequestAborted(request);
            }
        }

        /// <summary>
        /// Resets the scheduler.
        /// </summary>
        public void Reset()
        {
            DumpConfigurations();
            DumpRequests();
            _schedulingPlan = null;
            _schedulingData = new SchedulingData();
            _availableNodes = new Dictionary<int, NodeInfo>(8);
            _pendingRequestCoresCallbacks = new Queue<TaskCompletionSource<int>>();
            _currentInProcNodeCount = 0;
            _currentOutOfProcNodeCount = 0;

            _nextGlobalRequestId = 0;
            _customRequestSchedulingAlgorithm = null;
        }

        /// <summary>
        /// Writes out the detailed summary of the build.
        /// </summary>
        /// <param name="submissionId">The id of the submission which is at the root of the build.</param>
        public void WriteDetailedSummary(int submissionId)
        {
            ILoggingService loggingService = _componentHost.LoggingService;
            BuildEventContext context = new BuildEventContext(submissionId, 0, 0, 0, 0, 0);
            loggingService.LogComment(context, MessageImportance.Normal, "DetailedSummaryHeader");

            foreach (SchedulableRequest request in _schedulingData.GetRequestsByHierarchy(null))
            {
                if (request.BuildRequest.SubmissionId == submissionId)
                {
                    loggingService.LogComment(context, MessageImportance.Normal, "BuildHierarchyHeader");
                    WriteRecursiveSummary(loggingService, context, submissionId, request, 0, false /* useConfigurations */, true /* isLastChild */);
                }
            }

            WriteNodeUtilizationGraph(loggingService, context, false /* useConfigurations */);
        }

        /// <summary>
        /// Requests CPU resources.
        /// </summary>
        public Task<int> RequestCores(int requestId, int requestedCores, bool waitForCores)
        {
            if (requestedCores == 0)
            {
                return Task.FromResult(0);
            }

            Func<int, int> grantCores = (int availableCores) =>
            {
                int grantedCores = Math.Min(requestedCores, availableCores);
                if (grantedCores > 0)
                {
                    _schedulingData.GrantCoresToRequest(requestId, grantedCores);
                }
                return grantedCores;
            };

            int grantedCores = grantCores(GetAvailableCoresForExplicitRequests());
            if (grantedCores > 0 || !waitForCores)
            {
                return Task.FromResult(grantedCores);
            }
            else
            {
                // We have no cores to grant at the moment, queue up the request.
                TaskCompletionSource<int> completionSource = new TaskCompletionSource<int>();
                _pendingRequestCoresCallbacks.Enqueue(completionSource);
                return completionSource.Task.ContinueWith((Task<int> task) => grantCores(task.Result), TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        /// <summary>
        /// Returns CPU resources.
        /// </summary>
        public List<ScheduleResponse> ReleaseCores(int requestId, int coresToRelease)
        {
            _schedulingData.RemoveCoresFromRequest(requestId, coresToRelease);

            // Releasing cores means that we may be able to schedule more work.
            List<ScheduleResponse> responses = new List<ScheduleResponse>();
            ScheduleUnassignedRequests(responses);
            return responses;
        }

        #endregion

        #region IBuildComponent Members

        /// <summary>
        /// Initializes the component with the specified component host.
        /// </summary>
        /// <param name="host">The component host.</param>
        public void InitializeComponent(IBuildComponentHost host)
        {
            _componentHost = host;
            _resultsCache = (IResultsCache)_componentHost.GetComponent(BuildComponentType.ResultsCache);
            _configCache = (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache);
            _inprocNodeContext = new NodeLoggingContext(_componentHost.LoggingService, InProcNodeId, true);
        }

        /// <summary>
        /// Shuts down the component.
        /// </summary>
        public void ShutdownComponent()
        {
            Reset();
        }

        #endregion

        /// <summary>
        /// Factory for component construction.
        /// </summary>
        internal static IBuildComponent CreateComponent(BuildComponentType componentType)
        {
            ErrorUtilities.VerifyThrow(componentType == BuildComponentType.Scheduler, "Cannot create components of type {0}", componentType);
            return new Scheduler();
        }

        /// <summary>
        /// Updates the state of a request based on its desire to yield or reacquire control of its node.
        /// </summary>
        private void HandleYieldAction(SchedulableRequest parentRequest, BuildRequestBlocker blocker)
        {
            if (blocker.YieldAction == YieldAction.Yield)
            {
                // Mark the request blocked.
                parentRequest.Yield(blocker.TargetsInProgress);
            }
            else
            {
                // Mark the request ready.
                parentRequest.Reacquire();
            }
        }

        /// <summary>
        /// Attempts to schedule unassigned requests to free nodes.
        /// </summary>
        /// <param name="responses">The list which should be populated with responses from the scheduling.</param>
        private void ScheduleUnassignedRequests(List<ScheduleResponse> responses)
        {
            DateTime schedulingTime = DateTime.UtcNow;

            // See if we are done.  We are done if there are no unassigned requests and no requests assigned to nodes.
            if (_schedulingData.UnscheduledRequestsCount == 0 &&
                _schedulingData.ReadyRequestsCount == 0 &&
                _schedulingData.BlockedRequestsCount == 0)
            {
                if (_schedulingData.ExecutingRequestsCount == 0 && _schedulingData.YieldingRequestsCount == 0)
                {
                    // We are done.
                    TraceScheduler("Build complete");
                }
                else
                {
                    // Nodes still have work, but we have no requests.  Let them proceed and only handle resource requests.
                    HandlePendingResourceRequests();
                    TraceScheduler("{0}: Waiting for existing work to proceed.", schedulingTime);
                }

                return;
            }

            // Resume any work available which has already been assigned to specific nodes.
            ResumeRequiredWork(responses);
            HashSet<int> idleNodes = new HashSet<int>();
            foreach (int availableNodeId in _availableNodes.Keys)
            {
                if (!_schedulingData.IsNodeWorking(availableNodeId))
                {
                    idleNodes.Add(availableNodeId);
                }
            }

            int nodesFreeToDoWorkPriorToScheduling = idleNodes.Count;

            // Assign requests to any nodes which are currently idle.
            if (idleNodes.Count > 0 && _schedulingData.UnscheduledRequestsCount > 0)
            {
                AssignUnscheduledRequestsToNodes(responses, idleNodes);
            }

            // If we have no nodes free to do work, we might need more nodes.  This will occur if:
            // 1) We still have unscheduled requests, because an additional node might allow us to execute those in parallel, or
            // 2) We didn't schedule anything because there were no nodes to schedule to
            bool createNodePending = false;
            if (_schedulingData.UnscheduledRequestsCount > 0 || responses.Count == 0)
            {
                createNodePending = CreateNewNodeIfPossible(responses, _schedulingData.UnscheduledRequests);
            }

            if (_availableNodes.Count > 0)
            {
                // If we failed to schedule any requests, report any results or create any nodes, we might be done.
                if (_schedulingData.ExecutingRequestsCount > 0 || _schedulingData.YieldingRequestsCount > 0)
                {
                    // We are still doing work.
                }
                else if (_schedulingData.UnscheduledRequestsCount == 0 &&
                         _schedulingData.ReadyRequestsCount == 0 &&
                         _schedulingData.BlockedRequestsCount == 0)
                {
                    // We've exhausted our supply of work.
                    TraceScheduler("Build complete");
                }
                else if (_schedulingData.BlockedRequestsCount != 0)
                {
                    // It is legitimate to have blocked requests with none executing if none of the requests can
                    // be serviced by any currently existing node, or if they are blocked by requests, none of
                    // which can be serviced by any currently existing node.  However, in that case, we had better
                    // be requesting the creation of a node that can service them.
                    //
                    // Note: This is O(# nodes * closure of requests blocking current set of blocked requests),
                    // but all three numbers should usually be fairly small and, more importantly, this situation
                    // should occur at most once per build, since it requires a situation where all blocked requests
                    // are blocked on the creation of a node that can service them.
                    foreach (SchedulableRequest request in _schedulingData.BlockedRequests)
                    {
                        if (RequestOrAnyItIsBlockedByCanBeServiced(request))
                        {
                            DumpSchedulerState();
                            ErrorUtilities.ThrowInternalError("Somehow no requests are currently executing, and at least one of the {0} requests blocked by in-progress requests is servicable by a currently existing node, but no circular dependency was detected ...", _schedulingData.BlockedRequestsCount);
                        }
                    }

                    if (!createNodePending)
                    {
                        DumpSchedulerState();
                        ErrorUtilities.ThrowInternalError("None of the {0} blocked requests can be serviced by currently existing nodes, but we aren't requesting a new one.", _schedulingData.BlockedRequestsCount);
                    }
                }
                else if (_schedulingData.ReadyRequestsCount != 0)
                {
                    DumpSchedulerState();
                    ErrorUtilities.ThrowInternalError("Somehow we have {0} requests which are ready to go but we didn't tell the nodes to continue.", _schedulingData.ReadyRequestsCount);
                }
                else if (_schedulingData.UnscheduledRequestsCount != 0 && !createNodePending)
                {
                    DumpSchedulerState();
                    ErrorUtilities.ThrowInternalError("Somehow we have {0} unassigned build requests but {1} of our nodes are free and we aren't requesting a new one...", _schedulingData.UnscheduledRequestsCount, idleNodes.Count);
                }
            }
            else
            {
                ErrorUtilities.VerifyThrow(responses.Count > 0, "We failed to request a node to be created.");
            }

            TraceScheduler("Requests scheduled: {0} Unassigned Requests: {1} Blocked Requests: {2} Unblockable Requests: {3} Free Nodes: {4}/{5} Responses: {6}", nodesFreeToDoWorkPriorToScheduling - idleNodes.Count, _schedulingData.UnscheduledRequestsCount, _schedulingData.BlockedRequestsCount, _schedulingData.ReadyRequestsCount, idleNodes.Count, _availableNodes.Count, responses.Count);
            DumpSchedulerState();
        }

        /// <summary>
        /// Determines which requests to assign to available nodes.
        /// </summary>
        /// <remarks>
        /// This is where all the real scheduling decisions take place.  It should not be necessary to edit functions outside of this
        /// to alter how scheduling occurs.
        /// </remarks>
        private void AssignUnscheduledRequestsToNodes(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            if (_componentHost.BuildParameters.MaxNodeCount == 1)
            {
                // In the single-proc case, there are no decisions to be made.  First-come, first-serve.
                AssignUnscheduledRequestsFIFO(responses, idleNodes);
            }
            else
            {
                bool haveValidPlan = GetSchedulingPlanAndAlgorithm();

                if (_customRequestSchedulingAlgorithm != null)
                {
                    _customRequestSchedulingAlgorithm(responses, idleNodes);
                }
                else
                {
                    // We want to find more work first, and we assign traversals to the in-proc node first, if possible.
                    AssignUnscheduledRequestsByTraversalsFirst(responses, idleNodes);

                    AssignUnscheduledProxyBuildRequestsToInProcNode(responses, idleNodes);

                    if (idleNodes.Count == 0)
                    {
                        return;
                    }

                    if (haveValidPlan)
                    {
                        if (_componentHost.BuildParameters.MaxNodeCount == 2)
                        {
                            AssignUnscheduledRequestsWithPlanByMostImmediateReferences(responses, idleNodes);
                        }
                        else
                        {
                            AssignUnscheduledRequestsWithPlanByGreatestPlanTime(responses, idleNodes);
                        }
                    }
                    else
                    {
                        AssignUnscheduledRequestsWithConfigurationCountLevelling(responses, idleNodes);
                    }
                }
            }
        }

        /// <summary>
        /// Reads in the scheduling plan if one exists and has not previously been read; returns true if the scheduling plan
        /// both exists and is valid, or false otherwise.
        /// </summary>
        private bool GetSchedulingPlanAndAlgorithm()
        {
            // Read the plan, if any.
            if (_schedulingPlan == null)
            {
                _schedulingPlan = new SchedulingPlan(_configCache, _schedulingData);
                ReadSchedulingPlan(_schedulingData.GetRequestsByHierarchy(null).First().BuildRequest.SubmissionId);
            }

            if (_customRequestSchedulingAlgorithm == null)
            {
                string customScheduler = Environment.GetEnvironmentVariable("MSBUILDCUSTOMSCHEDULER");

                if (!String.IsNullOrEmpty(customScheduler))
                {
                    // Assign to the delegate
                    if (customScheduler.Equals("WithPlanByMostImmediateReferences", StringComparison.OrdinalIgnoreCase) && _schedulingPlan.IsPlanValid)
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithPlanByMostImmediateReferences;
                    }
                    else if (customScheduler.Equals("WithPlanByGreatestPlanTime", StringComparison.OrdinalIgnoreCase) && _schedulingPlan.IsPlanValid)
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithPlanByGreatestPlanTime;
                    }
                    else if (customScheduler.Equals("ByTraversalsFirst", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsByTraversalsFirst;
                    }
                    else if (customScheduler.Equals("WithConfigurationCountLevelling", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithConfigurationCountLevelling;
                    }
                    else if (customScheduler.Equals("WithSmallestFileSize", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithSmallestFileSize;
                    }
                    else if (customScheduler.Equals("WithLargestFileSize", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithLargestFileSize;
                    }
                    else if (customScheduler.Equals("WithMaxWaitingRequests", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithMaxWaitingRequests;
                    }
                    else if (customScheduler.Equals("WithMaxWaitingRequests2", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsWithMaxWaitingRequests2;
                    }
                    else if (customScheduler.Equals("FIFO", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsFIFO;
                    }
                    else if (customScheduler.Equals("CustomSchedulerForSQL", StringComparison.OrdinalIgnoreCase))
                    {
                        _customRequestSchedulingAlgorithm = AssignUnscheduledRequestsUsingCustomSchedulerForSQL;

                        string multiplier = Environment.GetEnvironmentVariable("MSBUILDCUSTOMSCHEDULERFORSQLCONFIGURATIONLIMITMULTIPLIER");
                        double convertedMultiplier = 0;
                        if (!Double.TryParse(multiplier, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture.NumberFormat, out convertedMultiplier) || convertedMultiplier < 1)
                        {
                            _customSchedulerForSQLConfigurationLimitMultiplier = DefaultCustomSchedulerForSQLConfigurationLimitMultiplier;
                        }
                        else
                        {
                            _customSchedulerForSQLConfigurationLimitMultiplier = convertedMultiplier;
                        }
                    }
                }
            }

            return _schedulingPlan.IsPlanValid;
        }

        /// <summary>
        /// Assigns requests to nodes based on those which refer to the most other projects.
        /// </summary>
        private void AssignUnscheduledRequestsWithPlanByMostImmediateReferences(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            AssignUnscheduledRequestsWithPlan(responses, idleNodes, (plan1, plan2) => plan1.ReferencesCount < plan2.ReferencesCount);
        }

        /// <summary>
        /// Assigns requests to nodes based on those which have the most plan time.
        /// </summary>
        private void AssignUnscheduledRequestsWithPlanByGreatestPlanTime(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            AssignUnscheduledRequestsWithPlan(responses, idleNodes, (plan1, plan2) => plan1.TotalPlanTime < plan2.TotalPlanTime);
        }

        private void AssignUnscheduledRequestsWithPlan(List<ScheduleResponse> responses, HashSet<int> idleNodes, Func<SchedulingPlan.PlanConfigData, SchedulingPlan.PlanConfigData, bool> comparisonFunction)
        {
            foreach (int idleNodeId in idleNodes)
            {
                SchedulingPlan.PlanConfigData bestConfig = null;
                SchedulableRequest bestRequest = null;

                // Find the most expensive request in the plan to schedule from among the ones available.
                foreach (SchedulableRequest request in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    if (CanScheduleRequestToNode(request, idleNodeId))
                    {
                        SchedulingPlan.PlanConfigData configToConsider = _schedulingPlan.GetConfiguration(request.BuildRequest.ConfigurationId);
                        if (configToConsider is null)
                        {
                            if (bestConfig is null)
                            {
                                // By default we assume configs we don't know about aren't as important, and will only schedule them
                                // if nothing else is suitable
                                bestRequest ??= request;
                            }
                        }
                        else
                        {
                            if (bestConfig is null || comparisonFunction(bestConfig, configToConsider))
                            {
                                bestConfig = configToConsider;
                                bestRequest = request;
                            }
                        }
                    }
                }

                if (bestRequest is not null)
                {
                    AssignUnscheduledRequestToNode(bestRequest, idleNodeId, responses);
                }
            }
        }

        /// <summary>
        /// Assigns requests preferring those which are traversal projects as determined by filename.
        /// </summary>
        private void AssignUnscheduledRequestsByTraversalsFirst(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            AssignUnscheduledRequestsToInProcNode(responses, idleNodes, request => IsTraversalRequest(request.BuildRequest));
        }

        /// <summary>
        /// Proxy build requests <see cref="ProxyTargets"/> should be really cheap (only return properties and items) and it's not worth
        /// paying the IPC cost and re-evaluating them on out of proc nodes (they are guaranteed to be evaluated in the Scheduler process).
        /// </summary>
        private void AssignUnscheduledProxyBuildRequestsToInProcNode(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            AssignUnscheduledRequestsToInProcNode(responses, idleNodes, request => request.IsProxyBuildRequest());
        }

        private void AssignUnscheduledRequestsToInProcNode(List<ScheduleResponse> responses, HashSet<int> idleNodes, Func<SchedulableRequest, bool> shouldBeScheduled)
        {
            if (idleNodes.Contains(InProcNodeId))
            {
                List<SchedulableRequest> unscheduledRequests = new List<SchedulableRequest>(_schedulingData.UnscheduledRequestsWhichCanBeScheduled);
                foreach (SchedulableRequest request in unscheduledRequests)
                {
                    if (CanScheduleRequestToNode(request, InProcNodeId) && shouldBeScheduled(request))
                    {
                        AssignUnscheduledRequestToNode(request, InProcNodeId, responses);
                        idleNodes.Remove(InProcNodeId);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the request is for a traversal project.  Traversals are used to find more work.
        /// </summary>
        private bool IsTraversalRequest(BuildRequest request)
        {
            return _configCache[request.ConfigurationId].IsTraversal;
        }

        /// <summary>
        /// Assigns requests to nodes attempting to ensure each node has the same number of configurations assigned to it.
        /// </summary>
        private void AssignUnscheduledRequestsWithConfigurationCountLevelling(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests but try to keep the same number of configurations on each node
            List<int> nodesByConfigurationCountAscending = new List<int>(_availableNodes.Keys);
            nodesByConfigurationCountAscending.Sort(delegate (int left, int right)
            {
                return Comparer<int>.Default.Compare(_schedulingData.GetConfigurationsCountByNode(left, true /* excludeTraversals */, _configCache), _schedulingData.GetConfigurationsCountByNode(right, true /* excludeTraversals */, _configCache));
            });

            // Assign projects to nodes, preferring to assign work to nodes with the fewest configurations first.
            foreach (int nodeId in nodesByConfigurationCountAscending)
            {
                if (!idleNodes.Contains(nodeId))
                {
                    continue;
                }

                if (AtSchedulingLimit())
                {
                    TraceScheduler("System load limit reached, cannot schedule new work.  Executing: {0} Yielding: {1} Max Count: {2}", _schedulingData.ExecutingRequestsCount, _schedulingData.YieldingRequestsCount, _componentHost.BuildParameters.MaxNodeCount);
                    break;
                }

                foreach (SchedulableRequest request in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    if (CanScheduleRequestToNode(request, nodeId))
                    {
                        AssignUnscheduledRequestToNode(request, nodeId, responses);
                        idleNodes.Remove(nodeId);
                        break;
                    }
                }
            }

            // Now they either all have the same number of configurations of we can no longer assign work.  Let the default scheduling algorithm
            // determine if any more work can be assigned.
            AssignUnscheduledRequestsFIFO(responses, idleNodes);
        }

        /// <summary>
        /// Assigns requests with the smallest file sizes first.
        /// </summary>
        private void AssignUnscheduledRequestsWithSmallestFileSize(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests with the largest file sizes.
            while (idleNodes.Count > 0 && _schedulingData.UnscheduledRequestsCount > 0)
            {
                SchedulableRequest requestWithSmallestSourceFile = null;
                int requestRequiredNodeId = InvalidNodeId;
                long sizeOfSmallestSourceFile = long.MaxValue;

                foreach (SchedulableRequest unscheduledRequest in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    int requiredNodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(unscheduledRequest.BuildRequest.ConfigurationId);
                    if (requiredNodeId == InvalidNodeId || idleNodes.Contains(requiredNodeId))
                    {
                        // Look for a request with the smallest source file
                        System.IO.FileInfo f = new FileInfo(_configCache[unscheduledRequest.BuildRequest.ConfigurationId].ProjectFullPath);
                        if (f.Length < sizeOfSmallestSourceFile)
                        {
                            sizeOfSmallestSourceFile = f.Length;
                            requestWithSmallestSourceFile = unscheduledRequest;
                            requestRequiredNodeId = requiredNodeId;
                        }
                    }
                }

                if (requestWithSmallestSourceFile != null)
                {
                    int nodeIdToAssign = requestRequiredNodeId == InvalidNodeId ? idleNodes.First() : requestRequiredNodeId;
                    AssignUnscheduledRequestToNode(requestWithSmallestSourceFile, nodeIdToAssign, responses);
                    idleNodes.Remove(nodeIdToAssign);
                }
                else
                {
                    // No more requests we can schedule.
                    break;
                }
            }
        }

        /// <summary>
        /// Assigns requests with the largest file sizes first.
        /// </summary>
        private void AssignUnscheduledRequestsWithLargestFileSize(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests with the largest file sizes.
            while (idleNodes.Count > 0 && _schedulingData.UnscheduledRequestsCount > 0)
            {
                SchedulableRequest requestWithLargestSourceFile = null;
                int requestRequiredNodeId = InvalidNodeId;
                long sizeOfLargestSourceFile = 0;

                foreach (SchedulableRequest unscheduledRequest in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    int requiredNodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(unscheduledRequest.BuildRequest.ConfigurationId);
                    if (requiredNodeId == InvalidNodeId || idleNodes.Contains(requiredNodeId))
                    {
                        // Look for a request with the largest source file
                        System.IO.FileInfo f = new FileInfo(_configCache[unscheduledRequest.BuildRequest.ConfigurationId].ProjectFullPath);
                        if (f.Length > sizeOfLargestSourceFile)
                        {
                            sizeOfLargestSourceFile = f.Length;
                            requestWithLargestSourceFile = unscheduledRequest;
                            requestRequiredNodeId = requiredNodeId;
                        }
                    }
                }

                if (requestWithLargestSourceFile != null)
                {
                    int nodeIdToAssign = requestRequiredNodeId == InvalidNodeId ? idleNodes.First() : requestRequiredNodeId;
                    AssignUnscheduledRequestToNode(requestWithLargestSourceFile, nodeIdToAssign, responses);
                    idleNodes.Remove(nodeIdToAssign);
                }
                else
                {
                    // No more requests we can schedule.
                    break;
                }
            }
        }

        /// <summary>
        /// Assigns requests preferring the ones which have the most other requests waiting on them using the transitive closure.
        /// </summary>
        private void AssignUnscheduledRequestsWithMaxWaitingRequests(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests based on how many other requests depend on them
            foreach (int nodeId in idleNodes)
            {
                int maxWaitingRequests = 0;
                SchedulableRequest requestToSchedule = null;
                SchedulableRequest requestToScheduleNoAffinity = null;
                SchedulableRequest requestToScheduleWithAffinity = null;
                foreach (SchedulableRequest currentSchedulableRequest in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    BuildRequest currentRequest = currentSchedulableRequest.BuildRequest;
                    int requiredNodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(currentRequest.ConfigurationId);

                    // This performs the depth-first traversal, assuming that the unassigned build requests has been populated such that the
                    // top-most requests are the ones most recently issued.  We schedule the first request which can be scheduled to this node.
                    if (requiredNodeId == InvalidNodeId || requiredNodeId == nodeId)
                    {
                        // Get the affinity from the request first.
                        NodeAffinity nodeAffinity = GetNodeAffinityForRequest(currentRequest);

                        if (_availableNodes[nodeId].CanServiceRequestWithAffinity(nodeAffinity))
                        {
                            // Get the 'most depended upon' request and schedule that.
                            int requestsWaiting = ComputeClosureOfWaitingRequests(currentSchedulableRequest);
                            bool selectedRequest = false;
                            if (requestsWaiting > maxWaitingRequests)
                            {
                                requestToSchedule = currentSchedulableRequest;
                                maxWaitingRequests = requestsWaiting;
                                selectedRequest = true;
                            }
                            else if (maxWaitingRequests == 0 && requestToSchedule == null)
                            {
                                requestToSchedule = currentSchedulableRequest;
                                selectedRequest = true;
                            }

                            // If we decided this request is a candidate, update the affinity-specific reference
                            // for later.
                            if (selectedRequest)
                            {
                                if (requiredNodeId == InvalidNodeId)
                                {
                                    requestToScheduleNoAffinity = requestToSchedule;
                                }
                                else
                                {
                                    requestToScheduleWithAffinity = requestToSchedule;
                                }
                            }
                        }
                    }
                }

                // Prefer to schedule requests which MUST go on this node instead of those which could go on any node.
                // This helps to prevent us from accumulating tons of request affinities toward a single node.
                if (requestToScheduleWithAffinity != null)
                {
                    requestToSchedule = requestToScheduleWithAffinity;
                }
                else
                {
                    requestToSchedule = requestToScheduleNoAffinity;
                }

                if (requestToSchedule != null)
                {
                    AssignUnscheduledRequestToNode(requestToSchedule, nodeId, responses);
                }
            }
        }

        /// <summary>
        /// Assigns requests preferring those with the most requests waiting on them, but only counting those requests which are
        /// directly waiting, as opposed to the transitive closure.
        /// </summary>
        private void AssignUnscheduledRequestsWithMaxWaitingRequests2(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests based on how many other requests depend on them
            foreach (int nodeId in idleNodes)
            {
                // Find the request with the most waiting requests
                SchedulableRequest mostWaitingRequests = null;
                foreach (SchedulableRequest unscheduledRequest in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    if (CanScheduleRequestToNode(unscheduledRequest, nodeId))
                    {
                        if (mostWaitingRequests == null || unscheduledRequest.RequestsWeAreBlockingCount > mostWaitingRequests.RequestsWeAreBlockingCount)
                        {
                            mostWaitingRequests = unscheduledRequest;
                        }
                    }
                }

                if (mostWaitingRequests != null)
                {
                    AssignUnscheduledRequestToNode(mostWaitingRequests, nodeId, responses);
                }
            }
        }

        /// <summary>
        /// Assigns requests on a first-come, first-serve basis.
        /// </summary>
        private void AssignUnscheduledRequestsFIFO(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // Assign requests on a first-come/first-serve basis
            foreach (int nodeId in idleNodes)
            {
                // Don't overload the system.
                if (AtSchedulingLimit())
                {
                    TraceScheduler("System load limit reached, cannot schedule new work.  Executing: {0} Yielding: {1} Max Count: {2}", _schedulingData.ExecutingRequestsCount, _schedulingData.YieldingRequestsCount, _componentHost.BuildParameters.MaxNodeCount);
                    return;
                }

                foreach (SchedulableRequest unscheduledRequest in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    if (CanScheduleRequestToNode(unscheduledRequest, nodeId))
                    {
                        AssignUnscheduledRequestToNode(unscheduledRequest, nodeId, responses);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Custom scheduler for the SQL folks to solve a performance problem with their builds where they end up with a few long-running
        /// requests on all but one node, and then a very large number of short-running requests on that one node -- which is by design for
        /// our current scheduler, but makes it so that later in the build, when these configurations are re-entered with new requests, the
        /// build becomes essentially serial because so many of the configurations are tied to that one node.
        ///
        /// Fixes that problem by intentionally choosing to refrain from assigning new configurations to idle nodes if those idle nodes already
        /// have more than their fair share of the existing configurations assigned to them.
        /// </summary>
        private void AssignUnscheduledRequestsUsingCustomSchedulerForSQL(List<ScheduleResponse> responses, HashSet<int> idleNodes)
        {
            // We want to find more work first, and we assign traversals to the in-proc node first, if possible.
            AssignUnscheduledRequestsByTraversalsFirst(responses, idleNodes);
            if (idleNodes.Count == 0)
            {
                return;
            }

            Dictionary<int, int> configurationCountsByNode = new Dictionary<int, int>(_availableNodes.Count);

            // The configuration count limit will be the average configuration count * X (to allow for some wiggle room) where
            // the default value of X is 1.1 (+ 10%)
            int configurationCountLimit = 0;

            foreach (int availableNodeId in _availableNodes.Keys)
            {
                configurationCountsByNode[availableNodeId] = _schedulingData.GetConfigurationsCountByNode(availableNodeId, true /* excludeTraversals */, _configCache);
                configurationCountLimit += configurationCountsByNode[availableNodeId];
            }

            configurationCountLimit = Math.Max(1, (int)Math.Ceiling(configurationCountLimit * _customSchedulerForSQLConfigurationLimitMultiplier / _availableNodes.Count));

            // Assign requests but try to keep the same number of configurations on each node
            List<int> nodesByConfigurationCountAscending = new List<int>(_availableNodes.Keys);
            nodesByConfigurationCountAscending.Sort(delegate (int left, int right)
            {
                return Comparer<int>.Default.Compare(configurationCountsByNode[left], configurationCountsByNode[right]);
            });

            // Assign projects to nodes, preferring to assign work to nodes with the fewest configurations first.
            foreach (int nodeId in nodesByConfigurationCountAscending)
            {
                if (!idleNodes.Contains(nodeId))
                {
                    continue;
                }

                if (AtSchedulingLimit())
                {
                    TraceScheduler("System load limit reached, cannot schedule new work.  Executing: {0} Yielding: {1} Max Count: {2}", _schedulingData.ExecutingRequestsCount, _schedulingData.YieldingRequestsCount, _componentHost.BuildParameters.MaxNodeCount);
                    break;
                }

                foreach (SchedulableRequest request in _schedulingData.UnscheduledRequestsWhichCanBeScheduled)
                {
                    if (CanScheduleRequestToNode(request, nodeId))
                    {
                        int requiredNodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(request.BuildRequest.ConfigurationId);

                        // Only schedule an entirely new configuration (one not already tied to this node) to this node if we're
                        // not already over the limit needed to keep a reasonable balance.
                        if (request.AssignedNode == nodeId || requiredNodeId == nodeId || configurationCountsByNode[nodeId] <= configurationCountLimit)
                        {
                            AssignUnscheduledRequestToNode(request, nodeId, responses);
                            idleNodes.Remove(nodeId);
                            break;
                        }
                        else if (configurationCountsByNode[nodeId] > configurationCountLimit)
                        {
                            TraceScheduler("Chose not to assign request {0} to node {2} because its count of configurations ({3}) exceeds the current limit ({4}).", request.BuildRequest.GlobalRequestId, request.BuildRequest.ConfigurationId, nodeId, configurationCountsByNode[nodeId], configurationCountLimit);
                        }
                    }
                }
            }

            // at this point, we may still have work left unassigned, but that's OK -- we're deliberately choosing to delay assigning all available
            // requests in order to avoid overloading certain nodes with excess numbers of requests.
        }

        /// <summary>
        /// Assigns the specified request to the specified node.
        /// </summary>
        private void AssignUnscheduledRequestToNode(SchedulableRequest request, int nodeId, List<ScheduleResponse> responses)
        {
            ErrorUtilities.VerifyThrowArgumentNull(request, nameof(request));
            ErrorUtilities.VerifyThrowArgumentNull(responses, nameof(responses));
            ErrorUtilities.VerifyThrow(nodeId != InvalidNodeId, "Invalid node id specified.");

            request.VerifyState(SchedulableRequestState.Unscheduled);

            // Determine if this node has seen our configuration before.  If not, we must send it along with this request.
            bool mustSendConfigurationToNode = _availableNodes[nodeId].AssignConfiguration(request.BuildRequest.ConfigurationId);

            // If this is the first time this configuration has been assigned to a node, we will mark the configuration with the assigned node
            // indicating that the master set of results is located there.  Should we ever need to move the results, we will know where to find them.
            BuildRequestConfiguration config = _configCache[request.BuildRequest.ConfigurationId];
            if (config.ResultsNodeId == InvalidNodeId)
            {
                config.ResultsNodeId = nodeId;
            }

            ErrorUtilities.VerifyThrow(config.ResultsNodeId != InvalidNodeId, "Configuration's results node is not set.");

            responses.Add(ScheduleResponse.CreateScheduleResponse(nodeId, request.BuildRequest, mustSendConfigurationToNode));
            TraceScheduler("Executing request {0} on node {1} with parent {2}", request.BuildRequest.GlobalRequestId, nodeId, (request.Parent == null) ? -1 : request.Parent.BuildRequest.GlobalRequestId);

            WarnWhenProxyBuildsGetScheduledOnOutOfProcNode();

            request.ResumeExecution(nodeId);

            void WarnWhenProxyBuildsGetScheduledOnOutOfProcNode()
            {
                if (request.IsProxyBuildRequest() && nodeId != InProcNodeId && _schedulingData.CanScheduleRequestToNode(request, InProcNodeId))
                {
                    ErrorUtilities.VerifyThrow(
                        _componentHost.BuildParameters.DisableInProcNode || ForceAffinityOutOfProc,
                        "Proxy requests should only get scheduled to out of proc nodes when the inproc node is disabled");

                    var loggedWarnings = Interlocked.CompareExchange(ref _loggedWarningsForProxyBuildsOnOutOfProcNodes, 1, 0);

                    if (loggedWarnings == 0)
                    {
                        _inprocNodeContext.LogWarning("ProxyRequestNotScheduledOnInprocNode");
                    }
                }
            }
        }

        /// <summary>
        /// Returns the maximum number of cores that can be returned from a RequestCores() call at the moment.
        /// </summary>
        private int GetAvailableCoresForExplicitRequests()
        {
            // At least one core is always implicitly granted to the node making the request.
            // If _nodeCoreAllocationWeight is more than zero, it can increase this value by the specified fraction of executing nodes.
            int implicitlyGrantedCores = Math.Max(1, (_schedulingData.ExecutingRequestsCount * _nodeCoreAllocationWeight) / 100);

            // The number of explicitly granted cores is a sum of everything we've granted via RequestCores() so far across all nodes.
            int explicitlyGrantedCores = _schedulingData.ExplicitlyGrantedCores;

            return Math.Max(0, _coreLimit - (implicitlyGrantedCores + explicitlyGrantedCores));
        }

        /// <summary>
        /// Returns true if we are at the limit of work we can schedule.
        /// </summary>
        private bool AtSchedulingLimit()
        {
            if (_schedulingUnlimited)
            {
                return false;
            }

            // We're at our limit of schedulable requests if:
            // (1) MaxNodeCount requests are currently executing
            if (_schedulingData.ExecutingRequestsCount >= _componentHost.BuildParameters.MaxNodeCount)
            {
                return true;
            }

            // (2) Fewer than MaxNodeCount requests are currently executing but the sum of executing request,
            //     yielding requests, and explicitly granted cores exceeds the limit set out below.
            int limit = _componentHost.BuildParameters.MaxNodeCount switch
            {
                1 => 1,
                2 => _componentHost.BuildParameters.MaxNodeCount + 1 + _nodeLimitOffset,
                _ => _componentHost.BuildParameters.MaxNodeCount + 2 + _nodeLimitOffset,
            };

            return _schedulingData.ExecutingRequestsCount +
                   _schedulingData.YieldingRequestsCount +
                   _schedulingData.ExplicitlyGrantedCores >= limit;
        }

        /// <summary>
        /// Returns true if a request can be scheduled to a node, false otherwise.
        /// </summary>
        private bool CanScheduleRequestToNode(SchedulableRequest request, int nodeId)
        {
            if (_schedulingData.CanScheduleRequestToNode(request, nodeId))
            {
                NodeAffinity affinity = GetNodeAffinityForRequest(request.BuildRequest);
                bool result = _availableNodes[nodeId].CanServiceRequestWithAffinity(affinity);
                return result;
            }

            return false;
        }

        /// <summary>
        /// Adds CreateNode responses to satisfy all the affinities in the list of requests, with the following constraints:
        ///
        /// a) Issue no more than one response to create an inproc node, and aggressively issues as many requests for an out-of-proc node
        ///    as there are requests to assign to them.
        ///
        /// b) Don't exceed the max node count, *unless* there isn't even one node of the necessary affinity yet. (That means that even if there's a max
        ///    node count of e.g., 3, and we have already created 3 out of proc nodes, we will still create an inproc node if affinity requires it; if
        ///    we didn't, the build would jam.)
        ///
        /// Returns true if there is a pending response to create a new node.
        /// </summary>
        private bool CreateNewNodeIfPossible(List<ScheduleResponse> responses, IEnumerable<SchedulableRequest> requests)
        {
            int availableNodesWithInProcAffinity = 1 - _currentInProcNodeCount;
            int availableNodesWithOutOfProcAffinity = _componentHost.BuildParameters.MaxNodeCount - _currentOutOfProcNodeCount;
            int requestsWithOutOfProcAffinity = 0;
            int requestsWithAnyAffinityOnInProcNodes = 0;

            int inProcNodesToCreate = 0;
            int outOfProcNodesToCreate = 0;

            foreach (SchedulableRequest request in requests)
            {
                int assignedNodeForConfiguration = _schedulingData.GetAssignedNodeForRequestConfiguration(request.BuildRequest.ConfigurationId);

                // Although this request has not been scheduled, this configuration may previously have been
                // scheduled to an existing node.  If so, we shouldn't count it in our checks for new node
                // creation, because it'll only eventually get assigned to its existing node anyway.
                if (assignedNodeForConfiguration != Scheduler.InvalidNodeId)
                {
                    continue;
                }

                NodeAffinity affinityRequired = GetNodeAffinityForRequest(request.BuildRequest);

                switch (affinityRequired)
                {
                    case NodeAffinity.InProc:
                        inProcNodesToCreate++;

                        // If we've previously seen "Any"-affinitized requests, now that there are some
                        // genuine inproc requests, they get to play with the inproc node first, so
                        // push the "Any" requests to the out-of-proc nodes.
                        if (requestsWithAnyAffinityOnInProcNodes > 0)
                        {
                            requestsWithAnyAffinityOnInProcNodes--;
                            outOfProcNodesToCreate++;
                        }

                        break;
                    case NodeAffinity.OutOfProc:
                        outOfProcNodesToCreate++;
                        requestsWithOutOfProcAffinity++;
                        break;
                    case NodeAffinity.Any:
                        // Prefer inproc node if there's space, but otherwise apportion to out-of-proc.
                        if (inProcNodesToCreate < availableNodesWithInProcAffinity && !_componentHost.BuildParameters.DisableInProcNode)
                        {
                            inProcNodesToCreate++;
                            requestsWithAnyAffinityOnInProcNodes++;
                        }
                        else
                        {
                            outOfProcNodesToCreate++;

                            // If we are *required* to create an OOP node because the IP node is disabled, then treat this as if
                            // the request had an OOP affinity.
                            if (_componentHost.BuildParameters.DisableInProcNode)
                            {
                                requestsWithOutOfProcAffinity++;
                            }
                        }

                        break;
                    default:
                        ErrorUtilities.ThrowInternalErrorUnreachable();
                        break;
                }

                // We've already hit the limit of the number of nodes we'll be allowed to create, so just quit counting now.
                if (inProcNodesToCreate >= availableNodesWithInProcAffinity && outOfProcNodesToCreate >= availableNodesWithOutOfProcAffinity)
                {
                    break;
                }
            }

            // If we think we want to create inproc nodes
            if (inProcNodesToCreate > 0)
            {
                // In-proc node determination is simple: we want as many as are available.
                inProcNodesToCreate = Math.Min(availableNodesWithInProcAffinity, inProcNodesToCreate);

                // If we still want to create one, go ahead
                if (inProcNodesToCreate > 0)
                {
                    ErrorUtilities.VerifyThrow(inProcNodesToCreate == 1, "We should never be trying to create more than one inproc node");
                    TraceScheduler("Requesting creation of new node satisfying affinity {0}", NodeAffinity.InProc);
                    responses.Add(ScheduleResponse.CreateNewNodeResponse(NodeAffinity.InProc, 1));

                    // We only want to submit one node creation request at a time -- as part of node creation we recursively re-request the scheduler
                    // to do more scheduling, so the other request will be dealt with soon enough.
                    return true;
                }
            }

            // If we think we want to create out-of-proc nodes
            if (outOfProcNodesToCreate > 0)
            {
                // Out-of-proc node determination is a bit more complicated.  If we have N out-of-proc requests, we want to
                // fill up to N out-of-proc nodes.  However, if we have N "any" requests, we must assume that at least some of them
                // will be fulfilled by the inproc node, in which case we only want to launch up to N-1 out-of-proc nodes, for a
                // total of N nodes overall -- the scheduler will only schedule to N nodes at a time, so launching any more than that
                // is ultimately pointless.
                int maxCreatableOutOfProcNodes = availableNodesWithOutOfProcAffinity;

                if (requestsWithOutOfProcAffinity < availableNodesWithOutOfProcAffinity)
                {
                    // We don't have enough explicitly out-of-proc requests to justify creating every technically allowed
                    // out-of-proc node, so our max is actually one less than the absolute max for the reasons explained above.
                    maxCreatableOutOfProcNodes--;
                }

                outOfProcNodesToCreate = Math.Min(maxCreatableOutOfProcNodes, outOfProcNodesToCreate);

                // If we still want to create them, go ahead
                if (outOfProcNodesToCreate > 0)
                {
                    TraceScheduler("Requesting creation of {0} new node(s) satisfying affinity {1}", outOfProcNodesToCreate, NodeAffinity.OutOfProc);
                    responses.Add(ScheduleResponse.CreateNewNodeResponse(NodeAffinity.OutOfProc, outOfProcNodesToCreate));
                }

                // We only want to submit one node creation request at a time -- as part of node creation we recursively re-request the scheduler
                // to do more scheduling, so the other request will be dealt with soon enough.
                return true;
            }

            // If we haven't returned before now, we haven't asked that any new nodes be created.
            return false;
        }

        /// <summary>
        /// Marks the specified request and all of its ancestors as having aborted.
        /// </summary>
        private void MarkRequestAborted(SchedulableRequest request)
        {
            _resultsCache.AddResult(new BuildResult(request.BuildRequest, new BuildAbortedException()));

            // Recursively abort all of the requests we are blocking.
            foreach (SchedulableRequest blockedRequest in request.RequestsWeAreBlocking)
            {
                MarkRequestAborted(blockedRequest);
            }
        }

        /// <summary>
        /// Marks the request as being blocked by another request which is currently building a target whose results we need to proceed.
        /// </summary>
        private void HandleRequestBlockedOnInProgressTarget(SchedulableRequest blockedRequest, BuildRequestBlocker blocker)
        {
            ErrorUtilities.VerifyThrowArgumentNull(blockedRequest, nameof(blockedRequest));
            ErrorUtilities.VerifyThrowArgumentNull(blocker, nameof(blocker));

            // We are blocked on an in-progress request building a target whose results we need.
            SchedulableRequest blockingRequest = _schedulingData.GetScheduledRequest(blocker.BlockingRequestId);

            // The request we blocked on couldn't have been executing (because we are) so it must either be yielding (which is ok because
            // it isn't modifying its own state, just running a background process), ready, or still blocked.
            blockingRequest.VerifyOneOfStates(new SchedulableRequestState[] { SchedulableRequestState.Yielding, SchedulableRequestState.Ready, SchedulableRequestState.Blocked });

            // detect the case for https://github.com/dotnet/msbuild/issues/3047
            // if we have partial results AND blocked and blocking share the same configuration AND are blocked on each other
            if (blocker.PartialBuildResult != null &&
                blockingRequest.BuildRequest.ConfigurationId == blockedRequest.BuildRequest.ConfigurationId &&
                blockingRequest.RequestsWeAreBlockedBy.Contains(blockedRequest))
            {
                // if the blocking request is waiting on a target we have partial results for, preemptively break its dependency
                if (blocker.PartialBuildResult.HasResultsForTarget(blockingRequest.BlockingTarget))
                {
                    blockingRequest.UnblockWithPartialResultForBlockingTarget(blocker.PartialBuildResult);
                }
            }

            blockedRequest.BlockByRequest(blockingRequest, blocker.TargetsInProgress, blocker.BlockingTarget);
        }

        /// <summary>
        /// Marks the parent as blocked waiting for results from a results transfer.
        /// </summary>
        private void HandleRequestBlockedOnResultsTransfer(SchedulableRequest parentRequest, List<ScheduleResponse> responses)
        {
            // Create the new request which will go to the configuration's results node.
            BuildRequest newRequest = new BuildRequest(parentRequest.BuildRequest.SubmissionId, BuildRequest.ResultsTransferNodeRequestId, parentRequest.BuildRequest.ConfigurationId, Array.Empty<string>(), null, parentRequest.BuildRequest.BuildEventContext, parentRequest.BuildRequest, parentRequest.BuildRequest.BuildRequestDataFlags);

            // Assign a new global request id - always different from any other.
            newRequest.GlobalRequestId = _nextGlobalRequestId;
            _nextGlobalRequestId++;

            // Now add the response.  Send it to the node where the configuration's results are stored.  When those results come back
            // we will update the storage location in the configuration.  This is doing a bit of a run around the scheduler - we don't
            // create a new formal request, so we treat the blocked request as if it is still executing - this prevents any other requests
            // from getting onto that node and also means we don't have to do additional work to get the scheduler to understand the bizarre
            // case of sending a request for results from a project's own configuration (which it believes reside on the very node which
            // is actually requesting the results in the first place.)
            BuildRequestConfiguration configuration = _configCache[parentRequest.BuildRequest.ConfigurationId];
            responses.Add(ScheduleResponse.CreateScheduleResponse(configuration.ResultsNodeId, newRequest, false));

            TraceScheduler("Created request {0} (node request {1}) for transfer of configuration {2}'s results from node {3} to node {4}", newRequest.GlobalRequestId, newRequest.NodeRequestId, configuration.ConfigurationId, configuration.ResultsNodeId, parentRequest.AssignedNode);

            // The configuration's results will now be homed at the new location (once they have come back from the
            // original node.)
            configuration.ResultsNodeId = parentRequest.AssignedNode;
        }

        /// <summary>
        /// Marks the request as being blocked by new requests whose results we must get before we can proceed.
        /// </summary>
        private void HandleRequestBlockedByNewRequests(SchedulableRequest parentRequest, BuildRequestBlocker blocker, List<ScheduleResponse> responses)
        {
            ErrorUtilities.VerifyThrowArgumentNull(blocker, nameof(blocker));
            ErrorUtilities.VerifyThrowArgumentNull(responses, nameof(responses));

            // The request is waiting on new requests.
            bool abortRequestBatch = false;
            Stack<BuildRequest> requestsToAdd = new Stack<BuildRequest>(blocker.BuildRequests.Length);
            foreach (BuildRequest request in blocker.BuildRequests)
            {
                // Assign a global request id to this request.
                if (request.GlobalRequestId == BuildRequest.InvalidGlobalRequestId)
                {
                    AssignGlobalRequestId(request);
                }

                int nodeForResults = (parentRequest == null) ? InvalidNodeId : parentRequest.AssignedNode;
                TraceScheduler("Received request {0} (node request {1}) with parent {2} from node {3}", request.GlobalRequestId, request.NodeRequestId, request.ParentGlobalRequestId, nodeForResults);

                // First, determine if we have already built this request and have results for it.  If we do, we prepare the responses for it
                // directly here.  We COULD simply report these as blocking the parent request and let the scheduler pick them up later when the parent
                // comes back up as schedulable, but we prefer to send the results back immediately so this request can (potentially) continue uninterrupted.
                ScheduleResponse response = TrySatisfyRequestFromCache(nodeForResults, request, skippedResultsDoNotCauseCacheMiss: _componentHost.BuildParameters.SkippedResultsDoNotCauseCacheMiss());
                if (response != null)
                {
                    TraceScheduler("Request {0} (node request {1}) satisfied from the cache.", request.GlobalRequestId, request.NodeRequestId);

                    // BuildResult result = (response.Action == ScheduleActionType.Unblock) ? response.Unblocker.Results[0] : response.BuildResult;
                    LogRequestHandledFromCache(request, response.BuildResult);
                    responses.Add(response);

                    // If we wish to implement an algorithm where the first failing request aborts the remaining request, check for
                    // overall result being failure rather than just circular dependency.  Sync with BasicScheduler.ReportResult and
                    // BuildRequestEntry.ReportResult.
                    if (response.BuildResult.CircularDependency)
                    {
                        abortRequestBatch = true;
                    }
                }
                else if (CheckIfCacheMissOnReferencedProjectIsAllowedAndErrorIfNot(nodeForResults, request, responses, out var emitNonErrorLogs))
                {
                    emitNonErrorLogs(_componentHost.LoggingService);

                    // Ensure there is no affinity mismatch between this request and a previous request of the same configuration.
                    NodeAffinity requestAffinity = GetNodeAffinityForRequest(request);
                    NodeAffinity existingRequestAffinity = NodeAffinity.Any;
                    if (requestAffinity != NodeAffinity.Any)
                    {
                        bool affinityMismatch = false;
                        int assignedNodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(request.ConfigurationId);
                        if (assignedNodeId != Scheduler.InvalidNodeId)
                        {
                            if (!_availableNodes[assignedNodeId].CanServiceRequestWithAffinity(GetNodeAffinityForRequest(request)))
                            {
                                // This request's configuration has already been assigned to a node which cannot service this affinity.
                                if (_schedulingData.GetRequestsAssignedToConfigurationCount(request.ConfigurationId) == 0)
                                {
                                    // If there are no other requests already scheduled for that configuration, we can safely reassign.
                                    _schedulingData.UnassignNodeForRequestConfiguration(request.ConfigurationId);
                                }
                                else
                                {
                                    existingRequestAffinity = (_availableNodes[assignedNodeId].ProviderType == NodeProviderType.InProc) ? NodeAffinity.InProc : NodeAffinity.OutOfProc;
                                    affinityMismatch = true;
                                }
                            }
                        }
                        else if (_schedulingData.GetRequestsAssignedToConfigurationCount(request.ConfigurationId) > 0)
                        {
                            // Would any other existing requests for this configuration mismatch?
                            foreach (SchedulableRequest existingRequest in _schedulingData.GetRequestsAssignedToConfiguration(request.ConfigurationId))
                            {
                                existingRequestAffinity = GetNodeAffinityForRequest(existingRequest.BuildRequest);
                                if (existingRequestAffinity != NodeAffinity.Any && existingRequestAffinity != requestAffinity)
                                {
                                    // The existing request has an affinity which doesn't match this one, so this one could never be scheduled.
                                    affinityMismatch = true;
                                    break;
                                }
                            }
                        }

                        if (affinityMismatch)
                        {
                            ErrorUtilities.VerifyThrowInternalError(
                                _configCache.HasConfiguration(request.ConfigurationId),
                                "A request should have a configuration if it makes it this far in the build process.");

                            var config = _configCache[request.ConfigurationId];
                            var globalProperties = string.Join(
                                ";",
                                config.GlobalProperties.ToDictionary().Select(kvp => $"{kvp.Key}={kvp.Value}"));

                            var result = new BuildResult(
                                request,
                                new InvalidOperationException(
                                    ResourceUtilities.FormatResourceStringStripCodeAndKeyword(
                                        "AffinityConflict",
                                        requestAffinity,
                                        existingRequestAffinity,
                                        config.ProjectFullPath,
                                        globalProperties)));
                            response = GetResponseForResult(nodeForResults, request, result);
                            responses.Add(response);
                            continue;
                        }
                    }

                    // Now add the requests so they would naturally be picked up by the scheduler in the order they were issued,
                    // but before any other requests in the list.  This makes us prefer a depth-first traversal.
                    requestsToAdd.Push(request);
                }
            }

            // Now add any unassigned build requests.
            if (!abortRequestBatch)
            {
                if (requestsToAdd.Count == 0)
                {
                    // All of the results are being reported directly from the cache (from above), so this request can continue on its merry way.
                    if (parentRequest != null)
                    {
                        // responses.Add(new ScheduleResponse(parentRequest.AssignedNode, new BuildRequestUnblocker(parentRequest.BuildRequest.GlobalRequestId)));
                        responses.Add(ScheduleResponse.CreateResumeExecutionResponse(parentRequest.AssignedNode, parentRequest.BuildRequest.GlobalRequestId));
                    }
                }
                else
                {
                    while (requestsToAdd.Count > 0)
                    {
                        BuildRequest requestToAdd = requestsToAdd.Pop();
                        SchedulableRequest blockingRequest = _schedulingData.CreateRequest(requestToAdd, parentRequest);

                        parentRequest?.BlockByRequest(blockingRequest, blocker.TargetsInProgress);
                    }
                }
            }
        }

        /// <summary>
        /// Resumes executing a request which was in the Ready state for the specified node, if any.
        /// </summary>
        private void ResumeReadyRequestIfAny(int nodeId, List<ScheduleResponse> responses)
        {
            // Look for ready requests.  We prefer to let these continue first rather than finding new work.
            // We only actually look at the first one, since that is all we need.
            foreach (SchedulableRequest request in _schedulingData.GetReadyRequestsByNode(nodeId))
            {
                TraceScheduler("Unblocking request {0} on node {1}", request.BuildRequest.GlobalRequestId, nodeId);

                // ScheduleResponse response = new ScheduleResponse(nodeId, new BuildRequestUnblocker(request.BuildRequest.GlobalRequestId));
                ScheduleResponse response = ScheduleResponse.CreateResumeExecutionResponse(nodeId, request.BuildRequest.GlobalRequestId);
                request.ResumeExecution(nodeId);
                responses.Add(response);
                return;
            }
        }

        /// <summary>
        /// Attempts to get results from the cache for this request.  If results are available, reports them to the
        /// correct node.  If that action causes the parent to become ready and its node is idle, the parent is
        /// resumed.
        /// </summary>
        private void ResolveRequestFromCacheAndResumeIfPossible(SchedulableRequest request, List<ScheduleResponse> responses)
        {
            int nodeForResults = (request.Parent != null) ? request.Parent.AssignedNode : InvalidNodeId;

            // Do we already have results?  If so, just return them.
            ScheduleResponse response = TrySatisfyRequestFromCache(nodeForResults, request.BuildRequest, skippedResultsDoNotCauseCacheMiss: _componentHost.BuildParameters.SkippedResultsDoNotCauseCacheMiss());
            if (response != null)
            {
                if (response.Action == ScheduleActionType.SubmissionComplete)
                {
                    ErrorUtilities.VerifyThrow(request.Parent == null, "Unexpectedly generated a SubmissionComplete response for a request which is not top-level.");
                    LogRequestHandledFromCache(request.BuildRequest, response.BuildResult);

                    // This was root request, we can report submission complete.
                    responses.Add(ScheduleResponse.CreateSubmissionCompleteResponse(response.BuildResult));
                    if (response.BuildResult.OverallResult != BuildResultCode.Failure)
                    {
                        WriteSchedulingPlan(response.BuildResult.SubmissionId);
                    }
                }
                else
                {
                    LogRequestHandledFromCache(request.BuildRequest, response.Unblocker.Result);
                    request.Complete(response.Unblocker.Result);

                    TraceScheduler("Reporting results for request {0} with parent {1} to node {2} from cache.", request.BuildRequest.GlobalRequestId, request.BuildRequest.ParentGlobalRequestId, response.NodeId);
                    if (response.NodeId != InvalidNodeId)
                    {
                        responses.Add(response);
                    }

                    // Is the node we are reporting to idle? If so, does reporting this result allow it to proceed with work?
                    if (!_schedulingData.IsNodeWorking(response.NodeId))
                    {
                        ResumeReadyRequestIfAny(response.NodeId, responses);
                    }
                }
            }
            else
            {
                CheckIfCacheMissOnReferencedProjectIsAllowedAndErrorIfNot(nodeForResults, request.BuildRequest, responses, out _);
            }
        }

        /// <summary>
        /// Satisfies pending resource requests. Requests are pulled from the queue in FIFO fashion and granted as many cores
        /// as possible, optimizing for maximum number of cores granted to a single request, not for maximum number of satisfied
        /// requests.
        /// </summary>
        private void HandlePendingResourceRequests()
        {
            while (_pendingRequestCoresCallbacks.Count > 0)
            {
                int availableCores = GetAvailableCoresForExplicitRequests();
                if (availableCores == 0)
                {
                    return;
                }
                TaskCompletionSource<int> completionSource = _pendingRequestCoresCallbacks.Dequeue();
                completionSource.SetResult(availableCores);
            }
        }

        /// <summary>
        /// Determines which work is available which must be assigned to the nodes.  This includes:
        /// 1. Ready requests - those requests which can immediately resume executing.
        /// 2. Requests which can continue because results are now available but we haven't distributed them.
        /// </summary>
        private void ResumeRequiredWork(List<ScheduleResponse> responses)
        {
            // If we have pending RequestCore calls, satisfy those first.
            HandlePendingResourceRequests();

            // Resume any ready requests on the existing nodes.
            foreach (int nodeId in _availableNodes.Keys)
            {
                // Don't overload the system.
                if (AtSchedulingLimit())
                {
                    TraceScheduler("System load limit reached, cannot resume any more work.  Executing: {0} Yielding: {1} Max Count: {2}", _schedulingData.ExecutingRequestsCount, _schedulingData.YieldingRequestsCount, _componentHost.BuildParameters.MaxNodeCount);
                    return;
                }

                // Determine if this node is actually free to do work.
                if (_schedulingData.IsNodeWorking(nodeId))
                {
                    continue; // Check the next node to see if it is free.
                }

                // Resume a ready request, if any.  We prefer to let existing requests complete before finding new work.
                ResumeReadyRequestIfAny(nodeId, responses);
            }

            // Now determine which unscheduled requests have results.  Reporting these may cause an blocked request to become ready
            // and potentially allow us to continue it.
            List<SchedulableRequest> unscheduledRequests = new List<SchedulableRequest>(_schedulingData.UnscheduledRequests);
            foreach (SchedulableRequest request in unscheduledRequests)
            {
                ResolveRequestFromCacheAndResumeIfPossible(request, responses);
            }
        }

        /// <summary>
        /// Attempts to get a result from the cache to satisfy the request, and returns the appropriate response if possible.
        /// </summary>
        private ScheduleResponse TrySatisfyRequestFromCache(int nodeForResults, BuildRequest request, bool skippedResultsDoNotCauseCacheMiss)
        {
            BuildRequestConfiguration config = _configCache[request.ConfigurationId];
            ResultsCacheResponse resultsResponse = _resultsCache.SatisfyRequest(request, config.ProjectInitialTargets, config.ProjectDefaultTargets, skippedResultsDoNotCauseCacheMiss);

            if (resultsResponse.Type == ResultsCacheResponseType.Satisfied)
            {
                return GetResponseForResult(nodeForResults, request, resultsResponse.Results);
            }

            return null;
        }

        /// <returns>True if caches misses are allowed, false otherwise</returns>
        private bool CheckIfCacheMissOnReferencedProjectIsAllowedAndErrorIfNot(int nodeForResults, BuildRequest request, List<ScheduleResponse> responses, out Action<ILoggingService> emitNonErrorLogs)
        {
            emitNonErrorLogs = _ => { };

            ProjectIsolationMode isolateProjects = _componentHost.BuildParameters.ProjectIsolationMode;
            var configCache = (IConfigCache)_componentHost.GetComponent(BuildComponentType.ConfigCache);

            // do not check root requests as nothing depends on them
            if (isolateProjects == ProjectIsolationMode.False || request.IsRootRequest || request.SkipStaticGraphIsolationConstraints
                || SkipNonexistentTargetsIfExistentTargetsHaveResults(request))
            {
                bool logComment = ((isolateProjects == ProjectIsolationMode.True || isolateProjects == ProjectIsolationMode.MessageUponIsolationViolation) && request.SkipStaticGraphIsolationConstraints);
                if (logComment)
                {
                    // retrieving the configs is not quite free, so avoid computing them eagerly
                    var configs = GetConfigurations();

                    emitNonErrorLogs = ls => ls.LogComment(
                            NewBuildEventContext(),
                            MessageImportance.Normal,
                            "SkippedConstraintsOnRequest",
                            configs.ParentConfig.ProjectFullPath,
                            configs.RequestConfig.ProjectFullPath);
                }

                return true;
            }

            (BuildRequestConfiguration requestConfig, BuildRequestConfiguration parentConfig) = GetConfigurations();

            // allow self references (project calling the msbuild task on itself, potentially with different global properties)
            if (parentConfig.ProjectFullPath.Equals(requestConfig.ProjectFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var errorMessage = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword(
                "CacheMissesNotAllowedInIsolatedGraphBuilds",
                parentConfig.ProjectFullPath,
                ConcatenateGlobalProperties(parentConfig),
                requestConfig.ProjectFullPath,
                ConcatenateGlobalProperties(requestConfig),
                request.Targets.Count == 0
                    ? "default"
                    : string.Join(";", request.Targets));

            // Issue a failed build result to have the msbuild task marked as failed and thus stop the build
            BuildResult result = new BuildResult(request);
            result.SetOverallResult(false);
            result.SchedulerInducedError = errorMessage;

            var response = GetResponseForResult(nodeForResults, request, result);
            responses.Add(response);

            return false;

            BuildEventContext NewBuildEventContext()
            {
                return new BuildEventContext(
                    request.SubmissionId,
                    1,
                    BuildEventContext.InvalidProjectInstanceId,
                    BuildEventContext.InvalidProjectContextId,
                    BuildEventContext.InvalidTargetId,
                    BuildEventContext.InvalidTaskId);
            }

            (BuildRequestConfiguration RequestConfig, BuildRequestConfiguration ParentConfig) GetConfigurations()
            {
                BuildRequestConfiguration buildRequestConfiguration = configCache[request.ConfigurationId];

                // Need the parent request. It might be blocked or executing; check both.
                SchedulableRequest parentRequest = _schedulingData.BlockedRequests.FirstOrDefault(r => r.BuildRequest.GlobalRequestId == request.ParentGlobalRequestId)
                    ?? _schedulingData.ExecutingRequests.FirstOrDefault(r => r.BuildRequest.GlobalRequestId == request.ParentGlobalRequestId);

                ErrorUtilities.VerifyThrowInternalNull(parentRequest, nameof(parentRequest));
                ErrorUtilities.VerifyThrow(
                    configCache.HasConfiguration(parentRequest.BuildRequest.ConfigurationId),
                    "All non root requests should have a parent with a loaded configuration");

                BuildRequestConfiguration parentConfiguration = configCache[parentRequest.BuildRequest.ConfigurationId];
                return (buildRequestConfiguration, parentConfiguration);
            }

            string ConcatenateGlobalProperties(BuildRequestConfiguration configuration)
            {
                return string.Join("; ", configuration.GlobalProperties.Select<ProjectPropertyInstance, string>(p => $"{p.Name}={p.EvaluatedValue}"));
            }

            bool SkipNonexistentTargetsIfExistentTargetsHaveResults(BuildRequest buildRequest)
            {
                // Return early if the top-level target(s) of this build request weren't requested to be skipped if nonexistent.
                if ((buildRequest.BuildRequestDataFlags & BuildRequestDataFlags.SkipNonexistentTargets) != BuildRequestDataFlags.SkipNonexistentTargets)
                {
                    return false;
                }

                BuildResult requestResults = _resultsCache.GetResultsForConfiguration(buildRequest.ConfigurationId);

                // On a self-referenced build, cache misses are allowed.
                if (requestResults == null)
                {
                    return false;
                }

                // A cache miss on at least one existing target without results is disallowed,
                // as it violates isolation constraints.
                foreach (string target in request.Targets)
                {
                    if (_configCache[buildRequest.ConfigurationId]
                        .ProjectTargets
                        .Contains(target) &&
                        !requestResults.HasResultsForTarget(target))
                    {
                        return false;
                    }
                }

                // A cache miss on nonexistent targets on the reference is allowed, given the request
                // to skip nonexistent targets.
                return true;
            }
        }

        /// <summary>
        /// Records the result to the current cache if its config isn't in the override cache.
        /// </summary>
        /// <param name="result">The result to potentially record in the current cache.</param>
        internal void RecordResultToCurrentCacheIfConfigNotInOverrideCache(BuildResult result)
        {
            // Record these results to the current cache only if their config isn't in the
            // override cache, which can happen if we are building in the project isolation mode
            // ProjectIsolationMode.MessageUponIsolationViolation, and the received result was built by an
            // isolation-violating dependency project.
            if (_configCache is not ConfigCacheWithOverride configCacheWithOverride
                || !configCacheWithOverride.HasConfigurationInOverrideCache(result.ConfigurationId))
            {
                _resultsCache.AddResult(result);
            }
        }

        /// <summary>
        /// Gets the appropriate ScheduleResponse for a result, either to complete a submission or to report to a node.
        /// </summary>
        private ScheduleResponse GetResponseForResult(int parentRequestNode, BuildRequest requestWhichGeneratedResult, BuildResult result)
        {
            // We have results, return them to the originating node, or if it is a root request, mark the submission complete.
            if (requestWhichGeneratedResult.IsRootRequest)
            {
                // return new ScheduleResponse(result);
                return ScheduleResponse.CreateSubmissionCompleteResponse(result);
            }
            else
            {
                ErrorUtilities.VerifyThrow(parentRequestNode != InvalidNodeId, "Invalid parent node provided.");

                // return new ScheduleResponse(parentRequestNode, new BuildRequestUnblocker(requestWhichGeneratedResult.ParentGlobalRequestId, result));
                ErrorUtilities.VerifyThrow(result.ParentGlobalRequestId == requestWhichGeneratedResult.ParentGlobalRequestId, "Result's parent doesn't match request's parent.");
                return ScheduleResponse.CreateReportResultResponse(parentRequestNode, result);
            }
        }

        /// <summary>
        /// Logs the project started/finished pair for projects which are skipped entirely because all
        /// of their results are available in the cache.
        /// </summary>
        private void LogRequestHandledFromCache(BuildRequest request, BuildResult result)
        {
            BuildRequestConfiguration configuration = _configCache[request.ConfigurationId];
            int nodeId = _schedulingData.GetAssignedNodeForRequestConfiguration(request.ConfigurationId);
            NodeLoggingContext nodeContext = new NodeLoggingContext(_componentHost.LoggingService, nodeId, true);
            nodeContext.LogRequestHandledFromCache(request, configuration, result);

            TraceScheduler(
                "Request {0} (node request {1}) with targets ({2}) satisfied from cache",
                request.GlobalRequestId,
                request.NodeRequestId,
                string.Join(";", request.Targets));
        }

        /// <summary>
        /// This method determines how many requests are waiting for this request, taking into account the full tree of all requests
        /// in all dependency chains which are waiting.
        /// </summary>
        private int ComputeClosureOfWaitingRequests(SchedulableRequest request)
        {
            int waitingRequests = 0;

            // In single-proc, this doesn't matter since scheduling is always 100% efficient.
            if (_componentHost.BuildParameters.MaxNodeCount > 1)
            {
                foreach (SchedulableRequest waitingRequest in request.RequestsWeAreBlocking)
                {
                    waitingRequests++;
                    waitingRequests += ComputeClosureOfWaitingRequests(waitingRequest);
                }
            }

            return waitingRequests;
        }

        /// <summary>
        /// Gets the node affinity for the specified request.
        /// </summary>
        private NodeAffinity GetNodeAffinityForRequest(BuildRequest request)
        {
            if (ForceAffinityOutOfProc)
            {
                return NodeAffinity.OutOfProc;
            }

            if (IsTraversalRequest(request))
            {
                return NodeAffinity.InProc;
            }

            ErrorUtilities.VerifyThrow(request.ConfigurationId != BuildRequestConfiguration.InvalidConfigurationId, "Requests should have a valid configuration id at this point");
            // If this configuration has been previously built on an out of proc node, scheduling it on the inproc node can cause either an affinity mismatch error when
            // there are other pending requests for the same configuration or "unscheduled requests remain in the presence of free out of proc nodes" errors if there's no pending requests.
            // So only assign proxy builds to the inproc node if their config hasn't been previously assigned to an out of proc node.
            if (_schedulingData.CanScheduleConfigurationToNode(request.ConfigurationId, InProcNodeId) && request.IsProxyBuildRequest())
            {
                return NodeAffinity.InProc;
            }

            BuildRequestConfiguration configuration = _configCache[request.ConfigurationId];

            // The affinity may have been specified by the host services.
            NodeAffinity affinity = NodeAffinity.Any;
            string pathOfProject = configuration.ProjectFullPath;
            if (request.HostServices != null)
            {
                affinity = request.HostServices.GetNodeAffinity(pathOfProject);
            }

            // If the request itself had no specific node affinity, it may be that the overall build still has
            // a requirement, so check that.
            if (affinity == NodeAffinity.Any)
            {
                if (_componentHost.BuildParameters.HostServices != null)
                {
                    affinity = _componentHost.BuildParameters.HostServices.GetNodeAffinity(pathOfProject);
                }
            }

            return affinity;
        }

        /// <summary>
        /// Iterates through the set of available nodes and checks whether any of them is
        /// capable of servicing this request or any of the requests that it is blocked
        /// by (regardless of whether they are currently available to do so).
        /// </summary>
        private bool RequestOrAnyItIsBlockedByCanBeServiced(SchedulableRequest request)
        {
            if (request.RequestsWeAreBlockedByCount > 0)
            {
                foreach (SchedulableRequest requestWeAreBlockedBy in request.RequestsWeAreBlockedBy)
                {
                    if (RequestOrAnyItIsBlockedByCanBeServiced(requestWeAreBlockedBy))
                    {
                        return true;
                    }
                }

                // if none of the requests we are blocked by can be serviced, it doesn't matter
                // whether we can be serviced or not -- the reason we're blocked is because none
                // of the requests we are blocked by can be serviced.
                return false;
            }
            else
            {
                foreach (NodeInfo node in _availableNodes.Values)
                {
                    if (CanScheduleRequestToNode(request, node.NodeId))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Determines if we have a matching request somewhere, and if so, assigns the same request ID.  Otherwise
        /// assigns a new request id.
        /// </summary>
        /// <remarks>
        /// UNDONE: (Performance) This algorithm should be modified so we don't have to iterate over all of the
        /// requests to find a matching one.  A HashSet with proper equality semantics and a good hash code for the BuildRequest
        /// would speed this considerably, especially for large numbers of projects in a build.
        /// </remarks>
        /// <param name="request">The request whose ID should be assigned</param>
        private void AssignGlobalRequestId(BuildRequest request)
        {
            bool assignNewId = false;
            if (request.GlobalRequestId == BuildRequest.InvalidGlobalRequestId && _schedulingData.GetRequestsAssignedToConfigurationCount(request.ConfigurationId) > 0)
            {
                foreach (SchedulableRequest existingRequest in _schedulingData.GetRequestsAssignedToConfiguration(request.ConfigurationId))
                {
                    if (existingRequest.BuildRequest.Targets.Count == request.Targets.Count)
                    {
                        List<string> leftTargets = new List<string>(existingRequest.BuildRequest.Targets);
                        List<string> rightTargets = new List<string>(request.Targets);

                        leftTargets.Sort(StringComparer.OrdinalIgnoreCase);
                        rightTargets.Sort(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < leftTargets.Count; i++)
                        {
                            if (!leftTargets[i].Equals(rightTargets[i], StringComparison.OrdinalIgnoreCase))
                            {
                                assignNewId = true;
                                break;
                            }
                        }

                        if (!assignNewId)
                        {
                            request.GlobalRequestId = existingRequest.BuildRequest.GlobalRequestId;
                            return;
                        }
                    }
                }
            }

            request.GlobalRequestId = _nextGlobalRequestId;
            _nextGlobalRequestId++;
        }

        /// <summary>
        /// Writes the graph representation of how the nodes were utilized.
        /// </summary>
        private void WriteNodeUtilizationGraph(ILoggingService loggingService, BuildEventContext context, bool useConfigurations)
        {
            int[] currentWork = new int[_availableNodes.Count];
            int[] previousWork = new int[currentWork.Length];
            HashSet<int>[] runningRequests = new HashSet<int>[currentWork.Length];
            DateTime currentEventTime = DateTime.MinValue;
            DateTime previousEventTime = DateTime.MinValue;
            double accumulatedDuration = 0;

            TimeSpan[] nodeActiveTimes = new TimeSpan[_availableNodes.Count];
            DateTime[] nodeStartTimes = new DateTime[_availableNodes.Count];
            int eventIndex = 0;

            Dictionary<int, int> availableNodeIdsToIndex = new Dictionary<int, int>(_availableNodes.Count);
            int[] indexToAvailableNodeId = new int[_availableNodes.Count];
            int indexIntoArrays = 0;

            foreach (int availableNodeId in _availableNodes.Keys)
            {
                availableNodeIdsToIndex[availableNodeId] = indexIntoArrays;
                indexToAvailableNodeId[indexIntoArrays] = availableNodeId;
                indexIntoArrays++;
            }

            // Prepare the arrays and headers.
            StringBuilder nodeIndices = new StringBuilder();
            int invalidWorkId = useConfigurations ? BuildRequestConfiguration.InvalidConfigurationId : BuildRequest.InvalidGlobalRequestId;
            for (int i = 0; i < currentWork.Length; i++)
            {
                currentWork[i] = invalidWorkId;
                previousWork[i] = invalidWorkId;
                runningRequests[i] = new HashSet<int>();
                nodeIndices.AppendFormat(CultureInfo.InvariantCulture, "{0,-5}   ", indexToAvailableNodeId[i]);
            }

            loggingService.LogComment(context, MessageImportance.Normal, "NodeUtilizationHeader", nodeIndices.ToString());

            // Walk through each of the events and grab all of the events which have the same timestamp to determine what occurred.
            foreach (SchedulingData.SchedulingEvent buildEvent in _schedulingData.BuildEvents)
            {
                int workId = useConfigurations ? buildEvent.Request.BuildRequest.ConfigurationId : buildEvent.Request.BuildRequest.GlobalRequestId;

                if (buildEvent.EventTime > currentEventTime)
                {
                    WriteNodeUtilizationGraphLine(loggingService, context, currentWork, previousWork, buildEvent.EventTime, currentEventTime, invalidWorkId, ref accumulatedDuration);

                    if (currentEventTime != DateTime.MinValue)
                    {
                        // Accumulate time for nodes which were not idle.
                        for (int i = 0; i < currentWork.Length; i++)
                        {
                            if (currentWork[i] != invalidWorkId)
                            {
                                for (int x = 0; x < runningRequests[i].Count; x++)
                                {
                                    nodeActiveTimes[i] += buildEvent.EventTime - currentEventTime;
                                }
                            }
                        }
                    }

                    currentWork.CopyTo(previousWork, 0);
                    previousEventTime = currentEventTime;
                    currentEventTime = buildEvent.EventTime;
                    eventIndex++;
                }

                // The assigned node may be invalid if the request was completed from the cache.
                // In that case, just skip assessing it -- it did effectively no work.
                if (buildEvent.Request.AssignedNode != InvalidNodeId)
                {
                    int nodeForEvent = availableNodeIdsToIndex[buildEvent.Request.AssignedNode];

                    switch (buildEvent.NewState)
                    {
                        case SchedulableRequestState.Executing:
                        case SchedulableRequestState.Yielding:
                            currentWork[nodeForEvent] = workId;
                            if (!runningRequests[nodeForEvent].Contains(workId))
                            {
                                runningRequests[nodeForEvent].Add(workId);
                            }

                            if (nodeStartTimes[nodeForEvent] == DateTime.MinValue)
                            {
                                nodeStartTimes[nodeForEvent] = buildEvent.EventTime;
                            }

                            break;

                        default:
                            if (runningRequests[nodeForEvent].Contains(workId))
                            {
                                runningRequests[nodeForEvent].Remove(workId);
                            }

                            if (previousWork[nodeForEvent] == workId)
                            {
                                // The previously executing request is no longer executing here.
                                if (runningRequests[nodeForEvent].Count == 0)
                                {
                                    currentWork[nodeForEvent] = invalidWorkId; // Idle
                                }
                                else
                                {
                                    currentWork[nodeForEvent] = runningRequests[nodeForEvent].First();
                                }
                            }

                            break;
                    }
                }
            }

            WriteNodeUtilizationGraphLine(loggingService, context, currentWork, previousWork, currentEventTime, previousEventTime, invalidWorkId, ref accumulatedDuration);

            // Write out the node utilization percentage.
            double utilizationAverage = 0;
            StringBuilder utilitzationPercentages = new StringBuilder();
            for (int i = 0; i < nodeActiveTimes.Length; i++)
            {
                TimeSpan totalDuration = currentEventTime - nodeStartTimes[i];
                double utilizationPercent = (double)nodeActiveTimes[i].TotalMilliseconds / (double)totalDuration.TotalMilliseconds;

                utilitzationPercentages.AppendFormat("{0,-5:###.0}   ", utilizationPercent * 100);
                utilizationAverage += utilizationPercent;
            }

            loggingService.LogComment(context, MessageImportance.Normal, "NodeUtilizationSummary", utilitzationPercentages.ToString(), (utilizationAverage / (double)_availableNodes.Count) * 100);
        }

        /// <summary>
        /// Writes a single line of node utilization information.
        /// </summary>
        private void WriteNodeUtilizationGraphLine(ILoggingService loggingService, BuildEventContext context, int[] currentWork, int[] previousWork, DateTime currentEventTime, DateTime previousEventTime, int invalidWorkId, ref double accumulatedDuration)
        {
            if (currentEventTime == DateTime.MinValue)
            {
                return;
            }

            bool haveNonIdleNode = false;
            StringBuilder stringBuilder = new StringBuilder(64);
            stringBuilder.AppendFormat("{0}:   ", previousEventTime.Ticks);
            for (int i = 0; i < currentWork.Length; i++)
            {
                if (currentWork[i] == invalidWorkId)
                {
                    stringBuilder.Append("x       "); // Idle
                }
                else if (currentWork[i] == previousWork[i])
                {
                    stringBuilder.Append("|       "); // Continuing the work from the previous time.
                    haveNonIdleNode = true;
                }
                else
                {
                    stringBuilder.AppendFormat(CultureInfo.InvariantCulture, "{0,-5}   ", currentWork[i]);
                    haveNonIdleNode = true;
                }
            }

            double duration = 0;
            if (previousEventTime != DateTime.MinValue)
            {
                duration = (currentEventTime - previousEventTime).TotalSeconds;
                accumulatedDuration += duration;
            }

            // Limit the number of histogram bar segments. For long runs the number of segments can be counted in
            // hundreds of thousands (for instance a build which took 8061.7s would generate a line 161,235 characters
            // long) which is a bit excessive. The scales implemented below limit the generated line length to
            // manageable proportions even for very long runs.
            int durationElementCount = (int)(duration / 0.05);
            int scale;
            char barSegment;
            if (durationElementCount <= 100)
            {
                barSegment = '.';
                scale = 1;
            }
            else if (durationElementCount <= 1000)
            {
                barSegment = '+';
                scale = 100;
            }
            else
            {
                barSegment = '#';
                scale = 1000;
            }

            string durationBar = new string(barSegment, durationElementCount / scale);
            if (scale > 1)
            {
                durationBar = $"{durationBar} (scale 1:{scale})";
            }
            if (haveNonIdleNode)
            {
                loggingService.LogComment(context, MessageImportance.Normal, "NodeUtilizationEntry", stringBuilder, duration, accumulatedDuration, durationBar);
            }
        }

        /// <summary>
        /// Recursively dumps the build information for the specified hierarchy
        /// </summary>
        private void WriteRecursiveSummary(ILoggingService loggingService, BuildEventContext context, int submissionId, SchedulableRequest request, int level, bool useConfigurations, bool isLastChild)
        {
            int postPad = Math.Max(20 /* field width */ - (2 * level) /* spacing for hierarchy lines */ - 3 /* length allocated for config/request id */, 0);

            StringBuilder prePadString = new StringBuilder(2 * level);
            if (level != 0)
            {
                int levelsToPad = level;
                if (isLastChild)
                {
                    levelsToPad--;
                }

                while (levelsToPad > 0)
                {
                    prePadString.Append("| ");
                    levelsToPad--;
                }

                if (isLastChild)
                {
                    prePadString.Append(@". ");
                }
            }

            loggingService.LogComment(
                context,
                MessageImportance.Normal,
                "BuildHierarchyEntry",
                prePadString.ToString(),
                useConfigurations ? request.BuildRequest.ConfigurationId : request.BuildRequest.GlobalRequestId,
                new String(' ', postPad),
                String.Format(CultureInfo.InvariantCulture, "{0:0.000}", request.GetTimeSpentInState(SchedulableRequestState.Executing).TotalSeconds),
                String.Format(CultureInfo.InvariantCulture, "{0:0.000}", request.GetTimeSpentInState(SchedulableRequestState.Executing).TotalSeconds + request.GetTimeSpentInState(SchedulableRequestState.Blocked).TotalSeconds + request.GetTimeSpentInState(SchedulableRequestState.Ready).TotalSeconds),
                _configCache[request.BuildRequest.ConfigurationId].ProjectFullPath,
                String.Join(", ", request.BuildRequest.Targets));

            List<SchedulableRequest> childRequests = new List<SchedulableRequest>(_schedulingData.GetRequestsByHierarchy(request));
            childRequests.Sort(delegate (SchedulableRequest left, SchedulableRequest right)
            {
                if (left.StartTime < right.StartTime)
                {
                    return -1;
                }
                else if (left.StartTime > right.StartTime)
                {
                    return 1;
                }

                return 0;
            });

            for (int i = 0; i < childRequests.Count; i++)
            {
                SchedulableRequest childRequest = childRequests[i];
                WriteRecursiveSummary(loggingService, context, submissionId, childRequest, level + 1, useConfigurations, i == childRequests.Count - 1);
            }
        }

        #region Debug Information

        /// <summary>
        /// Method used for debugging purposes.
        /// </summary>
        private void TraceScheduler(string format, params object[] stuff)
        {
            if (_debugDumpState)
            {
                try
                {
                    FileUtilities.EnsureDirectoryExists(_debugDumpPath);

                    using StreamWriter file = FileUtilities.OpenWrite(string.Format(CultureInfo.CurrentCulture, Path.Combine(_debugDumpPath, "SchedulerTrace_{0}.txt"), EnvironmentUtilities.CurrentProcessId), append: true);
                    file.Write("{0}({1})-{2}: ", Thread.CurrentThread.Name, Thread.CurrentThread.ManagedThreadId, _schedulingData.EventTime.Ticks);
                    file.WriteLine(format, stuff);
                    file.Flush();
                }
                catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                {
                    // Ignore exceptions
                }
            }
        }

        /// <summary>
        /// Dumps the current state of the scheduler.
        /// </summary>
        private void DumpSchedulerState()
        {
            if (_debugDumpState)
            {
                if (_schedulingData != null)
                {
                    try
                    {
                        FileUtilities.EnsureDirectoryExists(_debugDumpPath);
                        using StreamWriter file = FileUtilities.OpenWrite(string.Format(CultureInfo.CurrentCulture, Path.Combine(_debugDumpPath, "SchedulerState_{0}.txt"), EnvironmentUtilities.CurrentProcessId), append: true);

                        file.WriteLine("Scheduler state at timestamp {0}:", _schedulingData.EventTime.Ticks);
                        file.WriteLine("------------------------------------------------");

                        foreach (int nodeId in _availableNodes.Keys)
                        {
                            file.WriteLine(
                                "Node {0} {1} ({2} assigned requests, {3} configurations)",
                                nodeId,
                                _schedulingData.IsNodeWorking(nodeId)
                                    ? string.Format(
                                        CultureInfo.InvariantCulture,
                                        "Active ({0} executing)",
                                        _schedulingData.GetExecutingRequestByNode(nodeId)
                                            .BuildRequest.GlobalRequestId)
                                    : "Idle",
                                _schedulingData.GetScheduledRequestsCountByNode(nodeId),
                                _schedulingData.GetConfigurationsCountByNode(nodeId, false, null));

                            List<SchedulableRequest> scheduledRequestsByNode = new List<SchedulableRequest>(_schedulingData.GetScheduledRequestsByNode(nodeId));

                            foreach (SchedulableRequest request in scheduledRequestsByNode)
                            {
                                DumpRequestState(file, request, 1);
                                file.WriteLine();
                            }

                            // If the node is idle, we want to know why.
                            if (!_schedulingData.IsNodeWorking(nodeId))
                            {
                                file.WriteLine("Top-level requests causing this node to be idle:");

                                if (scheduledRequestsByNode.Count == 0)
                                {
                                    file.WriteLine("  Node is idle because there is no work available for this node to do.");
                                    file.WriteLine();
                                }
                                else
                                {
                                    Queue<SchedulableRequest> blockingRequests = new Queue<SchedulableRequest>();
                                    HashSet<SchedulableRequest> topLevelBlockingRequests = new HashSet<SchedulableRequest>();
                                    foreach (SchedulableRequest request in scheduledRequestsByNode)
                                    {
                                        if (request.RequestsWeAreBlockedByCount > 0)
                                        {
                                            foreach (SchedulableRequest blockingRequest in request.RequestsWeAreBlockedBy)
                                            {
                                                blockingRequests.Enqueue(blockingRequest);
                                            }
                                        }
                                    }

                                    while (blockingRequests.Count > 0)
                                    {
                                        SchedulableRequest request = blockingRequests.Dequeue();

                                        if (request.RequestsWeAreBlockedByCount > 0)
                                        {
                                            foreach (SchedulableRequest blockingRequest in request.RequestsWeAreBlockedBy)
                                            {
                                                blockingRequests.Enqueue(blockingRequest);
                                            }
                                        }
                                        else
                                        {
                                            topLevelBlockingRequests.Add(request);
                                        }
                                    }

                                    foreach (SchedulableRequest request in topLevelBlockingRequests)
                                    {
                                        DumpRequestState(file, request, 1);
                                        file.WriteLine();
                                    }
                                }
                            }
                        }

                        if (_schedulingData.UnscheduledRequestsCount == 0)
                        {
                            file.WriteLine("No unscheduled requests.");
                        }
                        else
                        {
                            file.WriteLine("Unscheduled requests:");

                            foreach (SchedulableRequest request in _schedulingData.UnscheduledRequests)
                            {
                                DumpRequestState(file, request, 1);
                            }
                        }

                        file.WriteLine();
                    }
                    catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                    {
                        // Ignore exceptions
                    }
                }
            }
        }

        /// <summary>
        /// Dumps all of the configurations.
        /// </summary>
        private void DumpConfigurations()
        {
            if (_debugDumpState)
            {
                if (_schedulingData != null)
                {
                    try
                    {
                        using StreamWriter file = FileUtilities.OpenWrite(string.Format(CultureInfo.CurrentCulture, Path.Combine(_debugDumpPath, "SchedulerState_{0}.txt"), EnvironmentUtilities.CurrentProcessId), append: true);

                        file.WriteLine("Configurations used during this build");
                        file.WriteLine("-------------------------------------");

                        List<int> configurations = new List<int>(_schedulingData.Configurations);
                        configurations.Sort();

                        foreach (int config in configurations)
                        {
                            file.WriteLine("Config {0} Node {1} TV: {2} File {3}", config, _schedulingData.GetAssignedNodeForRequestConfiguration(config), _configCache[config].ToolsVersion, _configCache[config].ProjectFullPath);
                            foreach (ProjectPropertyInstance property in _configCache[config].GlobalProperties)
                            {
                                file.WriteLine("{0} = \"{1}\"", property.Name, property.EvaluatedValue);
                            }

                            file.WriteLine();
                        }

                        file.Flush();
                    }
                    catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                    {
                        // Ignore exceptions
                    }
                }
            }
        }

        /// <summary>
        /// Dumps all of the requests.
        /// </summary>
        private void DumpRequests()
        {
            if (_debugDumpState)
            {
                if (_schedulingData != null)
                {
                    try
                    {
                        using StreamWriter file = FileUtilities.OpenWrite(string.Format(CultureInfo.CurrentCulture, Path.Combine(_debugDumpPath, "SchedulerState_{0}.txt"), EnvironmentUtilities.CurrentProcessId), append: true);

                        file.WriteLine("Requests used during the build:");
                        file.WriteLine("-------------------------------");
                        file.WriteLine("Format: GlobalRequestId: [NodeId] FinalState (ConfigId) Path (Targets)");
                        DumpRequestHierarchy(file, null, 0);
                        file.Flush();
                    }
                    catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                    {
                        // Ignore exceptions
                    }
                }
            }
        }

        /// <summary>
        /// Dumps the hierarchy of requests.
        /// </summary>
        private void DumpRequestHierarchy(StreamWriter file, SchedulableRequest root, int indent)
        {
            foreach (SchedulableRequest child in _schedulingData.GetRequestsByHierarchy(root))
            {
                DumpRequestSpec(file, child, indent, null);
                DumpRequestHierarchy(file, child, indent + 1);
            }
        }

        /// <summary>
        /// Dumps the state of a request.
        /// </summary>
        private void DumpRequestState(StreamWriter file, SchedulableRequest request, int indent)
        {
            DumpRequestSpec(file, request, indent, null);
            if (request.RequestsWeAreBlockedByCount > 0)
            {
                foreach (SchedulableRequest blockingRequest in request.RequestsWeAreBlockedBy)
                {
                    DumpRequestSpec(file, blockingRequest, indent + 1, "!");
                }
            }

            if (request.RequestsWeAreBlockingCount > 0)
            {
                foreach (SchedulableRequest blockedRequest in request.RequestsWeAreBlocking)
                {
                    DumpRequestSpec(file, blockedRequest, indent + 1, ">");
                }
            }
        }

        /// <summary>
        /// Dumps detailed information about a request.
        /// </summary>
        private void DumpRequestSpec(StreamWriter file, SchedulableRequest request, int indent, string prefix)
        {
            var buildRequest = request.BuildRequest;

            file.WriteLine(
                "{0}{1}{2}: [{3}] {4}{5} ({6}){7} ({8})",
                new string(' ', indent * 2),
                prefix ?? "",
                buildRequest.GlobalRequestId,
                _schedulingData.GetAssignedNodeForRequestConfiguration(buildRequest.ConfigurationId),
                _schedulingData.IsRequestScheduled(request)
                    ? "RUNNING "
                    : "",
                request.State,
                buildRequest.ConfigurationId,
                _configCache[buildRequest.ConfigurationId].ProjectFullPath,
                string.Join(", ", buildRequest.Targets.ToArray()));
        }

        /// <summary>
        /// Write out the scheduling information so the next time we can read the plan back in and use it.
        /// </summary>
        private void WriteSchedulingPlan(int submissionId)
        {
            SchedulingPlan plan = new SchedulingPlan(_configCache, _schedulingData);
            plan.WritePlan(submissionId, _componentHost.LoggingService, new BuildEventContext(submissionId, 0, 0, 0, 0, 0));
        }

        /// <summary>
        /// Retrieves the scheduling plan from the previous run.
        /// </summary>
        private void ReadSchedulingPlan(int submissionId)
        {
            _schedulingPlan = new SchedulingPlan(_configCache, _schedulingData);
            _schedulingPlan.ReadPlan(submissionId, _componentHost.LoggingService, new BuildEventContext(submissionId, 0, 0, 0, 0, 0));
        }

        #endregion
    }
}
