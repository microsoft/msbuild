// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Build.Construction;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck.Checks;
internal sealed class UntrustedLocationCheck : Check
{
    public static CheckRule SupportedRule = new CheckRule(
        "BC0301",
        "UntrustedLocation",
        ResourceUtilities.GetResourceString("BuildCheck_BC0301_Title")!,
        ResourceUtilities.GetResourceString("BuildCheck_BC0301_MessageFmt")!,
        new CheckConfiguration() { Severity = CheckResultSeverity.Error });

    public override string FriendlyName => "DotUtils.UntrustedLocationCheck";

    public override IReadOnlyList<CheckRule> SupportedRules { get; } = new List<CheckRule>() { SupportedRule };

    public override void Initialize(ConfigurationContext configurationContext)
    {
        checkedProjects.Clear();
    }

    internal override bool IsBuiltIn => true;

    public override void RegisterActions(IBuildCheckRegistrationContext registrationContext)
    {
        registrationContext.RegisterEvaluatedPropertiesAction(EvaluatedPropertiesAction);
    }

    private HashSet<string> checkedProjects = new HashSet<string>();

    private void EvaluatedPropertiesAction(BuildCheckDataContext<EvaluatedPropertiesCheckData> context)
    {
        if (checkedProjects.Add(context.Data.ProjectFilePath) &&
            context.Data.ProjectFileDirectory.StartsWith(PathsHelper.Downloads, Shared.FileUtilities.PathComparison))
        {
            context.ReportResult(BuildCheckResult.Create(
                SupportedRule,
                ElementLocation.EmptyLocation,
                context.Data.ProjectFileDirectory,
                context.Data.ProjectFilePath.Substring(context.Data.ProjectFileDirectory.Length + 1)));
        }
    }

    private static class PathsHelper
    {
        public static readonly string Downloads = GetDownloadsPath();

        /// <summary>
        /// Returns the current Downloads location. Makes sure the path doesn't end with directory separator
        ///   (to prevent false negatives during matching)
        /// </summary>
        private static string GetDownloadsPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Unsupported on pre-vista
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    try
                    {
                        // based on doc (https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)
                        //  - a final slash is not added
                        return SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string? locationFromEnv = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
                if (locationFromEnv != null && Directory.Exists(locationFromEnv))
                {
                    return locationFromEnv.TrimEnd(['\\','/']);
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        [DllImport("shell32",
            CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
        private static extern string SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
            IntPtr hToken);
    }
}
