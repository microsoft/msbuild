// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Xunit.NetCore.Extensions;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public class FileMatcherTest : IDisposable
    {
        private readonly TestEnvironment _env;
        private Lazy<DummyMappedDrive> _mappedDrive = DummyMappedDriveUtils.GetLazyDummyMappedDrive();

        public FileMatcherTest(ITestOutputHelper output)
        {
            _env = TestEnvironment.Create(output);
        }

        public void Dispose()
        {
            _env.Dispose();
            _mappedDrive.Value?.Dispose();
        }

        [Theory]
        [InlineData("*.txt", 5)]
        [InlineData("???.cs", 1)]
        [InlineData("????.cs", 1)]
        [InlineData("file?.txt", 1)]
        [InlineData("fi?e?.txt", 2)]
        [InlineData("???.*", 1)]
        [InlineData("????.*", 4)]
        [InlineData("*.???", 5)]
        [InlineData("f??e1.txt", 2)]
        [InlineData("file.*.txt", 1)]
        public void GetFilesPatternMatching(string pattern, int expectedMatchCount)
        {
            TransientTestFolder testFolder = _env.CreateFolder();

            foreach (var file in new[]
            {
                "Foo.cs",
                "Foo2.cs",
                "file.txt",
                "file1.txt",
                "file1.txtother",
                "fie1.txt",
                "fire1.txt",
                "file.bak.txt"
            })
            {
                File.WriteAllBytes(Path.Combine(testFolder.Path, file), new byte[1]);
            }

            string[] fileMatches = FileMatcher.Default.GetFiles(testFolder.Path, pattern).FileList;

            fileMatches.Length.ShouldBe(expectedMatchCount, $"Matches: '{String.Join("', '", fileMatches)}'");
        }

#if FEATURE_SYMLINK_TARGET
        [RequiresSymbolicLinksFact]
        public void DoNotFollowRecursiveSymlinks()
        {
            TransientTestFolder testFolder = _env.CreateFolder();
            TransientTestFile file = _env.CreateFile(testFolder, "Foo.cs");
            TransientTestFolder tf2 = _env.CreateFolder(Path.Combine(testFolder.Path, "subfolder"));
            string symlinkPath = Path.Combine(tf2.Path, "mySymlink");
            try
            {
                Directory.CreateSymbolicLink(symlinkPath, testFolder.Path);
                string[] fileMatches = FileMatcher.Default.GetFiles(testFolder.Path, "**").FileList;
                fileMatches.Length.ShouldBe(1);
            }
            finally
            {
                if (Directory.Exists(symlinkPath))
                {
                    Directory.Delete(symlinkPath);
                }
            }
        }
#endif

        [Theory]
        [MemberData(nameof(GetFilesComplexGlobbingMatchingInfo.GetTestData), MemberType = typeof(GetFilesComplexGlobbingMatchingInfo))]
        public void GetFilesComplexGlobbingMatching(GetFilesComplexGlobbingMatchingInfo info)
        {
            TransientTestFolder testFolder = _env.CreateFolder();

            // Create directories and files
            foreach (string fullPath in GetFilesComplexGlobbingMatchingInfo.FilesToCreate.Select(i => Path.Combine(testFolder.Path, i.ToPlatformSlash())))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                File.WriteAllBytes(fullPath, new byte[1]);
            }

            void VerifyImpl(FileMatcher fileMatcher, string include, string[] excludes, bool shouldHaveNoMatches = false, string customMessage = null)
            {
                string[] matchedFiles = fileMatcher.GetFiles(testFolder.Path, include, excludes?.ToList()).FileList;

                if (shouldHaveNoMatches)
                {
                    matchedFiles.ShouldBeEmpty(customMessage);
                }
                else
                {
                    // The matches are:
                    // 1. Normalized ("\" regardless of OS and lowercase)
                    // 2. Sorted
                    // Are the same as the expected matches sorted
                    matchedFiles
                        .Select(i => i.Replace(Path.DirectorySeparatorChar, '\\'))
                        .OrderBy(i => i)
                        .ToArray()
                        .ShouldBe(info.ExpectedMatches.OrderBy(i => i), caseSensitivity: Case.Insensitive, customMessage: customMessage);
                }
            }

            var fileMatcherWithCache = new FileMatcher(FileSystems.Default, new ConcurrentDictionary<string, IReadOnlyList<string>>());

            void Verify(string include, string[] excludes, bool shouldHaveNoMatches = false, string customMessage = null)
            {
                // Verify using the default non-caching FileMatcher.
                VerifyImpl(FileMatcher.Default, include, excludes, shouldHaveNoMatches, customMessage);

                // Verify using a caching FileMatcher and do it twice to exercise the cache.
                VerifyImpl(fileMatcherWithCache, include, excludes, shouldHaveNoMatches, customMessage);
                VerifyImpl(fileMatcherWithCache, include, excludes, shouldHaveNoMatches, customMessage);
            }

            // Normal matching
            Verify(info.Include, info.Excludes);

            // Include forward slash
            Verify(info.Include.Replace('\\', '/'), info.Excludes, customMessage: "Include directory separator was changed to forward slash");

            // Excludes forward slash
            Verify(info.Include, info.Excludes?.Select(o => o.Replace('\\', '/')).ToArray(), customMessage: "Excludes directory separator was changed to forward slash");

            // Uppercase includes
            Verify(info.Include.ToUpperInvariant(), info.Excludes, info.ExpectNoMatches, "Include was changed to uppercase");

            // Changing the case of the exclude break Linux
            if (!NativeMethodsShared.IsLinux)
            {
                // Uppercase excludes
                Verify(info.Include, info.Excludes?.Select(o => o.ToUpperInvariant()).ToArray(), false, "Excludes were changed to uppercase");
            }

            // Backward compatibilities:
            // 1. When an include or exclude starts with a fixed directory part e.g. "src/foo/**",
            //    then matching should be case-sensitive on Linux, as the directory was checked for its existance
            //    by using Directory.Exists, which is case-sensitive on Linux (on OSX is not).
            // 2. On Unix, when an include uses a simple ** wildcard e.g. "**\*.cs", the file pattern e.g. "*.cs",
            //    should be matched case-sensitive, as files were retrieved by using the searchPattern parameter
            //    of Directory.GetFiles, which is case-sensitive on Unix.
        }

        /// <summary>
        /// A test data class for providing data to the <see cref="FileMatcherTest.GetFilesComplexGlobbingMatching"/> test.
        /// </summary>
        public class GetFilesComplexGlobbingMatchingInfo
        {
            /// <summary>
            /// The list of known files to create.
            /// </summary>
            public static string[] FilesToCreate =
            {
                @"src\foo.cs",
                @"src\bar.cs",
                @"src\baz.cs",
                @"src\foo\foo.cs",
                @"src\foo\licence",
                @"src\bar\bar.cs",
                @"src\baz\baz.cs",
                @"src\foo\inner\foo.cs",
                @"src\foo\inner\foo\foo.cs",
                @"src\foo\inner\bar\bar.cs",
                @"src\bar\inner\baz.cs",
                @"src\bar\inner\baz\baz.cs",
                @"src\bar\inner\foo\foo.cs",
                @"subd\sub.cs",
                @"subdirectory\subdirectory.cs",
                @"build\baz\foo.cs",
                @"readme.txt",
                @"licence"
            };

            /// <summary>
            /// Gets or sets the include pattern.
            /// </summary>
            public string Include { get; set; }

            /// <summary>
            /// Gets or sets a list of exclude patterns.
            /// </summary>
            public string[] Excludes { get; set; }

            /// <summary>
            /// Gets or sets the list of expected matches.
            /// </summary>
            public string[] ExpectedMatches { get; set; }

            /// <summary>
            /// Get or sets a value indicating to expect no matches if the include pattern is mutated to uppercase.
            /// </summary>
            public bool ExpectNoMatches { get; set; }

            public override string ToString()
            {
                IEnumerable<string> GetParts()
                {
                    yield return $"Include = {Include}";

                    if (Excludes != null)
                    {
                        yield return $"Excludes = {String.Join(";", Excludes)}";
                    }

                    if (ExpectNoMatches)
                    {
                        yield return "ExpectNoMatches";
                    }
                }

                return String.Join(", ", GetParts());
            }

            /// <summary>
            /// Gets the test data
            /// </summary>
            public static IEnumerable<object[]> GetTestData()
            {
                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\**\inner\**\*.cs",
                        ExpectedMatches = new[]
                        {
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\foo\foo.cs",
                            @"src\foo\inner\bar\bar.cs",
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\baz\baz.cs",
                            @"src\bar\inner\foo\foo.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\**\inner\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\foo\inner\foo.*.cs"
                        },
                        ExpectedMatches = new[]
                            {
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\foo\foo.cs",
                            @"src\foo\inner\bar\bar.cs",
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\baz\baz.cs",
                            @"src\bar\inner\foo\foo.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\**\inner\**\*.cs",
                        Excludes = new[]
                        {
                            @"**\foo\**"
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\baz\baz.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\**\inner\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\bar\inner\baz\**"
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\foo\foo.cs",
                            @"src\foo\inner\bar\bar.cs",
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\foo\foo.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\foo\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\foo\**\foo\**"
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\foo\foo.cs",
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\bar\bar.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\foo\inner\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\foo\**\???\**"
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\foo\inner\foo.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                yield return new object[]
                {
                        new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"**\???\**\*.cs",
                        ExpectedMatches = new[]
                        {
                            @"src\foo.cs",
                            @"src\bar.cs",
                            @"src\baz.cs",
                            @"src\foo\foo.cs",
                            @"src\bar\bar.cs",
                            @"src\baz\baz.cs",
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\foo\foo.cs",
                            @"src\foo\inner\bar\bar.cs",
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\baz\baz.cs",
                            @"src\bar\inner\foo\foo.cs",
                            @"build\baz\foo.cs"
                        }
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"**\*.*",
                        Excludes = new[]
                        {
                            @"**\???\**\*.cs",
                            @"subd*\*",
                        },
                        ExpectedMatches = new[]
                        {
                            @"readme.txt",
                            @"licence",
                            @"src\foo\licence",
                        }
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"**\?a?\**\?a?\*.c?",
                        ExpectedMatches = new[]
                        {
                            @"src\bar\inner\baz\baz.cs"
                        }
                    }
                };

                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"**\?a?\**\?a?.c?",
                        Excludes = new[]
                        {
                            @"**\?a?\**\?a?\*.c?"
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\bar\bar.cs",
                            @"src\baz\baz.cs",
                            @"src\foo\inner\bar\bar.cs",
                            @"src\bar\inner\baz.cs"
                        }
                    }
                };

                // Regression test for https://github.com/dotnet/msbuild/issues/4175
                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"subdirectory\**",
                        Excludes = new[]
                        {
                            @"sub\**"
                        },
                        ExpectedMatches = new[]
                        {
                            @"subdirectory\subdirectory.cs",
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                // Regression test for https://github.com/dotnet/msbuild/issues/6502
                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\**",
                        Excludes = new[]
                        {
                            @"**\foo\**",
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\foo.cs",
                            @"src\bar.cs",
                            @"src\baz.cs",
                            @"src\bar\bar.cs",
                            @"src\baz\baz.cs",
                            @"src\bar\inner\baz.cs",
                            @"src\bar\inner\baz\baz.cs",
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                // Hits the early elimination of exclude file patterns that do not intersect with the include.
                // The exclude is redundant and can be eliminated before starting the file system walk.
                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\foo\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\foo\**\foo\**",
                            @"src\foo\**\*.vb" // redundant exclude
                        },
                        ExpectedMatches = new[]
                        {
                            @"src\foo\foo.cs",
                            @"src\foo\inner\foo.cs",
                            @"src\foo\inner\bar\bar.cs"
                        },
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };

                // Hits the early elimination of exclude file patterns that do not intersect with the include.
                // The exclude is not redundant and must not be eliminated.
                yield return new object[]
                {
                    new GetFilesComplexGlobbingMatchingInfo
                    {
                        Include = @"src\foo\**\*.cs",
                        Excludes = new[]
                        {
                            @"src\foo\**\*.*" // effective exclude
                        },
                        ExpectedMatches = Array.Empty<string>(),
                        ExpectNoMatches = NativeMethodsShared.IsLinux,
                    }
                };
            }
        }

        [Fact]
        public void WildcardMatching()
        {
            var inputs = new List<Tuple<string, string, bool>>
            {
                // No wildcards
                new Tuple<string, string, bool>("a", "a", true),
                new Tuple<string, string, bool>("a", "", false),
                new Tuple<string, string, bool>("", "a", false),

                // Non ASCII characters
                new Tuple<string, string, bool>("šđčćž", "šđčćž", true),

                // * wildcard
                new Tuple<string, string, bool>("abc", "*bc", true),
                new Tuple<string, string, bool>("abc", "a*c", true),
                new Tuple<string, string, bool>("abc", "ab*", true),
                new Tuple<string, string, bool>("ab", "*ab", true),
                new Tuple<string, string, bool>("ab", "a*b", true),
                new Tuple<string, string, bool>("ab", "ab*", true),
                new Tuple<string, string, bool>("aba", "ab*ba", false),
                new Tuple<string, string, bool>("", "*", true),

                // ? wildcard
                new Tuple<string, string, bool>("abc", "?bc", true),
                new Tuple<string, string, bool>("abc", "a?c", true),
                new Tuple<string, string, bool>("abc", "ab?", true),
                new Tuple<string, string, bool>("ab", "?ab", false),
                new Tuple<string, string, bool>("ab", "a?b", false),
                new Tuple<string, string, bool>("ab", "ab?", false),
                new Tuple<string, string, bool>("", "?", false),

                // Mixed wildcards
                new Tuple<string, string, bool>("a", "*?", true),
                new Tuple<string, string, bool>("a", "?*", true),
                new Tuple<string, string, bool>("ab", "*?", true),
                new Tuple<string, string, bool>("ab", "?*", true),
                new Tuple<string, string, bool>("abc", "*?", true),
                new Tuple<string, string, bool>("abc", "?*", true),

                // Multiple mixed wildcards
                new Tuple<string, string, bool>("a", "??", false),
                new Tuple<string, string, bool>("ab", "?*?", true),
                new Tuple<string, string, bool>("ab", "*?*?*", true),
                new Tuple<string, string, bool>("abc", "?**?*?", true),
                new Tuple<string, string, bool>("abc", "?**?*c?", false),
                new Tuple<string, string, bool>("abcd", "?b*??", true),
                new Tuple<string, string, bool>("abcd", "?a*??", false),
                new Tuple<string, string, bool>("abcd", "?**?c?", true),
                new Tuple<string, string, bool>("abcd", "?**?d?", false),
                new Tuple<string, string, bool>("abcde", "?*b*?*d*?", true),

                // ? wildcard in the input string
                new Tuple<string, string, bool>("?", "?", true),
                new Tuple<string, string, bool>("?a", "?a", true),
                new Tuple<string, string, bool>("a?", "a?", true),
                new Tuple<string, string, bool>("a?b", "a?", false),
                new Tuple<string, string, bool>("a?ab", "a?aab", false),
                new Tuple<string, string, bool>("aa?bbbc?d", "aa?bbc?dd", false),

                // * wildcard in the input string
                new Tuple<string, string, bool>("*", "*", true),
                new Tuple<string, string, bool>("*a", "*a", true),
                new Tuple<string, string, bool>("a*", "a*", true),
                new Tuple<string, string, bool>("a*b", "a*", true),
                new Tuple<string, string, bool>("a*ab", "a*aab", false),
                new Tuple<string, string, bool>("a*abab", "a*b", true),
                new Tuple<string, string, bool>("aa*bbbc*d", "aa*bbc*dd", false),
                new Tuple<string, string, bool>("aa*bbbc*d", "a*bbc*d", true)
            };
            foreach (var input in inputs)
            {
                try
                {
                    Assert.Equal(input.Item3, FileMatcher.IsMatch(input.Item1, input.Item2));
                    Assert.Equal(input.Item3, FileMatcher.IsMatch(input.Item1.ToUpperInvariant(), input.Item2));
                    Assert.Equal(input.Item3, FileMatcher.IsMatch(input.Item1, input.Item2.ToUpperInvariant()));
                }
                catch (Exception)
                {
                    Console.WriteLine($"Input {input.Item1} with pattern {input.Item2} failed");
                    throw;
                }
            }
        }

        /*
         * Method:  GetFileSystemEntries
         *
         * Simulate Directories.GetFileSystemEntries where file names are short.
         *
         */
        private static IReadOnlyList<string> GetFileSystemEntries(FileMatcher.FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory)
        {
            if
            (
                pattern == @"LONGDI~1"
                && (@"D:\" == path || @"\\server\share\" == path || path.Length == 0))
            {
                return new string[] { Path.Combine(path, "LongDirectoryName") };
            }
            else if
            (
                pattern == @"LONGSU~1"
                && (@"D:\LongDirectoryName" == path || @"\\server\share\LongDirectoryName" == path || @"LongDirectoryName" == path))
            {
                return new string[] { Path.Combine(path, "LongSubDirectory") };
            }
            else if
            (
                pattern == @"LONGFI~1.TXT"
                && (@"D:\LongDirectoryName\LongSubDirectory" == path || @"\\server\share\LongDirectoryName\LongSubDirectory" == path || @"LongDirectoryName\LongSubDirectory" == path))
            {
                return new string[] { Path.Combine(path, "LongFileName.txt") };
            }
            else if
            (
                pattern == @"pomegr~1"
                && @"c:\apple\banana\tomato" == path)
            {
                return new string[] { Path.Combine(path, "pomegranate") };
            }
            else if
            (
                @"c:\apple\banana\tomato\pomegranate\orange" == path)
            {
                // No files exist here. This is an empty directory.
                return Array.Empty<string>();
            }
            else
            {
                Console.WriteLine("GetFileSystemEntries('{0}', '{1}')", path, pattern);
                Assert.Fail("Unexpected input into GetFileSystemEntries");
            }
            return new string[] { "<undefined>" };
        }

        private static readonly char S = Path.DirectorySeparatorChar;

        public static IEnumerable<object[]> NormalizeTestData()
        {
            yield return new object[]
            {
                null,
                null
            };
            yield return new object[]
            {
                "",
                ""
            };
            yield return new object[]
            {
                " ",
                " "
            };

            yield return new object[]
            {
                @"\\",
                @"\\"
            };
            yield return new object[]
            {
                @"\\/\//",
                @"\\"
            };
            yield return new object[]
            {
                @"\\a/\b/\",
                $@"\\a{S}b"
            };

            yield return new object[]
            {
                @"\",
                @"\"
            };
            yield return new object[]
            {
                @"\/\/\/",
                @"\"
            };
            yield return new object[]
            {
                @"\a/\b/\",
                $@"\a{S}b"
            };

            yield return new object[]
            {
                "/",
                "/"
            };
            yield return new object[]
            {
                @"/\/\",
                "/"
            };
            yield return new object[]
            {
                @"/a\/b/\\",
                $@"/a{S}b"
            };

            yield return new object[]
            {
                @"c:\",
                @"c:\"
            };
            yield return new object[]
            {
                @"c:/",
                @"c:\"
            };
            yield return new object[]
            {
                @"c:/\/\/",
                @"c:\"
            };
            yield return new object[]
            {
                @"c:/ab",
                @"c:\ab"
            };
            yield return new object[]
            {
                @"c:\/\a//b",
                $@"c:\a{S}b"
            };
            yield return new object[]
            {
                @"c:\/\a//b\/",
                $@"c:\a{S}b"
            };

            yield return new object[]
            {
                @"..\/a\../.\b\/",
                $@"..{S}a{S}..{S}.{S}b"
            };
            yield return new object[]
            {
                @"**/\foo\/**\/",
                $@"**{S}foo{S}**"
            };

            yield return new object[]
            {
                "AbCd",
                "AbCd"
            };
        }

        [Theory]
        [MemberData(nameof(NormalizeTestData))]
        public void NormalizeTest(string inputString, string expectedString)
        {
            FileMatcher.Normalize(inputString).ShouldBe(expectedString);
        }

        /// <summary>
        /// Simple test of the MatchDriver code.
        /// </summary>
        [Fact]
        public void BasicMatchDriver()
        {
            MatchDriver(
                "Source" + Path.DirectorySeparatorChar + "**",
                new string[]  // Files that exist and should match.
                {
                    "Source" + Path.DirectorySeparatorChar + "Bart.txt",
                    "Source" + Path.DirectorySeparatorChar + "Sub" + Path.DirectorySeparatorChar + "Homer.txt",
                },
                new string[]  // Files that exist and should not match.
                {
                    "Destination" + Path.DirectorySeparatorChar + "Bart.txt",
                    "Destination" + Path.DirectorySeparatorChar + "Sub" + Path.DirectorySeparatorChar + "Homer.txt",
                },
                null);
        }

        /// <summary>
        /// This pattern should *not* recurse indefinitely since there is no '**' in the pattern:
        ///
        ///        c:\?emp\foo
        ///
        /// </summary>
        [Fact]
        public void Regress162390()
        {
            MatchDriver(
                @"c:\?emp\foo.txt",
                new string[] { @"c:\temp\foo.txt" },    // Should match
                new string[] { @"c:\timp\foo.txt" },    // Shouldn't match
                new string[]                            // Should not even consider.
                {
                    @"c:\temp\sub\foo.txt"
                });
        }

        /*
        * Method:  GetLongFileNameForShortLocalPath
        *
        * Convert a short local path to a long path.
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForShortLocalPath()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"D:\LONGDI~1\LONGSU~1\LONGFI~1.TXT",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"D:\LongDirectoryName\LongSubDirectory\LongFileName.txt", longPath);
        }

        /*
        * Method:  GetLongFileNameForLongLocalPath
        *
        * Convert a long local path to a long path (nop).
        *
        */
        [Fact]
        public void GetLongFileNameForLongLocalPath()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"D:\LongDirectoryName\LongSubDirectory\LongFileName.txt",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"D:\LongDirectoryName\LongSubDirectory\LongFileName.txt", longPath);
        }

        /*
        * Method:  GetLongFileNameForShortUncPath
        *
        * Convert a short UNC path to a long path.
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForShortUncPath()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"\\server\share\LONGDI~1\LONGSU~1\LONGFI~1.TXT",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"\\server\share\LongDirectoryName\LongSubDirectory\LongFileName.txt", longPath);
        }

        /*
        * Method:  GetLongFileNameForLongUncPath
        *
        * Convert a long UNC path to a long path (nop)
        *
        */
        [Fact]
        public void GetLongFileNameForLongUncPath()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"\\server\share\LongDirectoryName\LongSubDirectory\LongFileName.txt",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"\\server\share\LongDirectoryName\LongSubDirectory\LongFileName.txt", longPath);
        }

        /*
        * Method:  GetLongFileNameForRelativePath
        *
        * Convert a short relative path to a long path
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForRelativePath()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"LONGDI~1\LONGSU~1\LONGFI~1.TXT",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"LongDirectoryName\LongSubDirectory\LongFileName.txt", longPath);
        }

        /*
        * Method:  GetLongFileNameForRelativePathPreservesTrailingSlash
        *
        * Convert a short relative path with a trailing backslash to a long path
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForRelativePathPreservesTrailingSlash()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"LONGDI~1\LONGSU~1\",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"LongDirectoryName\LongSubDirectory\", longPath);
        }

        /*
        * Method:  GetLongFileNameForRelativePathPreservesExtraSlashes
        *
        * Convert a short relative path with doubled embedded backslashes to a long path
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForRelativePathPreservesExtraSlashes()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"LONGDI~1\\LONGSU~1\\",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"LongDirectoryName\\LongSubDirectory\\", longPath);
        }

        /*
        * Method:  GetLongFileNameForMixedLongAndShort
        *
        * Only part of the path might be short.
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameForMixedLongAndShort()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"c:\apple\banana\tomato\pomegr~1\orange\",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"c:\apple\banana\tomato\pomegranate\orange\", longPath);
        }

        /*
        * Method:  GetLongFileNameWherePartOfThePathDoesntExist
        *
        * Part of the path may not exist. In this case, we treat the non-existent parts
        * as if they were already a long file name.
        *
        */
        [WindowsOnlyFact("Short names are for Windows only.")]
        public void GetLongFileNameWherePartOfThePathDoesntExist()
        {
            string longPath = FileMatcher.GetLongPathName(
                @"c:\apple\banana\tomato\pomegr~1\orange\chocol~1\vanila~1",
                new FileMatcher.GetFileSystemEntries(FileMatcherTest.GetFileSystemEntries));

            Assert.Equal(@"c:\apple\banana\tomato\pomegranate\orange\chocol~1\vanila~1", longPath);
        }

        [Fact]
        public void BasicMatch()
        {
            ValidateFileMatch("file.txt", "File.txt", false);
            ValidateNoFileMatch("file.txt", "File.bin", false);
        }

        [Fact]
        public void MatchSingleCharacter()
        {
            ValidateFileMatch("file.?xt", "File.txt", false);
            ValidateNoFileMatch("file.?xt", "File.bin", false);
        }

        [Fact]
        public void MatchMultipleCharacters()
        {
            ValidateFileMatch("*.txt", "*.txt", false);
            ValidateNoFileMatch("*.txt", "*.bin", false);
        }

        [Fact]
        public void SimpleRecursive()
        {
            ValidateFileMatch("**", ".\\File.txt", true);
        }

        [Fact]
        public void DotForCurrentDirectory()
        {
            ValidateFileMatch(Path.Combine(".", "File.txt"), Path.Combine(".", "File.txt"), false);
            ValidateNoFileMatch(Path.Combine(".", "File.txt"), Path.Combine(".", "File.bin"), false);
        }

        [Fact]
        public void DotDotForParentDirectory()
        {
            ValidateFileMatch(Path.Combine("..", "..", "*.*"), Path.Combine("..", "..", "File.txt"), false);
            if (NativeMethodsShared.IsWindows)
            {
                // On Linux *. * does not pick up files with no extension
                ValidateFileMatch(Path.Combine("..", "..", "*.*"), Path.Combine("..", "..", "File"), false);
            }
            ValidateNoFileMatch(Path.Combine("..", "..", "*.*"), Path.Combine(new[] { "..", "..", "dir1", "dir2", "File.txt" }), false);
            ValidateNoFileMatch(Path.Combine("..", "..", "*.*"), Path.Combine(new[] { "..", "..", "dir1", "dir2", "File" }), false);
        }

        [Fact]
        public void ReduceDoubleSlashesBaseline()
        {
            // Baseline
            ValidateFileMatch(
                NativeMethodsShared.IsWindows ? "f:\\dir1\\dir2\\file.txt" : "/dir1/dir2/file.txt",
                NativeMethodsShared.IsWindows ? "f:\\dir1\\dir2\\file.txt" : "/dir1/dir2/file.txt",
                false);
            ValidateFileMatch(Path.Combine("**", "*.cs"), Path.Combine("dir1", "dir2", "file.cs"), true);
            ValidateFileMatch(Path.Combine("**", "*.cs"), "file.cs", true);
        }

        [Fact]
        public void ReduceDoubleSlashes()
        {
            ValidateFileMatch("f:\\\\dir1\\dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\\\dir1\\\\\\dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\\\dir1\\\\\\dir2\\\\\\\\\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("..\\**/\\*.cs", "..\\dir1\\dir2\\file.cs", true);
            ValidateFileMatch("..\\**/.\\*.cs", "..\\dir1\\dir2\\file.cs", true);
            ValidateFileMatch("..\\**\\./.\\*.cs", "..\\dir1\\dir2\\file.cs", true);
        }

        [Fact]
        public void DoubleSlashesOnBothSidesOfComparison()
        {
            ValidateFileMatch("f:\\\\dir1\\dir2\\file.txt", "f:\\\\dir1\\dir2\\file.txt", false, false);
            ValidateFileMatch("f:\\\\dir1\\\\\\dir2\\file.txt", "f:\\\\dir1\\\\\\dir2\\file.txt", false, false);
            ValidateFileMatch("f:\\\\dir1\\\\\\dir2\\\\\\\\\\file.txt", "f:\\\\dir1\\\\\\dir2\\\\\\\\\\file.txt", false, false);
            ValidateFileMatch("..\\**/\\*.cs", "..\\dir1\\dir2\\\\file.cs", true, false);
            ValidateFileMatch("..\\**/.\\*.cs", "..\\dir1\\dir2//\\file.cs", true, false);
            ValidateFileMatch("..\\**\\./.\\*.cs", "..\\dir1/\\/\\/dir2\\file.cs", true, false);
        }

        [Fact]
        public void DecomposeDotSlash()
        {
            ValidateFileMatch("f:\\.\\dir1\\dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\dir1\\.\\dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\dir1\\dir2\\.\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\.//dir1\\dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\dir1\\.//dir2\\file.txt", "f:\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch("f:\\dir1\\dir2\\.//file.txt", "f:\\dir1\\dir2\\file.txt", false);

            ValidateFileMatch(".\\dir1\\dir2\\file.txt", ".\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch(".\\.\\dir1\\dir2\\file.txt", ".\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch(".//dir1\\dir2\\file.txt", ".\\dir1\\dir2\\file.txt", false);
            ValidateFileMatch(".//.//dir1\\dir2\\file.txt", ".\\dir1\\dir2\\file.txt", false);
        }

        [Fact]
        public void RecursiveDirRecursive()
        {
            // Check that a wildcardpath of **\x\**\ matches correctly since, \**\ is a
            // separate code path.
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\x\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\y\x\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\x\y\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\y\x\y\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\x\x\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\x\x\file.txt", true);
            ValidateFileMatch(@"c:\foo\**\x\**\*.*", @"c:\foo\x\x\x\file.txt", true);
        }

        [Fact]
        public void Regress155731()
        {
            ValidateFileMatch(@"a\b\**\**\**\**\**\e\*", @"a\b\c\d\e\f.txt", true);
            ValidateFileMatch(@"a\b\**\e\*", @"a\b\c\d\e\f.txt", true);
            ValidateFileMatch(@"a\b\**\**\e\*", @"a\b\c\d\e\f.txt", true);
            ValidateFileMatch(@"a\b\**\**\**\e\*", @"a\b\c\d\e\f.txt", true);
            ValidateFileMatch(@"a\b\**\**\**\**\e\*", @"a\b\c\d\e\f.txt", true);
        }

        [Fact]
        public void ParentWithoutSlash()
        {
            // However, we don't wtool this to match,
            ValidateNoFileMatch(@"C:\foo\**", @"C:\foo", true);
            // because we don't know whether foo is a file or folder.

            // Same for UNC
            ValidateNoFileMatch(
                "\\\\server\\c$\\Documents and Settings\\User\\**",
                "\\\\server\\c$\\Documents and Settings\\User",
                true);
        }

        [Fact]
        public void Unc()
        {
            // Check UNC functionality
            ValidateFileMatch(
                "\\\\server\\c$\\**\\*.cs",
                "\\\\server\\c$\\Documents and Settings\\User\\Source.cs",
                true);

            ValidateNoFileMatch(
                "\\\\server\\c$\\**\\*.cs",
                "\\\\server\\c$\\Documents and Settings\\User\\Source.txt",
                true);
            ValidateFileMatch(
                "\\\\**",
                "\\\\server\\c$\\Documents and Settings\\User\\Source.cs",
                true);
            ValidateFileMatch(
                "\\\\**\\*.*",
                "\\\\server\\c$\\Documents and Settings\\User\\Source.cs",
                true);

            ValidateFileMatch(
                "**",
                "\\\\server\\c$\\Documents and Settings\\User\\Source.cs",
                true);
        }

        [Fact]
        public void ExplicitToolCompatibility()
        {
            // Explicit ANT compatibility. These patterns taken from the ANT documentation.
            ValidateFileMatch("**/SourceSafe/*", "./SourceSafe/Repository", true);
            ValidateFileMatch("**\\SourceSafe/*", "./SourceSafe/Repository", true);
            ValidateFileMatch("**/SourceSafe/*", ".\\SourceSafe\\Repository", true);
            ValidateFileMatch("**/SourceSafe/*", "./org/IIS/SourceSafe/Entries", true);
            ValidateFileMatch("**/SourceSafe/*", "./org/IIS/pluggin/tools/tool/SourceSafe/Entries", true);
            ValidateNoFileMatch("**/SourceSafe/*", "./org/IIS/SourceSafe/foo/bar/Entries", true);
            ValidateNoFileMatch("**/SourceSafe/*", "./SourceSafeRepository", true);
            ValidateNoFileMatch("**/SourceSafe/*", "./aSourceSafe/Repository", true);

            ValidateFileMatch("org/IIS/pluggin/**", "org/IIS/pluggin/tools/tool/docs/index.html", true);
            ValidateFileMatch("org/IIS/pluggin/**", "org/IIS/pluggin/test.xml", true);
            ValidateFileMatch("org/IIS/pluggin/**", "org/IIS/pluggin\\test.xml", true);
            ValidateNoFileMatch("org/IIS/pluggin/**", "org/IIS/abc.cs", true);

            ValidateFileMatch("org/IIS/**/SourceSafe/*", "org/IIS/SourceSafe/Entries", true);
            ValidateFileMatch("org/IIS/**/SourceSafe/*", "org\\IIS/SourceSafe/Entries", true);
            ValidateFileMatch("org/IIS/**/SourceSafe/*", "org/IIS\\SourceSafe/Entries", true);
            ValidateFileMatch("org/IIS/**/SourceSafe/*", "org/IIS/pluggin/tools/tool/SourceSafe/Entries", true);
            ValidateNoFileMatch("org/IIS/**/SourceSafe/*", "org/IIS/SourceSafe/foo/bar/Entries", true);
            ValidateNoFileMatch("org/IIS/**/SourceSafe/*", "org/IISSourceSage/Entries", true);
        }

        [Fact]
        public void ExplicitToolIncompatibility()
        {
            // NOTE: Weirdly, ANT syntax is to match a file here.
            // We don't because MSBuild philosophy is that a trailing slash indicates a directory
            ValidateNoFileMatch("**/test/**", ".\\test", true);

            // NOTE: We deviate from ANT format here. ANT would append a ** to any path
            // that ends with '/' or '\'. We think this is the wrong thing because 'folder\'
            // is a valid folder name.
            ValidateNoFileMatch("org/", "org/IISSourceSage/Entries", false);
            ValidateNoFileMatch("org\\", "org/IISSourceSage/Entries", false);
        }

        [Fact]
        public void MultipleStarStar()
        {
            // Multiple-** matches
            ValidateFileMatch("c:\\**\\user\\**\\*.*", "c:\\Documents and Settings\\user\\NTUSER.DAT", true);
            ValidateNoFileMatch("c:\\**\\user1\\**\\*.*", "c:\\Documents and Settings\\user\\NTUSER.DAT", true);
            ValidateFileMatch("c:\\**\\user\\**\\*.*", "c://Documents and Settings\\user\\NTUSER.DAT", true);
            ValidateNoFileMatch("c:\\**\\user1\\**\\*.*", "c:\\Documents and Settings//user\\NTUSER.DAT", true);
        }

        [Fact]
        public void RegressItemRecursionWorksAsExpected()
        {
            // Regress bug#54411:  Item recursion doesn't work as expected on "c:\foo\**"
            ValidateFileMatch("c:\\foo\\**", "c:\\foo\\two\\subfile.txt", true);
        }

        [Fact]
        public void IllegalPaths()
        {
            // Certain patterns are illegal.
            ValidateIllegal("**.cs");
            ValidateIllegal("***");
            ValidateIllegal("****");
            ValidateIllegal("*.cs**");
            ValidateIllegal("*.cs**");
            ValidateIllegal("...\\*.cs");
            ValidateIllegal("http://www.website.com");
        }

        [Fact]
        public void SplitFileSpec()
        {
            /*************************************************************************************
            * Call ValidateSplitFileSpec with various supported combinations.
            *************************************************************************************/
            ValidateSplitFileSpec("foo.cs", "", "", "foo.cs");
            ValidateSplitFileSpec("**\\foo.cs", "", "**\\", "foo.cs");
            ValidateSplitFileSpec("f:\\dir1\\**\\foo.cs", "f:\\dir1\\", "**\\", "foo.cs");
            ValidateSplitFileSpec("..\\**\\foo.cs", "..\\", "**\\", "foo.cs");
            ValidateSplitFileSpec("f:\\dir1\\foo.cs", "f:\\dir1\\", "", "foo.cs");
            ValidateSplitFileSpec("f:\\dir?\\foo.cs", "f:\\", "dir?\\", "foo.cs");
            ValidateSplitFileSpec("dir?\\foo.cs", "", "dir?\\", "foo.cs");
            ValidateSplitFileSpec(@"**\test\**", "", @"**\test\**\", "*.*");
            ValidateSplitFileSpec("bin\\**\\*.cs", "bin\\", "**\\", "*.cs");
            ValidateSplitFileSpec("bin\\**\\*.*", "bin\\", "**\\", "*.*");
            ValidateSplitFileSpec("bin\\**", "bin\\", "**\\", "*.*");
            ValidateSplitFileSpec("bin\\**\\", "bin\\", "**\\", "");
            ValidateSplitFileSpec("bin\\**\\*", "bin\\", "**\\", "*");
            ValidateSplitFileSpec("**", "", "**\\", "*.*");
        }

        [Fact]
        public void Regress367780_CrashOnStarDotDot()
        {
            string workingPath = _env.CreateFolder().Path;
            string workingPathSubfolder = Path.Combine(workingPath, "SubDir");
            string offendingPattern = Path.Combine(workingPath, @"*\..\bar");
            string[] files = Array.Empty<string>();

            Directory.CreateDirectory(workingPath);
            Directory.CreateDirectory(workingPathSubfolder);

            files = FileMatcher.Default.GetFiles(workingPath, offendingPattern).FileList;
        }

        [Fact]
        public void Regress141071_StarStarSlashStarStarIsLiteral()
        {
            string workingPath = _env.CreateFolder().Path;
            string fileName = Path.Combine(workingPath, "MyFile.txt");
            string offendingPattern = Path.Combine(workingPath, @"**\**");

            Directory.CreateDirectory(workingPath);
            File.WriteAllText(fileName, "Hello there.");
            var files = FileMatcher.Default.GetFiles(workingPath, offendingPattern).FileList;

            string result = String.Join(", ", files);
            Console.WriteLine(result);
            Assert.DoesNotContain("**", result);
            Assert.Contains("MyFile.txt", result);
        }

        [Fact]
        public void Regress14090_TrailingDotMatchesNoExtension()
        {
            string workingPath = _env.CreateFolder().Path;
            string workingPathSubdir = Path.Combine(workingPath, "subdir");
            string workingPathSubdirBing = Path.Combine(workingPathSubdir, "bing");

            string offendingPattern = Path.Combine(workingPath, @"**\sub*\*.");

            Directory.CreateDirectory(workingPath);
            Directory.CreateDirectory(workingPathSubdir);
            File.AppendAllText(workingPathSubdirBing, "y");
            var files = FileMatcher.Default.GetFiles(workingPath, offendingPattern).FileList;

            string result = String.Join(", ", files);
            Console.WriteLine(result);
            Assert.Single(files);
        }

        [Fact]
        public void Regress14090_TrailingDotMatchesNoExtension_Part2()
        {
            ValidateFileMatch(@"c:\mydir\**\*.", @"c:\mydir\subdir\bing", true, /* simulate filesystem? */ false);
            ValidateNoFileMatch(@"c:\mydir\**\*.", @"c:\mydir\subdir\bing.txt", true);
        }

        [Fact]
        public void FileEnumerationCacheTakesExcludesIntoAccount()
        {
            try
            {
                using (var env = TestEnvironment.Create())
                {
                    env.SetEnvironmentVariable("MsBuildCacheFileEnumerations", "1");

                    var testProject = env.CreateTestProjectWithFiles(string.Empty, new[] { "a.cs", "b.cs", "c.cs" });

                    var files = FileMatcher.Default.GetFiles(testProject.TestRoot, "**/*.cs").FileList;
                    Array.Sort(files);
                    Assert.Equal(new[] { "a.cs", "b.cs", "c.cs" }, files);

                    files = FileMatcher.Default.GetFiles(testProject.TestRoot, "**/*.cs", new List<string> { "a.cs" }).FileList;
                    Array.Sort(files);
                    Assert.Equal(new[] { "b.cs", "c.cs" }, files);

                    files = FileMatcher.Default.GetFiles(testProject.TestRoot, "**/*.cs", new List<string> { "a.cs", "c.cs" }).FileList;
                    Array.Sort(files);
                    Assert.Equal(new[] { "b.cs" }, files);
                }
            }
            finally
            {
                FileMatcher.ClearFileEnumerationsCache();
            }
        }

        [Theory]
        [InlineData(@"\", "**")]
        [InlineData(@"\\", "**")]
        [InlineData(@"\\\\\\\\", "**")]
        [InlineData("/", "**/*.cs")]
        [InlineData("/", "**")]
        [InlineData("//", "**")]
        [InlineData("////////", "**")]
        public void DriveEnumeratingWildcardIsObservedOnAnyPlatform(string directoryPart, string wildcardPart) =>
            DriveEnumeratingWildcardIsObserved(directoryPart, wildcardPart);

        [WindowsOnlyTheory]
        [InlineData(@"\", "**")]
        [InlineData(@"c:\", "**")]
        [InlineData(@"c:\\", "**")]
        [InlineData(@"c:\\\\\\\\", "**")]
        [InlineData(@"c:\", @"**\*.cs")]
        public void DriveEnumeratingWildcardIsObservedOnWindows(string directoryPart, string wildcardPart)
        {
            DriveEnumeratingWildcardIsObserved(directoryPart, wildcardPart);
            DriveEnumeratingWildcardFailsAndReturns(directoryPart, wildcardPart);
        }

        private void DriveEnumeratingWildcardIsObserved(string directoryPart, string wildcardPart) =>
            FileMatcher.IsDriveEnumeratingWildcardPattern(directoryPart, wildcardPart).ShouldBeTrue();

        [UnixOnlyTheory]
        [InlineData(@"\", "**")]
        [InlineData("/", "**/*.cs")]
        [InlineData("/", "**")]
        [InlineData("//", "**")]
        [InlineData("////////", "**")]
        public void DriveEnumeratingWildcardFailsAndReturnsOnUnix(string directoryPart, string wildcardPart)
        {
            DriveEnumeratingWildcardFailsAndReturns(directoryPart, wildcardPart);
        }

        private void DriveEnumeratingWildcardFailsAndReturns(string directoryPart, string wildcardPart)
        {
            string driveEnumeratingWildcard = string.Concat(directoryPart, wildcardPart);

            using (var env = TestEnvironment.Create())
            {
                try
                {
                    // Set env var to fail on drive enumerating wildcard detection
                    Helpers.ResetStateForDriveEnumeratingWildcardTests(env, "1");

                    (string[] fileList, FileMatcher.SearchAction action, string excludeFileSpec, _) = FileMatcher.Default.GetFiles(
                        string.Empty,
                        driveEnumeratingWildcard);

                    action.ShouldBe(FileMatcher.SearchAction.FailOnDriveEnumeratingWildcard);
                    fileList.ShouldBeEmpty();
                    excludeFileSpec.ShouldBe(string.Empty);

                    // Handle failing with drive enumerating exclude
                    (fileList, action, excludeFileSpec, _) = FileMatcher.Default.GetFiles(
                        string.Empty,
                        @"/*/*.cs",
                        new List<string> { driveEnumeratingWildcard });

                    action.ShouldBe(FileMatcher.SearchAction.FailOnDriveEnumeratingWildcard);
                    fileList.ShouldBeEmpty();
                    excludeFileSpec.ShouldBe(driveEnumeratingWildcard);
                }
                finally
                {
                    ChangeWaves.ResetStateForTests();
                }
            }
        }

        [WindowsOnlyTheory]
        [InlineData(@"%DRIVE%:\**")]
        [InlineData(@"%DRIVE%:\\**")]
        [InlineData(@"%DRIVE%:\\\\\\\\**")]
        [InlineData(@"%DRIVE%:\**\*.cs")]
        public void DriveEnumeratingWildcardIsLoggedOnWindows(string driveEnumeratingWildcard)
        {
            using (var env = TestEnvironment.Create())
            {
                try
                {
                    driveEnumeratingWildcard = DummyMappedDriveUtils.UpdatePathToMappedDrive(driveEnumeratingWildcard, _mappedDrive.Value.MappedDriveLetter);

                    // Set env var to log on drive enumerating wildcard detection
                    Helpers.ResetStateForDriveEnumeratingWildcardTests(env, "0");

                    (_, FileMatcher.SearchAction action, string excludeFileSpec, _) = FileMatcher.Default.GetFiles(
                        string.Empty,
                        driveEnumeratingWildcard);

                    action.ShouldBe(FileMatcher.SearchAction.LogDriveEnumeratingWildcard);
                    excludeFileSpec.ShouldBe(string.Empty);

                    // Handle logging with drive enumerating exclude
                    (_, action, excludeFileSpec, _) = FileMatcher.Default.GetFiles(
                        string.Empty,
                        @"/*/*.cs",
                        new List<string> { driveEnumeratingWildcard });

                    action.ShouldBe(FileMatcher.SearchAction.LogDriveEnumeratingWildcard);
                    excludeFileSpec.ShouldBe(driveEnumeratingWildcard);
                }
                finally
                {
                    ChangeWaves.ResetStateForTests();
                }
            }
        }

        [Theory]
        [InlineData(@"\", @"*\*.cs")]
        [InlineData(@"\\", @"*\*.cs")]
        [InlineData(@"\", @"*\*.*")]
        [InlineData(@"/", @"*/*.cs")]
        [InlineData(@"//", @"*/*.cs")]
        [InlineData(@"/", @"*/*.*")]
        public void DriveEnumeratingWildcardIsNotObservedOnAnyPlatform(string directoryPart, string wildcardPart) =>
            DriveEnumeratingWildcardIsNotObserved(directoryPart, wildcardPart);

        [UnixOnlyTheory]
        [InlineData(@"c:\", "**")]
        [InlineData(@"c:\\", "**")]
        [InlineData(@"c:\\\\\\\\", "**")]
        [InlineData(@"c:\", @"**\*.cs")]
        public void DriveEnumeratingWildcardIsNotObservedOnUnix(string directoryPart, string wildcardPart)
        {
            DriveEnumeratingWildcardIsNotObserved(directoryPart, wildcardPart);
        }

        private void DriveEnumeratingWildcardIsNotObserved(string directoryPart, string wildcardPart) =>
            FileMatcher.IsDriveEnumeratingWildcardPattern(directoryPart, wildcardPart).ShouldBeFalse();

        [Fact]
        public void RemoveProjectDirectory()
        {
            string[] strings = new string[1] { NativeMethodsShared.IsWindows ? "c:\\1.file" : "/1.file" };
            strings = FileMatcher.RemoveProjectDirectory(strings, NativeMethodsShared.IsWindows ? "c:\\" : "/").ToArray();
            Assert.Equal("1.file", strings[0]);

            strings = new string[1] { NativeMethodsShared.IsWindows ? "c:\\directory\\1.file" : "/directory/1.file" };
            strings = FileMatcher.RemoveProjectDirectory(strings, NativeMethodsShared.IsWindows ? "c:\\" : "/").ToArray();
            Assert.Equal(strings[0], NativeMethodsShared.IsWindows ? "directory\\1.file" : "directory/1.file");

            strings = new string[1] { NativeMethodsShared.IsWindows ? "c:\\directory\\1.file" : "/directory/1.file" };
            strings = FileMatcher.RemoveProjectDirectory(strings, NativeMethodsShared.IsWindows ? "c:\\directory" : "/directory").ToArray();
            Assert.Equal("1.file", strings[0]);

            strings = new string[1] { NativeMethodsShared.IsWindows ? "c:\\1.file" : "/1.file" };
            strings = FileMatcher.RemoveProjectDirectory(strings, NativeMethodsShared.IsWindows ? "c:\\directory" : "/directory").ToArray();
            Assert.Equal(strings[0], NativeMethodsShared.IsWindows ? "c:\\1.file" : "/1.file");

            strings = new string[1] { NativeMethodsShared.IsWindows ? "c:\\directorymorechars\\1.file" : "/directorymorechars/1.file" };
            strings = FileMatcher.RemoveProjectDirectory(strings, NativeMethodsShared.IsWindows ? "c:\\directory" : "/directory").ToArray();
            Assert.Equal(strings[0], NativeMethodsShared.IsWindows ? "c:\\directorymorechars\\1.file" : "/directorymorechars/1.file");

            if (NativeMethodsShared.IsWindows)
            {
                strings = new string[1] { "\\Machine\\1.file" };
                strings = FileMatcher.RemoveProjectDirectory(strings, "\\Machine").ToArray();
                Assert.Equal("1.file", strings[0]);

                strings = new string[1] { "\\Machine\\directory\\1.file" };
                strings = FileMatcher.RemoveProjectDirectory(strings, "\\Machine").ToArray();
                Assert.Equal("directory\\1.file", strings[0]);

                strings = new string[1] { "\\Machine\\directory\\1.file" };
                strings = FileMatcher.RemoveProjectDirectory(strings, "\\Machine\\directory").ToArray();
                Assert.Equal("1.file", strings[0]);

                strings = new string[1] { "\\Machine\\1.file" };
                strings = FileMatcher.RemoveProjectDirectory(strings, "\\Machine\\directory").ToArray();
                Assert.Equal("\\Machine\\1.file", strings[0]);

                strings = new string[1] { "\\Machine\\directorymorechars\\1.file" };
                strings = FileMatcher.RemoveProjectDirectory(strings, "\\Machine\\directory").ToArray();
                Assert.Equal("\\Machine\\directorymorechars\\1.file", strings[0]);
            }
        }

        [Theory]
        [InlineData(
            @"src/**/*.cs", // Include Pattern
            new string[] //  Matching files
            {
                @"src/a.cs",
                @"src/a\b\b.cs",
            })]
        [InlineData(
            @"src/test/**/*.cs", // Include Pattern
            new string[] //  Matching files
            {
                @"src/test/a.cs",
                @"src/test/a\b\c.cs",
            })]
        [InlineData(
            @"src/test/**/a/b/**/*.cs", // Include Pattern
            new string[] //  Matching files
            {
                @"src/test/dir\a\b\a.cs",
                @"src/test/dir\a\b\c\a.cs",
            })]
        public void IncludePatternShouldNotPreserveUserSlashesInFixedDirPart(string include, string[] matching)
        {
            MatchDriver(include, null, matching, null, null, normalizeAllPaths: false, normalizeExpectedMatchingFiles: true);
        }

        [Theory]
        [InlineData(
            @"**\*.cs", // Include Pattern
            new[] //  Exclude patterns
            {
                @"bin\**"
            },
            new string[] // Matching files
            {
            },
            new string[] // Non matching files
            {
            },
            new[] // Non matching files that shouldn't be touched
            {
                @"bin\foo.cs",
                @"bin\bar\foo.cs",
                @"bin\bar\"
            })]
        [InlineData(
            @"**\*.cs", // Include Pattern
            new[] //  Exclude patterns
            {
                @"bin\**"
            },
            new[] // Matching files
            {
                "a.cs",
                @"b\b.cs",
            },
            new[] // Non matching files
            {
                @"b\b.txt"
            },
            new[] // Non matching files that shouldn't be touched
            {
                @"bin\foo.cs",
                @"bin\bar\foo.cs",
                @"bin\bar\"
            })]
        public void ExcludePattern(string include, string[] exclude, string[] matching, string[] nonMatching, string[] untouchable)
        {
            MatchDriver(include, exclude, matching, nonMatching, untouchable);
        }

        [Fact]
        public void ExcludeSpecificFiles()
        {
            MatchDriverWithDifferentSlashes(
                @"**\*.cs",     // Include Pattern
                new[]    //  Exclude patterns
                {
                    @"Program_old.cs",
                    @"Properties\AssemblyInfo_old.cs"
                },
                new[]    // Matching files
                {
                    @"foo.cs",
                    @"Properties\AssemblyInfo.cs",
                    @"Foo\Bar\Baz\Buzz.cs"
                },
                new[]    // Non matching files
                {
                    @"Program_old.cs",
                    @"Properties\AssemblyInfo_old.cs"
                },
                Array.Empty<string>());    // Non matching files that shouldn't be touched
        }

        [Fact]
        public void ExcludePatternAndSpecificFiles()
        {
            MatchDriverWithDifferentSlashes(
                @"**\*.cs",     // Include Pattern
                new[]    //  Exclude patterns
                {
                    @"bin\**",
                    @"Program_old.cs",
                    @"Properties\AssemblyInfo_old.cs"
                },
                new[]    // Matching files
                {
                    @"foo.cs",
                    @"Properties\AssemblyInfo.cs",
                    @"Foo\Bar\Baz\Buzz.cs"
                },
                new[]    // Non matching files
                {
                    @"foo.txt",
                    @"Foo\foo.txt",
                    @"Program_old.cs",
                    @"Properties\AssemblyInfo_old.cs"
                },
                new[]    // Non matching files that shouldn't be touched
                {
                    @"bin\foo.cs",
                    @"bin\bar\foo.cs",
                    @"bin\bar\"
                });
        }

        [Theory]
        [InlineData(
            @"**\*.cs", // Include Pattern
            new[] // Exclude patterns
            {
                @"**\bin\**\*.cs",
                @"src\Common\**",
            },
            new[] // Matching files
            {
                @"foo.cs",
                @"src\Framework\Properties\AssemblyInfo.cs",
                @"src\Framework\Foo\Bar\Baz\Buzz.cs"
            },
            new[] // Non matching files
            {
                @"foo.txt",
                @"src\Framework\Readme.md",
                @"src\Common\foo.cs",

                // Ideally these would be untouchable
                @"src\Framework\bin\foo.cs",
                @"src\Framework\bin\Debug",
                @"src\Framework\bin\Debug\foo.cs",
            },
            new[] // Non matching files that shouldn't be touched
            {
                @"src\Common\Properties\",
                @"src\Common\Properties\AssemblyInfo.cs",
            })]
        [InlineData(
            @"**\*.cs", // Include Pattern
            new[] // Exclude patterns
            {
                @"**\bin\**\*.cs",
                @"src\Co??on\**",
            },
            new[] // Matching files
            {
                @"foo.cs",
                @"src\Framework\Properties\AssemblyInfo.cs",
                @"src\Framework\Foo\Bar\Baz\Buzz.cs"
            },
            new[] // Non matching files
            {
                @"foo.txt",
                @"src\Framework\Readme.md",
                @"src\Common\foo.cs",

                // Ideally these would be untouchable
                @"src\Framework\bin\foo.cs",
                @"src\Framework\bin\Debug",
                @"src\Framework\bin\Debug\foo.cs",
                @"src\Common\Properties\AssemblyInfo.cs"
            },
            new[] // Non matching files that shouldn't be touched
            {
                @"src\Common\Properties\"
            })]
        [InlineData(
            @"src\**\proj\**\*.cs", // Include Pattern
            new[] // Exclude patterns
            {
                @"src\**\proj\**\none\**\*",
            },
            new[] // Matching files
            {
                @"src\proj\m1.cs",
                @"src\proj\a\m2.cs",
                @"src\b\proj\m3.cs",
                @"src\c\proj\d\m4.cs",
            },
            new[] // Non matching files
            {
                @"nm1.cs",
                @"a\nm2.cs",
                @"src\nm3.cs",
                @"src\a\nm4.cs",

                // Ideally these would be untouchable
                @"src\proj\none\nm5.cs",
                @"src\proj\a\none\nm6.cs",
                @"src\b\proj\none\nm7.cs",
                @"src\c\proj\d\none\nm8.cs",
                @"src\e\proj\f\none\g\nm8.cs",
            },
            new string[] // Non matching files that shouldn't be touched
            {
            })]
        // patterns with excludes that ideally would prune entire recursive subtrees (files in pruned tree aren't touched at all) but the exclude pattern is too complex for that to work with the current logic
        public void ExcludeComplexPattern(string include, string[] exclude, string[] matching, string[] nonMatching, string[] untouchable)
        {
            MatchDriverWithDifferentSlashes(include, exclude, matching, nonMatching, untouchable);
        }

        [Theory]
        // Empty string is valid
        [InlineData(
            "",
            "",
            "",
            "",
            "^(?<WILDCARDDIR>)(?<FILENAME>)$",
            false,
            true)]
        // ... anywhere is invalid
        [InlineData(
            @"...\foo",
            "",
            "",
            "",
            "",
            false,
            false)]
        // : not placed at second index is invalid
        [InlineData(
            "http://www.website.com",
            "",
            "",
            "",
            "",
            false,
            false)]
        // ** not alone in filename part is invalid
        [InlineData(
            "**foo",
            "",
            "",
            "**foo",
            "",
            false,
            false)]
        // ** not alone in filename part is invalid
        [InlineData(
            "foo**",
            "",
            "",
            "foo**",
            "",
            false,
            false)]
        // ** not alone between slashes in wildcard part is invalid
        [InlineData(
            @"**foo\bar",
            "",
            @"**foo\",
            "bar",
            "",
            false,
            false)]
        // .. placed after any ** is invalid
        [InlineData(
            @"**\..\bar",
            "",
            @"**\..\",
            "bar",
            "",
            false,
            false)]
        // Common wildcard characters in wildcard and filename part
        [InlineData(
            @"*fo?ba?\*fo?ba?",
            "",
            @"*fo?ba?\",
            "*fo?ba?",
            @"^(?<WILDCARDDIR>[^/\\]*fo.ba.[/\\]+)(?<FILENAME>[^/\\]*fo.ba.)$",
            true,
            true)]
        // Special case for ? and * when trailing . in filename part
        [InlineData(
            "?oo*.",
            "",
            "",
            "?oo*.",
            @"^(?<WILDCARDDIR>)(?<FILENAME>[^\.].oo[^\.]*)$",
            false,
            true)]
        // Skip the .* portion of any *.* sequence in filename part
        [InlineData(
            "*.*foo*.*",
            "",
            "",
            "*.*foo*.*",
            @"^(?<WILDCARDDIR>)(?<FILENAME>[^/\\]*foo[^/\\]*)$",
            false,
            true)]
        // Collapse successive directory separators
        [InlineData(
            @"\foo///bar\\\?foo///bar\\\foo",
            @"\foo///bar\\\",
            @"?foo///bar\\\",
            "foo",
            @"^[/\\]+foo[/\\]+bar[/\\]+(?<WILDCARDDIR>.foo[/\\]+bar[/\\]+)(?<FILENAME>foo)$",
            true,
            true)]
        // Collapse successive relative separators
        [InlineData(
            @"\./.\foo/.\./bar\./.\?foo/.\./bar\./.\foo",
            @"\./.\foo/.\./bar\./.\",
            @"?foo/.\./bar\./.\",
            "foo",
            @"^[/\\]+foo[/\\]+bar[/\\]+(?<WILDCARDDIR>.foo[/\\]+bar[/\\]+)(?<FILENAME>foo)$",
            true,
            true)]
        // Collapse successive recursive operators
        [InlineData(
            @"foo\**/**\bar/**\**/foo\**/**\bar",
            @"foo\",
            @"**/**\bar/**\**/foo\**/**\",
            "bar",
            @"^foo[/\\]+(?<WILDCARDDIR>((.*/)|(.*\\)|())bar((/)|(\\)|(/.*/)|(/.*\\)|(\\.*\\)|(\\.*/))foo((/)|(\\)|(/.*/)|(/.*\\)|(\\.*\\)|(\\.*/)))(?<FILENAME>bar)$",
            true,
            true)]
        // Collapse all three cases combined
        [InlineData(
            @"foo\\\.///**\\\.///**\\\.///bar\\\.///**\\\.///**\\\.///foo\\\.///**\\\.///**\\\.///bar",
            @"foo\\\.///",
            @"**\\\.///**\\\.///bar\\\.///**\\\.///**\\\.///foo\\\.///**\\\.///**\\\.///",
            "bar",
            @"^foo[/\\]+(?<WILDCARDDIR>((.*/)|(.*\\)|())bar((/)|(\\)|(/.*/)|(/.*\\)|(\\.*\\)|(\\.*/))foo((/)|(\\)|(/.*/)|(/.*\\)|(\\.*\\)|(\\.*/)))(?<FILENAME>bar)$",
            true,
            true)]
        public void GetFileSpecInfoCommon(
            string filespec,
            string expectedFixedDirectoryPart,
            string expectedWildcardDirectoryPart,
            string expectedFilenamePart,
            string expectedMatchFileExpression,
            bool expectedNeedsRecursion,
            bool expectedIsLegalFileSpec)
        {
            if (NativeMethodsShared.IsUnixLike)
            {
                expectedFixedDirectoryPart = FileUtilities.FixFilePath(expectedFixedDirectoryPart);
                expectedWildcardDirectoryPart = FileUtilities.FixFilePath(expectedWildcardDirectoryPart);
            }
            TestGetFileSpecInfo(
                filespec,
                expectedFixedDirectoryPart,
                expectedWildcardDirectoryPart,
                expectedFilenamePart,
                expectedMatchFileExpression,
                expectedNeedsRecursion,
                expectedIsLegalFileSpec);
        }

        [WindowsOnlyTheory]
        // Escape pecial regex characters valid in Windows paths
        [InlineData(
            @"$()+.[^{\?$()+.[^{\$()+.[^{",
            @"$()+.[^{\",
            @"?$()+.[^{\",
            "$()+.[^{",
            @"^\$\(\)\+\.\[\^\{[/\\]+(?<WILDCARDDIR>.\$\(\)\+\.\[\^\{[/\\]+)(?<FILENAME>\$\(\)\+\.\[\^\{)$",
            true,
            true)]
        // Preserve UNC paths in fixed directory part
        [InlineData(
            @"\\\.\foo/bar",
            @"\\\.\foo/",
            "",
            "bar",
            @"^\\\\foo[/\\]+(?<WILDCARDDIR>)(?<FILENAME>bar)$",
            false,
            true)]
        public void GetFileSpecInfoWindows(
            string filespec,
            string expectedFixedDirectoryPart,
            string expectedWildcardDirectoryPart,
            string expectedFilenamePart,
            string expectedMatchFileExpression,
            bool expectedNeedsRecursion,
            bool expectedIsLegalFileSpec)
        {
            TestGetFileSpecInfo(
                filespec,
                expectedFixedDirectoryPart,
                expectedWildcardDirectoryPart,
                expectedFilenamePart,
                expectedMatchFileExpression,
                expectedNeedsRecursion,
                expectedIsLegalFileSpec);
        }

        [UnixOnlyTheory]
        // Escape regex characters valid in Unix paths
        [InlineData(
            @"$()+.[^{|\?$()+.[^{|\$()+.[^{|",
            @"$()+.[^{|/",
            @"?$()+.[^{|/",
            "$()+.[^{|",
            @"^\$\(\)\+\.\[\^\{\|[/\\]+(?<WILDCARDDIR>.\$\(\)\+\.\[\^\{\|[/\\]+)(?<FILENAME>\$\(\)\+\.\[\^\{\|)$",
            true,
            true)]
        // Collapse leading successive directory separators in fixed directory part
        [InlineData(
            @"\\\.\foo/bar",
            @"///./foo/",
            "",
            "bar",
            @"^[/\\]+foo[/\\]+(?<WILDCARDDIR>)(?<FILENAME>bar)$",
            false,
            true)]
        public void GetFileSpecInfoUnix(
            string filespec,
            string expectedFixedDirectoryPart,
            string expectedWildcardDirectoryPart,
            string expectedFilenamePart,
            string expectedMatchFileExpression,
            bool expectedNeedsRecursion,
            bool expectedIsLegalFileSpec)
        {
            TestGetFileSpecInfo(
                filespec,
                expectedFixedDirectoryPart,
                expectedWildcardDirectoryPart,
                expectedFilenamePart,
                expectedMatchFileExpression,
                expectedNeedsRecursion,
                expectedIsLegalFileSpec);
        }

        private void TestGetFileSpecInfo(
            string filespec,
            string expectedFixedDirectoryPart,
            string expectedWildcardDirectoryPart,
            string expectedFilenamePart,
            string expectedMatchFileExpression,
            bool expectedNeedsRecursion,
            bool expectedIsLegalFileSpec)
        {
            FileMatcher.Default.GetFileSpecInfo(
                filespec,
                out string fixedDirectoryPart,
                out string wildcardDirectoryPart,
                out string filenamePart,
                out bool needsRecursion,
                out bool isLegalFileSpec);
            string matchFileExpression = isLegalFileSpec
                ? FileMatcher.RegularExpressionFromFileSpec(fixedDirectoryPart, wildcardDirectoryPart, filenamePart)
                : string.Empty;

            fixedDirectoryPart.ShouldBe(expectedFixedDirectoryPart);
            wildcardDirectoryPart.ShouldBe(expectedWildcardDirectoryPart);
            filenamePart.ShouldBe(expectedFilenamePart);
            matchFileExpression.ShouldBe(expectedMatchFileExpression);
            needsRecursion.ShouldBe(expectedNeedsRecursion);
            isLegalFileSpec.ShouldBe(expectedIsLegalFileSpec);
        }

        #region Support functions.

        /// <summary>
        /// This support class simulates a file system.
        /// It accepts multiple sets of files and keeps track of how many files were "hit"
        /// In this case, "hit" means that the caller asked for that file directly.
        /// </summary>
        internal sealed class MockFileSystem
        {
            /// <summary>
            /// Array of files (set1)
            /// </summary>
            private string[] _fileSet1;

            /// <summary>
            /// Array of files (set2)
            /// </summary>
            private string[] _fileSet2;

            /// <summary>
            /// Array of files (set3)
            /// </summary>
            private string[] _fileSet3;

            /// <summary>
            /// Number of times a file from set 1 was requested.
            /// </summary>
            private int _fileSet1Hits = 0;

            /// <summary>
            /// Number of times a file from set 2 was requested.
            /// </summary>
            private int _fileSet2Hits = 0;

            /// <summary>
            /// Number of times a file from set 3 was requested.
            /// </summary>
            private int _fileSet3Hits = 0;

            /// <summary>
            /// Construct.
            /// </summary>
            /// <param name="fileSet1">First set of files.</param>
            /// <param name="fileSet2">Second set of files.</param>
            /// <param name="fileSet3">Third set of files.</param>
            internal MockFileSystem(
                string[] fileSet1,
                string[] fileSet2,
                string[] fileSet3)
            {
                _fileSet1 = fileSet1;
                _fileSet2 = fileSet2;
                _fileSet3 = fileSet3;
            }

            /// <summary>
            /// Number of times a file from set 1 was requested.
            /// </summary>
            internal int FileHits1
            {
                get { return _fileSet1Hits; }
            }

            /// <summary>
            /// Number of times a file from set 2 was requested.
            /// </summary>
            internal int FileHits2
            {
                get { return _fileSet2Hits; }
            }

            /// <summary>
            /// Number of times a file from set 3 was requested.
            /// </summary>
            internal int FileHits3
            {
                get { return _fileSet3Hits; }
            }

            /// <summary>
            /// Return files that match the given files.
            /// </summary>
            /// <param name="candidates">Candidate files.</param>
            /// <param name="path">The path to search within</param>
            /// <param name="pattern">The pattern to search for.</param>
            /// <param name="files">Hashtable receives the files.</param>
            /// <returns></returns>
            private int GetMatchingFiles(string[] candidates, string path, string pattern, ISet<string> files)
            {
                int hits = 0;

                if (candidates != null)
                {
                    foreach (string candidate in candidates)
                    {
                        string normalizedCandidate = Normalize(candidate);

                        // Get the candidate directory.
                        string candidateDirectoryName = "";
                        if (normalizedCandidate.IndexOfAny(FileMatcher.directorySeparatorCharacters) != -1)
                        {
                            candidateDirectoryName = Path.GetDirectoryName(normalizedCandidate);
                        }

                        // Does the candidate directory match the requested path?
                        if (FileUtilities.PathsEqual(path, candidateDirectoryName))
                        {
                            // Match the basic *.* or null. These both match any file.
                            if
                            (
                                pattern == null ||
                                String.Equals(pattern, "*.*", StringComparison.OrdinalIgnoreCase))
                            {
                                ++hits;
                                files.Add(FileMatcher.Normalize(candidate));
                            }
                            else if (pattern.Substring(0, 2) == "*.") // Match patterns like *.cs
                            {
                                string tail = pattern.Substring(1);
                                string candidateTail = candidate.Substring(candidate.Length - tail.Length);
                                if (String.Equals(tail, candidateTail, StringComparison.OrdinalIgnoreCase))
                                {
                                    ++hits;
                                    files.Add(FileMatcher.Normalize(candidate));
                                }
                            }
                            else if (pattern.Substring(pattern.Length - 4, 2) == ".?") // Match patterns like foo.?xt
                            {
                                string leader = pattern.Substring(0, pattern.Length - 4);
                                string candidateLeader = candidate.Substring(candidate.Length - leader.Length - 4, leader.Length);
                                if (String.Equals(leader, candidateLeader, StringComparison.OrdinalIgnoreCase))
                                {
                                    string tail = pattern.Substring(pattern.Length - 2);
                                    string candidateTail = candidate.Substring(candidate.Length - 2);
                                    if (String.Equals(tail, candidateTail, StringComparison.OrdinalIgnoreCase))
                                    {
                                        ++hits;
                                        files.Add(FileMatcher.Normalize(candidate));
                                    }
                                }
                            }
                            else if (!FileMatcher.HasWildcards(pattern))
                            {
                                if (normalizedCandidate == Path.Combine(path, pattern))
                                {
                                    ++hits;
                                    files.Add(candidate);
                                }
                            }
                            else
                            {
                                Assert.Fail(String.Format("Unhandled case in GetMatchingFiles: {0}", pattern));
                            }
                        }
                    }
                }

                return hits;
            }

            /// <summary>
            /// Given a path and pattern, return all the simulated directories out of candidates.
            /// </summary>
            /// <param name="candidates">Candidate file to extract directories from.</param>
            /// <param name="path">The path to search.</param>
            /// <param name="pattern">The pattern to match.</param>
            /// <param name="directories">Receives the directories.</param>
            private void GetMatchingDirectories(string[] candidates, string path, string pattern, ISet<string> directories)
            {
                if (candidates != null)
                {
                    foreach (string candidate in candidates)
                    {
                        string normalizedCandidate = Normalize(candidate);

                        if (IsMatchingDirectory(path, normalizedCandidate))
                        {
                            int nextSlash = normalizedCandidate.IndexOfAny(FileMatcher.directorySeparatorCharacters, path.Length + 1);
                            if (nextSlash != -1)
                            {
                                // UNC paths start with a \\ fragment. Match against \\ when path is empty (i.e., inside the current working directory)
                                string match = normalizedCandidate.StartsWith(@"\\") && string.IsNullOrEmpty(path)
                                    ? @"\\"
                                    : normalizedCandidate.Substring(0, nextSlash);

                                string baseMatch = Path.GetFileName(normalizedCandidate.Substring(0, nextSlash));

                                if
                                (
                                    String.Equals(pattern, "*.*", StringComparison.OrdinalIgnoreCase)
                                    || pattern == null)
                                {
                                    directories.Add(FileMatcher.Normalize(match));
                                }
                                else if    // Match patterns like ?emp
                                (
                                    pattern.Substring(0, 1) == "?"
                                    && pattern.Length == baseMatch.Length)
                                {
                                    string tail = pattern.Substring(1);
                                    string baseMatchTail = baseMatch.Substring(1);
                                    if (String.Equals(tail, baseMatchTail, StringComparison.OrdinalIgnoreCase))
                                    {
                                        directories.Add(FileMatcher.Normalize(match));
                                    }
                                }
                                else
                                {
                                    Assert.Fail(String.Format("Unhandled case in GetMatchingDirectories: {0}", pattern));
                                }
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Method that is delegable for use by FileMatcher. This method simulates a filesystem by returning
            /// files and\or folders that match the requested path and pattern.
            /// </summary>
            /// <param name="entityType">Files, Directories or both</param>
            /// <param name="path">The path to search.</param>
            /// <param name="pattern">The pattern to search (may be null)</param>
            /// <returns>The matched files or folders.</returns>
            internal IReadOnlyList<string> GetAccessibleFileSystemEntries(FileMatcher.FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory)
            {
                string normalizedPath = Normalize(path);

                ISet<string> files = new HashSet<string>();
                if (entityType == FileMatcher.FileSystemEntity.Files || entityType == FileMatcher.FileSystemEntity.FilesAndDirectories)
                {
                    _fileSet1Hits += GetMatchingFiles(_fileSet1, normalizedPath, pattern, files);
                    _fileSet2Hits += GetMatchingFiles(_fileSet2, normalizedPath, pattern, files);
                    _fileSet3Hits += GetMatchingFiles(_fileSet3, normalizedPath, pattern, files);
                }

                if (entityType == FileMatcher.FileSystemEntity.Directories || entityType == FileMatcher.FileSystemEntity.FilesAndDirectories)
                {
                    GetMatchingDirectories(_fileSet1, normalizedPath, pattern, files);
                    GetMatchingDirectories(_fileSet2, normalizedPath, pattern, files);
                    GetMatchingDirectories(_fileSet3, normalizedPath, pattern, files);
                }

                return files.ToList();
            }

            /// <summary>
            /// Given a path, fix it up so that it can be compared to another path.
            /// </summary>
            /// <param name="path">The path to fix up.</param>
            /// <returns>The normalized path.</returns>
            internal static string Normalize(string path)
            {
                if (path.Length == 0)
                {
                    return path;
                }

                string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (Path.DirectorySeparatorChar != '\\')
                {
                    normalized = path.Replace("\\", Path.DirectorySeparatorChar.ToString());
                }
                // Replace leading UNC.
                if (normalized.StartsWith(@"\\"))
                {
                    normalized = "<:UNC:>" + normalized.Substring(2);
                }

                // Preserve parent-directory markers.
                normalized = normalized.Replace(@".." + Path.DirectorySeparatorChar, "<:PARENT:>");

                // Just get rid of doubles enough to satisfy our test cases.
                string doubleSeparator = Path.DirectorySeparatorChar.ToString() + Path.DirectorySeparatorChar.ToString();
                normalized = normalized.Replace(doubleSeparator, Path.DirectorySeparatorChar.ToString());
                normalized = normalized.Replace(doubleSeparator, Path.DirectorySeparatorChar.ToString());
                normalized = normalized.Replace(doubleSeparator, Path.DirectorySeparatorChar.ToString());

                // Strip any .\
                normalized = normalized.Replace(@"." + Path.DirectorySeparatorChar, "");

                // Put back the preserved markers.
                normalized = normalized.Replace("<:UNC:>", @"\\");
                normalized = normalized.Replace("<:PARENT:>", @".." + Path.DirectorySeparatorChar);

                return normalized;
            }

            /// <summary>
            /// Determines whether candidate is in a subfolder of path.
            /// </summary>
            /// <param name="path"></param>
            /// <param name="candidate"></param>
            /// <returns>True if there is a match.</returns>
            private bool IsMatchingDirectory(string path, string candidate)
            {
                string normalizedPath = Normalize(path);
                string normalizedCandidate = Normalize(candidate);

                // Current directory always matches for non-rooted paths.
                if (path.Length == 0 && !Path.IsPathRooted(candidate))
                {
                    return true;
                }

                if (normalizedCandidate.Length > normalizedPath.Length)
                {
                    if (String.Compare(normalizedPath, 0, normalizedCandidate, 0, normalizedPath.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        if (FileUtilities.EndsWithSlash(normalizedPath))
                        {
                            return true;
                        }
                        else if (FileUtilities.IsSlash(normalizedCandidate[normalizedPath.Length]))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            /// <summary>
            /// Searches the candidates array for one that matches path
            /// </summary>
            /// <param name="path"></param>
            /// <param name="candidates"></param>
            /// <returns>The index of the first match or negative one.</returns>
            private int IndexOfFirstMatchingDirectory(string path, string[] candidates)
            {
                if (candidates != null)
                {
                    int i = 0;
                    foreach (string candidate in candidates)
                    {
                        if (IsMatchingDirectory(path, candidate))
                        {
                            return i;
                        }

                        ++i;
                    }
                }

                return -1;
            }

            /// <summary>
            /// Delegable method that returns true if the given directory exists in this simulated filesystem
            /// </summary>
            /// <param name="path">The path to check.</param>
            /// <returns>True if the directory exists.</returns>
            internal bool DirectoryExists(string path)
            {
                if (IndexOfFirstMatchingDirectory(path, _fileSet1) != -1)
                {
                    return true;
                }

                if (IndexOfFirstMatchingDirectory(path, _fileSet2) != -1)
                {
                    return true;
                }

                if (IndexOfFirstMatchingDirectory(path, _fileSet3) != -1)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// A general purpose method used to:
        ///
        /// (1) Simulate a file system.
        /// (2) Check whether all matchingFiles where hit by the filespec pattern.
        /// (3) Check whether all nonmatchingFiles were *not* hit by the filespec pattern.
        /// (4) Check whether all untouchableFiles were not even requested (usually for perf reasons).
        ///
        /// These can be used in various combinations to test the filematcher framework.
        /// </summary>
        /// <param name="filespec">A FileMatcher filespec, possibly with wildcards.</param>
        /// <param name="matchingFiles">Files that exist and should be matched.</param>
        /// <param name="nonmatchingFiles">Files that exists and should not be matched.</param>
        /// <param name="untouchableFiles">Files that exist but should not be requested.</param>
        private static void MatchDriver(
            string filespec,
            string[] matchingFiles,
            string[] nonmatchingFiles,
            string[] untouchableFiles)
        {
            MatchDriver(filespec, null, matchingFiles, nonmatchingFiles, untouchableFiles);
        }

        /// <summary>
        /// Runs the test 4 times with the include and exclude using either forward or backward slashes.
        /// Expects the <param name="filespec"></param> and <param name="excludeFileSpects"></param> to contain only backward slashes
        ///
        /// To preserve current MSBuild behaviour, it only does so if the path is not rooted. Rooted paths do not support forward slashes (as observed on MSBuild 14.0.25420.1)
        /// </summary>
        private static void MatchDriverWithDifferentSlashes(
            string filespec,
            string[] excludeFilespecs,
            string[] matchingFiles,
            string[] nonmatchingFiles,
            string[] untouchableFiles)
        {
            // tests should call this method with backward slashes
            Assert.DoesNotContain(filespec, "/");
            foreach (var excludeFilespec in excludeFilespecs)
            {
                Assert.DoesNotContain(excludeFilespec, "/");
            }

            var forwardSlashFileSpec = Helpers.ToForwardSlash(filespec);
            var forwardSlashExcludeSpecs = excludeFilespecs.Select(Helpers.ToForwardSlash).ToArray();

            MatchDriver(filespec, excludeFilespecs, matchingFiles, nonmatchingFiles, untouchableFiles);
            MatchDriver(filespec, forwardSlashExcludeSpecs, matchingFiles, nonmatchingFiles, untouchableFiles);
            MatchDriver(forwardSlashFileSpec, excludeFilespecs, matchingFiles, nonmatchingFiles, untouchableFiles);
            MatchDriver(forwardSlashFileSpec, forwardSlashExcludeSpecs, matchingFiles, nonmatchingFiles, untouchableFiles);
        }

        private static void MatchDriver(string filespec, string[] excludeFilespecs, string[] matchingFiles, string[] nonmatchingFiles, string[] untouchableFiles, bool normalizeAllPaths = true, bool normalizeExpectedMatchingFiles = false)
        {
            MockFileSystem mockFileSystem = new MockFileSystem(matchingFiles, nonmatchingFiles, untouchableFiles);

            var fileMatcher = new FileMatcher(new FileSystemAdapter(mockFileSystem), mockFileSystem.GetAccessibleFileSystemEntries);

            string[] files = fileMatcher.GetFiles(
                String.Empty, /* we don't need project directory as we use mock filesystem */
                filespec,
                excludeFilespecs?.ToList())
            .FileList;

            Func<string[], string[]> normalizeAllFunc = (paths => normalizeAllPaths ? paths.Select(MockFileSystem.Normalize).ToArray() : paths);
            Func<string[], string[]> normalizeMatching = (paths => normalizeExpectedMatchingFiles ? paths.Select(MockFileSystem.Normalize).ToArray() : paths);

            string[] normalizedFiles = normalizeAllFunc(files);

            // Validate the matching files.
            if (matchingFiles != null)
            {
                string[] normalizedMatchingFiles = normalizeAllFunc(normalizeMatching(matchingFiles));

                foreach (string matchingFile in normalizedMatchingFiles)
                {
                    int timesFound = 0;
                    foreach (string file in normalizedFiles)
                    {
                        if (String.Equals(file, matchingFile, StringComparison.OrdinalIgnoreCase))
                        {
                            ++timesFound;
                        }
                    }
                    Assert.Equal(1, timesFound);
                }
            }

            // Validate the non-matching files
            if (nonmatchingFiles != null)
            {
                string[] normalizedNonMatchingFiles = normalizeAllFunc(nonmatchingFiles);

                foreach (string nonmatchingFile in normalizedNonMatchingFiles)
                {
                    int timesFound = 0;
                    foreach (string file in normalizedFiles)
                    {
                        if (String.Equals(file, nonmatchingFile, StringComparison.OrdinalIgnoreCase))
                        {
                            ++timesFound;
                        }
                    }
                    Assert.Equal(0, timesFound);
                }
            }

            // Check untouchable files.
            Assert.Equal(0, mockFileSystem.FileHits3); // "At least one file that was marked untouchable was referenced."
        }

        /// <summary>
        /// Simulate GetFileSystemEntries
        /// </summary>
        /// <param name="path"></param>
        /// <param name="pattern"></param>
        /// <returns>Array of matching file system entries (can be empty).</returns>
        private static IReadOnlyList<string> GetFileSystemEntriesLoopBack(FileMatcher.FileSystemEntity entityType, string path, string pattern, string projectDirectory, bool stripProjectDirectory)
        {
            return new string[] { Path.Combine(path, pattern) };
        }

        /*************************************************************************************
         * Validate that SplitFileSpec(...) is returning the expected constituent values.
         *************************************************************************************/

        private static FileMatcher loopBackFileMatcher = new FileMatcher(FileSystems.Default, GetFileSystemEntriesLoopBack);

        private static void ValidateSplitFileSpec(
            string filespec,
            string expectedFixedDirectoryPart,
            string expectedWildcardDirectoryPart,
            string expectedFilenamePart)
        {
            string fixedDirectoryPart;
            string wildcardDirectoryPart;
            string filenamePart;

            loopBackFileMatcher.SplitFileSpec(
                filespec,
                out fixedDirectoryPart,
                out wildcardDirectoryPart,
                out filenamePart);

            expectedFixedDirectoryPart = FileUtilities.FixFilePath(expectedFixedDirectoryPart);
            expectedWildcardDirectoryPart = FileUtilities.FixFilePath(expectedWildcardDirectoryPart);
            expectedFilenamePart = FileUtilities.FixFilePath(expectedFilenamePart);

            if
                (
                expectedWildcardDirectoryPart != wildcardDirectoryPart
                || expectedFixedDirectoryPart != fixedDirectoryPart
                || expectedFilenamePart != filenamePart)
            {
                Console.WriteLine("Expect Fixed '{0}' got '{1}'", expectedFixedDirectoryPart, fixedDirectoryPart);
                Console.WriteLine("Expect Wildcard '{0}' got '{1}'", expectedWildcardDirectoryPart, wildcardDirectoryPart);
                Console.WriteLine("Expect Filename '{0}' got '{1}'", expectedFilenamePart, filenamePart);
                Assert.Fail("FileMatcher Regression: Failure while validating SplitFileSpec.");
            }
        }

        /*************************************************************************************
        * Given a pattern (filespec) and a candidate filename (fileToMatch). Verify that they
        * do indeed match.
        *************************************************************************************/
        private static void ValidateFileMatch(
            string filespec,
            string fileToMatch,
            bool shouldBeRecursive)
        {
            ValidateFileMatch(filespec, fileToMatch, shouldBeRecursive, /* Simulate filesystem? */ true);
        }

        /*************************************************************************************
        * Given a pattern (filespec) and a candidate filename (fileToMatch). Verify that they
        * do indeed match.
        *************************************************************************************/
        private static void ValidateFileMatch(
            string filespec,
            string fileToMatch,
            bool shouldBeRecursive,
            bool fileSystemSimulation)
        {
            if (!IsFileMatchAssertIfIllegal(filespec, fileToMatch, shouldBeRecursive))
            {
                Assert.Fail("FileMatcher Regression: Failure while validating that files match.");
            }

            // Now, simulate a filesystem with only fileToMatch. Make sure the file exists that way.
            if (fileSystemSimulation)
            {
                MatchDriver(
                    filespec,
                    new string[] { fileToMatch },
                    null,
                    null);
            }
        }

        /*************************************************************************************
        * Given a pattern (filespec) and a candidate filename (fileToMatch). Verify that they
        * DON'T match.
        *************************************************************************************/
        private static void ValidateNoFileMatch(
            string filespec,
            string fileToMatch,
            bool shouldBeRecursive)
        {
            if (IsFileMatchAssertIfIllegal(filespec, fileToMatch, shouldBeRecursive))
            {
                Assert.Fail("FileMatcher Regression: Failure while validating that files don't match.");
            }

            // Now, simulate a filesystem with only fileToMatch. Make sure the file doesn't exist that way.
            MatchDriver(
                filespec,
                null,
                new string[] { fileToMatch },
                null);
        }

        /*************************************************************************************
        * Verify that the given filespec is illegal.
        *************************************************************************************/
        private static void ValidateIllegal(
            string filespec)
        {
            Regex regexFileMatch;
            bool needsRecursion;
            bool isLegalFileSpec;
            loopBackFileMatcher.GetFileSpecInfoWithRegexObject(
                filespec,
                out regexFileMatch,
                out needsRecursion,
                out isLegalFileSpec);

            if (isLegalFileSpec)
            {
                Assert.Fail("FileMatcher Regression: Expected an illegal filespec, but got a legal one.");
            }

            // Now, FileMatcher is supposed to take any legal file name and just return it immediately.
            // Let's see if it does.
            MatchDriver(
                filespec,                        // Not legal.
                new string[] { filespec },        // Should match
                null,
                null);
        }
        /*************************************************************************************
        * Given a pattern (filespec) and a candidate filename (fileToMatch) return true if
        * FileMatcher would say that they match.
        *************************************************************************************/
        private static bool IsFileMatchAssertIfIllegal(
            string filespec,
            string fileToMatch,
            bool shouldBeRecursive)
        {
            FileMatcher.Result match = FileMatcher.Default.FileMatch(filespec, fileToMatch);

            if (!match.isLegalFileSpec)
            {
                Console.WriteLine("Checking FileSpec: '{0}' against '{1}'", filespec, fileToMatch);
                Assert.Fail("FileMatcher Regression: Invalid filespec.");
            }
            if (shouldBeRecursive != match.isFileSpecRecursive)
            {
                Console.WriteLine("Checking FileSpec: '{0}' against '{1}'", filespec, fileToMatch);
                Assert.True(shouldBeRecursive); // "FileMatcher Regression: Match was recursive when it shouldn't be."
                Assert.False(shouldBeRecursive); // "FileMatcher Regression: Match was not recursive when it should have been."
            }
            return match.isMatch;
        }

        #endregion

        private sealed class FileSystemAdapter : IFileSystem
        {
            private readonly MockFileSystem _mockFileSystem;

            public FileSystemAdapter(MockFileSystem mockFileSystem)
            {
                _mockFileSystem = mockFileSystem;
            }

            public TextReader ReadFile(string path) => throw new NotImplementedException();

            public Stream GetFileStream(string path, FileMode mode, FileAccess access, FileShare share) => throw new NotImplementedException();

            public string ReadFileAllText(string path) => throw new NotImplementedException();

            public byte[] ReadFileAllBytes(string path) => throw new NotImplementedException();

            public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                return FileSystems.Default.EnumerateFiles(path, searchPattern, searchOption);
            }

            public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                return FileSystems.Default.EnumerateDirectories(path, searchPattern, searchOption);
            }

            public IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                return FileSystems.Default.EnumerateFileSystemEntries(path, searchPattern, searchOption);
            }

            public FileAttributes GetAttributes(string path) => throw new NotImplementedException();

            public DateTime GetLastWriteTimeUtc(string path) => throw new NotImplementedException();

            public bool DirectoryExists(string path)
            {
                return _mockFileSystem.DirectoryExists(path);
            }

            public bool FileExists(string path)
            {
                return FileSystems.Default.FileExists(path);
            }

            public bool FileOrDirectoryExists(string path)
            {
                return FileSystems.Default.FileOrDirectoryExists(path);
            }
        }
    }
}
