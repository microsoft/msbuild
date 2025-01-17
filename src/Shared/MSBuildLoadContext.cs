// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class is used to isolate the types used by an MSBuild plugin
    /// (SDK resolver, logger, or task).
    /// </summary>
    internal class MSBuildLoadContext : AssemblyLoadContext
    {
        private AssemblyDependencyResolver? _resolver;

        private readonly string _directory;

        internal static readonly ImmutableHashSet<string> WellKnownAssemblyNames =
        [
            "MSBuild",
            "Microsoft.Build",
            "Microsoft.Build.Framework",
            "Microsoft.Build.Tasks.Core",
            "Microsoft.Build.Utilities.Core",
        ];

        /// <summary>
        /// The <see cref="AssemblyLoadContext"/> in which the MSBuild assemblies are loaded.
        /// </summary>
        internal static AssemblyLoadContext ThisAssemblyLoadContext => GetLoadContext(typeof(MSBuildLoadContext).Assembly)!;

        public MSBuildLoadContext(string assemblyPath)
            : base($"MSBuild plugin {assemblyPath}", ThisAssemblyLoadContext.IsCollectible)
        {
            _directory = Directory.GetParent(assemblyPath)!.FullName;

            // We check for the assemblyPath because it will fail with an AssemblyDependencyResolver-specific error
            // if it does not exist. We should instead fall back to the standard failure.
            // The second check is because AssemblyDependencyResolver loads assemblies differently than we do by default.
            // We should maintain previous behavior in the absence of new data (a .deps.json file) indicating that we
            // should do something different.
            // Setting the _resolver to null essentially just opts out of the new behavior.
            _resolver = File.Exists(assemblyPath) && File.Exists(Path.ChangeExtension(assemblyPath, ".deps.json"))
                ? new AssemblyDependencyResolver(assemblyPath) :
                null;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (WellKnownAssemblyNames.Contains(assemblyName.Name!))
            {
                // Force MSBuild assemblies to be loaded in the same ALC
                // and unify to the current version.
                return ThisAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
            }

            // respect plugin.deps.json with the AssemblyDependencyResolver
            string? assemblyPath = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // Fall back to the older MSBuild-on-Core behavior to continue to support
            // plugins that don't ship a .deps.json

            foreach (var cultureSubfolder in string.IsNullOrEmpty(assemblyName.CultureName)
                // If no culture is specified, attempt to load directly from
                // the known dependency paths.
                ? (string[])[string.Empty]
                // Search for satellite assemblies in culture subdirectories
                // of the assembly search directories, but fall back to the
                // bare search directory if that fails.
                : [assemblyName.CultureName, string.Empty])
            {
                var candidatePath = Path.Combine(_directory,
                    cultureSubfolder,
                    $"{assemblyName.Name}.dll");

                if (!FileSystems.Default.FileExists(candidatePath))
                {
                    continue;
                }

                AssemblyName candidateAssemblyName = GetAssemblyName(candidatePath);
                if (candidateAssemblyName.Version != assemblyName.Version)
                {
                    continue;
                }

                return LoadFromAssemblyPath(candidatePath);
            }

            // If the Assembly is provided via a file path, the following rules are used to load the assembly:
            // - the assembly from the user specified path is loaded, if it exists, into the custom ALC, or
            // - if the simple name of the assembly exists in the same folder as msbuild.exe, then that assembly gets loaded
            //   into MSBuild's ALC (so it's shared with other uses).
            var assemblyNameInExecutableDirectory = Path.Combine(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory,
                $"{assemblyName.Name}.dll");

            if (FileSystems.Default.FileExists(assemblyNameInExecutableDirectory))
            {
                return ThisAssemblyLoadContext.LoadFromAssemblyPath(assemblyNameInExecutableDirectory);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return base.LoadUnmanagedDll(unmanagedDllName);
        }
    }
}
