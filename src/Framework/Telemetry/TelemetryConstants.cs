// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Microsoft.Build.Framework.Telemetry;

/// <summary>
/// Constants for VS OpenTelemetry for basic configuration and appropriate naming for VS exporting/collection.
/// </summary>
internal static class TelemetryConstants
{
    /// <summary>
    /// "Microsoft.VisualStudio.OpenTelemetry.*" namespace is required by VS exporting/collection.
    /// </summary>
    public const string ActivitySourceNamespacePrefix = "Microsoft.VisualStudio.OpenTelemetry.MSBuild.";

    /// <summary>
    /// Namespace of the default ActivitySource handling e.g. End of build telemetry.
    /// </summary>
    public const string DefaultActivitySourceNamespace = $"{ActivitySourceNamespacePrefix}Default";

    /// <summary>
    /// Prefix required by VS exporting/collection.
    /// </summary>
    public const string EventPrefix = "VS/MSBuild/";

    /// <summary>
    /// Prefix required by VS exporting/collection.
    /// </summary>
    public const string PropertyPrefix = "VS.MSBuild.";

    /// <summary>
    /// For VS OpenTelemetry Collector to apply the correct privacy policy.
    /// </summary>
    public const string VSMajorVersion = "17.0";

    /// <summary>
    /// Opt out by setting this environment variable to "1" or "true", mirroring
    /// https://learn.microsoft.com/en-us/dotnet/core/tools/telemetry
    /// </summary>
    public const string DotnetOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";

    /// <summary>
    /// Variable controlling opt out at the level of not initializing telemetry infrastructure. Set to "1" or "true" to opt out.
    /// </summary>
    public const string TelemetryFxOptoutEnvVarName = "MSBUILD_TELEMETRY_OPTOUT";

    /// <summary>
    /// Overrides sample rate for all namespaces. 
    /// In core, OTel infrastructure is not initialized by default. Set to a nonzero value to opt in.
    /// </summary>
    public const string TelemetrySampleRateOverrideEnvVarName = "MSBUILD_TELEMETRY_SAMPLE_RATE";

    /// <summary>
    /// Sample rate for the default namespace.
    /// 1:25000 gives us sample size of sufficient confidence with the assumption we collect the order of 1e7 - 1e8 events per day.
    /// </summary>
    public const double DefaultSampleRate = 4e-5;
}
