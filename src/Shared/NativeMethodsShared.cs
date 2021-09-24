// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Reflection;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Interop methods.
    /// </summary>
    internal static class NativeMethodsShared
    {
        #region Constants

        internal const uint ERROR_INSUFFICIENT_BUFFER = 0x8007007A;
        internal const uint STARTUP_LOADER_SAFEMODE = 0x10;
        internal const uint S_OK = 0x0;
        internal const uint S_FALSE = 0x1;
        internal const uint ERROR_ACCESS_DENIED = 0x5;
        internal const uint ERROR_FILE_NOT_FOUND = 0x80070002;
        internal const uint FUSION_E_PRIVATE_ASM_DISALLOWED = 0x80131044; // Tried to find unsigned assembly in GAC
        internal const uint RUNTIME_INFO_DONT_SHOW_ERROR_DIALOG = 0x40;
        internal const uint FILE_TYPE_CHAR = 0x0002;
        internal const Int32 STD_OUTPUT_HANDLE = -11;
        internal const uint RPC_S_CALLPENDING = 0x80010115;
        internal const uint E_ABORT = (uint)0x80004004;

        internal const int FILE_ATTRIBUTE_READONLY = 0x00000001;
        internal const int FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        internal const int FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

        /// <summary>
        /// Default buffer size to use when dealing with the Windows API.
        /// </summary>
        internal const int MAX_PATH = 260;

        private const string kernel32Dll = "kernel32.dll";
        private const string mscoreeDLL = "mscoree.dll";

        private const string WINDOWS_FILE_SYSTEM_REGISTRY_KEY = @"SYSTEM\CurrentControlSet\Control\FileSystem";
        private const string WINDOWS_LONG_PATHS_ENABLED_VALUE_NAME = "LongPathsEnabled";

        internal static DateTime MinFileDate { get; } = DateTime.FromFileTimeUtc(0);

#if FEATURE_HANDLEREF
        internal static HandleRef NullHandleRef = new HandleRef(null, IntPtr.Zero);
#endif

        internal static IntPtr NullIntPtr = new IntPtr(0);

        // As defined in winnt.h:
        internal const ushort PROCESSOR_ARCHITECTURE_INTEL = 0;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM = 5;
        internal const ushort PROCESSOR_ARCHITECTURE_IA64 = 6;
        internal const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM64 = 12;

        internal const uint INFINITE = 0xFFFFFFFF;
        internal const uint WAIT_ABANDONED_0 = 0x00000080;
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint WAIT_TIMEOUT = 0x00000102;

#if FEATURE_CHARSET_AUTO
        internal const CharSet AutoOrUnicode = CharSet.Auto;
#else
        internal const CharSet AutoOrUnicode = CharSet.Unicode;
#endif

        #endregion

        #region Enums

        private enum PROCESSINFOCLASS : int
        {
            ProcessBasicInformation = 0,
            ProcessQuotaLimits,
            ProcessIoCounters,
            ProcessVmCounters,
            ProcessTimes,
            ProcessBasePriority,
            ProcessRaisePriority,
            ProcessDebugPort,
            ProcessExceptionPort,
            ProcessAccessToken,
            ProcessLdtInformation,
            ProcessLdtSize,
            ProcessDefaultHardErrorMode,
            ProcessIoPortHandlers, // Note: this is kernel mode only
            ProcessPooledUsageAndLimits,
            ProcessWorkingSetWatch,
            ProcessUserModeIOPL,
            ProcessEnableAlignmentFaultFixup,
            ProcessPriorityClass,
            ProcessWx86Information,
            ProcessHandleCount,
            ProcessAffinityMask,
            ProcessPriorityBoost,
            MaxProcessInfoClass
        };

        private enum eDesiredAccess : int
        {
            DELETE = 0x00010000,
            READ_CONTROL = 0x00020000,
            WRITE_DAC = 0x00040000,
            WRITE_OWNER = 0x00080000,
            SYNCHRONIZE = 0x00100000,
            STANDARD_RIGHTS_ALL = 0x001F0000,

            PROCESS_TERMINATE = 0x0001,
            PROCESS_CREATE_THREAD = 0x0002,
            PROCESS_SET_SESSIONID = 0x0004,
            PROCESS_VM_OPERATION = 0x0008,
            PROCESS_VM_READ = 0x0010,
            PROCESS_VM_WRITE = 0x0020,
            PROCESS_DUP_HANDLE = 0x0040,
            PROCESS_CREATE_PROCESS = 0x0080,
            PROCESS_SET_QUOTA = 0x0100,
            PROCESS_SET_INFORMATION = 0x0200,
            PROCESS_QUERY_INFORMATION = 0x0400,
            PROCESS_ALL_ACCESS = SYNCHRONIZE | 0xFFF
        }
#pragma warning disable 0649, 0169
        internal enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore,
            RelationNumaNode,
            RelationCache,
            RelationProcessorPackage,
            RelationGroup,
            RelationAll = 0xffff
        }
        internal struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public uint Size;
            public PROCESSOR_RELATIONSHIP Processor;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags;
            private byte EfficiencyClass;
            private fixed byte Reserved[20];
            public ushort GroupCount;
            public IntPtr GroupInfo;
        }
#pragma warning restore 0169, 0149

        /// <summary>
        /// Flags for CoWaitForMultipleHandles
        /// </summary>
        [Flags]
        public enum COWAIT_FLAGS : int
        {
            /// <summary>
            /// Exit when a handle is signaled.
            /// </summary>
            COWAIT_NONE = 0,

            /// <summary>
            /// Exit when all handles are signaled AND a message is received.
            /// </summary>
            COWAIT_WAITALL = 0x00000001,

            /// <summary>
            /// Exit when an RPC call is serviced.
            /// </summary>
            COWAIT_ALERTABLE = 0x00000002
        }

        /// <summary>
        /// Processor architecture values
        /// </summary>
        internal enum ProcessorArchitectures
        {
            // Intel 32 bit
            X86,

            // AMD64 64 bit
            X64,

            // Itanium 64
            IA64,

            // ARM
            ARM,

            // ARM64
            ARM64,

            // Who knows
            Unknown
        }

        #endregion

        #region Structs

        /// <summary>
        /// Structure that contain information about the system on which we are running
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            // This is a union of a DWORD and a struct containing 2 WORDs.
            internal ushort wProcessorArchitecture;
            internal ushort wReserved;

            internal uint dwPageSize;
            internal IntPtr lpMinimumApplicationAddress;
            internal IntPtr lpMaximumApplicationAddress;
            internal IntPtr dwActiveProcessorMask;
            internal uint dwNumberOfProcessors;
            internal uint dwProcessorType;
            internal uint dwAllocationGranularity;
            internal ushort wProcessorLevel;
            internal ushort wProcessorRevision;
        }

        /// <summary>
        /// Wrap the intptr returned by OpenProcess in a safe handle.
        /// </summary>
        internal class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            // Create a SafeHandle, informing the base class
            // that this SafeHandle instance "owns" the handle,
            // and therefore SafeHandle should call
            // our ReleaseHandle method when the SafeHandle
            // is no longer in use
            private SafeProcessHandle() : base(true)
            {
            }
            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        /// <summary>
        /// Contains information about the current state of both physical and virtual memory, including extended memory
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = AutoOrUnicode)]
        internal class MemoryStatus
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="T:MemoryStatus"/> class.
            /// </summary>
            public MemoryStatus()
            {
#if (CLR2COMPATIBILITY)
            _length = (uint)Marshal.SizeOf(typeof(NativeMethodsShared.MemoryStatus));
#else
            _length = (uint)Marshal.SizeOf<NativeMethodsShared.MemoryStatus>();
#endif
            }

            /// <summary>
            /// Size of the structure, in bytes. You must set this member before calling GlobalMemoryStatusEx.
            /// </summary>
            private uint _length;

            /// <summary>
            /// Number between 0 and 100 that specifies the approximate percentage of physical
            /// memory that is in use (0 indicates no memory use and 100 indicates full memory use).
            /// </summary>
            public uint MemoryLoad;

            /// <summary>
            /// Total size of physical memory, in bytes.
            /// </summary>
            public ulong TotalPhysical;

            /// <summary>
            /// Size of physical memory available, in bytes.
            /// </summary>
            public ulong AvailablePhysical;

            /// <summary>
            /// Size of the committed memory limit, in bytes. This is physical memory plus the
            /// size of the page file, minus a small overhead.
            /// </summary>
            public ulong TotalPageFile;

            /// <summary>
            /// Size of available memory to commit, in bytes. The limit is ullTotalPageFile.
            /// </summary>
            public ulong AvailablePageFile;

            /// <summary>
            /// Total size of the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong TotalVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the user mode portion of the virtual
            /// address space of the calling process, in bytes.
            /// </summary>
            public ulong AvailableVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the extended portion of the virtual
            /// address space of the calling process, in bytes.
            /// </summary>
            public ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public uint ExitStatus;
            public IntPtr PebBaseAddress;
            public UIntPtr AffinityMask;
            public int BasePriority;
            public UIntPtr UniqueProcessId;
            public UIntPtr InheritedFromUniqueProcessId;

            public uint Size
            {
                get
                {
                    unsafe
                    {
                        return (uint)sizeof(PROCESS_BASIC_INFORMATION);
                    }
                }
            }
        };

        /// <summary>
        /// Contains information about a file or directory; used by GetFileAttributesEx.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WIN32_FILE_ATTRIBUTE_DATA
        {
            internal int fileAttributes;
            internal uint ftCreationTimeLow;
            internal uint ftCreationTimeHigh;
            internal uint ftLastAccessTimeLow;
            internal uint ftLastAccessTimeHigh;
            internal uint ftLastWriteTimeLow;
            internal uint ftLastWriteTimeHigh;
            internal uint fileSizeHigh;
            internal uint fileSizeLow;
        }

        /// <summary>
        /// Contains the security descriptor for an object and specifies whether
        /// the handle retrieved by specifying this structure is inheritable.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal class SecurityAttributes
        {
            public SecurityAttributes()
            {
#if (CLR2COMPATIBILITY)
            _nLength = (uint)Marshal.SizeOf(typeof(NativeMethodsShared.SecurityAttributes));
#else
            _nLength = (uint)Marshal.SizeOf<NativeMethodsShared.SecurityAttributes>();
#endif
            }

            private uint _nLength;

            public IntPtr lpSecurityDescriptor;

            public bool bInheritHandle;
        }

        private class SystemInformationData
        {
            /// <summary>
            /// Architecture as far as the current process is concerned.
            /// It's x86 in wow64 (native architecture is x64 in that case).
            /// Otherwise it's the same as the native architecture.
            /// </summary>
            public readonly ProcessorArchitectures ProcessorArchitectureType;

            /// <summary>
            /// Actual architecture of the system.
            /// </summary>
            public readonly ProcessorArchitectures ProcessorArchitectureTypeNative;

            /// <summary>
            /// Convert SYSTEM_INFO architecture values to the internal enum
            /// </summary>
            /// <param name="arch"></param>
            /// <returns></returns>
            private static ProcessorArchitectures ConvertSystemArchitecture(ushort arch)
            {
                return arch switch
                {
                    PROCESSOR_ARCHITECTURE_INTEL => ProcessorArchitectures.X86,
                    PROCESSOR_ARCHITECTURE_AMD64 => ProcessorArchitectures.X64,
                    PROCESSOR_ARCHITECTURE_ARM => ProcessorArchitectures.ARM,
                    PROCESSOR_ARCHITECTURE_IA64 => ProcessorArchitectures.IA64,
                    PROCESSOR_ARCHITECTURE_ARM64 => ProcessorArchitectures.ARM64,
                    _ => ProcessorArchitectures.Unknown,
                };
            }

            /// <summary>
            /// Read system info values
            /// </summary>
            public SystemInformationData()
            {
                ProcessorArchitectureType = ProcessorArchitectures.Unknown;
                ProcessorArchitectureTypeNative = ProcessorArchitectures.Unknown;

                if (IsWindows)
                {
                    var systemInfo = new SYSTEM_INFO();

                    GetSystemInfo(ref systemInfo);
                    ProcessorArchitectureType = ConvertSystemArchitecture(systemInfo.wProcessorArchitecture);

                    GetNativeSystemInfo(ref systemInfo);
                    ProcessorArchitectureTypeNative = ConvertSystemArchitecture(systemInfo.wProcessorArchitecture);
                }
                else
                {
                    ProcessorArchitectures processorArchitecture = ProcessorArchitectures.Unknown;
#if !NET35
                    // Get the architecture from the runtime.
                    processorArchitecture = RuntimeInformation.OSArchitecture switch
                    {
                        Architecture.Arm => ProcessorArchitectures.ARM,
                        Architecture.Arm64 =>  ProcessorArchitectures.ARM64,
                        Architecture.X64 => ProcessorArchitectures.X64,
                        Architecture.X86 => ProcessorArchitectures.X86,
                        _ => ProcessorArchitectures.Unknown,
                    };
#endif
                    // Fall back to 'uname -m' to get the architecture.
                    if (processorArchitecture == ProcessorArchitectures.Unknown)
                    {
                        try
                        {
                            // On Unix run 'uname -m' to get the architecture. It's common for Linux and Mac
                            using (
                                var proc =
                                    Process.Start(
                                        new ProcessStartInfo("uname")
                                        {
                                            Arguments = "-m",
                                            UseShellExecute = false,
                                            RedirectStandardOutput = true,
                                            CreateNoWindow = true
                                        }))
                            {
                                string arch = null;
                                if (proc != null)
                                {
                                    arch = proc.StandardOutput.ReadLine();
                                    proc.WaitForExit();
                                }

                                if (!string.IsNullOrEmpty(arch))
                                {
                                    if (arch.StartsWith("x86_64", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessorArchitectureType = ProcessorArchitectures.X64;
                                    }
                                    else if (arch.StartsWith("ia64", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessorArchitectureType = ProcessorArchitectures.IA64;
                                    }
                                    else if (arch.StartsWith("arm", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessorArchitectureType = ProcessorArchitectures.ARM;
                                    }
                                    else if (arch.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessorArchitectureType = ProcessorArchitectures.ARM64;
                                    }
                                    else if (arch.StartsWith("i", StringComparison.OrdinalIgnoreCase)
                                            && arch.EndsWith("86", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessorArchitectureType = ProcessorArchitectures.X86;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Best effort: fall back to Unknown
                        }
                    }

                    ProcessorArchitectureTypeNative = ProcessorArchitectureType = processorArchitecture;
                }
            }
        }

        public static int GetLogicalCoreCount()
        {
            int numberOfCpus = Environment.ProcessorCount;
#if !MONO
            // .NET Core on Windows returns a core count limited to the current NUMA node
            //     https://github.com/dotnet/runtime/issues/29686
            // so always double-check it.
            if (IsWindows
#if NETFRAMEWORK
                // .NET Framework calls Windows APIs that have a core count limit (32/64 depending on process bitness).
                // So if we get a high core count on full framework, double-check it.
                && (numberOfCpus >= 32)
#endif
            )
            {
                var result = GetLogicalCoreCountOnWindows();
                if (result != -1)
                {
                    numberOfCpus = result;
                }
            }
#endif

            return numberOfCpus;
        }

        /// <summary>
        /// Get the exact physical core count on Windows
        /// Useful for getting the exact core count in 32 bits processes,
        /// as Environment.ProcessorCount has a 32-core limit in that case. 
        /// https://github.com/dotnet/runtime/blob/221ad5b728f93489655df290c1ea52956ad8f51c/src/libraries/System.Runtime.Extensions/src/System/Environment.Windows.cs#L171-L210
        /// </summary>
        private unsafe static int GetLogicalCoreCountOnWindows()
        {
            uint len = 0;
            const int ERROR_INSUFFICIENT_BUFFER = 122;

            if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref len) &&
                Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
            {
                // Allocate that much space
                var buffer = new byte[len];
                fixed (byte* bufferPtr = buffer)
                {
                    // Call GetLogicalProcessorInformationEx with the allocated buffer
                    if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, (IntPtr)bufferPtr, ref len))
                    {
                        // Walk each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX in the buffer, where the Size of each dictates how
                        // much space it's consuming.  For each group relation, count the number of active processors in each of its group infos.
                        int processorCount = 0;
                        byte* ptr = bufferPtr;
                        byte* endPtr = bufferPtr + len;
                        while (ptr < endPtr)
                        {
                            var current = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)ptr;
                            if (current->Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                // Flags is 0 if the core has a single logical proc, LTP_PC_SMT if more than one
                                // for now, assume "more than 1" == 2, as it has historically been for hyperthreading
                                processorCount += (current->Processor.Flags == 0) ? 1 : 2;
                            }
                            ptr += current->Size;
                        }
                        return processorCount;
                    }
                }
            }

            return -1;
        }

        #endregion

        #region Member data

        internal static bool HasMaxPath => MaxPath == MAX_PATH;

        /// <summary>
        /// Gets the max path limit of the current OS.
        /// </summary>
        internal static int MaxPath
        {
            get
            {
                if (!IsMaxPathSet)
                {
                    SetMaxPath();
                }
                return _maxPath;
            }
        }

        /// <summary>
        /// Cached value for MaxPath.
        /// </summary>
        private static int _maxPath;

        private static bool IsMaxPathSet { get; set; }

        private static readonly object MaxPathLock = new object();

        private static void SetMaxPath()
        {
            lock (MaxPathLock)
            {
                if (!IsMaxPathSet)
                {
                    bool isMaxPathRestricted = Traits.Instance.EscapeHatches.DisableLongPaths || IsMaxPathLegacyWindows();
                    _maxPath = isMaxPathRestricted ? MAX_PATH : int.MaxValue;
                    IsMaxPathSet = true;
                }
            }
        }

        internal static bool IsMaxPathLegacyWindows()
        {
            try
            {
                return IsWindows && !IsLongPathsEnabledRegistry();
            }
            catch
            {
                return true;
            }
        }

        // CA1416 warns about code that can only run on Windows, but we verified we're running on Windows before this.
        // This is the most reasonable way to resolve this part because other ways would require ifdef'ing on NET472.
#pragma warning disable CA1416
        private static bool IsLongPathsEnabledRegistry()
        {
            using (RegistryKey fileSystemKey = Registry.LocalMachine.OpenSubKey(WINDOWS_FILE_SYSTEM_REGISTRY_KEY))
            {
                object longPathsEnabledValue = fileSystemKey?.GetValue(WINDOWS_LONG_PATHS_ENABLED_VALUE_NAME, 0);
                return fileSystemKey != null && Convert.ToInt32(longPathsEnabledValue) == 1;
            }
        }
#pragma warning restore CA1416

        /// <summary>
        /// Cached value for IsUnixLike (this method is called frequently during evaluation).
        /// </summary>
        private static readonly bool s_isUnixLike = IsLinux || IsOSX || IsBSD;

        /// <summary>
        /// Gets a flag indicating if we are running under a Unix-like system (Mac, Linux, etc.)
        /// </summary>
        internal static bool IsUnixLike
        {
            get { return s_isUnixLike; }
        }

        /// <summary>
        /// Gets a flag indicating if we are running under Linux
        /// </summary>
        internal static bool IsLinux
        {
#if CLR2COMPATIBILITY
            get { return false; }
#else
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Linux); }
#endif
        }

        /// <summary>
        /// Gets a flag indicating if we are running under flavor of BSD (NetBSD, OpenBSD, FreeBSD)
        /// </summary>
        internal static bool IsBSD
        {
#if CLR2COMPATIBILITY
            get { return false; }
#else
            get
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")) ||
                       RuntimeInformation.IsOSPlatform(OSPlatform.Create("NETBSD")) ||
                       RuntimeInformation.IsOSPlatform(OSPlatform.Create("OPENBSD"));
            }
#endif
        }

        private static readonly object IsMonoLock = new object();

        private static bool? _isMono;

        /// <summary>
        /// Gets a flag indicating if we are running under MONO
        /// </summary>
        internal static bool IsMono
        {
            get
            {
                if (_isMono != null) return _isMono.Value;

                lock (IsMonoLock)
                {
                    if (_isMono == null)
                    {
                        // There could be potentially expensive TypeResolve events, so cache IsMono.
                        // Also, VS does not host Mono runtimes, so turn IsMono off when msbuild is running under VS
                        _isMono = !BuildEnvironmentHelper.Instance.RunningInVisualStudio &&
                                  Type.GetType("Mono.Runtime") != null;
                    }
                }

                return _isMono.Value;
            }
        }

#if !CLR2COMPATIBILITY
        private static bool? _isWindows;
#endif

        /// <summary>
        /// Gets a flag indicating if we are running under some version of Windows
        /// </summary>
        internal static bool IsWindows
        {
#if CLR2COMPATIBILITY
            get { return true; }
#else
            get {
                if (_isWindows == null)
                {
                    _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                }
                return _isWindows.Value;
            }
#endif
        }

#if !CLR2COMPATIBILITY
        private static bool? _isOSX;
#endif

        /// <summary>
        /// Gets a flag indicating if we are running under Mac OSX
        /// </summary>
        internal static bool IsOSX
        {
#if CLR2COMPATIBILITY
            get { return false; }
#else
            get {
                if (_isOSX == null)
                {
                    _isOSX = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                }
                return _isOSX.Value;
            }
#endif
        }

        /// <summary>
        /// Gets a string for the current OS. This matches the OS env variable
        /// for Windows (Windows_NT).
        /// </summary>
        internal static string OSName
        {
            get { return IsWindows ? "Windows_NT" : "Unix"; }
        }

        /// <summary>
        /// OS name that can be used for the msbuildExtensionsPathSearchPaths element
        /// for a toolset
        /// </summary>
        internal static string GetOSNameForExtensionsPath()
        {
            return IsOSX ? "osx" : IsUnixLike ? "unix" : "windows";
        }

        internal static bool OSUsesCaseSensitivePaths
        {
            get { return IsLinux; }
        }

        /// <summary>
        /// The base directory for all framework paths in Mono
        /// </summary>
        private static string s_frameworkBasePath;

        /// <summary>
        /// The directory of the current framework
        /// </summary>
        private static string s_frameworkCurrentPath;

        /// <summary>
        /// Gets the currently running framework path
        /// </summary>
        internal static string FrameworkCurrentPath
        {
            get
            {
                if (s_frameworkCurrentPath == null)
                {
                    var baseTypeLocation = AssemblyUtilities.GetAssemblyLocation(typeof(string).GetTypeInfo().Assembly);

                    s_frameworkCurrentPath =
                        Path.GetDirectoryName(baseTypeLocation)
                        ?? string.Empty;
                }

                return s_frameworkCurrentPath;
            }
        }

        /// <summary>
        /// Gets the base directory of all Mono frameworks
        /// </summary>
        internal static string FrameworkBasePath
        {
            get
            {
                if (s_frameworkBasePath == null)
                {
                    var dir = FrameworkCurrentPath;
                    if (dir != string.Empty)
                    {
                        dir = Path.GetDirectoryName(dir);
                    }

                    s_frameworkBasePath = dir ?? string.Empty;
                }

                return s_frameworkBasePath;
            }
        }

        /// <summary>
        /// System information, initialized when required.
        /// </summary>
        /// <remarks>
        /// Initially implemented as <see cref="Lazy{SystemInformationData}"/>, but
        /// that's .NET 4+, and this is used in MSBuildTaskHost.
        /// </remarks>
        private static SystemInformationData SystemInformation
        {
            get
            {
                if (!_systemInformationInitialized)
                {
                    lock (SystemInformationLock)
                    {
                        if (!_systemInformationInitialized)
                        {
                            _systemInformation = new SystemInformationData();
                            _systemInformationInitialized = true;
                        }
                    }
                }
                return _systemInformation;
            }
        }

        private static SystemInformationData _systemInformation;
        private static bool _systemInformationInitialized;
        private static readonly object SystemInformationLock = new object();

        /// <summary>
        /// Architecture getter
        /// </summary>
        internal static ProcessorArchitectures ProcessorArchitecture => SystemInformation.ProcessorArchitectureType;

        /// <summary>
        /// Native architecture getter
        /// </summary>
        internal static ProcessorArchitectures ProcessorArchitectureNative => SystemInformation.ProcessorArchitectureTypeNative;

#endregion

#region Wrapper methods

        /// <summary>
        /// Really truly non pumping wait.
        /// Raw IntPtrs have to be used, because the marshaller does not support arrays of SafeHandle, only
        /// single SafeHandles.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern Int32 WaitForMultipleObjects(uint handle, IntPtr[] handles, bool waitAll, uint milliseconds);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void GetSystemInfo(ref SYSTEM_INFO lpSystemInfo);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void GetNativeSystemInfo(ref SYSTEM_INFO lpSystemInfo);
        
        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType, IntPtr Buffer, ref uint ReturnedLength);

        /// <summary>
        /// Get the last write time of the fullpath to a directory. If the pointed path is not a directory, or
        /// if the directory does not exist, then false is returned and fileModifiedTimeUtc is set DateTime.MinValue.
        /// </summary>
        /// <param name="fullPath">Full path to the file in the filesystem</param>
        /// <param name="fileModifiedTimeUtc">The UTC last write time for the directory</param>
        internal static bool GetLastWriteDirectoryUtcTime(string fullPath, out DateTime fileModifiedTimeUtc)
        {
            // This code was copied from the reference manager, if there is a bug fix in that code, see if the same fix should also be made
            // there
            if (IsWindows)
            {
                fileModifiedTimeUtc = DateTime.MinValue;

                WIN32_FILE_ATTRIBUTE_DATA data = new WIN32_FILE_ATTRIBUTE_DATA();
                bool success = GetFileAttributesEx(fullPath, 0, ref data);
                if (success)
                {
                    if ((data.fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        long dt = ((long)(data.ftLastWriteTimeHigh) << 32) | ((long)data.ftLastWriteTimeLow);
                        fileModifiedTimeUtc = DateTime.FromFileTimeUtc(dt);
                    }
                    else
                    {
                        // Path does not point to a directory
                        success = false;
                    }
                }

                return success;
            }

            if (Directory.Exists(fullPath))
            {
                fileModifiedTimeUtc = Directory.GetLastWriteTimeUtc(fullPath);
                return true;
            }
            else
            {
                fileModifiedTimeUtc = DateTime.MinValue;
                return false;
            }
        }

        /// <summary>
        /// Takes the path and returns the short path
        /// </summary>
        internal static string GetShortFilePath(string path)
        {
            if (!IsWindows)
            {
                return path;
            }

            if (path != null)
            {
                int length = GetShortPathName(path, null, 0);
                int errorCode = Marshal.GetLastWin32Error();

                if (length > 0)
                {
                    StringBuilder fullPathBuffer = new StringBuilder(length);
                    length = GetShortPathName(path, fullPathBuffer, length);
                    errorCode = Marshal.GetLastWin32Error();

                    if (length > 0)
                    {
                        string fullPath = fullPathBuffer.ToString();
                        path = fullPath;
                    }
                }

                if (length == 0 && errorCode != 0)
                {
                    ThrowExceptionForErrorCode(errorCode);
                }
            }

            return path;
        }

        /// <summary>
        /// Takes the path and returns a full path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        internal static string GetLongFilePath(string path)
        {
            if (IsUnixLike)
            {
                return path;
            }

            if (path != null)
            {
                int length = GetLongPathName(path, null, 0);
                int errorCode = Marshal.GetLastWin32Error();

                if (length > 0)
                {
                    StringBuilder fullPathBuffer = new StringBuilder(length);
                    length = GetLongPathName(path, fullPathBuffer, length);
                    errorCode = Marshal.GetLastWin32Error();

                    if (length > 0)
                    {
                        string fullPath = fullPathBuffer.ToString();
                        path = fullPath;
                    }
                }

                if (length == 0 && errorCode != 0)
                {
                    ThrowExceptionForErrorCode(errorCode);
                }
            }

            return path;
        }

        /// <summary>
        /// Retrieves the current global memory status.
        /// </summary>
        internal static MemoryStatus GetMemoryStatus()
        {
            if (NativeMethodsShared.IsWindows)
            {
                MemoryStatus status = new MemoryStatus();
                bool returnValue = NativeMethodsShared.GlobalMemoryStatusEx(status);
                if (!returnValue)
                {
                    return null;
                }

                return status;
            }

            return null;
        }

        /// <summary>
        /// Get the last write time of the fullpath to the file.
        /// </summary>
        /// <param name="fullPath">Full path to the file in the filesystem</param>
        /// <returns>The last write time of the file, or DateTime.MinValue if the file does not exist.</returns>
        /// <remarks>
        /// This method should be accurate for regular files and symlinks, but can report incorrect data
        /// if the file's content was modified by writing to it through a different link, unless
        /// MSBUILDALWAYSCHECKCONTENTTIMESTAMP=1.
        /// </remarks>
        internal static DateTime GetLastWriteFileUtcTime(string fullPath)
        {
            DateTime fileModifiedTime = DateTime.MinValue;

            if (IsWindows)
            {
                if (Traits.Instance.EscapeHatches.AlwaysUseContentTimestamp)
                {
                    return GetContentLastWriteFileUtcTime(fullPath);
                }

                WIN32_FILE_ATTRIBUTE_DATA data = new WIN32_FILE_ATTRIBUTE_DATA();
                bool success = NativeMethodsShared.GetFileAttributesEx(fullPath, 0, ref data);

                if (success && (data.fileAttributes & NativeMethodsShared.FILE_ATTRIBUTE_DIRECTORY) == 0)
                {
                    long dt = ((long)(data.ftLastWriteTimeHigh) << 32) | ((long)data.ftLastWriteTimeLow);
                    fileModifiedTime = DateTime.FromFileTimeUtc(dt);

                    // If file is a symlink _and_ we're not instructed to do the wrong thing, get a more accurate timestamp. 
                    if ((data.fileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == FILE_ATTRIBUTE_REPARSE_POINT && !Traits.Instance.EscapeHatches.UseSymlinkTimeInsteadOfTargetTime)
                    {
                        fileModifiedTime = GetContentLastWriteFileUtcTime(fullPath);
                    }
                }

                return fileModifiedTime;
            }
            else
            {
                return File.Exists(fullPath)
                        ? File.GetLastWriteTimeUtc(fullPath)
                        : DateTime.MinValue;
            }
        }

        /// <summary>
        /// Get the last write time of the content pointed to by a file path.
        /// </summary>
        /// <param name="fullPath">Full path to the file in the filesystem</param>
        /// <returns>The last write time of the file, or DateTime.MinValue if the file does not exist.</returns>
        /// <remarks>
        /// This is the most accurate timestamp-extraction mechanism, but it is too slow to use all the time.
        /// See https://github.com/Microsoft/msbuild/issues/2052.
        /// </remarks>
        private static DateTime GetContentLastWriteFileUtcTime(string fullPath)
        {
            DateTime fileModifiedTime = DateTime.MinValue;

            using (SafeFileHandle handle =
                CreateFile(fullPath,
                    GENERIC_READ,
                    FILE_SHARE_READ,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL, /* No FILE_FLAG_OPEN_REPARSE_POINT; read through to content */
                    IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
                    if (GetFileTime(handle, out ftCreationTime, out ftLastAccessTime, out ftLastWriteTime))
                    {
                        long fileTime = ((long)(uint)ftLastWriteTime.dwHighDateTime) << 32 |
                                        (long)(uint)ftLastWriteTime.dwLowDateTime;
                        fileModifiedTime =
                            DateTime.FromFileTimeUtc(fileTime);
                    }
                }
            }

            return fileModifiedTime;
        }

        /// <summary>
        /// Did the HRESULT succeed
        /// </summary>
        public static bool HResultSucceeded(int hr)
        {
            return hr >= 0;
        }

        /// <summary>
        /// Did the HRESULT Fail
        /// </summary>
        public static bool HResultFailed(int hr)
        {
            return hr < 0;
        }

        /// <summary>
        /// Given an error code, converts it to an HRESULT and throws the appropriate exception.
        /// </summary>
        /// <param name="errorCode"></param>
        public static void ThrowExceptionForErrorCode(int errorCode)
        {
            // See ndp\clr\src\bcl\system\io\__error.cs for this code as it appears in the CLR.

            // Something really bad went wrong with the call
            // translate the error into an exception

            // Convert the errorcode into an HRESULT (See MakeHRFromErrorCode in Win32Native.cs in
            // ndp\clr\src\bcl\microsoft\win32)
            errorCode = unchecked(((int)0x80070000) | errorCode);

            // Throw an exception as best we can
            Marshal.ThrowExceptionForHR(errorCode);
        }

        /// <summary>
        /// Kills the specified process by id and all of its children recursively.
        /// </summary>
        internal static void KillTree(int processIdToKill)
        {
            // Note that GetProcessById does *NOT* internally hold on to the process handle.
            // Only when you create the process using the Process object
            // does the Process object retain the original handle.

            Process thisProcess;
            try
            {
                thisProcess = Process.GetProcessById(processIdToKill);
            }
            catch (ArgumentException)
            {
                // The process has already died for some reason.  So shrug and assume that any child processes
                // have all also either died or are in the process of doing so.
                return;
            }

            try
            {
                DateTime myStartTime = thisProcess.StartTime;

                // Grab the process handle.  We want to keep this open for the duration of the function so that
                // it cannot be reused while we are running.
                SafeProcessHandle hProcess = OpenProcess(eDesiredAccess.PROCESS_QUERY_INFORMATION, false, processIdToKill);
                if (hProcess.IsInvalid)
                {
                    return;
                }

                try
                {
                    try
                    {
                        // Kill this process, so that no further children can be created.
                        thisProcess.Kill();
                    }
                    catch (Win32Exception e)
                    {
                        // Access denied is potentially expected -- it happens when the process that
                        // we're attempting to kill is already dead.  So just ignore in that case.
                        if (e.NativeErrorCode != ERROR_ACCESS_DENIED)
                        {
                            throw;
                        }
                    }

                    // Now enumerate our children.  Children of this process are any process which has this process id as its parent
                    // and which also started after this process did.
                    List<KeyValuePair<int, SafeProcessHandle>> children = GetChildProcessIds(processIdToKill, myStartTime);

                    try
                    {
                        foreach (KeyValuePair<int, SafeProcessHandle> childProcessInfo in children)
                        {
                            KillTree(childProcessInfo.Key);
                        }
                    }
                    finally
                    {
                        foreach (KeyValuePair<int, SafeProcessHandle> childProcessInfo in children)
                        {
                            childProcessInfo.Value.Dispose();
                        }
                    }
                }
                finally
                {
                    // Release the handle.  After this point no more children of this process exist and this process has also exited.
                    hProcess.Dispose();
                }
            }
            finally
            {
                thisProcess.Dispose();
            }
        }

        /// <summary>
        /// Returns the parent process id for the specified process.
        /// Returns zero if it cannot be gotten for some reason.
        /// </summary>
        internal static int GetParentProcessId(int processId)
        {
            int ParentID = 0;
#if !CLR2COMPATIBILITY
            if (IsUnixLike)
            {
                string line = null;

                try
                {
                    // /proc/<processID>/stat returns a bunch of space separated fields. Get that string
                    using (var r = FileUtilities.OpenRead("/proc/" + processId + "/stat"))
                    {
                        line = r.ReadLine();
                    }
                }
                catch // Ignore errors since the process may have terminated
                {
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    // One of the fields is the process name. It may contain any characters, but since it's
                    // in parenthesis, we can finds its end by looking for the last parenthesis. After that,
                    // there comes a space, then the second fields separated by a space is the parent id.
                    string[] statFields = line.Substring(line.LastIndexOf(')')).Split(MSBuildConstants.SpaceChar, 4);
                    if (statFields.Length >= 3)
                    {
                        ParentID = Int32.Parse(statFields[2]);
                    }
                }
            }
            else
#endif
            {
                SafeProcessHandle hProcess = OpenProcess(eDesiredAccess.PROCESS_QUERY_INFORMATION, false, processId);

                if (!hProcess.IsInvalid)
                {
                    try
                    {
                        // UNDONE: NtQueryInformationProcess will fail if we are not elevated and other process is. Advice is to change to use ToolHelp32 API's
                        // For now just return zero and worst case we will not kill some children.
                        PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                        int pSize = 0;

                        if (0 == NtQueryInformationProcess(hProcess, PROCESSINFOCLASS.ProcessBasicInformation, ref pbi, pbi.Size, ref pSize))
                        {
                            ParentID = (int)pbi.InheritedFromUniqueProcessId;
                        }
                    }
                    finally
                    {
                        hProcess.Dispose();
                    }
                }
            }

            return ParentID;
        }

        /// <summary>
        /// Returns an array of all the immediate child processes by id.
        /// NOTE: The IntPtr in the tuple is the handle of the child process.  CloseHandle MUST be called on this.
        /// </summary>
        internal static List<KeyValuePair<int, SafeProcessHandle>> GetChildProcessIds(int parentProcessId, DateTime parentStartTime)
        {
            List<KeyValuePair<int, SafeProcessHandle>> myChildren = new List<KeyValuePair<int, SafeProcessHandle>>();

            foreach (Process possibleChildProcess in Process.GetProcesses())
            {
                using (possibleChildProcess)
                {
                    // Hold the child process handle open so that children cannot die and restart with a different parent after we've started looking at it.
                    // This way, any handle we pass back is guaranteed to be one of our actual children.
                    SafeProcessHandle childHandle = OpenProcess(eDesiredAccess.PROCESS_QUERY_INFORMATION, false, possibleChildProcess.Id);
                    if (childHandle.IsInvalid)
                    {
                        continue;
                    }

                    bool keepHandle = false;
                    try
                    {
                        if (possibleChildProcess.StartTime > parentStartTime)
                        {
                            int childParentProcessId = GetParentProcessId(possibleChildProcess.Id);
                            if (childParentProcessId != 0)
                            {
                                if (parentProcessId == childParentProcessId)
                                {
                                    // Add this one
                                    myChildren.Add(new KeyValuePair<int, SafeProcessHandle>(possibleChildProcess.Id, childHandle));
                                    keepHandle = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (!keepHandle)
                        {
                            childHandle.Dispose();
                        }
                    }
                }
            }

            return myChildren;
        }

        /// <summary>
        /// Internal, optimized GetCurrentDirectory implementation that simply delegates to the native method
        /// </summary>
        /// <returns></returns>
        internal unsafe static string GetCurrentDirectory()
        {
#if FEATURE_LEGACY_GETCURRENTDIRECTORY
            if (IsWindows)
            {
                int bufferSize = GetCurrentDirectoryWin32(0, null);
                char* buffer = stackalloc char[bufferSize];
                int pathLength = GetCurrentDirectoryWin32(bufferSize, buffer);
                return new string(buffer, startIndex: 0, length: pathLength);
            }
#endif
            return Directory.GetCurrentDirectory();
        }

        private unsafe static int GetCurrentDirectoryWin32(int nBufferLength, char* lpBuffer)
        {
            int pathLength = GetCurrentDirectory(nBufferLength, lpBuffer);
            VerifyThrowWin32Result(pathLength);
            return pathLength;
        }

        internal unsafe static string GetFullPath(string path)
        {
            int bufferSize = GetFullPathWin32(path, 0, null, IntPtr.Zero);
            char* buffer = stackalloc char[bufferSize];
            int fullPathLength = GetFullPathWin32(path, bufferSize, buffer, IntPtr.Zero);
            // Avoid creating new strings unnecessarily
            return AreStringsEqual(buffer, fullPathLength, path) ? path : new string(buffer, startIndex: 0, length: fullPathLength);
        }

        private unsafe static int GetFullPathWin32(string target, int bufferLength, char* buffer, IntPtr mustBeZero)
        {
            int pathLength = GetFullPathName(target, bufferLength, buffer, mustBeZero);
            VerifyThrowWin32Result(pathLength);
            return pathLength;
        }

        /// <summary>
        /// Compare an unsafe char buffer with a <see cref="System.String"/> to see if their contents are identical.
        /// </summary>
        /// <param name="buffer">The beginning of the char buffer.</param>
        /// <param name="len">The length of the buffer.</param>
        /// <param name="s">The string.</param>
        /// <returns>True only if the contents of <paramref name="s"/> and the first <paramref name="len"/> characters in <paramref name="buffer"/> are identical.</returns>
        private unsafe static bool AreStringsEqual(char* buffer, int len, string s)
        {
            if (len != s.Length)
            {
                return false;
            }

            foreach (char ch in s)
            {
                if (ch != *buffer++)
                {
                    return false;
                }
            }

            return true;
        }

        internal static void VerifyThrowWin32Result(int result)
        {
            bool isError = result == 0;
            if (isError)
            {
                int code = Marshal.GetLastWin32Error();
                ThrowExceptionForErrorCode(code);
            }
        }

#endregion

#region PInvoke

        /// <summary>
        /// Gets the current OEM code page which is used by console apps
        /// (as opposed to the Windows/ANSI code page)
        /// Basically for each ANSI code page (set in Regional settings) there's a corresponding OEM code page
        /// that needs to be used for instance when writing to batch files
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport(kernel32Dll)]
        internal static extern int GetOEMCP();

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileAttributesEx(String name, int fileInfoLevel, ref WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport(kernel32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint SearchPath
        (
            string path,
            string fileName,
            string extension,
            int numBufferChars,
            [Out] StringBuilder buffer,
            int[] filePart
        );

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", PreserveSig = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary([In] IntPtr module);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", PreserveSig = true, BestFitMapping = false, ThrowOnUnmappableChar = true, CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, PreserveSig = true, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string fileName);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport(mscoreeDLL, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint GetRequestedRuntimeInfo(String pExe,
                                                String pwszVersion,
                                                String pConfigurationFile,
                                                uint startupFlags,
                                                uint runtimeInfoFlags,
                                                [Out] StringBuilder pDirectory,
                                                int dwDirectory,
                                                out uint dwDirectoryLength,
                                                [Out] StringBuilder pVersion,
                                                int cchBuffer,
                                                out uint dwlength);

        /// <summary>
        /// Gets the fully qualified filename of the currently executing .exe
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport(kernel32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetModuleFileName(
#if FEATURE_HANDLEREF
            HandleRef hModule,
#else
            IntPtr hModule,
#endif
            [Out] StringBuilder buffer, int length);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll")]
        internal static extern uint GetFileType(IntPtr hFile);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [SuppressMessage("Microsoft.Usage", "CA2205:UseManagedEquivalentsOfWin32Api", Justification = "Using unmanaged equivalent for performance reasons")]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal unsafe static extern int GetCurrentDirectory(int nBufferLength, char* lpBuffer);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [SuppressMessage("Microsoft.Usage", "CA2205:UseManagedEquivalentsOfWin32Api", Justification = "Using unmanaged equivalent for performance reasons")]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetCurrentDirectory")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCurrentDirectoryWindows(string path);

        internal static bool SetCurrentDirectory(string path)
        {
            if (IsWindows)
            {
                return SetCurrentDirectoryWindows(path);
            }

            // Make sure this does not throw
            try
            {
                Directory.SetCurrentDirectory(path);
            }
            catch
            {
            }
            return true;
        }

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static unsafe extern int GetFullPathName(string target, int bufferLength, char* buffer, IntPtr mustBeZero);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("KERNEL32.DLL")]
        private static extern SafeProcessHandle OpenProcess(eDesiredAccess dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("NTDLL.DLL")]
        private static extern int NtQueryInformationProcess(SafeProcessHandle hProcess, PROCESSINFOCLASS pic, ref PROCESS_BASIC_INFORMATION pbi, uint cb, ref int pSize);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = AutoOrUnicode, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus lpBuffer);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)]
        internal static extern int GetShortPathName(string path, [Out] StringBuilder fullpath, [In] int length);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)]
        internal static extern int GetLongPathName([In] string path, [Out] StringBuilder fullpath, [In] int length);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", CharSet = AutoOrUnicode, SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, SecurityAttributes lpPipeAttributes, int nSize);

        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("kernel32.dll", CharSet = AutoOrUnicode, SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        /// <summary>
        /// CoWaitForMultipleHandles allows us to wait in an STA apartment and still service RPC requests from other threads.
        /// VS needs this in order to allow the in-proc compilers to properly initialize, since they will make calls from the
        /// build thread which the main thread (blocked on BuildSubmission.Execute) must service.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass", Justification = "Class name is NativeMethodsShared for increased clarity")]
        [DllImport("ole32.dll")]
        public static extern int CoWaitForMultipleHandles(COWAIT_FLAGS dwFlags, int dwTimeout, int cHandles, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pHandles, out int pdwIndex);

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_SHARE_READ = 0x1;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        internal const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = AutoOrUnicode, CallingConvention = CallingConvention.StdCall,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile
            );

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetFileTime(
            SafeFileHandle hFile,
            out FILETIME lpCreationTime,
            out FILETIME lpLastAccessTime,
            out FILETIME lpLastWriteTime
            );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetThreadErrorMode(int newMode, out int oldMode);

#endregion

#region Extensions

        /// <summary>
        /// Waits while pumping APC messages.  This is important if the waiting thread is an STA thread which is potentially
        /// servicing COM calls from other threads.
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Runtime.InteropServices.SafeHandle.DangerousGetHandle", Scope = "member", Target = "Microsoft.Build.Shared.NativeMethodsShared.#MsgWaitOne(System.Threading.WaitHandle,System.Int32)", Justification = "This is necessary and it has been used for a long time. No need to change it now.")]
        internal static bool MsgWaitOne(this WaitHandle handle)
        {
            return handle.MsgWaitOne(Timeout.Infinite);
        }

        /// <summary>
        /// Waits while pumping APC messages.  This is important if the waiting thread is an STA thread which is potentially
        /// servicing COM calls from other threads.
        /// </summary>
        internal static bool MsgWaitOne(this WaitHandle handle, TimeSpan timeout)
        {
            return MsgWaitOne(handle, (int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Waits while pumping APC messages.  This is important if the waiting thread is an STA thread which is potentially
        /// servicing COM calls from other threads.
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Runtime.InteropServices.SafeHandle.DangerousGetHandle", Justification = "Necessary to avoid pumping")]
        internal static bool MsgWaitOne(this WaitHandle handle, int timeout)
        {
            // CoWaitForMultipleHandles allows us to wait in an STA apartment and still service RPC requests from other threads.
            // VS needs this in order to allow the in-proc compilers to properly initialize, since they will make calls from the
            // build thread which the main thread (blocked on BuildSubmission.Execute) must service.
            int waitIndex;
#if FEATURE_HANDLE_SAFEWAITHANDLE
            IntPtr handlePtr = handle.SafeWaitHandle.DangerousGetHandle();
#else
            IntPtr handlePtr = handle.GetSafeWaitHandle().DangerousGetHandle();
#endif
            int returnValue = CoWaitForMultipleHandles(COWAIT_FLAGS.COWAIT_NONE, timeout, 1, new IntPtr[] { handlePtr }, out waitIndex);
            ErrorUtilities.VerifyThrow(returnValue == 0 || ((uint)returnValue == RPC_S_CALLPENDING && timeout != Timeout.Infinite), "Received {0} from CoWaitForMultipleHandles, but expected 0 (S_OK)", returnValue);
            return returnValue == 0;
        }

#endregion

#region helper methods

        internal static bool DirectoryExists(string fullPath)
        {
            return NativeMethodsShared.IsWindows
                ? DirectoryExistsWindows(fullPath)
                : Directory.Exists(fullPath);
        }

        internal static bool DirectoryExistsWindows(string fullPath)
        {
            NativeMethodsShared.WIN32_FILE_ATTRIBUTE_DATA data = new NativeMethodsShared.WIN32_FILE_ATTRIBUTE_DATA();
            bool success = NativeMethodsShared.GetFileAttributesEx(fullPath, 0, ref data);
            return success && (data.fileAttributes & NativeMethodsShared.FILE_ATTRIBUTE_DIRECTORY) != 0;
        }

        internal static bool FileExists(string fullPath)
        {
            return NativeMethodsShared.IsWindows
                ? FileExistsWindows(fullPath)
                : File.Exists(fullPath);
        }

        internal static bool FileExistsWindows(string fullPath)
        {
            NativeMethodsShared.WIN32_FILE_ATTRIBUTE_DATA data = new NativeMethodsShared.WIN32_FILE_ATTRIBUTE_DATA();
            bool success = NativeMethodsShared.GetFileAttributesEx(fullPath, 0, ref data);
            return success && (data.fileAttributes & NativeMethodsShared.FILE_ATTRIBUTE_DIRECTORY) == 0;
        }

        internal static bool FileOrDirectoryExists(string path)
        {
            return IsWindows
                ? FileOrDirectoryExistsWindows(path)
                : File.Exists(path) || Directory.Exists(path);
        }

        internal static bool FileOrDirectoryExistsWindows(string path)
        {
            WIN32_FILE_ATTRIBUTE_DATA data = new WIN32_FILE_ATTRIBUTE_DATA();
            return GetFileAttributesEx(path, 0, ref data);
        }

#endregion
    }
}
