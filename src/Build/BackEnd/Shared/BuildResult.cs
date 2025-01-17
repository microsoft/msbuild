// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Framework;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// Overall results for targets and requests
    /// </summary>
    public enum BuildResultCode
    {
        /// <summary>
        /// The target or request was a complete success.
        /// </summary>
        Success,

        /// <summary>
        /// The target or request failed in some way.
        /// </summary>
        Failure
    }

    /// <summary>
    /// Contains the current results for all of the targets which have produced results for a particular configuration.
    /// </summary>
    /// When modifying serialization/deserialization, bump the version and support previous versions in order to keep <see cref="ResultsCache"/> backwards compatible.
    public class BuildResult : BuildResultBase, INodePacket, IBuildResults
    {
        /// <summary>
        /// The submission with which this result is associated.
        /// </summary>
        private int _submissionId;

        /// <summary>
        /// The configuration ID with which this result is associated.
        /// </summary>
        private int _configurationId;

        /// <summary>
        /// The global build request ID for which these results are intended.
        /// </summary>
        private int _globalRequestId;

        /// <summary>
        /// The global build request ID which issued the request leading to this result.
        /// </summary>
        private int _parentGlobalRequestId;

        /// <summary>
        /// The build request ID on the originating node.
        /// </summary>
        private int _nodeRequestId;

        /// <summary>
        /// The first build request to generate results for a configuration will set this so that future
        /// requests may be properly satisfied from the cache.
        /// </summary>
        private List<string>? _initialTargets;

        /// <summary>
        /// The first build request to generate results for a configuration will set this so that future
        /// requests may be properly satisfied from the cache.
        /// </summary>
        private List<string>? _defaultTargets;

        /// <summary>
        /// The set of results for each target.
        /// </summary>
        private ConcurrentDictionary<string, TargetResult> _resultsByTarget;

        /// <summary>
        /// Version of the build result.
        /// </summary>
        /// <remarks>
        /// Allows to serialize and deserialize different versions of the build result.
        /// </remarks>
        private int _version = Traits.Instance.EscapeHatches.DoNotVersionBuildResult ? 0 : 1;

        /// <summary>
        /// The request caused a circular dependency in scheduling.
        /// </summary>
        private bool _circularDependency;

        /// <summary>
        /// The exception generated while this request was running, if any.
        /// Note that this can be set if the request itself fails, or if it receives
        /// an exception from a target or task.
        /// </summary>
        private Exception? _requestException;

        /// <summary>
        /// The overall result calculated in the constructor.
        /// </summary>
        private bool _baseOverallResult = true;

        /// <summary>
        /// Snapshot of the environment from the configuration this results comes from.
        /// This should only be populated when the configuration for this result is moved between nodes.
        /// </summary>
        private Dictionary<string, string>? _savedEnvironmentVariables;

        /// <summary>
        /// When this key is in the dictionary <see cref="_savedEnvironmentVariables"/>, serialize the build result version.
        /// </summary>
        private const string SpecialKeyForVersion = "=MSBUILDFEATUREBUILDRESULTHASVERSION=";

        /// <summary>
        /// Set of additional keys tat might be added to the dictionary <see cref="_savedEnvironmentVariables"/>.
        /// </summary>
        private static readonly HashSet<string> s_additionalEntriesKeys = new HashSet<string> { SpecialKeyForVersion };

        /// <summary>
        /// Snapshot of the current directory from the configuration this result comes from.
        /// This should only be populated when the configuration for this result is moved between nodes.
        /// </summary>
        private string? _savedCurrentDirectory;

        /// <summary>
        /// <see cref="ProjectInstance"/> state after the build. This is only provided if <see cref="BuildRequest.BuildRequestDataFlags"/>
        /// includes <see cref="BuildRequestDataFlags.ProvideProjectStateAfterBuild"/> or
        /// <see cref="BuildRequestDataFlags.ProvideSubsetOfStateAfterBuild"/> for the build request which this object is a result of,
        /// and will be <c>null</c> otherwise. Where available, it may be a non buildable-dummy object, and should only
        /// be used to retrieve <see cref="ProjectInstance.Properties"/>, <see cref="ProjectInstance.GlobalProperties"/> and
        /// <see cref="ProjectInstance.Items"/> from it. No other operation is guaranteed to be supported.
        /// </summary>
        private ProjectInstance? _projectStateAfterBuild;

        /// <summary>
        /// The flags provide additional control over the build results and may affect the cached value.
        /// </summary>
        /// <remarks>
        /// Is optional, the field is expected to be present starting <see cref="_version"/> 1.
        /// </remarks>
        private BuildRequestDataFlags _buildRequestDataFlags;

        private string? _schedulerInducedError;

        private HashSet<string>? _projectTargets;

        /// <summary>
        /// Constructor for serialization.
        /// </summary>
        public BuildResult()
        {
            _resultsByTarget = CreateTargetResultDictionary(1);
        }

        /// <summary>
        /// Constructor creates an empty build result
        /// </summary>
        /// <param name="request">The build request to which these results should be associated.</param>
        internal BuildResult(BuildRequest request)
            : this(request, null)
        {
        }

        /// <summary>
        /// Constructs a build result with an exception
        /// </summary>
        /// <param name="request">The build request to which these results should be associated.</param>
        /// <param name="exception">The exception, if any.</param>
        internal BuildResult(BuildRequest request, Exception? exception)
            : this(request, null, exception)
        {
        }

        /// <summary>
        /// Constructor creates a build result indicating a circular dependency was created.
        /// </summary>
        /// <param name="request">The build request to which these results should be associated.</param>
        /// <param name="circularDependency">Set to true if a circular dependency was detected.</param>
        internal BuildResult(BuildRequest request, bool circularDependency)
            : this(request, null)
        {
            _circularDependency = circularDependency;
        }

        /// <summary>
        /// Constructs a new build result based on existing results, but filtered by a specified set of target names
        /// </summary>
        /// <param name="existingResults">The existing results.</param>
        /// <param name="targetNames">The target names whose results we will take from the existing results, if they exist.</param>
        internal BuildResult(BuildResult existingResults, string[] targetNames)
        {
            _submissionId = existingResults._submissionId;
            _configurationId = existingResults._configurationId;
            _globalRequestId = existingResults._globalRequestId;
            _parentGlobalRequestId = existingResults._parentGlobalRequestId;
            _nodeRequestId = existingResults._nodeRequestId;
            _requestException = existingResults._requestException;
            _resultsByTarget = CreateTargetResultDictionaryWithContents(existingResults, targetNames);
            _baseOverallResult = existingResults.OverallResult == BuildResultCode.Success;
            _buildRequestDataFlags = existingResults._buildRequestDataFlags;
            _projectStateAfterBuild = existingResults._projectStateAfterBuild;

            _circularDependency = existingResults._circularDependency;
        }

        /// <summary>
        /// Constructs a new build result with existing results, but associated with the specified request.
        /// </summary>
        /// <param name="request">The build request with which these results should be associated.</param>
        /// <param name="existingResults">The existing results, if any.</param>
        /// <param name="exception">The exception, if any</param>
        internal BuildResult(BuildRequest request, BuildResult? existingResults, Exception? exception)
            : this(request, existingResults, null, exception)
        {
        }

        /// <summary>
        /// Constructs a new build result with existing results, but associated with the specified request.
        /// </summary>
        /// <param name="request">The build request with which these results should be associated.</param>
        /// <param name="existingResults">The existing results, if any.</param>
        /// <param name="targetNames">The list of target names that are the subset of results that should be returned.</param>
        /// <param name="exception">The exception, if any</param>
        internal BuildResult(BuildRequest request, BuildResult? existingResults, string[]? targetNames, Exception? exception)
        {
            _submissionId = request.SubmissionId;
            _configurationId = request.ConfigurationId;
            _globalRequestId = request.GlobalRequestId;
            _parentGlobalRequestId = request.ParentGlobalRequestId;
            _nodeRequestId = request.NodeRequestId;
            _circularDependency = false;
            _baseOverallResult = true;
            _buildRequestDataFlags = request.BuildRequestDataFlags;

            if (existingResults == null)
            {
                _requestException = exception;
                _resultsByTarget = CreateTargetResultDictionary(0);
            }
            else
            {
                _requestException = exception ?? existingResults._requestException;
                _resultsByTarget = targetNames == null ? existingResults._resultsByTarget : CreateTargetResultDictionaryWithContents(existingResults, targetNames);
                if (request.RequestedProjectState != null)
                {
                    _projectStateAfterBuild = existingResults._projectStateAfterBuild?.FilteredCopy(request.RequestedProjectState);
                }
            }
        }

        /// <summary>
        /// Constructor which allows reporting results for a different nodeRequestId
        /// </summary>
        internal BuildResult(BuildResult result, int nodeRequestId)
        {
            _configurationId = result._configurationId;
            _globalRequestId = result._globalRequestId;
            _parentGlobalRequestId = result._parentGlobalRequestId;
            _nodeRequestId = nodeRequestId;
            _requestException = result._requestException;
            _resultsByTarget = result._resultsByTarget;
            _circularDependency = result._circularDependency;
            _initialTargets = result._initialTargets;
            _defaultTargets = result._defaultTargets;
            _projectTargets = result._projectTargets;
            _baseOverallResult = result.OverallResult == BuildResultCode.Success;
        }

        internal BuildResult(BuildResult result, int submissionId, int configurationId, int requestId, int parentRequestId, int nodeRequestId)
        {
            _submissionId = submissionId;
            _configurationId = configurationId;
            _globalRequestId = requestId;
            _parentGlobalRequestId = parentRequestId;
            _nodeRequestId = nodeRequestId;

            _requestException = result._requestException;
            _resultsByTarget = result._resultsByTarget;
            _circularDependency = result._circularDependency;
            _initialTargets = result._initialTargets;
            _defaultTargets = result._defaultTargets;
            _projectTargets = result._projectTargets;
            _baseOverallResult = result.OverallResult == BuildResultCode.Success;
        }

        /// <summary>
        /// Constructor for deserialization
        /// </summary>
        private BuildResult(ITranslator translator)
        {
            ((ITranslatable)this).Translate(translator);
            _resultsByTarget ??= CreateTargetResultDictionary(1);
        }

        /// <summary>
        /// Returns the submission id.
        /// </summary>
        public override int SubmissionId
        {
            [DebuggerStepThrough]
            get
            { return _submissionId; }
        }

        /// <summary>
        /// Returns the configuration ID for this result.
        /// </summary>
        public int ConfigurationId
        {
            [DebuggerStepThrough]
            get
            { return _configurationId; }
        }

        /// <summary>
        /// Returns the build request id for which this result was generated
        /// </summary>
        public int GlobalRequestId
        {
            [DebuggerStepThrough]
            get
            { return _globalRequestId; }
        }

        /// <summary>
        /// Returns the build request id for the parent of the request for which this result was generated
        /// </summary>
        public int ParentGlobalRequestId
        {
            [DebuggerStepThrough]
            get
            { return _parentGlobalRequestId; }
        }

        /// <summary>
        /// Returns the node build request id for which this result was generated
        /// </summary>
        public int NodeRequestId
        {
            [DebuggerStepThrough]
            get
            { return _nodeRequestId; }
        }

        /// <summary>
        /// Returns the exception generated while this result was run, if any.
        /// </summary>
        public override Exception? Exception
        {
            [DebuggerStepThrough]
            get
            { return _requestException; }

            [DebuggerStepThrough]
            internal set
            { _requestException = value; }
        }

        /// <summary>
        /// Returns a flag indicating if a circular dependency was detected.
        /// </summary>
        public override bool CircularDependency
        {
            [DebuggerStepThrough]
            get
            { return _circularDependency; }
        }

        /// <summary>
        /// Returns the overall result for this result set.
        /// </summary>
        public override BuildResultCode OverallResult
        {
            get
            {
                if (_requestException != null || _circularDependency || !_baseOverallResult)
                {
                    return BuildResultCode.Failure;
                }

                foreach (KeyValuePair<string, TargetResult> result in _resultsByTarget ?? [])
                {
                    if ((result.Value.ResultCode == TargetResultCode.Failure && !result.Value.TargetFailureDoesntCauseBuildFailure)
                        || result.Value.AfterTargetsHaveFailed)
                    {
                        return BuildResultCode.Failure;
                    }
                }

                return BuildResultCode.Success;
            }
        }

        /// <summary>
        /// Returns an enumerator for all target results in this build result
        /// </summary>
        public IDictionary<string, TargetResult> ResultsByTarget
        {
            [DebuggerStepThrough]
            get
            { return _resultsByTarget; }
        }

        /// <summary>
        /// <see cref="ProjectInstance"/> state after the build. In general, it may be a non buildable-dummy object, and should only
        /// be used to retrieve <see cref="ProjectInstance.Properties"/>, <see cref="ProjectInstance.GlobalProperties"/> and
        /// <see cref="ProjectInstance.Items"/> from it. Any other operation is not guaranteed to be supported.
        /// </summary>
        public ProjectInstance? ProjectStateAfterBuild
        {
            get => _projectStateAfterBuild;
            set => _projectStateAfterBuild = value;
        }

        /// <summary>
        /// Gets the flags that were used in the build request to which these results are associated.
        /// See <see cref="Execution.BuildRequestDataFlags"/> for examples of the available flags.
        /// </summary>
        /// <remarks>
        /// Is optional, this property exists starting version 1.
        /// </remarks>
        public BuildRequestDataFlags? BuildRequestDataFlags => (_version > 0) ? _buildRequestDataFlags : null;

        /// <summary>
        /// Returns the node packet type.
        /// </summary>
        NodePacketType INodePacket.Type
        {
            [DebuggerStepThrough]
            get
            { return NodePacketType.BuildResult; }
        }

        /// <summary>
        /// Holds a snapshot of the environment at the time we blocked.
        /// </summary>
        Dictionary<string, string>? IBuildResults.SavedEnvironmentVariables
        {
            get => _savedEnvironmentVariables;

            set => _savedEnvironmentVariables = value;
        }

        /// <summary>
        /// Holds a snapshot of the current working directory at the time we blocked.
        /// </summary>
        string? IBuildResults.SavedCurrentDirectory
        {
            get => _savedCurrentDirectory;

            set => _savedCurrentDirectory = value;
        }

        /// <summary>
        /// Returns the initial targets for the configuration which requested these results.
        /// </summary>
        internal List<string>? InitialTargets
        {
            [DebuggerStepThrough]
            get
            { return _initialTargets; }

            [DebuggerStepThrough]
            set
            { _initialTargets = value; }
        }

        /// <summary>
        /// Returns the default targets for the configuration which requested these results.
        /// </summary>
        internal List<string>? DefaultTargets
        {
            [DebuggerStepThrough]
            get
            { return _defaultTargets; }

            [DebuggerStepThrough]
            set
            { _defaultTargets = value; }
        }

        /// <summary>
        /// The defined targets for the project associated with this build result.
        /// </summary>
        internal HashSet<string>? ProjectTargets
        {
            [DebuggerStepThrough]
            get => _projectTargets;
            [DebuggerStepThrough]
            set => _projectTargets = value;
        }

        /// <summary>
        /// Container used to transport errors from the scheduler (issued while computing a build result)
        /// to the TaskHost that has the proper logging context (project id, target id, task id, file location)
        /// </summary>
        internal string? SchedulerInducedError
        {
            get => _schedulerInducedError;
            set => _schedulerInducedError = value;
        }

        /// <summary>
        /// Indexer which sets or returns results for the specified target
        /// </summary>
        /// <param name="target">The target</param>
        /// <returns>The results for the specified target</returns>
        /// <exception>KeyNotFoundException is returned if the specified target doesn't exist when reading this property.</exception>
        /// <exception>ArgumentException is returned if the specified target already has results.</exception>
        public ITargetResult this[string target]
        {
            [DebuggerStepThrough]
            get
            { return _resultsByTarget![target]; }
        }

        /// <summary>
        /// Adds the results for the specified target to this result collection.
        /// </summary>
        /// <param name="target">The target to which these results apply.</param>
        /// <param name="result">The results for the target.</param>
        public void AddResultsForTarget(string target, TargetResult result)
        {
            ErrorUtilities.VerifyThrowArgumentNull(target);
            ErrorUtilities.VerifyThrowArgumentNull(result);

            lock (this)
            {
                _resultsByTarget ??= CreateTargetResultDictionary(1);
            }

            if (_resultsByTarget.TryGetValue(target, out TargetResult? targetResult))
            {
                ErrorUtilities.VerifyThrow(targetResult.ResultCode == TargetResultCode.Skipped, "Items already exist for target {0}.", target);
            }

            _resultsByTarget[target] = result;
        }

        /// <summary>
        /// Keep the results only for targets in <paramref name="targetsToKeep"/>.
        /// </summary>
        /// <param name="targetsToKeep">The targets whose results to keep.</param>
        internal void KeepSpecificTargetResults(IReadOnlyCollection<string> targetsToKeep)
        {
            ErrorUtilities.VerifyThrow(
                targetsToKeep.Count > 0,
                $"{nameof(targetsToKeep)} should contain at least one target.");

            foreach (string target in _resultsByTarget?.Keys ?? [])
            {
                if (!targetsToKeep.Contains(target))
                {
                    _ = _resultsByTarget!.TryRemove(target, out _);
                }
            }
        }

        /// <summary>
        /// Merges the specified results with the results contained herein.
        /// </summary>
        /// <param name="results">The results to merge in.</param>
        public void MergeResults(BuildResult results)
        {
            ErrorUtilities.VerifyThrowArgumentNull(results);
            ErrorUtilities.VerifyThrow(results.ConfigurationId == ConfigurationId, "Result configurations don't match");

            // If we are merging with ourself or with a shallow clone, do nothing.
            if (ReferenceEquals(this, results) || ReferenceEquals(_resultsByTarget, results._resultsByTarget))
            {
                return;
            }

            // Merge in the results
            foreach (KeyValuePair<string, TargetResult> targetResult in results._resultsByTarget ?? [])
            {
                // NOTE: I believe that because we only allow results for a given target to be produced and cached once for a given configuration,
                // we can never receive conflicting results for that target, since the cache and build request manager would always return the
                // cached results after the first time the target is built.  As such, we can allow "duplicates" to be merged in because there is
                // no change.  If, however, this turns out not to be the case, we need to re-evaluate this merging and possibly re-enable the
                // assertion below.
                // ErrorUtilities.VerifyThrow(!HasResultsForTarget(targetResult.Key), "Results already exist");

                // Copy the new results in.
                _resultsByTarget![targetResult.Key] = targetResult.Value;
            }

            // If there is an exception and we did not previously have one, add it in.
            _requestException ??= results.Exception;
        }

        /// <summary>
        /// Determines if there are any results for the specified target.
        /// </summary>
        /// <param name="target">The target for which results are desired.</param>
        /// <returns>True if results exist, false otherwise.</returns>
        public bool HasResultsForTarget(string target)
        {
            return _resultsByTarget?.ContainsKey(target) ?? false;
        }

        public bool TryGetResultsForTarget(string target, [NotNullWhen(true)] out TargetResult? value)
        {
            if (_resultsByTarget is null)
            {
                value = default;
                return false;
            }

            return _resultsByTarget.TryGetValue(target, out value);
        }
        #region INodePacket Members

        /// <summary>
        /// Reads or writes the packet to the serializer.
        /// </summary>
        void ITranslatable.Translate(ITranslator translator)
        {
            translator.Translate(ref _submissionId);
            translator.Translate(ref _configurationId);
            translator.Translate(ref _globalRequestId);
            translator.Translate(ref _parentGlobalRequestId);
            translator.Translate(ref _nodeRequestId);
            translator.Translate(ref _initialTargets);
            translator.Translate(ref _defaultTargets);
            translator.Translate(ref _projectTargets);
            translator.Translate(ref _circularDependency);
            translator.TranslateException(ref _requestException);
            translator.TranslateDictionary(ref _resultsByTarget, TargetResult.FactoryForDeserialization, CreateTargetResultDictionary);
            translator.Translate(ref _baseOverallResult);
            translator.Translate(ref _projectStateAfterBuild, ProjectInstance.FactoryForDeserialization);
            translator.Translate(ref _savedCurrentDirectory);
            translator.Translate(ref _schedulerInducedError);

            // This is a work-around for the bug https://github.com/dotnet/msbuild/issues/10208
            // We are adding a version field to this class to make the ResultsCache backwards compatible with at least 2 previous releases.
            // The adding of a version field is done without a breaking change in 3 steps, each separated with at least 1 intermediate release.
            //
            // 1st step (done): Add a special key to the _savedEnvironmentVariables dictionary during the serialization. A workaround overload of the TranslateDictionary function is created to achieve it.
            // The presence of this key will indicate that the version is serialized next.
            // When serializing, add a key to the dictionary and serialize a version field.
            // Do not actually save the special key to dictionary during the deserialization, but read a version as a next field if it presents.
            //
            // 2nd step: Stop serialize a special key with the dictionary _savedEnvironmentVariables using the TranslateDictionary function workaround overload. Always serialize and de-serialize the version field.
            // Continue to deserialize _savedEnvironmentVariables with the TranslateDictionary function workaround overload in order not to deserialize dictionary with the special keys.
            //
            // 3rd step: Stop using the TranslateDictionary function workaround overload during _savedEnvironmentVariables deserialization.
            if (_version == 0)
            {
                // Escape hatch: serialize/deserialize without version field.
                translator.TranslateDictionary(ref _savedEnvironmentVariables, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Dictionary<string, string> additionalEntries = new();

                if (translator.Mode == TranslationDirection.WriteToStream)
                {
                    // Add the special key SpecialKeyForVersion to additional entries indicating the presence of a version to the _savedEnvironmentVariables dictionary.
                    additionalEntries.Add(SpecialKeyForVersion, String.Empty);

                    // Serialize the special key together with _savedEnvironmentVariables dictionary using the workaround overload of TranslateDictionary:
                    translator.TranslateDictionary(ref _savedEnvironmentVariables, StringComparer.OrdinalIgnoreCase, ref additionalEntries, s_additionalEntriesKeys);

                    // Serialize version
                    translator.Translate(ref _version);
                }
                else if (translator.Mode == TranslationDirection.ReadFromStream)
                {
                    // Read the dictionary using the workaround overload of TranslateDictionary: special keys (additionalEntriesKeys) would be read to additionalEntries instead of the _savedEnvironmentVariables dictionary.
                    translator.TranslateDictionary(ref _savedEnvironmentVariables, StringComparer.OrdinalIgnoreCase, ref additionalEntries, s_additionalEntriesKeys);

                    // If the special key SpecialKeyForVersion present in additionalEntries, also read a version, otherwise set it to 0.
                    if (additionalEntries is not null && additionalEntries.ContainsKey(SpecialKeyForVersion))
                    {
                        translator.Translate(ref _version);
                    }
                    else
                    {
                        _version = 0;
                    }
                }
            }

            // Starting version 1 this _buildRequestDataFlags field is present.
            if (_version > 0)
            {
                translator.TranslateEnum(ref _buildRequestDataFlags, (int)_buildRequestDataFlags);
            }
        }

        /// <summary>
        /// Factory for serialization
        /// </summary>
        internal static BuildResult FactoryForDeserialization(ITranslator translator)
        {
            return new BuildResult(translator);
        }

        #endregion

        /// <summary>
        /// Caches all of the targets results we can.
        /// </summary>
        internal void CacheIfPossible()
        {
            foreach (KeyValuePair<string, TargetResult> targetResultPair in _resultsByTarget ?? [])
            {
                targetResultPair.Value.CacheItems(ConfigurationId, targetResultPair.Key);
            }
        }

        /// <summary>
        /// Clear cached files from disk.
        /// </summary>
        internal void ClearCachedFiles()
        {
            string resultsDirectory = TargetResult.GetCacheDirectory(_configurationId, "None" /*Does not matter because we just need the directory name not the file*/);
            if (FileSystems.Default.DirectoryExists(resultsDirectory))
            {
                FileUtilities.DeleteDirectoryNoThrow(resultsDirectory, true /*recursive*/);
            }
        }

        /// <summary>
        /// Clones the build result (the resultsByTarget field is only a shallow copy).
        /// </summary>
        internal BuildResult Clone()
        {
            BuildResult result = new BuildResult
            {
                _submissionId = _submissionId,
                _configurationId = _configurationId,
                _globalRequestId = _globalRequestId,
                _parentGlobalRequestId = _parentGlobalRequestId,
                _nodeRequestId = _nodeRequestId,
                _requestException = _requestException,
                _resultsByTarget = new ConcurrentDictionary<string, TargetResult>(_resultsByTarget, StringComparer.OrdinalIgnoreCase),
                _baseOverallResult = OverallResult == BuildResultCode.Success,
                _circularDependency = _circularDependency
            };

            return result;
        }

        /// <summary>
        /// Sets the overall result.
        /// </summary>
        /// <param name="overallResult"><code>true</code> if the result is success, otherwise <code>false</code>.</param>
        internal void SetOverallResult(bool overallResult)
        {
            _baseOverallResult = false;
        }

        /// <summary>
        /// Creates the target result dictionary.
        /// </summary>
        private static ConcurrentDictionary<string, TargetResult> CreateTargetResultDictionary(int capacity)
        {
            return new ConcurrentDictionary<string, TargetResult>(1, capacity, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates the target result dictionary and populates it with however many target results are
        /// available given the list of targets passed.
        /// </summary>
        private static ConcurrentDictionary<string, TargetResult> CreateTargetResultDictionaryWithContents(BuildResult existingResults, string[] targetNames)
        {
            ConcurrentDictionary<string, TargetResult> resultsByTarget = CreateTargetResultDictionary(targetNames.Length);

            foreach (string target in targetNames)
            {
                if (existingResults.ResultsByTarget?.TryGetValue(target, out TargetResult? targetResult) ?? false)
                {
                    resultsByTarget[target] = targetResult;
                }
            }

            return resultsByTarget;
        }
    }
}
