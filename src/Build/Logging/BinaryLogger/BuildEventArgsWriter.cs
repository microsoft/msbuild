// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Shared;
using Microsoft.NET.StringTools;

#nullable disable

namespace Microsoft.Build.Logging
{
    /// <summary>
    /// Serializes BuildEventArgs-derived objects into a provided BinaryWriter
    /// </summary>
    internal class BuildEventArgsWriter
    {
        private readonly Stream originalStream;

        /// <summary>
        /// When writing the current record, first write it to a memory stream,
        /// then flush to the originalStream. This is needed so that if we discover
        /// that we need to write a string record in the middle of writing the
        /// current record, we will write the string record to the original stream
        /// and the current record will end up after the string record.
        /// </summary>
        private readonly MemoryStream currentRecordStream;

        /// <summary>
        /// For NameValueList we need to prefix the storage size
        ///  (distinct from values count due to variable int encoding)
        /// So using same technique as with 'currentRecordStream'.
        /// </summary>
        private readonly MemoryStream nameValueListStream;

        /// <summary>
        /// The binary writer around the originalStream.
        /// </summary>
        private readonly BinaryWriter originalBinaryWriter;

        /// <summary>
        /// The binary writer around the currentRecordStream.
        /// </summary>
        private readonly BinaryWriter currentRecordWriter;

        /// <summary>
        /// The binary writer we're currently using. Is pointing at the currentRecordWriter usually,
        /// but sometimes we repoint it to the originalBinaryWriter temporarily, when writing string
        /// and name-value records.
        /// </summary>
        private BinaryWriter binaryWriter;

        /// <summary>
        /// Hashtable used for deduplicating strings. When we need to write a string,
        /// we check in this hashtable first, and if we've seen the string before,
        /// just write out its index. Otherwise write out a string record, and then
        /// write the string index. A string record is guaranteed to precede its first
        /// usage.
        /// The reader will read the string records first and then be able to retrieve
        /// a string by its index. This allows us to keep the format streaming instead
        /// of writing one giant string table at the end. If a binlog is interrupted
        /// we'll be able to use all the information we've discovered thus far.
        /// </summary>
        private readonly Dictionary<HashKey, int> stringHashes = new Dictionary<HashKey, int>();

        /// <summary>
        /// Hashtable used for deduplicating name-value lists. Same as strings.
        /// </summary>
        private readonly Dictionary<HashKey, int> nameValueListHashes = new Dictionary<HashKey, int>();

        /// <summary>
        /// Index 0 is null, Index 1 is the empty string.
        /// Reserve indices 2-9 for future use. Start indexing actual strings at 10.
        /// </summary>
        internal const int StringStartIndex = 10;

        /// <summary>
        /// Let's reserve a few indices for future use.
        /// </summary>
        internal const int NameValueRecordStartIndex = 10;

        /// <summary>
        /// 0 is null, 1 is empty string
        /// 2-9 are reserved for future use.
        /// Start indexing at 10.
        /// </summary>
        private int stringRecordId = StringStartIndex;

        /// <summary>
        /// The index of the next record to be written.
        /// </summary>
        private int nameValueRecordId = NameValueRecordStartIndex;

        /// <summary>
        /// A temporary buffer we use when writing a NameValueList record. Avoids allocating a list each time.
        /// </summary>
        private readonly List<KeyValuePair<string, string>> nameValueListBuffer = new List<KeyValuePair<string, string>>(1024);

        /// <summary>
        /// A temporary buffer we use when hashing a NameValueList record. Stores the indices of hashed strings
        /// instead of the actual names and values.
        /// </summary>
        private readonly List<KeyValuePair<int, int>> nameValueIndexListBuffer = new List<KeyValuePair<int, int>>(1024);

        /// <summary>
        /// Raised when an item is encountered with a hint to embed a file into the binlog.
        /// </summary>
        public event Action<string> EmbedFile;

        /// <summary>
        /// Initializes a new instance of BuildEventArgsWriter with a BinaryWriter
        /// </summary>
        /// <param name="binaryWriter">A BinaryWriter to write the BuildEventArgs instances to</param>
        public BuildEventArgsWriter(BinaryWriter binaryWriter)
        {
            originalStream = binaryWriter.BaseStream;

            // this doesn't exceed 30K for smaller binlogs so seems like a reasonable
            // starting point to avoid reallocations in the common case
            this.currentRecordStream = new MemoryStream(65536);

            this.nameValueListStream = new MemoryStream(256);

            this.originalBinaryWriter = binaryWriter;
            this.currentRecordWriter = new BinaryWriter(currentRecordStream);

            this.binaryWriter = currentRecordWriter;
        }

        /// <summary>
        /// Write a provided instance of BuildEventArgs to the BinaryWriter
        /// </summary>
        public void Write(BuildEventArgs e)
        {
            // reset the temp stream (in case last usage forgot to do so).
            this.currentRecordStream.SetLength(0);
            BinaryLogRecordKind eventKind = WriteCore(e);

            FlushRecordToFinalStream(eventKind, currentRecordStream);
        }

        private void FlushRecordToFinalStream(BinaryLogRecordKind recordKind, MemoryStream recordStream)
        {
            using var redirectionScope = RedirectWritesToOriginalWriter();
            Write(recordKind);
            Write((int)recordStream.Length);
            recordStream.WriteTo(originalStream);
            recordStream.SetLength(0);
        }

        /*
        Base types and inheritance ("EventArgs" suffix omitted):

        Build
            Telemetry
            LazyFormattedBuild
                BuildMessage
                    CriticalBuildMessage
                    EnvironmentVariableRead
                    MetaprojectGenerated
                    ProjectImported
                    PropertyInitialValueSet
                    PropertyReassignment
                    TargetSkipped
                    TaskCommandLine
                    TaskParameter
                    UninitializedPropertyRead
                    ExtendedMessage
                BuildStatus
                    TaskStarted
                    TaskFinished
                    TargetStarted
                    TargetFinished
                    ProjectStarted
                    ProjectFinished
                    BuildSubmissionStarted
                    BuildStarted
                    BuildFinished
                    BuildCanceled
                    ProjectEvaluationStarted
                    ProjectEvaluationFinished
                BuildError
                    ExtendedBuildError
                BuildWarning
                    ExtendedBuildWarning
                CustomBuild
                    ExternalProjectStarted
                    ExternalProjectFinished
                    ExtendedCustomBuild
        */

        private BinaryLogRecordKind WriteCore(BuildEventArgs e)
        {
            switch (e)
            {
                case BuildMessageEventArgs buildMessage: return Write(buildMessage);
                case TaskStartedEventArgs taskStarted: return Write(taskStarted);
                case TaskFinishedEventArgs taskFinished: return Write(taskFinished);
                case TargetStartedEventArgs targetStarted: return Write(targetStarted);
                case TargetFinishedEventArgs targetFinished: return Write(targetFinished);
                case BuildErrorEventArgs buildError: return Write(buildError);
                case BuildWarningEventArgs buildWarning: return Write(buildWarning);
                case ProjectStartedEventArgs projectStarted: return Write(projectStarted);
                case ProjectFinishedEventArgs projectFinished: return Write(projectFinished);
                case BuildSubmissionStartedEventArgs buildSubmissionStarted: return Write(buildSubmissionStarted);
                case BuildStartedEventArgs buildStarted: return Write(buildStarted);
                case BuildFinishedEventArgs buildFinished: return Write(buildFinished);
                case BuildCanceledEventArgs buildCanceled: return Write(buildCanceled);
                case ProjectEvaluationStartedEventArgs projectEvaluationStarted: return Write(projectEvaluationStarted);
                case ProjectEvaluationFinishedEventArgs projectEvaluationFinished: return Write(projectEvaluationFinished);
                case BuildCheckTracingEventArgs buildCheckTracing: return Write(buildCheckTracing);
                case BuildCheckAcquisitionEventArgs buildCheckAcquisition: return Write(buildCheckAcquisition);
                default:
                    // convert all unrecognized objects to message
                    // and just preserve the message
                    BuildMessageEventArgs buildMessageEventArgs;
                    if (e is IExtendedBuildEventArgs extendedData)
                    {
                        // For Extended events convert to ExtendedBuildMessageEventArgs
                        buildMessageEventArgs = new ExtendedBuildMessageEventArgs(
                            extendedData.ExtendedType,
                            e.Message,
                            e.HelpKeyword,
                            e.SenderName,
                            MessageImportance.Normal,
                            e.Timestamp)
                        {
                            ExtendedData = extendedData.ExtendedData,
                            ExtendedMetadata = extendedData.ExtendedMetadata,
                        };
                    }
                    else
                    {
                        buildMessageEventArgs = new BuildMessageEventArgs(
                            e.Message,
                            e.HelpKeyword,
                            e.SenderName,
                            MessageImportance.Normal,
                            e.Timestamp);
                    }
                    buildMessageEventArgs.BuildEventContext = e.BuildEventContext ?? BuildEventContext.Invalid;
                    return Write(buildMessageEventArgs);
            }
        }

        public void WriteBlob(BinaryLogRecordKind kind, Stream stream)
        {
            if (stream.CanSeek && stream.Length > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(stream));
            }

            // write the blob directly to the underlying writer,
            // bypassing the memory stream
            using var redirection = RedirectWritesToOriginalWriter();

            Write(kind);
            Write((int)stream.Length);
            WriteToOriginalStream(stream);
        }

        /// <summary>
        /// Switches the binaryWriter used by the Write* methods to the direct underlying stream writer
        /// until the disposable is disposed. Useful to bypass the currentRecordWriter to write a string,
        /// blob or NameValueRecord that should precede the record being currently written.
        /// </summary>
        private IDisposable RedirectWritesToOriginalWriter()
        {
            return RedirectWritesToDifferentWriter(originalBinaryWriter, currentRecordWriter);
        }

        private IDisposable RedirectWritesToDifferentWriter(BinaryWriter inScopeWriter, BinaryWriter afterScopeWriter)
        {
            binaryWriter = inScopeWriter;
            return new CleanupScope(() => binaryWriter = afterScopeWriter);
        }

        private BinaryLogRecordKind Write(BuildStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e);
            if (Traits.LogAllEnvironmentVariables)
            {
                Write(e.BuildEnvironment);
            }
            else
            {
                Write(e.BuildEnvironment?.Where(kvp => EnvironmentUtilities.IsWellKnownEnvironmentDerivedProperty(kvp.Key)));
            }

            return BinaryLogRecordKind.BuildStarted;
        }

        private BinaryLogRecordKind Write(BuildFinishedEventArgs e)
        {
            WriteBuildEventArgsFields(e);
            Write(e.Succeeded);

            return BinaryLogRecordKind.BuildFinished;
        }

        private BinaryLogRecordKind Write(BuildCanceledEventArgs e)
        {
            WriteBuildEventArgsFields(e);

            return BinaryLogRecordKind.BuildCanceled;
        }

        private BinaryLogRecordKind Write(ProjectEvaluationStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.ProjectFile);
            return BinaryLogRecordKind.ProjectEvaluationStarted;
        }

        private BinaryLogRecordKind Write(BuildCheckTracingEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);

            Dictionary<string, TimeSpan> stats = e.TracingData.ExtractCheckStats();
            stats.Merge(e.TracingData.InfrastructureTracingData, (span1, span2) => span1 + span2);

            WriteProperties(stats);

            return BinaryLogRecordKind.BuildCheckTracing;
        }

        private BinaryLogRecordKind Write(BuildCheckAcquisitionEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.AcquisitionPath);
            WriteDeduplicatedString(e.ProjectPath);

            return BinaryLogRecordKind.BuildCheckAcquisition;
        }

        private BinaryLogRecordKind Write(ProjectEvaluationFinishedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.ProjectFile);

            WriteProperties(e.GlobalProperties);

            WriteProperties(e.Properties);

            WriteProjectItems(e.Items);

            var result = e.ProfilerResult;
            Write(result.HasValue);
            if (result.HasValue)
            {
                Write(result.Value.ProfiledLocations.Count);

                foreach (var item in result.Value.ProfiledLocations)
                {
                    Write(item.Key);
                    Write(item.Value);
                }
            }

            return BinaryLogRecordKind.ProjectEvaluationFinished;
        }

        private BinaryLogRecordKind Write(BuildSubmissionStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            Write(e.GlobalProperties);
            WriteStringList(e.EntryProjectsFullPath);
            WriteStringList(e.TargetNames);
            Write((int)e.Flags);
            Write(e.SubmissionId);

            return BinaryLogRecordKind.BuildSubmissionStarted;
        }

        private BinaryLogRecordKind Write(ProjectStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);

            if (e.ParentProjectBuildEventContext == null)
            {
                Write(false);
            }
            else
            {
                Write(true);
                Write(e.ParentProjectBuildEventContext);
            }

            WriteDeduplicatedString(e.ProjectFile);

            Write(e.ProjectId);
            WriteDeduplicatedString(e.TargetNames);
            WriteDeduplicatedString(e.ToolsVersion);

            Write(e.GlobalProperties);

            WriteProperties(e.Properties);

            WriteProjectItems(e.Items);

            return BinaryLogRecordKind.ProjectStarted;
        }

        private BinaryLogRecordKind Write(ProjectFinishedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.Succeeded);

            return BinaryLogRecordKind.ProjectFinished;
        }

        private BinaryLogRecordKind Write(TargetStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.TargetName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.ParentTarget);
            Write((int)e.BuildReason);

            return BinaryLogRecordKind.TargetStarted;
        }

        private BinaryLogRecordKind Write(TargetFinishedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            Write(e.Succeeded);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.TargetName);
            WriteTaskItemList(e.TargetOutputs);

            return BinaryLogRecordKind.TargetFinished;
        }

        private BinaryLogRecordKind Write(TaskStartedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false, writeLineAndColumn: true);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            WriteDeduplicatedString(e.TaskName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TaskFile);
            WriteDeduplicatedString(e.TaskAssemblyLocation);

            return BinaryLogRecordKind.TaskStarted;
        }

        private BinaryLogRecordKind Write(TaskFinishedEventArgs e)
        {
            WriteBuildEventArgsFields(e, writeMessage: false);
            Write(e.Succeeded);
            WriteDeduplicatedString(e.TaskName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TaskFile);

            return BinaryLogRecordKind.TaskFinished;
        }

        private BinaryLogRecordKind Write(BuildErrorEventArgs e)
        {
            WriteBuildEventArgsFields(e);
            WriteArguments(e.RawArguments);
            WriteDeduplicatedString(e.Subcategory);
            WriteDeduplicatedString(e.Code);
            WriteDeduplicatedString(e.File);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            Write(e.EndLineNumber);
            Write(e.EndColumnNumber);

            return BinaryLogRecordKind.Error;
        }

        private BinaryLogRecordKind Write(BuildWarningEventArgs e)
        {
            WriteBuildEventArgsFields(e);
            WriteArguments(e.RawArguments);
            WriteDeduplicatedString(e.Subcategory);
            WriteDeduplicatedString(e.Code);
            WriteDeduplicatedString(e.File);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            Write(e.EndLineNumber);
            Write(e.EndColumnNumber);

            return BinaryLogRecordKind.Warning;
        }

        private BinaryLogRecordKind Write(BuildMessageEventArgs e)
        {
            switch (e)
            {
                case ResponseFileUsedEventArgs responseFileUsed: return Write(responseFileUsed);
                case TaskParameterEventArgs taskParameter: return Write(taskParameter);
                case ProjectImportedEventArgs projectImported: return Write(projectImported);
                case TargetSkippedEventArgs targetSkipped: return Write(targetSkipped);
                case PropertyReassignmentEventArgs propertyReassignment: return Write(propertyReassignment);
                case TaskCommandLineEventArgs taskCommandLine: return Write(taskCommandLine);
                case UninitializedPropertyReadEventArgs uninitializedPropertyRead: return Write(uninitializedPropertyRead);
                case EnvironmentVariableReadEventArgs environmentVariableRead: return Write(environmentVariableRead);
                case PropertyInitialValueSetEventArgs propertyInitialValueSet: return Write(propertyInitialValueSet);
                case CriticalBuildMessageEventArgs criticalBuildMessage: return Write(criticalBuildMessage);
                case AssemblyLoadBuildEventArgs assemblyLoad: return Write(assemblyLoad);

                default: // actual BuildMessageEventArgs
                    WriteMessageFields(e, writeImportance: true);
                    return BinaryLogRecordKind.Message;
            }
        }

        private BinaryLogRecordKind Write(ProjectImportedEventArgs e)
        {
            WriteMessageFields(e);
            Write(e.ImportIgnored);
            WriteDeduplicatedString(e.ImportedProjectFile);
            WriteDeduplicatedString(e.UnexpandedProject);
            return BinaryLogRecordKind.ProjectImported;
        }

        private BinaryLogRecordKind Write(TargetSkippedEventArgs e)
        {
            WriteMessageFields(e, writeMessage: false);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.TargetName);
            WriteDeduplicatedString(e.ParentTarget);
            WriteDeduplicatedString(e.Condition);
            WriteDeduplicatedString(e.EvaluatedCondition);
            Write(e.OriginallySucceeded);
            Write((int)e.BuildReason);
            Write((int)e.SkipReason);
            binaryWriter.WriteOptionalBuildEventContext(e.OriginalBuildEventContext);
            return BinaryLogRecordKind.TargetSkipped;
        }

        private BinaryLogRecordKind Write(AssemblyLoadBuildEventArgs e)
        {
            WriteMessageFields(e, writeMessage: false, writeImportance: false);
            Write((int)e.LoadingContext);
            WriteDeduplicatedString(e.LoadingInitiator);
            WriteDeduplicatedString(e.AssemblyName);
            WriteDeduplicatedString(e.AssemblyPath);
            Write(e.MVID);
            WriteDeduplicatedString(e.AppDomainDescriptor);
            return BinaryLogRecordKind.AssemblyLoad;
        }

        private BinaryLogRecordKind Write(CriticalBuildMessageEventArgs e)
        {
            WriteMessageFields(e);
            return BinaryLogRecordKind.CriticalBuildMessage;
        }

        private BinaryLogRecordKind Write(PropertyReassignmentEventArgs e)
        {
            WriteMessageFields(e, writeMessage: true, writeImportance: true);
            WriteDeduplicatedString(e.PropertyName);
            WriteDeduplicatedString(e.PreviousValue);
            WriteDeduplicatedString(e.NewValue);
            WriteDeduplicatedString(e.Location);

            return BinaryLogRecordKind.PropertyReassignment;
        }

        private BinaryLogRecordKind Write(UninitializedPropertyReadEventArgs e)
        {
            WriteMessageFields(e, writeImportance: true);
            WriteDeduplicatedString(e.PropertyName);
            return BinaryLogRecordKind.UninitializedPropertyRead;
        }

        private BinaryLogRecordKind Write(PropertyInitialValueSetEventArgs e)
        {
            WriteMessageFields(e, writeImportance: true);
            WriteDeduplicatedString(e.PropertyName);
            WriteDeduplicatedString(e.PropertyValue);
            WriteDeduplicatedString(e.PropertySource);
            return BinaryLogRecordKind.PropertyInitialValueSet;
        }

        private BinaryLogRecordKind Write(EnvironmentVariableReadEventArgs e)
        {
            WriteMessageFields(e, writeImportance: false);
            WriteDeduplicatedString(e.EnvironmentVariableName);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            WriteDeduplicatedString(e.File);

            return BinaryLogRecordKind.EnvironmentVariableRead;
        }

        private BinaryLogRecordKind Write(ResponseFileUsedEventArgs e)
        {
            WriteMessageFields(e);
            WriteDeduplicatedString(e.ResponseFilePath);
            return BinaryLogRecordKind.ResponseFileUsed;
        }

        private BinaryLogRecordKind Write(TaskCommandLineEventArgs e)
        {
            WriteMessageFields(e, writeMessage: false, writeImportance: true);
            WriteDeduplicatedString(e.CommandLine);
            WriteDeduplicatedString(e.TaskName);
            return BinaryLogRecordKind.TaskCommandLine;
        }

        private BinaryLogRecordKind Write(TaskParameterEventArgs e)
        {
            WriteMessageFields(e, writeMessage: false);
            Write((int)e.Kind);
            WriteDeduplicatedString(e.ItemType);
            WriteTaskItemList(e.Items, e.LogItemMetadata);
            WriteDeduplicatedString(e.ParameterName);
            WriteDeduplicatedString(e.PropertyName);
            if (e.Kind == TaskParameterMessageKind.AddItem
               || e.Kind == TaskParameterMessageKind.TaskOutput)
            {
                CheckForFilesToEmbed(e.ItemType, e.Items);
            }
            return BinaryLogRecordKind.TaskParameter;
        }

        private void WriteBuildEventArgsFields(BuildEventArgs e, bool writeMessage = true, bool writeLineAndColumn = false)
        {
            var flags = GetBuildEventArgsFieldFlags(e, writeMessage);
            if (writeLineAndColumn)
            {
                flags |= BuildEventArgsFieldFlags.LineNumber | BuildEventArgsFieldFlags.ColumnNumber;
            }

            Write((int)flags);
            WriteBaseFields(e, flags);
        }

        private void WriteBaseFields(BuildEventArgs e, BuildEventArgsFieldFlags flags)
        {
            if ((flags & BuildEventArgsFieldFlags.Message) != 0)
            {
                WriteDeduplicatedString(e.RawMessage);
            }

            if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
            {
                Write(e.BuildEventContext);
            }

            if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                Write(e.ThreadId);
            }

            if ((flags & BuildEventArgsFieldFlags.HelpKeyword) != 0)
            {
                WriteDeduplicatedString(e.HelpKeyword);
            }

            if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                WriteDeduplicatedString(e.SenderName);
            }

            if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                Write(e.Timestamp);
            }

            if ((flags & BuildEventArgsFieldFlags.Extended) != 0)
            {
                Write(e as IExtendedBuildEventArgs);
            }
        }

        private void WriteMessageFields(BuildMessageEventArgs e, bool writeMessage = true, bool writeImportance = false)
        {
            var flags = GetBuildEventArgsFieldFlags(e, writeMessage);
            flags = GetMessageFlags(e, flags, writeImportance);

            Write((int)flags);

            WriteBaseFields(e, flags);

            if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
            {
                WriteDeduplicatedString(e.Subcategory);
            }

            if ((flags & BuildEventArgsFieldFlags.Code) != 0)
            {
                WriteDeduplicatedString(e.Code);
            }

            if ((flags & BuildEventArgsFieldFlags.File) != 0)
            {
                WriteDeduplicatedString(e.File);
            }

            if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
            {
                WriteDeduplicatedString(e.ProjectFile);
            }

            if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
            {
                Write(e.LineNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
            {
                Write(e.ColumnNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
            {
                Write(e.EndLineNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
            {
                Write(e.EndColumnNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.Arguments) != 0)
            {
                WriteArguments(e.RawArguments);
            }

            if ((flags & BuildEventArgsFieldFlags.Importance) != 0)
            {
                Write((int)e.Importance);
            }
        }

        private void WriteArguments(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
            {
                return;
            }

            int count = arguments.Length;
            Write(count);
            for (int i = 0; i < count; i++)
            {
                string argument = Convert.ToString(arguments[i], CultureInfo.CurrentCulture);
                WriteDeduplicatedString(argument);
            }
        }

        private static BuildEventArgsFieldFlags GetMessageFlags(BuildMessageEventArgs e, BuildEventArgsFieldFlags flags, bool writeImportance = false)
        {
            if (e.Subcategory != null)
            {
                flags |= BuildEventArgsFieldFlags.Subcategory;
            }

            if (e.Code != null)
            {
                flags |= BuildEventArgsFieldFlags.Code;
            }

            if (e.File != null)
            {
                flags |= BuildEventArgsFieldFlags.File;
            }

            if (e.ProjectFile != null)
            {
                flags |= BuildEventArgsFieldFlags.ProjectFile;
            }

            if (e.LineNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.LineNumber;
            }

            if (e.ColumnNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.ColumnNumber;
            }

            if (e.EndLineNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.EndLineNumber;
            }

            if (e.EndColumnNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.EndColumnNumber;
            }

            if (writeImportance && e.Importance != MessageImportance.Low)
            {
                flags |= BuildEventArgsFieldFlags.Importance;
            }

            return flags;
        }

        private static BuildEventArgsFieldFlags GetBuildEventArgsFieldFlags(BuildEventArgs e, bool writeMessage = true)
        {
            var flags = BuildEventArgsFieldFlags.None;
            if (e.BuildEventContext != null)
            {
                flags |= BuildEventArgsFieldFlags.BuildEventContext;
            }

            if (e.HelpKeyword != null)
            {
                flags |= BuildEventArgsFieldFlags.HelpKeyword;
            }

            if (writeMessage)
            {
                flags |= BuildEventArgsFieldFlags.Message;

                // We're only going to write the arguments for messages,
                // warnings and errors. Only set the flag for these.
                if (e is LazyFormattedBuildEventArgs { RawArguments: { Length: > 0 } } and
                    (BuildMessageEventArgs or BuildWarningEventArgs or BuildErrorEventArgs))
                {
                    flags |= BuildEventArgsFieldFlags.Arguments;
                }
            }

            // no need to waste space for the default sender name
            if (e.SenderName != null && e.SenderName != "MSBuild")
            {
                flags |= BuildEventArgsFieldFlags.SenderName;
            }

            if (e.Timestamp != default(DateTime))
            {
                flags |= BuildEventArgsFieldFlags.Timestamp;
            }

            if (e is IExtendedBuildEventArgs extendedData)
            {
                flags |= BuildEventArgsFieldFlags.Extended;
            }

            return flags;
        }

        // Both of these are used simultaneously so can't just have a single list
        private readonly List<object> reusableItemsList = new List<object>();
        private readonly List<object> reusableProjectItemList = new List<object>();

        private void WriteTaskItemList(IEnumerable items, bool writeMetadata = true)
        {
            if (items == null)
            {
                Write(false);
                return;
            }

            // For target outputs bypass copying of all items to save on performance.
            // The proxy creates a deep clone of each item to protect against writes,
            // but since we're not writing we don't need the deep cloning.
            // Additionally, it is safe to access the underlying List<ITaskItem> as it's allocated
            // in a single location and noboby else mutates it after that:
            // https://github.com/dotnet/msbuild/blob/f0eebf2872d76ab0cd43fdc4153ba636232b222f/src/Build/BackEnd/Components/RequestBuilder/TargetEntry.cs#L564
            if (items is TargetLoggingContext.TargetOutputItemsInstanceEnumeratorProxy proxy)
            {
                items = proxy.BackingItems;
            }

            int count;

            if (items is ICollection arrayList)
            {
                count = arrayList.Count;
            }
            else if (items is ICollection<ITaskItem> genericList)
            {
                count = genericList.Count;
            }
            else
            {
                // enumerate only once
                foreach (var item in items)
                {
                    if (item != null)
                    {
                        reusableItemsList.Add(item);
                    }
                }

                items = reusableItemsList;
                count = reusableItemsList.Count;
            }

            Write(count);

            foreach (var item in items)
            {
                if (item is ITaskItem taskItem)
                {
                    Write(taskItem, writeMetadata);
                }
                else
                {
                    WriteDeduplicatedString(item?.ToString() ?? ""); // itemspec
                    Write(0); // no metadata
                }
            }

            reusableItemsList.Clear();
        }

        private void WriteProjectItems(IEnumerable items)
        {
            if (items == null)
            {
                Write(0);
                return;
            }

            if (items is ItemDictionary<ProjectItemInstance> itemInstanceDictionary)
            {
                // If we have access to the live data from evaluation, it exposes a special method
                // to iterate the data structure under a lock and return results grouped by item type.
                // There's no need to allocate or call GroupBy this way.
                itemInstanceDictionary.EnumerateItemsPerType((itemType, itemList) =>
                {
                    WriteDeduplicatedString(itemType);
                    WriteTaskItemList(itemList);
                    CheckForFilesToEmbed(itemType, itemList);
                });

                // signal the end
                Write(0);
            }
            // not sure when this can get hit, but best to be safe and support this
            else if (items is ItemDictionary<ProjectItem> itemDictionary)
            {
                itemDictionary.EnumerateItemsPerType((itemType, itemList) =>
                {
                    WriteDeduplicatedString(itemType);
                    WriteTaskItemList(itemList);
                    CheckForFilesToEmbed(itemType, itemList);
                });

                // signal the end
                Write(0);
            }
            else
            {
                string currentItemType = null;

                // Write out a sequence of items for each item type while avoiding GroupBy
                // and associated allocations. We rely on the fact that items of each type
                // are contiguous. For each item type, write the item type name and the list
                // of items. Write 0 at the end (which would correspond to item type null).
                // This is how the reader will know how to stop. We can't write out the
                // count of item types at the beginning because we don't know how many there
                // will be (we'd have to enumerate twice to calculate that). This scheme
                // allows us to stream in a single pass with no allocations for intermediate
                // results.
                Internal.Utilities.EnumerateItems(items, dictionaryEntry =>
                {
                    string key = (string)dictionaryEntry.Key;

                    // boundary between item types
                    if (currentItemType != null && currentItemType != key)
                    {
                        WriteDeduplicatedString(currentItemType);
                        WriteTaskItemList(reusableProjectItemList);
                        CheckForFilesToEmbed(currentItemType, reusableProjectItemList);
                        reusableProjectItemList.Clear();
                    }

                    reusableProjectItemList.Add(dictionaryEntry.Value);
                    currentItemType = key;
                });

                // write out the last item type
                if (reusableProjectItemList.Count > 0)
                {
                    WriteDeduplicatedString(currentItemType);
                    WriteTaskItemList(reusableProjectItemList);
                    CheckForFilesToEmbed(currentItemType, reusableProjectItemList);
                    reusableProjectItemList.Clear();
                }

                // signal the end
                Write(0);
            }
        }

        private void CheckForFilesToEmbed(string itemType, object itemList)
        {
            if (EmbedFile == null ||
                !string.Equals(itemType, ItemTypeNames.EmbedInBinlog, StringComparison.OrdinalIgnoreCase) ||
                itemList is not IEnumerable list)
            {
                return;
            }

            foreach (var item in list)
            {
                if (item is ITaskItem taskItem && !string.IsNullOrEmpty(taskItem.ItemSpec))
                {
                    EmbedFile.Invoke(taskItem.ItemSpec);
                }
                else if (item is string itemSpec && !string.IsNullOrEmpty(itemSpec))
                {
                    EmbedFile.Invoke(itemSpec);
                }
            }
        }

        private void Write(ITaskItem item, bool writeMetadata = true)
        {
            WriteDeduplicatedString(item.ItemSpec);
            if (!writeMetadata)
            {
                Write((byte)0);
                return;
            }

            // WARNING: Can't use AddRange here because CopyOnWriteDictionary in Microsoft.Build.Utilities.v4.0.dll
            // is broken. Microsoft.Build.Utilities.v4.0.dll loads from the GAC by XAML markup tooling and it's
            // implementation doesn't work with AddRange because AddRange special-cases ICollection<T> and
            // CopyOnWriteDictionary doesn't implement it properly.
            foreach (var kvp in item.EnumerateMetadata())
            {
                nameValueListBuffer.Add(kvp);
            }

            // Don't sort metadata because we want the binary log to be fully roundtrippable
            // and we need to preserve the original order.
            // if (nameValueListBuffer.Count > 1)
            // {
            //    nameValueListBuffer.Sort((l, r) => StringComparer.OrdinalIgnoreCase.Compare(l.Key, r.Key));
            // }

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void WriteProperties(IEnumerable properties)
        {
            if (properties == null)
            {
                Write(0);
                return;
            }

            Internal.Utilities.EnumerateProperties(properties, nameValueListBuffer, static (list, kvp) => list.Add(kvp));

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void Write(BuildEventContext buildEventContext)
        {
            Write(buildEventContext.NodeId);
            Write(buildEventContext.ProjectContextId);
            Write(buildEventContext.TargetId);
            Write(buildEventContext.TaskId);
            Write(buildEventContext.SubmissionId);
            Write(buildEventContext.ProjectInstanceId);
            Write(buildEventContext.EvaluationId);
        }

        private void Write(IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (keyValuePairs != null)
            {
                foreach (var kvp in keyValuePairs)
                {
                    nameValueListBuffer.Add(kvp);
                }
            }

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void WriteStringList(IEnumerable<string> items)
        {
            int length = items.Count();
            Write(length);
            foreach (string entry in items)
            {
                WriteDeduplicatedString(entry);
            }
        }

        private void WriteNameValueList()
        {
            if (nameValueListBuffer.Count == 0)
            {
                Write((byte)0);
                return;
            }

            HashKey hash = HashAllStrings(nameValueListBuffer);
            if (!nameValueListHashes.TryGetValue(hash, out var recordId))
            {
                recordId = nameValueRecordId;
                nameValueListHashes[hash] = nameValueRecordId;

                WriteNameValueListRecord();

                nameValueRecordId += 1;
            }

            Write(recordId);
        }

        /// <summary>
        /// In the middle of writing the current record we may discover that we want to write another record
        /// preceding the current one, specifically the list of names and values we want to reuse in the
        /// future. As we are writing the current record to a MemoryStream first, it's OK to temporarily
        /// switch to the direct underlying stream and write the NameValueList record first.
        /// When the current record is done writing, the MemoryStream will flush to the underlying stream
        /// and the current record will end up after the NameValueList record, as desired.
        /// </summary>
        private void WriteNameValueListRecord()
        {
            // We want this record to precede the record we're currently writing to currentRecordWriter
            // We as well want to know the storage size (differs from nameValueIndexListBuffer.Count as
            //  we use variable integer encoding).
            // So we redirect the writes to a MemoryStream and then flush the record to the final stream.
            // All that is redirected away from the 'currentRecordStream' - that will be flushed last

            nameValueListStream.SetLength(0);
            var nameValueListBw = new BinaryWriter(nameValueListStream);

            using (var _ = RedirectWritesToDifferentWriter(nameValueListBw, binaryWriter))
            {
                Write(nameValueIndexListBuffer.Count);
                for (int i = 0; i < nameValueListBuffer.Count; i++)
                {
                    var kvp = nameValueIndexListBuffer[i];
                    Write(kvp.Key);
                    Write(kvp.Value);
                }
            }

            FlushRecordToFinalStream(BinaryLogRecordKind.NameValueList, nameValueListStream);
        }

        /// <summary>
        /// Compute the total hash of all items in the nameValueList
        /// while simultaneously filling the nameValueIndexListBuffer with the individual
        /// hashes of the strings, mirroring the strings in the original nameValueList.
        /// This helps us avoid hashing strings twice (once to hash the string individually
        /// and the second time when hashing it as part of the nameValueList)
        /// </summary>
        private HashKey HashAllStrings(List<KeyValuePair<string, string>> nameValueList)
        {
            HashKey hash = new HashKey();

            nameValueIndexListBuffer.Clear();

            for (int i = 0; i < nameValueList.Count; i++)
            {
                var kvp = nameValueList[i];
                var (keyIndex, keyHash) = HashString(kvp.Key);
                var (valueIndex, valueHash) = HashString(kvp.Value);
                hash = hash.Add(keyHash);
                hash = hash.Add(valueHash);
                nameValueIndexListBuffer.Add(new KeyValuePair<int, int>(keyIndex, valueIndex));
            }

            return hash;
        }

        private void Write(BinaryLogRecordKind kind)
        {
            Write((int)kind);
        }

        internal void Write(int value)
        {
            BinaryWriterExtensions.Write7BitEncodedInt(binaryWriter, value);
        }

        private void Write(long value)
        {
            binaryWriter.Write(value);
        }

        private void Write(byte[] bytes)
        {
            binaryWriter.Write(bytes);
        }

        private void WriteToOriginalStream(Stream stream)
        {
            // WARNING: avoid calling binaryWriter.BaseStream here
            // as it will flush the underlying stream - since that is a
            // BufferedStream it will make buffering nearly useless
            stream.CopyTo(originalStream);
        }

        private void Write(byte b)
        {
            binaryWriter.Write(b);
        }

        private void Write(bool boolean)
        {
            binaryWriter.Write(boolean);
        }

        private unsafe void Write(Guid guid)
        {
            byte* ptr = (byte*)&guid;
            for (int i = 0; i < sizeof(Guid); i++, ptr++)
            {
                binaryWriter.Write(*ptr);
            }
        }

        private void WriteDeduplicatedString(string text)
        {
            var (recordId, _) = HashString(text);
            Write(recordId);
        }

        /// <summary>
        /// Hash the string and write a String record if not already hashed.
        /// </summary>
        /// <returns>Returns the string record index as well as the hash.</returns>
        private (int index, HashKey hash) HashString(string text)
        {
            if (text == null)
            {
                return (0, default);
            }
            else if (text.Length == 0)
            {
                return (1, default);
            }

            var hash = new HashKey(text);
            if (!stringHashes.TryGetValue(hash, out var recordId))
            {
                recordId = stringRecordId;
                stringHashes[hash] = stringRecordId;

                WriteStringRecord(text);

                stringRecordId += 1;
            }

            return (recordId, hash);
        }

        internal void WriteStringRecord(string text)
        {
            using var redirectionScope = RedirectWritesToOriginalWriter();

            Write(BinaryLogRecordKind.String);
            binaryWriter.Write(text);
        }

        private void Write(DateTime timestamp)
        {
            binaryWriter.Write(timestamp.Ticks);
            Write((int)timestamp.Kind);
        }

        private void Write(TimeSpan timeSpan)
        {
            binaryWriter.Write(timeSpan.Ticks);
        }

        private void Write(EvaluationLocation item)
        {
            WriteDeduplicatedString(item.ElementName);
            WriteDeduplicatedString(item.ElementDescription);
            WriteDeduplicatedString(item.EvaluationPassDescription);
            WriteDeduplicatedString(item.File);
            Write((int)item.Kind);
            Write((int)item.EvaluationPass);

            Write(item.Line.HasValue);
            if (item.Line.HasValue)
            {
                Write(item.Line.Value);
            }

            Write(item.Id);
            Write(item.ParentId.HasValue);
            if (item.ParentId.HasValue)
            {
                Write(item.ParentId.Value);
            }
        }

        private void Write(ProfiledLocation e)
        {
            Write(e.NumberOfHits);
            Write(e.ExclusiveTime);
            Write(e.InclusiveTime);
        }

        private void Write(IExtendedBuildEventArgs extendedData)
        {
            if (extendedData?.ExtendedType != null)
            {
                WriteDeduplicatedString(extendedData.ExtendedType);
                Write(extendedData.ExtendedMetadata);
                WriteDeduplicatedString(extendedData.ExtendedData);
            }
        }

        internal readonly struct HashKey : IEquatable<HashKey>
        {
            private readonly long value;

            private HashKey(long i)
            {
                value = i;
            }

            public HashKey(string text)
            {
                if (text == null)
                {
                    value = 0;
                }
                else
                {
                    value = FowlerNollVo1aHash.ComputeHash64Fast(text);
                }
            }

            public static HashKey Combine(HashKey left, HashKey right)
            {
                return new HashKey(FowlerNollVo1aHash.Combine64(left.value, right.value));
            }

            public HashKey Add(HashKey other) => Combine(this, other);

            public bool Equals(HashKey other)
            {
                return value == other.value;
            }

            public override bool Equals(object obj)
            {
                if (obj is HashKey other)
                {
                    return Equals(other);
                }

                return false;
            }

            public override int GetHashCode()
            {
                return unchecked((int)value);
            }

            public override string ToString()
            {
                return value.ToString();
            }
        }
    }
}
