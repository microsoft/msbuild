// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using Xunit;
using Shouldly;

namespace Microsoft.Build.Framework.Telemetry.Tests
{
    /// <summary>
    /// Ensures tests run serially so environment variables and the singleton do not interfere with parallel test runs.
    /// </summary>
    [Collection("OpenTelemetryManagerTests")]
    public class OpenTelemetryManagerTests : IDisposable
    {
        // Store original environment variables so we can restore after each test
        private readonly string? _originalDotnetOptOut;
        private readonly string? _originalMsBuildTelemetryOptOut;
        private readonly string? _originalSampleRateOverride;

        private const string TelemetryFxOptoutEnvVarName = "MSBUILD_TELEMETRY_OPTOUT";
        private const string DotnetOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
        private const string TelemetrySampleRateOverrideEnvVarName = "MSBUILD_TELEMETRY_SAMPLE_RATE";

        public OpenTelemetryManagerTests()
        {
            // Capture existing env vars
            _originalDotnetOptOut = Environment.GetEnvironmentVariable(DotnetOptOut);
            _originalMsBuildTelemetryOptOut = Environment.GetEnvironmentVariable(TelemetryFxOptoutEnvVarName);
            _originalSampleRateOverride = Environment.GetEnvironmentVariable(TelemetrySampleRateOverrideEnvVarName);

            // Ensure a clean manager state before each test
            ResetManagerState();
        }

        public void Dispose()
        {
            // Restore environment variables
            Environment.SetEnvironmentVariable(DotnetOptOut, _originalDotnetOptOut);
            Environment.SetEnvironmentVariable(TelemetryFxOptoutEnvVarName, _originalMsBuildTelemetryOptOut);
            Environment.SetEnvironmentVariable(TelemetrySampleRateOverrideEnvVarName, _originalSampleRateOverride);

            // Ensure manager is reset after each test
            ResetManagerState();
        }

        [Theory]
        [InlineData(DotnetOptOut, "true")]
        [InlineData(TelemetryFxOptoutEnvVarName, "true")]
        [InlineData(DotnetOptOut, "1")]
        [InlineData(TelemetryFxOptoutEnvVarName, "1")]
        public void Initialize_ShouldSetStateToOptOut_WhenOptOutEnvVarIsTrue(string optoutvar, string value)
        {
            // Arrange
            Environment.SetEnvironmentVariable(optoutvar, value);

            // Act
            OpenTelemetryManager.Instance.Initialize(isStandalone: false);

            // Assert
            var state = GetTelemetryState(OpenTelemetryManager.Instance);
            state.ShouldBe(TelemetryState.OptOut);
            OpenTelemetryManager.Instance.DefaultActivitySource.ShouldBeNull();
        }
#if NET
        [Fact]
        public void Initialize_ShouldSetStateToUnsampled_WhenNoOverrideOnNetCore()
        {

            // Clear any override that might have existed
            Environment.SetEnvironmentVariable(TelemetrySampleRateOverrideEnvVarName, null);

            // Also ensure we are not opting out
            Environment.SetEnvironmentVariable(DotnetOptOut, "false");
            Environment.SetEnvironmentVariable(TelemetryFxOptoutEnvVarName, "false");

            OpenTelemetryManager.Instance.Initialize(isStandalone: false);

            var state = GetTelemetryState(OpenTelemetryManager.Instance);
            state.ShouldBe(TelemetryState.Unsampled);
            OpenTelemetryManager.Instance.DefaultActivitySource.ShouldBeNull();
        }
#endif
        
        [WindowsFullFrameworkOnlyTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Initialize_ShouldSetSampleRateOverride_AndCreateActivitySource_WhenRandomBelowOverride(bool standalone)
        {

            // Arrange
            Environment.SetEnvironmentVariable(TelemetryFxOptoutEnvVarName, "false");
            Environment.SetEnvironmentVariable(DotnetOptOut, "false");
            Environment.SetEnvironmentVariable(TelemetrySampleRateOverrideEnvVarName, "1.0");

            // Act
            OpenTelemetryManager.Instance.Initialize(isStandalone: standalone);

            // Assert
            var state = GetTelemetryState(OpenTelemetryManager.Instance);
            // On .NET Framework, we expect TelemetryState.ExporterInitialized
            // On .NET / .NET Standard, the code doesn't explicitly set TelemetryState 
            // => it remains TelemetryState.Uninitialized if not net48 or netframework.
            // So we can do a check to see if it is either ExporterInitialized or left at Uninitialized.
            // If your code has changed to set a different state, adapt accordingly.

#if NETFRAMEWORK
            if (standalone)
            {
                state.ShouldBe(TelemetryState.CollectorInitialized);
            }
            else
            {
                // TODO: collector in VS
                // state.ShouldBe(TelemetryState.ExporterInitialized);
                state.ShouldBe(TelemetryState.CollectorInitialized);
            }
#else
            state.ShouldBe(TelemetryState.TracerInitialized);
#endif
            // In either scenario, we expect a non-null DefaultActivitySource
            OpenTelemetryManager.Instance.DefaultActivitySource.ShouldNotBeNull();
        }

        [Fact]
        public void Initialize_ShouldNoOp_WhenCalledMultipleTimes()
        {
            // Arrange
            Environment.SetEnvironmentVariable(DotnetOptOut, "true");

            // Act #1
            OpenTelemetryManager.Instance.Initialize(isStandalone: true);
            var firstState = GetTelemetryState(OpenTelemetryManager.Instance);

            // Act #2
            // Try to re-initialize with different env var settings
            Environment.SetEnvironmentVariable(DotnetOptOut, null);
            OpenTelemetryManager.Instance.Initialize(isStandalone: true);
            var secondState = GetTelemetryState(OpenTelemetryManager.Instance);

            // Assert
            // Because the manager was already set to "OptOut" on the first call, 
            // the second call is a no-op (the state remains the same).
            firstState.ShouldBe(TelemetryState.OptOut);
            secondState.ShouldBe(TelemetryState.OptOut);
        }

        /* Helper methods */

        /// <summary>
        /// Resets the singleton manager to a known uninitialized state so each test is isolated.
        /// </summary>
        private void ResetManagerState()
        {
            // The manager is a private static Lazy<OpenTelemetryManager>. We can forcibly 
            // set the instance's internal fields to revert it to Uninitialized. 
            // Another approach is to forcibly re-create the Lazy<T>, but that's more complicated.
            //
            // For demonstration, we do minimal reflection to set:
            //    _telemetryState = TelemetryState.Uninitialized
            //    DefaultActivitySource = null

            var instance = OpenTelemetryManager.Instance;
            // 1. telemetryState
            var telemetryStateField = typeof(OpenTelemetryManager)
                .GetField("_telemetryState", BindingFlags.NonPublic | BindingFlags.Instance);
            telemetryStateField?.SetValue(instance, TelemetryState.Uninitialized);

            // 2. DefaultActivitySource
            var defaultSourceProp = typeof(OpenTelemetryManager)
                .GetProperty(nameof(OpenTelemetryManager.DefaultActivitySource),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            defaultSourceProp?.SetValue(instance, null);
        }

        /// <summary>
        /// Reads the private _telemetryState field from the given manager instance using reflection.
        /// </summary>
        private TelemetryState GetTelemetryState(OpenTelemetryManager manager)
        {
            var field = typeof(OpenTelemetryManager)
                .GetField("_telemetryState", BindingFlags.NonPublic | BindingFlags.Instance);
            return (TelemetryState)field?.GetValue(manager)!;
        }
    }
}
