﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.BackEnd.Components.Logging;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Build.BackEnd.SdkResolution
{
    /// <summary>
    /// An implementation of <see cref="ISdkResolverService"/> that is hosted in the main node for multi-proc builds.  This instance of the service
    /// listens for requests from out-of-proc nodes so that SDK resolution is handled in a central location.  This instance is registered in <see cref="BuildComponentFactoryCollection.RegisterDefaultFactories"/>
    /// and can be overridden for different contexts.  This service calls the <see cref="SdkResolverService"/> to do any actual SDK resolution
    /// because the <see cref="SdkResolverService"/> is used for stand-alone evaluations where there is no build context available so caching
    /// is not an option.
    ///
    /// Since this object is a registered <see cref="IBuildComponent"/>, it is a singleton for the main process.  To get an instance of it, you
    /// must have access to an <see cref="IBuildComponentHost"/> and call <see cref="IBuildComponentHost.GetComponent"/> and pass <see cref="BuildComponentType.SdkResolverService"/>.
    /// </summary>
    internal sealed class MainNodeSdkResolverService : HostedSdkResolverServiceBase
    {
        /// <summary>
        /// Stores the cache in a set of concurrent dictionaries.  The main dictionary is by build submission ID and the inner dictionary contains a case-insensitive SDK name and the cached <see cref="SdkResult"/>.
        /// </summary>
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, SdkResult>> _cache = new ConcurrentDictionary<int, ConcurrentDictionary<string, SdkResult>>();

        /// <summary>
        /// An object used for locking in this class instance.
        /// </summary>
        private readonly object _lockObject = new object();

        /// <summary>
        /// A <see cref="Task"/> running in the background which handles requests from remote nodes.
        /// </summary>
        private Task _requestHandler;

        /// <summary>
        /// An event which is signaled when a request is received from a remote host.
        /// </summary>
        private ManualResetEvent _requestReceivedEvent;

        /// <summary>
        /// A list of requests from remote hosts which need to be processed.
        /// </summary>
        private ConcurrentQueue<SdkResolverRequest> _requests;

        /// <summary>
        /// A factory which is registered to create an instance of this class.
        /// </summary>
        public static IBuildComponent CreateComponent(BuildComponentType type)
        {
            return new MainNodeSdkResolverService();
        }

        /// <inheritdoc cref="ISdkResolverService.ClearCache"/>
        public override void ClearCache(int submissionId)
        {
            ConcurrentDictionary<string, SdkResult> entry;

            _cache.TryRemove(submissionId, out entry);
        }

        /// <inheritdoc cref="INodePacketHandler.PacketReceived"/>
        public override void PacketReceived(int node, INodePacket packet)
        {
            switch (packet.Type)
            {
                case NodePacketType.ResolveSdkRequest:
                    HandleRequest(node, packet as SdkResolverRequest);
                    break;
            }
        }

        /// <inheritdoc cref="ISdkResolverService.ResolveSdk"/>
        public override string ResolveSdk(int submissionId, SdkReference sdk, LoggingContext loggingContext, ElementLocation sdkReferenceLocation, string solutionPath, string projectPath)
        {
            SdkResult result = GetSdkResult(submissionId, sdk, loggingContext, sdkReferenceLocation, solutionPath, projectPath);

            return result?.Path;
        }

        /// <summary>
        /// Resolves the specified SDK.
        /// </summary>
        /// <param name="submissionId">The current build submission ID that is resolving an SDK.</param>
        /// <param name="sdk">The <see cref="SdkReference"/> containing information about the SDK to resolve.</param>
        /// <param name="loggingContext">The <see cref="LoggingContext"/> to use when logging messages during resolution.</param>
        /// <param name="sdkReferenceLocation">The <see cref="ElementLocation"/> of the element that referenced the SDK.</param>
        /// <param name="solutionPath">The full path to the solution, if any, that is being built.</param>
        /// <param name="projectPath">The full path to the project that referenced the SDK.</param>
        /// <returns>An <see cref="SdkResult"/> containing information about the SDK if one was resolved, otherwise <code>null</code>.</returns>
        private SdkResult GetSdkResult(int submissionId, SdkReference sdk, LoggingContext loggingContext, ElementLocation sdkReferenceLocation, string solutionPath, string projectPath)
        {
            ErrorUtilities.VerifyThrowInternalNull(sdk, nameof(sdk));
            ErrorUtilities.VerifyThrowInternalNull(loggingContext, nameof(loggingContext));
            ErrorUtilities.VerifyThrowInternalNull(sdkReferenceLocation, nameof(sdkReferenceLocation));

            // Get the dictionary for the specified submission if one is already added otherwise create a new dictionary for the submission.
            ConcurrentDictionary<string, SdkResult> cached = _cache.GetOrAdd(submissionId, new ConcurrentDictionary<string, SdkResult>(MSBuildNameIgnoreCaseComparer.Default));

            // Get a cached result if available, otherwise resolve the SDK with the SdkResolverService.Instance
            SdkResult result = cached.GetOrAdd(
                sdk.Name,
                key =>
                {
                    SdkResult sdkResult = SdkResolverService.Instance.GetSdkResult(sdk, loggingContext, sdkReferenceLocation, solutionPath, projectPath);

                    // Associate the element location of the resolved SDK reference
                    sdkResult.ElementLocation = sdkReferenceLocation;

                    return sdkResult;
                });

            if (!SdkResolverService.IsReferenceSameVersion(sdk, result.Version))
            {
                // MSB4240: Multiple versions of the same SDK "{0}" cannot be specified. The SDK version already specified at "{1}" will be used and the version will be "{2}" ignored.
                loggingContext.LogWarning(null, new BuildEventFileInfo(sdkReferenceLocation), "ReferencingMultipleVersionsOfTheSameSdk", sdk.Name, result.ElementLocation, sdk.Version);
            }

            return result;
        }

        /// <summary>
        /// Handles a request from a remote node.
        /// </summary>
        /// <param name="node">The ID of the remote node.</param>
        /// <param name="request">The <see cref="SdkResolverRequest"/> containing information about the SDK to resolve.</param>
        /// <remarks>This method must not directly handle requests because it would block requests from other nodes.  Instead, it simply
        /// adds requests to a queue which are processed by a background thread.</remarks>
        private void HandleRequest(int node, SdkResolverRequest request)
        {
            // Start the background thread which will process queued requests if it has not already been started.
            lock (_lockObject)
            {
                if (_requestHandler == null)
                {
                    // Create the event used to signal that a request was received
                    _requestReceivedEvent = new ManualResetEvent(initialState: false);

                    // Create the queue used to store requests that need to be processed
                    _requests = new ConcurrentQueue<SdkResolverRequest>();

                    // Create the thread which processes requests
                    _requestHandler = Task.Run((Action)RequestHandlerPumpProc);
                }
            }

            // Associate the node with the request
            request.NodeId = node;

            _requests.Enqueue(request);

            // Signal that one or more requests have been received
            _requestReceivedEvent.Set();
        }

        /// <summary>
        /// Processes all requests that are currently in the queue.
        /// </summary>
        private void ProcessRequests()
        {
            // Store a list of threads which are resolving SDKs
            List<Task> tasks = new List<Task>(_requests.Count);

            SdkResolverRequest item;

            while (_requests.TryDequeue(out item))
            {
                SdkResolverRequest request = item;

                // Start a thread to resolve an SDK and add it to the list of threads
                tasks.Add(Task.Run(() =>
                {
                    // Create an SdkReference from the request
                    SdkReference sdkReference = new SdkReference(request.Name, request.Version, request.MinimumVersion);

                    ILoggingService loggingService = Host.GetComponent(BuildComponentType.LoggingService) as ILoggingService;

                    // This call is usually cached so is very fast but can take longer for a new SDK that is downloaded.  Other queued threads for different SDKs will complete sooner and continue on which unblocks evaluations
                    SdkResult result = GetSdkResult(request.SubmissionId, sdkReference, new EvaluationLoggingContext(loggingService, request.BuildEventContext, request.ProjectPath), request.ElementLocation, request.SolutionPath, request.ProjectPath);

                    // Create a response
                    SdkResolverResponse response = new SdkResolverResponse(result.Path, result.Version);

                    // Get the node manager and send the response back to the node that requested the SDK
                    INodeManager nodeManager = Host.GetComponent(BuildComponentType.NodeManager) as INodeManager;

                    nodeManager.SendData(request.NodeId, response);
                }));
            }

            // Wait for all tasks to complete
            Task.WaitAll(tasks.ToArray());
        }

        /// <summary>
        /// A background thread that waits for requests to be received.
        /// </summary>
        private void RequestHandlerPumpProc()
        {
            try
            {
                while (true)
                {
                    WaitHandle[] handles = new WaitHandle[] { ShutdownEvent, _requestReceivedEvent };

                    int waitId = WaitHandle.WaitAny(handles);
                    switch (waitId)
                    {
                        case 0:
                            return;

                        case 1:
                            _requestReceivedEvent.Reset();

                            ProcessRequests();
                            break;

                        default:
                            ErrorUtilities.ThrowInternalError("waitId {0} out of range.", waitId);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                ExceptionHandling.DumpExceptionToFile(e);
                throw;
            }
        }
    }
}
