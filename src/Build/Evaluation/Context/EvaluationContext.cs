// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Policy;
using System.Threading;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    /// <summary>
    ///     An object used by the caller to extend the lifespan of evaluation caches (by passing the object on to other
    ///     evaluations).
    ///     The caller should throw away the context when the environment changes (IO, environment variables, SDK resolution
    ///     inputs, etc).
    ///     This class and its closure needs to be thread safe since API users can do evaluations in parallel.
    /// </summary>
    public class EvaluationContext
    {
        private readonly ProjectLoadSettings? _projectLoadSettings;

        public enum SharingPolicy
        {
            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to reuse all cached state between the different project evaluations that use it.
            /// </summary>
            Shared,

            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to not reuse any cached state between the different project evaluations that use it.
            /// </summary>
            Isolated,

            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to reuse SDK resolver cache between the different project evaluations that use it.
            /// No other cached state is reused.
            /// </summary>
            SharedSDKCache,
        }

        /// <summary>
        /// For contexts that are not fully shared, this field tracks whether the instance has already been used for evaluation.
        /// </summary>
        private int _used;

        internal static Action<EvaluationContext> TestOnlyHookOnCreate { get; set; }

        internal SharingPolicy Policy { get; }

        internal ISdkResolverService SdkResolverService { get; }
        internal IFileSystem FileSystem { get; }
        internal FileMatcher FileMatcher { get; }

        /// <summary>
        /// Key to file entry list. Example usages: cache glob expansion and intermediary directory expansions during glob expansion.
        /// </summary>
        private ConcurrentDictionary<string, IReadOnlyList<string>> FileEntryExpansionCache { get; }

        private EvaluationContext(SharingPolicy policy, IFileSystem fileSystem, ProjectLoadSettings? projectLoadSettings, ISdkResolverService sdkResolverService = null,
            ConcurrentDictionary<string, IReadOnlyList<string>> fileEntryExpansionCache = null)
        {
            Policy = policy;

            _projectLoadSettings = projectLoadSettings;
            SdkResolverService = sdkResolverService ?? new CachingSdkResolverService();
            FileEntryExpansionCache = fileEntryExpansionCache ?? new ConcurrentDictionary<string, IReadOnlyList<string>>();
            bool skipExistenceCheck = (_projectLoadSettings?.HasFlag(ProjectLoadSettings.IgnoreMissingImports) ?? false) && Traits.Instance.SkipExistenceCheckForCache;
            FileSystem = fileSystem ?? new CachingFileSystemWrapper(FileSystems.Default, skipExistenceCheck);
            FileMatcher = new FileMatcher(FileSystem, FileEntryExpansionCache);
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        public static EvaluationContext Create(SharingPolicy policy)
        {
            // Do not remove this method to avoid breaking binary compatibility.
            return Create(policy, fileSystem: null, projectLoadSettings: null);
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        /// <param name="fileSystem">The <see cref="MSBuildFileSystemBase"/> to use.
        ///     This parameter is compatible only with <see cref="SharingPolicy.Shared"/>.
        ///     The method throws if a file system is used with <see cref="SharingPolicy.Isolated"/> or <see cref="SharingPolicy.SharedSDKCache"/>.
        ///     The reasoning is that these values guarantee not reusing file system caches between evaluations,
        ///     and the passed in <paramref name="fileSystem"/> might cache state.
        /// </param>
        public static EvaluationContext Create(SharingPolicy policy, MSBuildFileSystemBase fileSystem)
        {
            return Create(policy, fileSystem, projectLoadSettings: null);
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        /// <param name="projectLoadSettings">The <see cref="ProjectLoadSettings"/> to use.</param>
        public static EvaluationContext Create(SharingPolicy policy, ProjectLoadSettings? projectLoadSettings)
        {
            // Do not remove this method to avoid breaking binary compatibility.
            return Create(policy, fileSystem: null, projectLoadSettings: projectLoadSettings);
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        /// <param name="fileSystem">The <see cref="IFileSystem"/> to use.
        ///     This parameter is compatible only with <see cref="SharingPolicy.Shared"/>.
        ///     The method throws if a file system is used with <see cref="SharingPolicy.Isolated"/> or <see cref="SharingPolicy.SharedSDKCache"/>.
        ///     The reasoning is that these values guarantee not reusing file system caches between evaluations,
        ///     and the passed in <paramref name="fileSystem"/> might cache state.
        /// </param>
        /// <param name="projectLoadSettings">The <see cref="ProjectLoadSettings"/> to use.</param>
        public static EvaluationContext Create(SharingPolicy policy, MSBuildFileSystemBase fileSystem, ProjectLoadSettings? projectLoadSettings)
        {
            // Unsupported case: not-fully-shared context with non null file system.
            ErrorUtilities.VerifyThrowArgument(
                policy == SharingPolicy.Shared || fileSystem == null,
                "IsolatedContextDoesNotSupportFileSystem");

            var context = new EvaluationContext(
                policy,
                fileSystem,
                projectLoadSettings);

            TestOnlyHookOnCreate?.Invoke(context);

            return context;
        }

        internal EvaluationContext ContextForNewProject()
        {
            // Projects using Isolated and SharedSDKCache contexts need to get a new context instance.
            switch (Policy)
            {
                case SharingPolicy.Shared:
                    return this;
                case SharingPolicy.SharedSDKCache:
                case SharingPolicy.Isolated:
                    // Reuse the first not-fully-shared context if it's not been used for an evaluation yet.
                    if (Interlocked.CompareExchange(ref _used, 1, 0) == 0)
                    {
                        return this;
                    }
                    // Create a copy if this context has already been used. Mark it used.
                    EvaluationContext context = new EvaluationContext(Policy, fileSystem: null, projectLoadSettings: _projectLoadSettings, sdkResolverService: Policy == SharingPolicy.SharedSDKCache ? SdkResolverService : null)
                    {
                        _used = 1,
                    };
                    TestOnlyHookOnCreate?.Invoke(context);
                    return context;

                default:
                    ErrorUtilities.ThrowInternalErrorUnreachable();
                    return null;
            }
        }

        /// <summary>
        /// Creates a copy of this <see cref="EvaluationContext"/> with a given <see cref="IFileSystem"/> swapped in.
        /// </summary>
        /// <param name="fileSystem">The file system to use by the new evaluation context.</param>
        /// <returns>The new evaluation context.</returns>
        internal EvaluationContext ContextWithFileSystem(IFileSystem fileSystem)
        {
            return new EvaluationContext(Policy, fileSystem, null, SdkResolverService, FileEntryExpansionCache)
            {
                _used = 1,
            };
        }
    }
}
