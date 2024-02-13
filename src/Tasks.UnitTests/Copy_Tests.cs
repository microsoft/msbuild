// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
#if FEATURE_SECURITY_PERMISSIONS
using System.Security.AccessControl;
#endif

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public class Copy_Tests : IDisposable
    {
        public static IEnumerable<object[]> GetDestinationExists() =>
            new List<object[]>
            {
                new object[] { true },
                new object[] { false },
            };

        public static IEnumerable<object[]> GetNullAndEmptyArrays() =>
            new List<object[]>
            {
                new object[] { null },
                new object[] { Array.Empty<ITaskItem>() },
            };

        /// <summary>
        /// Gets data for testing with combinations of isUseHardLinks and isUseSymbolicLinks.
        /// Index 0 is the value for isUseHardLinks.
        /// Index 1 is the value for isUseSymbolicLinks.
        /// </summary>
        public static IEnumerable<object[]> GetHardLinksSymLinks() => new List<object[]>
        {
            new object[] { false, false },
            new object[] { false, true },
            new object[] { true, false },

            /* Cases not covered
            new object[] { true, true },
            */
        };

        /// <summary>
        /// Gets data for testing with combinations of isUseHardLinks, isUseSymbolicLinks, and isUseSingleThreadedCopy.
        /// Index 0 is the value for isUseHardLinks.
        /// Index 1 is the value for isUseSymbolicLinks.
        /// Index 2 is the value for isUseSingleThreadedCopy.
        /// </summary>
        public static IEnumerable<object[]> GetHardLinksSymLinksSingleThreaded() => new List<object[]>
        {
            new object[] { false, false, false },
            new object[] { false, false, true },
            new object[] { false, true, false },
            new object[] { true, false, false },

            /* Cases not covered
            new object[] { false, true, true },
            new object[] { true, false, true },
            new object[] { true, true, false },
            new object[] { true, true, true },
            */
        };

        private const int NoParallelismThreadCount = 1;
        private const int DefaultParallelismThreadCount = int.MaxValue;

        private int GetParallelismThreadCount(bool isUseSingleThreadedCopy) => isUseSingleThreadedCopy ? NoParallelismThreadCount : DefaultParallelismThreadCount;

        /// <summary>
        /// Temporarily save off the value of MSBUILDALWAYSOVERWRITEREADONLYFILES, so that we can run
        /// the tests isolated from the current state of the environment, but put it back how it belongs
        /// once we're done.
        /// </summary>
        private readonly string _alwaysOverwriteReadOnlyFiles;

        /// <summary>
        /// Temporarily save off the value of MSBUILDALWAYSRETRY, so that we can run
        /// the tests isolated from the current state of the environment, but put it back how it belongs
        /// once we're done.
        /// </summary>
        private readonly string _alwaysRetry;

        private readonly ITestOutputHelper _testOutputHelper;

        /// <summary>
        /// There are a couple of environment variables that can affect the operation of the Copy
        /// task.  Make sure none of them are set.
        /// </summary>
        public Copy_Tests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            _alwaysOverwriteReadOnlyFiles = Environment.GetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar);
            _alwaysRetry = Environment.GetEnvironmentVariable(Copy.AlwaysRetryEnvVar);

            Environment.SetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar, String.Empty);
            Environment.SetEnvironmentVariable(Copy.AlwaysRetryEnvVar, String.Empty);

            Copy.RefreshInternalEnvironmentValues();
        }

        /// <summary>
        /// Restore the environment variables we cleared out at the beginning of the test.
        /// </summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar, _alwaysOverwriteReadOnlyFiles);
            Environment.SetEnvironmentVariable(Copy.AlwaysRetryEnvVar, _alwaysRetry);

            Copy.RefreshInternalEnvironmentValues();
        }

        [Fact]
        public void CopyWithNoInput()
        {
            var task = new Copy { BuildEngine = new MockEngine(true), };
            task.Execute().ShouldBeTrue();
            (task.CopiedFiles == null || task.CopiedFiles.Length == 0).ShouldBeTrue();
            (task.DestinationFiles == null || task.DestinationFiles.Length == 0).ShouldBeTrue();
            task.WroteAtLeastOneFile.ShouldBeFalse();
        }

        [Fact]
        public void CopyWithMatchingSourceFilesToDestinationFiles()
        {
            using (var env = TestEnvironment.Create())
            {
                var sourceFile = env.CreateFile("source.txt");

                var task = new Copy
                {
                    BuildEngine = new MockEngine(true),
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination.txt") },
                    RetryDelayMilliseconds = 1,
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(1);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(1);
                task.WroteAtLeastOneFile.ShouldBeTrue();
            }
        }

        [Theory]
        [MemberData(nameof(GetDestinationExists))]
        public void CopyWithSourceFilesToDestinationFolder(bool isDestinationExists)
        {
            using (var env = TestEnvironment.Create())
            {
                var sourceFile = env.CreateFile("source.txt");
                var destinationFolder = env.CreateFolder(isDestinationExists);

                var task = new Copy
                {
                    BuildEngine = new MockEngine(true),
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                    RetryDelayMilliseconds = 1,
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(1);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(1);
                task.WroteAtLeastOneFile.ShouldBeTrue();
            }
        }

        [Theory]
        [MemberData(nameof(GetDestinationExists))]
        public void CopyWithSourceFoldersToDestinationFolder(bool isDestinationExists)
        {
            using (var env = TestEnvironment.Create())
            {
                var s0Folder = env.DefaultTestDirectory.CreateDirectory("source0");
                s0Folder.CreateFile("00.txt");
                s0Folder.CreateFile("01.txt");
                var s0AFolder = s0Folder.CreateDirectory("a");
                s0AFolder.CreateFile("a0.txt");
                s0AFolder.CreateFile("a1.txt");
                _ = s0Folder.CreateDirectory("b");
                var s0CFolder = s0Folder.CreateDirectory("c");
                s0CFolder.CreateFile("c0.txt");

                var s1Folder = env.DefaultTestDirectory.CreateDirectory("source1");
                s1Folder.CreateFile("10.txt");
                s1Folder.CreateFile("11.txt");
                var s1AFolder = s1Folder.CreateDirectory("a");
                s1AFolder.CreateFile("a0.txt");
                s1AFolder.CreateFile("a1.txt");
                var s1BFolder = s1Folder.CreateDirectory("b");
                s1BFolder.CreateFile("b0.txt");

                var destinationFolder = env.CreateFolder(isDestinationExists);

                var task = new Copy
                {
                    BuildEngine = new MockEngine(true),
                    SourceFolders = new ITaskItem[] { new TaskItem(s0Folder.Path), new TaskItem(s1Folder.Path) },
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                    RetryDelayMilliseconds = 1,
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(10);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(10);
                task.WroteAtLeastOneFile.ShouldBeTrue();
                Directory.Exists(Path.Combine(destinationFolder.Path, "source0")).ShouldBeTrue();
                Directory.Exists(Path.Combine(destinationFolder.Path, "source1")).ShouldBeTrue();
            }
        }

        [Fact]
        public void CopyWithNoSource()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var destinationFolder = env.CreateFolder(true);

                var task = new Copy
                {
                    BuildEngine = engine,
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(0);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(0);
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Theory]
        [MemberData(nameof(GetDestinationExists))]
        public void CopyWithMultipleSourceTypes(bool isDestinationExists)
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");
                var sourceFolder = env.DefaultTestDirectory.CreateDirectory("source");
                sourceFolder.CreateFile("source.txt");
                var aDirectory = sourceFolder.CreateDirectory("a");
                aDirectory.CreateFile("a.txt");
                sourceFolder.CreateDirectory("b");
                var destinationFolder = env.CreateFolder(isDestinationExists);

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    SourceFolders = new ITaskItem[] { new TaskItem(sourceFolder.Path) },
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(3);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(3);
                task.WroteAtLeastOneFile.ShouldBeTrue();
            }
        }

        [Theory]
        [MemberData(nameof(GetNullAndEmptyArrays))]
        public void CopyWithEmptySourceFiles(ITaskItem[] sourceFiles)
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var destinationFolder = env.CreateFolder(true);

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(0);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(0);
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Theory]
        [MemberData(nameof(GetNullAndEmptyArrays))]
        public void CopyWithEmptySourceFolders(ITaskItem[] sourceFolders)
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var destinationFolder = env.CreateFolder(true);

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFolders = sourceFolders,
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                };
                task.Execute().ShouldBeTrue();
                task.CopiedFiles.ShouldNotBeNull();
                task.CopiedFiles.Length.ShouldBe(0);
                task.DestinationFiles.ShouldNotBeNull();
                task.DestinationFiles.Length.ShouldBe(0);
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Theory]
        [MemberData(nameof(GetNullAndEmptyArrays))]
        public void CopyWithNoDestination(ITaskItem[] destinationFiles)
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = destinationFiles,
                };
                task.Execute().ShouldBeFalse();
                // Copy.NeedsDestination (MSB3023) or General.TwoVectorsMustHaveSameLength (MSB3094)
                engine.AssertLogContains(destinationFiles == null ? "MSB3023" : "MSB3094");
                task.CopiedFiles.ShouldBeNull();
                (task.DestinationFiles == null || task.DestinationFiles.Length == 0).ShouldBeTrue();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Fact]
        public void CopyWithMultipleDestinationTypes()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");
                var destinationFolder = env.CreateFolder(true);

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination.txt") },
                    DestinationFolder = new TaskItem(destinationFolder.Path),
                };
                task.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3022"); // Copy.ExactlyOneTypeOfDestination
                task.CopiedFiles.ShouldBeNull();
                task.DestinationFiles.ShouldNotBeNull();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Fact]
        public void CopyWithSourceFoldersAndDestinationFiles()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");
                var sourceFolder = env.CreateFolder(true);

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    SourceFolders = new ITaskItem[] { new TaskItem(sourceFolder.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination0.txt"), new TaskItem("destination1.txt") },
                };
                task.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3896"); // Copy.IncompatibleParameters
                task.CopiedFiles.ShouldBeNull();
                task.DestinationFiles.ShouldNotBeNull();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        [Fact]
        public void CopyWithDifferentLengthSourceFilesToDestinationFiles()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination0.txt"), new TaskItem("destination1.txt") },
                };
                task.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3094"); // General.TwoVectorsMustHaveSameLength
                task.CopiedFiles.ShouldBeNull();
                task.DestinationFiles.ShouldNotBeNull();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        /// <summary>
        /// Verifies that we error for retries less than 0
        /// </summary>
        [Fact]
        public void CopyWithInvalidRetryCount()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination.txt") },
                    Retries = -1,
                };
                task.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3028"); // Copy.InvalidRetryCount
                task.CopiedFiles.ShouldBeNull();
                task.DestinationFiles.ShouldNotBeNull();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        /// <summary>
        /// Verifies that we error for retry delay less than 0
        /// </summary>
        [Fact]
        public void CopyWithInvalidRetryDelay()
        {
            using (var env = TestEnvironment.Create())
            {
                var engine = new MockEngine(true);
                var sourceFile = env.CreateFile("source.txt");

                var task = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile.Path) },
                    DestinationFiles = new ITaskItem[] { new TaskItem("destination.txt") },
                    RetryDelayMilliseconds = -1,
                };
                task.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3029"); // Copy.InvalidRetryDelay
                task.CopiedFiles.ShouldBeNull();
                task.DestinationFiles.ShouldNotBeNull();
                task.WroteAtLeastOneFile.ShouldBeFalse();
            }
        }

        /// <summary>
        /// If OnlyCopyIfDifferent is set to "true" then we shouldn't copy over files that
        /// have the same date and time.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void DontCopyOverSameFile(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string file = FileUtilities.GetTemporaryFile();
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(file, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a temp file.");
                }

                ITaskItem f = new TaskItem(file);
                ITaskItem[] sourceFiles = { f };
                ITaskItem[] destinationFiles = { f };

                CopyMonitor m = new CopyMonitor();
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                t.Execute(m.CopyFile, GetParallelismThreadCount(isUseSingleThreadedCopy));

                // Expect for there to have been no copies.
                Assert.Equal(0, m.copyCount);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Question should not copy any files.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void QuestionCopyFile(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile(null, ".tmp", false);
            string content = "This is a source file.";

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write(content);
                }

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                CopyMonitor m = new CopyMonitor();
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    FailIfNotIncremental = true,
                };

                Assert.False(t.Execute(m.CopyFile, GetParallelismThreadCount(isUseSingleThreadedCopy)));

                // Expect for there to have been no copies.
                Assert.Equal(0, m.copyCount);

                Assert.False(FileUtilities.FileExistsNoThrow(destination));
            }
            finally
            {
                File.Delete(source);
            }
        }

        /// <summary>
        /// Question copy should not error if copy did no work.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void QuestionCopyFileSameContent(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            string content = "This is a source file.";
            DateTime testTime = DateTime.Now;

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write(content);
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write(content);
                }

                FileInfo sourcefi = new FileInfo(source);
                sourcefi.LastWriteTimeUtc = testTime;

                FileInfo destinationfi = new FileInfo(destination);
                destinationfi.LastWriteTimeUtc = testTime;

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                CopyMonitor m = new CopyMonitor();
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    SkipUnchangedFiles = true,
                    FailIfNotIncremental = true,
                };
                Assert.True(t.Execute(m.CopyFile, GetParallelismThreadCount(isUseSingleThreadedCopy)));

                // Expect for there to have been no copies.
                Assert.Equal(0, m.copyCount);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// Question copy should error if a copy will occur.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void QuestionCopyFileNotSameContent(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write("This is a source file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write("This is a destination file.");
                }

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                CopyMonitor m = new CopyMonitor();
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    SkipUnchangedFiles = true,
                    FailIfNotIncremental = true,
                };

                Assert.False(t.Execute(m.CopyFile, GetParallelismThreadCount(isUseSingleThreadedCopy)));

                // Expect for there to have been no copies.
                Assert.Equal(0, m.copyCount);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// Unless ignore readonly attributes is set, we should not copy over readonly files.
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotNormallyCopyOverReadOnlyFile(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write("This is a source file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write("This is a destination file.");
                }

                File.SetAttributes(destination, FileAttributes.ReadOnly);

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    // OverwriteReadOnlyFiles defaults to false
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                // Should fail: target is readonly
                Assert.False(t.Execute());

                // Expect for there to have been no copies.
                Assert.Empty(t.CopiedFiles);

                string destinationContent = File.ReadAllText(destination);
                Assert.Equal("This is a destination file.", destinationContent);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // did not do retries as it was r/o
            }
            finally
            {
                File.SetAttributes(source, FileAttributes.Normal);
                File.SetAttributes(destination, FileAttributes.Normal);
                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// If MSBUILDALWAYSOVERWRITEREADONLYFILES is set, then overwrite read-only even when
        /// OverwriteReadOnlyFiles is false
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyOverReadOnlyFileEnvironmentOverride(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            string oldAlwaysOverwriteValue = Environment.GetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar, "1   ");

                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write("This is a source file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write("This is a destination file.");
                }

                File.SetAttributes(destination, FileAttributes.ReadOnly);

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    OverwriteReadOnlyFiles = false,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                // Should not fail although target is readonly
                Assert.True(t.Execute());

                // Should have copied file anyway
                Assert.Single(t.CopiedFiles);

                string destinationContent = File.ReadAllText(destination);
                Assert.Equal("This is a source file.", destinationContent);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                Environment.SetEnvironmentVariable(Copy.AlwaysOverwriteReadOnlyFilesEnvVar, oldAlwaysOverwriteValue);

                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// If MSBUILDALWAYSRETRY is set, keep retrying the copy.
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void AlwaysRetryCopyEnvironmentOverride(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            string oldAlwaysRetryValue = Environment.GetEnvironmentVariable(Copy.AlwaysRetryEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(Copy.AlwaysRetryEnvVar, "1   ");
                Copy.RefreshInternalEnvironmentValues();

                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write("This is a source file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write("This is a destination file.");
                }

                File.SetAttributes(destination, FileAttributes.ReadOnly);

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    OverwriteReadOnlyFiles = false,
                    Retries = 5,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                // The file is read-only, so the retries will all fail.
                Assert.False(t.Execute());

                // 3 warnings per retry, except the last one which has only two.
                ((MockEngine)t.BuildEngine).AssertLogContains("MSB3026");
                Assert.Equal(((t.Retries + 1) * 3) - 1, ((MockEngine)t.BuildEngine).Warnings);

                // One error for "retrying failed", one error for "copy failed"
                ((MockEngine)t.BuildEngine).AssertLogContains("MSB3027");
                ((MockEngine)t.BuildEngine).AssertLogContains("MSB3021");
                Assert.Equal(2, ((MockEngine)t.BuildEngine).Errors);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Copy.AlwaysRetryEnvVar, oldAlwaysRetryValue);
                Copy.RefreshInternalEnvironmentValues();

                File.SetAttributes(destination, FileAttributes.Normal);

                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// Unless ignore readonly attributes is set, we should not copy over readonly files.
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyOverReadOnlyFileParameterIsSet(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string source = FileUtilities.GetTemporaryFile();
            string destination = FileUtilities.GetTemporaryFile();
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(source, true))
                {
                    sw.Write("This is a source file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination, true))
                {
                    sw.Write("This is a destination file.");
                }

                File.SetAttributes(destination, FileAttributes.ReadOnly);

                ITaskItem sourceItem = new TaskItem(source);
                ITaskItem destinationItem = new TaskItem(destination);
                ITaskItem[] sourceFiles = { sourceItem };
                ITaskItem[] destinationFiles = { destinationItem };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    OverwriteReadOnlyFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                // Should not fail although target is readonly
                Assert.True(t.Execute());

                // Should have copied file anyway
                Assert.Single(t.CopiedFiles);

                string destinationContent = File.ReadAllText(destination);
                Assert.Equal("This is a source file.", destinationContent);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(source);
                File.Delete(destination);
            }
        }

        /// <summary>
        /// Unless ignore readonly attributes is set, we should not copy over readonly files.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyOverReadOnlyFileParameterIsSetWithDestinationFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string source1 = FileUtilities.GetTemporaryFile();
            string source2 = FileUtilities.GetTemporaryFile();
            string destinationFolder = Path.Combine(Path.GetTempPath(), "2A333ED756AF4dc392E728D0F874A398");
            string destination1 = Path.Combine(destinationFolder, Path.GetFileName(source1));
            string destination2 = Path.Combine(destinationFolder, Path.GetFileName(source2));
            try
            {
                Directory.CreateDirectory(destinationFolder);

                using (StreamWriter sw = FileUtilities.OpenWrite(source1, true))
                {
                    sw.Write("This is a source file1.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(source2, true))
                {
                    sw.Write("This is a source file2.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination1, true))
                {
                    sw.Write("This is a destination file1.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destination2, true))
                {
                    sw.Write("This is a destination file2.");
                }

                // Set one destination readonly.
                File.SetAttributes(destination1, FileAttributes.ReadOnly);

                ITaskItem sourceItem1 = new TaskItem(source1);
                ITaskItem sourceItem2 = new TaskItem(source2);
                ITaskItem[] sourceFiles = new ITaskItem[] { sourceItem1, sourceItem2 };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destinationFolder),
                    OverwriteReadOnlyFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                // Should not fail although one target is readonly
                Assert.True(t.Execute());

                // Should have copied files anyway
                Assert.Equal(2, t.CopiedFiles.Length);

                string destinationContent1 = File.ReadAllText(destination1);
                Assert.Equal("This is a source file1.", destinationContent1);
                string destinationContent2 = File.ReadAllText(destination2);
                Assert.Equal("This is a source file2.", destinationContent2);

                Assert.NotEqual(FileAttributes.ReadOnly, File.GetAttributes(destination1) & FileAttributes.ReadOnly);
                Assert.NotEqual(FileAttributes.ReadOnly, File.GetAttributes(destination2) & FileAttributes.ReadOnly);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.SetAttributes(destination1, FileAttributes.Normal); // just in case
                File.SetAttributes(destination2, FileAttributes.Normal); // just in case
                File.Delete(source1);
                File.Delete(source2);
                File.Delete(destination1);
                File.Delete(destination2);
                FileUtilities.DeleteWithoutTrailingBackslash(destinationFolder, true);
            }
        }

        /*
         * Method:   DoCopyOverDifferentFile
         *
         * If OnlyCopyIfDifferent is set to "true" then we should still copy over files that
         * have different dates or sizes.
         */
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoCopyOverDifferentFile(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string destinationFile = FileUtilities.GetTemporaryFile();
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destinationFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                t.Execute();

                string destinationFileContents;
                using (StreamReader sr = FileUtilities.OpenRead(destinationFile)) // HIGHCHAR: Test reads ASCII (not ANSI).
                {
                    destinationFileContents = sr.ReadToEnd();
                }

                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination file to contain the contents of source file."

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile);
                File.Delete(destinationFile);
            }
        }

        /*
         * Method:   DoCopyOverCopiedFile
         *
         * If SkipUnchangedFiles is set to "false" then we should always copy over files that have same dates and sizes.
         * If SkipUnchangedFiles is set to "true" then we should never copy over files that have same dates and sizes.
         */
        [Theory(Skip = "https://github.com/dotnet/msbuild/issues/4126")]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void DoCopyOverCopiedFile(bool skipUnchangedFiles, bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            using (var env = TestEnvironment.Create())
            {
                var sourceFile = FileUtilities.GetTemporaryFile(env.DefaultTestDirectory.Path, null, "src", false);
                var destinationFile = FileUtilities.GetTemporaryFile(env.DefaultTestDirectory.Path, null, "dst", false);

                File.WriteAllText(sourceFile, "This is a source temp file.");

                // run copy twice, so we test if we are able to overwrite previously copied (or linked) file
                for (var i = 0; i < 2; i++)
                {
                    var engine = new MockEngine(_testOutputHelper);
                    var t = new Copy
                    {
                        RetryDelayMilliseconds = 1,  // speed up tests!
                        BuildEngine = engine,
                        SourceFiles = new[] { new TaskItem(sourceFile) },
                        DestinationFiles = new[] { new TaskItem(destinationFile) },
                        SkipUnchangedFiles = skipUnchangedFiles,
                        UseHardlinksIfPossible = isUseHardLinks,
                        UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    };

                    var success = t.Execute();
                    Assert.True(success);

                    var shouldNotCopy = skipUnchangedFiles &&
                                        i == 1 &&
                                        // SkipUnchanged check will always fail for symbolic links,
                                        // because we compare attributes of real file with attributes of symbolic link.
                                        !isUseSymbolicLinks &&
                                        // On Windows and MacOS File.Copy already preserves LastWriteTime, but on Linux extra step is needed.
                                        // TODO - this need to be fixed on Linux
                                        (!NativeMethodsShared.IsLinux || isUseHardLinks);

                    if (shouldNotCopy)
                    {
                        engine.AssertLogContainsMessageFromResource(AssemblyResources.GetString,
                            "Copy.DidNotCopyBecauseOfFileMatch",
                            sourceFile,
                            destinationFile,
                            "SkipUnchangedFiles",
                            "true");
                    }
                    else
                    {
                        engine.AssertLogDoesntContainMessageFromResource(AssemblyResources.GetString,
                            "Copy.DidNotCopyBecauseOfFileMatch",
                            sourceFile,
                            destinationFile,
                            "SkipUnchangedFiles",
                            "true");
                    }

                    // "Expected the destination file to contain the contents of source file."
                    Assert.Equal("This is a source temp file.", File.ReadAllText(destinationFile));
                    engine.AssertLogDoesntContain("MSB3026"); // Didn't do retries
                }
            }
        }

        /*
         * Method:   DoCopyOverNonExistentFile
         *
         * If OnlyCopyIfDifferent is set to "true" then we should still copy over files that
         * don't exist.
         */
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoCopyOverNonExistentFile(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string destinationFile = FileUtilities.GetTemporaryFile();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }

                using (StreamWriter sw = FileUtilities.OpenWrite(destinationFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                File.Delete(destinationFile);

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                t.Execute();

                Assert.True(File.Exists(destinationFile)); // "Expected the destination file to exist."
                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile);
                File.Delete(destinationFile);
            }
        }

        /// <summary>
        /// Make sure we do not retry when the source file has a misplaced colon
        /// </summary>
        [WindowsFullFrameworkOnlyTheory(additionalMessage: ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486. Colon is special only on Windows.")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryCopyNotSupportedException(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string destinationFile = "foobar:";

            try
            {
                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);
                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
                engine.AssertLogContains("MSB3021");
                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile);
            }
        }

        /// <summary>
        /// Make sure we do not retry when the source file does not exist
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryCopyNonExistentSourceFile(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = "Nannanacat";
            string destinationFile = FileUtilities.GetTemporaryFile();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(destinationFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                File.Delete(destinationFile);

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);
                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
                engine.AssertLogContains("MSB3030");
                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(destinationFile);
            }
        }

        /// <summary>
        /// Make sure we do not retry when the source file is a folder
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryCopyWhenSourceIsFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = Path.GetTempPath();
            string destinationFile = FileUtilities.GetTemporaryFile();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(destinationFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                File.Delete(destinationFile);

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);
                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
                engine.AssertLogContains("MSB3025");
                engine.AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(destinationFile);
            }
        }

        /// <summary>
        /// Most important case is when destination is locked
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoRetryWhenDestinationLocked(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string destinationFile = Path.GetTempFileName();
            string sourceFile = Path.GetTempFileName();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(destinationFile, true)) // Keep it locked
                {
                    ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                    var engine = new MockEngine(_testOutputHelper);
                    var t = new Copy
                    {
                        RetryDelayMilliseconds = 1,  // speed up tests!
                        BuildEngine = engine,
                        SourceFiles = sourceFiles,
                        DestinationFiles = new ITaskItem[] { new TaskItem(destinationFile) },
                        UseHardlinksIfPossible = isUseHardLinks,
                        UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    };

                    bool result = t.Execute();
                    Assert.False(result);

                    engine.AssertLogContains("MSB3021"); // copy failed
                    engine.AssertLogContains("MSB3026"); // DID retry

                    if (NativeMethodsShared.IsWindows)
                    {
                        engine.AssertLogContains(Process.GetCurrentProcess().Id.ToString()); // the file is locked by the current process
                    }

                    Assert.Equal(2, engine.Errors); // retries failed and the actual failure
                    Assert.Equal(10, engine.Warnings);
                }
            }
            finally
            {
                File.Delete(sourceFile);
                File.Delete(destinationFile);
            }
        }

#if FEATURE_SECURITY_PERMISSIONS
        /// <summary>
        /// When destination is inaccessible due to ACL, do NOT retry
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryWhenDestinationLockedDueToAcl(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "DoNotRetryWhenDestinationLockedDueToAcl");
            string destinationFile = Path.Combine(tempDirectory, "DestinationFile.txt");
            string sourceFile = Path.Combine(tempDirectory, "SourceFile.txt");

            if (Directory.Exists(tempDirectory))
            {
                FileUtilities.DeleteDirectoryNoThrow(tempDirectory, true);
            }

            Directory.CreateDirectory(tempDirectory);

            File.WriteAllText(destinationFile, "Destination");
            File.WriteAllText(sourceFile, "SourceFile");

            string userAccount = $@"{Environment.UserDomainName}\{Environment.UserName}";

            var denyFile = new FileSystemAccessRule(userAccount, FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteData, AccessControlType.Deny);
            var denyDirectory = new FileSystemAccessRule(userAccount, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);

            FileSecurity fSecurity = File.GetAccessControl(destinationFile);
            DirectorySecurity dSecurity = Directory.GetAccessControl(tempDirectory);

            try
            {
                fSecurity.AddAccessRule(denyFile);
                File.SetAccessControl(destinationFile, fSecurity);

                dSecurity.AddAccessRule(denyDirectory);
                Directory.SetAccessControl(tempDirectory, dSecurity);

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(sourceFile) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(destinationFile) },
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);

                engine.AssertLogContains("MSB3021"); // copy failed
                engine.AssertLogDoesntContain("MSB3026"); // Didn't retry

                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
            }
            finally
            {
                fSecurity.RemoveAccessRule(denyFile);
                File.SetAccessControl(destinationFile, fSecurity);

                dSecurity.RemoveAccessRule(denyDirectory);
                Directory.SetAccessControl(tempDirectory, dSecurity);

                if (Directory.Exists(tempDirectory))
                {
                    FileUtilities.DeleteDirectoryNoThrow(tempDirectory, true);
                }
            }
        }
#endif

        /// <summary>
        /// Make sure we do not retry when the destination file is a folder
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryCopyWhenDestinationFolderIsFile(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string destinationFile = FileUtilities.GetTemporaryFile();
            string sourceFile = FileUtilities.GetTemporaryFile();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true))
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destinationFile),
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);

                engine.AssertLogContains("MSB3021"); // copy failed
                engine.AssertLogDoesntContain("MSB3026"); // Didn't retry

                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
            }
            finally
            {
                File.Delete(sourceFile);
            }
        }

        /// <summary>
        /// Make sure we do not retry when the destination file is a folder
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotRetryCopyWhenDestinationFileIsFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string destinationFile = Path.GetTempPath();
            string sourceFile = FileUtilities.GetTemporaryFile();

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true))   // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a destination temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();
                Assert.False(result);
                Assert.Equal(1, engine.Errors);
                Assert.Equal(0, engine.Warnings);
                engine.AssertLogContains("MSB3024");
                engine.AssertLogDoesntContain("MSB3026");
            }
            finally
            {
                File.Delete(sourceFile);
            }
        }

        /// <summary>
        /// CopiedFiles should only include files that were successfully copied
        /// (or skipped), not files for which there was an error.
        /// </summary>
        [WindowsFullFrameworkOnlyTheory(additionalMessage: ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486. Under Unix all filenames are valid and this test is not useful.")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void OutputsOnlyIncludeSuccessfulCopies(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string temp = Path.GetTempPath();
            string inFile1 = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A392");
            string inFile2 = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A393");
            const string invalidFile = "!@#$%^&*()|";
            string validOutFile = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A394");

            try
            {
                FileStream fs = null;
                FileStream fs2 = null;

                try
                {
                    fs = File.Create(inFile1);
                    fs2 = File.Create(inFile2);
                }
                finally
                {
                    fs?.Dispose();
                    fs2?.Dispose();
                }

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                ITaskItem i1 = new TaskItem(inFile1);
                i1.SetMetadata("Locale", "en-GB");
                i1.SetMetadata("Color", "taupe");
                t.SourceFiles = new[] { new TaskItem(inFile2), i1 };

                ITaskItem o1 = new TaskItem(validOutFile);
                o1.SetMetadata("Locale", "fr");
                o1.SetMetadata("Flavor", "Pumpkin");
                t.DestinationFiles = new[] { new TaskItem(invalidFile), o1 };

                bool success = t.Execute();

                Assert.False(success);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(validOutFile, t.CopiedFiles[0].ItemSpec);
                Assert.Equal(2, t.DestinationFiles.Length);
                Assert.Equal("fr", t.DestinationFiles[1].GetMetadata("Locale"));

                // Output ItemSpec should not be overwritten.
                Assert.Equal(invalidFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(validOutFile, t.DestinationFiles[1].ItemSpec);
                Assert.Equal(validOutFile, t.CopiedFiles[0].ItemSpec);

                // Sources attributes should be left untouched.
                Assert.Equal("en-GB", t.SourceFiles[1].GetMetadata("Locale"));
                Assert.Equal("taupe", t.SourceFiles[1].GetMetadata("Color"));

                // Attributes not on Sources should be left untouched.
                Assert.Equal("Pumpkin", t.DestinationFiles[1].GetMetadata("Flavor"));
                Assert.Equal("Pumpkin", t.CopiedFiles[0].GetMetadata("Flavor"));

                // Attribute should have been forwarded
                Assert.Equal("taupe", t.DestinationFiles[1].GetMetadata("Color"));
                Assert.Equal("taupe", t.CopiedFiles[0].GetMetadata("Color"));

                // Attribute should not have been updated if it already existed on destination
                Assert.Equal("fr", t.DestinationFiles[1].GetMetadata("Locale"));
                Assert.Equal("fr", t.CopiedFiles[0].GetMetadata("Locale"));

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(inFile1);
                File.Delete(inFile2);
                File.Delete(validOutFile);
            }
        }

        /// <summary>
        /// Copying a file on top of itself should be a success (no-op) whether
        /// or not skipUnchangedFiles is true or false.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyFileOnItself(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string temp = Path.GetTempPath();
            string file = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A395");

            try
            {
                FileStream fs = null;

                try
                {
                    fs = File.Create(file);
                }
                finally
                {
                    fs?.Dispose();
                }

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(file) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(file) },
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                Assert.True(success);
                Assert.Single(t.DestinationFiles);
                Assert.Equal(file, t.DestinationFiles[0].ItemSpec);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries, nothing to do

                engine = new MockEngine(_testOutputHelper);
                t = new Copy
                {
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(file) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(file) },
                    SkipUnchangedFiles = false,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                success = t.Execute();

                Assert.True(success);
                Assert.Single(t.DestinationFiles);
                Assert.Equal(file, t.DestinationFiles[0].ItemSpec);
                Assert.Single(t.CopiedFiles);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries, nothing to do
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Copying a file on top of itself should be a success (no-op) whether
        /// or not skipUnchangedFiles is true or false. Variation with different casing/relativeness.
        /// </summary>
        [WindowsOnlyTheory(additionalMessage: "File names under Unix are case-sensitive and this test is not useful.")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyFileOnItself2(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string currdir = Directory.GetCurrentDirectory();
            string filename = "2A333ED756AF4dc392E728D0F864A396";
            string file = Path.Combine(currdir, filename);

            try
            {
                FileStream fs = null;

                try
                {
                    fs = File.Create(file);
                }
                finally
                {
                    fs?.Dispose();
                }

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1, // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(file) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(filename.ToLowerInvariant()) },
                    SkipUnchangedFiles = false,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                Assert.True(success);
                Assert.Single(t.DestinationFiles);
                Assert.Equal(filename.ToLowerInvariant(), t.DestinationFiles[0].ItemSpec);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries, nothing to do
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Copying a file on top of itself should be a success (no-op) whether
        /// or not skipUnchangedFiles is true or false. Variation with a second copy failure.
        /// </summary>
        [Theory]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyFileOnItselfAndFailACopy(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string temp = Path.GetTempPath();
            string file = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A395");
            string invalidFile = NativeMethodsShared.IsUnixLike ? Path.Combine(temp, "!@#$%^&*()|") : "!@#$%^&*()|";
            const string dest2 = "whatever";

            try
            {
                FileStream fs = null;

                try
                {
                    fs = File.Create(file);
                }
                finally
                {
                    fs?.Dispose();
                }

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(file), new TaskItem(invalidFile) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(file), new TaskItem(dest2) },
                    SkipUnchangedFiles = false,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                // Since on Unix there are no invalid file names, the copy will succeed
                Assert.False(NativeMethodsShared.IsUnixLike ? !success : success);
                Assert.Equal(2, t.DestinationFiles.Length);
                Assert.Equal(file, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(dest2, t.DestinationFiles[1].ItemSpec);
                Assert.Equal(file, t.CopiedFiles[0].ItemSpec);

                if (NativeMethodsShared.IsUnixLike)
                {
                    Assert.Equal(2, t.CopiedFiles.Length);
                }
                else
                {
                    Assert.Single(t.CopiedFiles);

                    ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026");
                    // Didn't do retries, no op then invalid
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyToDestinationFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string temp = Path.GetTempPath();
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));
            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }

                // Don't create the dest folder, let task do that

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                var me = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile)); // "destination exists"

                string destinationFileContents;
                using (StreamReader sr = FileUtilities.OpenRead(destFile))
                {
                    destinationFileContents = sr.ReadToEnd();
                }

                if (!isUseHardLinks)
                {
                    MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;
                    me.AssertLogDoesntContainMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile, destFile);
                }
                else
                {
                    MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;
                    me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile, destFile);
                }

                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination file to contain the contents of source file."

                Assert.Single(t.DestinationFiles);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(destFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile, t.CopiedFiles[0].ItemSpec);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                Helpers.DeleteFiles(sourceFile, destFile);
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void CopyDoubleEscapableFileToDestinationFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFileEscaped = Path.GetTempPath() + "a%253A_" + Guid.NewGuid().ToString("N") + ".txt";
            string sourceFile = EscapingUtilities.UnescapeAll(sourceFileEscaped);
            string temp = Path.GetTempPath();
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true))    // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }

                // Don't create the dest folder, let task do that

                ITaskItem[] sourceFiles = { new TaskItem(sourceFileEscaped) };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile)); // "destination exists"

                string destinationFileContents;
                using (StreamReader sr = FileUtilities.OpenRead(destFile))
                {
                    destinationFileContents = sr.ReadToEnd();
                }

                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination file to contain the contents of source file."

                Assert.Single(t.DestinationFiles);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(destFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile, t.CopiedFiles[0].ItemSpec);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                Helpers.DeleteFiles(sourceFile, destFile);
            }
        }

        /// <summary>
        /// Copying duplicates should only perform the actual copy once for each unique source/destination pair
        /// but should still produce outputs for all specified source/destination pairs.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void CopyWithDuplicatesUsingFolder(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string tempPath = Path.GetTempPath();

            ITaskItem[] sourceFiles =
            {
                new TaskItem(Path.Combine(tempPath, "a.cs")),
                new TaskItem(Path.Combine(tempPath, "b.cs")),
                new TaskItem(Path.Combine(tempPath, "a.cs")),
                new TaskItem(Path.Combine(tempPath, "a.cs")),
            };

            foreach (ITaskItem item in sourceFiles)
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(item.ItemSpec, false))    // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }
            }

            var filesActuallyCopied = new List<KeyValuePair<FileState, FileState>>();

            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = new MockEngine(_testOutputHelper),
                SourceFiles = sourceFiles,
                DestinationFolder = new TaskItem(Path.Combine(tempPath, "foo")),
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            bool success = t.Execute(delegate (FileState source, FileState dest)
            {
                lock (filesActuallyCopied)
                {
                    filesActuallyCopied.Add(new KeyValuePair<FileState, FileState>(source, dest));
                }
                return true;
            }, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.True(success);
            Assert.Equal(2, filesActuallyCopied.Count);
            Assert.Equal(4, t.CopiedFiles.Length);

            // Copy calls to different destinations can come in any order when running in parallel.
            filesActuallyCopied.Select(f => Path.GetFileName(f.Key.Name)).ShouldBe(new[] { "a.cs", "b.cs" }, ignoreOrder: true);

            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
        }

        /// <summary>
        /// Copying duplicates should only perform the actual copy once for each unique source/destination pair
        /// but should still produce outputs for all specified source/destination pairs.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void CopyWithDuplicatesUsingFiles(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            string tempPath = Path.GetTempPath();

            ITaskItem[] sourceFiles =
            {
                new TaskItem(Path.Combine(tempPath, "a.cs")),
                new TaskItem(Path.Combine(tempPath, "b.cs")),
                new TaskItem(Path.Combine(tempPath, "a.cs")),
                new TaskItem(Path.Combine(tempPath, "a.cs")),
                new TaskItem(Path.Combine(tempPath, "a.cs")),
            };

            foreach (ITaskItem item in sourceFiles)
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(item.ItemSpec, false))    // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }
            }

            ITaskItem[] destFiles =
            {
                new TaskItem(Path.Combine(tempPath, @"xa.cs")), // a.cs -> xa.cs
                new TaskItem(Path.Combine(tempPath, @"xa.cs")), // b.cs -> xa.cs should copy because it's a different source
                new TaskItem(Path.Combine(tempPath, @"xb.cs")), // a.cs -> xb.cs should copy because it's a different destination
                new TaskItem(Path.Combine(tempPath, @"xa.cs")), // a.cs -> xa.cs should copy because it's a different source from the b.cs copy done previously
                new TaskItem(Path.Combine(tempPath, @"xa.cs")), // a.cs -> xa.cs should not copy because it's the same source
            };

            var filesActuallyCopied = new List<KeyValuePair<FileState, FileState>>();

            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
                BuildEngine = new MockEngine(_testOutputHelper),
                SourceFiles = sourceFiles,
                DestinationFiles = destFiles,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            bool success = t.Execute(delegate (FileState source, FileState dest)
            {
                lock (filesActuallyCopied)
                {
                    filesActuallyCopied.Add(new KeyValuePair<FileState, FileState>(source, dest));
                }
                return true;
            }, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.True(success);
            Assert.Equal(4, filesActuallyCopied.Count);
            Assert.Equal(5, t.CopiedFiles.Length);

            // Copy calls to different destinations can come in any order when running in parallel.
            string xaPath = Path.Combine(tempPath, "xa.cs");
            var xaCopies = filesActuallyCopied.Where(f => f.Value.Name == xaPath).ToList();
            Assert.Equal(3, xaCopies.Count);
            Assert.Equal(Path.Combine(tempPath, "a.cs"), xaCopies[0].Key.Name);
            Assert.Equal(Path.Combine(tempPath, "b.cs"), xaCopies[1].Key.Name);
            Assert.Equal(Path.Combine(tempPath, "a.cs"), xaCopies[2].Key.Name);

            string xbPath = Path.Combine(tempPath, "xb.cs");
            var xbCopies = filesActuallyCopied.Where(f => f.Value.Name == xbPath).ToList();
            Assert.Single(xbCopies);
            Assert.Equal(Path.Combine(tempPath, "a.cs"), xbCopies[0].Key.Name);

            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
        }

        /// <summary>
        /// DestinationFiles should only include files that were successfully copied
        /// (or skipped), not files for which there was an error.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DestinationFilesLengthNotEqualSourceFilesLength(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string temp = Path.GetTempPath();
            string inFile1 = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string inFile2 = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A399");
            string outFile1 = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A400");

            try
            {
                FileStream fs = null;
                FileStream fs2 = null;

                try
                {
                    fs = File.Create(inFile1);
                    fs2 = File.Create(inFile2);
                }
                finally
                {
                    fs?.Dispose();
                    fs2?.Dispose();
                }

                var engine = new MockEngine(_testOutputHelper);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(inFile1), new TaskItem(inFile2) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(outFile1) },
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool success = t.Execute();

                Assert.False(success);
                Assert.Single(t.DestinationFiles);
                Assert.Null(t.CopiedFiles);
                Assert.False(File.Exists(outFile1));

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(inFile1);
                File.Delete(inFile2);
                File.Delete(outFile1);
            }
        }

        /// <summary>
        /// If the destination path is too long, the task should not bubble up
        /// the System.IO.PathTooLongException
        /// </summary>
        [WindowsFullFrameworkOnlyTheory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void Regress451057_ExitGracefullyIfPathNameIsTooLong(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            const string destinationFile = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ";

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(sourceFile, true)) // HIGHCHAR: Test writes in UTF8 without preamble.
                {
                    sw.Write("This is a source temp file.");
                }

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
                ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = new MockEngine(_testOutputHelper),
                    SourceFiles = sourceFiles,
                    DestinationFiles = destinationFiles,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                };

                bool result = t.Execute();

                // Expect for there to have been no copies.
                Assert.False(result);

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile);
            }
        }

        /// <summary>
        /// If the source path is too long, the task should not bubble up
        /// the System.IO.PathTooLongException
        /// </summary>
        [WindowsFullFrameworkOnlyTheory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void Regress451057_ExitGracefullyIfPathNameIsTooLong2(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            const string sourceFile = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ";
            string destinationFile = FileUtilities.GetTemporaryFile();
            File.Delete(destinationFile);

            ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };
            ITaskItem[] destinationFiles = { new TaskItem(destinationFile) };

            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = new MockEngine(_testOutputHelper),
                SourceFiles = sourceFiles,
                DestinationFiles = destinationFiles,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            bool result = t.Execute();

            // Expect for there to have been no copies.
            Assert.False(result);
            Assert.False(File.Exists(destinationFile));

            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
        }

        /// <summary>
        /// If the SourceFiles parameter is given invalid path characters, make sure the task exits gracefully.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void ExitGracefullyOnInvalidPathCharacters(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
                BuildEngine = new MockEngine(_testOutputHelper),
                SourceFiles = new ITaskItem[] { new TaskItem("foo | bar") },
                DestinationFolder = new TaskItem("dest"),
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            bool result = t.Execute();

            // Expect for there to have been no copies.
            Assert.False(result);
            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
        }

        /// <summary>
        /// If the DestinationFolder parameter is given invalid path characters, make sure the task exits gracefully.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void ExitGracefullyOnInvalidPathCharactersInDestinationFolder(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
                BuildEngine = new MockEngine(_testOutputHelper),
                SourceFiles = new ITaskItem[] { new TaskItem("foo") },
                DestinationFolder = new TaskItem("here | there"),
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            bool result = t.Execute();

            // Expect for there to have been no copies.
            Assert.False(result);
            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
        }

        /// <summary>
        /// Verifies that we error for retries less than 0
        /// </summary>
        [Fact]
        public void InvalidRetryCount()
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = -1,
            };

            bool result = t.Execute();

            Assert.False(result);
            engine.AssertLogContains("MSB3028");
        }

        /// <summary>
        /// Verifies that we error for retry delay less than 0
        /// </summary>
        [Fact]
        public void InvalidRetryDelayCount()
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = 1,
                RetryDelayMilliseconds = -1,
            };

            bool result = t.Execute();

            Assert.False(result);
            engine.AssertLogContains("MSB3029");
        }

        /// <summary>
        /// Verifies that we do not log the retrying warning if we didn't request
        /// retries.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void FailureWithNoRetries(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = 0,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            var copyFunctor = new CopyFunctor(2, false /* do not throw on failure */);
            bool result = t.Execute(copyFunctor.Copy, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.False(result);
            engine.AssertLogDoesntContain("MSB3026");
            engine.AssertLogDoesntContain("MSB3027");
        }

        /// <summary>
        /// Retrying default
        /// </summary>
        [Fact]
        public void DefaultRetriesIs10()
        {
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
            };

            Assert.Equal(10, t.Retries);
        }

        /// <summary>
        /// Delay default
        /// </summary>
        [Fact]
        public void DefaultRetryDelayIs1000()
        {
            var t = new Copy();

            Assert.Equal(1000, t.RetryDelayMilliseconds);
        }

        /// <summary>
        /// Hardlink default
        /// </summary>
        [Fact]
        public void DefaultNoHardlink()
        {
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
            };

            Assert.False(t.UseHardlinksIfPossible);
        }

        /// <summary>
        /// Verifies that we get the one retry we ask for after the first attempt fails,
        /// and we get appropriate messages.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void SuccessAfterOneRetry(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 0, // Can't really test the delay, but at least try passing in a value
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = 1,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            var copyFunctor = new CopyFunctor(2, false /* do not throw on failure */);
            bool result = t.Execute(copyFunctor.Copy, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.True(result);
            engine.AssertLogContains("MSB3026");
            engine.AssertLogDoesntContain("MSB3027");
        }

        /// <summary>
        /// Verifies that after a successful retry we continue to the next file
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void SuccessAfterOneRetryContinueToNextFile(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // Can't really test the delay, but at least try passing in a value
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source"), new TaskItem("c:\\source2") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination"), new TaskItem("c:\\destination2") },
                Retries = 1,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            var copyFunctor = new CopyFunctor(2, false /* do not throw on failure */);
            bool result = t.Execute(copyFunctor.Copy, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.True(result);
            engine.AssertLogContains("MSB3026");
            engine.AssertLogDoesntContain("MSB3027");

            // Copy calls to different destinations can come in any order when running in parallel.
            Assert.Contains(copyFunctor.FilesCopiedSuccessfully, f => f.Name == FileUtilities.FixFilePath("c:\\source"));
            Assert.Contains(copyFunctor.FilesCopiedSuccessfully, f => f.Name == FileUtilities.FixFilePath("c:\\source2"));
        }

        /// <summary>
        /// The copy delegate can return false, or throw on failure.
        /// This test tests returning false.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void TooFewRetriesReturnsFalse(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = 2,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            var copyFunctor = new CopyFunctor(4, false /* do not throw */);
            bool result = t.Execute(copyFunctor.Copy, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.False(result);
            engine.AssertLogContains("MSB3026");
            engine.AssertLogContains("MSB3027");
        }


        /// <summary>
        /// The copy delegate can return false, or throw on failure.
        /// This test tests the throw case.
        /// </summary>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinksSingleThreaded))]
        public void TooFewRetriesThrows(bool isUseHardLinks, bool isUseSymbolicLinks, bool isUseSingleThreadedCopy)
        {
            var engine = new MockEngine(true /* log to console */);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1,  // speed up tests!
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                Retries = 1,
                UseHardlinksIfPossible = isUseHardLinks,
                UseSymboliclinksIfPossible = isUseSymbolicLinks,
            };

            var copyFunctor = new CopyFunctor(3, true /* throw */);
            bool result = t.Execute(copyFunctor.Copy, GetParallelismThreadCount(isUseSingleThreadedCopy));

            Assert.False(result);
            engine.AssertLogContains("MSB3026");
            engine.AssertLogContains("MSB3027");
        }

        [WindowsOnlyTheory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void ErrorIfLinkFailedCheck(bool isUseHardLinks, bool isUseSymbolicLinks)
        {
            using (var env = TestEnvironment.Create())
            {
                var source = env.DefaultTestDirectory.CreateFile("source.txt", "This is a source file").Path;
                var existing = env.DefaultTestDirectory.CreateFile("destination.txt", "This is an existing file.").Path;

                File.SetAttributes(existing, FileAttributes.ReadOnly);

                MockEngine engine = new MockEngine(_testOutputHelper);
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1,
                    UseHardlinksIfPossible = isUseHardLinks,
                    UseSymboliclinksIfPossible = isUseSymbolicLinks,
                    ErrorIfLinkFails = true,
                    BuildEngine = engine,
                    SourceFiles = new ITaskItem[] { new TaskItem(source) },
                    DestinationFiles = new ITaskItem[] { new TaskItem(existing) },
                };

                t.Execute().ShouldBeFalse();
                engine.AssertLogContains("MSB3893");
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        [Fact]
        public void CopyToDestinationFolderWithHardLinkCheck()
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string temp = Path.GetTempPath();
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));
            try
            {
                File.WriteAllText(sourceFile, "This is a source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                // Don't create the dest folder, let task do that

                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                var me = new MockEngine(true);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1, // speed up tests!
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true,
                    UseHardlinksIfPossible = true
                };

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile)); // "destination exists"
                MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;

                me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile, destFile);

                string destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is a source temp file.", destinationFileContents);

                Assert.Single(t.DestinationFiles);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(destFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile, t.CopiedFiles[0].ItemSpec);

                // Now we will write new content to the source file
                // we'll then check that the destination file automatically
                // has the same content (i.e. it's been hard linked)
                File.WriteAllText(sourceFile, "This is another source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                // Read the destination file (it should have the same modified content as the source)
                destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is another source temp file.", destinationFileContents); // "Expected the destination hard linked file to contain the contents of source file. Even after modification of the source"

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                Helpers.DeleteFiles(sourceFile, destFile);
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        [WindowsOnlyFact(additionalMessage: "SMB share paths only work on Windows.")]
        public void CopyToDestinationFolderWithHardLinkFallbackNetwork()
        {
            // Workaround: For some reason when this test runs with all other tests we are getting
            // the incorrect result from CreateHardLink error message (a message associated with
            // another test). Calling GetHRForLastWin32Error / GetExceptionForHR seems to clear
            // out the previous message and allow us to get the right message in the Copy task.
            int errorCode = Marshal.GetHRForLastWin32Error();
            Marshal.GetExceptionForHR(errorCode);

            string sourceFile1 = FileUtilities.GetTemporaryFile();
            string sourceFile2 = FileUtilities.GetTemporaryFile();
            const string temp = @"\\localhost\c$\temp";
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile1 = Path.Combine(destFolder, Path.GetFileName(sourceFile1));
            string destFile2 = Path.Combine(destFolder, Path.GetFileName(sourceFile2));

            try
            {
                Directory.CreateDirectory(destFolder);
                string nothingFile = Path.Combine(destFolder, "nothing.txt");
                File.WriteAllText(nothingFile, "nothing");
                File.Delete(nothingFile);
            }
            catch (Exception)
            {
                Console.WriteLine("CopyToDestinationFolderWithHardLinkFallbackNetwork test could not access the network.");
                // Something caused us to not be able to access our "network" share, don't fail.
                return;
            }

            try
            {
                // Create 2 files to ensure we test with parallel copy.
                File.WriteAllText(sourceFile1, "This is source temp file 1."); // HIGHCHAR: Test writes in UTF8 without preamble.
                File.WriteAllText(sourceFile2, "This is source temp file 2.");

                ITaskItem[] sourceFiles =
                {
                    new TaskItem(sourceFile1),
                    new TaskItem(sourceFile2)
                };

                var me = new MockEngine(true);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1, // speed up tests!
                    UseHardlinksIfPossible = true,
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true
                };

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile1)); // "destination exists"
                Assert.True(File.Exists(destFile2)); // "destination exists"
                MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;

                me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile1, destFile1);

                // Can't do this below, because the real message doesn't end with String.Empty, it ends with a CLR exception string, and so matching breaks in PLOC.
                // Instead look for the HRESULT that CLR unfortunately puts inside its exception string. Something like this:
                //   The system cannot move the file to a different disk drive. (Exception from HRESULT: 0x80070011)
                // me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.RetryingAsFileCopy", sourceFile, destFile, String.Empty);
                me.AssertLogContains("0x80070011");

                string destinationFileContents = File.ReadAllText(destFile1);
                Assert.Equal("This is source temp file 1.", destinationFileContents); // "Expected the destination file to contain the contents of source file."
                destinationFileContents = File.ReadAllText(destFile2);
                Assert.Equal("This is source temp file 2.", destinationFileContents); // "Expected the destination file to contain the contents of source file."

                Assert.Equal(2, t.DestinationFiles.Length);
                Assert.Equal(2, t.CopiedFiles.Length);
                Assert.Equal(destFile1, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile2, t.DestinationFiles[1].ItemSpec);
                Assert.Equal(destFile1, t.CopiedFiles[0].ItemSpec);
                Assert.Equal(destFile2, t.CopiedFiles[1].ItemSpec);

                // Now we will write new content to a source file
                // we'll then check that the destination file automatically
                // has the same content (i.e. it's been hard linked)
                File.WriteAllText(sourceFile1, "This is another source temp file.");  // HIGHCHAR: Test writes in UTF8 without preamble.

                // Read the destination file (it should have the same modified content as the source)
                destinationFileContents = File.ReadAllText(destFile1);
                Assert.Equal("This is source temp file 1.", destinationFileContents); // "Expected the destination copied file to contain the contents of original source file only."

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile1);
                File.Delete(sourceFile2);
                File.Delete(destFile1);
                File.Delete(destFile2);
                FileUtilities.DeleteWithoutTrailingBackslash(destFolder, true);
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        [WindowsOnlyFact(additionalMessage: "Only Windows has a (small) link limit, and this tests for an HRESULT.")]
        public void CopyToDestinationFolderWithHardLinkFallbackTooManyLinks()
        {
            // Workaround: For some reason when this test runs with all other tests we are getting
            // the incorrect result from CreateHardLink error message (a message associated with
            // another test). Calling GetHRForLastWin32Error / GetExceptionForHR seems to clear
            // out the previous message and allow us to get the right message in the Copy task.
            int errorCode = Marshal.GetHRForLastWin32Error();
            Marshal.GetExceptionForHR(errorCode);

            string sourceFile = FileUtilities.GetTemporaryFile();
            string temp = Path.GetTempPath();
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));

            try
            {
                File.WriteAllText(sourceFile, "This is a source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                Directory.CreateDirectory(destFolder);


                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                MockEngine me = new MockEngine(true);
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1, // speed up tests!
                    UseHardlinksIfPossible = true,
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true
                };

                // Exhaust the number (1024) of directory entries that can be created for a file
                // This is 1 + (1 x hard links)
                // We need to test the fallback code path when we're out of directory entries for a file..
                for (int n = 0; n < 1025 /* make sure */; n++)
                {
                    string destLink = Path.Combine(destFolder, Path.GetFileNameWithoutExtension(sourceFile) + "." + n);
                    string linkError = String.Empty;
                    Tasks.NativeMethods.MakeHardLink(destLink, sourceFile, ref linkError, t.Log);
                }

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile)); // "destination exists"
                MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;

                me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile, destFile);

                // Can't do this below, because the real message doesn't end with String.Empty, it ends with a CLR exception string, and so matching breaks in PLOC.
                // Instead look for the HRESULT that CLR unfortunately puts inside its exception string. Something like this
                // Tried to create more than a few links to a file that is supported by the file system. (! yhMcE! Exception from HRESULT: Table c?! 0x80070476)
                // me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.RetryingAsFileCopy", sourceFile, destFile, String.Empty);
                me.AssertLogContains("0x80070476");

                string destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination file to contain the contents of source file."

                Assert.Single(t.DestinationFiles);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(destFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile, t.CopiedFiles[0].ItemSpec);

                // Now we will write new content to the source file
                // we'll then check that the destination file automatically
                // has the same content (i.e. it's been hard linked)
                File.WriteAllText(sourceFile, "This is another source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                // Read the destination file (it should have the same modified content as the source)
                destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination copied file to contain the contents of original source file only."

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries
            }
            finally
            {
                File.Delete(sourceFile);
                File.Delete(destFile);
                FileUtilities.DeleteWithoutTrailingBackslash(destFolder, true);
            }
        }

        /// <summary>
        /// DestinationFolder should work.
        /// </summary>
        /// <param name="useRelative">Use a relative path, e.g. obj/Debug</param>
        [RequiresSymbolicLinksTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void CopyToDestinationFolderWithSymbolicLinkCheck(bool useRelative)
        {
            string currentDirectory = Directory.GetCurrentDirectory();
            string sourceFolder = null;
            string absoluteSourceFile = null;
            string destFolder = null;
            string destFile = null;

            try
            {
                string temp = Path.GetTempPath();

                // Due to symlinks from /var to /private/var on MacOs
                // Setting the current directory to /var/... returns a current directory of /private/var/...
                // This causes the relative path from /private/var to /var to contain a lot of ../ components up to the root.
                // Although that should work it is not what we are testing here.
                Directory.SetCurrentDirectory(temp);
                temp = Directory.GetCurrentDirectory();

                sourceFolder = Path.Combine(temp, $"MSBuildSymLinkTemp{Environment.UserName}");
                Directory.CreateDirectory(sourceFolder);
                absoluteSourceFile = FileUtilities.GetTemporaryFile(sourceFolder, null, ".tmp", false);
                destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
                destFile = Path.Combine(destFolder, Path.GetFileName(absoluteSourceFile));
                string relativeSourceFile = useRelative ? absoluteSourceFile.Substring(temp.Length + 1) : absoluteSourceFile;

                File.WriteAllText(absoluteSourceFile, "This is a source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                // Don't create the dest folder, let task do that
                ITaskItem[] sourceFiles = { new TaskItem(relativeSourceFile) };

                var me = new MockEngine(true);
                var t = new Copy
                {
                    RetryDelayMilliseconds = 1,  // speed up tests!
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true,
                    UseSymboliclinksIfPossible = true
                };

                bool success = t.Execute();

                Assert.True(success); // "success"
                Assert.True(File.Exists(destFile)); // "destination exists"
                Assert.True((File.GetAttributes(destFile) & FileAttributes.ReparsePoint) != 0, "File was copied but is not a symlink");

#if NET6_0_OR_GREATER
                string expectedRelativeLink = Path.Combine("..", relativeSourceFile);
                var info = new FileInfo(destFile);

                Assert.Equal(expectedRelativeLink, info.LinkTarget);
#endif

                MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;

                me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.SymbolicLinkComment", relativeSourceFile, destFile);

                string destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is a source temp file.", destinationFileContents); // "Expected the destination symbolic linked file to contain the contents of source file."

                Assert.Single(t.DestinationFiles);
                Assert.Single(t.CopiedFiles);
                Assert.Equal(destFile, t.DestinationFiles[0].ItemSpec);
                Assert.Equal(destFile, t.CopiedFiles[0].ItemSpec);

                // Now we will write new content to the source file
                // we'll then check that the destination file automatically
                // has the same content (i.e. it's been linked)
                File.WriteAllText(absoluteSourceFile, "This is another source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.

                // Read the destination file (it should have the same modified content as the source)
                destinationFileContents = File.ReadAllText(destFile);
                Assert.Equal("This is another source temp file.", destinationFileContents); // "Expected the destination hard linked file to contain the contents of source file. Even after modification of the source"

                ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3891"); // Didn't do retries
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDirectory);
                if (destFile != null)
                {
                    File.Delete(absoluteSourceFile);
                    File.Delete(destFile);
                    FileUtilities.DeleteWithoutTrailingBackslash(destFolder, true);
                    FileUtilities.DeleteWithoutTrailingBackslash(sourceFolder, true);
                }
            }
        }

        /// <summary>
        /// Verify build successful when UseHardlinksIfPossible and UseSymboliclinksIfPossible are true
        /// </summary>
        [Fact]
        public void CopyWithHardAndSymbolicLinks()
        {
            string sourceFile = FileUtilities.GetTemporaryFile();
            string temp = Path.GetTempPath();
            string destFolder = Path.Combine(temp, "2A333ED756AF4dc392E728D0F864A398");
            string destFile = Path.Combine(destFolder, Path.GetFileName(sourceFile));

            try
            {
                ITaskItem[] sourceFiles = { new TaskItem(sourceFile) };

                MockEngine me = new MockEngine(true);
                Copy t = new Copy
                {
                    RetryDelayMilliseconds = 1, // speed up tests!
                    UseHardlinksIfPossible = true,
                    UseSymboliclinksIfPossible = true,
                    BuildEngine = me,
                    SourceFiles = sourceFiles,
                    DestinationFolder = new TaskItem(destFolder),
                    SkipUnchangedFiles = true
                };

                bool success = t.Execute();

                Assert.True(success);
                MockEngine.GetStringDelegate resourceDelegate = AssemblyResources.GetString;
                me.AssertLogContainsMessageFromResource(resourceDelegate, "Copy.HardLinkComment", sourceFile, destFile);
            }
            finally
            {
                Helpers.DeleteFiles(sourceFile, destFile);
            }
        }

        /// <summary>
        /// Verifies that we error when ErrorIfLinkFailed is true when UseHardlinksIfPossible
        /// and UseSymboliclinksIfPossible are false.
        /// </summary>
        [Fact]
        public void InvalidErrorIfLinkFailed()
        {
            var engine = new MockEngine(true);
            var t = new Copy
            {
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem("c:\\source") },
                DestinationFiles = new ITaskItem[] { new TaskItem("c:\\destination") },
                UseHardlinksIfPossible = false,
                UseSymboliclinksIfPossible = false,
                ErrorIfLinkFails = true,
            };

            bool result = t.Execute();

            Assert.False(result);
            engine.AssertLogContains("MSB3892");
        }

        /// <summary>
        /// An existing link source should not be modified.
        /// </summary>
        /// <remarks>
        /// Related to issue [#8273](https://github.com/dotnet/msbuild/issues/8273)
        /// </remarks>
        [Theory]
        [MemberData(nameof(GetHardLinksSymLinks))]
        public void DoNotCorruptSourceOfLink(bool useHardLink, bool useSymbolicLink)
        {
            using TestEnvironment env = TestEnvironment.Create();
            TransientTestFile sourceFile1 = env.CreateFile("source1.tmp", "This is the first source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.
            TransientTestFile sourceFile2 = env.CreateFile("source2.tmp", "This is the second source temp file."); // HIGHCHAR: Test writes in UTF8 without preamble.
            TransientTestFolder destFolder = env.CreateFolder(createFolder: false);
            string destFile = Path.Combine(destFolder.Path, "The Destination");

            // Don't create the dest folder, let task do that
            ITaskItem[] sourceFiles = { new TaskItem(sourceFile1.Path) };
            ITaskItem[] destinationFiles = { new TaskItem(destFile) };

            var me = new MockEngine(true);
            var t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = me,
                SourceFiles = sourceFiles,
                DestinationFiles = destinationFiles,
                SkipUnchangedFiles = true,
                UseHardlinksIfPossible = useHardLink,
                UseSymboliclinksIfPossible = useSymbolicLink,
            };

            t.Execute().ShouldBeTrue();
            File.Exists(destFile).ShouldBeTrue();
            File.ReadAllText(destFile).ShouldBe("This is the first source temp file.");

            sourceFiles = new TaskItem[] { new TaskItem(sourceFile2.Path) };

            t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = me,
                SourceFiles = sourceFiles,
                DestinationFiles = destinationFiles,
                SkipUnchangedFiles = true,
                UseHardlinksIfPossible = false,
                UseSymboliclinksIfPossible = false,
            };

            t.Execute().ShouldBeTrue();
            File.Exists(destFile).ShouldBeTrue();
            File.ReadAllText(destFile).ShouldBe("This is the second source temp file.");

            // Read the source file (it should not have been overwritten)
            File.ReadAllText(sourceFile1.Path).ShouldBe("This is the first source temp file.");
            ((MockEngine)t.BuildEngine).AssertLogDoesntContain("MSB3026"); // Didn't do retries

            destinationFiles = new TaskItem[] { new TaskItem(
                Path.Combine(Path.GetDirectoryName(sourceFile2.Path), ".", Path.GetFileName(sourceFile2.Path))) // sourceFile2.Path with a "." inserted before the file name
            };

            t = new Copy
            {
                RetryDelayMilliseconds = 1, // speed up tests!
                BuildEngine = me,
                SourceFiles = sourceFiles,
                DestinationFiles = destinationFiles,
                SkipUnchangedFiles = true,
            };

            t.Execute().ShouldBeTrue();
            File.Exists(sourceFile2.Path).ShouldBeTrue();
        }

        internal sealed class CopyMonitor
        {
            internal int copyCount;

            /*
            * Method:   CopyFile
            *
            * Don't really copy the file, just count how many times this was called.
            */
            internal bool? CopyFile(FileState source, FileState destination)
            {
                Interlocked.Increment(ref copyCount);
                return true;
            }
        }

        /// <summary>
        /// Helper functor for retry tests.
        /// Simulates the File.Copy method without touching the disk.
        /// First copy fails as requested, subsequent copies succeed.
        /// </summary>
        private sealed class CopyFunctor
        {
            /// <summary>
            /// Protects the counts and lists below.
            /// </summary>
            private readonly object _lockObj = new object();

            /// <summary>
            /// On what attempt count should we stop failing?
            /// </summary>
            private readonly int _countOfSuccess;

            /// <summary>
            /// Should we throw when we fail, instead of just returning false?
            /// </summary>
            private readonly bool _throwOnFailure;

            /// <summary>
            /// How many tries have we done so far
            /// </summary>
            private int _tries;

            /// <summary>
            /// Which files we actually copied
            /// </summary>
            internal List<FileState> FilesCopiedSuccessfully { get; } = new List<FileState>();

            /// <summary>
            /// Constructor
            /// </summary>
            internal CopyFunctor(int countOfSuccess, bool throwOnFailure)
            {
                _countOfSuccess = countOfSuccess;
                _throwOnFailure = throwOnFailure;
            }

            /// <summary>
            /// Pretend to be File.Copy.
            /// </summary>
            internal bool? Copy(FileState source, FileState destination)
            {
                lock (_lockObj)
                {
                    _tries++;

                    // 2nd and subsequent copies always succeed
                    if (FilesCopiedSuccessfully.Count > 0 || _countOfSuccess == _tries)
                    {
                        Console.WriteLine("Copied {0} to {1} OK", source, destination);
                        FilesCopiedSuccessfully.Add(source);
                        return true;
                    }
                }

                if (_throwOnFailure)
                {
                    throw new IOException("oops");
                }

                return null;
            }
        }
    }
}
