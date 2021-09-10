// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

using Microsoft.Build.Evaluation;

namespace Microsoft.Build.FileSystem
{
    /// <summary>
    /// A provider of <see cref="IDirectoryCache"/> instances. To be implemented by MSBuild hosts that wish to intercept
    /// file existence checks and file enumerations performed during project evaluation.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MSBuildFileSystemBase"/>, file enumeration returns file/directory names, not full paths.
    /// </remarks>
    public interface IDirectoryCacheFactory
    {
        /// <summary>
        /// Returns an <see cref="IDirectoryCache"/> to be used when evaluating the project associated with this <see cref="IDirectoryCacheFactory"/>.
        /// </summary>
        /// <param name="evaluationId">The ID of the evaluation for which the interface is requested.</param>
        IDirectoryCache GetDirectoryCacheForEvaluation(int evaluationId);
    }

    /// <summary>
    /// A predicate taking file name.
    /// </summary>
    /// <param name="fileName">The file name to check.</param>
    public delegate bool FindPredicate(ref ReadOnlySpan<char> fileName);

    /// <summary>
    /// A function taking file name and returning an arbitrary result.
    /// </summary>
    /// <typeparam name="TResult">The type of the result to return</typeparam>
    /// <param name="fileName">The file name to transform.</param>
    public delegate TResult FindTransform<TResult>(ref ReadOnlySpan<char> fileName);

    /// <summary>
    /// Allows the implementor to intercept file existence checks and file enumerations performed during project evaluation.
    /// </summary>
    public interface IDirectoryCache
    {
        /// <summary>
        /// Returns <code>true</code> if the given path points to an existing file on disk.
        /// </summary>
        /// <param name="path">A normalized path.</param>
        bool FileExists(string path);

        /// <summary>
        /// Returns <code>true</code> if the given path points to an existing directory on disk.
        /// </summary>
        /// <param name="path">A normalized path.</param>
        bool DirectoryExists(string path);

        /// <summary>
        /// Enumerates files in the given directory only (non-recursively).
        /// </summary>
        /// <typeparam name="TResult">The desired return type.</typeparam>
        /// <param name="path">The directory to enumerate.</param>
        /// <param name="pattern">A search pattern supported by the platform which is guaranteed to return a superset of relevant files.</param>
        /// <param name="predicate">A predicate to test whether a file should be included.</param>
        /// <param name="transform">A transform from <code>ReadOnlySpan&lt;char&gt;</code> to <typeparamref name="TResult"/>.</param>
        /// <remarks>
        /// The <paramref name="pattern"/> parameter may match more files than what the caller is interested in. In other words,
        /// <paramref name="predicate"/> can return <code>false</code> even if the implementation enumerates only files whose names
        /// match the pattern. The implementation is free to ignore the pattern and call the predicate for all files on the given
        /// <paramref name="path"/>.
        /// </remarks>
        IEnumerable<TResult> EnumerateFiles<TResult>(string path, string pattern, FindPredicate predicate, FindTransform<TResult> transform);

        /// <summary>
        /// Enumerates subdirectories in the given directory only (non-recursively).
        /// </summary>
        /// <typeparam name="TResult">The desired return type.</typeparam>
        /// <param name="path">The directory to enumerate.</param>
        /// <param name="pattern">A search pattern supported by the platform which is guaranteed to return a superset of relevant directories.</param>
        /// <param name="predicate">A predicate to test whether a directory should be included.</param>
        /// <param name="transform">A transform from <code>ReadOnlySpan&lt;char&gt;</code> to <typeparamref name="TResult"/>.</param>
        /// <remarks>
        /// The <paramref name="pattern"/> parameter may match more direcories than what the caller is interested in. In other words,
        /// <paramref name="predicate"/> can return <code>false</code> even if the implementation enumerates only directories whose names
        /// match the pattern. The implementation is free to ignore the pattern and call the predicate for all directories on the given
        /// <paramref name="path"/>.
        /// </remarks>
        IEnumerable<TResult> EnumerateDirectories<TResult>(string path, string pattern, FindPredicate predicate, FindTransform<TResult> transform);
    }
}
