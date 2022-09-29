// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Client;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Eventing;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental
{
    /// <summary>
    /// This class is the public entry point for executing builds in msbuild server.
    /// It processes command-line arguments and invokes the build engine.
    /// </summary>
    public sealed class MSBuildClient
    {
        /// <summary>
        /// The build inherits all the environment variables from the client process.
        /// This property allows to add extra environment variables or reset some of the existing ones.
        /// </summary>
        private readonly Dictionary<string, string> _serverEnvironmentVariables;

        /// <summary>
        /// The console mode we had before the build.
        /// </summary>
        private uint? _originalConsoleMode;

        /// <summary>
        /// Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.
        /// </summary>
        private readonly string _msbuildLocation;

        /// <summary>
        /// The command line to process.
        /// The first argument on the command line is assumed to be the name/path of the executable, and is ignored.
        /// </summary>
#if FEATURE_GET_COMMANDLINE
        private readonly string _commandLine;
#else
        private readonly string[] _commandLine;
#endif

        /// <summary>
        /// The MSBuild client execution result.
        /// </summary>
        private readonly MSBuildClientExitResult _exitResult;

        /// <summary>
        /// Whether MSBuild server finished the build.
        /// </summary>
        private bool _buildFinished = false;

        /// <summary>
        /// Handshake between server and client.
        /// </summary>
        private readonly ServerNodeHandshake _handshake;

        /// <summary>
        /// The named pipe name for client-server communication.
        /// </summary>
        private readonly string _pipeName;

        /// <summary>
        /// The named pipe stream for client-server communication.
        /// </summary>
        private NamedPipeClientStream _nodeStream = null!;

        /// <summary>
        /// A way to cache a byte array when writing out packets
        /// </summary>
        private readonly MemoryStream _packetMemoryStream;

        /// <summary>
        /// A binary writer to help write into <see cref="_packetMemoryStream"/>
        /// </summary>
        private readonly BinaryWriter _binaryWriter;

        /// <summary>
        /// Used to estimate the size of the build with an ETW trace.
        /// </summary>
        private int _numConsoleWritePackets;
        private long _sizeOfConsoleWritePackets;

        /// <summary>
        /// Capture configuration of Client Console.
        /// </summary>
        private TargetConsoleConfiguration? _consoleConfiguration;

        /// <summary>
        /// Incoming packet pump and redirection.
        /// </summary>
        private MSBuildClientPacketPump _packetPump = null!;

        /// <summary>
        /// Public constructor with parameters.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and is ignored</param>
        /// <param name="msbuildLocation"> Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.</param>
        public MSBuildClient(
#if FEATURE_GET_COMMANDLINE
            string commandLine,
#else
            string[] commandLine,
#endif
            string msbuildLocation)
        {
            _serverEnvironmentVariables = new();
            _exitResult = new();

            // dll & exe locations
            _commandLine = commandLine;
            _msbuildLocation = msbuildLocation;

            // Client <-> Server communication stream
            _handshake = GetHandshake();
            _pipeName = OutOfProcServerNode.GetPipeName(_handshake);
            _packetMemoryStream = new MemoryStream();
            _binaryWriter = new BinaryWriter(_packetMemoryStream);

            CreateNodePipeStream();
        }

        private void CreateNodePipeStream()
        {
            _nodeStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous
#if FEATURE_PIPEOPTIONS_CURRENTUSERONLY
                | PipeOptions.CurrentUserOnly
#endif
            );
            _packetPump = new MSBuildClientPacketPump(_nodeStream);
        }

        /// <summary>
        /// Orchestrates the execution of the build on the server,
        /// responsible for client-server communication.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildClientExitResult"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        public MSBuildClientExitResult Execute(CancellationToken cancellationToken)
        {
            // Command line in one string used only in human readable content.
            string descriptiveCommandLine =
#if FEATURE_GET_COMMANDLINE
                _commandLine;
#else
                string.Join(" ", _commandLine);
#endif

            CommunicationsUtilities.Trace("Executing build with command line '{0}'", descriptiveCommandLine);
            bool serverIsAlreadyRunning = ServerIsRunning();
            if (KnownTelemetry.BuildTelemetry != null)
            {
                KnownTelemetry.BuildTelemetry.InitialServerState = serverIsAlreadyRunning ? "hot" : "cold";
            }
            if (!serverIsAlreadyRunning)
            {
                CommunicationsUtilities.Trace("Server was not running. Starting server now.");
                if (!TryLaunchServer())
                {
                    _exitResult.MSBuildClientExitType = MSBuildClientExitType.LaunchError;
                    return _exitResult;
                }
            }

            // Check that server is not busy.
            bool serverWasBusy = ServerWasBusy();
            if (serverWasBusy)
            {
                CommunicationsUtilities.Trace("Server is busy, falling back to former behavior.");
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.ServerBusy;
                return _exitResult;
            }

            // Connect to server.
            if (!TryConnectToServer(serverIsAlreadyRunning ? 1_000 : 20_000))
            {
                return _exitResult;
            }

            ConfigureAndQueryConsoleProperties();

            // Send build command.
            // Let's send it outside the packet pump so that we easier and quicker deal with possible issues with connection to server.
            MSBuildEventSource.Log.MSBuildServerBuildStart(descriptiveCommandLine);
            IntPtr stdOut = NativeMethodsShared.GetStdHandle(NativeMethodsShared.STD_OUTPUT_HANDLE);
            if (!TrySendBuildCommand())
            {
                if (_originalConsoleMode is not null)
                {
                    NativeMethodsShared.SetConsoleMode(stdOut, _originalConsoleMode.Value);
                }

                return _exitResult;
            }

            _numConsoleWritePackets = 0;
            _sizeOfConsoleWritePackets = 0;

            ReadPacketsLoop(cancellationToken);

            MSBuildEventSource.Log.MSBuildServerBuildStop(descriptiveCommandLine, _numConsoleWritePackets, _sizeOfConsoleWritePackets, _exitResult.MSBuildClientExitType.ToString(), _exitResult.MSBuildAppExitTypeString);
            CommunicationsUtilities.Trace("Build finished.");

            if (_originalConsoleMode is not null)
            {
                NativeMethodsShared.SetConsoleMode(stdOut, _originalConsoleMode.Value);
            }

            return _exitResult;
        }

        /// <summary>
        /// Attempt to shutdown MSBuild Server node.
        /// </summary>
        /// <remarks>
        /// It shutdown only server created by current user with current admin elevation.
        /// </remarks>
        /// <param name="cancellationToken"></param>
        /// <returns>True if server is not running anymore.</returns>
        public static bool ShutdownServer(CancellationToken cancellationToken)
        {
            // Neither commandLine nor msbuildlocation is involved in node shutdown
            var client = new MSBuildClient(commandLine: null!, msbuildLocation: null!);

            return client.TryShutdownServer(cancellationToken);
        }

        private bool TryShutdownServer(CancellationToken cancellationToken)
        {
            CommunicationsUtilities.Trace("Trying shutdown server node.");

            bool serverIsAlreadyRunning = ServerIsRunning();
            if (!serverIsAlreadyRunning)
            {
                CommunicationsUtilities.Trace("No need to shutdown server node for it is not running.");
                return true;
            }

            // Check that server is not busy.
            bool serverWasBusy = ServerWasBusy();
            if (serverWasBusy)
            {
                CommunicationsUtilities.Trace("Server cannot be shut down for it is not idle.");
                return false;
            }

            // Connect to server.
            if (!TryConnectToServer(1_000))
            {
                CommunicationsUtilities.Trace("Client cannot connect to idle server to shut it down.");
                return false;
            }

            if (!TrySendShutdownCommand())
            {
                CommunicationsUtilities.Trace("Failed to send shutdown command to the server.");
                return false;
            }

            ReadPacketsLoop(cancellationToken);

            return _exitResult.MSBuildClientExitType == MSBuildClientExitType.Success;
        }

        internal bool ServerIsRunning()
        {
            string serverRunningMutexName = OutOfProcServerNode.GetRunningServerMutexName(_handshake);
            bool serverIsAlreadyRunning = ServerNamedMutex.WasOpen(serverRunningMutexName);
            return serverIsAlreadyRunning;
        }

        private bool ServerWasBusy()
        {
            string serverBusyMutexName = OutOfProcServerNode.GetBusyServerMutexName(_handshake);
            var serverWasBusy = ServerNamedMutex.WasOpen(serverBusyMutexName);
            return serverWasBusy;
        }

        private void ReadPacketsLoop(CancellationToken cancellationToken)
        {
            try
            {
                // Start packet pump
                using MSBuildClientPacketPump packetPump = _packetPump;

                packetPump.RegisterPacketHandler(NodePacketType.ServerNodeConsoleWrite, ServerNodeConsoleWrite.FactoryForDeserialization, packetPump);
                packetPump.RegisterPacketHandler(NodePacketType.ServerNodeBuildResult, ServerNodeBuildResult.FactoryForDeserialization, packetPump);
                packetPump.Start();

                WaitHandle[] waitHandles =
                {
                    cancellationToken.WaitHandle,
                    packetPump.PacketPumpCompleted,
                    packetPump.PacketReceivedEvent
                };

                while (!_buildFinished)
                {
                    int index = WaitHandle.WaitAny(waitHandles);
                    switch (index)
                    {
                        case 0:
                            HandleCancellation();
                            // After the cancelation, we want to wait to server gracefuly finish the build.
                            // We have to replace the cancelation handle, because WaitAny would cause to repeatedly hit this branch of code.
                            waitHandles[0] = CancellationToken.None.WaitHandle;
                            break;

                        case 1:
                            HandlePacketPumpCompleted(packetPump);
                            break;

                        case 2:
                            while (packetPump.ReceivedPacketsQueue.TryDequeue(out INodePacket? packet) &&
                                   !_buildFinished)
                            {
                                if (packet != null)
                                {
                                    HandlePacket(packet);
                                }
                            }

                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("MSBuild client error: problem during packet handling occurred: {0}.", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.Unexpected;
            }
        }

        private void ConfigureAndQueryConsoleProperties()
        {
            var (acceptAnsiColorCodes, outputIsScreen) = QueryIsScreenAndTryEnableAnsiColorCodes();
            int bufferWidth = QueryConsoleBufferWidth();
            ConsoleColor backgroundColor = QueryConsoleBackgroundColor();

            _consoleConfiguration = new TargetConsoleConfiguration(bufferWidth, acceptAnsiColorCodes, outputIsScreen, backgroundColor);
        }

        private (bool acceptAnsiColorCodes, bool outputIsScreen) QueryIsScreenAndTryEnableAnsiColorCodes()
        {
            bool acceptAnsiColorCodes = false;
            bool outputIsScreen = false;

            if (NativeMethodsShared.IsWindows)
            {
                try
                {
                    IntPtr stdOut = NativeMethodsShared.GetStdHandle(NativeMethodsShared.STD_OUTPUT_HANDLE);
                    if (NativeMethodsShared.GetConsoleMode(stdOut, out uint consoleMode))
                    {
                        _originalConsoleMode = consoleMode;
                        bool success;
                        if ((consoleMode & NativeMethodsShared.ENABLE_VIRTUAL_TERMINAL_PROCESSING) == NativeMethodsShared.ENABLE_VIRTUAL_TERMINAL_PROCESSING &&
                            (consoleMode & NativeMethodsShared.DISABLE_NEWLINE_AUTO_RETURN) == NativeMethodsShared.DISABLE_NEWLINE_AUTO_RETURN)
                        {
                            // Console is already in required state
                            success = true;
                        }
                        else
                        {
                            consoleMode |= NativeMethodsShared.ENABLE_VIRTUAL_TERMINAL_PROCESSING | NativeMethodsShared.DISABLE_NEWLINE_AUTO_RETURN;
                            success = NativeMethodsShared.SetConsoleMode(stdOut, consoleMode);
                        }

                        if (success)
                        {
                            acceptAnsiColorCodes = true;
                        }

                        uint fileType = NativeMethodsShared.GetFileType(stdOut);
                        // The std out is a char type(LPT or Console)
                        outputIsScreen = fileType == NativeMethodsShared.FILE_TYPE_CHAR;
                        acceptAnsiColorCodes &= outputIsScreen;
                    }
                }
                catch (Exception ex)
                {
                    CommunicationsUtilities.Trace("MSBuild client warning: problem during enabling support for VT100: {0}.", ex);
                }
            }
            else
            {
                // On posix OSes we expect console always supports VT100 coloring unless it is redirected
                acceptAnsiColorCodes = outputIsScreen = !Console.IsOutputRedirected;
            }

            return (acceptAnsiColorCodes: acceptAnsiColorCodes, outputIsScreen: outputIsScreen);
        }
        
        private int QueryConsoleBufferWidth()
        {
            int consoleBufferWidth = -1;
            try
            {
                consoleBufferWidth = Console.BufferWidth;
            }
            catch (Exception ex)
            {
                // on Win8 machines while in IDE Console.BufferWidth will throw (while it talks to native console it gets "operation aborted" native error)
                // this is probably temporary workaround till we understand what is the reason for that exception
                CommunicationsUtilities.Trace("MSBuild client warning: problem during querying console buffer width.", ex);
            }

            return consoleBufferWidth;
        }

        /// <summary>
        /// Some platforms do not allow getting current background color. There
        /// is not way to check, but not-supported exception is thrown. Assume
        /// black, but don't crash.
        /// </summary>
        private ConsoleColor QueryConsoleBackgroundColor()
        {
            ConsoleColor consoleBackgroundColor;
            try
            {
                consoleBackgroundColor = Console.BackgroundColor;
            }
            catch (PlatformNotSupportedException)
            {
                consoleBackgroundColor = ConsoleColor.Black;
            }

            return consoleBackgroundColor;
        }

        private bool TrySendPacket(Func<INodePacket> packetResolver)
        {
            INodePacket? packet = null;
            try
            {
                packet = packetResolver();
                WritePacket(_nodeStream, packet);
                CommunicationsUtilities.Trace("Command packet of type '{0}' sent...", packet.Type);
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("Failed to send command packet of type '{0}' to server: {1}", packet?.Type.ToString() ?? "Unknown", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.Unexpected;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Launches MSBuild server. 
        /// </summary>
        /// <returns> Whether MSBuild server was started successfully.</returns>
        private bool TryLaunchServer()
        {
            string serverLaunchMutexName = $@"Global\msbuild-server-launch-{_handshake.ComputeHash()}";
            try
            {
                // For unknown root cause, opening mutex can sometimes throw 'Connection timed out' exception. See: https://github.com/dotnet/msbuild/issues/7993
                using var serverLaunchMutex = ServerNamedMutex.OpenOrCreateMutex(serverLaunchMutexName, out bool mutexCreatedNew);
                if (!mutexCreatedNew)
                {
                    // Some other client process launching a server and setting a build request for it. Fallback to usual msbuild app build.
                    CommunicationsUtilities.Trace("Another process launching the msbuild server, falling back to former behavior.");
                    _exitResult.MSBuildClientExitType = MSBuildClientExitType.ServerBusy;
                    return false;
                }

                string[] msBuildServerOptions = new string[] {
                    "/nologo",
                    "/nodemode:8"
                };

                NodeLauncher nodeLauncher = new NodeLauncher();
                CommunicationsUtilities.Trace("Starting Server...");
                Process msbuildProcess = nodeLauncher.Start(_msbuildLocation, string.Join(" ", msBuildServerOptions));
                CommunicationsUtilities.Trace("Server started with PID: {0}", msbuildProcess?.Id);
            }
            catch (Exception ex)
            {
                CommunicationsUtilities.Trace("Failed to launch the msbuild server: {0}", ex);
                _exitResult.MSBuildClientExitType = MSBuildClientExitType.LaunchError;
                return false;
            }

            return true;
        }

        private bool TrySendBuildCommand() => TrySendPacket(() => GetServerNodeBuildCommand());

        private bool TrySendCancelCommand() => TrySendPacket(() => new ServerNodeBuildCancel());

        private bool TrySendShutdownCommand()
        {
            _packetPump.ServerWillDisconnect();
            return  TrySendPacket(() => new NodeBuildComplete(false /* no node reuse */));
        }

        private ServerNodeBuildCommand GetServerNodeBuildCommand()
        {
            Dictionary<string, string> envVars = new();

            foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
            {
                envVars[(string)envVar.Key] = (envVar.Value as string) ?? string.Empty;
            }

            foreach (var pair in _serverEnvironmentVariables)
            {
                envVars[pair.Key] = pair.Value;
            }

            // We remove env variable used to invoke MSBuild server as that might be equal to 1, so we do not get an infinite recursion here. 
            envVars.Remove(Traits.UseMSBuildServerEnvVarName);

            Debug.Assert(KnownTelemetry.BuildTelemetry == null || KnownTelemetry.BuildTelemetry.StartAt.HasValue, "BuildTelemetry.StartAt was not initialized!");

            PartialBuildTelemetry? partialBuildTelemetry = KnownTelemetry.BuildTelemetry == null
                ? null
                : new PartialBuildTelemetry(
                    startedAt: KnownTelemetry.BuildTelemetry.StartAt.GetValueOrDefault(),
                    initialServerState: KnownTelemetry.BuildTelemetry.InitialServerState,
                    serverFallbackReason: KnownTelemetry.BuildTelemetry.ServerFallbackReason);

            return new ServerNodeBuildCommand(
                        _commandLine,
                        startupDirectory: Directory.GetCurrentDirectory(),
                        buildProcessEnvironment: envVars,
                        CultureInfo.CurrentCulture,
                        CultureInfo.CurrentUICulture,
                        _consoleConfiguration!,
                        partialBuildTelemetry);
        }

        private ServerNodeHandshake GetHandshake()
        {
            return new ServerNodeHandshake(CommunicationsUtilities.GetHandshakeOptions(taskHost: false, architectureFlagToSet: XMakeAttributes.GetCurrentMSBuildArchitecture()));
        }

        /// <summary>
        /// Handle cancellation.
        /// </summary>
        private void HandleCancellation()
        {
            TrySendCancelCommand();

            CommunicationsUtilities.Trace("MSBuild client sent cancellation command.");
        }

        /// <summary>
        /// Handle when packet pump is completed both successfully or with error.
        /// </summary>
        private void HandlePacketPumpCompleted(MSBuildClientPacketPump packetPump)
        {
            if (packetPump.PacketPumpException != null)
            {
                CommunicationsUtilities.Trace("MSBuild client error: packet pump unexpectedly shut down: {0}", packetPump.PacketPumpException);
                throw packetPump.PacketPumpException ?? new InternalErrorException("Packet pump unexpectedly shut down");
            }

            _buildFinished = true;
        }

        /// <summary>
        /// Dispatches the packet to the correct handler.
        /// </summary>
        private void HandlePacket(INodePacket packet)
        {
            switch (packet.Type)
            {
                case NodePacketType.ServerNodeConsoleWrite:
                    ServerNodeConsoleWrite writePacket = (packet as ServerNodeConsoleWrite)!;
                    HandleServerNodeConsoleWrite(writePacket);
                    _numConsoleWritePackets++;
                    _sizeOfConsoleWritePackets += writePacket.Text.Length;
                    break;
                case NodePacketType.ServerNodeBuildResult:
                    HandleServerNodeBuildResult((ServerNodeBuildResult)packet);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected packet type {packet.GetType().Name}");
            }
        }

        private void HandleServerNodeConsoleWrite(ServerNodeConsoleWrite consoleWrite)
        {
            switch (consoleWrite.OutputType)
            {
                case ConsoleOutput.Standard:
                    Console.Write(consoleWrite.Text);
                    break;
                case ConsoleOutput.Error:
                    Console.Error.Write(consoleWrite.Text);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected console output type {consoleWrite.OutputType}");
            }
        }

        private void HandleServerNodeBuildResult(ServerNodeBuildResult response)
        {
            CommunicationsUtilities.Trace("Build response received: exit code '{0}', exit type '{1}'", response.ExitCode, response.ExitType);
            _exitResult.MSBuildClientExitType = MSBuildClientExitType.Success;
            _exitResult.MSBuildAppExitTypeString = response.ExitType;
            _buildFinished = true;
        }

        /// <summary>
        /// Connects to MSBuild server.
        /// </summary>
        /// <returns> Whether the client connected to MSBuild server successfully.</returns>
        private bool TryConnectToServer(int timeoutMilliseconds)
        {
            bool tryAgain = true;
            Stopwatch sw = Stopwatch.StartNew();

            while (tryAgain && sw.ElapsedMilliseconds < timeoutMilliseconds)
            {
                tryAgain = false;
                try
                {
                    NodeProviderOutOfProcBase.ConnectToPipeStream(_nodeStream, _pipeName, _handshake, Math.Max(1, timeoutMilliseconds - (int)sw.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    if (ex is not TimeoutException && sw.ElapsedMilliseconds < timeoutMilliseconds)
                    {
                        CommunicationsUtilities.Trace("Retrying to connect to server after {0} ms", sw.ElapsedMilliseconds);
                        // This solves race condition for time in which server started but have not yet listen on pipe or
                        // when it just finished build request and is recycling pipe.
                        tryAgain = true;
                        CreateNodePipeStream();
                    }
                    else
                    {
                        CommunicationsUtilities.Trace("Failed to connect to server: {0}", ex);
                        _exitResult.MSBuildClientExitType = MSBuildClientExitType.UnableToConnect;
                        return false;
                    }
                }
            }

            return true;
        }

        private void WritePacket(Stream nodeStream, INodePacket packet)
        {
            MemoryStream memoryStream = _packetMemoryStream;
            memoryStream.SetLength(0);

            ITranslator writeTranslator = BinaryTranslator.GetWriteTranslator(memoryStream);

            // Write header
            memoryStream.WriteByte((byte)packet.Type);

            // Pad for packet length
            _binaryWriter.Write(0);

            // Reset the position in the write buffer.
            packet.Translate(writeTranslator);

            int packetStreamLength = (int)memoryStream.Position;

            // Now write in the actual packet length
            memoryStream.Position = 1;
            _binaryWriter.Write(packetStreamLength - 5);

            nodeStream.Write(memoryStream.GetBuffer(), 0, packetStreamLength);
        }
    }
}
