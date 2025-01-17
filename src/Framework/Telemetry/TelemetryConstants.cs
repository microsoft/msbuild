// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Microsoft.Build.Framework.Telemetry;

internal static class TelemetryConstants
{
    /// <summary>
    /// "Microsoft.VisualStudio.OpenTelemetry.*" namespace is required by VS exporting/collection.
    /// </summary>
    public const string DefaultActivitySourceNamespace = "Microsoft.VisualStudio.OpenTelemetry.MSBuild.Default";
    public const string EventPrefix = "VS/MSBuild/";
    public const string PropertyPrefix = "VS.MSBuild.";

    /// <summary>
    /// For VS OpenTelemetry Collector to apply the correct privacy policy.
    /// </summary>
    public const string VSMajorVersion = "17.0";

    /// <summary>
    /// https://learn.microsoft.com/en-us/dotnet/core/tools/telemetry
    /// </summary>
    public const string DotnetOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";

    public const string MSBuildFxOptout = "MSBUILD_TELEMETRY_OPTOUT";
    public const string MSBuildCoreOptin = "MSBUILD_TELEMETRY_OPTIN";

    public const double MaxVSSampleRate = 1;
    public const double MaxStandaloneSampleRate = 1;

    public const double DefaultSampleRate = 4e-5; // 1:25000 
}
