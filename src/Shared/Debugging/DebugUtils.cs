// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Shared.Debugging
{
    internal static class DebugUtils
    {
        private enum NodeMode
        {
            CentralNode,
            OutOfProcNode,
            OutOfProcTaskHostNode
        }

        static DebugUtils()
        {
            SetDebugPath();
        }

        // DebugUtils are initialized early on by the test runner - during preparing data for DataMemeberAttribute of some test,
        //  for that reason it is not easily possible to inject the DebugPath in tests via env var (unless we want to run expensive exec style test).
        internal static void SetDebugPath()
        {
            string environmentDebugPath = FileUtilities.TrimAndStripAnyQuotes(Environment.GetEnvironmentVariable("MSBUILDDEBUGPATH"));
            string debugDirectory = environmentDebugPath;

            if (Traits.Instance.DebugEngine)
            {
                if (!string.IsNullOrWhiteSpace(debugDirectory) && FileUtilities.CanWriteToDirectory(debugDirectory))
                {
                    // Debug directory is writable; no need for fallbacks
                }
                else if (FileUtilities.CanWriteToDirectory(Directory.GetCurrentDirectory()))
                {
                    debugDirectory = Path.Combine(Directory.GetCurrentDirectory(), "MSBuild_Logs");
                }
                else
                {
                    debugDirectory = Path.Combine(FileUtilities.TempFileDirectory, "MSBuild_Logs");
                }

                // Out of proc nodes do not know the startup directory so set the environment variable for them.
                if (string.IsNullOrWhiteSpace(environmentDebugPath))
                {
                    Environment.SetEnvironmentVariable("MSBUILDDEBUGPATH", debugDirectory);
                }
            }

            if (debugDirectory is not null)
            {
                FileUtilities.EnsureDirectoryExists(debugDirectory);
            }

            DebugPath = debugDirectory;
        }

        private static readonly Lazy<NodeMode> ProcessNodeMode = new(
        () =>
        {
            return ScanNodeMode(Environment.CommandLine);

            NodeMode ScanNodeMode(string input)
            {
                var match = Regex.Match(input, @"/nodemode:(?<nodemode>[12\s])(\s|$)", RegexOptions.IgnoreCase);

                if (!match.Success)
                {
                    return NodeMode.CentralNode;
                }
                var nodeMode = match.Groups["nodemode"].Value;

                Trace.Assert(!string.IsNullOrEmpty(nodeMode));

                return nodeMode switch
                {
                    "1" => NodeMode.OutOfProcNode,
                    "2" => NodeMode.OutOfProcTaskHostNode,
                    _ => throw new NotImplementedException(),
                };
            }
        });

        private static bool CurrentProcessMatchesDebugName()
        {
            var processNameToBreakInto = Environment.GetEnvironmentVariable("MSBuildDebugProcessName");
            var thisProcessMatchesName = string.IsNullOrWhiteSpace(processNameToBreakInto) ||
                                         EnvironmentUtilities.ProcessName.Contains(processNameToBreakInto);

            return thisProcessMatchesName;
        }

        public static readonly string ProcessInfoString =
            $"{ProcessNodeMode.Value}_{EnvironmentUtilities.ProcessName}_PID={EnvironmentUtilities.CurrentProcessId}_x{(Environment.Is64BitProcess ? "64" : "86")}";

        public static readonly bool ShouldDebugCurrentProcess = CurrentProcessMatchesDebugName();

        public static string DebugPath { get; private set; }

        public static string FindNextAvailableDebugFilePath(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            var fullPath = Path.Combine(DebugPath, fileName);

            var counter = 0;
            while (File.Exists(fullPath))
            {
                fileName = $"{fileNameWithoutExtension}_{counter++}{extension}";
                fullPath = Path.Combine(DebugPath, fileName);
            }

            return fullPath;
        }
    }
}
