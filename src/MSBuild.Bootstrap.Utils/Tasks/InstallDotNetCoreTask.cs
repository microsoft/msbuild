﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if RUNTIME_TYPE_NETCORE

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using AsyncTasks = System.Threading.Tasks;

namespace MSBuild.Bootstrap.Utils.Tasks
{
    public sealed class InstallDotNetCoreTask : Task
    {
        private const string ScriptName = "dotnet-install";
        private const string DotNetInstallBaseUrl = "https://dot.net/v1/";

        public InstallDotNetCoreTask()
        {
            InstallDir = string.Empty;
            DotNetInstallScriptRootPath = string.Empty;
            Version = string.Empty;
        }

        [Required]
        public string InstallDir { get; set; }

        [Required]
        public string DotNetInstallScriptRootPath { get; set; }

        [Required]
        public string Version { get; set; }

        private bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public override bool Execute()
        {
            ScriptExecutionSettings executionSettings = SetupScriptsExecutionSettings();
            if (!File.Exists(executionSettings.ScriptsFullPath))
            {
                AsyncTasks.Task.Run(() => DownloadScriptAsync(executionSettings.ScriptName, executionSettings.ScriptsFullPath)).GetAwaiter().GetResult();
            }

            MakeScriptExecutable(executionSettings.ScriptsFullPath);

            return RunScript(executionSettings);
        }

        private async AsyncTasks.Task DownloadScriptAsync(string scriptName, string scriptPath)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync($"{DotNetInstallBaseUrl}{scriptName}").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string scriptContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(scriptContent))
                    {
                        File.WriteAllText(scriptPath, scriptContent);
                    }
                }
                else
                {
                    Log.LogError($"Install-scripts download from {DotNetInstallBaseUrl} error. Status code: {response.StatusCode}.");
                }
            }
        }

        private void MakeScriptExecutable(string scriptPath)
        {
            if (IsWindows)
            {
                return;
            }

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = $"+x {scriptPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            })
            {
                _ = process.Start();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string errors = process.StandardError.ReadToEnd() ?? string.Empty;
                    Log.LogError($"Install-scripts can not be made executable due to the errors: {errors}.");
                }
            }
        }

        private bool RunScript(ScriptExecutionSettings executionSettings)
        {
            if (Log.HasLoggedErrors)
            {
                return false;
            }

            using (Process process = new Process { StartInfo = executionSettings.StartInfo })
            {
                bool started = process.Start();
                if (started)
                {
                    string output = process.StandardOutput.ReadToEnd() ?? string.Empty;
                    Log.LogMessage($"Install-scripts output logs: {output}");

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        string errors = process.StandardError.ReadToEnd() ?? string.Empty;
                        Log.LogError($"Install-scripts execution errors: {errors}");
                    }
                }
                else
                {
                    Log.LogError("Process for install-scripts execution has not started.");
                }
            }

            return !Log.HasLoggedErrors;
        }

        private ScriptExecutionSettings SetupScriptsExecutionSettings()
        {
            string scriptExtension = IsWindows ? "ps1" : "sh";
            string executableName = IsWindows ? "powershell.exe" : "/bin/bash";
            string scriptPath = Path.Combine(DotNetInstallScriptRootPath, $"{ScriptName}.{scriptExtension}");
            string scriptArgs = IsWindows
                ? $"-NoProfile -ExecutionPolicy Bypass -File {scriptPath} -Version {Version} -InstallDir {InstallDir}"
                : $"{scriptPath} --version {Version} --install-dir {InstallDir}";

            var startInfo = new ProcessStartInfo
            {
                FileName = executableName,
                Arguments = scriptArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return new ScriptExecutionSettings(executableName, startInfo, $"{ScriptName}.{scriptExtension}", scriptPath);
        }

        private struct ScriptExecutionSettings(string executableName, ProcessStartInfo startInfo, string scriptName, string scriptsFullPath)
        {
            public string ExecutableName { get; } = executableName;

            public ProcessStartInfo StartInfo { get; } = startInfo;

            public string ScriptName { get; } = scriptName;

            public string ScriptsFullPath { get; } = scriptsFullPath;
        }
    }
}

#endif
