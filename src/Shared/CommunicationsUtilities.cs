// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
#if FEATURE_SECURITY_PRINCIPAL_WINDOWS
using System.Security.Principal;
#endif
using System.Threading;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

#if !CLR2COMPATIBILITY
using Microsoft.Build.Shared.Debugging;
#endif
#if !FEATURE_APM
using System.Threading.Tasks;
#endif

#nullable disable

namespace Microsoft.Build.Internal
{
    /// <summary>
    /// Enumeration of all possible (currently supported) options for handshakes.
    /// </summary>
    [Flags]
    internal enum HandshakeOptions
    {
        None = 0,

        /// <summary>
        /// Process is a TaskHost
        /// </summary>
        TaskHost = 1,

        /// <summary>
        /// Using the 2.0 CLR
        /// </summary>
        CLR2 = 2,

        /// <summary>
        /// 64-bit Intel process
        /// </summary>
        X64 = 4,

        /// <summary>
        /// Node reuse enabled
        /// </summary>
        NodeReuse = 8,

        /// <summary>
        /// Building with BelowNormal priority
        /// </summary>
        LowPriority = 16,

        /// <summary>
        /// Building with administrator privileges
        /// </summary>
        Administrator = 32,

        /// <summary>
        /// Using the .NET Core/.NET 5.0+ runtime
        /// </summary>
        NET = 64,

        /// <summary>
        /// ARM64 process
        /// </summary>
        Arm64 = 128,
    }

    internal class Handshake
    {
        protected readonly int options;
        protected readonly int salt;
        protected readonly int fileVersionMajor;
        protected readonly int fileVersionMinor;
        protected readonly int fileVersionBuild;
        protected readonly int fileVersionPrivate;
        private readonly int sessionId;

        protected internal Handshake(HandshakeOptions nodeType)
        {
            const int handshakeVersion = (int)CommunicationsUtilities.handshakeVersion;

            // We currently use 7 bits of this 32-bit integer. Very old builds will instantly reject any handshake that does not start with F5 or 06; slightly old builds always lead with 00.
            // This indicates in the first byte that we are a modern build.
            options = (int)nodeType | (handshakeVersion << 24);
            CommunicationsUtilities.Trace("Building handshake for node type {0}, (version {1}): options {2}.", nodeType, handshakeVersion, options);

            string handshakeSalt = Environment.GetEnvironmentVariable("MSBUILDNODEHANDSHAKESALT");
            CommunicationsUtilities.Trace("Handshake salt is " + handshakeSalt);
            string toolsDirectory = BuildEnvironmentHelper.Instance.MSBuildToolsDirectoryRoot;
            CommunicationsUtilities.Trace("Tools directory root is " + toolsDirectory);
            salt = CommunicationsUtilities.GetHashCode(handshakeSalt + toolsDirectory);
            Version fileVersion = new Version(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion);
            fileVersionMajor = fileVersion.Major;
            fileVersionMinor = fileVersion.Minor;
            fileVersionBuild = fileVersion.Build;
            fileVersionPrivate = fileVersion.Revision;
            sessionId = Process.GetCurrentProcess().SessionId;
        }

        // This is used as a key, so it does not need to be human readable.
        public override string ToString()
        {
            return String.Format("{0} {1} {2} {3} {4} {5} {6}", options, salt, fileVersionMajor, fileVersionMinor, fileVersionBuild, fileVersionPrivate, sessionId);
        }

        public virtual int[] RetrieveHandshakeComponents()
        {
            return
            [
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(options),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(salt),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionMajor),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionMinor),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionBuild),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionPrivate),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(sessionId)
            ];
        }

        public virtual string GetKey() => $"{options} {salt} {fileVersionMajor} {fileVersionMinor} {fileVersionBuild} {fileVersionPrivate} {sessionId}".ToString(CultureInfo.InvariantCulture);

        public virtual byte? ExpectedVersionInFirstByte => CommunicationsUtilities.handshakeVersion;
    }

    internal sealed class ServerNodeHandshake : Handshake
    {
        /// <summary>
        /// Caching computed hash.
        /// </summary>
        private string _computedHash = null;

        public override byte? ExpectedVersionInFirstByte => null;

        internal ServerNodeHandshake(HandshakeOptions nodeType)
            : base(nodeType)
        {
        }

        public override int[] RetrieveHandshakeComponents()
        {
            return
            [
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(options),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(salt),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionMajor),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionMinor),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionBuild),
                CommunicationsUtilities.AvoidEndOfHandshakeSignal(fileVersionPrivate),
            ];
        }

        public override string GetKey()
        {
            return $"{options} {salt} {fileVersionMajor} {fileVersionMinor} {fileVersionBuild} {fileVersionPrivate}"
                .ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Computes Handshake stable hash string representing whole state of handshake.
        /// </summary>
        public string ComputeHash()
        {
            if (_computedHash == null)
            {
                var input = GetKey();
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                _computedHash = Convert.ToBase64String(bytes)
                    .Replace("/", "_")
                    .Replace("=", string.Empty);
            }
            return _computedHash;
        }
    }

    /// <summary>
    /// This class contains utility methods for the MSBuild engine.
    /// </summary>
    internal static class CommunicationsUtilities
    {
        /// <summary>
        /// Indicates to the NodeEndpoint that all the various parts of the Handshake have been sent.
        /// </summary>
        private const int EndOfHandshakeSignal = -0x2a2a2a2a;

        /// <summary>
        /// The version of the handshake. This should be updated each time the handshake structure is altered.
        /// </summary>
        internal const byte handshakeVersion = 0x01;

        /// <summary>
        /// The timeout to connect to a node.
        /// </summary>
        private const int DefaultNodeConnectionTimeout = 900 * 1000; // 15 minutes; enough time that a dev will typically do another build in this time

        /// <summary>
        /// Whether to trace communications
        /// </summary>
        private static bool s_trace = Traits.Instance.DebugNodeCommunication;

        /// <summary>
        /// Lock trace to ensure we are logging in serial fashion.
        /// </summary>
        private static readonly object s_traceLock = new();

        /// <summary>
        /// Place to dump trace
        /// </summary>
        private static string s_debugDumpPath;

        /// <summary>
        /// Ticks at last time logged
        /// </summary>
        private static long s_lastLoggedTicks = DateTime.UtcNow.Ticks;

        /// <summary>
        /// Delegate to debug the communication utilities.
        /// </summary>
        internal delegate void LogDebugCommunications(string format, params object[] stuff);

        /// <summary>
        /// Gets or sets the node connection timeout.
        /// </summary>
        internal static int NodeConnectionTimeout
        {
            get { return GetIntegerVariableOrDefault("MSBUILDNODECONNECTIONTIMEOUT", DefaultNodeConnectionTimeout); }
        }

#if NETFRAMEWORK
        /// <summary>
        /// Get environment block.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern unsafe char* GetEnvironmentStrings();

        /// <summary>
        /// Free environment block.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern unsafe bool FreeEnvironmentStrings(char* pStrings);

        /// <summary>
        /// Set environment variable P/Invoke.
        /// </summary>
        [DllImport("kernel32.dll", EntryPoint = "SetEnvironmentVariable", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetEnvironmentVariableNative(string name, string value);

        /// <summary>
        /// Sets an environment variable using P/Invoke to workaround the .NET Framework BCL implementation.
        /// </summary>
        /// <remarks>
        /// .NET Framework implementation of SetEnvironmentVariable checks the length of the value and throws an exception if
        /// it's greater than or equal to 32,767 characters. This limitation does not exist on modern Windows or .NET.
        /// </remarks>
        internal static void SetEnvironmentVariable(string name, string value)
        {
            if (!SetEnvironmentVariableNative(name, value))
            {
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
        }

        /// <summary>
        /// Returns key value pairs of environment variables in a new dictionary
        /// with a case-insensitive key comparer.
        /// </summary>
        /// <remarks>
        /// Copied from the BCL implementation to eliminate some expensive security asserts on .NET Framework.
        /// </remarks>
        internal static Dictionary<string, string> GetEnvironmentVariables()
        {
#if !CLR2COMPATIBILITY
            // The DebugUtils static constructor can set the MSBUILDDEBUGPATH environment variable to propagate the debug path to out of proc nodes.
            // Need to ensure that constructor is called before this method returns in order to capture its env var write.
            // Otherwise the env var is not captured and thus gets deleted when RequiestBuilder resets the environment based on the cached results of this method.
            ErrorUtilities.VerifyThrowInternalNull(DebugUtils.ProcessInfoString, nameof(DebugUtils.DebugPath));
#endif

            Dictionary<string, string> table = new Dictionary<string, string>(200, StringComparer.OrdinalIgnoreCase); // Razzle has 150 environment variables

            unsafe
            {
                char* pEnvironmentBlock = null;

                try
                {
                    pEnvironmentBlock = GetEnvironmentStrings();
                    if (pEnvironmentBlock == null)
                    {
                        throw new OutOfMemoryException();
                    }

                    // Search for terminating \0\0 (two unicode \0's).
                    char* pEnvironmentBlockEnd = pEnvironmentBlock;
                    while (!(*pEnvironmentBlockEnd == '\0' && *(pEnvironmentBlockEnd + 1) == '\0'))
                    {
                        pEnvironmentBlockEnd++;
                    }
                    long stringBlockLength = pEnvironmentBlockEnd - pEnvironmentBlock;

                    // Copy strings out, parsing into pairs and inserting into the table.
                    // The first few environment variable entries start with an '='!
                    // The current working directory of every drive (except for those drives
                    // you haven't cd'ed into in your DOS window) are stored in the
                    // environment block (as =C:=pwd) and the program's exit code is
                    // as well (=ExitCode=00000000)  Skip all that start with =.
                    // Read docs about Environment Blocks on MSDN's CreateProcess page.

                    // Format for GetEnvironmentStrings is:
                    // (=HiddenVar=value\0 | Variable=value\0)* \0
                    // See the description of Environment Blocks in MSDN's
                    // CreateProcess page (null-terminated array of null-terminated strings).
                    // Note the =HiddenVar's aren't always at the beginning.
                    for (int i = 0; i < stringBlockLength; i++)
                    {
                        int startKey = i;

                        // Skip to key
                        // On some old OS, the environment block can be corrupted.
                        // Some lines will not have '=', so we need to check for '\0'.
                        while (*(pEnvironmentBlock + i) != '=' && *(pEnvironmentBlock + i) != '\0')
                        {
                            i++;
                        }

                        if (*(pEnvironmentBlock + i) == '\0')
                        {
                            continue;
                        }

                        // Skip over environment variables starting with '='
                        if (i - startKey == 0)
                        {
                            while (*(pEnvironmentBlock + i) != 0)
                            {
                                i++;
                            }

                            continue;
                        }

                        string key = new string(pEnvironmentBlock, startKey, i - startKey);
                        i++;

                        // skip over '='
                        int startValue = i;

                        while (*(pEnvironmentBlock + i) != 0)
                        {
                            // Read to end of this entry
                            i++;
                        }

                        string value = new string(pEnvironmentBlock, startValue, i - startValue);

                        // skip over 0 handled by for loop's i++
                        table[key] = value;
                    }
                }
                finally
                {
                    if (pEnvironmentBlock != null)
                    {
                        FreeEnvironmentStrings(pEnvironmentBlock);
                    }
                }
            }

            return table;
        }

#else // NETFRAMEWORK

        /// <summary>
        /// Sets an environment variable using <see cref="Environment.SetEnvironmentVariable(string,string)" />.
        /// </summary>
        internal static void SetEnvironmentVariable(string name, string value)
            => Environment.SetEnvironmentVariable(name, value);

        /// <summary>
        /// Returns key value pairs of environment variables in a new dictionary
        /// with a case-insensitive key comparer.
        /// </summary>
        internal static Dictionary<string, string> GetEnvironmentVariables()
        {
            var vars = Environment.GetEnvironmentVariables();

            Dictionary<string, string> table = new Dictionary<string, string>(vars.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var key in vars.Keys)
            {
                table[(string)key] = (string)vars[key];
            }
            return table;
        }
#endif // NETFRAMEWORK

        /// <summary>
        /// Updates the environment to match the provided dictionary.
        /// </summary>
        internal static void SetEnvironment(IDictionary<string, string> newEnvironment)
        {
            if (newEnvironment != null)
            {
                // First, delete all no longer set variables
                Dictionary<string, string> currentEnvironment = GetEnvironmentVariables();
                foreach (KeyValuePair<string, string> entry in currentEnvironment)
                {
                    if (!newEnvironment.ContainsKey(entry.Key))
                    {
                        SetEnvironmentVariable(entry.Key, null);
                    }
                }

                // Then, make sure the new ones have their new values.
                foreach (KeyValuePair<string, string> entry in newEnvironment)
                {
                    if (!currentEnvironment.TryGetValue(entry.Key, out string currentValue) || currentValue != entry.Value)
                    {
                        SetEnvironmentVariable(entry.Key, entry.Value);
                    }
                }
            }
        }

#nullable enable
        /// <summary>
        /// Indicate to the client that all elements of the Handshake have been sent.
        /// </summary>
        internal static void WriteEndOfHandshakeSignal(this PipeStream stream)
        {
            stream.WriteIntForHandshake(EndOfHandshakeSignal);
        }

        /// <summary>
        /// Extension method to write a series of bytes to a stream
        /// </summary>
        internal static void WriteIntForHandshake(this PipeStream stream, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);

            // We want to read the long and send it from left to right (this means big endian)
            // if we are little endian we need to reverse the array to keep the left to right reading
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            ErrorUtilities.VerifyThrow(bytes.Length == 4, "Int should be 4 bytes");

            stream.Write(bytes, 0, bytes.Length);
        }

#pragma warning disable SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        internal static void ReadEndOfHandshakeSignal(
            this PipeStream stream,
            bool isProvider
#if NETCOREAPP2_1_OR_GREATER
            , int timeout
#endif
            )
#pragma warning restore SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        {
            // Accept only the first byte of the EndOfHandshakeSignal
#pragma warning disable SA1111, SA1009 // Closing parenthesis should be on line of last parameter
            int valueRead = stream.ReadIntForHandshake(
                byteToAccept: null
#if NETCOREAPP2_1_OR_GREATER
            , timeout
#endif
                );
#pragma warning restore SA1111, SA1009 // Closing parenthesis should be on line of last parameter

            if (valueRead != EndOfHandshakeSignal)
            {
                if (isProvider)
                {
                    CommunicationsUtilities.Trace("Handshake failed on part {0}. Probably the client is a different MSBuild build.", valueRead);
                }
                else
                {
                    CommunicationsUtilities.Trace("Expected end of handshake signal but received {0}. Probably the host is a different MSBuild build.", valueRead);
                }
                throw new InvalidOperationException();
            }
        }

#pragma warning disable SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        /// <summary>
        /// Extension method to read a series of bytes from a stream.
        /// If specified, leading byte matches one in the supplied array if any, returns rejection byte and throws IOException.
        /// </summary>
        internal static int ReadIntForHandshake(this PipeStream stream, byte? byteToAccept
#if NETCOREAPP2_1_OR_GREATER
            , int timeout
#endif
            )
#pragma warning restore SA1111, SA1009 // Closing parenthesis should be on line of last parameter
        {
            byte[] bytes = new byte[4];

#if NETCOREAPP2_1_OR_GREATER
            if (!NativeMethodsShared.IsWindows)
            {
                // Enforce a minimum timeout because the Windows code can pass
                // a timeout of 0 for the connection, but that doesn't work for
                // the actual timeout here.
                timeout = Math.Max(timeout, 50);

                // A legacy MSBuild.exe won't try to connect to MSBuild running
                // in a dotnet host process, so we can read the bytes simply.
                var readTask = stream.ReadAsync(bytes, 0, bytes.Length);

                // Manual timeout here because the timeout passed to Connect() just before
                // calling this method does not apply on UNIX domain socket-based
                // implementations of PipeStream.
                // https://github.com/dotnet/corefx/issues/28791
                if (!readTask.Wait(timeout))
                {
                    throw new IOException(string.Format(CultureInfo.InvariantCulture, "Did not receive return handshake in {0}ms", timeout));
                }

                readTask.GetAwaiter().GetResult();
            }
            else
#endif
            {
                int bytesRead = stream.Read(bytes, 0, bytes.Length);
                if (bytesRead != bytes.Length)
                {
                    // We've unexpectly reached end of stream.
                    // We are now in a bad state, disconnect on our end
                    throw new IOException(String.Format(CultureInfo.InvariantCulture, "Unexpected end of stream while reading for handshake"));
                }

                // Legacy approach with an early-abort for connection attempts from ancient MSBuild.exes
                if (byteToAccept != null && byteToAccept != bytes[0])
                {
                    stream.WriteIntForHandshake(0x0F0F0F0F);
                    stream.WriteIntForHandshake(0x0F0F0F0F);
                    throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture, "Client: rejected old host. Received byte {0} instead of {1}.", bytes[0], byteToAccept));
                }
            }

            int result;

            try
            {
                // We want to read the long and send it from left to right (this means big endian)
                // If we are little endian the stream has already been reversed by the sender, we need to reverse it again to get the original number
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }

                result = BitConverter.ToInt32(bytes, 0 /* start index */);
            }
            catch (ArgumentException ex)
            {
                throw new IOException(String.Format(CultureInfo.InvariantCulture, "Failed to convert the handshake to big-endian. {0}", ex.Message));
            }

            return result;
        }
#nullable disable

#if !FEATURE_APM
        internal static async Task<int> ReadAsync(Stream stream, byte[] buffer, int bytesToRead)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < bytesToRead)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytesRead, bytesToRead - totalBytesRead), CancellationToken.None);
                if (bytesRead == 0)
                {
                    return totalBytesRead;
                }
                totalBytesRead += bytesRead;
            }
            return totalBytesRead;
        }
#endif

        /// <summary>
        /// Given the appropriate information, return the equivalent HandshakeOptions.
        /// </summary>
        internal static HandshakeOptions GetHandshakeOptions(bool taskHost, string architectureFlagToSet = null, bool nodeReuse = false, bool lowPriority = false, IDictionary<string, string> taskHostParameters = null)
        {
            HandshakeOptions context = taskHost ? HandshakeOptions.TaskHost : HandshakeOptions.None;

            int clrVersion = 0;

            // We don't know about the TaskHost.
            if (taskHost)
            {
                // No parameters given, default to current
                if (taskHostParameters == null)
                {
                    clrVersion = typeof(bool).GetTypeInfo().Assembly.GetName().Version.Major;
                    architectureFlagToSet = XMakeAttributes.GetCurrentMSBuildArchitecture();
                }
                else // Figure out flags based on parameters given
                {
                    ErrorUtilities.VerifyThrow(taskHostParameters.TryGetValue(XMakeAttributes.runtime, out string runtimeVersion), "Should always have an explicit runtime when we call this method.");
                    ErrorUtilities.VerifyThrow(taskHostParameters.TryGetValue(XMakeAttributes.architecture, out string architecture), "Should always have an explicit architecture when we call this method.");

                    if (runtimeVersion.Equals(XMakeAttributes.MSBuildRuntimeValues.clr2, StringComparison.OrdinalIgnoreCase))
                    {
                        clrVersion = 2;
                    }
                    else if (runtimeVersion.Equals(XMakeAttributes.MSBuildRuntimeValues.clr4, StringComparison.OrdinalIgnoreCase))
                    {
                        clrVersion = 4;
                    }
                    else if (runtimeVersion.Equals(XMakeAttributes.MSBuildRuntimeValues.net, StringComparison.OrdinalIgnoreCase))
                    {
                        clrVersion = 5;
                    }
                    else
                    {
                        ErrorUtilities.ThrowInternalErrorUnreachable();
                    }

                    architectureFlagToSet = architecture;
                }
            }

            if (!string.IsNullOrEmpty(architectureFlagToSet))
            {
                if (architectureFlagToSet.Equals(XMakeAttributes.MSBuildArchitectureValues.x64, StringComparison.OrdinalIgnoreCase))
                {
                    context |= HandshakeOptions.X64;
                }
                else if (architectureFlagToSet.Equals(XMakeAttributes.MSBuildArchitectureValues.arm64, StringComparison.OrdinalIgnoreCase))
                {
                    context |= HandshakeOptions.Arm64;
                }
            }

            switch (clrVersion)
            {
                case 0:
                // Not a taskhost, runtime must match
                case 4:
                    // Default for MSBuild running on .NET Framework 4,
                    // not represented in handshake
                    break;
                case 2:
                    context |= HandshakeOptions.CLR2;
                    break;
                case >= 5:
                    context |= HandshakeOptions.NET;
                    break;
                default:
                    ErrorUtilities.ThrowInternalErrorUnreachable();
                    break;
            }

            if (nodeReuse)
            {
                context |= HandshakeOptions.NodeReuse;
            }
            if (lowPriority)
            {
                context |= HandshakeOptions.LowPriority;
            }
#if FEATURE_SECURITY_PRINCIPAL_WINDOWS
            // If we are running in elevated privs, we will only accept a handshake from an elevated process as well.
            // Both the client and the host will calculate this separately, and the idea is that if they come out the same
            // then we can be sufficiently confident that the other side has the same elevation level as us.  This is complementary
            // to the username check which is also done on connection.
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                context |= HandshakeOptions.Administrator;
            }
#endif
            return context;
        }

        /// <summary>
        /// Gets the value of an integer environment variable, or returns the default if none is set or it cannot be converted.
        /// </summary>
        internal static int GetIntegerVariableOrDefault(string environmentVariable, int defaultValue)
        {
            string environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
            if (String.IsNullOrEmpty(environmentValue))
            {
                return defaultValue;
            }

            int localDefaultValue;
            if (Int32.TryParse(environmentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out localDefaultValue))
            {
                defaultValue = localDefaultValue;
            }

            return defaultValue;
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T>(string format, T arg0)
        {
            Trace(nodeId: -1, format, arg0);
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T>(int nodeId, string format, T arg0)
        {
            if (s_trace)
            {
                TraceCore(nodeId, string.Format(format, arg0));
            }
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T0, T1>(string format, T0 arg0, T1 arg1)
        {
            Trace(nodeId: -1, format, arg0, arg1);
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T0, T1>(int nodeId, string format, T0 arg0, T1 arg1)
        {
            if (s_trace)
            {
                TraceCore(nodeId, string.Format(format, arg0, arg1));
            }
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2)
        {
            Trace(nodeId: -1, format, arg0, arg1, arg2);
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace<T0, T1, T2>(int nodeId, string format, T0 arg0, T1 arg1, T2 arg2)
        {
            if (s_trace)
            {
                TraceCore(nodeId, string.Format(format, arg0, arg1, arg2));
            }
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace(string format, params object[] args)
        {
            Trace(nodeId: -1, format, args);
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        internal static void Trace(int nodeId, string format, params object[] args)
        {
            if (s_trace)
            {
                string message = string.Format(CultureInfo.CurrentCulture, format, args);
                TraceCore(nodeId, message);
            }
        }

        internal static void Trace(int nodeId, string message)
        {
            if (s_trace)
            {
                TraceCore(nodeId, message);
            }
        }

        /// <summary>
        /// Writes trace information to a log file
        /// </summary>
        private static void TraceCore(int nodeId, string message)
        {
            lock (s_traceLock)
            {
                s_debugDumpPath ??=
#if CLR2COMPATIBILITY
                    Environment.GetEnvironmentVariable("MSBUILDDEBUGPATH");
#else
                        DebugUtils.DebugPath;
#endif

                if (String.IsNullOrEmpty(s_debugDumpPath))
                {
                    s_debugDumpPath = FileUtilities.TempFileDirectory;
                }
                else
                {
                    Directory.CreateDirectory(s_debugDumpPath);
                }

                try
                {
                    string fileName = @"MSBuild_CommTrace_PID_{0}";
                    if (nodeId != -1)
                    {
                        fileName += "_node_" + nodeId;
                    }

                    fileName += ".txt";

                    using (StreamWriter file = FileUtilities.OpenWrite(
                        String.Format(CultureInfo.CurrentCulture, Path.Combine(s_debugDumpPath, fileName), Process.GetCurrentProcess().Id, nodeId), append: true))
                    {
                        long now = DateTime.UtcNow.Ticks;
                        float millisecondsSinceLastLog = (float)(now - s_lastLoggedTicks) / 10000L;
                        s_lastLoggedTicks = now;
                        file.WriteLine("{0} (TID {1}) {2,15} +{3,10}ms: {4}", Thread.CurrentThread.Name, Thread.CurrentThread.ManagedThreadId, now, millisecondsSinceLastLog, message);
                    }
                }
                catch (IOException)
                {
                    // Ignore
                }
            }
        }

        /// <summary>
        /// Gets a hash code for this string.  If strings A and B are such that A.Equals(B), then
        /// they will return the same hash code.
        /// This is as implemented in CLR String.GetHashCode() [ndp\clr\src\BCL\system\String.cs]
        /// but stripped out architecture specific defines
        /// that causes the hashcode to be different and this causes problem in cross-architecture handshaking
        /// </summary>
        internal static int GetHashCode(string fileVersion)
        {
            unsafe
            {
                fixed (char* src = fileVersion)
                {
                    int hash1 = (5381 << 16) + 5381;
                    int hash2 = hash1;

                    int* pint = (int*)src;
                    int len = fileVersion.Length;
                    while (len > 0)
                    {
                        hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ pint[0];
                        if (len <= 2)
                        {
                            break;
                        }

                        hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ pint[1];
                        pint += 2;
                        len -= 4;
                    }

                    return hash1 + (hash2 * 1566083941);
                }
            }
        }

        internal static int AvoidEndOfHandshakeSignal(int x)
        {
            return x == EndOfHandshakeSignal ? ~x : x;
        }
    }
}
