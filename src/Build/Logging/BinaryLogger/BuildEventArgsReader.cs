// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.BuildCheck;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Logging
{
    /// <summary>
    /// Deserializes and returns BuildEventArgs-derived objects from a BinaryReader.
    /// </summary>
    public class BuildEventArgsReader : IBuildEventArgsReaderNotifications, IDisposable
    {
        private readonly BinaryReader _binaryReader;
        // This is used to verify that events deserialization is not overreading expected size.
        private readonly TransparentReadStream _readStream;
        private readonly int _fileFormatVersion;
        private long _recordNumber = 0;
        private bool _skipUnknownEvents;
        private bool _skipUnknownEventParts;

        /// <summary>
        /// A list of string records we've encountered so far. If it's a small string, it will be the string directly.
        /// If it's a large string, it will be a pointer into a temporary page file where the string content will be
        /// written out to. This is necessary so we don't keep all the strings in memory when reading large binlogs.
        /// We will OOM otherwise.
        /// </summary>
        private readonly List<object> stringRecords = new List<object>();

        /// <summary>
        /// A list of dictionaries we've encountered so far. Dictionaries are referred to by their order in this list.
        /// </summary>
        /// <remarks>This is designed to not hold on to strings. We just store the string indices and
        /// hydrate the dictionary on demand before returning.</remarks>
        private readonly List<(int keyIndex, int valueIndex)[]> nameValueListRecords = new List<(int, int)[]>();

        /// <summary>
        /// A "page-file" for storing strings we've read so far. Keeping them in memory would OOM the 32-bit MSBuild
        /// when reading large binlogs. This is a no-op in a 64-bit process.
        /// </summary>
        private readonly StringStorage stringStorage = new StringStorage();

        // reflection is needed to set these three fields because public constructors don't provide
        // a way to set these from the outside
        private static FieldInfo? buildEventArgsFieldThreadId =
            typeof(BuildEventArgs).GetField("threadId", BindingFlags.Instance | BindingFlags.NonPublic);
        private static FieldInfo? buildEventArgsFieldSenderName =
            typeof(BuildEventArgs).GetField("senderName", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Initializes a new instance of <see cref="BuildEventArgsReader"/> using a <see cref="BinaryReader"/> instance.
        /// </summary>
        /// <param name="binaryReader">The <see cref="BinaryReader"/> to read <see cref="BuildEventArgs"/> from.</param>
        /// <param name="fileFormatVersion">The file format version of the log file being read.</param>
        public BuildEventArgsReader(BinaryReader binaryReader, int fileFormatVersion)
        {
            this._readStream = TransparentReadStream.EnsureTransparentReadStream(binaryReader.BaseStream);
            // make sure the reader we're going to use wraps the transparent stream wrapper
            this._binaryReader = binaryReader.BaseStream == _readStream
                ? binaryReader
                : new BinaryReader(_readStream);
            this._fileFormatVersion = fileFormatVersion;
        }

        /// <summary>
        /// Directs whether the passed <see cref="BinaryReader"/> should be closed when this instance is disposed.
        /// Defaults to "false".
        /// </summary>
        public bool CloseInput { private get; set; } = false;

        /// <summary>
        /// Indicates whether unknown BuildEvents should be silently skipped. Read returns null otherwise.
        /// Parameter is supported only if the file format supports forward compatible reading (version is 18 or higher).
        /// </summary>
        public bool SkipUnknownEvents
        {
            set
            {
                if (value)
                {
                    EnsureForwardCompatibleReadingSupported();
                }

                _skipUnknownEvents = value;
            }
        }

        /// <summary>
        /// Indicates whether unread parts of BuildEvents (probably added in newer format of particular BuildEvent)should be silently skipped. Exception thrown otherwise.
        /// Parameter is supported only if the file format supports forward compatible reading (version is 18 or higher).
        /// </summary>
        public bool SkipUnknownEventParts
        {
            set
            {
                if (value)
                {
                    EnsureForwardCompatibleReadingSupported();
                }
                _skipUnknownEventParts = value;
            }
        }

        private void EnsureForwardCompatibleReadingSupported()
        {
            if (_fileFormatVersion < BinaryLogger.ForwardCompatibilityMinimalVersion)
            {
                throw new InvalidOperationException(
                    ResourceUtilities.FormatResourceStringStripCodeAndKeyword("Binlog_FwdCompatUnsupported",
                        _fileFormatVersion));
            }
        }

        /// <summary>
        /// Receives recoverable errors during reading. See <see cref="IBuildEventArgsReaderNotifications.RecoverableReadError"/> for documentation on arguments.
        /// Applicable mainly when <see cref="SkipUnknownEvents"/> or <see cref="SkipUnknownEventParts"/> is set to true."/>
        /// </summary>
        public event Action<BinaryLogReaderErrorEventArgs>? RecoverableReadError;

        public void Dispose()
        {
            stringStorage.Dispose();
            if (CloseInput)
            {
                _binaryReader.Dispose();
            }
        }

        /// <inheritdoc cref="IBuildEventArgsReaderNotifications.StringReadDone"/>
        public event Action<StringReadEventArgs>? StringReadDone;

        internal int FileFormatVersion => _fileFormatVersion;
        internal int MinimumReaderVersion { get; set; } = BinaryLogger.ForwardCompatibilityMinimalVersion;

        /// <inheritdoc cref="IBinaryLogReplaySource.EmbeddedContentRead"/>
        internal event Action<EmbeddedContentEventArgs>? EmbeddedContentRead;

        /// <inheritdoc cref="IBuildEventArgsReaderNotifications.ArchiveFileEncountered"/>
        public event Action<ArchiveFileEventArgs>? ArchiveFileEncountered;

        private SubStream? _lastSubStream;

        internal readonly record struct RawRecord(BinaryLogRecordKind RecordKind, Stream Stream);

        /// <summary>
        /// Reads the next serialized log record from the <see cref="BinaryReader"/>.
        /// </summary>
        /// <returns>ArraySegment containing serialized BuildEventArgs record</returns>
        internal RawRecord ReadRaw()
        {
            // This method is internal and condition is checked once before calling in loop,
            //  so avoiding it here on each call.
            // But keeping it for documentation purposes - in case someone will try to call it and debug issues.
            ////if (fileFormatVersion < 18)
            ////{
            ////    throw new InvalidOperationException(
            ////                           $"Raw data reading is not supported for file format version {fileFormatVersion} (needs >=18).");
            ////}

            if (_lastSubStream?.IsAtEnd == false)
            {
                _lastSubStream.ReadToEnd();
            }

            BinaryLogRecordKind recordKind = PreprocessRecordsTillNextEvent(IsTextualDataRecord);

            if (recordKind == BinaryLogRecordKind.EndOfFile)
            {
                return new(recordKind, Stream.Null);
            }

            int serializedEventLength = ReadInt32();
            Stream stream = _binaryReader.BaseStream.Slice(serializedEventLength);

            _lastSubStream = stream as SubStream;
            _recordNumber += 1;

            return new(recordKind, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckErrorsSubscribed()
        {
            if ((_skipUnknownEvents || _skipUnknownEventParts) && RecoverableReadError == null)
            {
                throw new InvalidOperationException(
                    ResourceUtilities.GetResourceString("Binlog_MissingRecoverableErrorSubscribeError"));
            }
        }

        /// <summary>
        /// Reads the next log record from the <see cref="BinaryReader"/>.
        /// </summary>
        /// <returns>
        /// The next <see cref="BuildEventArgs"/>.
        /// If there are no more records, returns <see langword="null"/>.
        /// </returns>
        public BuildEventArgs? Read()
        {
            CheckErrorsSubscribed();
            BuildEventArgs? result = null;
            while (result == null)
            {
                BinaryLogRecordKind recordKind = PreprocessRecordsTillNextEvent(IsAuxiliaryRecord);

                if (recordKind == BinaryLogRecordKind.EndOfFile)
                {
                    return null;
                }

                int serializedEventLength = 0;
                if (_fileFormatVersion >= BinaryLogger.ForwardCompatibilityMinimalVersion)
                {
                    serializedEventLength = ReadInt32(); // record length
                    _readStream.BytesCountAllowedToRead = serializedEventLength;
                }

                bool hasError = false;
                try
                {
                    result = ReadBuildEventArgs(recordKind);
                }
                catch (Exception e) when (
                    // We throw this on mismatches in metadata (name-value list, strings index).
                    e is InvalidDataException ||
                    // Thrown when BinaryReader is unable to deserialize binary data into expected type.
                    e is FormatException ||
                    // Thrown when we attempt to read more bytes than what is in the next event chunk.
                    (e is EndOfStreamException && _readStream.BytesCountAllowedToReadRemaining <= 0))
                {
                    hasError = true;
                    int localSerializedEventLength = serializedEventLength;
                    Exception localException = e;
                    string ErrorFactory() =>
                        ResourceUtilities.FormatResourceStringStripCodeAndKeyword("Binlog_ReaderMismatchedRead",
                            _recordNumber, localSerializedEventLength, localException.GetType(), localException.Message) + (_skipUnknownEvents
                            ? " " + ResourceUtilities.GetResourceString("Binlog_ReaderSkippingRecord")
                            : string.Empty);

                    HandleError(ErrorFactory, _skipUnknownEvents, ReaderErrorType.UnknownFormatOfEventData, recordKind, e);
                }

                if (result == null && !hasError)
                {
                    int localSerializedEventLength = serializedEventLength;
                    BinaryLogRecordKind localRecordKind = recordKind;
                    string ErrorFactory() =>
                        ResourceUtilities.FormatResourceStringStripCodeAndKeyword("Binlog_ReaderUnknownType",
                            _recordNumber, localSerializedEventLength, localRecordKind) + (_skipUnknownEvents
                            ? " " + ResourceUtilities.GetResourceString("Binlog_ReaderSkippingRecord")
                            : string.Empty);

                    HandleError(ErrorFactory, _skipUnknownEvents, ReaderErrorType.UnknownEventType, recordKind);
                }

                if (_readStream.BytesCountAllowedToReadRemaining > 0)
                {
                    int localSerializedEventLength = serializedEventLength;
                    string ErrorFactory() => ResourceUtilities.FormatResourceStringStripCodeAndKeyword(
                        "Binlog_ReaderUnderRead", _recordNumber, localSerializedEventLength,
                        localSerializedEventLength - _readStream.BytesCountAllowedToReadRemaining);

                    HandleError(ErrorFactory, _skipUnknownEventParts, ReaderErrorType.UnknownEventData, recordKind);
                }

                _recordNumber += 1;
            }

            return result;

            void HandleError(FormatErrorMessage msgFactory, bool noThrow, ReaderErrorType readerErrorType, BinaryLogRecordKind recordKind, Exception? innerException = null)
            {
                if (noThrow)
                {
                    RecoverableReadError?.Invoke(new BinaryLogReaderErrorEventArgs(readerErrorType, recordKind, msgFactory));
                    SkipBytes(_readStream.BytesCountAllowedToReadRemaining);
                }
                else
                {
                    throw new InvalidDataException(msgFactory(), innerException);
                }
            }
        }

        private BuildEventArgs? ReadBuildEventArgs(BinaryLogRecordKind recordKind)
            => recordKind switch
            {
                BinaryLogRecordKind.BuildStarted => ReadBuildStartedEventArgs(),
                BinaryLogRecordKind.BuildFinished => ReadBuildFinishedEventArgs(),
                BinaryLogRecordKind.BuildSubmissionStarted => ReadBuildSubmissionStartedEventArgs(),
                BinaryLogRecordKind.ProjectStarted => ReadProjectStartedEventArgs(),
                BinaryLogRecordKind.ProjectFinished => ReadProjectFinishedEventArgs(),
                BinaryLogRecordKind.TargetStarted => ReadTargetStartedEventArgs(),
                BinaryLogRecordKind.TargetFinished => ReadTargetFinishedEventArgs(),
                BinaryLogRecordKind.TaskStarted => ReadTaskStartedEventArgs(),
                BinaryLogRecordKind.TaskFinished => ReadTaskFinishedEventArgs(),
                BinaryLogRecordKind.Error => ReadBuildErrorEventArgs(),
                BinaryLogRecordKind.Warning => ReadBuildWarningEventArgs(),
                BinaryLogRecordKind.Message => ReadBuildMessageEventArgs(),
                BinaryLogRecordKind.CriticalBuildMessage => ReadCriticalBuildMessageEventArgs(),
                BinaryLogRecordKind.TaskCommandLine => ReadTaskCommandLineEventArgs(),
                BinaryLogRecordKind.TaskParameter => ReadTaskParameterEventArgs(),
                BinaryLogRecordKind.ProjectEvaluationStarted => ReadProjectEvaluationStartedEventArgs(),
                BinaryLogRecordKind.ProjectEvaluationFinished => ReadProjectEvaluationFinishedEventArgs(),
                BinaryLogRecordKind.ProjectImported => ReadProjectImportedEventArgs(),
                BinaryLogRecordKind.TargetSkipped => ReadTargetSkippedEventArgs(),
                BinaryLogRecordKind.EnvironmentVariableRead => ReadEnvironmentVariableReadEventArgs(),
                BinaryLogRecordKind.ResponseFileUsed => ReadResponseFileUsedEventArgs(),
                BinaryLogRecordKind.PropertyReassignment => ReadPropertyReassignmentEventArgs(),
                BinaryLogRecordKind.UninitializedPropertyRead => ReadUninitializedPropertyReadEventArgs(),
                BinaryLogRecordKind.PropertyInitialValueSet => ReadPropertyInitialValueSetEventArgs(),
                BinaryLogRecordKind.AssemblyLoad => ReadAssemblyLoadEventArgs(),
                BinaryLogRecordKind.BuildCheckMessage => ReadBuildMessageEventArgs(),
                BinaryLogRecordKind.BuildCheckWarning => ReadBuildWarningEventArgs(),
                BinaryLogRecordKind.BuildCheckError => ReadBuildErrorEventArgs(),
                BinaryLogRecordKind.BuildCheckTracing => ReadBuildCheckTracingEventArgs(),
                BinaryLogRecordKind.BuildCheckAcquisition => ReadBuildCheckAcquisitionEventArgs(),
                BinaryLogRecordKind.BuildCanceled => ReadBuildCanceledEventArgs(),
                _ => null
            };

        private void SkipBytes(int count)
        {
            _binaryReader.BaseStream.Seek(count, SeekOrigin.Current);
        }

        private BinaryLogRecordKind PreprocessRecordsTillNextEvent(Func<BinaryLogRecordKind, bool> isPreprocessRecord)
        {
            _readStream.BytesCountAllowedToRead = null;

            BinaryLogRecordKind recordKind = (BinaryLogRecordKind)ReadInt32();

            // Skip over data storage records since they don't result in a BuildEventArgs.
            // just ingest their data and continue.
            while (isPreprocessRecord(recordKind))
            {
                // these are ordered by commonality
                if (recordKind == BinaryLogRecordKind.String)
                {
                    ReadStringRecord();
                }
                else if (recordKind == BinaryLogRecordKind.NameValueList)
                {
                    ReadNameValueList();
                    _readStream.BytesCountAllowedToRead = null;
                }
                else if (recordKind == BinaryLogRecordKind.ProjectImportArchive)
                {
                    ReadEmbeddedContent(recordKind);
                }

                _recordNumber += 1;

                recordKind = (BinaryLogRecordKind)ReadInt32();
            }

            return recordKind;
        }

        private static bool IsAuxiliaryRecord(BinaryLogRecordKind recordKind)
        {
            return recordKind == BinaryLogRecordKind.String
                || recordKind == BinaryLogRecordKind.NameValueList
                || recordKind == BinaryLogRecordKind.ProjectImportArchive;
        }

        private static bool IsTextualDataRecord(BinaryLogRecordKind recordKind)
        {
            return recordKind == BinaryLogRecordKind.String
                   || recordKind == BinaryLogRecordKind.ProjectImportArchive;
        }

        private void ReadEmbeddedContent(BinaryLogRecordKind recordKind)
        {
            int length = ReadInt32();

            if (ArchiveFileEncountered != null)
            {
                // We could create ZipArchive over the target stream, and write to that directly,
                //  however, binlog format needs to know stream size upfront - which is unknown,
                //  so we would need to write the size after - and that would require the target stream to be seekable (which it's not)
                ProjectImportsCollector? projectImportsCollector = null;

                if (EmbeddedContentRead != null)
                {
                    projectImportsCollector =
                        new ProjectImportsCollector(Path.GetRandomFileName(), false, runOnBackground: false);
                }

                Stream embeddedStream = _binaryReader.BaseStream.Slice(length);

                // We are intentionally not grace handling corrupt embedded stream

                using var zipArchive = new ZipArchive(embeddedStream, ZipArchiveMode.Read);

                foreach (var entry in zipArchive.Entries/*.OrderBy(e => e.LastWriteTime)*/)
                {
                    var file = ArchiveStream.From(entry);
                    ArchiveFileEventArgs archiveFileEventArgs = new(file);
                    // ArchiveFileEventArgs is not IDisposable as we do not want to clutter exposed API
                    using var cleanupScope = new CleanupScope(archiveFileEventArgs.Dispose);
                    ArchiveFileEncountered(archiveFileEventArgs);

                    if (projectImportsCollector != null)
                    {
                        var resultFile = archiveFileEventArgs.ArchiveData;

                        if (resultFile is ArchiveStream archiveStream)
                        {
                            projectImportsCollector.AddFileFromMemory(
                                archiveStream.FullPath,
                                archiveStream.ContentReader.BaseStream,
                                makePathAbsolute: false,
                                entryCreationStamp: entry.LastWriteTime);
                        }
                        else
                        {
                            projectImportsCollector.AddFileFromMemory(
                                resultFile.FullPath,
                                resultFile.ToArchiveFile().Content,
                                makePathAbsolute: false,
                                entryCreationStamp: entry.LastWriteTime);
                        }
                    }
                }

                // Once embedded files are replayed one by one - we can send the resulting stream to subscriber
                if (EmbeddedContentRead != null)
                {
                    projectImportsCollector!.ProcessResult(
                        streamToEmbed => EmbeddedContentRead(new EmbeddedContentEventArgs(recordKind, streamToEmbed)),
                        error => throw new InvalidDataException(error));
                    projectImportsCollector.DeleteArchive();
                }
            }
            else if (EmbeddedContentRead != null)
            {
                EmbeddedContentRead(new EmbeddedContentEventArgs(
                    recordKind,
                    _binaryReader.BaseStream.Slice(length)));
            }
            else
            {
                SkipBytes(length);
            }
        }

        private void ReadNameValueList()
        {
            if (_fileFormatVersion >= BinaryLogger.ForwardCompatibilityMinimalVersion)
            {
                _readStream.BytesCountAllowedToRead = ReadInt32();
            }

            int count = ReadInt32();

            var list = new (int, int)[count];
            for (int i = 0; i < count; i++)
            {
                int key = ReadInt32();
                int value = ReadInt32();
                list[i] = (key, value);
            }

            nameValueListRecords.Add(list);
        }

        private IDictionary<string, string> GetNameValueList(int id)
        {
            id -= BuildEventArgsWriter.NameValueRecordStartIndex;
            if (id >= 0 && id < nameValueListRecords.Count)
            {
                var list = nameValueListRecords[id];

                // We can't cache these as they would hold on to strings.
                // This reader is designed to not hold onto strings,
                // so that we can fit in a 32-bit process when reading huge binlogs
                var dictionary = ArrayDictionary<string, string>.Create(list.Length);
                for (int i = 0; i < list.Length; i++)
                {
                    string? key = GetStringFromRecord(list[i].keyIndex);
                    // passing null forward would require changes to API surface of existing events
                    // (BuildStartedEventArgs.BuildEnvironment and ProjectStartedEventArgs.GlobalProperties)
                    string value = GetStringFromRecord(list[i].valueIndex) ?? string.Empty;
                    if (key != null)
                    {
                        dictionary.Add(key, value);
                    }
                }

                return dictionary;
            }

            // this should never happen for valid binlogs
            throw new InvalidDataException(
                $"NameValueList record number {_recordNumber} is invalid: index {id} is not within {nameValueListRecords.Count}.");
        }

        private readonly StringReadEventArgs stringReadEventArgs = new StringReadEventArgs(string.Empty);

        private void ReadStringRecord()
        {
            string text = ReadString();
            object storedString = stringStorage.Add(text);
            stringRecords.Add(storedString);
        }

        private BuildEventArgs ReadProjectImportedEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            bool importIgnored = false;

            // the ImportIgnored field was introduced in file format version 3
            if (_fileFormatVersion > 2)
            {
                importIgnored = ReadBoolean();
            }

            var importedProjectFile = ReadOptionalString();
            var unexpandedProject = ReadOptionalString();

            var e = new ProjectImportedEventArgs(
                fields.LineNumber,
                fields.ColumnNumber,
                fields.Message,
                fields.Arguments);

            SetCommonFields(e, fields);

            e.ProjectFile = fields.ProjectFile;

            e.ImportedProjectFile = importedProjectFile;
            e.UnexpandedProject = unexpandedProject;
            e.ImportIgnored = importIgnored;
            return e;
        }

        private BuildEventArgs ReadTargetSkippedEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            var targetFile = ReadOptionalString();
            var targetName = ReadOptionalString();
            var parentTarget = ReadOptionalString();

            string? condition = null;
            string? evaluatedCondition = null;
            bool originallySucceeded = false;
            TargetSkipReason skipReason = TargetSkipReason.None;
            BuildEventContext? originalBuildEventContext = null;
            if (_fileFormatVersion >= 13)
            {
                condition = ReadOptionalString();
                evaluatedCondition = ReadOptionalString();
                originallySucceeded = ReadBoolean();

                // Attempt to infer skip reason from the data we have
                skipReason = condition != null ?
                    TargetSkipReason.ConditionWasFalse // condition expression only stored when false
                    : originallySucceeded ?
                        TargetSkipReason.PreviouslyBuiltSuccessfully
                        : TargetSkipReason.PreviouslyBuiltUnsuccessfully;
            }

            var buildReason = (TargetBuiltReason)ReadInt32();

            if (_fileFormatVersion >= 14)
            {
                skipReason = (TargetSkipReason)ReadInt32();
                originalBuildEventContext = _binaryReader.ReadOptionalBuildEventContext();
            }

            var e = new TargetSkippedEventArgs(
                fields.Message,
                fields.Arguments);

            SetCommonFields(e, fields);

            e.ProjectFile = fields.ProjectFile;
            e.TargetFile = targetFile;
            e.TargetName = targetName;
            e.ParentTarget = parentTarget;
            e.BuildReason = buildReason;
            e.Condition = condition;
            e.EvaluatedCondition = evaluatedCondition;
            e.OriginallySucceeded = originallySucceeded;
            e.SkipReason = skipReason;
            e.OriginalBuildEventContext = originalBuildEventContext;

            return e;
        }

        private BuildEventArgs ReadBuildStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var environment = ReadStringDictionary();

            var e = new BuildStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                environment);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadBuildFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();

            var e = new BuildFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadBuildSubmissionStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();

            IDictionary<string, string>? globalProperties = null;
            globalProperties = ReadStringDictionary() ?? new Dictionary<string, string>();

            var entryProjectsFullPath = ReadStringIEnumerable() ?? [];
            var targetNames = ReadStringIEnumerable() ?? [];
            var flags = (BuildRequestDataFlags)ReadInt32();
            var submissionId = ReadInt32();

            var e = new BuildSubmissionStartedEventArgs(
                (IReadOnlyDictionary<string, string?>)globalProperties,
                entryProjectsFullPath,
                targetNames,
                flags,
                submissionId);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadProjectEvaluationStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            // Null message arg is not expected by the ProjectEvaluationStartedEventArgs
            // Ensuring the non-null value - to avoid a need for public API change
            var projectFile = ReadDeduplicatedString() ?? string.Empty;

            var e = new ProjectEvaluationStartedEventArgs(
                ResourceUtilities.GetResourceString("EvaluationStarted"),
                projectFile)
            {
                ProjectFile = projectFile
            };
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadProjectEvaluationFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            // Null message arg is not expected
            var projectFile = ReadDeduplicatedString() ?? string.Empty;

            var e = new ProjectEvaluationFinishedEventArgs(
                ResourceUtilities.GetResourceString("EvaluationFinished"),
                projectFile)
            {
                ProjectFile = projectFile
            };
            SetCommonFields(e, fields);

            if (_fileFormatVersion >= 12)
            {
                IEnumerable? globalProperties = null;
                // In newer versions, we store the global properties always, as it handles
                //  null and empty within WriteProperties already.
                // This saves a single boolean, but mainly doesn't hide the difference between null and empty
                //  during write->read roundtrip.
                if (_fileFormatVersion >= BinaryLogger.ForwardCompatibilityMinimalVersion ||
                    ReadBoolean())
                {
                    globalProperties = ReadStringDictionary();
                }

                var propertyList = ReadPropertyList();
                var itemList = ReadProjectItems();

                e.GlobalProperties = globalProperties;
                e.Properties = propertyList;
                e.Items = itemList;
            }

            // ProfilerResult was introduced in version 5
            if (_fileFormatVersion > 4)
            {
                var hasProfileData = ReadBoolean();
                if (hasProfileData)
                {
                    var count = ReadInt32();

                    var d = new Dictionary<EvaluationLocation, ProfiledLocation>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var evaluationLocation = ReadEvaluationLocation();
                        var profiledLocation = ReadProfiledLocation();
                        d[evaluationLocation] = profiledLocation;
                    }

                    e.ProfilerResult = new ProfilerResult(d);
                }
            }

            return e;
        }

        private BuildEventArgs ReadProjectStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            BuildEventContext? parentContext = null;
            if (ReadBoolean())
            {
                parentContext = ReadBuildEventContext();
            }

            var projectFile = ReadOptionalString();
            var projectId = ReadInt32();
            var targetNames = ReadDeduplicatedString();
            var toolsVersion = ReadOptionalString();

            IDictionary<string, string>? globalProperties = null;

            if (_fileFormatVersion > 6)
            {
                // See ReadProjectEvaluationFinishedEventArgs for details on why we always store global properties in newer version.
                if (_fileFormatVersion >= BinaryLogger.ForwardCompatibilityMinimalVersion ||
                    ReadBoolean())
                {
                    globalProperties = ReadStringDictionary();
                }
            }

            var propertyList = ReadPropertyList();
            var itemList = ReadProjectItems();

            var e = new ProjectStartedEventArgs(
                projectId,
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                targetNames,
                propertyList,
                itemList,
                parentContext,
                globalProperties,
                toolsVersion);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadProjectFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var projectFile = ReadOptionalString();
            var succeeded = ReadBoolean();

            var e = new ProjectFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTargetStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var targetName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var targetFile = ReadOptionalString();
            var parentTarget = ReadOptionalString();
            // BuildReason was introduced in version 4
            var buildReason = _fileFormatVersion > 3 ? (TargetBuiltReason)ReadInt32() : TargetBuiltReason.None;

            var e = new TargetStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                targetName,
                projectFile,
                targetFile,
                parentTarget,
                buildReason,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTargetFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();
            var projectFile = ReadOptionalString();
            var targetFile = ReadOptionalString();
            var targetName = ReadOptionalString();
            var targetOutputItemList = ReadTaskItemList();

            var e = new TargetFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                targetName,
                projectFile,
                targetFile,
                succeeded,
                fields.Timestamp,
                targetOutputItemList);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTaskStartedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var taskName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var taskFile = ReadOptionalString();
            var taskAssemblyLocation = _fileFormatVersion > 19 ? ReadOptionalString() : null;

            var e = new TaskStartedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                taskFile,
                taskName,
                fields.Timestamp,
                taskAssemblyLocation);
            e.LineNumber = fields.LineNumber;
            e.ColumnNumber = fields.ColumnNumber;
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadTaskFinishedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var succeeded = ReadBoolean();
            var taskName = ReadOptionalString();
            var projectFile = ReadOptionalString();
            var taskFile = ReadOptionalString();

            var e = new TaskFinishedEventArgs(
                fields.Message,
                fields.HelpKeyword,
                projectFile,
                taskFile,
                taskName,
                succeeded,
                fields.Timestamp);
            SetCommonFields(e, fields);
            return e;
        }

        private BuildEventArgs ReadBuildErrorEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            ReadDiagnosticFields(fields);

            BuildEventArgs e;
            if (fields.Extended == null)
            {
                e = new BuildErrorEventArgs(
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile
                };
            }
            else
            {
                e = new ExtendedBuildErrorEventArgs(
                    fields.Extended.ExtendedType,
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                    ExtendedMetadata = fields.Extended.ExtendedMetadataAsDictionary,
                    ExtendedData = fields.Extended.ExtendedData,
                };
            }
            e.BuildEventContext = fields.BuildEventContext;

            return e;
        }

        private BuildEventArgs ReadBuildWarningEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            ReadDiagnosticFields(fields);

            BuildEventArgs e;
            if (fields.Extended == null)
            {
                e = new BuildWarningEventArgs(
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile
                };
            }
            else
            {
                e = new ExtendedBuildWarningEventArgs(
                    fields.Extended.ExtendedType,
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                    ExtendedMetadata = fields.Extended.ExtendedMetadataAsDictionary,
                    ExtendedData = fields.Extended.ExtendedData,
                };
            }
            e.BuildEventContext = fields.BuildEventContext;

            return e;
        }

        private BuildEventArgs ReadBuildMessageEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            BuildEventArgs e;
            if (fields.Extended == null)
            {
                e = new BuildMessageEventArgs(
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Importance,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                };
            }
            else
            {
                e = new ExtendedBuildMessageEventArgs(
                    fields.Extended?.ExtendedType ?? string.Empty,
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Importance,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                    ExtendedMetadata = fields.Extended?.ExtendedMetadataAsDictionary,
                    ExtendedData = fields.Extended?.ExtendedData,
                };
            }

            e.BuildEventContext = fields.BuildEventContext;

            return e;
        }

        private BuildEventArgs ReadTaskCommandLineEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);
            var commandLine = ReadOptionalString();
            var taskName = ReadOptionalString();

            var e = new TaskCommandLineEventArgs(
                commandLine,
                taskName,
                fields.Importance,
                fields.Timestamp);
            e.BuildEventContext = fields.BuildEventContext;
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadTaskParameterEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            var kind = (TaskParameterMessageKind)ReadInt32();
            var itemType = ReadDeduplicatedString();
            var items = ReadTaskItemList() as IList;
            var (parameterName, propertyName) = _fileFormatVersion >= 21
                ? (ReadDeduplicatedString(), ReadDeduplicatedString())
                : (null, null);

            var e = ItemGroupLoggingHelper.CreateTaskParameterEventArgs(
                fields.BuildEventContext,
                kind,
                parameterName,
                propertyName,
                itemType,
                items,
                logItemMetadata: true,
                fields.Timestamp,
                fields.LineNumber,
                fields.ColumnNumber);
            e.ProjectFile = fields.ProjectFile;
            return e;
        }

        private BuildEventArgs ReadCriticalBuildMessageEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            BuildEventArgs e;
            if (fields.Extended == null)
            {
                e = new CriticalBuildMessageEventArgs(
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                };
            }
            else
            {
                e = new ExtendedCriticalBuildMessageEventArgs(
                    fields.Extended?.ExtendedType ?? string.Empty,
                    fields.Subcategory,
                    fields.Code,
                    fields.File,
                    fields.LineNumber,
                    fields.ColumnNumber,
                    fields.EndLineNumber,
                    fields.EndColumnNumber,
                    fields.Message,
                    fields.HelpKeyword,
                    fields.SenderName,
                    fields.Timestamp,
                    fields.Arguments)
                {
                    ProjectFile = fields.ProjectFile,
                    ExtendedMetadata = fields.Extended?.ExtendedMetadataAsDictionary,
                    ExtendedData = fields.Extended?.ExtendedData,
                };
            }
            e.BuildEventContext = fields.BuildEventContext;
            return e;
        }

        private BuildEventArgs ReadEnvironmentVariableReadEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            string? environmentVariableName = ReadDeduplicatedString();
            int line = 0;
            int column = 0;
            string? fileName = null;
            if (_fileFormatVersion >= 22)
            {
                line = ReadInt32();
                column = ReadInt32();
                fileName = ReadDeduplicatedString();
            }

            BuildEventArgs e = new EnvironmentVariableReadEventArgs(
                    environmentVariableName ?? string.Empty,
                    fields.Message,
                    fileName ?? string.Empty,
                    line,
                    column);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadResponseFileUsedEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var responseFilePath = ReadDeduplicatedString();
            var e = new ResponseFileUsedEventArgs(responseFilePath);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadPropertyReassignmentEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            string? propertyName = ReadDeduplicatedString();
            string? previousValue = ReadDeduplicatedString();
            string? newValue = ReadDeduplicatedString();
            string? location = ReadDeduplicatedString();

            var e = new PropertyReassignmentEventArgs(
                propertyName,
                previousValue,
                newValue,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Importance);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadUninitializedPropertyReadEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);
            string? propertyName = ReadDeduplicatedString();

            var e = new UninitializedPropertyReadEventArgs(
                propertyName,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Importance);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadPropertyInitialValueSetEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: true);

            string? propertyName = ReadDeduplicatedString();
            string? propertyValue = ReadDeduplicatedString();
            string? propertySource = ReadDeduplicatedString();

            var e = new PropertyInitialValueSetEventArgs(
                propertyName,
                propertyValue,
                propertySource,
                fields.File,
                fields.LineNumber,
                fields.ColumnNumber,
                fields.Message,
                fields.HelpKeyword,
                fields.SenderName,
                fields.Importance);

            SetCommonFields(e, fields);

            return e;
        }

        private AssemblyLoadBuildEventArgs ReadAssemblyLoadEventArgs()
        {
            var fields = ReadBuildEventArgsFields(readImportance: false);

            AssemblyLoadingContext context = (AssemblyLoadingContext)ReadInt32();
            string? loadingInitiator = ReadDeduplicatedString();
            string? assemblyName = ReadDeduplicatedString();
            string? assemblyPath = ReadDeduplicatedString();
            Guid mvid = ReadGuid();
            string? appDomainName = ReadDeduplicatedString();

            var e = new AssemblyLoadBuildEventArgs(
                context,
                loadingInitiator,
                assemblyName,
                assemblyPath,
                mvid,
                appDomainName);
            SetCommonFields(e, fields);
            e.ProjectFile = fields.ProjectFile;

            return e;
        }

        private BuildEventArgs ReadBuildCheckTracingEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var rawTracingData = ReadStringDictionary() ?? new Dictionary<string, string>();

            var e = new BuildCheckTracingEventArgs(rawTracingData.ToDictionary(
                kvp => kvp.Key,
                kvp => TimeSpan.FromTicks(long.Parse(kvp.Value))));
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadBuildCheckAcquisitionEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var acquisitionPath = ReadString();
            var projectPath = ReadString();
            var e = new BuildCheckAcquisitionEventArgs(acquisitionPath, projectPath);
            SetCommonFields(e, fields);

            return e;
        }

        private BuildEventArgs ReadBuildCanceledEventArgs()
        {
            var fields = ReadBuildEventArgsFields();
            var e = new BuildCanceledEventArgs(fields.Message);
            SetCommonFields(e, fields);

            return e;
        }

        /// <summary>
        /// For errors and warnings these 8 fields are written out explicitly
        /// (their presence is not marked as a bit in the flags). So we have to
        /// read explicitly.
        /// </summary>
        /// <param name="fields"></param>
        private void ReadDiagnosticFields(BuildEventArgsFields fields)
        {
            fields.Subcategory = ReadOptionalString();
            fields.Code = ReadOptionalString();
            fields.File = ReadOptionalString();
            fields.ProjectFile = ReadOptionalString();
            fields.LineNumber = ReadInt32();
            fields.ColumnNumber = ReadInt32();
            fields.EndLineNumber = ReadInt32();
            fields.EndColumnNumber = ReadInt32();
        }

        private ExtendedDataFields? ReadExtendedDataFields()
        {
            string extendedType = ReadOptionalString()!;
            IDictionary<string, string?>? extendedMetadata = ReadStringDictionary()!;
            string? extendedData = ReadOptionalString();

            return new ExtendedDataFields(extendedType, extendedMetadata, extendedData);
        }

        private BuildEventArgsFields ReadBuildEventArgsFields(bool readImportance = false)
        {
            BuildEventArgsFieldFlags flags = (BuildEventArgsFieldFlags)ReadInt32();
            var result = new BuildEventArgsFields();
            result.Flags = flags;

            // Base Fields

            if ((flags & BuildEventArgsFieldFlags.Message) != 0)
            {
                result.Message = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
            {
                result.BuildEventContext = ReadBuildEventContext();
            }

            if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                result.ThreadId = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.HelpKeyword) != 0)
            {
                result.HelpKeyword = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                result.SenderName = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                result.Timestamp = ReadDateTime();
            }

            if ((flags & BuildEventArgsFieldFlags.Extended) != 0)
            {
                result.Extended = ReadExtendedDataFields();
            }

            // End of BaseFields

            if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
            {
                result.Subcategory = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.Code) != 0)
            {
                result.Code = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.File) != 0)
            {
                result.File = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
            {
                result.ProjectFile = ReadDeduplicatedString();
            }

            if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
            {
                result.LineNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
            {
                result.ColumnNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
            {
                result.EndLineNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
            {
                result.EndColumnNumber = ReadInt32();
            }

            if ((flags & BuildEventArgsFieldFlags.Arguments) != 0)
            {
                int count = ReadInt32();
                object?[] arguments = new object[count];
                for (int i = 0; i < count; i++)
                {
                    arguments[i] = ReadDeduplicatedString();
                }

                result.Arguments = arguments;
            }

            if ((_fileFormatVersion < 13 && readImportance) || (_fileFormatVersion >= 13 && (flags & BuildEventArgsFieldFlags.Importance) != 0))
            {
                result.Importance = (MessageImportance)ReadInt32();
            }

            return result;
        }

        private void SetCommonFields(BuildEventArgs buildEventArgs, BuildEventArgsFields fields)
        {
            buildEventArgs.BuildEventContext = fields.BuildEventContext;

            if ((fields.Flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                buildEventArgsFieldThreadId?.SetValue(buildEventArgs, fields.ThreadId);
            }

            if ((fields.Flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                buildEventArgsFieldSenderName?.SetValue(buildEventArgs, fields.SenderName);
            }

            if ((fields.Flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                buildEventArgs.RawTimestamp = fields.Timestamp;
            }
        }

        private IList<DictionaryEntry>? ReadPropertyList()
        {
            IDictionary<string, string>? properties = ReadStringDictionary();
            if (properties == null || properties.Count == 0)
            {
                return null;
            }

            int count = properties.Count;
            var list = new DictionaryEntry[count];
            int i = 0;
            foreach (var property in properties)
            {
                list[i] = new DictionaryEntry(property.Key, property.Value);
                i++;
            }

            return list;
        }

        private BuildEventContext ReadBuildEventContext()
        {
            int nodeId = ReadInt32();
            int projectContextId = ReadInt32();
            int targetId = ReadInt32();
            int taskId = ReadInt32();
            int submissionId = ReadInt32();
            int projectInstanceId = ReadInt32();

            // evaluationId was introduced in format version 2
            int evaluationId = BuildEventContext.InvalidEvaluationId;
            if (_fileFormatVersion > 1)
            {
                evaluationId = ReadInt32();
            }

            var result = new BuildEventContext(
                submissionId,
                nodeId,
                evaluationId,
                projectInstanceId,
                projectContextId,
                targetId,
                taskId);
            return result;
        }

        private IDictionary<string, string>? ReadStringDictionary()
        {
            if (_fileFormatVersion < 10)
            {
                return ReadLegacyStringDictionary();
            }

            int index = ReadInt32();
            if (index == 0)
            {
                return null;
            }

            var record = GetNameValueList(index);
            return record;
        }

        private IDictionary<string, string>? ReadLegacyStringDictionary()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, string>(count);

            for (int i = 0; i < count; i++)
            {
                string key = ReadString();
                string value = ReadString();
                result[key] = value;
            }

            return result;
        }

        private ITaskItem ReadTaskItem()
        {
            string? itemSpec = ReadDeduplicatedString();
            var metadata = ReadStringDictionary();

            var taskItem = new TaskItemData(itemSpec, metadata);
            return taskItem;
        }

        private IList<DictionaryEntry>? ReadProjectItems()
        {
            IList<DictionaryEntry>? list;

            // starting with format version 10 project items are grouped by name
            // so we only have to write the name once, and then the count of items
            // with that name. When reading a legacy binlog we need to read the
            // old style flat list where the name is duplicated for each item.
            if (_fileFormatVersion < 10)
            {
                int count = ReadInt32();
                if (count == 0)
                {
                    return null;
                }

                list = new DictionaryEntry[count];
                for (int i = 0; i < count; i++)
                {
                    string itemName = ReadString();
                    ITaskItem item = ReadTaskItem();
                    list[i] = new DictionaryEntry(itemName, item);
                }
            }
            else if (_fileFormatVersion < 12)
            {
                int count = ReadInt32();
                if (count == 0)
                {
                    return null;
                }

                list = new List<DictionaryEntry>();
                for (int i = 0; i < count; i++)
                {
                    string itemType = ReadDeduplicatedString()!;
                    var items = ReadTaskItemList();
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            list.Add(new DictionaryEntry(itemType, item));
                        }
                    }
                }

                if (list.Count == 0)
                {
                    list = null;
                }
            }
            else
            {
                list = new List<DictionaryEntry>();

                while (true)
                {
                    string itemType = ReadDeduplicatedString()!;
                    if (string.IsNullOrEmpty(itemType))
                    {
                        break;
                    }

                    var items = ReadTaskItemList();
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            list.Add(new DictionaryEntry(itemType, item));
                        }
                    }
                }

                if (list.Count == 0)
                {
                    list = null;
                }
            }

            return list;
        }

        private IList<ITaskItem>? ReadTaskItemList()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            var list = new ITaskItem[count];

            for (int i = 0; i < count; i++)
            {
                ITaskItem item = ReadTaskItem();
                list[i] = item;
            }

            return list;
        }

        private IEnumerable<string>? ReadStringIEnumerable()
        {
            int count = ReadInt32();
            if (count == 0)
            {
                return null;
            }

            var list = new string[count];

            for (int i = 0; i < count; i++)
            {
                string? item = ReadDeduplicatedString();
                if (item is not null)
                {
                    list[i] = item;
                }
            }

            return list;
        }

        private string ReadString()
        {
            string text = _binaryReader.ReadString();
            if (this.StringReadDone != null)
            {
                stringReadEventArgs.Reuse(text);
                StringReadDone(stringReadEventArgs);
                text = stringReadEventArgs.StringToBeUsed;
            }
            return text;
        }

        private string? ReadOptionalString()
        {
            if (_fileFormatVersion < 10)
            {
                if (ReadBoolean())
                {
                    return ReadString();
                }
                else
                {
                    return null;
                }
            }

            return ReadDeduplicatedString();
        }

        private string? ReadDeduplicatedString()
        {
            if (_fileFormatVersion < 10)
            {
                return ReadString();
            }

            int index = ReadInt32();
            return GetStringFromRecord(index);
        }

        private string? GetStringFromRecord(int index)
        {
            if (index == 0)
            {
                return null;
            }
            else if (index == 1)
            {
                return string.Empty;
            }

            // we reserve numbers 2-9 for future use.
            // the writer assigns 10 as the index of the first string
            index -= BuildEventArgsWriter.StringStartIndex;
            if (index >= 0 && index < this.stringRecords.Count)
            {
                object storedString = stringRecords[index];
                string result = stringStorage.Get(storedString);
                return result;
            }

            // this should never happen for valid binlogs
            throw new InvalidDataException(
                $"String record number {_recordNumber} is invalid: string index {index} is not within {stringRecords.Count}.");
        }

        private int ReadInt32()
        {
            // on some platforms (net5) this method was added to BinaryReader
            // but it's not available on others. Call our own extension method
            // explicitly to avoid ambiguity.
            return BinaryReaderExtensions.Read7BitEncodedInt(_binaryReader);
        }

        private long ReadInt64()
        {
            return _binaryReader.ReadInt64();
        }

        private bool ReadBoolean()
        {
            return _binaryReader.ReadBoolean();
        }

        private Guid ReadGuid()
        {
            return new Guid(_binaryReader.ReadBytes(16 /*sizeof(Guid) - to avoid unsafe context, Guid will never change in size */));
        }

        private DateTime ReadDateTime()
        {
            return new DateTime(_binaryReader.ReadInt64(), (DateTimeKind)ReadInt32());
        }

        private TimeSpan ReadTimeSpan()
        {
            return new TimeSpan(_binaryReader.ReadInt64());
        }

        private ProfiledLocation ReadProfiledLocation()
        {
            var numberOfHits = ReadInt32();
            var exclusiveTime = ReadTimeSpan();
            var inclusiveTime = ReadTimeSpan();

            return new ProfiledLocation(inclusiveTime, exclusiveTime, numberOfHits);
        }

        private EvaluationLocation ReadEvaluationLocation()
        {
            var elementName = ReadOptionalString();
            var description = ReadOptionalString();
            var evaluationDescription = ReadOptionalString();
            var file = ReadOptionalString();
            var kind = (EvaluationLocationKind)ReadInt32();
            var evaluationPass = (EvaluationPass)ReadInt32();

            int? line = null;
            var hasLine = ReadBoolean();
            if (hasLine)
            {
                line = ReadInt32();
            }

            // Id and parent Id were introduced in version 6
            if (_fileFormatVersion > 5)
            {
                var id = ReadInt64();
                long? parentId = null;
                var hasParent = ReadBoolean();
                if (hasParent)
                {
                    parentId = ReadInt64();
                }

                return new EvaluationLocation(id, parentId, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
            }

            return new EvaluationLocation(0, null, evaluationPass, evaluationDescription, file, line, elementName, description, kind);
        }

        /// <summary>
        /// Locates the string in the page file.
        /// </summary>
        internal class StringPosition
        {
            /// <summary>
            /// Offset in the file.
            /// </summary>
            public long FilePosition;

            /// <summary>
            /// The length of the string in chars (not bytes).
            /// </summary>
            public int StringLength;
        }

        /// <summary>
        /// Stores large strings in a temp file on disk, to avoid keeping all strings in memory.
        /// Only creates a file for 32-bit MSBuild.exe, just returns the string directly on 64-bit.
        /// </summary>
        internal class StringStorage : IDisposable
        {
            private readonly string? filePath;
            private FileStream? stream;
            private StreamWriter? streamWriter;
            private readonly StreamReader? streamReader;
            private readonly StringBuilder? stringBuilder;

            public const int StringSizeThreshold = 1024;

            public StringStorage()
            {
                if (!Environment.Is64BitProcess)
                {
                    filePath = Path.GetTempFileName();
                    var utf8noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    stream = new FileStream(
                        filePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096, // 4096 seems to have the best performance on SSD
                        FileOptions.RandomAccess | FileOptions.DeleteOnClose);

                    // 65536 has no particular significance, and maybe could be tuned
                    // but 65536 performs well enough and isn't a lot of memory for a singleton
                    streamWriter = new StreamWriter(stream, utf8noBom, 65536);
                    streamWriter.AutoFlush = true;
                    streamReader = new StreamReader(stream, utf8noBom);
                    stringBuilder = new StringBuilder();
                }
            }

            private long totalAllocatedShortStrings = 0;

            public object Add(string text)
            {
                if (filePath == null)
                {
                    // on 64-bit, we have as much memory as we want
                    // so no need to write to the file at all
                    return text;
                }

                // Tradeoff between not crashing with OOM on large binlogs and
                // keeping the playback of smaller binlogs relatively fast.
                // It is slow to store all small strings in the file and constantly
                // seek to retrieve them. Instead we'll keep storing small strings
                // in memory until we allocate 2 GB. After that, all strings go to
                // the file.
                // Win-win: small binlog playback is fast and large binlog playback
                // doesn't OOM.
                if (text.Length <= StringSizeThreshold && totalAllocatedShortStrings < 1_000_000_000)
                {
                    totalAllocatedShortStrings += text.Length;
                    return text;
                }

                var stringPosition = new StringPosition();

                stringPosition.FilePosition = stream!.Position;

                streamWriter!.Write(text);

                stringPosition.StringLength = text.Length;
                return stringPosition;
            }

            public string Get(object storedString)
            {
                if (storedString is string text)
                {
                    return text;
                }

                var position = (StringPosition)storedString;

                stream!.Position = position.FilePosition;
                stringBuilder!.Length = position.StringLength;
                for (int i = 0; i < position.StringLength; i++)
                {
                    char ch = (char)streamReader!.Read();
                    stringBuilder[i] = ch;
                }

                stream.Position = stream.Length;
                streamReader!.DiscardBufferedData();

                string result = stringBuilder.ToString();
                stringBuilder.Clear();
                return result;
            }

            public void Dispose()
            {
                try
                {
                    if (streamWriter != null)
                    {
                        streamWriter.Dispose();
                        streamWriter = null;
                    }

                    if (stream != null)
                    {
                        stream.Dispose();
                        stream = null;
                    }
                }
                catch
                {
                    // The StringStorage class is not crucial for other functionality and if
                    // there are exceptions when closing the temp file, it's too late to do anything about it.
                    // Since we don't want to disrupt anything and the file is in the TEMP directory, it will
                    // get cleaned up at some point anyway.
                }
            }
        }
    }
}
