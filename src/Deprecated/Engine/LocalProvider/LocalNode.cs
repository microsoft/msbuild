// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// THE ASSEMBLY BUILT FROM THIS SOURCE FILE HAS BEEN DEPRECATED FOR YEARS. IT IS BUILT ONLY TO PROVIDE
// BACKWARD COMPATIBILITY FOR API USERS WHO HAVE NOT YET MOVED TO UPDATED APIS. PLEASE DO NOT SEND PULL
// REQUESTS THAT CHANGE THIS FILE WITHOUT FIRST CHECKING WITH THE MAINTAINERS THAT THE FIX IS REQUIRED.

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Threading;
using Microsoft.Build.BuildEngine.Shared;
using Microsoft.Build.Shared;

namespace Microsoft.Build.BuildEngine
{
    /// <summary>
    /// This class (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
    /// <see href="/dotnet/api/microsoft.build.construction">Microsoft.Build.Construction</see>
    /// <see href="/dotnet/api/microsoft.build.evaluation">Microsoft.Build.Evaluation</see>
    /// <see href="/dotnet/api/microsoft.build.execution">Microsoft.Build.Execution</see>
    /// 
    /// This class hosts a node class in the child process. It uses shared memory to communicate
    /// with the local node provider.
    /// Wraps a Node.
    /// </summary>
    /// <remarks>
    /// <format type="text/markdown"><![CDATA[
    /// ## Remarks
    /// > [!WARNING]
    /// > This class (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
    /// > <xref:Microsoft.Build.Construction>
    /// > <xref:Microsoft.Build.Evaluation>
    /// > <xref:Microsoft.Build.Execution>
    /// ]]></format>
    /// </remarks>
    public class LocalNode
    {
        #region Static Constructors
        /// <summary>
        /// This method (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
        /// <see href="/dotnet/api/microsoft.build.construction">Microsoft.Build.Construction</see>
        /// <see href="/dotnet/api/microsoft.build.evaluation">Microsoft.Build.Evaluation</see>
        /// <see href="/dotnet/api/microsoft.build.execution">Microsoft.Build.Execution</see>
        /// 
        /// Hook up an unhandled exception handler, in case our error handling paths are leaky
        /// </summary>
        /// <remarks>
        /// <format type="text/markdown"><![CDATA[
        /// ## Remarks
        /// > [!WARNING]
        /// > This method (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
        /// > <xref:Microsoft.Build.Construction>
        /// > <xref:Microsoft.Build.Evaluation>
        /// > <xref:Microsoft.Build.Execution>
        /// ]]></format>
        /// </remarks>
        static LocalNode()
        {
            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.UnhandledException += UnhandledExceptionHandler;
        }
        #endregion

        #region Static Methods

        /// <summary>
        /// Dump any unhandled exceptions to a file so they can be diagnosed
        /// </summary>
        private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            DumpExceptionToFile(ex);
        }

        /// <summary>
        /// Dump the exception information to a file
        /// </summary>
        internal static void DumpExceptionToFile(Exception ex)
        {
            // Lock as multiple threads may throw simultaneously
            lock (dumpFileLocker)
            {
                if (dumpFileName == null)
                {
                    Guid guid = Guid.NewGuid();
                    string tempPath = Path.GetTempPath();

                    // For some reason we get Watson buckets because GetTempPath gives us a folder here that doesn't exist.
                    // Either because %TMP% is misdefined, or because they deleted the temp folder during the build.
                    if (!Directory.Exists(tempPath))
                    {
                        // If this throws, no sense catching it, we can't log it now, and we're here
                        // because we're a child node with no console to log to, so die
                        Directory.CreateDirectory(tempPath);
                    }

                    dumpFileName = Path.Combine(tempPath, "MSBuild_" + guid.ToString());

                    using (StreamWriter writer = new StreamWriter(dumpFileName, true /*append*/))
                    {
                        writer.WriteLine("UNHANDLED EXCEPTIONS FROM CHILD NODE:");
                        writer.WriteLine("===================");
                    }
                }

                using (StreamWriter writer = new StreamWriter(dumpFileName, true /*append*/))
                {
                    writer.WriteLine(DateTime.Now.ToLongTimeString());
                    writer.WriteLine(ex.ToString());
                    writer.WriteLine("===================");
                }
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates an instance of this class.
        /// </summary>
        internal LocalNode(int nodeNumberIn)
        {
            this.nodeNumber = nodeNumberIn;

            engineCallback = new LocalNodeCallback(communicationThreadExitEvent, this);
        }

        #endregion

        #region Communication Methods

        /// <summary>
        /// This method causes the reader and writer threads to start and create the shared memory structures
        /// </summary>
        private void StartCommunicationThreads()
        {
            // The writer thread should be created before the
            // reader thread because some LocalCallDescriptors
            // assume the shared memory for the writer thread
            // has already been created. The method will both
            // instantiate the shared memory for the writer
            // thread and also start the writer thread itself.
            // We will verifyThrow in the method if the
            // sharedMemory was not created correctly.
            engineCallback.StartWriterThread(nodeNumber);

            // Create the shared memory buffer
            this.sharedMemory =
                  new SharedMemory
                  (
                        // Generate the name for the shared memory region
                        LocalNodeProviderGlobalNames.NodeInputMemoryName(nodeNumber),
                        SharedMemoryType.ReadOnly,
                        // Reuse an existing shared memory region as it should have already
                        // been created by the parent node side
                        true
                  );

            ErrorUtilities.VerifyThrow(this.sharedMemory.IsUsable,
                "Failed to create shared memory for local node input.");

            // Start the thread that will be processing the calls from the parent engine
            ThreadStart threadState = new ThreadStart(this.SharedMemoryReaderThread);
            readerThread = new Thread(threadState);
            readerThread.Name = "MSBuild Child<-Parent Reader";
            readerThread.Start();
        }

        /// <summary>
        /// This method causes the reader and writer threads to exit and dispose of the shared memory structures
        /// </summary>
        private void StopCommunicationThreads()
        {
            communicationThreadExitEvent.Set();

            // Wait for communication threads to exit
            Thread writerThread = engineCallback.GetWriterThread();
            // The threads may not exist if the child has timed out before the parent has told the node
            // to start up its communication threads. This can happen if the node is started with /nodemode:x
            // and no parent is running, or if the parent node has spawned a new process and then crashed
            // before establishing communication with the child node.
            writerThread?.Join();

            readerThread?.Join();

            // Make sure the exit event is not set
            communicationThreadExitEvent.Reset();
        }

        #endregion

        #region Startup Methods

        /// <summary>
        /// Create global events necessary for handshaking with the parent
        /// </summary>
        /// <param name="nodeNumber"></param>
        /// <returns>True if events created successfully and false otherwise</returns>
        private static bool CreateGlobalEvents(int nodeNumber)
        {
            bool createdNew;
            if (NativeMethods.IsUserAdministrator())
            {
                EventWaitHandleSecurity mSec = new EventWaitHandleSecurity();

                // Add a rule that grants the access only to admins and systems
                mSec.SetSecurityDescriptorSddlForm(NativeMethods.ADMINONLYSDDL);

                // Create an initiation event to allow the parent side  to prove to the child that we have the same level of privilege as it does.
                // this is done by having the parent set this event which means it needs to have administrative permissions to do so.
                globalInitiateActivationEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeInitiateActivationEventName(nodeNumber), out createdNew, mSec);
            }
            else
            {
                // Create an initiation event to allow the parent side  to prove to the child that we have the same level of privilege as it does.
                // this is done by having the parent set this event which means it has atleast the same permissions as the child process
                globalInitiateActivationEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeInitiateActivationEventName(nodeNumber), out createdNew);
            }

            // This process must be the creator of the event to prevent squating by a lower privilaged attacker
            if (!createdNew)
            {
                return false;
            }

            // Informs the parent process that the child process has been created.
            globalNodeActive = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeActiveEventName(nodeNumber));
            globalNodeActive.Set();

            // Indicate to the parent process, this node is currently is ready to start to recieve requests
            globalNodeInUse = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeInUseEventName(nodeNumber));

            // Used by the parent process to inform the child process to shutdown due to the child process
            // not recieving the initialization command.
            globalNodeErrorShutdown = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeErrorShutdownEventName(nodeNumber));

            // Inform the parent process the node has started its communication threads.
            globalNodeActivate = new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeActivedEventName(nodeNumber));

            return true;
        }

        /// <summary>
        /// This method (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
        /// <see href="/dotnet/api/microsoft.build.construction">Microsoft.Build.Construction</see>
        /// <see href="/dotnet/api/microsoft.build.evaluation">Microsoft.Build.Evaluation</see>
        /// <see href="/dotnet/api/microsoft.build.execution">Microsoft.Build.Execution</see>
        /// 
        /// This function starts local node when process is launched and shuts it down on time out
        /// Called by msbuild.exe.
        /// </summary>
        /// <remarks>
        /// <format type="text/markdown"><![CDATA[
        /// ## Remarks
        /// > [!WARNING]
        /// > This method (and the whole namespace) is deprecated. Please use the classes in these namespaces instead: 
        /// > <xref:Microsoft.Build.Construction>
        /// > <xref:Microsoft.Build.Evaluation>
        /// > <xref:Microsoft.Build.Execution>
        /// ]]></format>
        /// </remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Agreed not to touch entries from Deprecated folder")]
        public static void StartLocalNodeServer(int nodeNumber)
        {
            // Create global events necessary for handshaking with the parent
            if (!CreateGlobalEvents(nodeNumber))
            {
                return;
            }

            LocalNode localNode = new LocalNode(nodeNumber);

            WaitHandle[] waitHandles = new WaitHandle[4];
            waitHandles[0] = shutdownEvent;
            waitHandles[1] = globalNodeErrorShutdown;
            waitHandles[2] = inUseEvent;
            waitHandles[3] = globalInitiateActivationEvent;

            // This is necessary to make build.exe finish promptly. Dont remove.
            if (!Engine.debugMode)
            {
                // Create null streams for the current input/output/error streams
                Console.SetOut(new StreamWriter(Stream.Null));
                Console.SetError(new StreamWriter(Stream.Null));
                Console.SetIn(new StreamReader(Stream.Null));
            }

            bool continueRunning = true;

            while (continueRunning)
            {
                int eventType = WaitHandle.WaitAny(waitHandles, inactivityTimeout, false);

                if (eventType == 0 || eventType == 1 || eventType == WaitHandle.WaitTimeout)
                {
                    continueRunning = false;
                    localNode.ShutdownNode(eventType != 1 ?
                                           Node.NodeShutdownLevel.PoliteShutdown :
                                           Node.NodeShutdownLevel.ErrorShutdown, true, true);
                }
                else if (eventType == 2)
                {
                    // reset the event as we do not want it to go into this state again when we are done with this if statement.
                    inUseEvent.Reset();
                    // The parent knows at this point the child process has been launched
                    globalNodeActivate.Reset();
                    // Set the global inuse event so other parent processes know this node is now initialized
                    globalNodeInUse.Set();
                    // Make a copy of the parents handle to protect ourselves in case the parent dies,
                    // this is to prevent a parent from reserving a node another parent is trying to use.
                    globalNodeReserveHandle =
                        new EventWaitHandle(false, EventResetMode.ManualReset, LocalNodeProviderGlobalNames.NodeReserveEventName(nodeNumber));
                    WaitHandle[] waitHandlesActive = new WaitHandle[3];
                    waitHandlesActive[0] = shutdownEvent;
                    waitHandlesActive[1] = globalNodeErrorShutdown;
                    waitHandlesActive[2] = notInUseEvent;

                    eventType = WaitHandle.WaitTimeout;
                    while (eventType == WaitHandle.WaitTimeout && continueRunning)
                    {
                        eventType = WaitHandle.WaitAny(waitHandlesActive, parentCheckInterval, false);

                        if (eventType == 0 || /* nice shutdown due to shutdownEvent */
                            eventType == 1 || /* error shutdown due to globalNodeErrorShutdown */
                            (eventType == WaitHandle.WaitTimeout && !localNode.IsParentProcessAlive()))
                        {
                            continueRunning = false;
                            // If the exit is not triggered by running of shutdown method
                            if (eventType != 0)
                            {
                                localNode.ShutdownNode(Node.NodeShutdownLevel.ErrorShutdown, true, true);
                            }
                        }
                        else if (eventType == 2)
                        {
                            // Trigger a collection before the node goes idle to insure that
                            // the memory is released to the system as soon as possible
                            GC.Collect();
                            // Change the current directory to a safe one so that the directory
                            // last used by the build can be safely deleted. We must have read
                            // access to the safe directory so use SystemDirectory for this purpose.
                            Directory.SetCurrentDirectory(Environment.SystemDirectory);
                            notInUseEvent.Reset();
                            globalNodeInUse.Reset();
                        }
                    }

                    ErrorUtilities.VerifyThrow(localNode.node == null,
                                               "Expected either node to be null or continueRunning to be false.");

                    // Stop the communication threads and release the shared memory object so that the next parent can create it
                    localNode.StopCommunicationThreads();
                    // Close the local copy of the reservation handle (this allows another parent to reserve
                    // the node)
                    globalNodeReserveHandle.Close();
                    globalNodeReserveHandle = null;
                }
                else if (eventType == 3)
                {
                    globalInitiateActivationEvent.Reset();
                    localNode.StartCommunicationThreads();
                    globalNodeActivate.Set();
                }
            }
            // Stop the communication threads and release the shared memory object so that the next parent can create it
            localNode.StopCommunicationThreads();

            globalNodeActive.Close();
            globalNodeInUse.Close();
        }

        #endregion

        #region Methods

        /// <summary>
        /// This method is run in its own thread, it is responsible for reading messages sent from the parent process
        /// through the shared memory region.
        /// </summary>
        private void SharedMemoryReaderThread()
        {
            // Create an array of event to the node thread responds
            WaitHandle[] waitHandles = new WaitHandle[2];
            waitHandles[0] = communicationThreadExitEvent;
            waitHandles[1] = sharedMemory.ReadFlag;

            bool continueExecution = true;

            try
            {
                while (continueExecution)
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
                        // Read the list of LocalCallDescriptors from sharedMemory,
                        // this will be null if a large object is being read from shared
                        // memory and will continue to be null until the large object has
                        // been completly sent.
                        IList localCallDescriptorList = sharedMemory.Read();

                        if (localCallDescriptorList != null)
                        {
                            foreach (LocalCallDescriptor callDescriptor in localCallDescriptorList)
                            {
                                // Execute the command method which relates to running on a child node
                                callDescriptor.NodeAction(node, this);

                                if ((callDescriptor.IsReply) && (callDescriptor is LocalReplyCallDescriptor))
                                {
                                    // Process the reply from the parent so it can be looked in a hashtable based
                                    // on the call descriptor who requested the reply.
                                    engineCallback.PostReplyFromParent((LocalReplyCallDescriptor)callDescriptor);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Will rethrow the exception if necessary
                ReportFatalCommunicationError(e);
            }

            // Dispose of the shared memory buffer
            if (sharedMemory != null)
            {
                sharedMemory.Dispose();
                sharedMemory = null;
            }
        }

        /// <summary>
        /// This method will shutdown the node being hosted by the child process and notify the parent process if requested,
        /// </summary>
        /// <param name="shutdownLevel">What kind of shutdown is causing the child node to shutdown</param>
        /// <param name="exitProcess">should the child process exit as part of the shutdown process</param>
        /// <param name="noParentNotification">Indicates if the parent process should be notified the child node is being shutdown</param>
        internal void ShutdownNode(Node.NodeShutdownLevel shutdownLevel, bool exitProcess, bool noParentNotification)
        {
            if (node != null)
            {
                try
                {
                    node.ShutdownNode(shutdownLevel);

                    if (!noParentNotification)
                    {
                        // Write the last event out directly
                        LocalCallDescriptorForShutdownComplete callDescriptor =

                            new LocalCallDescriptorForShutdownComplete(shutdownLevel, node.TotalTaskTime);
                        // Post the message indicating that the shutdown is complete
                        engineCallback.PostMessageToParent(callDescriptor, true);
                    }
                }
                catch (Exception e)
                {
                    if (shutdownLevel != Node.NodeShutdownLevel.ErrorShutdown)
                    {
                        ReportNonFatalCommunicationError(e);
                    }
                }
            }

            // If the shutdownLevel is not a build complete message, then this means there was a politeshutdown or an error shutdown, null the node out
            // as either it is no longer needed due to the node goign idle or there was a error and it is now in a bad state.
            if (shutdownLevel != Node.NodeShutdownLevel.BuildCompleteSuccess &&
                shutdownLevel != Node.NodeShutdownLevel.BuildCompleteFailure)
            {
                node = null;
                notInUseEvent.Set();
            }

            if (exitProcess)
            {
                // Even if we completed a build, if we are goign to exit the process we need to null out the node and set the notInUseEvent, this is
                // accomplished by calling this method again with the ErrorShutdown handle
                if (shutdownLevel == Node.NodeShutdownLevel.BuildCompleteSuccess || shutdownLevel == Node.NodeShutdownLevel.BuildCompleteFailure)
                {
                    ShutdownNode(Node.NodeShutdownLevel.ErrorShutdown, false, true);
                }
                // Signal all the communication threads to exit
                shutdownEvent.Set();
            }
        }

        /// <summary>
        /// This methods activates the local node
        /// </summary>
        internal void Activate
        (
            Hashtable environmentVariables,
            LoggerDescription[] nodeLoggers,
            int nodeId,
            BuildPropertyGroup parentGlobalProperties,
            ToolsetDefinitionLocations toolsetSearchLocations,
            int parentId,
            string parentStartupDirectory
        )
        {
            ErrorUtilities.VerifyThrow(node == null, "Expected node to be null on activation.");

            this.parentProcessId = parentId;

            engineCallback.Reset();

            inUseEvent.Set();

            // Clear the environment so that we dont have extra variables laying around, this
            // may be a performance hog but needs to be done
            IDictionary variableDictionary = Environment.GetEnvironmentVariables();
            foreach (string variableName in variableDictionary.Keys)
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }

            foreach (string key in environmentVariables.Keys)
            {
                Environment.SetEnvironmentVariable(key, (string)environmentVariables[key]);
            }

            // Host the msbuild engine and system
            node = new Node(nodeId, nodeLoggers, engineCallback, parentGlobalProperties, toolsetSearchLocations, parentStartupDirectory);

            // Write the initialization complete event out directly
            LocalCallDescriptorForInitializationComplete callDescriptor =
                new LocalCallDescriptorForInitializationComplete(EnvironmentUtilities.CurrentProcessId);

            // Post the message indicating that the initialization is complete
            engineCallback.PostMessageToParent(callDescriptor, true);
        }

        /// <summary>
        /// This method checks is the parent process has not exited
        /// </summary>
        /// <returns>True if the parent process is still alive</returns>
        private bool IsParentProcessAlive()
        {
            bool isParentAlive = true;
            try
            {
                // Check if the parent is still there
                using Process parentProcess = Process.GetProcessById(parentProcessId);
                if (parentProcess.HasExited)
                {
                    isParentAlive = false;
                }
            }
            catch (ArgumentException)
            {
                isParentAlive = false;
            }

            if (!isParentAlive)
            {
                // No logging's going to reach the parent at this point:
                // indicate on the console what's going on
                string message = ResourceUtilities.FormatResourceString("ParentProcessUnexpectedlyDied", node.NodeId);
                Console.WriteLine(message);
            }

            return isParentAlive;
        }

        /// <summary>
        /// Any error occuring in the shared memory transport is considered to be fatal
        /// </summary>
        /// <param name="originalException"></param>
        /// <exception cref="Exception">Re-throws exception passed in</exception>
        internal void ReportFatalCommunicationError(Exception originalException)
        {
            try
            {
                DumpExceptionToFile(originalException);
            }
            finally
            {
                node?.ReportFatalCommunicationError(originalException, null);
            }
        }

        /// <summary>
        /// This function is used to report exceptions which don't indicate breakdown
        /// of communication with the parent
        /// </summary>
        /// <param name="originalException"></param>
        internal void ReportNonFatalCommunicationError(Exception originalException)
        {
            if (node != null)
            {
                try
                {
                    DumpExceptionToFile(originalException);
                }
                finally
                {
                    node.ReportUnhandledError(originalException);
                }
            }
            else
            {
                // Since there is no node object report rethrow the exception
                ReportFatalCommunicationError(originalException);
            }
        }

        #endregion
        #region Properties
        internal static string DumpFileName
        {
            get
            {
                return dumpFileName;
            }
        }
        #endregion

        #region Member data

        private Node node;
        private SharedMemory sharedMemory;
        private LocalNodeCallback engineCallback;
        private int parentProcessId;
        private int nodeNumber;
        private Thread readerThread;
        private static object dumpFileLocker = new Object();

        // Public named events
        // If this event is set the node host process is currently running
        private static EventWaitHandle globalNodeActive;
        // If this event is set the node is currently running a build
        private static EventWaitHandle globalNodeInUse;
        // If this event exists the node is reserved for use by a particular parent engine
        // the node keeps a handle to this event during builds to prevent it from being used
        // by another parent engine if the original dies
        private static EventWaitHandle globalNodeReserveHandle;
        // If this event is set the node will immediatelly exit. The event is used by the
        // parent engine to cause the node to exit if communication is lost.
        private static EventWaitHandle globalNodeErrorShutdown;
        // This event is used to cause the child to create the shared memory structures to start communication
        // with the parent
        private static EventWaitHandle globalInitiateActivationEvent;
        // This event is used to indicate to the parent that shared memory buffers have been created and are ready for
        // use
        private static EventWaitHandle globalNodeActivate;
        // Private local events
        private static ManualResetEvent communicationThreadExitEvent = new ManualResetEvent(false);
        private static ManualResetEvent shutdownEvent = new ManualResetEvent(false);
        private static ManualResetEvent notInUseEvent = new ManualResetEvent(false);

        /// <summary>
        /// Indicates the node is now in use. This means the node has recieved an activate command with initialization
        /// data from the parent procss
        /// </summary>
        private static ManualResetEvent inUseEvent = new ManualResetEvent(false);

        /// <summary>
        /// Randomly generated file name for all exceptions thrown by this node that need to be dumped to a file.
        /// (There may be more than one exception, if they occur on different threads.)
        /// </summary>
        private static string dumpFileName = null;

        // Timeouts && Constants
        private const int inactivityTimeout = 60 * 1000; // 60 seconds of inactivity to exit
        private const int parentCheckInterval = 5 * 1000; // Check if the parent process is there every 5 seconds

        #endregion

    }
}

