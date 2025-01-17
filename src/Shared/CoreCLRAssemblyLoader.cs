// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// CoreCLR-compatible wrapper for loading task assemblies.
    /// </summary>
    internal sealed class CoreClrAssemblyLoader
    {
        private readonly Dictionary<string, Assembly> _pathsToAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Assembly> _namesToAssemblies = new Dictionary<string, Assembly>();
        private readonly HashSet<string> _dependencyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _guard = new object();

        private bool _resolvingHandlerHookedUp = false;

        public void AddDependencyLocation(string fullPath)
        {
            if (fullPath == null)
            {
                throw new ArgumentNullException(nameof(fullPath));
            }

            lock (_guard)
            {
                _dependencyPaths.Add(fullPath);
            }
        }

        public Assembly LoadFromPath(string fullPath)
        {
            if (fullPath == null)
            {
                throw new ArgumentNullException(nameof(fullPath));
            }

            Debug.Assert(Path.IsPathRooted(fullPath));

            // Normalize because the same assembly might get loaded via
            // multiple paths (for instance, the `build` and `buildCrossTargeting`
            // folders in a NuGet package).
            fullPath = FileUtilities.NormalizePath(fullPath);

            if (Traits.Instance.EscapeHatches.UseSingleLoadContext)
            {
                return LoadUsingLegacyDefaultContext(fullPath);
            }
            else
            {
                return LoadUsingPluginContext(fullPath);
            }
        }

        private Assembly LoadUsingLegacyDefaultContext(string fullPath)
        {
            lock (_guard)
            {
                if (!_resolvingHandlerHookedUp)
                {
                    MSBuildLoadContext.ThisAssemblyLoadContext.Resolving += TryResolveAssembly;
                    _resolvingHandlerHookedUp = true;
                }

                Assembly assembly;
                if (_pathsToAssemblies.TryGetValue(fullPath, out assembly))
                {
                    return assembly;
                }

                return LoadAndCache(MSBuildLoadContext.ThisAssemblyLoadContext, fullPath);
            }
        }

        private Assembly LoadUsingPluginContext(string fullPath)
        {
            lock (_guard)
            {
                Assembly assembly;
                if (_pathsToAssemblies.TryGetValue(fullPath, out assembly))
                {
                    return assembly;
                }

                var contextForAssemblyPath = new MSBuildLoadContext(fullPath);

                assembly = contextForAssemblyPath.LoadFromAssemblyPath(fullPath);

                if (assembly != null)
                {
                    _pathsToAssemblies[fullPath] = assembly;
                }

                return assembly;
            }
        }

        private Assembly TryResolveAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            lock (_guard)
            {
                if (MSBuildLoadContext.WellKnownAssemblyNames.Contains(assemblyName.Name))
                {
                    return MSBuildLoadContext.ThisAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
                }

                if (_namesToAssemblies.TryGetValue(assemblyName.FullName, out Assembly assembly))
                {
                    return assembly;
                }

                return TryResolveAssemblyFromPaths(context, assemblyName, _dependencyPaths);
            }
        }

        private Assembly TryResolveAssemblyFromPaths(AssemblyLoadContext context, AssemblyName assemblyName, IEnumerable<string> searchPaths)
        {
            foreach (string cultureSubfolder in string.IsNullOrEmpty(assemblyName.CultureName)
                // If no culture is specified, attempt to load directly from
                // the known dependency paths.
                ? new[] { string.Empty }
                // Search for satellite assemblies in culture subdirectories
                // of the assembly search directories, but fall back to the
                // bare search directory if that fails.
                : [assemblyName.CultureName, string.Empty])
            {
                foreach (var searchPath in searchPaths)
                {
                    var candidatePath = Path.Combine(searchPath,
                        cultureSubfolder,
                        $"{assemblyName.Name}.dll");

                    if (IsAssemblyAlreadyLoaded(candidatePath) ||
                        !FileSystems.Default.FileExists(candidatePath))
                    {
                        continue;
                    }

                    AssemblyName candidateAssemblyName = AssemblyLoadContext.GetAssemblyName(candidatePath);
                    if (candidateAssemblyName.Version != assemblyName.Version)
                    {
                        continue;
                    }

                    return LoadAndCache(context, candidatePath);
                }
            }

            return null;
        }

        /// <remarks>
        /// Assumes we have a lock on _guard.
        /// </remarks>
        private Assembly LoadAndCache(AssemblyLoadContext context, string fullPath)
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            var name = assembly.FullName;

            _pathsToAssemblies[fullPath] = assembly;
            _namesToAssemblies[name] = assembly;

            return assembly;
        }

        private bool IsAssemblyAlreadyLoaded(string path)
        {
            return _pathsToAssemblies.ContainsKey(path);
        }
    }
}
