// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;
using Microsoft.NET.StringTools;
using Microsoft.Win32;
using System.Linq;

// Needed for DoesTaskHostExistForParameters
using NodeProviderOutOfProcTaskHost = Microsoft.Build.BackEnd.NodeProviderOutOfProcTaskHost;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// The Intrinsic class provides static methods that can be accessed from MSBuild's
    /// property functions using $([MSBuild]::Function(x,y)).
    /// </summary>
    internal static partial class IntrinsicFunctions
    {
        // lang=regex
        private const string RegistrySdkSpecification = @"^HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Microsoft SDKs\\Windows\\v(\d+\.\d+)$";

#pragma warning disable CA1416 // Platform compatibility: we'll only use this on Windows
        private static readonly object[] DefaultRegistryViews = [RegistryView.Default];
#pragma warning restore CA1416

#if NET7_0_OR_GREATER
        [GeneratedRegex(RegistrySdkSpecification, RegexOptions.IgnoreCase)]
        private static partial Regex RegistrySdkPattern();
#else
        private static readonly Lazy<Regex> RegistrySdkPattern = new Lazy<Regex>(() => new Regex(RegistrySdkSpecification, RegexOptions.IgnoreCase));
#endif

        private static Regex RegistrySdkRegex
        {
            get
            {
#if NET7_0_OR_GREATER
                return RegistrySdkPattern();
#else
                return RegistrySdkPattern.Value;
#endif
            }
        }

        private static readonly Lazy<NuGetFrameworkWrapper> NuGetFramework = new Lazy<NuGetFrameworkWrapper>(() => NuGetFrameworkWrapper.CreateInstance());

        /// <summary>
        /// Add two doubles
        /// </summary>
        internal static double Add(double a, double b)
        {
            return a + b;
        }

        /// <summary>
        /// Add two longs
        /// </summary>
        internal static long Add(long a, long b)
        {
            return a + b;
        }

        /// <summary>
        /// Subtract two doubles
        /// </summary>
        internal static double Subtract(double a, double b)
        {
            return a - b;
        }

        /// <summary>
        /// Subtract two longs
        /// </summary>
        internal static long Subtract(long a, long b)
        {
            return a - b;
        }

        /// <summary>
        /// Multiply two doubles
        /// </summary>
        internal static double Multiply(double a, double b)
        {
            return a * b;
        }

        /// <summary>
        /// Multiply two longs
        /// </summary>
        internal static long Multiply(long a, long b)
        {
            return a * b;
        }

        /// <summary>
        /// Divide two doubles
        /// </summary>
        internal static double Divide(double a, double b)
        {
            return a / b;
        }

        /// <summary>
        /// Divide two longs
        /// </summary>
        internal static long Divide(long a, long b)
        {
            return a / b;
        }

        /// <summary>
        /// Modulo two doubles
        /// </summary>
        internal static double Modulo(double a, double b)
        {
            return a % b;
        }

        /// <summary>
        /// Modulo two longs
        /// </summary>
        internal static long Modulo(long a, long b)
        {
            return a % b;
        }

        /// <summary>
        /// Escape the string according to MSBuild's escaping rules
        /// </summary>
        internal static string Escape(string unescaped)
        {
            return EscapingUtilities.Escape(unescaped);
        }

        /// <summary>
        /// Unescape the string according to MSBuild's escaping rules
        /// </summary>
        internal static string Unescape(string escaped)
        {
            return EscapingUtilities.UnescapeAll(escaped);
        }

        /// <summary>
        /// Perform a bitwise OR on the first and second (first | second)
        /// </summary>
        internal static int BitwiseOr(int first, int second)
        {
            return first | second;
        }

        /// <summary>
        /// Perform a bitwise AND on the first and second (first &amp; second)
        /// </summary>
        internal static int BitwiseAnd(int first, int second)
        {
            return first & second;
        }

        /// <summary>
        /// Perform a bitwise XOR on the first and second (first ^ second)
        /// </summary>
        internal static int BitwiseXor(int first, int second)
        {
            return first ^ second;
        }

        /// <summary>
        /// Perform a bitwise NOT on the first and second (~first)
        /// </summary>
        internal static int BitwiseNot(int first)
        {
            return ~first;
        }

        internal static int LeftShift(int operand, int count)
        {
            return operand << count;
        }

        internal static int RightShift(int operand, int count)
        {
            return operand >> count;
        }

        internal static int RightShiftUnsigned(int operand, int count)
        {
            return operand >>> count;
        }

        /// <summary>
        /// Get the value of the registry key and value, default value is null
        /// </summary>
        internal static object GetRegistryValue(string keyName, string valueName)
        {
#if RUNTIME_TYPE_NETCORE
            // .NET Core MSBuild used to always return empty, so match that behavior
            // on non-Windows (no registry).
            if (!NativeMethodsShared.IsWindows)
            {
                return null;
            }
#endif
            return Registry.GetValue(keyName, valueName, null /* null to match the $(Regsitry:XYZ@ZBC) behaviour */);
        }

        /// <summary>
        /// Get the value of the registry key and value
        /// </summary>
        internal static object GetRegistryValue(string keyName, string valueName, object defaultValue)
        {
#if RUNTIME_TYPE_NETCORE
            // .NET Core MSBuild used to always return empty, so match that behavior
            // on non-Windows (no registry).
            if (!NativeMethodsShared.IsWindows)
            {
                return defaultValue;
            }
#endif
            return Registry.GetValue(keyName, valueName, defaultValue);
        }

        internal static object GetRegistryValueFromView(string keyName, string valueName, object defaultValue, params object[] views)
        {
#if RUNTIME_TYPE_NETCORE
            // .NET Core MSBuild used to always return empty, so match that behavior
            // on non-Windows (no registry).
            if (!NativeMethodsShared.IsWindows)
            {
                return defaultValue;
            }
#endif

            if (views == null || views.Length == 0)
            {
                views = DefaultRegistryViews;
            }

            return GetRegistryValueFromView(keyName, valueName, defaultValue, new ArraySegment<object>(views));
        }

        /// <summary>
        /// Get the value of the registry key from one of the RegistryView's specified
        /// </summary>
        internal static object GetRegistryValueFromView(string keyName, string valueName, object defaultValue, ArraySegment<object> views)
        {
#if RUNTIME_TYPE_NETCORE
            // .NET Core MSBuild used to always return empty, so match that behavior
            // on non-Windows (no registry).
            if (!NativeMethodsShared.IsWindows)
            {
                return defaultValue;
            }
#endif

            // We will take on handing of default value
            // A we need to act on the null return from the GetValue call below
            // so we can keep searching other registry views
            object result = defaultValue;

            // If we haven't been passed any views, then we'll just use the default view
            if (views.Count == 0)
            {
                views = new ArraySegment<object>(DefaultRegistryViews);
            }

            foreach (object viewObject in views)
            {
                if (viewObject is string viewAsString)
                {
                    string typeLeafName = typeof(RegistryView).Name + ".";
                    string typeFullName = typeof(RegistryView).FullName + ".";

                    // We'll allow the user to specify the leaf or full type name on the RegistryView enum
                    viewAsString = viewAsString.Replace(typeFullName, "").Replace(typeLeafName, "");

                    // This may throw - and that's fine as the user will receive a controlled version
                    // of that error.
                    RegistryView view = (RegistryView)Enum.Parse(typeof(RegistryView), viewAsString, true);

                    if (!NativeMethodsShared.IsWindows && !keyName.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                    {
                        // Fake common requests to HKLM that we can resolve

                        // See if this asks for a specific SDK
                        var m = RegistrySdkRegex.Match(keyName);

                        if (m.Success && m.Groups.Count >= 1 && valueName.Equals("InstallRoot", StringComparison.OrdinalIgnoreCase))
                        {
                            return Path.Combine(NativeMethodsShared.FrameworkBasePath, m.Groups[0].Value) + Path.DirectorySeparatorChar;
                        }

                        return string.Empty;
                    }

#pragma warning disable CA2000 // Dispose objects before losing scope is false positive here.
                    using (RegistryKey key = GetBaseKeyFromKeyName(keyName, view, out string subKeyName))
                    {
                        if (key != null)
                        {
                            using (RegistryKey subKey = key.OpenSubKey(subKeyName, false))
                            {
                                // If we managed to retrieve the subkey, then move onto locating the value
                                if (subKey != null)
                                {
                                    result = subKey.GetValue(valueName);
                                }

                                // We've found a value, so stop looking
                                if (result != null)
                                {
                                    break;
                                }
                            }
                        }
                    }
#pragma warning restore CA2000 // Dispose objects before losing scope
                }
            }

            // We will have either found a result or defaultValue if one wasn't found at this point
            return result;
        }

        /// <summary>
        /// Given the absolute location of a file, and a disc location, returns relative file path to that disk location.
        /// Throws UriFormatException.
        /// </summary>
        /// <param name="basePath">
        /// The base path we want to relativize to. Must be absolute.
        /// Should <i>not</i> include a filename as the last segment will be interpreted as a directory.
        /// </param>
        /// <param name="path">
        /// The path we need to make relative to basePath.  The path can be either absolute path or a relative path in which case it is relative to the base path.
        /// If the path cannot be made relative to the base path (for example, it is on another drive), it is returned verbatim.
        /// </param>
        /// <returns>relative path (can be the full path)</returns>
        internal static string MakeRelative(string basePath, string path)
        {
            string result = FileUtilities.MakeRelative(basePath, path);

            return result;
        }

        /// <summary>
        /// Searches upward for a directory containing the specified file, beginning in the specified directory.
        /// </summary>
        /// <param name="startingDirectory">The directory to start the search in.</param>
        /// <param name="fileName">The name of the file to search for.</param>
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// <returns>The full path of the directory containing the file if it is found, otherwise an empty string. </returns>
        internal static string GetDirectoryNameOfFileAbove(string startingDirectory, string fileName, IFileSystem fileSystem)
        {
            return FileUtilities.GetDirectoryNameOfFileAbove(startingDirectory, fileName, fileSystem);
        }

        /// <summary>
        /// Searches upward for the specified file, beginning in the specified <see cref="IElementLocation"/>.
        /// </summary>
        /// <param name="file">The name of the file to search for.</param>
        /// <param name="startingDirectory">An optional directory to start the search in.  The default location is the directory
        /// <param name="fileSystem">The file system abstraction to use that implements file system operations</param>
        /// of the file containing the property function.</param>
        /// <returns>The full path of the file if it is found, otherwise an empty string.</returns>
        internal static string GetPathOfFileAbove(string file, string startingDirectory, IFileSystem fileSystem)
        {
            return FileUtilities.GetPathOfFileAbove(file, startingDirectory, fileSystem);
        }

        /// <summary>
        /// Return the string in parameter 'defaultValue' only if parameter 'conditionValue' is empty
        /// else, return the value conditionValue
        /// </summary>
        internal static string ValueOrDefault(string conditionValue, string defaultValue)
        {
            if (String.IsNullOrEmpty(conditionValue))
            {
                return defaultValue;
            }
            else
            {
                return conditionValue;
            }
        }

        /// <summary>
        /// Returns the string after converting all bytes to base 64 (alphanumeric characters plus '+' and '/'), ending in one or two '='.
        /// </summary>
        /// <param name="toEncode">String to encode in base 64.</param>
        /// <returns>The encoded string.</returns>
        internal static string ConvertToBase64(string toEncode)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(toEncode));
        }

        /// <summary>
        /// Returns the string after converting from base 64 (alphanumeric characters plus '+' and '/'), ending in one or two '='.
        /// </summary>
        /// <param name="toDecode">The string to decode.</param>
        /// <returns>The decoded string.</returns>
        internal static string ConvertFromBase64(string toDecode)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(toDecode));
        }

        internal enum StringHashingAlgorithm
        {
            // Legacy way of calculating StableStringHash - which was derived from string GetHashCode
            Legacy,
            // FNV-1a 32bit hash
            Fnv1a32bit,
            // Custom FNV-1a 32bit hash - optimized for speed by hashing by the whole chars (not individual bytes)
            Fnv1a32bitFast,
            // FNV-1a 64bit hash
            Fnv1a64bit,
            // Custom FNV-1a 64bit hash - optimized for speed by hashing by the whole chars (not individual bytes)
            Fnv1a64bitFast,
            // SHA256 hash - gets the hex string of the hash (with no prefix)
            Sha256
        }

        /// <summary>
        /// Legacy implementation that doesn't lead to JIT pulling the new functions from StringTools (so those must not be referenced anywhere in the function body)
        ///  - for cases where the calling code would erroneously load old version of StringTools alongside of the new version of Microsoft.Build.
        /// Should be removed once Wave17_10 is removed.
        /// </summary>
        internal static object StableStringHashLegacy(string toHash)
            => CommunicationsUtilities.GetHashCode(toHash);

        /// <summary>
        /// Hash the string independent of bitness, target framework and default codepage of the environment.
        /// We do not want this to be inlined, as then the Expander would call directly the new overload, and hence
        ///  JIT load the functions from StringTools - so we would not be able to prevent their loading with ChangeWave as we do now.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object StableStringHash(string toHash)
            => StableStringHash(toHash, StringHashingAlgorithm.Legacy);

        internal static object StableStringHash(string toHash, StringHashingAlgorithm algo) =>
            algo switch
            {
                StringHashingAlgorithm.Legacy => CommunicationsUtilities.GetHashCode(toHash),
                StringHashingAlgorithm.Fnv1a32bit => FowlerNollVo1aHash.ComputeHash32(toHash),
                StringHashingAlgorithm.Fnv1a32bitFast => FowlerNollVo1aHash.ComputeHash32Fast(toHash),
                StringHashingAlgorithm.Fnv1a64bit => FowlerNollVo1aHash.ComputeHash64(toHash),
                StringHashingAlgorithm.Fnv1a64bitFast => FowlerNollVo1aHash.ComputeHash64Fast(toHash),
                StringHashingAlgorithm.Sha256 => CalculateSha256(toHash),
                _ => throw new ArgumentOutOfRangeException(nameof(algo), algo, null)
            };

        private static string CalculateSha256(string toHash)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hashResult = new StringBuilder();
            foreach (byte theByte in sha.ComputeHash(Encoding.UTF8.GetBytes(toHash)))
            {
                hashResult.Append(theByte.ToString("x2"));
            }

            return hashResult.ToString();
        }

        /// <summary>
        /// Returns true if a task host exists that can service the requested runtime and architecture
        /// values, and false otherwise.
        /// </summary>
        internal static bool DoesTaskHostExist(string runtime, string architecture)
        {
            if (runtime != null)
            {
                runtime = runtime.Trim();
            }

            if (architecture != null)
            {
                architecture = architecture.Trim();
            }

            if (!XMakeAttributes.IsValidMSBuildRuntimeValue(runtime))
            {
                ErrorUtilities.ThrowArgument("InvalidTaskHostFactoryParameter", runtime, "Runtime", XMakeAttributes.MSBuildRuntimeValues.clr2, XMakeAttributes.MSBuildRuntimeValues.clr4, XMakeAttributes.MSBuildRuntimeValues.currentRuntime, XMakeAttributes.MSBuildRuntimeValues.any);
            }

            if (!XMakeAttributes.IsValidMSBuildArchitectureValue(architecture))
            {
                ErrorUtilities.ThrowArgument("InvalidTaskHostFactoryParameter", architecture, "Architecture", XMakeAttributes.MSBuildArchitectureValues.x86, XMakeAttributes.MSBuildArchitectureValues.x64, XMakeAttributes.MSBuildArchitectureValues.currentArchitecture, XMakeAttributes.MSBuildArchitectureValues.any);
            }

            runtime = XMakeAttributes.GetExplicitMSBuildRuntime(runtime);
            architecture = XMakeAttributes.GetExplicitMSBuildArchitecture(architecture);

            IDictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            parameters.Add(XMakeAttributes.runtime, runtime);
            parameters.Add(XMakeAttributes.architecture, architecture);

            HandshakeOptions desiredContext = CommunicationsUtilities.GetHandshakeOptions(taskHost: true, taskHostParameters: parameters);
            string taskHostLocation = NodeProviderOutOfProcTaskHost.GetMSBuildLocationFromHostContext(desiredContext);

            if (taskHostLocation != null && FileUtilities.FileExistsNoThrow(taskHostLocation))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// If the given path doesn't have a trailing slash then add one.
        /// If the path is an empty string, does not modify it.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>The specified path with a trailing slash.</returns>
        internal static string EnsureTrailingSlash(string path)
        {
            return FileUtilities.EnsureTrailingSlash(path);
        }

        /// <summary>
        /// Gets the canonicalized full path of the provided directory and ensures it contains the correct directory separator characters for the current operating system
        /// while ensuring it has a trailing slash.
        /// </summary>
        /// <param name="path">One or more directory paths to combine and normalize.</param>
        /// <returns>A canonicalized full directory path with the correct directory separators and a trailing slash.</returns>
        internal static string NormalizeDirectory(params string[] path)
        {
            return EnsureTrailingSlash(NormalizePath(path));
        }

        /// <summary>
        /// Gets the canonicalized full path of the provided path and ensures it contains the correct directory separator characters for the current operating system.
        /// </summary>
        /// <param name="path">One or more paths to combine and normalize.</param>
        /// <returns>A canonicalized full path with the correct directory separators.</returns>
        internal static string NormalizePath(params string[] path)
        {
            return FileUtilities.NormalizePath(path);
        }

        /// <summary>
        /// Specify whether the current OS platform is <paramref name="platformString"/>
        /// </summary>
        /// <param name="platformString">The platform string. Must be a member of <see cref="OSPlatform"/>. Case Insensitive</param>
        /// <returns></returns>
        internal static bool IsOSPlatform(string platformString)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Create(platformString.ToUpperInvariant()));
        }

        /// <summary>
        /// True if current OS is a Unix system.
        /// </summary>
        /// <returns></returns>
        internal static bool IsOsUnixLike()
        {
            return NativeMethodsShared.IsUnixLike;
        }

        /// <summary>
        /// True if current OS is a BSD system.
        /// </summary>
        /// <returns></returns>
        internal static bool IsOsBsdLike()
        {
            return NativeMethodsShared.IsBSD;
        }

        internal static bool VersionEquals(string a, string b)
        {
            return SimpleVersion.Parse(a) == SimpleVersion.Parse(b);
        }

        internal static bool VersionNotEquals(string a, string b)
        {
            return SimpleVersion.Parse(a) != SimpleVersion.Parse(b);
        }

        internal static bool VersionGreaterThan(string a, string b)
        {
            return SimpleVersion.Parse(a) > SimpleVersion.Parse(b);
        }

        internal static bool VersionGreaterThanOrEquals(string a, string b)
        {
            return SimpleVersion.Parse(a) >= SimpleVersion.Parse(b);
        }

        internal static bool VersionLessThan(string a, string b)
        {
            return SimpleVersion.Parse(a) < SimpleVersion.Parse(b);
        }

        internal static bool VersionLessThanOrEquals(string a, string b)
        {
            return SimpleVersion.Parse(a) <= SimpleVersion.Parse(b);
        }

        internal static string GetTargetFrameworkIdentifier(string tfm)
        {
            return NuGetFramework.Value.GetTargetFrameworkIdentifier(tfm);
        }

        internal static string GetTargetFrameworkVersion(string tfm, int versionPartCount = 2)
        {
            return NuGetFramework.Value.GetTargetFrameworkVersion(tfm, versionPartCount);
        }

        internal static bool IsTargetFrameworkCompatible(string target, string candidate)
        {
            return NuGetFramework.Value.IsCompatible(target, candidate);
        }

        internal static string GetTargetPlatformIdentifier(string tfm)
        {
            return NuGetFramework.Value.GetTargetPlatformIdentifier(tfm);
        }

        internal static string GetTargetPlatformVersion(string tfm, int versionPartCount = 2)
        {
            return NuGetFramework.Value.GetTargetPlatformVersion(tfm, versionPartCount);
        }

        internal static string FilterTargetFrameworks(string incoming, string filter)
        {
            return NuGetFramework.Value.FilterTargetFrameworks(incoming, filter);
        }

        internal static bool AreFeaturesEnabled(Version wave)
        {
            return ChangeWaves.AreFeaturesEnabled(wave);
        }

        internal static string SubstringByAsciiChars(string input, int start, int length)
        {
            if (start > input.Length)
            {
                return string.Empty;
            }
            if (start + length > input.Length)
            {
                length = input.Length - start;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < start + length; i++)
            {
                char c = input[i];
                if (c >= 32 && c <= 126 && !FileUtilities.InvalidFileNameChars.Contains(c))
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.ToString();
        }

        internal static string CheckFeatureAvailability(string featureName)
        {
            return Features.CheckFeatureAvailability(featureName).ToString();
        }

        public static string GetCurrentToolsDirectory()
        {
            return BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory;
        }

        public static string GetToolsDirectory32()
        {
            return BuildEnvironmentHelper.Instance.MSBuildToolsDirectory32;
        }

        public static string GetToolsDirectory64()
        {
            return BuildEnvironmentHelper.Instance.MSBuildToolsDirectory64;
        }

        public static string GetMSBuildSDKsPath()
        {
            return BuildEnvironmentHelper.Instance.MSBuildSDKsPath;
        }

        public static string GetVsInstallRoot()
        {
            return BuildEnvironmentHelper.Instance.VisualStudioInstallRootDirectory;
        }

        public static string GetProgramFiles32()
        {
            return FrameworkLocationHelper.programFiles32;
        }

        public static string GetMSBuildExtensionsPath()
        {
            return BuildEnvironmentHelper.Instance.MSBuildExtensionsPath;
        }

        public static bool IsRunningFromVisualStudio() => BuildEnvironmentHelper.Instance.Mode == BuildEnvironmentMode.VisualStudio;

        public static bool RegisterBuildCheck(string projectPath, string pathToAssembly, LoggingContext loggingContext)
        {
            pathToAssembly = FileUtilities.GetFullPathNoThrow(pathToAssembly);
            if (File.Exists(pathToAssembly))
            {
                loggingContext.LogBuildEvent(new BuildCheckAcquisitionEventArgs(pathToAssembly, projectPath));

                return true;
            }

            loggingContext.LogComment(MessageImportance.Low, "CustomCheckAssemblyNotExist", pathToAssembly);

            return false;
        }

        #region Debug only intrinsics

        /// <summary>
        /// returns if the string contains escaped wildcards
        /// </summary>
        internal static List<string> __GetListTest()
        {
            return new List<string> { "A", "B", "C", "D" };
        }

        #endregion

        /// <summary>
        /// Following function will parse a keyName and returns the basekey for it.
        /// It will also store the subkey name in the out parameter.
        /// If the keyName is not valid, we will throw ArgumentException.
        /// The return value shouldn't be null.
        /// Taken from: \ndp\clr\src\BCL\Microsoft\Win32\Registry.cs
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static RegistryKey GetBaseKeyFromKeyName(string keyName, RegistryView view, out string subKeyName)
        {
            if (keyName == null)
            {
                throw new ArgumentNullException(nameof(keyName));
            }

            string basekeyName;
            int i = keyName.IndexOf('\\');
            if (i != -1)
            {
                basekeyName = keyName.Substring(0, i).ToUpperInvariant();
            }
            else
            {
                basekeyName = keyName.ToUpperInvariant();
            }

            RegistryKey basekey = null;

            switch (basekeyName)
            {
                case "HKEY_CURRENT_USER":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    break;
                case "HKEY_LOCAL_MACHINE":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    break;
                case "HKEY_CLASSES_ROOT":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                    break;
                case "HKEY_USERS":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
                    break;
                case "HKEY_PERFORMANCE_DATA":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.PerformanceData, view);
                    break;
                case "HKEY_CURRENT_CONFIG":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.CurrentConfig, view);
                    break;
#if FEATURE_REGISTRYHIVE_DYNDATA
                case "HKEY_DYN_DATA":
                    basekey = RegistryKey.OpenBaseKey(RegistryHive.DynData, view);
                    break;
#endif
                default:
                    ErrorUtilities.ThrowArgument(keyName);
                    break;
            }

            if (i == -1 || i == keyName.Length)
            {
                subKeyName = string.Empty;
            }
            else
            {
                subKeyName = keyName.Substring(i + 1, keyName.Length - i - 1);
            }

            return basekey;
        }
    }
}
