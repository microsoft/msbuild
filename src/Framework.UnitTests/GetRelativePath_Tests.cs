// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Xunit;

namespace Microsoft.Build.Framework.UnitTests
{
    // Test cases taken from .NET 7.0 runtime.
    public static class GetRelativePath_Tests
    {
        [Theory]
        [InlineData(@"C:\", @"C:\", @".")]
        [InlineData(@"C:\a", @"C:\a\", @".")]
        [InlineData(@"C:\a\", @"C:\a", @".")]
        [InlineData(@"C:\", @"C:\b", @"b")]
        [InlineData(@"C:\a", @"C:\b", @"..\b")]
        [InlineData(@"C:\a", @"C:\b\", @"..\b\")]
        [InlineData(@"C:\a\b", @"C:\a", @"..")]
        [InlineData(@"C:\a\b", @"C:\a\", @"..")]
        [InlineData(@"C:\a\b\", @"C:\a", @"..")]
        [InlineData(@"C:\a\b\", @"C:\a\", @"..")]
        [InlineData(@"C:\a\b\c", @"C:\a\b", @"..")]
        [InlineData(@"C:\a\b\c", @"C:\a\b\", @"..")]
        [InlineData(@"C:\a\b\c", @"C:\a", @"..\..")]
        [InlineData(@"C:\a\b\c", @"C:\a\", @"..\..")]
        [InlineData(@"C:\a\b\c\", @"C:\a\b", @"..")]
        [InlineData(@"C:\a\b\c\", @"C:\a\b\", @"..")]
        [InlineData(@"C:\a\b\c\", @"C:\a", @"..\..")]
        [InlineData(@"C:\a\b\c\", @"C:\a\", @"..\..")]
        [InlineData(@"C:\a\", @"C:\b", @"..\b")]
        [InlineData(@"C:\a", @"C:\a\b", @"b")]
        [InlineData(@"C:\a", @"C:\b\c", @"..\b\c")]
        [InlineData(@"C:\a\", @"C:\a\b", @"b")]
        [InlineData(@"C:\", @"D:\", @"D:\")]
        [InlineData(@"C:\", @"D:\b", @"D:\b")]
        [InlineData(@"C:\", @"D:\b\", @"D:\b\")]
        [InlineData(@"C:\a", @"D:\b", @"D:\b")]
        [InlineData(@"C:\a\", @"D:\b", @"D:\b")]
        [InlineData(@"C:\ab", @"C:\a", @"..\a")]
        [InlineData(@"C:\a", @"C:\ab", @"..\ab")]
        [InlineData(@"C:\", @"\\LOCALHOST\Share\b", @"\\LOCALHOST\Share\b")]
        [InlineData(@"\\LOCALHOST\Share\a", @"\\LOCALHOST\Share\b", @"..\b")]
        [PlatformSpecific(TestPlatforms.Windows)] // Tests Windows-specific paths
        public static void GetRelativePath_Windows(string relativeTo, string path, string expected)
        {
            string result = NativeMethods.GetRelativePath(relativeTo, path);
            Assert.Equal(expected, result);

            // Check that we get the equivalent path when the result is combined with the sources
            Assert.Equal(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.Combine(Path.GetFullPath(relativeTo), result)).TrimEnd(Path.DirectorySeparatorChar),
                ignoreCase: true,
                ignoreLineEndingDifferences: false,
                ignoreWhiteSpaceDifferences: false);
        }

        [Theory]
        [InlineData(@"/", @"/", @".")]
        [InlineData(@"/a", @"/a/", @".")]
        [InlineData(@"/a/", @"/a", @".")]
        [InlineData(@"/", @"/b", @"b")]
        [InlineData(@"/a", @"/b", @"../b")]
        [InlineData(@"/a/", @"/b", @"../b")]
        [InlineData(@"/a", @"/a/b", @"b")]
        [InlineData(@"/a", @"/b/c", @"../b/c")]
        [InlineData(@"/a/", @"/a/b", @"b")]
        [InlineData(@"/ab", @"/a", @"../a")]
        [InlineData(@"/a", @"/ab", @"../ab")]
        [PlatformSpecific(TestPlatforms.AnyUnix & ~TestPlatforms.Browser)] // Tests Unix-specific paths
        public static void GetRelativePath_AnyUnix(string relativeTo, string path, string expected)
        {
            string result = NativeMethods.GetRelativePath(relativeTo, path);

            // Somehow the PlatformSpecific seems to be ignored inside Visual Studio
            result = result.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Assert.Equal(expected, result);

            // Check that we get the equivalent path when the result is combined with the sources
            Assert.Equal(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.Combine(Path.GetFullPath(relativeTo), result)).TrimEnd(Path.DirectorySeparatorChar));
        }
    }
}
