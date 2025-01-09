// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
#if !CLR2COMPATIBILITY
using Microsoft.Build.Experimental.FileAccess;
#endif
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
#if FEATURE_APPDOMAIN
using System.Runtime.Remoting;
#endif

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// This class represents an implementation of INode for out-of-proc node for hosting tasks.
    /// </summary>
    internal class OutOfProcTaskHostNode :
#if FEATURE_APPDOMAIN
        MarshalByRefObject,
#endif
        INodePacketFactory, INodePacketHandler,
#if CLR2COMPATIBILITY
        IBuildEngine3
#else
        IBuildEngine10
#endif
    {
        /// <summary>
        /// Keeps a record of all environment variables that, on startup of the task host, have a different
        /// value from those that are passed to the task host in the configuration packet for the first task.
        /// These environments are assumed to be effectively identical, so the only difference between the
        /// two sets of values should be any environment variables that differ between e.g. a 32-bit and a 64-bit
        /// process.  Those are the variables that this dictionary should store.
        ///
        /// - The key into the dictionary is the name of the environment variable.
        /// - The Key of the KeyValuePair is the value of the variable in the parent process -- the value that we
        ///   wish to ensure is replaced by whatever the correct value in our current process is.
        /// - The Value of the KeyValuePair is the value of the variable in the current process -- the value that
        ///   we wish to replay the Key value with in the environment that we receive from the parent before
        ///   applying it to the current process.
        ///
        /// Note that either value in the KeyValuePair can be null, as it is completely possible to have an
        /// environment variable that is set in 32-bit processes but not in 64-bit, or vice versa.
        ///
        /// This dictionary must be static because otherwise, if a node is sitting around waiting for reuse, it will
        /// have inherited the environment from the previous build, and any differences between the two will be seen
        /// as "legitimate".  There is no way for us to know what the differences between the startup environment of
        /// the previous build and the environment of the first task run in the task host in this build -- so we
        /// must assume that the 4ish system environment variables that this is really meant to catch haven't
        /// somehow magically changed between two builds spaced no more than 15 minutes apart.
        /// </summary>
        private static IDictionary<string, KeyValuePair<string, string>> s_mismatchedEnvironmentValues;

        /// <summary>
        /// The endpoint used to talk to the host.
        /// </summary>
        private NodeEndpointOutOfProcTaskHost _nodeEndpoint;

        /// <summary>
        /// The packet factory.
        /// </summary>
        private NodePacketFactory _packetFactory;

        /// <summary>
        /// The event which is set when we receive packets.
        /// </summary>
        private AutoResetEvent _packetReceivedEvent;

        /// <summary>
        /// The queue of packets we have received but which have not yet been processed.
        /// </summary>
        private Queue<INodePacket> _receivedPackets;

        /// <summary>
        /// The current configuration for this task host.
        /// </summary>
        private TaskHostConfiguration _currentConfiguration;

        /// <summary>
        /// The saved environment for the process.
        /// </summary>
        private IDictionary<string, string> _savedEnvironment;

        /// <summary>
        /// The event which is set when we should shut down.
        /// </summary>
        private ManualResetEvent _shutdownEvent;

        /// <summary>
        /// The reason we are shutting down.
        /// </summary>
        private NodeEngineShutdownReason _shutdownReason;

        /// <summary>
        /// We set this flag to track a currently executing task
        /// </summary>
        private bool _isTaskExecuting;

        /// <summary>
        /// The event which is set when a task has completed.
        /// </summary>
        private AutoResetEvent _taskCompleteEvent;

        /// <summary>
        /// Packet containing all the information relating to the
        /// completed state of the task.
        /// </summary>
        private TaskHostTaskComplete _taskCompletePacket;

        /// <summary>
        /// Object used to synchronize access to taskCompletePacket
        /// </summary>
        private Object _taskCompleteLock = new Object();

        /// <summary>
        /// The event which is set when a task is cancelled
        /// </summary>
        private ManualResetEvent _taskCancelledEvent;

        /// <summary>
        /// The thread currently executing user task in the TaskRunner
        /// </summary>
        private Thread _taskRunnerThread;

        /// <summary>
        /// This is the wrapper for the user task to be executed.
        /// We are providing a wrapper to create a possibility of executing the task in a separate AppDomain
        /// </summary>
        private OutOfProcTaskAppDomainWrapper _taskWrapper;

        /// <summary>
        /// Flag indicating if we should debug communications or not.
        /// </summary>
        private bool _debugCommunications;

        /// <summary>
        /// Flag indicating whether we should modify the environment based on any differences we find between that of the
        /// task host at startup and the environment passed to us in our initial task configuration packet.
        /// </summary>
        private bool _updateEnvironment;

        /// <summary>
        /// An interim step between MSBuildTaskHostDoNotUpdateEnvironment=1 and the default update behavior:  go ahead and
        /// do all the updates that we would otherwise have done by default, but log any updates that are made (at low
        /// importance) so that the user is aware.
        /// </summary>
        private bool _updateEnvironmentAndLog;

#if !CLR2COMPATIBILITY
        /// <summary>
        /// The task object cache.
        /// </summary>
        private RegisteredTaskObjectCacheBase _registeredTaskObjectCache;
#endif

#if FEATURE_REPORTFILEACCESSES
        /// <summary>
        /// The file accesses reported by the most recently completed task.
        /// </summary>
        private List<FileAccessData> _fileAccessData = new List<FileAccessData>();
#endif

        /// <summary>
        /// Constructor.
        /// </summary>
        public OutOfProcTaskHostNode()
        {
            // We don't know what the current build thinks this variable should be until RunTask(), but as a fallback in case there are
            // communications before we get the configuration set up, just go with what was already in the environment from when this node
            // was initially launched.
            _debugCommunications = Traits.Instance.DebugNodeCommunication;

            _receivedPackets = new Queue<INodePacket>();

            // These WaitHandles are disposed in HandleShutDown()
            _packetReceivedEvent = new AutoResetEvent(false);
            _shutdownEvent = new ManualResetEvent(false);
            _taskCompleteEvent = new AutoResetEvent(false);
            _taskCancelledEvent = new ManualResetEvent(false);

            _packetFactory = new NodePacketFactory();

            INodePacketFactory thisINodePacketFactory = (INodePacketFactory)this;

            thisINodePacketFactory.RegisterPacketHandler(NodePacketType.TaskHostConfiguration, TaskHostConfiguration.FactoryForDeserialization, this);
            thisINodePacketFactory.RegisterPacketHandler(NodePacketType.TaskHostTaskCancelled, TaskHostTaskCancelled.FactoryForDeserialization, this);
            thisINodePacketFactory.RegisterPacketHandler(NodePacketType.NodeBuildComplete, NodeBuildComplete.FactoryForDeserialization, this);

#if !CLR2COMPATIBILITY
            EngineServices = new EngineServicesImpl(this);
#endif
        }

        #region IBuildEngine Implementation (Properties)

        /// <summary>
        /// Returns the value of ContinueOnError for the currently executing task.
        /// </summary>
        public bool ContinueOnError
        {
            get
            {
                ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration during a BuildEngine callback!");
                return _currentConfiguration.ContinueOnError;
            }
        }

        /// <summary>
        /// Returns the line number of the location in the project file of the currently executing task.
        /// </summary>
        public int LineNumberOfTaskNode
        {
            get
            {
                ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration during a BuildEngine callback!");
                return _currentConfiguration.LineNumberOfTask;
            }
        }

        /// <summary>
        /// Returns the column number of the location in the project file of the currently executing task.
        /// </summary>
        public int ColumnNumberOfTaskNode
        {
            get
            {
                ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration during a BuildEngine callback!");
                return _currentConfiguration.ColumnNumberOfTask;
            }
        }

        /// <summary>
        /// Returns the project file of the currently executing task.
        /// </summary>
        public string ProjectFileOfTaskNode
        {
            get
            {
                ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration during a BuildEngine callback!");
                return _currentConfiguration.ProjectFileOfTask;
            }
        }

        #endregion // IBuildEngine Implementation (Properties)

        #region IBuildEngine2 Implementation (Properties)

        /// <summary>
        /// Stub implementation of IBuildEngine2.IsRunningMultipleNodes.  The task host does not support this sort of
        /// IBuildEngine callback, so error.
        /// </summary>
        public bool IsRunningMultipleNodes
        {
            get
            {
                LogErrorFromResource("BuildEngineCallbacksInTaskHostUnsupported");
                return false;
            }
        }

        #endregion // IBuildEngine2 Implementation (Properties)

        #region IBuildEngine7 Implementation
        /// <summary>
        /// Enables or disables emitting a default error when a task fails without logging errors
        /// </summary>
        public bool AllowFailureWithoutError { get; set; } = false;
        #endregion

        #region IBuildEngine8 Implementation

        /// <summary>
        /// Contains all warnings that should be logged as errors.
        /// Non-null empty set when all warnings should be treated as errors.
        /// </summary>
        private ICollection<string> WarningsAsErrors { get; set; }

        private ICollection<string> WarningsNotAsErrors { get; set; }

        private ICollection<string> WarningsAsMessages { get; set; }

        public bool ShouldTreatWarningAsError(string warningCode)
        {
            // Warnings as messages overrides warnings as errors.
            if (WarningsAsErrors == null || WarningsAsMessages?.Contains(warningCode) == true)
            {
                return false;
            }

            return (WarningsAsErrors.Count == 0 && WarningAsErrorNotOverriden(warningCode)) || WarningsAsMessages.Contains(warningCode);
        }

        private bool WarningAsErrorNotOverriden(string warningCode)
        {
            return WarningsNotAsErrors?.Contains(warningCode) != true;
        }
        #endregion

        #region IBuildEngine Implementation (Methods)

        /// <summary>
        /// Sends the provided error back to the parent node to be logged, tagging it with
        /// the parent node's ID so that, as far as anyone is concerned, it might as well have
        /// just come from the parent node to begin with.
        /// </summary>
        public void LogErrorEvent(BuildErrorEventArgs e)
        {
            SendBuildEvent(e);
        }

        /// <summary>
        /// Sends the provided warning back to the parent node to be logged, tagging it with
        /// the parent node's ID so that, as far as anyone is concerned, it might as well have
        /// just come from the parent node to begin with.
        /// </summary>
        public void LogWarningEvent(BuildWarningEventArgs e)
        {
            SendBuildEvent(e);
        }

        /// <summary>
        /// Sends the provided message back to the parent node to be logged, tagging it with
        /// the parent node's ID so that, as far as anyone is concerned, it might as well have
        /// just come from the parent node to begin with.
        /// </summary>
        public void LogMessageEvent(BuildMessageEventArgs e)
        {
            SendBuildEvent(e);
        }

        /// <summary>
        /// Sends the provided custom event back to the parent node to be logged, tagging it with
        /// the parent node's ID so that, as far as anyone is concerned, it might as well have
        /// just come from the parent node to begin with.
        /// </summary>
        public void LogCustomEvent(CustomBuildEventArgs e)
        {
            SendBuildEvent(e);
        }

        /// <summary>
        /// Stub implementation of IBuildEngine.BuildProjectFile.  The task host does not support IBuildEngine
        /// callbacks for the purposes of building projects, so error.
        /// </summary>
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        {
            LogErrorFromResource("BuildEngineCallbacksInTaskHostUnsupported");
            return false;
        }

        #endregion // IBuildEngine Implementation (Methods)

        #region IBuildEngine2 Implementation (Methods)

        /// <summary>
        /// Stub implementation of IBuildEngine2.BuildProjectFile.  The task host does not support IBuildEngine
        /// callbacks for the purposes of building projects, so error.
        /// </summary>
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs, string toolsVersion)
        {
            LogErrorFromResource("BuildEngineCallbacksInTaskHostUnsupported");
            return false;
        }

        /// <summary>
        /// Stub implementation of IBuildEngine2.BuildProjectFilesInParallel.  The task host does not support IBuildEngine
        /// callbacks for the purposes of building projects, so error.
        /// </summary>
        public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion)
        {
            LogErrorFromResource("BuildEngineCallbacksInTaskHostUnsupported");
            return false;
        }

        #endregion // IBuildEngine2 Implementation (Methods)

        #region IBuildEngine3 Implementation

        /// <summary>
        /// Stub implementation of IBuildEngine3.BuildProjectFilesInParallel.  The task host does not support IBuildEngine
        /// callbacks for the purposes of building projects, so error.
        /// </summary>
        public BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs)
        {
            LogErrorFromResource("BuildEngineCallbacksInTaskHostUnsupported");
            return new BuildEngineResult(false, null);
        }

        /// <summary>
        /// Stub implementation of IBuildEngine3.Yield.  The task host does not support yielding, so just go ahead and silently
        /// return, letting the task continue.
        /// </summary>
        public void Yield()
        {
            return;
        }

        /// <summary>
        /// Stub implementation of IBuildEngine3.Reacquire. The task host does not support yielding, so just go ahead and silently
        /// return, letting the task continue.
        /// </summary>
        public void Reacquire()
        {
            return;
        }

        #endregion // IBuildEngine3 Implementation

#if !CLR2COMPATIBILITY
        #region IBuildEngine4 Implementation

        /// <summary>
        /// Registers an object with the system that will be disposed of at some specified time
        /// in the future.
        /// </summary>
        /// <param name="key">The key used to retrieve the object.</param>
        /// <param name="obj">The object to be held for later disposal.</param>
        /// <param name="lifetime">The lifetime of the object.</param>
        /// <param name="allowEarlyCollection">The object may be disposed earlier that the requested time if
        /// MSBuild needs to reclaim memory.</param>
        public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection)
        {
            _registeredTaskObjectCache.RegisterTaskObject(key, obj, lifetime, allowEarlyCollection);
        }

        /// <summary>
        /// Retrieves a previously registered task object stored with the specified key.
        /// </summary>
        /// <param name="key">The key used to retrieve the object.</param>
        /// <param name="lifetime">The lifetime of the object.</param>
        /// <returns>
        /// The registered object, or null is there is no object registered under that key or the object
        /// has been discarded through early collection.
        /// </returns>
        public object GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            return _registeredTaskObjectCache.GetRegisteredTaskObject(key, lifetime);
        }

        /// <summary>
        /// Unregisters a previously-registered task object.
        /// </summary>
        /// <param name="key">The key used to retrieve the object.</param>
        /// <param name="lifetime">The lifetime of the object.</param>
        /// <returns>
        /// The registered object, or null is there is no object registered under that key or the object
        /// has been discarded through early collection.
        /// </returns>
        public object UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            return _registeredTaskObjectCache.UnregisterTaskObject(key, lifetime);
        }

        #endregion

        #region IBuildEngine5 Implementation

        /// <summary>
        /// Logs a telemetry event.
        /// </summary>
        /// <param name="eventName">The event name.</param>
        /// <param name="properties">The list of properties associated with the event.</param>
        public void LogTelemetry(string eventName, IDictionary<string, string> properties)
        {
            SendBuildEvent(new TelemetryEventArgs
            {
                EventName = eventName,
                Properties = properties == null ? new Dictionary<string, string>() : new Dictionary<string, string>(properties),
            });
        }

        #endregion

        #region IBuildEngine6 Implementation

        /// <summary>
        /// Gets the global properties for the current project.
        /// </summary>
        /// <returns>An <see cref="IReadOnlyDictionary{String, String}" /> containing the global properties of the current project.</returns>
        public IReadOnlyDictionary<string, string> GetGlobalProperties()
        {
            return new Dictionary<string, string>(_currentConfiguration.GlobalProperties);
        }

        #endregion

        #region IBuildEngine9 Implementation

        public int RequestCores(int requestedCores)
        {
            // No resource management in OOP nodes
            throw new NotImplementedException();
        }

        public void ReleaseCores(int coresToRelease)
        {
            // No resource management in OOP nodes
            throw new NotImplementedException();
        }

        #endregion

        #region IBuildEngine10 Members

        [Serializable]
        private sealed class EngineServicesImpl : EngineServices
        {
            private readonly OutOfProcTaskHostNode _taskHost;

            internal EngineServicesImpl(OutOfProcTaskHostNode taskHost)
            {
                _taskHost = taskHost;
            }

            /// <summary>
            /// No logging verbosity optimization in OOP nodes.
            /// </summary>
            public override bool LogsMessagesOfImportance(MessageImportance importance) => true;

            /// <inheritdoc />
            public override bool IsTaskInputLoggingEnabled
            {
                get
                {
                    ErrorUtilities.VerifyThrow(_taskHost._currentConfiguration != null, "We should never have a null configuration during a BuildEngine callback!");
                    return _taskHost._currentConfiguration.IsTaskInputLoggingEnabled;
                }
            }

#if FEATURE_REPORTFILEACCESSES
            /// <summary>
            /// Reports a file access from a task.
            /// </summary>
            /// <param name="fileAccessData">The file access to report.</param>
            public void ReportFileAccess(FileAccessData fileAccessData)
            {
                _taskHost._fileAccessData.Add(fileAccessData);
            }
#endif
        }

        public EngineServices EngineServices { get; }

#endregion

#endif

        #region INodePacketFactory Members

        /// <summary>
        /// Registers the specified handler for a particular packet type.
        /// </summary>
        /// <param name="packetType">The packet type.</param>
        /// <param name="factory">The factory for packets of the specified type.</param>
        /// <param name="handler">The handler to be called when packets of the specified type are received.</param>
        public void RegisterPacketHandler(NodePacketType packetType, NodePacketFactoryMethod factory, INodePacketHandler handler)
        {
            _packetFactory.RegisterPacketHandler(packetType, factory, handler);
        }

        /// <summary>
        /// Unregisters a packet handler.
        /// </summary>
        /// <param name="packetType">The packet type.</param>
        public void UnregisterPacketHandler(NodePacketType packetType)
        {
            _packetFactory.UnregisterPacketHandler(packetType);
        }

        /// <summary>
        /// Takes a serializer, deserializes the packet and routes it to the appropriate handler.
        /// </summary>
        /// <param name="nodeId">The node from which the packet was received.</param>
        /// <param name="packetType">The packet type.</param>
        /// <param name="translator">The translator containing the data from which the packet should be reconstructed.</param>
        public void DeserializeAndRoutePacket(int nodeId, NodePacketType packetType, ITranslator translator)
        {
            _packetFactory.DeserializeAndRoutePacket(nodeId, packetType, translator);
        }

        /// <summary>
        /// Routes the specified packet
        /// </summary>
        /// <param name="nodeId">The node from which the packet was received.</param>
        /// <param name="packet">The packet to route.</param>
        public void RoutePacket(int nodeId, INodePacket packet)
        {
            _packetFactory.RoutePacket(nodeId, packet);
        }

        #endregion // INodePacketFactory Members

        #region INodePacketHandler Members

        /// <summary>
        /// This method is invoked by the NodePacketRouter when a packet is received and is intended for
        /// this recipient.
        /// </summary>
        /// <param name="node">The node from which the packet was received.</param>
        /// <param name="packet">The packet.</param>
        public void PacketReceived(int node, INodePacket packet)
        {
            lock (_receivedPackets)
            {
                _receivedPackets.Enqueue(packet);
                _packetReceivedEvent.Set();
            }
        }

        #endregion // INodePacketHandler Members

        #region INode Members

        /// <summary>
        /// Starts up the node and processes messages until the node is requested to shut down.
        /// </summary>
        /// <param name="shutdownException">The exception which caused shutdown, if any.</param>
        /// <returns>The reason for shutting down.</returns>
        public NodeEngineShutdownReason Run(out Exception shutdownException)
        {
#if !CLR2COMPATIBILITY
            _registeredTaskObjectCache = new RegisteredTaskObjectCacheBase();
#endif
            shutdownException = null;

            // Snapshot the current environment
            _savedEnvironment = CommunicationsUtilities.GetEnvironmentVariables();

            _nodeEndpoint = new NodeEndpointOutOfProcTaskHost();
            _nodeEndpoint.OnLinkStatusChanged += new LinkStatusChangedDelegate(OnLinkStatusChanged);
            _nodeEndpoint.Listen(this);

            WaitHandle[] waitHandles = new WaitHandle[] { _shutdownEvent, _packetReceivedEvent, _taskCompleteEvent, _taskCancelledEvent };

            while (true)
            {
                int index = WaitHandle.WaitAny(waitHandles);
                switch (index)
                {
                    case 0: // shutdownEvent
                        NodeEngineShutdownReason shutdownReason = HandleShutdown();
                        return shutdownReason;

                    case 1: // packetReceivedEvent
                        INodePacket packet = null;

                        int packetCount = _receivedPackets.Count;

                        while (packetCount > 0)
                        {
                            lock (_receivedPackets)
                            {
                                if (_receivedPackets.Count > 0)
                                {
                                    packet = _receivedPackets.Dequeue();
                                }
                                else
                                {
                                    break;
                                }
                            }

                            if (packet != null)
                            {
                                HandlePacket(packet);
                            }
                        }

                        break;
                    case 2: // taskCompleteEvent
                        CompleteTask();
                        break;
                    case 3: // taskCancelledEvent
                        CancelTask();
                        break;
                }
            }

            // UNREACHABLE
        }
        #endregion

        /// <summary>
        /// Dispatches the packet to the correct handler.
        /// </summary>
        private void HandlePacket(INodePacket packet)
        {
            switch (packet.Type)
            {
                case NodePacketType.TaskHostConfiguration:
                    HandleTaskHostConfiguration(packet as TaskHostConfiguration);
                    break;
                case NodePacketType.TaskHostTaskCancelled:
                    _taskCancelledEvent.Set();
                    break;
                case NodePacketType.NodeBuildComplete:
                    HandleNodeBuildComplete(packet as NodeBuildComplete);
                    break;
            }
        }

        /// <summary>
        /// Configure the task host according to the information received in the
        /// configuration packet
        /// </summary>
        private void HandleTaskHostConfiguration(TaskHostConfiguration taskHostConfiguration)
        {
            ErrorUtilities.VerifyThrow(!_isTaskExecuting, "Why are we getting a TaskHostConfiguration packet while we're still executing a task?");
            _currentConfiguration = taskHostConfiguration;

            // Kick off the task running thread.
            _taskRunnerThread = new Thread(new ParameterizedThreadStart(RunTask));
            _taskRunnerThread.Name = "Task runner for task " + taskHostConfiguration.TaskName;
            _taskRunnerThread.Start(taskHostConfiguration);
        }

        /// <summary>
        /// The task has been completed
        /// </summary>
        private void CompleteTask()
        {
            ErrorUtilities.VerifyThrow(!_isTaskExecuting, "The task should be done executing before CompleteTask.");
            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                TaskHostTaskComplete taskCompletePacketToSend;

                lock (_taskCompleteLock)
                {
                    ErrorUtilities.VerifyThrowInternalNull(_taskCompletePacket, "taskCompletePacket");
                    taskCompletePacketToSend = _taskCompletePacket;
                    _taskCompletePacket = null;
                }

                _nodeEndpoint.SendData(taskCompletePacketToSend);
            }

            _currentConfiguration = null;

            // If the task has been canceled, the event will still be set.
            // If so, now that we've completed the task, we want to shut down
            // this node -- with no reuse, since we don't know whether the
            // task we canceled left the node in a good state or not.
            if (_taskCancelledEvent.WaitOne(0))
            {
                _shutdownReason = NodeEngineShutdownReason.BuildComplete;
                _shutdownEvent.Set();
            }
        }

        /// <summary>
        /// This task has been cancelled. Attempt to cancel the task
        /// </summary>
        private void CancelTask()
        {
            // If the task is an ICancellable task in CLR4 we will call it here and wait for it to complete
            // Otherwise it's a classic ITask.

            // Store in a local to avoid a race
            var wrapper = _taskWrapper;
            if (wrapper?.CancelTask() == false)
            {
                // Create a possibility for the task to be aborted if the user really wants it dropped dead asap
                if (Environment.GetEnvironmentVariable("MSBUILDTASKHOSTABORTTASKONCANCEL") == "1")
                {
                    // Don't bother aborting the task if it has passed the actual user task Execute()
                    // It means we're already in the process of shutting down - Wait for the taskCompleteEvent to be set instead.
                    if (_isTaskExecuting)
                    {
#if FEATURE_THREAD_ABORT
                        // The thread will be terminated crudely so our environment may be trashed but it's ok since we are
                        // shutting down ASAP.
                        _taskRunnerThread.Abort();
#endif
                    }
                }
            }
        }

        /// <summary>
        /// Handles the NodeBuildComplete packet.
        /// </summary>
        private void HandleNodeBuildComplete(NodeBuildComplete buildComplete)
        {
            ErrorUtilities.VerifyThrow(!_isTaskExecuting, "We should never have a task in the process of executing when we receive NodeBuildComplete.");

            // TaskHostNodes lock assemblies with custom tasks produced by build scripts if NodeReuse is on. This causes failures if the user builds twice.
            _shutdownReason = buildComplete.PrepareForReuse && Traits.Instance.EscapeHatches.ReuseTaskHostNodes ? NodeEngineShutdownReason.BuildCompleteReuse : NodeEngineShutdownReason.BuildComplete;
            _shutdownEvent.Set();
        }

        /// <summary>
        /// Perform necessary actions to shut down the node.
        /// </summary>
        private NodeEngineShutdownReason HandleShutdown()
        {
            // Wait for the RunTask task runner thread before shutting down so that we can cleanly dispose all WaitHandles.
            _taskRunnerThread?.Join();

            using StreamWriter debugWriter = GetDebugWriter(_debugCommunications);

            debugWriter?.WriteLine("Node shutting down with reason {0}.", _shutdownReason);

#if !CLR2COMPATIBILITY
            _registeredTaskObjectCache.DisposeCacheObjects(RegisteredTaskObjectLifetime.Build);
            _registeredTaskObjectCache = null;
#endif

            // On Windows, a process holds a handle to the current directory,
            // so reset it away from a user-requested folder that may get deleted.
            NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory);

            // Restore the original environment, best effort.
            try
            {
                CommunicationsUtilities.SetEnvironment(_savedEnvironment);
            }
            catch (Exception ex)
            {
                debugWriter?.WriteLine("Failed to restore the original environment: {0}.", ex);
            }

            if (_nodeEndpoint.LinkStatus == LinkStatus.Active)
            {
                // Notify the BuildManager that we are done.
                _nodeEndpoint.SendData(new NodeShutdown(_shutdownReason == NodeEngineShutdownReason.Error ? NodeShutdownReason.Error : NodeShutdownReason.Requested));

                // Flush all packets to the pipe and close it down.  This blocks until the shutdown is complete.
                _nodeEndpoint.OnLinkStatusChanged -= new LinkStatusChangedDelegate(OnLinkStatusChanged);
            }

            _nodeEndpoint.Disconnect();

            // Dispose these WaitHandles
#if CLR2COMPATIBILITY
            _packetReceivedEvent.Close();
            _shutdownEvent.Close();
            _taskCompleteEvent.Close();
            _taskCancelledEvent.Close();
#else
            _packetReceivedEvent.Dispose();
            _shutdownEvent.Dispose();
            _taskCompleteEvent.Dispose();
            _taskCancelledEvent.Dispose();
#endif

            return _shutdownReason;

            static StreamWriter GetDebugWriter(bool debugCommunications)
            {
                StreamWriter debugWriter = debugCommunications
                    ? File.CreateText(string.Format(CultureInfo.CurrentCulture, Path.Combine(FileUtilities.TempFileDirectory, @"MSBuild_NodeShutdown_{0}.txt"), EnvironmentUtilities.CurrentProcessId))
                    : null;
                return debugWriter;
            }
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
        /// Task runner method
        /// </summary>
        private void RunTask(object state)
        {
            _isTaskExecuting = true;
            OutOfProcTaskHostTaskResult taskResult = null;
            TaskHostConfiguration taskConfiguration = state as TaskHostConfiguration;
            IDictionary<string, TaskParameter> taskParams = taskConfiguration.TaskParameters;

            // We only really know the values of these variables for sure once we see what we received from our parent
            // environment -- otherwise if this was a completely new build, we could lose out on expected environment
            // variables.
            _debugCommunications = taskConfiguration.BuildProcessEnvironment.ContainsValueAndIsEqual("MSBUILDDEBUGCOMM", "1", StringComparison.OrdinalIgnoreCase);
            _updateEnvironment = !taskConfiguration.BuildProcessEnvironment.ContainsValueAndIsEqual("MSBuildTaskHostDoNotUpdateEnvironment", "1", StringComparison.OrdinalIgnoreCase);
            _updateEnvironmentAndLog = taskConfiguration.BuildProcessEnvironment.ContainsValueAndIsEqual("MSBuildTaskHostUpdateEnvironmentAndLog", "1", StringComparison.OrdinalIgnoreCase);
            WarningsAsErrors = taskConfiguration.WarningsAsErrors;
            WarningsNotAsErrors = taskConfiguration.WarningsNotAsErrors;
            WarningsAsMessages = taskConfiguration.WarningsAsMessages;
            try
            {
                // Change to the startup directory
                NativeMethodsShared.SetCurrentDirectory(taskConfiguration.StartupDirectory);

                if (_updateEnvironment)
                {
                    InitializeMismatchedEnvironmentTable(taskConfiguration.BuildProcessEnvironment);
                }

                // Now set the new environment
                SetTaskHostEnvironment(taskConfiguration.BuildProcessEnvironment);

                // Set culture
                Thread.CurrentThread.CurrentCulture = taskConfiguration.Culture;
                Thread.CurrentThread.CurrentUICulture = taskConfiguration.UICulture;

                string taskName = taskConfiguration.TaskName;
                string taskLocation = taskConfiguration.TaskLocation;

                // We will not create an appdomain now because of a bug
                // As a fix, we will create the class directly without wrapping it in a domain
                _taskWrapper = new OutOfProcTaskAppDomainWrapper();

                taskResult = _taskWrapper.ExecuteTask(
                    this as IBuildEngine,
                    taskName,
                    taskLocation,
                    taskConfiguration.ProjectFileOfTask,
                    taskConfiguration.LineNumberOfTask,
                    taskConfiguration.ColumnNumberOfTask,
#if FEATURE_APPDOMAIN
                    taskConfiguration.AppDomainSetup,
#endif
                    taskParams);
            }
            catch (ThreadAbortException)
            {
                // This thread was aborted as part of Cancellation, we will return a failure task result
                taskResult = new OutOfProcTaskHostTaskResult(TaskCompleteType.Failure);
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                taskResult = new OutOfProcTaskHostTaskResult(TaskCompleteType.CrashedDuringExecution, e);
            }
            finally
            {
                try
                {
                    _isTaskExecuting = false;

                    IDictionary<string, string> currentEnvironment = CommunicationsUtilities.GetEnvironmentVariables();
                    currentEnvironment = UpdateEnvironmentForMainNode(currentEnvironment);

                    taskResult ??= new OutOfProcTaskHostTaskResult(TaskCompleteType.Failure);

                    lock (_taskCompleteLock)
                    {
                        _taskCompletePacket = new TaskHostTaskComplete(
                            taskResult,
#if FEATURE_REPORTFILEACCESSES
                            _fileAccessData,
#endif
                            currentEnvironment);
                    }

#if FEATURE_APPDOMAIN
                    foreach (TaskParameter param in taskParams.Values)
                    {
                        // Tell remoting to forget connections to the parameter
                        RemotingServices.Disconnect(param);
                    }
#endif

                    // Restore the original clean environment
                    CommunicationsUtilities.SetEnvironment(_savedEnvironment);
                }
                catch (Exception e)
                {
                    lock (_taskCompleteLock)
                    {
                        // Create a minimal taskCompletePacket to carry the exception so that the TaskHostTask does not hang while waiting
                        _taskCompletePacket = new TaskHostTaskComplete(
                            new OutOfProcTaskHostTaskResult(TaskCompleteType.CrashedAfterExecution, e),
#if FEATURE_REPORTFILEACCESSES
                            _fileAccessData,
#endif
                            null);
                    }
                }
                finally
                {
#if FEATURE_REPORTFILEACCESSES
                    _fileAccessData = new List<FileAccessData>();
#endif

                    // Call CleanupTask to unload any domains and other necessary cleanup in the taskWrapper
                    _taskWrapper.CleanupTask();

                    // The task has now fully completed executing
                    _taskCompleteEvent.Set();
                }
            }
        }

        /// <summary>
        /// Set the environment for the task host -- includes possibly munging the given
        /// environment somewhat to account for expected environment differences between,
        /// e.g. parent processes and task hosts of different bitnesses.
        /// </summary>
        private void SetTaskHostEnvironment(IDictionary<string, string> environment)
        {
            ErrorUtilities.VerifyThrowInternalNull(s_mismatchedEnvironmentValues, "mismatchedEnvironmentValues");
            IDictionary<string, string> updatedEnvironment = null;

            if (_updateEnvironment)
            {
                foreach (KeyValuePair<string, KeyValuePair<string, string>> variable in s_mismatchedEnvironmentValues)
                {
                    string oldValue = variable.Value.Key;
                    string newValue = variable.Value.Value;

                    // We don't check the return value, because having the variable not exist == be
                    // null is perfectly valid, and mismatchedEnvironmentValues stores those values
                    // as null as well, so the String.Equals should still return that they are equal.
                    string environmentValue = null;
                    environment.TryGetValue(variable.Key, out environmentValue);

                    if (String.Equals(environmentValue, oldValue, StringComparison.OrdinalIgnoreCase))
                    {
                        if (updatedEnvironment == null)
                        {
                            if (_updateEnvironmentAndLog)
                            {
                                LogMessageFromResource(MessageImportance.Low, "ModifyingTaskHostEnvironmentHeader");
                            }

                            updatedEnvironment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
                        }

                        if (newValue != null)
                        {
                            if (_updateEnvironmentAndLog)
                            {
                                LogMessageFromResource(MessageImportance.Low, "ModifyingTaskHostEnvironmentVariable", variable.Key, newValue, environmentValue ?? String.Empty);
                            }

                            updatedEnvironment[variable.Key] = newValue;
                        }
                        else
                        {
                            updatedEnvironment.Remove(variable.Key);
                        }
                    }
                }
            }

            // if it's still null here, there were no changes necessary -- so just
            // set it to what was already passed in.
            if (updatedEnvironment == null)
            {
                updatedEnvironment = environment;
            }

            CommunicationsUtilities.SetEnvironment(updatedEnvironment);
        }

        /// <summary>
        /// Given the environment of the task host at the end of task execution, make sure that any
        /// processor-specific variables have been re-applied in the correct form for the main node,
        /// so that when we pass this dictionary back to the main node, all it should have to do
        /// is just set it.
        /// </summary>
        private IDictionary<string, string> UpdateEnvironmentForMainNode(IDictionary<string, string> environment)
        {
            ErrorUtilities.VerifyThrowInternalNull(s_mismatchedEnvironmentValues, "mismatchedEnvironmentValues");
            IDictionary<string, string> updatedEnvironment = null;

            if (_updateEnvironment)
            {
                foreach (KeyValuePair<string, KeyValuePair<string, string>> variable in s_mismatchedEnvironmentValues)
                {
                    // Since this is munging the property list for returning to the parent process,
                    // then the value we wish to replace is the one that is in this process, and the
                    // replacement value is the one that originally came from the parent process,
                    // instead of the other way around.
                    string oldValue = variable.Value.Value;
                    string newValue = variable.Value.Key;

                    // We don't check the return value, because having the variable not exist == be
                    // null is perfectly valid, and mismatchedEnvironmentValues stores those values
                    // as null as well, so the String.Equals should still return that they are equal.
                    string environmentValue = null;
                    environment.TryGetValue(variable.Key, out environmentValue);

                    if (String.Equals(environmentValue, oldValue, StringComparison.OrdinalIgnoreCase))
                    {
                        updatedEnvironment ??= new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);

                        if (newValue != null)
                        {
                            updatedEnvironment[variable.Key] = newValue;
                        }
                        else
                        {
                            updatedEnvironment.Remove(variable.Key);
                        }
                    }
                }
            }

            // if it's still null here, there were no changes necessary -- so just
            // set it to what was already passed in.
            if (updatedEnvironment == null)
            {
                updatedEnvironment = environment;
            }

            return updatedEnvironment;
        }

        /// <summary>
        /// Make sure the mismatchedEnvironmentValues table has been populated.  Note that this should
        /// only do actual work on the very first run of a task in the task host -- otherwise, it should
        /// already have been populated.
        /// </summary>
        private void InitializeMismatchedEnvironmentTable(IDictionary<string, string> environment)
        {
            if (s_mismatchedEnvironmentValues == null)
            {
                // This is the first time that we have received a TaskHostConfiguration packet, so we
                // need to construct the mismatched environment table based on our current environment
                // (assumed to be effectively identical to startup) and the environment we were given
                // via the task host configuration, assumed to be effectively identical to the startup
                // environment of the task host, given that the configuration packet is sent immediately
                // after the node is launched.
                s_mismatchedEnvironmentValues = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, string> variable in _savedEnvironment)
                {
                    string oldValue = variable.Value;
                    string newValue;
                    if (!environment.TryGetValue(variable.Key, out newValue))
                    {
                        s_mismatchedEnvironmentValues[variable.Key] = new KeyValuePair<string, string>(null, oldValue);
                    }
                    else
                    {
                        if (!String.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                        {
                            s_mismatchedEnvironmentValues[variable.Key] = new KeyValuePair<string, string>(newValue, oldValue);
                        }
                    }
                }

                foreach (KeyValuePair<string, string> variable in environment)
                {
                    string newValue = variable.Value;
                    string oldValue;
                    if (!_savedEnvironment.TryGetValue(variable.Key, out oldValue))
                    {
                        s_mismatchedEnvironmentValues[variable.Key] = new KeyValuePair<string, string>(newValue, null);
                    }
                    else
                    {
                        if (!String.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                        {
                            s_mismatchedEnvironmentValues[variable.Key] = new KeyValuePair<string, string>(newValue, oldValue);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sends the requested packet across to the main node.
        /// </summary>
        private void SendBuildEvent(BuildEventArgs e)
        {
            if (_nodeEndpoint?.LinkStatus == LinkStatus.Active)
            {
#pragma warning disable SYSLIB0050
                // Types which are not serializable and are not IExtendedBuildEventArgs as
                // those always implement custom serialization by WriteToStream and CreateFromStream.
                if (!e.GetType().GetTypeInfo().IsSerializable && e is not IExtendedBuildEventArgs)
#pragma warning disable SYSLIB0050
                {
                    // log a warning and bail.  This will end up re-calling SendBuildEvent, but we know for a fact
                    // that the warning that we constructed is serializable, so everything should be good.
                    LogWarningFromResource("ExpectedEventToBeSerializable", e.GetType().Name);
                    return;
                }

                LogMessagePacket logMessage = new LogMessagePacket(new KeyValuePair<int, BuildEventArgs>(_currentConfiguration.NodeId, e));
                _nodeEndpoint.SendData(logMessage);
            }
        }

        /// <summary>
        /// Generates the message event corresponding to a particular resource string and set of args
        /// </summary>
        private void LogMessageFromResource(MessageImportance importance, string messageResource, params object[] messageArgs)
        {
            ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration when we're trying to log messages!");

            // Using the CLR 2 build event because this class is shared between MSBuildTaskHost.exe (CLR2) and MSBuild.exe (CLR4+)
            BuildMessageEventArgs message = new BuildMessageEventArgs(
                                                    ResourceUtilities.FormatString(AssemblyResources.GetString(messageResource), messageArgs),
                                                    null,
                                                    _currentConfiguration.TaskName,
                                                    importance);

            LogMessageEvent(message);
        }

        /// <summary>
        /// Generates the error event corresponding to a particular resource string and set of args
        /// </summary>
        private void LogWarningFromResource(string messageResource, params object[] messageArgs)
        {
            ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration when we're trying to log warnings!");

            // Using the CLR 2 build event because this class is shared between MSBuildTaskHost.exe (CLR2) and MSBuild.exe (CLR4+)
            BuildWarningEventArgs warning = new BuildWarningEventArgs(
                                                    null,
                                                    null,
                                                    ProjectFileOfTaskNode,
                                                    LineNumberOfTaskNode,
                                                    ColumnNumberOfTaskNode,
                                                    0,
                                                    0,
                                                    ResourceUtilities.FormatString(AssemblyResources.GetString(messageResource), messageArgs),
                                                    null,
                                                    _currentConfiguration.TaskName);

            LogWarningEvent(warning);
        }

        /// <summary>
        /// Generates the error event corresponding to a particular resource string and set of args
        /// </summary>
        private void LogErrorFromResource(string messageResource)
        {
            ErrorUtilities.VerifyThrow(_currentConfiguration != null, "We should never have a null configuration when we're trying to log errors!");

            // Using the CLR 2 build event because this class is shared between MSBuildTaskHost.exe (CLR2) and MSBuild.exe (CLR4+)
            BuildErrorEventArgs error = new BuildErrorEventArgs(
                                                    null,
                                                    null,
                                                    ProjectFileOfTaskNode,
                                                    LineNumberOfTaskNode,
                                                    ColumnNumberOfTaskNode,
                                                    0,
                                                    0,
                                                    AssemblyResources.GetString(messageResource),
                                                    null,
                                                    _currentConfiguration.TaskName);

            LogErrorEvent(error);
        }
    }
}
