// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Components.Caching;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Evaluation;
using Microsoft.Build.FileAccesses;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// This class represents an implementation of INode for out-of-proc nodes.
    /// </summary>
    public class OutOfProcNode : INode, IBuildComponentHost, INodePacketFactory, INodePacketHandler
    {
        /// <summary>
        /// Whether the current appdomain has an out of proc node.
        /// For diagnostics.
        /// </summary>
        private static bool s_isOutOfProcNode;

        /// <summary>
        /// The one and only project root element cache to be used for the build
        /// on this out of proc node.
        /// </summary>
        private static ProjectRootElementCacheBase s_projectRootElementCacheBase;

        /// <summary>
        /// The endpoint used to talk to the host.
        /// </summary>
        private NodeEndpointOutOfProc _nodeEndpoint;

        /// <summary>
        /// The saved environment for the process.
        /// </summary>
        private IDictionary<string, string> _savedEnvironment;

        /// <summary>
        /// The component factories.
        /// </summary>
        private readonly BuildComponentFactoryCollection _componentFactories;

        /// <summary>
        /// The build system parameters.
        /// </summary>
        private BuildParameters _buildParameters;

        /// <summary>
        /// The logging service.
        /// </summary>
        private ILoggingService _loggingService;

        /// <summary>
        /// The node logging context.
        /// </summary>
        private NodeLoggingContext _loggingContext;

        /// <summary>
        /// The global config cache.
        /// </summary>
        private readonly IConfigCache _globalConfigCache;

        /// <summary>
        /// The global node manager
        /// </summary>
        private readonly INodeManager _taskHostNodeManager;

        /// <summary>
        /// The build request engine.
        /// </summary>
        private readonly IBuildRequestEngine _buildRequestEngine;

        /// <summary>
        /// The packet factory.
        /// </summary>
        private readonly NodePacketFactory _packetFactory;

        /// <summary>
        /// The current node configuration
        /// </summary>
        private NodeConfiguration _currentConfiguration;

        /// <summary>
        /// The queue of packets we have received but which have not yet been processed.
        /// </summary>
        private readonly ConcurrentQueue<INodePacket> _receivedPackets;

        /// <summary>
        /// The event which is set when we receive packets.
        /// </summary>
        private readonly AutoResetEvent _packetReceivedEvent;

        /// <summary>
        /// The event which is set when we should shut down.
        /// </summary>
        private readonly ManualResetEvent _shutdownEvent;

        /// <summary>
        /// The reason we are shutting down.
        /// </summary>
        private NodeEngineShutdownReason _shutdownReason;

        /// <summary>
        /// The exception, if any, which caused shutdown.
        /// </summary>
        private Exception _shutdownException;

        /// <summary>
        /// Data for the use of LegacyThreading semantics.
        /// </summary>
        private readonly LegacyThreadingData _legacyThreadingData;

        /// <summary>
        /// The current <see cref="ISdkResolverService"/> instance.
        /// </summary>
        private readonly ISdkResolverService _sdkResolverService;

        /// <summary>
        /// Constructor.
        /// </summary>
        public OutOfProcNode()
        {
            s_isOutOfProcNode = true;

            _receivedPackets = new ConcurrentQueue<INodePacket>();
            _packetReceivedEvent = new AutoResetEvent(false);
            _shutdownEvent = new ManualResetEvent(false);
            _legacyThreadingData = new LegacyThreadingData();

            _componentFactories = new BuildComponentFactoryCollection(this);
            _componentFactories.RegisterDefaultFactories();
            SerializationContractInitializer.Initialize();
            _packetFactory = new NodePacketFactory();

            _buildRequestEngine = (this as IBuildComponentHost).GetComponent(BuildComponentType.RequestEngine) as IBuildRequestEngine;
            _globalConfigCache = (this as IBuildComponentHost).GetComponent(BuildComponentType.ConfigCache) as IConfigCache;
            _taskHostNodeManager = (this as IBuildComponentHost).GetComponent(BuildComponentType.TaskHostNodeManager) as INodeManager;

            // Create a factory for the out-of-proc SDK resolver service which can pass our SendPacket delegate to be used for sending packets to the main node
            OutOfProcNodeSdkResolverServiceFactory sdkResolverServiceFactory = new OutOfProcNodeSdkResolverServiceFactory(SendPacket);
            ((IBuildComponentHost)this).RegisterFactory(BuildComponentType.SdkResolverService, sdkResolverServiceFactory.CreateInstance);
            _sdkResolverService = (this as IBuildComponentHost).GetComponent(BuildComponentType.SdkResolverService) as ISdkResolverService;

#if FEATURE_REPORTFILEACCESSES
            ((IBuildComponentHost)this).RegisterFactory(
                BuildComponentType.FileAccessManager,
                (componentType) => OutOfProcNodeFileAccessManager.CreateComponent(componentType, SendPacket));
#endif

            if (s_projectRootElementCacheBase == null)
            {
                s_projectRootElementCacheBase = new ProjectRootElementCache(true /* automatically reload any changes from disk */);
            }

            _buildRequestEngine.OnEngineException += OnEngineException;
            _buildRequestEngine.OnNewConfigurationRequest += OnNewConfigurationRequest;
            _buildRequestEngine.OnRequestBlocked += OnNewRequest;
            _buildRequestEngine.OnRequestComplete += OnRequestComplete;
            _buildRequestEngine.OnResourceRequest += OnResourceRequest;

            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.BuildRequest, BuildRequest.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.BuildRequestConfiguration, BuildRequestConfiguration.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.BuildRequestConfigurationResponse, BuildRequestConfigurationResponse.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.BuildRequestUnblocker, BuildRequestUnblocker.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.NodeConfiguration, NodeConfiguration.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.NodeBuildComplete, NodeBuildComplete.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.ResourceResponse, ResourceResponse.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.ResolveSdkResponse, SdkResult.FactoryForDeserialization, _sdkResolverService as INodePacketHandler);
        }

        /// <summary>
        /// Get the logging service for a build.
        /// </summary>
        /// <returns>The logging service.</returns>
        ILoggingService IBuildComponentHost.LoggingService => _loggingService;

        /// <summary>
        /// Retrieves the LegacyThreadingData associated with a particular build manager
        /// </summary>
        LegacyThreadingData IBuildComponentHost.LegacyThreadingData => _legacyThreadingData;

        /// <summary>
        /// Retrieves the name of this component host.
        /// </summary>
        string IBuildComponentHost.Name => "OutOfProc";

        /// <summary>
        /// Retrieves the build parameters for the current build.
        /// </summary>
        /// <returns>The build parameters.</returns>
        BuildParameters IBuildComponentHost.BuildParameters => _buildParameters;

        /// <summary>
        /// Whether the current appdomain has an out of proc node.
        /// </summary>
        internal static bool IsOutOfProcNode => s_isOutOfProcNode;

        #region INode Members

        /// <summary>
        /// Starts up the node and processes messages until the node is requested to shut down.
        /// Assumes no node reuse.
        /// Assumes low priority is disabled.
        /// </summary>
        /// <param name="shutdownException">The exception which caused shutdown, if any.</param>
        /// <returns>The reason for shutting down.</returns>
        public NodeEngineShutdownReason Run(out Exception shutdownException)
        {
            return Run(false, false, out shutdownException);
        }

        /// <summary>
        /// Starts up the node and processes messages until the node is requested to shut down.
        /// Assumes low priority is disabled.
        /// </summary>
        /// <param name="enableReuse">Whether this node is eligible for reuse later.</param>
        /// <param name="shutdownException">The exception which caused shutdown, if any.</param>
        /// <returns>The reason for shutting down.</returns>
        public NodeEngineShutdownReason Run(bool enableReuse, out Exception shutdownException)
        {
            return Run(enableReuse, false, out shutdownException);
        }

        /// <summary>
        /// Starts up the node and processes messages until the node is requested to shut down.
        /// </summary>
        /// <param name="enableReuse">Whether this node is eligible for reuse later.</param>
        /// <param name="lowPriority">Whether this node should be running with low priority.</param>
        /// <param name="shutdownException">The exception which caused shutdown, if any.</param>
        /// <returns>The reason for shutting down.</returns>
        public NodeEngineShutdownReason Run(bool enableReuse, bool lowPriority, out Exception shutdownException)
        {
            _nodeEndpoint = new NodeEndpointOutOfProc(enableReuse, lowPriority);
            _nodeEndpoint.OnLinkStatusChanged += OnLinkStatusChanged;
            _nodeEndpoint.Listen(this);

            var waitHandles = new WaitHandle[] { _shutdownEvent, _packetReceivedEvent };

            // Get the current directory before doing any work. We need this so we can restore the directory when the node shutsdown.
            while (true)
            {
                int index = WaitHandle.WaitAny(waitHandles);
                switch (index)
                {
                    case 0:
                        NodeEngineShutdownReason shutdownReason = HandleShutdown(out shutdownException);
                        return shutdownReason;

                    case 1:

                        while (_receivedPackets.TryDequeue(out INodePacket packet))
                        {
                            if (packet != null)
                            {
                                HandlePacket(packet);
                            }
                        }

                        break;
                }
            }

            // UNREACHABLE
        }

        #endregion

        #region IBuildComponentHost Members

        /// <summary>
        /// Registers a factory with the component host.
        /// </summary>
        /// <param name="factoryType">The factory type to register.</param>
        /// <param name="factory">The factory method.</param>
        void IBuildComponentHost.RegisterFactory(BuildComponentType factoryType, BuildComponentFactoryDelegate factory)
        {
            _componentFactories.ReplaceFactory(factoryType, factory);
        }

        /// <summary>
        /// Get a component from the host.
        /// </summary>
        /// <param name="type">The component type to get.</param>
        /// <returns>The component.</returns>
        IBuildComponent IBuildComponentHost.GetComponent(BuildComponentType type)
        {
            return _componentFactories.GetComponent(type);
        }

        TComponent IBuildComponentHost.GetComponent<TComponent>(BuildComponentType type)
            => (TComponent)((IBuildComponentHost)this).GetComponent(type);

        #endregion

        #region INodePacketFactory Members

        /// <summary>
        /// Registers a packet handler.
        /// </summary>
        /// <param name="packetType">The packet type for which the handler should be registered.</param>
        /// <param name="factory">The factory used to create packets.</param>
        /// <param name="handler">The handler for the packets.</param>
        void INodePacketFactory.RegisterPacketHandler(NodePacketType packetType, NodePacketFactoryMethod factory, INodePacketHandler handler)
        {
            _packetFactory.RegisterPacketHandler(packetType, factory, handler);
        }

        /// <summary>
        /// Unregisters a packet handler.
        /// </summary>
        /// <param name="packetType">The type of packet for which the handler should be unregistered.</param>
        void INodePacketFactory.UnregisterPacketHandler(NodePacketType packetType)
        {
            _packetFactory.UnregisterPacketHandler(packetType);
        }

        /// <summary>
        /// Deserializes and routes a packer to the appropriate handler.
        /// </summary>
        /// <param name="nodeId">The node from which the packet was received.</param>
        /// <param name="packetType">The packet type.</param>
        /// <param name="translator">The translator to use as a source for packet data.</param>
        void INodePacketFactory.DeserializeAndRoutePacket(int nodeId, NodePacketType packetType, ITranslator translator)
        {
            _packetFactory.DeserializeAndRoutePacket(nodeId, packetType, translator);
        }

        /// <summary>
        /// Routes a packet to the appropriate handler.
        /// </summary>
        /// <param name="nodeId">The node id from which the packet was received.</param>
        /// <param name="packet">The packet to route.</param>
        void INodePacketFactory.RoutePacket(int nodeId, INodePacket packet)
        {
            _packetFactory.RoutePacket(nodeId, packet);
        }

        #endregion

        #region INodePacketHandler Members

        /// <summary>
        /// Called when a packet has been received.
        /// </summary>
        /// <param name="node">The node from which the packet was received.</param>
        /// <param name="packet">The packet.</param>
        void INodePacketHandler.PacketReceived(int node, INodePacket packet)
        {
            _receivedPackets.Enqueue(packet);
            _packetReceivedEvent.Set();
        }

        #endregion

        /// <summary>
        /// Event handler for the BuildEngine's OnRequestComplete event.
        /// </summary>
        private void OnRequestComplete(BuildRequest request, BuildResult result)
        {
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                _nodeEndpoint.SendData(result);
            }

#if FEATURE_REPORTFILEACCESSES
            if (_buildParameters.ReportFileAccesses)
            {
                FileAccessManager.NotifyFileAccessCompletion(result.GlobalRequestId);
            }
#endif
        }

        /// <summary>
        /// Event handler for the BuildEngine's OnNewRequest event.
        /// </summary>
        private void OnNewRequest(BuildRequestBlocker blocker)
        {
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                _nodeEndpoint.SendData(blocker);
            }
        }

        /// <summary>
        /// Event handler for the BuildEngine's OnNewConfigurationRequest event.
        /// </summary>
        private void OnNewConfigurationRequest(BuildRequestConfiguration config)
        {
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                _nodeEndpoint.SendData(config);
            }
        }

        /// <summary>
        /// Event handler for the BuildEngine's OnResourceRequest event.
        /// </summary>
        private void OnResourceRequest(ResourceRequest request)
        {
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                _nodeEndpoint.SendData(request);
            }
        }

        /// <summary>
        /// Event handler for the LoggingService's OnLoggingThreadException event.
        /// </summary>
        private void OnLoggingThreadException(Exception e)
        {
            OnEngineException(e);
        }

        /// <summary>
        /// Event handler for the BuildEngine's OnEngineException event.
        /// </summary>
        private void OnEngineException(Exception e)
        {
            _shutdownException = e;
            _shutdownReason = NodeEngineShutdownReason.Error;
            _shutdownEvent.Set();
        }

        /// <summary>
        /// Perform necessary actions to shut down the node.
        /// </summary>
        private NodeEngineShutdownReason HandleShutdown(out Exception exception)
        {
            CommunicationsUtilities.Trace("Shutting down with reason: {0}, and exception: {1}.", _shutdownReason, _shutdownException);

            // Clean up the engine
            if (_buildRequestEngine != null && _buildRequestEngine.Status != BuildRequestEngineStatus.Uninitialized)
            {
                _buildRequestEngine.CleanupForBuild();

                if (_shutdownReason == NodeEngineShutdownReason.BuildCompleteReuse)
                {
                    ((IBuildComponent)_buildRequestEngine).ShutdownComponent();
                }
            }

            // Signal the SDK resolver service to shutdown
            ((IBuildComponent)_sdkResolverService).ShutdownComponent();

            // Dispose of any build registered objects
            IRegisteredTaskObjectCache objectCache = (IRegisteredTaskObjectCache)(_componentFactories.GetComponent(BuildComponentType.RegisteredTaskObjectCache));
            objectCache.DisposeCacheObjects(RegisteredTaskObjectLifetime.Build);

            if (_shutdownReason != NodeEngineShutdownReason.BuildCompleteReuse)
            {
                // Dispose of any node registered objects.
                ((IBuildComponent)objectCache).ShutdownComponent();
            }

            // Shutdown any Out Of Proc Nodes Created
            _taskHostNodeManager.ShutdownConnectedNodes(_shutdownReason == NodeEngineShutdownReason.BuildCompleteReuse);

            // On Windows, a process holds a handle to the current directory,
            // so reset it away from a user-requested folder that may get deleted.
            NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory);

            // Restore the original environment, best effort.
            // If the node was never configured, this will be null.
            if (_savedEnvironment != null)
            {
                try
                {
                    CommunicationsUtilities.SetEnvironment(_savedEnvironment);
                }
                catch (Exception ex)
                {
                    CommunicationsUtilities.Trace("Failed to restore the original environment: {0}.", ex);
                }
                Traits.UpdateFromEnvironment();
            }
            try
            {
                // Shut down logging, which will cause all queued logging messages to be sent.
                if (_loggingContext != null && _loggingService != null)
                {
                    _loggingContext.LogBuildFinished(true);
                    ((IBuildComponent)_loggingService).ShutdownComponent();
                }
            }
            finally
            {
                // Shut down logging, which will cause all queued logging messages to be sent.
                if (_loggingContext != null && _loggingService != null)
                {
                    _loggingContext.LoggingService.OnLoggingThreadException -= OnLoggingThreadException;
                    _loggingContext = null;
                }

                exception = _shutdownException;

                if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
                {
                    // Notify the BuildManager that we are done.
                    _nodeEndpoint.SendData(new NodeShutdown(_shutdownReason == NodeEngineShutdownReason.Error ? NodeShutdownReason.Error : NodeShutdownReason.Requested, exception));

                    // Flush all packets to the pipe and close it down.  This blocks until the shutdown is complete.
                    _nodeEndpoint.OnLinkStatusChanged -= OnLinkStatusChanged;
                }

                _nodeEndpoint.Disconnect();
                CleanupCaches();
            }

            CommunicationsUtilities.Trace("Shut down complete.");

            return _shutdownReason;
        }

        /// <summary>
        /// Clears all the caches used during the build.
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect", Justification = "Required because when calling this method, we want the memory back NOW.")]
        private void CleanupCaches()
        {
            if (_componentFactories.GetComponent(BuildComponentType.ConfigCache) is IConfigCache configCache)
            {
                configCache.ClearConfigurations();
            }

            if (_componentFactories.GetComponent(BuildComponentType.ResultsCache) is IResultsCache resultsCache)
            {
                resultsCache.ClearResults();
            }

            if (Environment.GetEnvironmentVariable("MSBUILDCLEARXMLCACHEONCHILDNODES") == "1")
            {
                // Optionally clear out the cache. This has the advantage of releasing memory,
                // but the disadvantage of causing the next build to repeat the load and parse.
                // We'll experiment here and ship with the best default.
                s_projectRootElementCacheBase = null;
            }

            // Since we aren't going to be doing any more work, lets clean up all our memory usage.
            GC.Collect();
        }

        /// <summary>
        /// Event handler for the node endpoint's LinkStatusChanged event.
        /// </summary>
        private void OnLinkStatusChanged(INodeEndpoint endpoint, LinkStatus status)
        {
            switch (status)
            {
                case LinkStatus.ConnectionFailed:
                case LinkStatus.Failed:
                    _shutdownReason = NodeEngineShutdownReason.ConnectionFailed;
                    _shutdownEvent.Set();
                    break;

                case LinkStatus.Inactive:
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Callback for logging packets to be sent.
        /// </summary>
        private void SendPacket(INodePacket packet)
        {
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
#if RUNTIME_TYPE_NETCORE
                if (packet is LogMessagePacketBase logMessage
                    && logMessage.EventType == LoggingEventType.CustomEvent
                    && Traits.Instance.EscapeHatches.EnableWarningOnCustomBuildEvent)
                {
                    BuildEventArgs buildEvent = logMessage.NodeBuildEvent.Value.Value;

                    // Serializing unknown CustomEvent which has to use unsecure BinaryFormatter by TranslateDotNet<T>
                    // Since BinaryFormatter is deprecated in dotnet 8+, log error so users discover root cause easier
                    // then by reading CommTrace where it would be otherwise logged as critical infra error.
                    _loggingService.LogError(_loggingContext?.BuildEventContext ?? BuildEventContext.Invalid, null, BuildEventFileInfo.Empty,
                            "DeprecatedEventSerialization",
                            buildEvent?.GetType().Name ?? string.Empty);
                }
                else
                {
                    _nodeEndpoint.SendData(packet);
                }
#else
                _nodeEndpoint.SendData(packet);
#endif
            }
        }

        /// <summary>
        /// Dispatches the packet to the correct handler.
        /// </summary>
        private void HandlePacket(INodePacket packet)
        {
            // Console.WriteLine("Handling packet {0} at {1}", packet.Type, DateTime.Now);
            switch (packet.Type)
            {
                case NodePacketType.BuildRequest:
                    HandleBuildRequest(packet as BuildRequest);
                    break;

                case NodePacketType.BuildRequestConfiguration:
                    HandleBuildRequestConfiguration(packet as BuildRequestConfiguration);
                    break;

                case NodePacketType.BuildRequestConfigurationResponse:
                    HandleBuildRequestConfigurationResponse(packet as BuildRequestConfigurationResponse);
                    break;

                case NodePacketType.BuildRequestUnblocker:
                    HandleBuildRequestUnblocker(packet as BuildRequestUnblocker);
                    break;

                case NodePacketType.ResourceResponse:
                    HandleResourceResponse(packet as ResourceResponse);
                    break;

                case NodePacketType.NodeConfiguration:
                    HandleNodeConfiguration(packet as NodeConfiguration);
                    break;

                case NodePacketType.NodeBuildComplete:
                    HandleNodeBuildComplete(packet as NodeBuildComplete);
                    break;
            }
        }

        /// <summary>
        /// Handles the BuildRequest packet.
        /// </summary>
        private void HandleBuildRequest(BuildRequest request)
        {
            _buildRequestEngine.SubmitBuildRequest(request);
        }

        /// <summary>
        /// Handles the BuildRequestConfiguration packet.
        /// </summary>
        private void HandleBuildRequestConfiguration(BuildRequestConfiguration configuration)
        {
            _globalConfigCache.AddConfiguration(configuration);
        }

        /// <summary>
        /// Handles the BuildRequestConfigurationResponse packet.
        /// </summary>
        private void HandleBuildRequestConfigurationResponse(BuildRequestConfigurationResponse response)
        {
            _buildRequestEngine.ReportConfigurationResponse(response);
        }

        /// <summary>
        /// Handles the BuildResult packet.
        /// </summary>
        private void HandleBuildRequestUnblocker(BuildRequestUnblocker unblocker)
        {
            _buildRequestEngine.UnblockBuildRequest(unblocker);
        }

        /// <summary>
        /// Handles the ResourceResponse packet.
        /// </summary>
        /// <param name="response"></param>
        private void HandleResourceResponse(ResourceResponse response)
        {
            _buildRequestEngine.GrantResources(response);
        }

        /// <summary>
        /// Handles the NodeConfiguration packet.
        /// </summary>
        private void HandleNodeConfiguration(NodeConfiguration configuration)
        {
            // Grab the system parameters.
            _buildParameters = configuration.BuildParameters;

            _buildParameters.ProjectRootElementCache = s_projectRootElementCacheBase;

            // Snapshot the current environment
            _savedEnvironment = CommunicationsUtilities.GetEnvironmentVariables();

            // Change to the startup directory
            try
            {
                NativeMethodsShared.SetCurrentDirectory(BuildParameters.StartupDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                // Somehow the startup directory vanished. This can happen if build was started from a USB Key and it was removed.
                NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory);
            }

            // Replicate the environment.  First, unset any environment variables set by the previous configuration.
            if (_currentConfiguration != null)
            {
                foreach (string key in _currentConfiguration.BuildParameters.BuildProcessEnvironment.Keys)
                {
                    Environment.SetEnvironmentVariable(key, null);
                }
            }

            // Now set the new environment and update Traits class accordingly
            foreach (KeyValuePair<string, string> environmentPair in _buildParameters.BuildProcessEnvironment)
            {
                Environment.SetEnvironmentVariable(environmentPair.Key, environmentPair.Value);
            }

            Traits.UpdateFromEnvironment();

            // We want to make sure the global project collection has the toolsets which were defined on the parent
            // so that any custom toolsets defined can be picked up by tasks who may use the global project collection but are
            // executed on the child node.
            ICollection<Toolset> parentToolSets = _buildParameters.ToolsetProvider.Toolsets;
            if (parentToolSets != null)
            {
                ProjectCollection.GlobalProjectCollection.RemoveAllToolsets();

                foreach (Toolset toolSet in parentToolSets)
                {
                    ProjectCollection.GlobalProjectCollection.AddToolset(toolSet);
                }
            }

            // Set the culture.
            CultureInfo.CurrentCulture = _buildParameters.Culture;
            CultureInfo.CurrentUICulture = _buildParameters.UICulture;

            // Get the node ID.
            _buildParameters.NodeId = configuration.NodeId;
            _buildParameters.IsOutOfProc = true;

#if FEATURE_APPDOMAIN
            // And the AppDomainSetup
            _buildParameters.AppDomainSetup = configuration.AppDomainSetup;
#endif

            // Set up the logging service.
            LoggingServiceFactory loggingServiceFactory = new LoggingServiceFactory(LoggerMode.Asynchronous, configuration.NodeId);
            _componentFactories.ReplaceFactory(BuildComponentType.LoggingService, loggingServiceFactory.CreateInstance);

            _loggingService = _componentFactories.GetComponent(BuildComponentType.LoggingService) as ILoggingService;

            BuildEventArgTransportSink sink = new BuildEventArgTransportSink(SendPacket);

            _shutdownException = null;

            if (configuration.LoggingNodeConfiguration.IncludeEvaluationMetaprojects)
            {
                _loggingService.IncludeEvaluationMetaprojects = true;
            }

            if (configuration.LoggingNodeConfiguration.IncludeEvaluationProfiles)
            {
                _loggingService.IncludeEvaluationProfile = true;
            }

            if (configuration.LoggingNodeConfiguration.IncludeTaskInputs)
            {
                _loggingService.IncludeTaskInputs = true;
            }

            if (configuration.LoggingNodeConfiguration.IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent)
            {
                _loggingService.SetIncludeEvaluationPropertiesAndItemsInEvents(
                    configuration.LoggingNodeConfiguration.IncludeEvaluationPropertiesAndItemsInProjectStartedEvent,
                    configuration.LoggingNodeConfiguration
                        .IncludeEvaluationPropertiesAndItemsInEvaluationFinishedEvent);
            }

            try
            {
                // If there are no node loggers to initialize dont do anything
                if (configuration.LoggerDescriptions?.Length > 0)
                {
                    _loggingService.InitializeNodeLoggers(configuration.LoggerDescriptions, sink, configuration.NodeId);
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                OnEngineException(ex);
            }

            _loggingService.OnLoggingThreadException += OnLoggingThreadException;

            string forwardPropertiesFromChild = Environment.GetEnvironmentVariable("MSBUILDFORWARDPROPERTIESFROMCHILD");
            string[] propertyListToSerialize = null;

            // Get a list of properties which should be serialized
            if (!String.IsNullOrEmpty(forwardPropertiesFromChild))
            {
                propertyListToSerialize = forwardPropertiesFromChild.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);
            }

            _loggingService.PropertiesToSerialize = propertyListToSerialize;
            _loggingService.RunningOnRemoteNode = true;

            string forwardAllProperties = Environment.GetEnvironmentVariable("MSBUILDFORWARDALLPROPERTIESFROMCHILD");
            if (String.Equals(forwardAllProperties, "1", StringComparison.OrdinalIgnoreCase) || _buildParameters.LogInitialPropertiesAndItems)
            {
                _loggingService.SerializeAllProperties = true;
            }
            else
            {
                _loggingService.SerializeAllProperties = false;
            }

            // Now prep the buildRequestEngine for the build.
            _loggingContext = new NodeLoggingContext(_loggingService, configuration.NodeId, false /* inProcNode */);

            if (_shutdownException != null)
            {
                HandleShutdown(out Exception exception);
                throw exception;
            }

            _buildRequestEngine.InitializeForBuild(_loggingContext);

            // Finally store off this configuration packet.
            _currentConfiguration = configuration;
        }

        /// <summary>
        /// Handles the NodeBuildComplete packet.
        /// </summary>
        private void HandleNodeBuildComplete(NodeBuildComplete buildComplete)
        {
            _shutdownReason = buildComplete.PrepareForReuse ? NodeEngineShutdownReason.BuildCompleteReuse : NodeEngineShutdownReason.BuildComplete;
            if (_shutdownReason == NodeEngineShutdownReason.BuildCompleteReuse)
            {
                using Process currentProcess = Process.GetCurrentProcess();
                ProcessPriorityClass priorityClass = currentProcess.PriorityClass;
                if (priorityClass != ProcessPriorityClass.Normal && priorityClass != ProcessPriorityClass.BelowNormal)
                {
                    // This isn't a priority class known by MSBuild. We should avoid connecting to this node.
                    _shutdownReason = NodeEngineShutdownReason.BuildComplete;
                }
                else
                {
                    bool lowPriority = priorityClass == ProcessPriorityClass.BelowNormal;
                    if (_nodeEndpoint.LowPriority != lowPriority)
                    {
                        if (!lowPriority || NativeMethodsShared.IsWindows)
                        {
                            currentProcess.PriorityClass = lowPriority ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
                        }
                        else
                        {
                            // On *nix, we can't adjust the priority up, so to avoid using this node at the wrong priority, we should not be reused.
                            _shutdownReason = NodeEngineShutdownReason.BuildComplete;
                        }
                    }
                }
            }

            _shutdownEvent.Set();
        }
    }
}
