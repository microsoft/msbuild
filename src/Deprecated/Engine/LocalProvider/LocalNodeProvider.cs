// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// THE ASSEMBLY BUILT FROM THIS SOURCE FILE HAS BEEN DEPRECATED FOR YEARS. IT IS BUILT ONLY TO PROVIDE
// BACKWARD COMPATIBILITY FOR API USERS WHO HAVE NOT YET MOVED TO UPDATED APIS. PLEASE DO NOT SEND PULL
// REQUESTS THAT CHANGE THIS FILE WITHOUT FIRST CHECKING WITH THE MAINTAINERS THAT THE FIX IS REQUIRED.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using System.IO;
using Microsoft.Build.BuildEngine.Shared;
using System.Runtime.InteropServices;
using Microsoft.Build.Shared;

namespace Microsoft.Build.BuildEngine
{
    internal class LocalNodeProvider : INodeProvider
    {
        #region Methods implementing INodeProvider
        public void Initialize
        (
            string configuration,
            IEngineCallback parentEngineCallback,
            BuildPropertyGroup parentGlobalPropertyGroup,
            ToolsetDefinitionLocations toolSetSearchLocations,
            string startupDirectory
        )
        {
            // Get from the environment how long we should wait in seconds for shutdown to complete
            string shutdownTimeoutFromEnvironment = Environment.GetEnvironmentVariable("MSBUILDNODESHUTDOWNTIMEOUT");
            int result;
            if (int.TryParse(shutdownTimeoutFromEnvironment, out result) && result >= 0)
            {
                shutdownTimeout = result;
            }

            this.cpuCount = 1;

            if (configuration != null)
            {
                // Split out the parameter sets based on ;
                string[] parameters = configuration.Split(parameterDelimiters);
                // Go through each of the parameter name value pairs and split them appart
                for (int param = 0; param < parameters.Length; param++)
                {
                    if (parameters[param].Length > 0)
                    {
                        string[] parameterComponents = parameters[param].Split(valueDelimiters);
                        // If there is a name and value associated with the parameter, apply the paramter to the provider
                        if (parameterComponents.Length == 2)
                        {
                            ApplyParameter(parameterComponents[0], parameterComponents[1]);
                        }
                        else // Only the parameter name is known, this could be for a boolean parameter
                        {
                            ApplyParameter(parameters[param], null);
                        }
                    }
                }
            }

            /* If we dont get a path passed in as a parameter, we can only assume that our path
             is in the current appdomain basedirectory, this is the base directory
              that the assembly resolver uses to probe for assemblies
           */
            if (string.IsNullOrEmpty(this.locationOfMSBuildExe))
            {
                this.locationOfMSBuildExe = AppDomain.CurrentDomain.BaseDirectory;
            }
            if ((cpuCount - 1) <= 0)
            {
                return;
            }

            this.exitCommunicationThreads = new ManualResetEvent(false);

            this.activeNodeCount = 0;
            this.responseCountChangeEvent = new ManualResetEvent(false);

            this.nodeStateLock = new object();
            this.nodesToLaunch = new Queue<int>();
            this.nodeLoggers = new List<LoggerDescription>();

            nodeData = new LocalNodeInfo[cpuCount - 1];

            // Initialize the internal state indicating that no nodes have been launched
            int lastUsedNodeNumber = 0;
            for (int i = 0; i < nodeData.Length; i++)
            {
                nodeData[i] = new LocalNodeInfo(lastUsedNodeNumber);
                lastUsedNodeNumber = nodeData[i].NodeNumber + 1;
            }

            // Set up the callback
            this.engineCallback = parentEngineCallback;
            this.parentGlobalProperties = parentGlobalPropertyGroup;
            this.toolsetSearchLocations = toolSetSearchLocations;
            this.startupDirectory = startupDirectory;

            // Default node settings
            centralizedLogging = false;
            onlyLogCriticalEvents = false;
            useBreadthFirstTraversal = true;
            shuttingDown = false;

            // Start the thread that will be processing the calls from the parent engine
            ThreadStart threadState = new ThreadStart(this.SharedMemoryWriterThread);
            Thread taskThread = new Thread(threadState);
            taskThread.Name = "MSBuild Parent->Child Writer";
            taskThread.Start();
            threadState = new ThreadStart(this.SharedMemoryReaderThread);
            taskThread = new Thread(threadState);
            taskThread.Name = "MSBuild Parent<-Child Reader";
            taskThread.Start();
        }

        /// <summary>
        /// Apply a parameter.
        /// </summary>
        public void ApplyParameter(string parameterName, string parameterValue)
        {
            ErrorUtilities.VerifyThrowArgumentNull(parameterName, nameof(parameterName));

            if (String.Equals(parameterName, "MAXCPUCOUNT", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    this.cpuCount = Convert.ToInt32(parameterValue, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    //
                }
                catch (OverflowException)
                {
                    //
                }
            }
            else if (String.Equals(parameterName, "MSBUILDLOCATION", StringComparison.OrdinalIgnoreCase))
            {
                this.locationOfMSBuildExe = parameterValue;
            }
            else if (String.Equals(parameterName, "NODEREUSE", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // There does not seem to be a localizable function for this
                    if (bool.Parse(parameterValue))
                    {
                        this.enableNodeReuse = true;
                    }
                    else
                    {
                        this.enableNodeReuse = false;
                    }
                }
                catch (FormatException)
                {
                    //
                }
                catch (ArgumentNullException)
                {
                    //
                }
            }
        }

        public INodeDescription[] QueryNodeDescriptions()
        {
            return new INodeDescription[cpuCount - 1];
        }

        public void AssignNodeIdentifiers(int[] nodeIds)
        {
            if ((cpuCount - 1) <= 0)
            {
                return;
            }

            ErrorUtilities.VerifyThrow(nodeIds.Length == nodeData.Length, "Expected an ID for each node");

            for (int i = 0; i < nodeIds.Length; i++)
            {
                nodeData[i].NodeId = nodeIds[i];
            }
        }

        public void RegisterNodeLogger(LoggerDescription loggerDescription)
        {
            ErrorUtilities.VerifyThrow(nodeLoggers != null, "Must call Initialize first");
            ErrorUtilities.VerifyThrow(loggerDescription != null, "Logger description should be non-null");

            if (!nodeLoggers.Contains(loggerDescription))
            {
                nodeLoggers.Add(loggerDescription);
            }
        }

        public void RequestNodeStatus(int nodeIndex, int requestId)
        {
            ErrorUtilities.VerifyThrow(nodeLoggers != null, "Must call Initialize first");
            ErrorUtilities.VerifyThrow(nodeIndex < nodeData.Length && nodeIndex >= 0, "Node index must be within array boundaries");

            // If the node has not been launched we need to create a reply
            // on its behalf
            if (nodeData[nodeIndex].NodeState != NodeState.Launched)
            {
                NodeStatus nodeStatus = new NodeStatus(requestId, false, 0, 0, 0, nodeData[nodeIndex].NodeState == NodeState.LaunchInProgress);
                engineCallback.PostStatus(nodeData[nodeIndex].NodeId, nodeStatus, false);
            }
            else if (!IsNodeProcessAliveOrUninitialized(nodeIndex))
            {
                NodeStatus nodeStatus = new NodeStatus(requestId); // Indicate that the node has exited
                engineCallback.PostStatus(nodeData[nodeIndex].NodeId, nodeStatus, false);
            }
            else
            {
                // Send the request to the node
                LocalCallDescriptorForRequestStatus callDescriptor =
                    new LocalCallDescriptorForRequestStatus(requestId);
                nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
            }
        }

        public void PostBuildRequestToNode(int nodeIndex, BuildRequest buildRequest)
        {
            ErrorUtilities.VerifyThrow(nodeIndex < nodeData.Length && nodeIndex >= 0, "Node index must be within array boundaries");

            if (nodeData[nodeIndex].NodeState != NodeState.Launched)
            {
                // Note that we have to check the node status again inside the mutex. This
                // ensures that that after flipping the status to launched inside the mutex
                // there will be no more writes to the queue of targets waiting to be sent
                lock (nodeStateLock)
                {
                    // Check if we didn't initialize this node
                    if (nodeData[nodeIndex].NodeState != NodeState.Launched && !shuttingDown)
                    {
                        // Check if launch is in progress
                        if (nodeData[nodeIndex].NodeState == NodeState.NotLaunched)
                        {
                            nodeData[nodeIndex].NodeState = NodeState.LaunchInProgress;
                            lock (nodesToLaunch)
                            {
                                nodesToLaunch.Enqueue(nodeIndex);
                            }
                            ThreadStart threadState = new ThreadStart(this.LaunchNodeAndPostBuildRequest);
                            Thread taskThread = new Thread(threadState);
                            taskThread.Name = "MSBuild Node Launcher";
                            taskThread.Start();
                        }
                        nodeData[nodeIndex].TargetList.AddFirst(new LinkedListNode<BuildRequest>(buildRequest));
                    }
                    else
                    {
                        LocalCallDescriptorForPostBuildRequests callDescriptor =
                            new LocalCallDescriptorForPostBuildRequests(buildRequest);
                        nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
                    }
                }
            }
            else
            {
                LocalCallDescriptorForPostBuildRequests callDescriptor =
                    new LocalCallDescriptorForPostBuildRequests(buildRequest);
                nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
            }
        }

        public void PostBuildResultToNode(int nodeIndex, BuildResult buildResult)
        {
            ErrorUtilities.VerifyThrow(nodeIndex < nodeData.Length && nodeIndex >= 0, "Node index must be within array boundaries");
            ErrorUtilities.VerifyThrow(nodeData[nodeIndex].NodeState == NodeState.Launched, "Node must be launched before result can be posted");

            LocalCallDescriptorForPostBuildResult callDescriptor =
                    new LocalCallDescriptorForPostBuildResult(buildResult);
            nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
        }

        /// <summary>
        /// Shutdown the nodes which are being tracked and managed by this localNodeProvider.
        /// </summary>
        public void ShutdownNodes(Node.NodeShutdownLevel nodeShutdownLevel)
        {
            // Indicate that nodes should no longer be launched
            shuttingDown = true;

            // Send out shutdown requests to all active  launched nodes
            responseCount = activeNodeCount;
            SendShutdownRequests(nodeShutdownLevel);

            DateTime startTime = DateTime.Now;

            // Wait for all nodes to shutdown
            bool timeoutExpired = false;

            // Loop until we are ready to shutdown. We are ready to shutdown when
            // all nodes either have sent their shutdown completed response or they are dead.
            // Secondly, we will exit the loop if our shudtownTimeout has expired
            TimeSpan shutdownTimeoutSpan = new TimeSpan(0, 0, shutdownTimeout);
            while (!ReadyToShutdown() && !timeoutExpired)
            {
                responseCountChangeEvent.WaitOne(shutdownResponseTimeout, false);
                responseCountChangeEvent.Reset();

                // Timeout when the loop has been executing for more than shutdownTimeout seconds.
                timeoutExpired = DateTime.Now.Subtract(startTime) >= shutdownTimeoutSpan;
            }

            if (timeoutExpired)
            {
                foreach (LocalNodeInfo nodeInfo in nodeData)
                {
                    //Terminate all of the nodes which have valid processId's but for which we
                    // have not recieved a shutdown response
                    if (nodeInfo.ProcessId > 0 && !nodeInfo.ShutdownResponseReceived)
                    {
                        TerminateChildNode(nodeInfo.ProcessId);
                    }
                }
            }

            // Reset the shutdown response received properties incase the nodes are going
            // to be used for another build on the same engine.
            foreach (LocalNodeInfo nodeInfo in nodeData)
            {
                nodeInfo.ShutdownResponseReceived = false;
            }

            // If all nodes are exiting - exit the communication threads
            if (nodeShutdownLevel != Node.NodeShutdownLevel.BuildCompleteSuccess &&
                nodeShutdownLevel != Node.NodeShutdownLevel.BuildCompleteFailure)
            {
                exitCommunicationThreads.Set();
            }

            shuttingDown = false;
        }

        /// <summary>
        /// Determine when the child node has either responsed with a shutdown complete event or the node has died
        /// </summary>
        internal bool ReadyToShutdown()
        {
            for (int i = 0; i < nodeData.Length; i++)
            {
                LocalNodeInfo nodeInfo = nodeData[i];
                // Determine if the node is alive or dead, this check will set the processId to invalid if
                // the process is dead
                IsNodeProcessAliveOrUninitialized(i);
                // If any node is still alive and we have not recieved a shutdown response say we are not ready to shutdown
                if (nodeInfo.ProcessId > 0 && !nodeInfo.ShutdownResponseReceived)
                {
                    return false;
                }
            }
            return true;
        }
        /// <summary>
        /// TEMPORARY
        /// </summary>
        public void UpdateSettings
        (
            bool enableCentralizedLogging,
            bool enableOnlyLogCriticalEvents,
            bool useBreadthFirstTraversalSetting
        )
        {
            this.centralizedLogging = enableCentralizedLogging;
            this.onlyLogCriticalEvents = enableOnlyLogCriticalEvents;
            this.useBreadthFirstTraversal = useBreadthFirstTraversalSetting;

            for (int i = 0; i < nodeData.Length; i++)
            {
                if (nodeData[i].NodeState == NodeState.Launched)
                {
                    UpdateSettings(i);
                }
            }
        }

        private void UpdateSettings(int nodeIndex)
        {
            // Send the updated settings once the node has initialized
            LocalCallDescriptorForUpdateNodeSettings callDescriptor =
                  new LocalCallDescriptorForUpdateNodeSettings(onlyLogCriticalEvents, centralizedLogging, useBreadthFirstTraversal);
            nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
        }

        public void PostIntrospectorCommand(int nodeIndex, TargetInProgessState child, TargetInProgessState parent)
        {
            // Send the updated settings once the node has initialized
            LocalCallDescriptorForPostIntrospectorCommand callDescriptor =
                  new LocalCallDescriptorForPostIntrospectorCommand(child, parent);
            nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Send shutdown message to the launched child nodes.
        /// </summary>
        private void SendShutdownRequests(Node.NodeShutdownLevel nodeShutdownLevel)
        {
            for (int i = 0; i < nodeData.Length; i++)
            {
                // If there is a node launch in progress wait for it complete or fail
                // before shutting down the node
                while (nodeData[i].NodeState == NodeState.LaunchInProgress && !nodeData[i].CommunicationFailed)
                {
                    Thread.Sleep(500);
                }

                if (nodeData[i].NodeState == NodeState.Launched)
                {
                    if (!nodeData[i].CommunicationFailed)
                    {
                        bool exitProcess = !enableNodeReuse;
                        // If we are shutting down due to a BuildComplete then dont kill the nodes as this method will be called again in the engine shutdown method
                        if (nodeShutdownLevel == Node.NodeShutdownLevel.BuildCompleteSuccess || nodeShutdownLevel == Node.NodeShutdownLevel.BuildCompleteFailure)
                        {
                            exitProcess = false;
                        }
                        // Signal to the node to shutdown
                        LocalCallDescriptorForShutdownNode callDescriptor =
                            new LocalCallDescriptorForShutdownNode(nodeShutdownLevel, exitProcess);
                        nodeData[i].NodeCommandQueue.Enqueue(callDescriptor);
                    }
                    else
                    {
                        TerminateChildNode(nodeData[i].ProcessId);
                    }

                    if (nodeShutdownLevel != Node.NodeShutdownLevel.BuildCompleteSuccess &&
                        nodeShutdownLevel != Node.NodeShutdownLevel.BuildCompleteFailure)
                    {
                        nodeData[i].NodeState = NodeState.NotLaunched;
                    }
                }
            }
        }

        /// <summary>
        /// Kill the child process directly if we can't communicate with it
        /// </summary>
        private void TerminateChildNode(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (ArgumentException)
            {
                // The exception indicates that the child process is no longer running
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The exception indicates that the child process is no longer running or
                // the parent cannot access the child process information due to insufficent security permissions
            }
        }

        /// <summary>
        /// Returns true if the process for the given node was started and has not exited
        /// </summary>
        private bool IsNodeProcessAliveOrUninitialized(int nodeId)
        {
            // If it's alive but not being communicated with anymore, that counts as not alive
            if (nodeData[nodeId].CommunicationFailed)
            {
                return false;
            }

            try
            {
                bool isUninitialized = nodeData[nodeId].ProcessId == LocalNodeInfo.unInitializedProcessId;

                if (isUninitialized)
                {
                    return true;
                }

                bool isInvalidProcessId = nodeData[nodeId].ProcessId == LocalNodeInfo.invalidProcessId;

                if (!isInvalidProcessId)
                {
                    using Process process = Process.GetProcessById(nodeData[nodeId].ProcessId);
                    if (!process.HasExited)
                    {
                        return true;
                    }
                    
                }
            }
            catch (ArgumentException)
            {
                // Process already exited

            }

            nodeData[nodeId].ProcessId = LocalNodeInfo.invalidProcessId;
            nodeData[nodeId].CommunicationFailed = true;

            return false;
        }

        private void DecreaseActiveNodeCount(int nodeId)
        {
            int i = 0;
            for (; i < nodeData.Length; i++)
            {
                if (nodeData[i].NodeId == nodeId)
                {
                    nodeData[i].ReleaseNode();
                    Interlocked.Decrement(ref activeNodeCount);
                    break;
                }
            }
            ErrorUtilities.VerifyThrow(i < nodeData.Length, "Expected to find a node to decrement count");
        }

        /// <summary>
        ///  This function is used to increment the count of active nodes
        /// </summary>
        private void IncreaseActiveNodeCount()
        {
            Interlocked.Increment(ref activeNodeCount);
        }

        /// <summary>
        /// This function is used to decrement the count of active nodes
        /// </summary>
        internal void RecordNodeResponse(int nodeId, Node.NodeShutdownLevel shutdownLevel, int totalTaskTime)
        {
            // If the node is shutting down - decrease the count of active nodes
            if (shutdownLevel == Node.NodeShutdownLevel.ErrorShutdown ||
                shutdownLevel == Node.NodeShutdownLevel.PoliteShutdown)
            {
                DecreaseActiveNodeCount(nodeId);
            }

            //Console.WriteLine("Node " + nodeId + " Task Time " + totalTaskTime);

            int i = 0;
            for (; i < nodeData.Length; i++)
            {
                if (nodeData[i].NodeId == nodeId)
                {
                    nodeData[i].ShutdownResponseReceived = true;
                    Interlocked.Decrement(ref responseCount);
                    responseCountChangeEvent.Set();
                    break;
                }
            }
            ErrorUtilities.VerifyThrow(i < nodeData.Length, "Expected to find a node to decrement count");
        }

        /// <summary>
        /// This function is used by the node to set its own processId after it has been initialized
        /// </summary>
        internal void SetNodeProcessId(int processId, int nodeId)
        {
            for (int i = 0; i < nodeData.Length; i++)
            {
                if (nodeData[i].NodeId == nodeId)
                {
                    nodeData[i].ProcessId = processId;
                    break;
                }
            }
        }

        /// <summary>
        /// This function will start a node and send requests to it
        /// </summary>
        private void LaunchNodeAndPostBuildRequest()
        {
            int nodeIndex = 0;

            // Find out what node to launch
            lock (nodesToLaunch)
            {
                nodeIndex = nodesToLaunch.Dequeue();
            }

            // If the provider is shutting down - don't launch the node
            if (shuttingDown)
            {
                nodeData[nodeIndex].NodeState = NodeState.NotLaunched;
                return;
            }

            try
            {
                // Either launch node or connect to an already running node
                InitializeNode(nodeIndex);

                if (!nodeData[nodeIndex].CommunicationFailed)
                {
                    // Change the state of the node to launched
                    lock (nodeStateLock)
                    {
                        nodeData[nodeIndex].NodeState = NodeState.Launched;
                    }

                    // Send all the requests to the node. Note that the requests may end up in
                    // mixed order with the request currently being posted.
                    LinkedListNode<BuildRequest> current = nodeData[nodeIndex].TargetList.First;
                    BuildRequest[] buildRequests = new BuildRequest[nodeData[nodeIndex].TargetList.Count];
                    int i = 0;
                    while (current != null)
                    {
                        buildRequests[i] = current.Value;
                        i++;

                        current = current.Next;
                    }
                    LocalCallDescriptorForPostBuildRequests callDescriptor =
                            new LocalCallDescriptorForPostBuildRequests(buildRequests);
                    nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor);

                    nodeData[nodeIndex].TargetList = null;
                }
                else
                {
                    // Allow the engine to decide how to proceed since the node failed to launch
                    string message = ResourceUtilities.FormatResourceString("NodeProviderFailure");
                    ReportNodeCommunicationFailure(nodeIndex, new Exception(message), false);
                }
            }
            catch (Exception e)
            {
                // Allow the engine to deal with the exception
                ReportNodeCommunicationFailure(nodeIndex, e, false);
            }
        }

        /// <summary>
        /// This function establishes communication with a node given an index. If a node
        /// is not running it is launched.
        /// </summary>
        private void InitializeNode(int nodeIndex)
        {
            bool nodeConnected = false;
            int restartCount = 0;

            try
            {
                IncreaseActiveNodeCount();

                while (!nodeConnected && restartCount < maximumNodeRestartCount)
                {
                    if (!checkIfNodeActive(nodeData[nodeIndex].NodeNumber))
                    {
                        // Attempt to launch a new node process
                        LaunchNode(nodeIndex);
                        // If we could not launch the node there is no reason to continue
                        if (nodeData[nodeIndex].CommunicationFailed)
                        {
                            break;
                        }
                    }

                    if (checkIfNodeActive(nodeData[nodeIndex].NodeNumber))
                    {
                        nodeData[nodeIndex].SharedMemoryToNode.Reset();
                        nodeData[nodeIndex].SharedMemoryFromNode.Reset();

                        // Activate the initiation event to prove to the child that we have the same level of privilege as it does. This operation will not fail because each privilege level creates
                        // events in different namespaces
                        EventWaitHandle nodeInitiateActivationEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeInitiateActivationEventName(nodeData[nodeIndex].NodeNumber));
                        nodeInitiateActivationEvent.Set();
                        nodeInitiateActivationEvent.Close();

                        // Wait for node to indicate that it is activated
                        EventWaitHandle nodeActivatedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeActivedEventName(nodeData[nodeIndex].NodeNumber));
                        nodeActivatedEvent.WaitOne(initializationTimeout, false);
                        nodeActivatedEvent.Close();

                        // Looked in Environment.cs the IDictionary is a HashTable
                        IDictionary variableDictionary = Environment.GetEnvironmentVariables();
                        Hashtable environmentVariablesTable = new Hashtable(variableDictionary);

                        LocalCallDescriptorForInitializeNode callDescriptorInit =
                                new LocalCallDescriptorForInitializeNode(environmentVariablesTable, nodeLoggers.ToArray(), nodeData[nodeIndex].NodeId, parentGlobalProperties, toolsetSearchLocations, EnvironmentUtilities.CurrentProcessId, startupDirectory);
                        nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptorInit);

                        EventWaitHandle nodeInUseEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeInUseEventName(nodeData[nodeIndex].NodeNumber));

                        // Wait for node to indicate that it is ready. The node may time out and exit, between
                        // when we check that it is active and before the initialization messages reaches it.
                        // In that rare case we have to restart the node.
                        if (nodeInUseEvent.WaitOne(initializationTimeout, false))
                        {
                            UpdateSettings(nodeIndex);
                            nodeConnected = true;
                        }
                        nodeInUseEvent.Close();

                        // If the node is still active and has not replied to the initialization message it must
                        // be in bad state - try to get that node to exit
                        if (!nodeConnected && checkIfNodeActive(nodeData[nodeIndex].NodeNumber))
                        {
                            EventWaitHandle nodeShutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeErrorShutdownEventName(nodeData[nodeIndex].NodeNumber));
                            nodeShutdownEvent.Set();
                            nodeShutdownEvent.Close();

                            restartCount = maximumNodeRestartCount;
                        }

                        restartCount++;
                    }
                }
            }
            finally
            {
                // Make sure to decrement the active node count if the communication has failed
                if (!nodeConnected)
                {
                    DecreaseActiveNodeCount(nodeData[nodeIndex].NodeId);
                    nodeData[nodeIndex].CommunicationFailed = true;
                }
            }
        }

        /// <summary>
        /// This function attempts to find out if there is currently a node running
        /// for a given index. The node is running if the global mutex with a
        /// "Node_" + nodeId + "_ActiveReady" as a name was created
        /// </summary>
        private static bool checkIfNodeActive(int nodeNumber)
        {
            bool nodeIsActive = false;
            EventWaitHandle nodeActiveHandle = null;
            try
            {
                nodeActiveHandle = EventWaitHandle.OpenExisting(LocalNodeProviderGlobalNames.NodeActiveEventName(nodeNumber));
                nodeIsActive = true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Assume that the node is not running
            }
            finally
            {
                nodeActiveHandle?.Close();
            }

            return nodeIsActive;
        }

        /// <summary>
        /// This function launches a new node given a node index
        /// </summary>
        private void LaunchNode(int nodeIndex)
        {
            EventWaitHandle nodeReadyEvent = null;

            string msbuildLocation = Path.Combine(locationOfMSBuildExe, "MSBuild.exe");
            ErrorUtilities.VerifyThrow(File.Exists(msbuildLocation), "Msbuild.exe cannot be found at: " + msbuildLocation);

            bool exitedDueToError = true;
            try
            {
                NativeMethods.STARTUPINFO startInfo = new NativeMethods.STARTUPINFO();
                startInfo.cb = Marshal.SizeOf(startInfo);
                uint dwCreationFlags = NativeMethods.NORMAL_PRIORITY_CLASS;
                if (!Engine.debugMode)
                {
                    startInfo.hStdError = NativeMethods.InvalidHandle;
                    startInfo.hStdInput = NativeMethods.InvalidHandle;
                    startInfo.hStdOutput = NativeMethods.InvalidHandle;
                    startInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
                    dwCreationFlags |= NativeMethods.CREATE_NO_WINDOW;
                }

                NativeMethods.SECURITY_ATTRIBUTES pSec = new NativeMethods.SECURITY_ATTRIBUTES();
                NativeMethods.SECURITY_ATTRIBUTES tSec = new NativeMethods.SECURITY_ATTRIBUTES();
                pSec.nLength = Marshal.SizeOf(pSec);
                tSec.nLength = Marshal.SizeOf(tSec);

                NativeMethods.PROCESS_INFORMATION pInfo = new NativeMethods.PROCESS_INFORMATION();

                string appName = msbuildLocation;
                // Repeat the executable name as the first token of the command line because the command line
                // parser logic expects it and will otherwise skip the first argument
                string cmdLine = msbuildLocation + " /nologo /oldom /nodemode:" + nodeData[nodeIndex].NodeNumber;
                NativeMethods.CreateProcess(appName, cmdLine,
                                            ref pSec, ref tSec,
                                            false, dwCreationFlags,
                                            NativeMethods.NullPtr, null, ref startInfo, out pInfo);

                nodeReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeActiveEventName(nodeData[nodeIndex].NodeNumber));

                // Wait until the node is ready to process the requests
                if (nodeReadyEvent.WaitOne(launchTimeout, false))
                {
                    exitedDueToError = false;
                }
            }
            finally
            {
                // Dispose before losing scope
                nodeReadyEvent?.Close();

                if (exitedDueToError)
                {
                    nodeData[nodeIndex].CommunicationFailed = true;
                }
            }
        }

        /// <summary>
        /// Report communication failure and update internal state
        /// </summary>
        private void ReportNodeCommunicationFailure
        (
            int nodeIndex,
            Exception innerException,
            bool decreaseActiveNodeCount
        )
        {
            // Indicate that communication with a particular node has failed
            if (nodeIndex >= 0 && nodeIndex < nodeData.Length)
            {
                if (decreaseActiveNodeCount && !nodeData[nodeIndex].CommunicationFailed)
                {
                    DecreaseActiveNodeCount(nodeData[nodeIndex].NodeId);
                }

                nodeData[nodeIndex].CommunicationFailed = true;
            }

            string message = ResourceUtilities.FormatResourceString("NodeProviderFailure");
            RemoteErrorException wrappedException = new RemoteErrorException(message, innerException, null);
            NodeStatus nodeStatus = new NodeStatus(wrappedException);

            if (nodeIndex < 0 || nodeIndex >= nodeData.Length)
            {
                // Bogus node index came out of the wait handle, perhaps due to memory pressure
                // We can't really do anything except re-throw so this problem can be diagnosed.
                throw wrappedException;
            }

            engineCallback.PostStatus(nodeData[nodeIndex].NodeId, nodeStatus, false);
        }

        /// <summary>
        /// This thread writes out the messages to the shared memory, where the LocalNode class
        /// reads it.
        /// </summary>
        private void SharedMemoryWriterThread()
        {
            // Create an array of event to the node thread responds
            WaitHandle[] waitHandles = new WaitHandle[1 + nodeData.Length];
            waitHandles[0] = exitCommunicationThreads;
            for (int i = 0; i < nodeData.Length; i++)
            {
                waitHandles[i + 1] = nodeData[i].NodeCommandQueue.QueueReadyEvent;
            }

            bool continueExecution = true;

            while (continueExecution)
            {
                int nodeIndex = -1;
                try
                {
                    // Wait for the next work item or an exit command
                    int eventType = WaitHandle.WaitAny(waitHandles);

                    if (eventType == 0)
                    {
                        // Exit node event
                        continueExecution = false;
                    }
                    else
                    {
                        nodeIndex = eventType - 1;
                        nodeData[nodeIndex].SharedMemoryToNode.Write(nodeData[nodeIndex].NodeCommandQueue, nodeData[nodeIndex].NodeHiPriCommandQueue, false);
                    }
                }
                catch (Exception e)
                {
                    // Ignore the queue of commands to the node that failed
                    if (nodeIndex >= 0 && nodeIndex < nodeData.Length)
                    {
                        waitHandles[1 + nodeIndex] = new ManualResetEvent(false);
                    }
                    ReportNodeCommunicationFailure(nodeIndex, e, true);
                }
            }

            for (int i = 0; i < nodeData.Length; i++)
            {
                // Dispose of the shared memory buffer
                if (nodeData[i].SharedMemoryToNode != null)
                {
                    nodeData[i].SharedMemoryToNode.Dispose();
                    nodeData[i].SharedMemoryToNode = null;
                }
            }
        }

        /// <summary>
        /// This thread is responsible for reading messages from the nodes. The messages are posted
        /// to the shared memory by the LocalNodeCallback
        /// </summary>
        private void SharedMemoryReaderThread()
        {
            // Create an array of event to the node thread responds
            WaitHandle[] waitHandles = new WaitHandle[1 + nodeData.Length];
            waitHandles[0] = exitCommunicationThreads;
            for (int i = 0; i < nodeData.Length; i++)
            {
                waitHandles[i + 1] = nodeData[i].SharedMemoryFromNode.ReadFlag;
            }

            bool continueExecution = true;

            while (continueExecution)
            {
                int nodeIndex = -1;
                try
                {
                    // Wait for the next work item or an exit command
                    int eventType = WaitHandle.WaitAny(waitHandles);

                    if (eventType == 0)
                    {
                        // Exit node event
                        continueExecution = false;
                    }
                    else
                    {
                        nodeIndex = eventType - 1;
                        IList localCallDescriptorList = nodeData[nodeIndex].SharedMemoryFromNode.Read();

                        if (localCallDescriptorList != null)
                        {
                            foreach (LocalCallDescriptor callDescriptor in localCallDescriptorList)
                            {
                                // Act as requested by the call
                                callDescriptor.HostAction(engineCallback, this, nodeData[nodeIndex].NodeId);
                                // Check if there is a reply to this call
                                if (callDescriptor.NeedsReply)
                                {
                                    nodeData[nodeIndex].NodeCommandQueue.Enqueue(callDescriptor.ReplyFromHostAction());
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    ReportNodeCommunicationFailure(nodeIndex, e, true);
                    // Ignore the events reported from that node from now on
                    if (nodeIndex >= 0 && nodeIndex < nodeData.Length)
                    {
                        waitHandles[1 + nodeIndex] = new ManualResetEvent(false);
                    }
                }
            }

            // Dispose of shared memory when done
            for (int i = 0; i < nodeData.Length; i++)
            {
                // Dispose of the shared memory buffer
                if (nodeData[i].SharedMemoryFromNode != null)
                {
                    nodeData[i].SharedMemoryFromNode.Dispose();
                    nodeData[i].SharedMemoryFromNode = null;
                }
            }
        }

        #endregion

        #region Data
        private IEngineCallback engineCallback;
        private ManualResetEvent exitCommunicationThreads;

        private ManualResetEvent responseCountChangeEvent;
        private int activeNodeCount;
        private int responseCount;

        private int cpuCount;

        private object nodeStateLock;
        private Queue<int> nodesToLaunch;

        private bool centralizedLogging;

        private bool onlyLogCriticalEvents;

        private bool useBreadthFirstTraversal;

        private bool enableNodeReuse = true;

        // True after shut down has been called, this flag prevents launching of new nodes after shutdown has been called
        private bool shuttingDown;

        private List<LoggerDescription> nodeLoggers;

        private string locationOfMSBuildExe = null;

        private BuildPropertyGroup parentGlobalProperties;
        private ToolsetDefinitionLocations toolsetSearchLocations;
        private string startupDirectory;

        private static readonly char[] parameterDelimiters = { ';' };
        private static readonly char[] valueDelimiters = { '=' };

        private LocalNodeInfo[] nodeData;

        // Timeouts and contants
        private const int initializationTimeout = 10 * 1000; // 10 seconds to process the init message
        private const int launchTimeout = 60 * 1000; // 60 seconds to launch the process
        private const int maximumNodeRestartCount = 2; // try twice to connect to the node
        private const int shutdownResponseTimeout = 1000; // every second check if the children are still alive
        private static int shutdownTimeout = 30; // Wait for 30 seconds for all nodes to shutdown.
        #endregion

        #region Local enums
        internal enum NodeState
        {
            /// <summary>
            /// This node has not been launched
            /// </summary>
            NotLaunched = 0,
            /// <summary>
            /// This node is in progress of being launched
            /// </summary>
            LaunchInProgress = 1,
            /// <summary>
            /// This node is launched
            /// </summary>
            Launched = 2,
            /// <summary>
            /// This node has been shutdown
            /// </summary>
            Shutdown = 3
        }
        #endregion
    }
}
