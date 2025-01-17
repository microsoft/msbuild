// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#if NETFRAMEWORK
using Microsoft.VisualStudio.OpenTelemetry.ClientExtensions;
using Microsoft.VisualStudio.OpenTelemetry.ClientExtensions.Exporters;
using Microsoft.VisualStudio.OpenTelemetry.Collector.Interfaces;
using Microsoft.VisualStudio.OpenTelemetry.Collector.Settings;
using OpenTelemetry;
using OpenTelemetry.Trace;
#endif
// #if DEBUG && NETFRAMEWORK
// using OpenTelemetry.Exporter;
// #endif

namespace Microsoft.Build.Framework.Telemetry
{

    internal enum TelemetryState
    {
        Uninitialized,

        /// <summary>
        /// Opt out of telemetry.
        /// </summary>
        OptOut,

        /// <summary>
        /// Run not sampled for telemetry.
        /// </summary>
        Unsampled,

        /// <summary>
        /// For core hook, only ActivitySource is created.
        /// </summary>
        CoreInitialized,

        /// <summary>
        /// ActivitySource, OTel TracerProvider are initialized.
        /// </summary>
        VSInitialized,

        /// <summary>
        /// ActivitySource, OTel TracerProvider, VS OpenTelemetry Collector are initialized.
        /// </summary>
        StandaloneInitialized,

        Disposed
    }

    /// <summary>
    /// Class for configuring and managing the telemetry infrastructure with System.Diagnostics.Activity, OpenTelemetry SDK and VS OpenTelemetry Collector.
    /// </summary>
    internal static class OpenTelemetryManager
    {
        private static TelemetryState _telemetryState = TelemetryState.Uninitialized;
        private static readonly object s_initialize_lock = new();
        // private static double _sampleRate = TelemetryConstants.DefaultSampleRate;

#if NETFRAMEWORK
        private static TracerProvider? s_tracerProvider;
        private static IOpenTelemetryCollector? s_collector;
#endif

        public static MSBuildActivitySource? DefaultActivitySource { get; set; }
        
        // unsampled -> initialized or unsampled again
        public static bool ResampleInitialize()
        {
            return false;
        }


        public static void Initialize(bool isStandalone)
        {
            lock (s_initialize_lock)
            {
                if (!ShouldInitialize())
                {
                    return;
                }
                
                // create activity sources
                DefaultActivitySource = new MSBuildActivitySource(TelemetryConstants.DefaultActivitySourceNamespace);

                // create trace exporter in framework
#if NETFRAMEWORK
                var exporterSettings = OpenTelemetryExporterSettingsBuilder
                    .CreateVSDefault(TelemetryConstants.VSMajorVersion)
                    .Build();

                TracerProviderBuilder tracerProviderBuilder = OpenTelemetry.Sdk
                    .CreateTracerProviderBuilder()
                    .AddSource(TelemetryConstants.DefaultActivitySourceNamespace)
                    .AddVisualStudioDefaultTraceExporter(exporterSettings);

                s_tracerProvider =
                    tracerProviderBuilder
                        /*
#if DEBUG
                        .AddOtlpExporter()
#endif
                        */
                        .Build();

                // create collector if not in vs
                if (isStandalone)
                {
                    IOpenTelemetryCollectorSettings collectorSettings = OpenTelemetryCollectorSettingsBuilder
                        .CreateVSDefault(TelemetryConstants.VSMajorVersion)
                        .Build();

                    s_collector = OpenTelemetryCollectorProvider
                        .CreateCollector(collectorSettings);
                    s_collector.StartAsync().Wait();
                }
#endif
                _telemetryState = TelemetryState.VSInitialized;
            }
        }

        public static void ForceFlush()
        {
            lock (s_initialize_lock)
            {
                if (_telemetryState == TelemetryState.VSInitialized)
                {
#if NETFRAMEWORK
                    s_tracerProvider?.ForceFlush();
                    // s_collector.
#endif
                }
            }
        }

        private static bool ShouldInitialize()
        {
            // only initialize once
            if (_telemetryState != TelemetryState.Uninitialized )
            {
                return false;
            }

            string? dotnetCliTelemetryOptOut = Environment.GetEnvironmentVariable(TelemetryConstants.DotnetOptOut);
            if (dotnetCliTelemetryOptOut == "1" || dotnetCliTelemetryOptOut == "true")
            {
                return false;
            }
#if NETFRAMEWORK
            string? telemetryMSBuildOptOut = Environment.GetEnvironmentVariable(TelemetryConstants.MSBuildFxOptout);
            if (telemetryMSBuildOptOut == "1" || telemetryMSBuildOptOut == "true")
            {
                return false;
            }
            return true;
#else
            string? telemetryOptIn = Environment.GetEnvironmentVariable(TelemetryConstants.MSBuildCoreOptin);
            if (telemetryOptIn == "1" || telemetryOptIn == "true")
            {
                return true;
            }
            return false;   
            

#endif

        }

        public static void Shutdown()
        {
            lock (s_initialize_lock)
            {
                if (_telemetryState == TelemetryState.VSInitialized)
                {
#if NETFRAMEWORK
                    s_tracerProvider?.Shutdown();
                    s_collector?.Dispose();
#endif
                }
            }
        }
    }

    internal class MSBuildActivitySource
    {
        private readonly ActivitySource _source;

        public MSBuildActivitySource(string name)
        {
            _source = new ActivitySource(name);
        }

        public Activity? StartActivity(string name)
        {
            var activity = Activity.Current?.HasRemoteParent == true
                ? _source.StartActivity($"{TelemetryConstants.EventPrefix}{name}", ActivityKind.Internal, parentId: Activity.Current.ParentId)
                : _source.StartActivity($"{TelemetryConstants.EventPrefix}{name}");
            return activity;
        }
    }

    internal static class ActivityExtensions
    {
        public static Activity WithTags(this Activity activity, IActivityTelemetryDataHolder dataHolder)
        {
            if (dataHolder != null)
            {
                foreach ((string name, object value, bool hashed) in dataHolder.GetActivityProperties())
                {
                    object? hashedValue = null;
                    if (hashed)
                    {
                        // TODO: make this work
                        hashedValue = value;

                        // Hash the value via Visual Studio mechanism in Framework & same algo as in core telemetry hashing
                        // https://github.com/dotnet/sdk/blob/8bd19a2390a6bba4aa80d1ac3b6c5385527cc311/src/Cli/Microsoft.DotNet.Cli.Utils/Sha256Hasher.cs
#if NETFRAMEWORK
                        // hashedValue = new Microsoft.VisualStudio.Telemetry.TelemetryHashedProperty(value);
#endif
                    }

                    activity.SetTag($"{TelemetryConstants.PropertyPrefix}{name}", hashed ? hashedValue : value);
                }
            }
            return activity;
        }

        public static Activity WithTags(this Activity activity, IDictionary<string, object>? tags)
        {
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    activity.SetTag($"{TelemetryConstants.PropertyPrefix}{tag.Key}", tag.Value);
                }
            }

            return activity;
        }

        public static Activity WithTag(this Activity activity, string name, object value, bool hashed = false)
        {
            activity.SetTag($"{TelemetryConstants.PropertyPrefix}{name}", hashed ? value.GetHashCode() : value);
            return activity;
        }

        public static Activity WithStartTime(this Activity activity, DateTime? startTime)
        {
            if (startTime.HasValue)
            {
                activity.SetStartTime(startTime.Value);
            }
            return activity;
        }
    }
}
