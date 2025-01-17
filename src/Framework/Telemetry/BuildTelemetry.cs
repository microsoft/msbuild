// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.Build.Framework.Telemetry
{
    /// <summary>
    /// Telemetry of build.
    /// </summary>
    internal class BuildTelemetry : TelemetryBase, IActivityTelemetryDataHolder
    {
        public override string EventName => "build";

        /// <summary>
        /// Time at which build have started.
        /// </summary>
        /// <remarks>
        /// It is time when build started, not when BuildManager start executing build.
        /// For example in case of MSBuild Server it is time before we connected or launched MSBuild Server.
        /// </remarks>
        public DateTime? StartAt { get; set; }

        /// <summary>
        /// Time at which inner build have started.
        /// </summary>
        /// <remarks>
        /// It is time when build internally started, i.e. when BuildManager starts it.
        /// In case of MSBuild Server it is time when Server starts build.
        /// </remarks>
        public DateTime? InnerStartAt { get; set; }

        /// <summary>
        /// Time at which build have finished.
        /// </summary>
        public DateTime? FinishedAt { get; set; }

        /// <summary>
        /// Overall build success.
        /// </summary>
        public bool? BuildSuccess { get; set; }

        /// <summary>
        /// Build Target.
        /// </summary>
        public string? BuildTarget { get; set; }

        /// <summary>
        /// MSBuild server fallback reason.
        /// Either "ServerBusy", "ConnectionError" or null (no fallback).
        /// </summary>
        public string? ServerFallbackReason { get; set; }

        /// <summary>
        /// Version of MSBuild.
        /// </summary>
        public Version? BuildEngineVersion { get; set; }

        /// <summary>
        /// Display version of the Engine suitable for display to a user.
        /// </summary>
        public string? BuildEngineDisplayVersion { get; set; }

        /// <summary>
        /// Path to project file.
        /// </summary>
        public string? ProjectPath { get; set; }

        /// <summary>
        /// Host in which MSBuild build was executed.
        /// For example: "VS", "VSCode", "Azure DevOps", "GitHub Action", "CLI", ...
        /// </summary>
        public string? BuildEngineHost { get; set; }

        /// <summary>
        /// True if buildcheck was used.
        /// </summary>
        public bool? BuildCheckEnabled { get; set; }

        /// <summary>
        /// True if Smart Application Control was enabled.
        /// </summary>
        public bool? SACEnabled { get; set; }

        /// <summary>
        /// State of MSBuild server process before this build.
        /// One of 'cold', 'hot', null (if not run as server)
        /// </summary>
        public string? InitialMSBuildServerState { get; set; }

        /// <summary>
        /// Framework name suitable for display to a user.
        /// </summary>
        public string? BuildEngineFrameworkName { get; set; }

        public override IDictionary<string, string> GetProperties()
        {
            var properties = new Dictionary<string, string>();

            // populate property values
            if (BuildEngineDisplayVersion != null)
            {
                properties[nameof(BuildEngineDisplayVersion)] = BuildEngineDisplayVersion;
            }

            if (StartAt.HasValue && FinishedAt.HasValue)
            {
                properties[TelemetryConstants.BuildDurationPropertyName] = (FinishedAt.Value - StartAt.Value).TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
            }

            if (InnerStartAt.HasValue && FinishedAt.HasValue)
            {
                properties[TelemetryConstants.InnerBuildDurationPropertyName] = (FinishedAt.Value - InnerStartAt.Value).TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
            }

            if (BuildEngineFrameworkName != null)
            {
                properties[nameof(BuildEngineFrameworkName)] = BuildEngineFrameworkName;
            }

            if (BuildEngineHost != null)
            {
                properties[nameof(BuildEngineHost)] = BuildEngineHost;
            }

            if (InitialMSBuildServerState != null)
            {
                properties[nameof(InitialMSBuildServerState)] = InitialMSBuildServerState;
            }

            if (ProjectPath != null)
            {
                properties[nameof(ProjectPath)] = ProjectPath;
            }

            if (ServerFallbackReason != null)
            {
                properties[nameof(ServerFallbackReason)] = ServerFallbackReason;
            }

            if (BuildSuccess.HasValue)
            {
                properties[nameof(BuildSuccess)] = BuildSuccess.HasValue.ToString(CultureInfo.InvariantCulture);
            }

            if (BuildTarget != null)
            {
                properties[nameof(BuildTarget)] = BuildTarget;
            }

            if (BuildEngineVersion != null)
            {
                properties[nameof(BuildEngineVersion)] = BuildEngineVersion.ToString();
            }

            if (BuildCheckEnabled != null)
            {
                properties[nameof(BuildCheckEnabled)] = BuildCheckEnabled.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (SACEnabled != null)
            {
                properties[nameof(SACEnabled)] = SACEnabled.Value.ToString(CultureInfo.InvariantCulture);
            }

            return properties;
        }

        /// <summary>
        /// Create a list of properties sent to VS telemetry with the information whether they should be hashed.
        /// </summary>
        /// <returns></returns>
        public IList<TelemetryItem> GetActivityProperties()
        {
            List<TelemetryItem> telemetryItems = new(8);

            if (StartAt.HasValue && FinishedAt.HasValue)
            {
                telemetryItems.Add(new TelemetryItem(TelemetryConstants.BuildDurationPropertyName, (FinishedAt.Value - StartAt.Value).TotalMilliseconds, false));
            }

            if (InnerStartAt.HasValue && FinishedAt.HasValue)
            {
                telemetryItems.Add(new TelemetryItem(TelemetryConstants.InnerBuildDurationPropertyName, (FinishedAt.Value - InnerStartAt.Value).TotalMilliseconds, false));
            }

            if (BuildEngineHost != null)
            {
                telemetryItems.Add(new TelemetryItem(nameof(BuildEngineHost), BuildEngineHost, false));
            }

            if (BuildSuccess.HasValue)
            {
                telemetryItems.Add(new TelemetryItem(nameof(BuildSuccess), BuildSuccess, false));
            }

            if (BuildTarget != null)
            {
                telemetryItems.Add(new TelemetryItem(nameof(BuildTarget), BuildTarget, true));
            }

            if (BuildEngineVersion != null)
            {
                telemetryItems.Add(new TelemetryItem(nameof(BuildEngineVersion), BuildEngineVersion.ToString(), false));
            }

            if (BuildCheckEnabled != null)
            {
                telemetryItems.Add(new TelemetryItem(nameof(BuildCheckEnabled), BuildCheckEnabled, false));
            }

            if (SACEnabled != null)
            {
                telemetryItems.Add(new TelemetryItem(nameof(SACEnabled), SACEnabled, false));
            }

            return telemetryItems;
        }
    }
}
