// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Build.Eventing;

namespace Microsoft.Build.BackEnd.SdkResolution
{
    /// <summary>
    /// The main implementation of <see cref="ISdkResolverService"/> which resolves SDKs.  This class is the central location for all SDK resolution and is used
    /// directly by the main node and non-build evaluations and is used indirectly by the out-of-proc node when it sends requests to the main node.
    /// </summary>
    internal class SdkResolverService : ISdkResolverService
    {
        /// <summary>
        /// Stores the singleton instance for a particular process.
        /// </summary>
        private static readonly Lazy<SdkResolverService> InstanceLazy = new Lazy<SdkResolverService>(() => new SdkResolverService(), isThreadSafe: true);

        /// <summary>
        /// A lock object used for this class.
        /// </summary>
        private readonly object _lockObject = new object();

        /// <summary>
        /// Stores resolver state by build submission ID.
        /// </summary>
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<SdkResolver, object>> _resolverStateBySubmission = new ConcurrentDictionary<int, ConcurrentDictionary<SdkResolver, object>>();

        /// <summary>
        /// Stores the list of SDK resolvers which were loaded.
        /// </summary>
        private IList<SdkResolver> _resolvers;

        /// <summary>
        /// Stores an <see cref="SdkResolverLoader"/> which can load registered SDK resolvers.
        /// </summary>
        private SdkResolverLoader _sdkResolverLoader = new SdkResolverLoader();

        public SdkResolverService()
        {
        }

        /// <summary>
        /// Gets the current instance of <see cref="SdkResolverService"/> for this process.
        /// </summary>
        public static SdkResolverService Instance => InstanceLazy.Value;

        /// <inheritdoc cref="ISdkResolverService.SendPacket"/>
        public Action<INodePacket> SendPacket { get; }

        /// <summary>
        /// Determines if the <see cref="SdkReference"/> is the same as the specified version.  If the <paramref name="sdk"/> object has <code>null</code> for the version,
        /// this method will always return true since <code>null</code> can match any version.
        /// </summary>
        /// <param name="sdk">An <see cref="SdkReference"/> object.</param>
        /// <param name="version">The version to compare.</param>
        /// <returns><code>true</code> if the specified SDK reference has the same version as the specified result, otherwise <code>false</code>.</returns>
        public static bool IsReferenceSameVersion(SdkReference sdk, string version)
        {
            // If the reference has a null version, it matches any result
            if (String.IsNullOrEmpty(sdk.Version))
            {
                return true;
            }

            return String.Equals(sdk.Version, version, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc cref="ISdkResolverService.ClearCache"/>
        public virtual void ClearCache(int submissionId)
        {
            _resolverStateBySubmission.TryRemove(submissionId, out _);
        }

        public virtual void ClearCaches()
        {
            _resolverStateBySubmission.Clear();
        }

        /// <inheritdoc cref="ISdkResolverService.ResolveSdk"/>
        public virtual SdkResult ResolveSdk(int submissionId, SdkReference sdk, LoggingContext loggingContext, ElementLocation sdkReferenceLocation, string solutionPath, string projectPath, bool interactive, bool isRunningInVisualStudio)
        {
            // Lazy initialize the SDK resolvers
            if (_resolvers == null)
            {
                Initialize(loggingContext, sdkReferenceLocation);
            }

            List<SdkResult> results = new List<SdkResult>();

            // Loop through resolvers which have already been sorted by priority, returning the first result that was successful
            SdkLogger buildEngineLogger = new SdkLogger(loggingContext);

            loggingContext.LogComment(MessageImportance.Low, "SdkResolving", sdk.ToString());

            foreach (SdkResolver sdkResolver in _resolvers)
            {
                SdkResolverContext context = new SdkResolverContext(buildEngineLogger, projectPath, solutionPath, ProjectCollection.Version, interactive, isRunningInVisualStudio)
                {
                    State = GetResolverState(submissionId, sdkResolver)
                };

                SdkResultFactory resultFactory = new SdkResultFactory(sdk);

                SdkResult result;

                try
                {
                    MSBuildEventSource.Log.SdkResolverResolveSdkStart();
                    result = (SdkResult)sdkResolver.Resolve(sdk, context, resultFactory);
                    MSBuildEventSource.Log.SdkResolverResolveSdkStop(sdkResolver.Name, sdk.Name, solutionPath, projectPath, result?.Path, result?.Success ?? false);
                }
                catch (Exception e) when ((e is FileNotFoundException || e is FileLoadException) && sdkResolver.GetType().GetTypeInfo().Name.Equals("NuGetSdkResolver", StringComparison.Ordinal))
                {
                    // Since we explicitly add the NuGetSdkResolver, we special case this.  The NuGetSdkResolver has special logic
                    // to load NuGet assemblies at runtime which could fail if the user is not running installed MSBuild.  Rather
                    // than give them a generic error, we want to give a more specific message.  This exception cannot be caught by
                    // the resolver itself because it is usually thrown before the class is loaded
                    // The NuGet-based SDK resolver failed to run because NuGet assemblies could not be located.  Check your installation of MSBuild or set the environment variable "{0}" to the folder that contains the required NuGet assemblies. {1}
                    throw new SdkResolverException("CouldNotRunNuGetSdkResolver", sdkResolver, sdk, e, MSBuildConstants.NuGetAssemblyPathEnvironmentVariableName, e.ToString());
                }
                catch (Exception e)
                {
                    // The SDK resolver "{0}" failed while attempting to resolve the SDK "{1}": {2}
                    throw new SdkResolverException("SDKResolverFailed", sdkResolver, sdk, e, sdkResolver.Name, sdk.ToString(), e.ToString());
                }

                SetResolverState(submissionId, sdkResolver, context.State);

                if (result == null)
                {
                    continue;
                }

                if (result.Success)
                {
                    LogWarnings(loggingContext, sdkReferenceLocation, result);

                    if (!IsReferenceSameVersion(sdk, result.Version))
                    {
                        // MSB4241: The SDK reference "{0}" version "{1}" was resolved to version "{2}" instead.  You could be using a different version than expected if you do not update the referenced version to match.
                        loggingContext.LogWarning(null, new BuildEventFileInfo(sdkReferenceLocation), "SdkResultVersionDifferentThanReference", sdk.Name, sdk.Version, result.Version);
                    }

                    // Associate the element location of the resolved SDK reference
                    result.ElementLocation = sdkReferenceLocation;

                    return result;
                }

                results.Add(result);
            }

            foreach (SdkResult result in results)
            {
                LogWarnings(loggingContext, sdkReferenceLocation, result);

                if (result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
                        loggingContext.LogErrorFromText(subcategoryResourceName: null, errorCode: null, helpKeyword: null, file: new BuildEventFileInfo(sdkReferenceLocation), message: error);
                    }
                }
            }

            return new SdkResult(sdk, null, null);
        }

        /// <summary>
        /// Used for unit tests only.  This is currently only called through reflection in Microsoft.Build.Engine.UnitTests.TransientSdkResolution.CallResetForTests
        /// </summary>
        /// <param name="resolverLoader">An <see cref="SdkResolverLoader"/> to use for loading SDK resolvers.</param>
        /// <param name="resolvers">Explicit set of SdkResolvers to use for all SDK resolution.</param>
        internal void InitializeForTests(SdkResolverLoader resolverLoader = null, IList<SdkResolver> resolvers = null)
        {
            if (resolverLoader != null)
            {
                _sdkResolverLoader = resolverLoader;
            }

            _resolvers = resolvers;
        }

        private static void LogWarnings(LoggingContext loggingContext, ElementLocation location, SdkResult result)
        {
            if (result.Warnings == null)
            {
                return;
            }

            foreach (string warning in result.Warnings)
            {
                loggingContext.LogWarningFromText(null, null, null, new BuildEventFileInfo(location), warning);
            }
        }

        private object GetResolverState(int submissionId, SdkResolver resolver)
        {
            // Do not fetch state for resolution requests that are not associated with a valid build submission ID
            if (submissionId != BuildEventContext.InvalidSubmissionId)
            {
                ConcurrentDictionary<SdkResolver, object> resolverState;

                if (_resolverStateBySubmission.TryGetValue(submissionId, out resolverState))
                {
                    object state;

                    if (resolverState.TryGetValue(resolver, out state))
                    {
                        return state;
                    }
                }
            }

            return null;
        }

        private void Initialize(LoggingContext loggingContext, ElementLocation location)
        {
            lock (_lockObject)
            {
                if (_resolvers != null)
                {
                    return;
                }

                MSBuildEventSource.Log.SdkResolverServiceInitializeStart();
                _resolvers = _sdkResolverLoader.LoadResolvers(loggingContext, location);
                MSBuildEventSource.Log.SdkResolverServiceInitializeStop(_resolvers.Count);
            }

        }

        private void SetResolverState(int submissionId, SdkResolver resolver, object state)
        {
            // Do not set state for resolution requests that are not associated with a valid build submission ID
            if (submissionId != BuildEventContext.InvalidSubmissionId)
            {
                ConcurrentDictionary<SdkResolver, object> resolverState = _resolverStateBySubmission.GetOrAdd(submissionId, new ConcurrentDictionary<SdkResolver, object>(NativeMethodsShared.GetLogicalCoreCount(), _resolvers.Count));

                resolverState.AddOrUpdate(resolver, state, (sdkResolver, obj) => state);
            }
        }
    }
}
