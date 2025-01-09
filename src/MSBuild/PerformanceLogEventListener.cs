// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text;
using Microsoft.Build.Eventing;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    internal sealed class PerformanceLogEventListener : EventListener
    {
        internal struct ProviderConfiguration
        {
            internal string Name { get; set; }
            internal EventKeywords Keywords { get; set; }
            internal EventLevel Level { get; set; }
        }

        private static ProviderConfiguration[] s_config = new ProviderConfiguration[]
        {
            new ProviderConfiguration()
            {
                Name = "Microsoft-Build",
                Keywords = MSBuildEventSource.Keywords.PerformanceLog,
                Level = EventLevel.Verbose
            }
        };

        private const string PerfLogDirEnvVar = "DOTNET_PERFLOG_DIR";
        private const char EventDelimiter = '\n';
        private string _processIDStr;
        private StreamWriter _writer;

        [ThreadStatic]
        private static StringBuilder s_builder;

        internal static PerformanceLogEventListener Create()
        {
            PerformanceLogEventListener eventListener = null;
            try
            {
                // Initialization happens as a separate step and not in the constructor to ensure that
                // if an exception is thrown during init, we have the opportunity to dispose of the listener,
                // which will disable any EventSources that have been enabled.  Any EventSources that existed before
                // this EventListener will be passed to OnEventSourceCreated before our constructor is called, so
                // we if we do this work in the constructor, and don't get an opportunity to call Dispose, the
                // EventSources will remain enabled even if there aren't any consuming EventListeners.

                // Check to see if we should enable the event listener.
                string logDirectory = Environment.GetEnvironmentVariable(PerfLogDirEnvVar);

                if (!string.IsNullOrEmpty(logDirectory) && Directory.CreateDirectory(logDirectory).Exists)
                {
                    eventListener = new PerformanceLogEventListener();
                    eventListener.Initialize(logDirectory);
                }
            }
            catch
            {
                if (eventListener != null)
                {
                    eventListener.Dispose();
                    eventListener = null;
                }
            }

            return eventListener;
        }

        private PerformanceLogEventListener()
        {
        }

        internal void Initialize(string logDirectory)
        {
            _processIDStr = EnvironmentUtilities.CurrentProcessId.ToString();

            // Use a GUID disambiguator to make sure that we have a unique file name.
            string logFilePath = Path.Combine(logDirectory, $"perf-{_processIDStr}-{Guid.NewGuid().ToString("N")}.log");

            Stream outputStream = new FileStream(
                logFilePath,
                FileMode.Create,    // Create or overwrite.
                FileAccess.Write,   // Open for writing.
                FileShare.Read,     // Allow others to read.
                4096,               // Default buffer size.
                FileOptions.None);  // No hints about how the file will be written.

            _writer = new StreamWriter(outputStream);
        }

        public override void Dispose()
        {
            lock (this)
            {
                if (_writer != null)
                {
                    _writer.Dispose();
                    _writer = null;
                }
            }

            base.Dispose();
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            try
            {
                // Enable the provider if it matches a requested configuration.
                foreach (ProviderConfiguration entry in s_config)
                {
                    if (entry.Name.Equals(eventSource.Name))
                    {
                        EnableEvents(eventSource, entry.Level, entry.Keywords);
                    }
                }
            }
            catch
            {
                // If we fail to enable, just skip it and continue.
            }

            base.OnEventSourceCreated(eventSource);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                if (s_builder == null)
                {
                    s_builder = new StringBuilder();
                }
                else
                {
                    s_builder.Clear();
                }

                s_builder.Append($"[{DateTime.UtcNow.ToString("o")}] Event={eventData.EventSource.Name}/{eventData.EventName} ProcessID={_processIDStr} ThreadID={System.Threading.Thread.CurrentThread.ManagedThreadId}\t ");
                for (int i = 0; i < eventData.PayloadNames.Count; i++)
                {
                    s_builder.Append($"{eventData.PayloadNames[i]}=\"{eventData.Payload[i]}\" ");
                }

                lock (this)
                {
                    if (_writer != null)
                    {
                        _writer.Write(s_builder.ToString());
                        _writer.Write(EventDelimiter);
                    }
                }
            }
            catch
            {
                // If we fail to log an event, just skip it and continue.
            }

            base.OnEventWritten(eventData);
        }
    }
}
