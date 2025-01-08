// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// Contains the result items for a single target as well as the overall result code.
    /// </summary>
    public class TargetResult : ITargetResult, ITranslatable
    {
        /// <summary>
        /// The result for this target.
        /// </summary>
        private WorkUnitResult _result;

        /// <summary>
        /// Flag indicating whether to consider this target failure as having caused a build failure.
        /// </summary>
        private bool _targetFailureDoesntCauseBuildFailure;

        /// <summary>
        /// Flag indicating whether at least one target which has run after us (transitively via AfterTargets) failed.
        /// </summary>
        private bool _afterTargetsHaveFailed;

        /// <summary>
        /// The store of items in this result.
        /// </summary>
        private TaskItem[] _items;

        /// <summary>
        /// The context under which these results have been cached.
        /// </summary>
        private CacheInfo _cacheInfo;

        /// <summary>
        /// The (possibly null) <see cref="BuildEventContext"/> from the original target build
        /// </summary>
        private BuildEventContext _originalBuildEventContext;

        /// <summary>
        /// Initializes the results with specified items and result.
        /// </summary>
        /// <param name="items">The items produced by the target.</param>
        /// <param name="result">The overall result for the target.</param>
        /// <param name="originalBuildEventContext">The original build event context from when the target was first built, if available.
        /// Non-null when creating a <see cref="TargetResult"/> after building the target initially (or skipping due to false condition).
        /// Null when the <see cref="TargetResult"/> is being created in other scenarios:
        ///  * Target that never ran because a dependency had an error
        ///  * in <see cref="ITargetBuilderCallback.LegacyCallTarget"/> when Cancellation was requested
        ///  * in ProjectCache.CacheResult.ConstructBuildResult
        /// </param>
        internal TargetResult(TaskItem[] items, WorkUnitResult result, BuildEventContext originalBuildEventContext = null)
        {
            ErrorUtilities.VerifyThrowArgumentNull(items);
            ErrorUtilities.VerifyThrowArgumentNull(result);
            _items = items;
            _result = result;
            _originalBuildEventContext = originalBuildEventContext;
        }

        /// <summary>
        /// Private constructor for deserialization
        /// </summary>
        private TargetResult(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);
        }

        /// <summary>
        /// Returns the exception which aborted this target, if any.
        /// </summary>
        /// <value>The exception which aborted this target, if any.</value>
        public Exception Exception
        {
            [DebuggerStepThrough]
            get => _result.Exception;
        }

        /// <summary>
        /// Returns the items produced by the target.
        /// These are ITaskItem's, so they have no item-type.
        /// </summary>
        /// <value>The items produced by the target.</value>
        public ITaskItem[] Items
        {
            [DebuggerStepThrough]
            get
            {
                lock (_result)
                {
                    RetrieveItemsFromCache();

                    return _items;
                }
            }
        }

        /// <summary>
        /// Returns the result code for the target.
        /// </summary>
        /// <value>The result code for the target.</value>
        public TargetResultCode ResultCode
        {
            [DebuggerStepThrough]
            get
            {
                switch (_result.ResultCode)
                {
                    case WorkUnitResultCode.Canceled:
                    case WorkUnitResultCode.Failed:
                        return TargetResultCode.Failure;

                    case WorkUnitResultCode.Skipped:
                        return TargetResultCode.Skipped;

                    case WorkUnitResultCode.Success:
                        return TargetResultCode.Success;

                    default:
                        return TargetResultCode.Skipped;
                }
            }
        }

        public string TargetResultCodeToString()
        {
            switch (ResultCode)
            {
                case TargetResultCode.Failure:
                    return nameof(TargetResultCode.Failure);
                case TargetResultCode.Skipped:
                    return nameof(TargetResultCode.Skipped);
                case TargetResultCode.Success:
                    return nameof(TargetResultCode.Success);
                default:
                    Debug.Fail($"Unknown enum value: {ResultCode}");
                    return ResultCode.ToString();
            }
        }

        /// <summary>
        /// Returns the internal result for the target.
        /// </summary>
        internal WorkUnitResult WorkUnitResult
        {
            [DebuggerStepThrough]
            get => _result;
        }

        /// <summary>
        /// The (possibly null) <see cref="BuildEventContext"/> from the original target build
        /// </summary>
        internal BuildEventContext OriginalBuildEventContext => _originalBuildEventContext;

        /// <summary>
        /// Sets or gets a flag indicating whether or not a failure results should cause the build to fail.
        /// </summary>
        internal bool TargetFailureDoesntCauseBuildFailure
        {
            [DebuggerStepThrough]
            get => _targetFailureDoesntCauseBuildFailure;

            [DebuggerStepThrough]
            set => _targetFailureDoesntCauseBuildFailure = value;
        }

        /// <summary>
        /// Sets or gets a flag indicating whether at least one target which has run after us (transitively via AfterTargets) failed.
        /// </summary>
        internal bool AfterTargetsHaveFailed
        {
            [DebuggerStepThrough]
            get => _afterTargetsHaveFailed;

            [DebuggerStepThrough]
            set => _afterTargetsHaveFailed = value;
        }

        #region INodePacketTranslatable Members

        /// <summary>
        /// Reads or writes the packet to the serializer.
        /// </summary>
        void ITranslatable.Translate(ITranslator translator)
        {
            if (translator.Mode == TranslationDirection.WriteToStream)
            {
                lock (_result)
                {
                    // Should we have cached these items but now want to send them to another node, we need to
                    // ensure they are loaded before doing so.
                    RetrieveItemsFromCache();
                    InternalTranslate(translator);
                }
            }
            else
            {
                InternalTranslate(translator);
            }
        }

        /// <summary>
        /// Factory for serialization.
        /// </summary>
        internal static TargetResult FactoryForDeserialization(ITranslator translator)
        {
            return new TargetResult(translator);
        }

        #endregion

        /// <summary>
        /// Gets the name of the cache file for this configuration.
        /// </summary>
        internal static string GetCacheFile(int configId, string targetToCache)
        {
            string filename = Path.Combine(FileUtilities.GetCacheDirectory(), String.Format(CultureInfo.InvariantCulture, Path.Combine("Results{0}", "{1}.cache"), configId, targetToCache));
            return filename;
        }

        /// <summary>
        /// Gets the name of the cache file for this configuration.
        /// </summary>
        internal static string GetCacheDirectory(int configId, string targetToCache)
        {
            string filename = GetCacheFile(configId, targetToCache);
            string directoryName = Path.GetDirectoryName(filename);
            return directoryName;
        }

        /// <summary>
        /// Cache the items.
        /// </summary>
        internal void CacheItems(int configId, string targetName)
        {
            lock (_result)
            {
                if (_items == null)
                {
                    // Already cached.
                    return;
                }

                if (_items.Length == 0)
                {
                    // Nothing to cache.
                    return;
                }

                string cacheFile = GetCacheFile(configId, targetName);
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

                // If the file doesn't already exists, then we haven't cached this once before. We need to cache it again since it could have changed.
                if (!FileSystems.Default.FileExists(cacheFile))
                {
                    using Stream stream = File.Create(cacheFile);
                    using ITranslator translator = GetResultsCacheTranslator(TranslationDirection.WriteToStream, stream);

                    // If the translator is null, it means these results were cached once before.  Since target results are immutable once they
                    // have been created, there is no point in writing them again.
                    if (translator != null)
                    {
                        TranslateItems(translator);
                        _items = null;
                        _cacheInfo = new CacheInfo(configId, targetName);
                    }
                }
            }
        }

        /// <summary>
        /// Performs the actual translation
        /// </summary>
        private void InternalTranslate(ITranslator translator)
        {
            translator.Translate(ref _result, WorkUnitResult.FactoryForDeserialization);
            translator.Translate(ref _targetFailureDoesntCauseBuildFailure);
            translator.Translate(ref _afterTargetsHaveFailed);
            translator.TranslateOptionalBuildEventContext(ref _originalBuildEventContext);
            TranslateItems(translator);
        }

        /// <summary>
        /// Retrieve the items from the cache.
        /// </summary>
        private void RetrieveItemsFromCache()
        {
            lock (_result)
            {
                if (_items == null)
                {
                    string cacheFile = GetCacheFile(_cacheInfo.ConfigId, _cacheInfo.TargetName);
                    using Stream stream = File.OpenRead(cacheFile);
                    using ITranslator translator = GetResultsCacheTranslator(TranslationDirection.ReadFromStream, stream);

                    TranslateItems(translator);
                    _cacheInfo = new CacheInfo();
                }
            }
        }

        private void TranslateItems(ITranslator translator)
        {
            var itemsCount = _items?.Length ?? 0;
            translator.Translate(ref itemsCount);

            if (translator.Mode == TranslationDirection.WriteToStream)
            {
                // We will just calculate a very rough starting buffer size for the memory stream based on the number of items and a
                // rough guess for an average number of bytes needed to store them.  This doesn't have to be accurate, just
                // big enough to avoid unnecessary buffer reallocations in most cases.
                var defaultBufferCapacity = _items.Length * 128;

                using var itemsStream = new MemoryStream(defaultBufferCapacity);
                var itemTranslator = BinaryTranslator.GetWriteTranslator(itemsStream);

                // When creating the interner, we use the number of items as the initial size of the collections since the
                // number of strings will be of the order of the number of items in the collection.  This assumes basically
                // one unique string per item (frequently a path related to the item) with most of the rest of the metadata
                // being the same (and thus interning.)  This is a hueristic meant to get us in the ballpark to avoid
                // too many reallocations when growing the collections.
                var interner = new LookasideStringInterner(StringComparer.Ordinal, _items.Length);
                foreach (TaskItem t in _items)
                {
                    t.TranslateWithInterning(itemTranslator, interner);
                }

                interner.Translate(translator);
                var buffer = itemsStream.GetBuffer();
                var bufferSize = (int)itemsStream.Length;
                translator.Translate(ref buffer, ref bufferSize);
            }
            else
            {
                var interner = new LookasideStringInterner(translator);

                byte[] buffer = null;
                translator.Translate(ref buffer);
                ErrorUtilities.VerifyThrow(buffer != null, "Unexpected null items buffer during translation.");

                using MemoryStream itemsStream = new MemoryStream(buffer, 0, buffer.Length, writable: false, publiclyVisible: true);
                using var itemTranslator = BinaryTranslator.GetReadTranslator(itemsStream, InterningBinaryReader.PoolingBuffer);
                _items = new TaskItem[itemsCount];
                for (int i = 0; i < _items.Length; i++)
                {
                    _items[i] = TaskItem.FactoryForDeserialization(itemTranslator, interner);
                }
            }
        }

        /// <summary>
        /// Gets the translator for this configuration.
        /// </summary>
        private static ITranslator GetResultsCacheTranslator(TranslationDirection direction, Stream stream) =>
            direction == TranslationDirection.WriteToStream
                    ? BinaryTranslator.GetWriteTranslator(stream)
                    : BinaryTranslator.GetReadTranslator(stream, InterningBinaryReader.PoolingBuffer);

        /// <summary>
        /// Information about where the cache for the items in this result are stored.
        /// </summary>
        private struct CacheInfo
        {
            /// <summary>
            /// Constructor.
            /// </summary>
            public CacheInfo(int configId, string targetName)
            {
                ConfigId = configId;
                TargetName = targetName;
            }

            /// <summary>
            /// Retrieves the configuration id.
            /// </summary>
            public int ConfigId { get; }

            /// <summary>
            /// Retrieves the target name.
            /// </summary>
            public string TargetName { get; }
        }
    }
}
