// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental
{
    /// <summary>
    /// This class represents an implementation of INode for out-of-proc server nodes aka MSBuild server
    /// </summary>
    public sealed class OutOfProcServerNode : INode, INodePacketFactory, INodePacketHandler
    {
        /// <summary>
        /// A callback used to execute command line build.
        /// </summary>
        public delegate (int exitCode, string exitType) BuildCallback(
#if FEATURE_GET_COMMANDLINE
            string commandLine);
#else
            string[] commandLine);
#endif

        private readonly BuildCallback _buildFunction;

        /// <summary>
        /// The endpoint used to talk to the host.
        /// </summary>
        private INodeEndpoint _nodeEndpoint = default!;

        /// <summary>
        /// The packet factory.
        /// </summary>
        private readonly NodePacketFactory _packetFactory;

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
        private Exception? _shutdownException = null;

        /// <summary>
        /// Indicate that cancel has been requested and initiated.
        /// </summary>
        private bool _cancelRequested = false;
        private string _serverBusyMutexName = default!;

        public OutOfProcServerNode(BuildCallback buildFunction)
        {
            _buildFunction = buildFunction;

            _receivedPackets = new ConcurrentQueue<INodePacket>();
            _packetReceivedEvent = new AutoResetEvent(false);
            _shutdownEvent = new ManualResetEvent(false);
            _packetFactory = new NodePacketFactory();

            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.ServerNodeBuildCommand, ServerNodeBuildCommand.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.NodeBuildComplete, NodeBuildComplete.FactoryForDeserialization, this);
            (this as INodePacketFactory).RegisterPacketHandler(NodePacketType.ServerNodeBuildCancel, ServerNodeBuildCancel.FactoryForDeserialization, this);
        }

        #region INode Members

        /// <summary>
        /// Starts up the server node and processes all build requests until the server is requested to shut down.
        /// </summary>
        /// <param name="shutdownException">The exception which caused shutdown, if any.</param>
        /// <returns>The reason for shutting down.</returns>
        public NodeEngineShutdownReason Run(out Exception? shutdownException)
        {
            ServerNodeHandshake handshake = new(
                CommunicationsUtilities.GetHandshakeOptions(taskHost: false, architectureFlagToSet: XMakeAttributes.GetCurrentMSBuildArchitecture()));

            _serverBusyMutexName = GetBusyServerMutexName(handshake);

            // Handled race condition. If two processes spawn to start build Server one will die while
            // one Server client connects to the other one and run build on it.
            CommunicationsUtilities.Trace("Starting new server node with handshake {0}", handshake);
            using var serverRunningMutex = ServerNamedMutex.OpenOrCreateMutex(GetRunningServerMutexName(handshake), out bool mutexCreatedNew);
            if (!mutexCreatedNew)
            {
                shutdownException = new InvalidOperationException("MSBuild server is already running!");
                return NodeEngineShutdownReason.Error;
            }

            while (true)
            {
                NodeEngineShutdownReason shutdownReason = RunInternal(out shutdownException, handshake);
                if (shutdownReason != NodeEngineShutdownReason.BuildCompleteReuse)
                {
                    return shutdownReason;
                }

                // We need to clear cache for two reasons:
                // - cache file names can collide cross build requests, which would cause stale caching
                // - we might need to avoid cache builds-up in files system during lifetime of server
                FileUtilities.ClearCacheDirectory();
                _shutdownEvent.Reset();
            }

            // UNREACHABLE
        }

        private NodeEngineShutdownReason RunInternal(out Exception? shutdownException, ServerNodeHandshake handshake)
        {
            _nodeEndpoint = new ServerNodeEndpointOutOfProc(GetPipeName(handshake), handshake);
            _nodeEndpoint.OnLinkStatusChanged += OnLinkStatusChanged;
            _nodeEndpoint.Listen(this);

            WaitHandle[] waitHandles = [_shutdownEvent, _packetReceivedEvent];

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

                        while (_receivedPackets.TryDequeue(out INodePacket? packet))
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

        internal static string GetPipeName(ServerNodeHandshake handshake)
            => NamedPipeUtil.GetPlatformSpecificPipeName($"MSBuildServer-{handshake.ComputeHash()}");

        internal static string GetRunningServerMutexName(ServerNodeHandshake handshake)
            => $@"Global\msbuild-server-running-{handshake.ComputeHash()}";

        internal static string GetBusyServerMutexName(ServerNodeHandshake handshake)
            => $@"Global\msbuild-server-busy-{handshake.ComputeHash()}";

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
        /// Perform necessary actions to shut down the node.
        /// </summary>
        // TODO: it is too complicated, for simple role of server node it needs to be simplified
        private NodeEngineShutdownReason HandleShutdown(out Exception? exception)
        {
            CommunicationsUtilities.Trace("Shutting down with reason: {0}, and exception: {1}.", _shutdownReason, _shutdownException);

            // On Windows, a process holds a handle to the current directory,
            // so reset it away from a user-requested folder that may get deleted.
            NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory);

            exception = _shutdownException;

            _nodeEndpoint.OnLinkStatusChanged -= OnLinkStatusChanged;
            _nodeEndpoint.Disconnect();

            CommunicationsUtilities.Trace("Shut down complete.");

            return _shutdownReason;
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
                _nodeEndpoint.SendData(packet);
            }
        }

        /// <summary>
        /// Dispatches the packet to the correct handler.
        /// </summary>
        private void HandlePacket(INodePacket packet)
        {
            switch (packet.Type)
            {
                case NodePacketType.ServerNodeBuildCommand:
                    HandleServerNodeBuildCommandAsync((ServerNodeBuildCommand)packet);
                    break;
                case NodePacketType.NodeBuildComplete:
                    HandleServerShutdownCommand((NodeBuildComplete)packet);
                    break;
                case NodePacketType.ServerNodeBuildCancel:
                    HandleBuildCancel();
                    break;
            }
        }

        /// <summary>
        /// NodeBuildComplete is used to signalize that node work is done (including server node)
        /// and shall recycle or shutdown if PrepareForReuse is false.
        /// </summary>
        /// <param name="buildComplete"></param>
        private void HandleServerShutdownCommand(NodeBuildComplete buildComplete)
        {
            _shutdownReason = buildComplete.PrepareForReuse ? NodeEngineShutdownReason.BuildCompleteReuse : NodeEngineShutdownReason.BuildComplete;
            _shutdownEvent.Set();
        }

        private void HandleBuildCancel()
        {
            CommunicationsUtilities.Trace("Received request to cancel build running on MSBuild Server. MSBuild server will shutdown.}");
            _cancelRequested = true;
            BuildManager.DefaultBuildManager.CancelAllSubmissions();
        }

        private void HandleServerNodeBuildCommandAsync(ServerNodeBuildCommand command)
        {
            Task.Run(() =>
            {
                try
                {
                    HandleServerNodeBuildCommand(command);
                }
                catch (Exception e)
                {
                    _shutdownException = e;
                    _shutdownReason = NodeEngineShutdownReason.Error;
                    _shutdownEvent.Set();
                }
            });
        }

        private void HandleServerNodeBuildCommand(ServerNodeBuildCommand command)
        {
            CommunicationsUtilities.Trace("Building with MSBuild server with command line {0}", command.CommandLine);
            using var serverBusyMutex = ServerNamedMutex.OpenOrCreateMutex(name: _serverBusyMutexName, createdNew: out var holdsMutex);
            if (!holdsMutex)
            {
                // Client must have send request message to server even though serer is busy.
                // It is not a race condition, as client exclusivity is also guaranteed by name pipe which allows only one client to connect.
                _shutdownException = new InvalidOperationException("Client requested build while server is busy processing previous client build request.");
                _shutdownReason = NodeEngineShutdownReason.Error;
                _shutdownEvent.Set();

                return;
            }

            // Set build process context
            Directory.SetCurrentDirectory(command.StartupDirectory);

            CommunicationsUtilities.SetEnvironment(command.BuildProcessEnvironment);
            Traits.UpdateFromEnvironment();

            Thread.CurrentThread.CurrentCulture = command.Culture;
            Thread.CurrentThread.CurrentUICulture = command.UICulture;

            // Reconfigure static BuildParameters.StartupDirectory to have this value
            // same as startup directory of msbuild entry client or dotnet CLI.
            BuildParameters.StartupDirectory = command.StartupDirectory;

            // Configure console configuration so Loggers can change their behavior based on Target (client) Console properties.
            ConsoleConfiguration.Provider = command.ConsoleConfiguration;

            // Initiate build telemetry
            if (command.PartialBuildTelemetry != null)
            {
                BuildTelemetry buildTelemetry = KnownTelemetry.PartialBuildTelemetry ??= new BuildTelemetry();

                buildTelemetry.StartAt = command.PartialBuildTelemetry.StartedAt;
                buildTelemetry.InitialMSBuildServerState = command.PartialBuildTelemetry.InitialServerState;
                buildTelemetry.ServerFallbackReason = command.PartialBuildTelemetry.ServerFallbackReason;
            }

            // Also try our best to increase chance custom Loggers which use Console static members will work as expected.
            try
            {
                if (NativeMethodsShared.IsWindows && command.ConsoleConfiguration.BufferWidth > 0)
                {
                    Console.BufferWidth = command.ConsoleConfiguration.BufferWidth;
                }

                if ((int)command.ConsoleConfiguration.BackgroundColor != -1)
                {
                    Console.BackgroundColor = command.ConsoleConfiguration.BackgroundColor;
                }
            }
            catch (Exception)
            {
                // Ignore exception, it is best effort only
            }

            // Configure console output redirection
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            (int exitCode, string exitType) buildResult;

            // Dispose must be called before the server sends ServerNodeBuildResult packet
            using (RedirectConsoleWriter outWriter = new(text => SendPacket(new ServerNodeConsoleWrite(text, ConsoleOutput.Standard))))
            using (RedirectConsoleWriter errWriter = new(text => SendPacket(new ServerNodeConsoleWrite(text, ConsoleOutput.Error))))
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);

                buildResult = _buildFunction(command.CommandLine);

                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            // On Windows, a process holds a handle to the current directory,
            // so reset it away from a user-requested folder that may get deleted.
            NativeMethodsShared.SetCurrentDirectory(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory);

            _nodeEndpoint.ClientWillDisconnect();
            var response = new ServerNodeBuildResult(buildResult.exitCode, buildResult.exitType);
            SendPacket(response);

            // Shutdown server if cancel was requested. This is consistent with nodes behavior.
            _shutdownReason = _cancelRequested ? NodeEngineShutdownReason.BuildComplete : NodeEngineShutdownReason.BuildCompleteReuse;
            _shutdownEvent.Set();
        }

        internal sealed class RedirectConsoleWriter : TextWriter
        {
            private readonly Action<string> _writeCallback;
            private readonly Timer _timer;
            private readonly object _lock = new();
            private readonly StringWriter _internalWriter;

            public RedirectConsoleWriter(Action<string> writeCallback)
            {
                _writeCallback = writeCallback;
                _internalWriter = new StringWriter();
                _timer = new Timer(TimerCallback, null, 0, 40);
            }

            public override Encoding Encoding => _internalWriter.Encoding;

            public override void Flush()
            {
                lock (_lock)
                {
                    var sb = _internalWriter.GetStringBuilder();
                    string captured = sb.ToString();
                    sb.Clear();

                    _writeCallback(captured);
                    _internalWriter.Flush();
                }
            }

            public override void Write(char value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(char[]? buffer)
            {
                lock (_lock)
                {
                    _internalWriter.Write(buffer);
                }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (_lock)
                {
                    _internalWriter.Write(buffer, index, count);
                }
            }

            public override void Write(bool value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(int value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(uint value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(long value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(ulong value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(float value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(double value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(decimal value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(string? value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(object? value)
            {
                lock (_lock)
                {
                    _internalWriter.Write(value);
                }
            }

            public override void Write(string format, object? arg0)
            {
                lock (_lock)
                {
                    _internalWriter.Write(format, arg0);
                }
            }

            public override void Write(string format, object? arg0, object? arg1)
            {
                lock (_lock)
                {
                    _internalWriter.Write(format, arg0, arg1);
                }
            }

            public override void Write(string format, object? arg0, object? arg1, object? arg2)
            {
                lock (_lock)
                {
                    _internalWriter.Write(format, arg0, arg1, arg2);
                }
            }

            public override void Write(string format, params object?[] arg)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(format, arg);
                }
            }

            public override void WriteLine()
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine();
                }
            }

            public override void WriteLine(char value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(decimal value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(char[]? buffer)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(buffer);
                }
            }

            public override void WriteLine(char[] buffer, int index, int count)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(buffer, index, count);
                }
            }

            public override void WriteLine(bool value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(int value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(uint value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(long value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(ulong value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(float value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(double value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(object? value)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(value);
                }
            }

            public override void WriteLine(string format, object? arg0)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(format, arg0);
                }
            }

            public override void WriteLine(string format, object? arg0, object? arg1)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(format, arg0, arg1);
                }
            }

            public override void WriteLine(string format, object? arg0, object? arg1, object? arg2)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(format, arg0, arg1, arg2);
                }
            }

            public override void WriteLine(string format, params object?[] arg)
            {
                lock (_lock)
                {
                    _internalWriter.WriteLine(format, arg);
                }
            }

            private void TimerCallback(object? state)
            {
                if (_internalWriter.GetStringBuilder().Length > 0)
                {
                    Flush();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _timer.Dispose();
                    Flush();
                    _internalWriter?.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
