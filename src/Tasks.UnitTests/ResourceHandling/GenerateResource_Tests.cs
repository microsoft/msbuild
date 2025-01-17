// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;
using Microsoft.Build.UnitTests.Shared;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Xunit.NetCore.Extensions;

#nullable disable

namespace Microsoft.Build.UnitTests.GenerateResource_Tests.InProc
{
    public sealed class RequiredTransformations : IDisposable
    {
        private readonly TestEnvironment _env;
        private readonly ITestOutputHelper _output;

        public RequiredTransformations(ITestOutputHelper output)
        {
            _env = TestEnvironment.Create(output);
            _output = output;
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        /// <summary>
        ///  ResX to Resources, no references
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BasicResX2Resources(bool resourceReadOnly)
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing BasicResX2Resources() test");

            string resxFile = null;

            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            try
            {
                resxFile = Utilities.WriteTestResX(false, null, null);

                if (resourceReadOnly)
                {
                    File.SetAttributes(resxFile, FileAttributes.ReadOnly);
                }

                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.Sources[0].SetMetadata("Attribute", "InputValue");

                Utilities.ExecuteTask(t);

                Assert.Equal("InputValue", t.OutputResources[0].GetMetadata("Attribute"));
                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", resxFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 1, resxFile);
            }
            finally
            {
                // Done, so clean up.
                if (resourceReadOnly && !string.IsNullOrEmpty(resxFile))
                {
                    File.SetAttributes(resxFile, FileAttributes.Normal);
                }

                File.Delete(t.Sources[0].ItemSpec);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        /// Ensure that OutputResource Metadata is populated on the Sources item
        /// </summary>
        [Fact]
        public void OutputResourceMetadataPopulatedOnInputItems()
        {
            string resxFile0 = Utilities.WriteTestResX(false, null, null);
            string resxFile1 = Utilities.WriteTestResX(false, null, null);
            string resxFile2 = Utilities.WriteTestResX(false, null, null);
            string resxFile3 = Utilities.WriteTestResX(false, null, null);

            string expectedOutFile0 = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(resxFile0, ".resources"));
            string expectedOutFile1 = Path.Combine(Path.GetTempPath(), "resx1.foo.resources");
            string expectedOutFile2 = Path.Combine(Path.GetTempPath(), Utilities.GetTempFileName(".resources"));
            string expectedOutFile3 = Path.Combine(Path.GetTempPath(), Utilities.GetTempFileName(".resources"));

            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] {
                new TaskItem(resxFile0), new TaskItem(resxFile1), new TaskItem(resxFile2), new TaskItem(resxFile3) };

            t.OutputResources = new ITaskItem[] {
                new TaskItem(expectedOutFile0), new TaskItem(expectedOutFile1), new TaskItem(expectedOutFile2), new TaskItem(expectedOutFile3) };

            Utilities.ExecuteTask(t);

            Assert.Equal(expectedOutFile0, t.Sources[0].GetMetadata("OutputResource"));
            Assert.Equal(expectedOutFile1, t.Sources[1].GetMetadata("OutputResource"));
            Assert.Equal(expectedOutFile2, t.Sources[2].GetMetadata("OutputResource"));
            Assert.Equal(expectedOutFile3, t.Sources[3].GetMetadata("OutputResource"));

            // Done, so clean up.
            File.Delete(resxFile0);
            File.Delete(resxFile1);
            File.Delete(resxFile2);
            File.Delete(resxFile3);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Text to Resources
        /// </summary>
        [Fact]
        public void BasicText2Resources()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.Sources[0].SetMetadata("Attribute", "InputValue");

                Utilities.ExecuteTask(t);

                Assert.Equal("InputValue", t.OutputResources[0].GetMetadata("Attribute"));
                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  ResX to Resources with references that are used in the resx
        /// </summary>
        /// <remarks>System dll is not locked because it forces a new app domain</remarks>
#if RUNTIME_TYPE_NETCORE
        [Fact(Skip = "Depends on referencing System.dll")]
#else
        [Fact]
#endif
        public void ResX2ResourcesWithReferences()
        {
            string systemDll = Utilities.GetPathToCopiedSystemDLL();
            string resxFile = null;
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);

                resxFile = Utilities.WriteTestResX(true /*system type*/, null, null);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.References = new ITaskItem[] { new TaskItem(systemDll) };

                Utilities.ExecuteTask(t);

                resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                Assert.Equal(t.FilesWritten[0].ItemSpec, resourcesFile);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", resxFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 2, resxFile);
            }
            finally
            {
                File.Delete(systemDll);
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }
            }
        }

        /// <summary>
        ///  Resources to ResX
        /// </summary>
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
        [Fact]
#else
        [Fact(Skip = "ResGen.exe not supported on .NET Core MSBuild")]
#endif
        public void BasicResources2ResX()
        {
            string resourcesFile = Utilities.CreateBasicResourcesFile(false, _output);

            // Fork 1: create a resx file directly from the resources
            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(resourcesFile, ".resx")) };
            Utilities.ExecuteTask(t);
            Assert.Equal(".resx", Path.GetExtension(t.FilesWritten[0].ItemSpec));

            // Fork 2a: create a text file from the resources
            GenerateResource t2a = Utilities.CreateTask(_output);
            t2a.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            t2a.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(resourcesFile, ".txt")) };
            Utilities.ExecuteTask(t2a);
            Assert.Equal(".txt", Path.GetExtension(t2a.FilesWritten[0].ItemSpec));

            // Fork 2b: create a resx file from the text file
            GenerateResource t2b = Utilities.CreateTask(_output);
            t2b.Sources = new ITaskItem[] { new TaskItem(t2a.FilesWritten[0].ItemSpec) };
            t2b.OutputResources = new ITaskItem[] { new TaskItem(Utilities.GetTempFileName(".resx")) };
            Utilities.ExecuteTask(t2b);
            Assert.Equal(".resx", Path.GetExtension(t2b.FilesWritten[0].ItemSpec));

            // make sure the output resx files from each fork are the same
            Assert.Equal(Utilities.ReadFileContent(t.OutputResources[0].ItemSpec),
                                   Utilities.ReadFileContent(t2b.OutputResources[0].ItemSpec));
            Utilities.AssertLogContainsResource(t2b, "GenerateResource.ProcessingFile", t2b.Sources[0].ItemSpec, t2b.OutputResources[0].ItemSpec);
            Utilities.AssertLogContainsResource(t2b, "GenerateResource.ReadResourceMessage", 4, t2b.Sources[0].ItemSpec);

            // Done, so clean up.
            File.Delete(resourcesFile);
            File.Delete(t.OutputResources[0].ItemSpec);
            File.Delete(t2a.OutputResources[0].ItemSpec);
            foreach (ITaskItem item in t2b.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Resources to Text
        /// </summary>
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
        [Fact]
#else
        [Fact(Skip = "ResGen.exe not supported on .NET Core MSBuild")]
#endif
        public void BasicResources2Text()
        {
            string resourcesFile = Utilities.CreateBasicResourcesFile(false, _output);

            GenerateResource t = Utilities.CreateTask(_output);

            t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };

            string outputFile = Path.ChangeExtension(resourcesFile, ".txt");
            t.OutputResources = new ITaskItem[] { new TaskItem(outputFile) };
            Utilities.ExecuteTask(t);

            resourcesFile = t.FilesWritten[0].ItemSpec;
            Assert.Equal(".txt", Path.GetExtension(resourcesFile));
            Assert.Equal(Utilities.GetTestTextContent(null, null, true /*cleaned up */), Utilities.ReadFileContent(resourcesFile));
            Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", t.Sources[0].ItemSpec, outputFile);
            Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, t.Sources[0].ItemSpec);

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Force out-of-date with ShouldRebuildResgenOutputFile on the source only
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void ForceOutOfDate()
        {
            var folder = _env.CreateFolder();
            string resxFileInput = Utilities.WriteTestResX(false, null, null, _env.CreateFile(folder, ".resx").Path);

            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(_env.GetTempFile(".cache").Path);
            t.Sources = new ITaskItem[] { new TaskItem(resxFileInput) };

            Utilities.ExecuteTask(t);

            t.OutputResources.Length.ShouldBe(1);
            var resourceOutput = t.OutputResources[0].ItemSpec;
            Path.GetExtension(resourceOutput).ShouldBe(".resources");
            Path.GetExtension(t.FilesWritten[0].ItemSpec).ShouldBe(".resources");


            /* Unmerged change from project 'Microsoft.Build.Tasks.UnitTests (net7.0)'
            Before:
            Utilities.AssertLogContainsResource(t, "GenerateResource.OutputDoesntExist", t.OutputResources[0].ItemSpec);

            Utilities.AssertStateFileWasWritten(t);
            After:
            Utilities.AssertLogContainsResource(t, "GenerateResource.OutputDoesntExist", t.OutputResources[0].ItemSpec);

            Utilities.AssertStateFileWasWritten(t);
            */
            Utilities.AssertLogContainsResource(t, "GenerateResource.OutputDoesntExist", t.OutputResources[0].ItemSpec);

            Utilities.AssertStateFileWasWritten(t);

            GenerateResource t2 = Utilities.CreateTask(_output);
            t2.StateFile = new TaskItem(t.StateFile);
            t2.Sources = new ITaskItem[] { new TaskItem(resxFileInput) };

            // Execute the task again when the input (5m ago) is newer than the previous outputs (10m ago)
            File.SetLastWriteTime(resxFileInput, DateTime.Now.Subtract(TimeSpan.FromMinutes(5)));
            File.SetLastWriteTime(resourceOutput, DateTime.Now.Subtract(TimeSpan.FromMinutes(10)));
            Utilities.ExecuteTask(t2);

            File.GetLastAccessTime(t2.OutputResources[0].ItemSpec).ShouldBe(DateTime.Now, TimeSpan.FromSeconds(5));

            Utilities.AssertLogContainsResource(t2, "GenerateResource.InputNewer", t2.Sources[0].ItemSpec, t2.OutputResources[0].ItemSpec);
        }

        [Fact]
        public void ForceOutOfDateByDeletion()
        {
            var folder = _env.CreateFolder();
            string resxFileInput = Utilities.WriteTestResX(false, null, null, _env.CreateFile(folder, ".resx").Path);

            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(_env.GetTempFile(".cache").Path);
            t.Sources = new ITaskItem[] { new TaskItem(resxFileInput) };

            Utilities.ExecuteTask(t);

            Utilities.AssertLogContainsResource(t, "GenerateResource.OutputDoesntExist", t.OutputResources[0].ItemSpec);

            GenerateResource t2 = Utilities.CreateTask(_output);
            t2.StateFile = new TaskItem(t.StateFile);
            t2.Sources = new ITaskItem[] { new TaskItem(resxFileInput) };

            // Execute the task again when the input (5m ago) is newer than the previous outputs (10m ago)
            File.Delete(resxFileInput);

            t2.Execute().ShouldBeFalse();

            Utilities.AssertLogContainsResource(t2, "GenerateResource.ResourceNotFound", t2.Sources[0].ItemSpec);
        }

        /// <summary>
        ///  Force out-of-date with ShouldRebuildResgenOutputFile on the linked file
        /// </summary>
        [Theory]
        [MemberData(nameof(Utilities.UsePreserializedResourceStates), MemberType = typeof(Utilities))]
        public void ForceOutOfDateLinked(bool usePreserialized)
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();
            string resxFile = Utilities.WriteTestResX(false, bitmap, null, false);

            GenerateResource t = Utilities.CreateTask(_output, usePreserialized, _env);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                Utilities.ExecuteTask(t);

                Path.GetExtension(t.OutputResources[0].ItemSpec).ShouldBe(".resources");
                Path.GetExtension(t.FilesWritten[0].ItemSpec).ShouldBe(".resources");

                Utilities.AssertStateFileWasWritten(t);

                GenerateResource t2 = Utilities.CreateTask(_output, usePreserialized, _env);
                t2.StateFile = new TaskItem(t.StateFile);
                t2.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                DateTime firstWriteTime = File.GetLastWriteTime(t.OutputResources[0].ItemSpec);
                System.Threading.Thread.Sleep(200);
                File.SetLastWriteTime(bitmap, DateTime.Now + TimeSpan.FromSeconds(2));

                Utilities.ExecuteTask(t2);

                File.GetLastWriteTime(t2.OutputResources[0].ItemSpec).ShouldBeGreaterThan(firstWriteTime);

                Utilities.AssertLogContainsResource(
                    t2,
                    "GenerateResource.LinkedInputNewer",
                    // ToUpper because WriteTestResX uppercases links
                    NativeMethodsShared.IsWindows ? bitmap.ToUpper() : bitmap,
                    t2.OutputResources[0].ItemSpec);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(bitmap);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        [Fact]
        public void QuestionOutOfDateByDeletion()
        {
            var folder = _env.CreateFolder();
            string resxFileInput = Utilities.WriteTestResX(false, null, null, _env.CreateFile(folder, ".resx").Path);
            TaskItem stateFile = new TaskItem(_env.GetTempFile(".cache").Path);
            ITaskItem[] sources = new ITaskItem[] { new TaskItem(resxFileInput) };
            ITaskItem[] output;

            GenerateResource t1 = Utilities.CreateTask(_output);
            t1.Sources = sources;
            t1.StateFile = stateFile;
            Utilities.ExecuteTask(t1);

            Utilities.AssertLogContainsResource(t1, "GenerateResource.OutputDoesntExist", t1.OutputResources[0].ItemSpec);

            output = t1.OutputResources;

            // Run again to ensure all files are up to date.
            GenerateResource t2 = Utilities.CreateTask(_output);
            t2.Sources = sources;
            t2.StateFile = stateFile;
            t2.FailIfNotIncremental = true;
            Utilities.ExecuteTask(t2);

            // Delete the file and verify that FailIfNotIncremental will print the missing file
            GenerateResource t3 = Utilities.CreateTask(_output);
            t3.StateFile = stateFile;
            t3.Sources = sources;
            t3.FailIfNotIncremental = true;

            // Delete the output
            File.Delete(output[0].ItemSpec);

            t3.Execute().ShouldBeFalse();

            Utilities.AssertLogContainsResource(t3, "GenerateResource.ProcessingFile", sources[0].ItemSpec, output[0].ItemSpec);

            GenerateResource t4 = Utilities.CreateTask(_output);
            t4.Sources = sources;
            t4.StateFile = stateFile;
            Utilities.ExecuteTask(t4);

            Utilities.AssertLogContainsResource(t4, "GenerateResource.OutputDoesntExist", t4.OutputResources[0].ItemSpec);

            // Run again to ensure all files are up to date.
            GenerateResource t5 = Utilities.CreateTask(_output);
            t5.Sources = sources;
            t5.StateFile = stateFile;
            t5.FailIfNotIncremental = true;
            Utilities.ExecuteTask(t5);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void WritingNonString_WithoutProperyOrSystemResourcesExtensions_FailsUnlessRunningOnFullFrameworkWithoutProperty(
            bool usePreserialized,
            bool useSystemResourcesExtensions)
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();

            string resxFile = Utilities.WriteTestResX(
                useType: false,
                bitmap,
                extraToken: null,
                useInvalidType: false);

            GenerateResource t = Utilities.CreateTask(
                _output,
                usePreserialized,
                _env,
                useSystemResourcesExtensions);

            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                string outputResource = Path.ChangeExtension(Path.GetFullPath(resxFile), ".resources");

#if NETFRAMEWORK
                if (!usePreserialized)
                {
                    t.Execute().ShouldBeTrue();
                    Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresProperty");
                    Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresExtensions");
                    return;
                }
#endif

                t.Execute().ShouldBeFalse();

                if (usePreserialized)
                {
                    Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresProperty");
                }
                else
                {
                    Utilities.AssertLogContainsResource(t, "GenerateResource.PreserializedResourcesRequiresProperty");
                }

                if (useSystemResourcesExtensions)
                {
                    Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresExtensions");
                }
                else
                {
                    Utilities.AssertLogContainsResource(t, "GenerateResource.PreserializedResourcesRequiresExtensions");
                    Utilities.AssertLogContainsResource(t, "GenerateResource.CorruptOutput", outputResource);
                }

                File.Exists(outputResource)
                    .ShouldBeFalse("Resources file was left on disk even though resource creation failed.");
            }
            finally
            {
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(bitmap);

                foreach (ITaskItem item in t.FilesWritten)
                {
                    File.Delete(item.ItemSpec);
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WritingString_WithoutSystemResourcesExtensions_Succeeds(bool usePreserialized)
        {
            string resxFile = Utilities.WriteTestResX(
                useType: false,
                linkedBitmap: null,
                extraToken: null,
                useInvalidType: false);

            GenerateResource t = Utilities.CreateTask(
                _output,
                usePreserialized,
                _env,
                useSystemResourcesExtensions: false);

            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                t.Execute().ShouldBeTrue();

                Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresProperty");
                Utilities.AssertLogNotContainsResource(t, "GenerateResource.PreserializedResourcesRequiresExtensions");
            }
            finally
            {
                File.Delete(t.Sources[0].ItemSpec);

                foreach (ITaskItem item in t.FilesWritten)
                {
                    File.Delete(item.ItemSpec);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Utilities.UsePreserializedResourceStates), MemberType = typeof(Utilities))]
        public void ForceOutOfDateLinkedByDeletion(bool usePreserialized)
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();
            string resxFile = Utilities.WriteTestResX(false, bitmap, null, false);

            GenerateResource t = Utilities.CreateTask(_output, usePreserialized, _env);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            t.UsePreserializedResources = usePreserialized;

            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                Utilities.ExecuteTask(t);

                Path.GetExtension(t.OutputResources[0].ItemSpec).ShouldBe(".resources");
                Path.GetExtension(t.FilesWritten[0].ItemSpec).ShouldBe(".resources");

                Utilities.AssertStateFileWasWritten(t);

                GenerateResource t2 = Utilities.CreateTask(_output, usePreserialized, _env);
                t2.StateFile = new TaskItem(t.StateFile);
                t2.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t2.UsePreserializedResources = usePreserialized;

                File.Delete(bitmap);

                t2.Execute().ShouldBeFalse();

                // ToUpper because WriteTestResX uppercases links
                Utilities.AssertLogContainsResource(
                    t2,
                    "GenerateResource.LinkedInputDoesntExist",
                    // ToUpper because WriteTestResX uppercases links
                    NativeMethodsShared.IsWindows ? bitmap.ToUpper() : bitmap);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(bitmap);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  Force partially out-of-date: should build only the out of date inputs
        /// </summary>
        [Fact]
        public void ForceSomeOutOfDate()
        {
            var folder = _env.CreateFolder();

            var firstResx = Utilities.WriteTestResX(false, null, null, _env.CreateFile(folder, ".resx").Path);
            var secondResx = Utilities.WriteTestResX(false, null, null, _env.CreateFile(folder, ".resx").Path);
            var cache = _env.GetTempFile(folder, ".cache").Path;

            GenerateResource createResources = Utilities.CreateTask(_output);
            createResources.StateFile = new TaskItem(cache);
            createResources.Sources = new ITaskItem[] { new TaskItem(firstResx), new TaskItem(secondResx) };

            _output.WriteLine("Transform both");
            Utilities.ExecuteTask(createResources);

            _output.WriteLine("Get current write times of outputs");
            DateTime firstOutputCreationTime = File.GetLastWriteTime(createResources.OutputResources[0].ItemSpec);
            DateTime secondOutputCreationTime = File.GetLastWriteTime(createResources.OutputResources[1].ItemSpec);

            _output.WriteLine("Create a new task to transform them again");
            GenerateResource t2 = Utilities.CreateTask(_output);
            t2.StateFile = new TaskItem(createResources.StateFile.ItemSpec);
            t2.Sources = new ITaskItem[] { new TaskItem(firstResx), new TaskItem(secondResx) };

            System.Threading.Thread.Sleep(200);
            if (!NativeMethodsShared.IsWindows)
            {
                // Must be > 1 sec on some file systems for proper timestamp granularity
                // TODO: Implement an interface for fetching deterministic timestamps rather than relying on the file
                System.Threading.Thread.Sleep(1000);
            }

            _output.WriteLine("Touch one input");
            File.SetLastWriteTime(firstResx, DateTime.Now);

            // Increasing the space between the last write and task execution due to precision on file time
            System.Threading.Thread.Sleep(1000);

            Utilities.ExecuteTask(t2);

            _output.WriteLine("Check only one output was updated");
            File.GetLastWriteTime(t2.OutputResources[0].ItemSpec).ShouldBeGreaterThan(firstOutputCreationTime);
            File.GetLastWriteTime(t2.OutputResources[1].ItemSpec).ShouldBe(secondOutputCreationTime);

            // Although only one file was updated, both should be in OutputResources and FilesWritten
            t2.OutputResources[0].ItemSpec.ShouldBe(createResources.OutputResources[0].ItemSpec);
            t2.OutputResources[1].ItemSpec.ShouldBe(createResources.OutputResources[1].ItemSpec);
            t2.FilesWritten[0].ItemSpec.ShouldBe(createResources.FilesWritten[0].ItemSpec);
            t2.FilesWritten[1].ItemSpec.ShouldBe(createResources.FilesWritten[1].ItemSpec);

            Utilities.AssertLogContainsResource(t2, "GenerateResource.InputNewer", firstResx, t2.OutputResources[0].ItemSpec);
        }

        /// <summary>
        ///  Allow ShouldRebuildResgenOutputFile to return "false" since nothing's out of date, including linked file
        /// </summary>
        [Theory]
        [MemberData(nameof(Utilities.UsePreserializedResourceStates), MemberType = typeof(Utilities))]
        public void AllowLinkedNoGenerate(bool usePreserialized)
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();
            string resxFile = Utilities.WriteTestResX(false, bitmap, null, false);

            GenerateResource t = Utilities.CreateTask(_output, usePreserialized, _env);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            t.UsePreserializedResources = usePreserialized;

            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                DateTime time = File.GetLastWriteTime(t.OutputResources[0].ItemSpec);

                GenerateResource t2 = Utilities.CreateTask(_output, usePreserialized, _env);
                t2.StateFile = new TaskItem(t.StateFile);
                t2.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                System.Threading.Thread.Sleep(500);

                Utilities.ExecuteTask(t2);

                Assert.True(time.Equals(File.GetLastWriteTime(t2.OutputResources[0].ItemSpec)));
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(bitmap);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  Allow the task to skip processing based on having nothing out of date
        /// </summary>
        [Fact]
        public void NothingOutOfDate()
        {
            string resxFile = null;
            string txtFile = null;
            string resourcesFile1 = null;
            string resourcesFile2 = null;

            try
            {
                resxFile = Utilities.WriteTestResX(false, null, null);
                txtFile = Utilities.WriteTestText(null, null);

                GenerateResource t = Utilities.CreateTask(_output);
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));
                t.Sources = new ITaskItem[] { new TaskItem(resxFile), new TaskItem(txtFile) };
                resourcesFile1 = Path.ChangeExtension(resxFile, ".resources");
                resourcesFile2 = Path.ChangeExtension(txtFile, ".resources");

                Utilities.ExecuteTask(t);

                Assert.Equal(t.OutputResources[0].ItemSpec, resourcesFile1);
                Assert.Equal(t.FilesWritten[0].ItemSpec, resourcesFile1);
                Assert.Equal(t.OutputResources[1].ItemSpec, resourcesFile2);
                Assert.Equal(t.FilesWritten[1].ItemSpec, resourcesFile2);

                Utilities.AssertStateFileWasWritten(t);

                // Repeat, and it should do nothing as they are up to date
                GenerateResource t2 = Utilities.CreateTask(_output);
                t2.StateFile = new TaskItem(t.StateFile);
                t2.Sources = new ITaskItem[] { new TaskItem(resxFile), new TaskItem(txtFile) };

                DateTime time = File.GetLastWriteTime(t.OutputResources[0].ItemSpec);
                DateTime time2 = File.GetLastWriteTime(t.OutputResources[1].ItemSpec);
                System.Threading.Thread.Sleep(200);

                Utilities.ExecuteTask(t2);
                // Although everything was up to date, OutputResources and FilesWritten
                // must contain the files that would have been created if they weren't up to date.
                Assert.Equal(t2.OutputResources[0].ItemSpec, resourcesFile1);
                Assert.Equal(t2.FilesWritten[0].ItemSpec, resourcesFile1);
                Assert.Equal(t2.OutputResources[1].ItemSpec, resourcesFile2);
                Assert.Equal(t2.FilesWritten[1].ItemSpec, resourcesFile2);

                Utilities.AssertStateFileWasWritten(t2);

                Assert.True(time.Equals(File.GetLastWriteTime(t2.OutputResources[0].ItemSpec)));
                Assert.True(time2.Equals(File.GetLastWriteTime(t2.OutputResources[1].ItemSpec)));
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }

                if (resourcesFile1 != null)
                {
                    File.Delete(resourcesFile1);
                }

                if (resourcesFile2 != null)
                {
                    File.Delete(resourcesFile2);
                }
            }
        }

        /// <summary>
        /// If the reference has been touched, it should rebuild even if the inputs are
        /// otherwise up to date
        /// </summary>
        /// <remarks>System dll is not locked because it forces a new app domain</remarks>
#if RUNTIME_TYPE_NETCORE
        [Fact(Skip = "Depends on referencing System.dll")]
#else
        [Fact]
#endif
        public void NothingOutOfDateExceptReference()
        {
            string resxFile = null;
            string resourcesFile = null;
            string stateFile = Utilities.GetTempFileName(".cache");
            string localSystemDll = Utilities.GetPathToCopiedSystemDLL();

            try
            {
                resxFile = Utilities.WriteTestResX(true /* uses system type */, null, null);

                _output.WriteLine("** Running task to create resources.");

                GenerateResource initialCreator = Utilities.CreateTask(_output);
                initialCreator.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                initialCreator.References = new ITaskItem[] { new TaskItem(localSystemDll) };
                initialCreator.StateFile = new TaskItem(stateFile);
                Utilities.ExecuteTask(initialCreator);

                DateTime firstWriteTime = File.GetLastWriteTime(initialCreator.OutputResources[0].ItemSpec);

                _output.WriteLine("** Repeat, and it should do nothing as they are up to date");

                GenerateResource incrementalUpToDate = Utilities.CreateTask(_output);
                incrementalUpToDate.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                incrementalUpToDate.References = new ITaskItem[] { new TaskItem(localSystemDll) };
                incrementalUpToDate.StateFile = new TaskItem(stateFile);
                Utilities.ExecuteTask(incrementalUpToDate);

                File.GetLastWriteTime(incrementalUpToDate.OutputResources[0].ItemSpec).ShouldBe(firstWriteTime);

                _output.WriteLine("** Touch the reference, and repeat, it should now rebuild");
                DateTime newTime = DateTime.Now + new TimeSpan(0, 1, 0);
                File.SetLastWriteTime(localSystemDll, newTime);

                GenerateResource incrementalOutOfDate = Utilities.CreateTask(_output);
                incrementalOutOfDate.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                incrementalOutOfDate.References = new ITaskItem[] { new TaskItem(localSystemDll) };
                incrementalOutOfDate.StateFile = new TaskItem(stateFile);

                if (!NativeMethodsShared.IsWindows)
                {
                    // Must be > 1 sec on some file systems for proper timestamp granularity
                    // TODO: Implement an interface for fetching deterministic timestamps rather than relying on the file
                    System.Threading.Thread.Sleep(1100);
                }

                Utilities.ExecuteTask(incrementalOutOfDate);

                File.GetLastWriteTime(incrementalOutOfDate.OutputResources[0].ItemSpec).ShouldBeGreaterThan(firstWriteTime);

                resourcesFile = incrementalOutOfDate.OutputResources[0].ItemSpec;

                Utilities.AssertLogContainsResource(incrementalOutOfDate, "GenerateResource.InputNewer", localSystemDll, incrementalOutOfDate.OutputResources[0].ItemSpec);
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }

                if (stateFile != null)
                {
                    File.Delete(stateFile);
                }

                if (localSystemDll != null)
                {
                    File.Delete(localSystemDll);
                }
            }
        }

        /// <summary>
        /// If an additional input is out of date, resources should be regenerated.
        /// </summary>
        [Fact]
        public void NothingOutOfDateExceptAdditionalInput()
        {
            string resxFile = null;
            string resourcesFile = null;
            ITaskItem[] additionalInputs = null;

            try
            {
                resxFile = Utilities.WriteTestResX(false, null, null);
                additionalInputs = new ITaskItem[] { new TaskItem(FileUtilities.GetTemporaryFile()), new TaskItem(FileUtilities.GetTemporaryFile()) };

                foreach (ITaskItem file in additionalInputs)
                {
                    if (!File.Exists(file.ItemSpec))
                    {
                        File.WriteAllText(file.ItemSpec, "");
                    }
                }

                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.AdditionalInputs = additionalInputs;
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));
                Utilities.ExecuteTask(t);

                // Repeat, and it should do nothing as they are up to date
                GenerateResource t2 = Utilities.CreateTask(_output);
                t2.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t2.AdditionalInputs = additionalInputs;
                t2.StateFile = new TaskItem(t.StateFile);
                Utilities.ExecuteTask(t2);
                Utilities.AssertLogContainsResource(t2, "GenerateResource.NothingOutOfDate", "");

                // Touch one of the additional inputs and repeat, it should now rebuild
                DateTime newTime = DateTime.Now + new TimeSpan(0, 1, 0);
                File.SetLastWriteTime(additionalInputs[1].ItemSpec, newTime);
                GenerateResource t3 = Utilities.CreateTask(_output);
                t3.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t3.AdditionalInputs = additionalInputs;
                t3.StateFile = new TaskItem(t.StateFile);
                Utilities.ExecuteTask(t3);
                Utilities.AssertLogNotContainsResource(t3, "GenerateResource.NothingOutOfDate", "");
                Utilities.AssertLogContainsResource(t3, "GenerateResource.InputNewer", additionalInputs[1].ItemSpec, t3.OutputResources[0].ItemSpec);
                resourcesFile = t3.OutputResources[0].ItemSpec;
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }

                if (additionalInputs?[0] != null && File.Exists(additionalInputs[0].ItemSpec))
                {
                    File.Delete(additionalInputs[0].ItemSpec);
                }

                if (additionalInputs?[1] != null && File.Exists(additionalInputs[1].ItemSpec))
                {
                    File.Delete(additionalInputs[1].ItemSpec);
                }
            }
        }

        /// <summary>
        ///  Text to ResX
        /// </summary>
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
        [Fact]
#else
        [Fact(Skip = "Writing to XML not supported on .net core")]
#endif
        public void BasicText2ResX()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            string textFile = Utilities.WriteTestText(null, null);
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(textFile, ".resx")) };

            Utilities.ExecuteTask(t);

            string resourcesFile = t.OutputResources[0].ItemSpec;
            Assert.Equal(".resx", Path.GetExtension(resourcesFile));

            Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
            Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Round trip from resx to resources to resx with the same blobs
        /// </summary>
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
        [Fact]
#else
        [Fact(Skip = "ResGen.exe not supported on.NET Core MSBuild")]
#endif
        public void ResX2ResX()
        {
            string resourcesFile = Utilities.CreateBasicResourcesFile(true, _output);

            // Step 1: create a resx file directly from the resources, to get a framework generated resx
            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(resourcesFile, ".resx")) };
            Utilities.ExecuteTask(t);
            Assert.Equal(".resx", Path.GetExtension(t.FilesWritten[0].ItemSpec));

            // Step 2a: create a resources file from the resx
            GenerateResource t2a = Utilities.CreateTask(_output);
            t2a.Sources = new ITaskItem[] { new TaskItem(t.FilesWritten[0].ItemSpec) };
            t2a.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(t.FilesWritten[0].ItemSpec, ".resources")) };
            Utilities.ExecuteTask(t2a);
            Assert.Equal(".resources", Path.GetExtension(t2a.FilesWritten[0].ItemSpec));

            // Step 2b: create a resx from the resources
            GenerateResource t2b = Utilities.CreateTask(_output);
            t2b.Sources = new ITaskItem[] { new TaskItem(t2a.FilesWritten[0].ItemSpec) };
            t2b.OutputResources = new ITaskItem[] { new TaskItem(Utilities.GetTempFileName(".resx")) };
            File.Delete(t2b.OutputResources[0].ItemSpec);
            Utilities.ExecuteTask(t2b);
            Assert.Equal(".resx", Path.GetExtension(t2b.FilesWritten[0].ItemSpec));

            // make sure the output resx files from each fork are the same
            Assert.Equal(Utilities.ReadFileContent(t.OutputResources[0].ItemSpec),
                         Utilities.ReadFileContent(t2b.OutputResources[0].ItemSpec));

            // Done, so clean up.
            File.Delete(resourcesFile);
            File.Delete(t.OutputResources[0].ItemSpec);
            File.Delete(t2a.OutputResources[0].ItemSpec);
            foreach (ITaskItem item in t2b.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Round trip from text to resources to text with the same blobs
        /// </summary>
#if FEATURE_RESXREADER_LIVEDESERIALIZATION
        [Fact]
#else
        [Fact(Skip = "ResGen.exe not supported on.NET Core MSBuild")]
#endif
        public void Text2Text()
        {
            string textFile = Utilities.WriteTestText(null, null);

            // Round 1, do the Text2Resource
            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };

            Utilities.ExecuteTask(t);

            // make sure round 1 is successful
            string resourcesFile = t.OutputResources[0].ItemSpec;
            Assert.Equal(".resources", Path.GetExtension(resourcesFile));

            // round 2, do the resources2Text from the same file
            GenerateResource t2 = Utilities.CreateTask(_output);

            t2.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            string outputFile = Utilities.GetTempFileName(".txt");
            t2.OutputResources = new ITaskItem[] { new TaskItem(outputFile) };
            Utilities.ExecuteTask(t2);

            resourcesFile = t2.FilesWritten[0].ItemSpec;
            Assert.Equal(".txt", Path.GetExtension(resourcesFile));

            Assert.Equal(Utilities.GetTestTextContent(null, null, true /*cleaned up */), Utilities.ReadFileContent(resourcesFile));

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }

            File.Delete(t2.Sources[0].ItemSpec);
            foreach (ITaskItem item in t2.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  STR without references yields proper output, message
        /// </summary>
        [Fact]
        public void StronglyTypedResources()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                // STR class name should have been generated from the output
                string stronglyTypedClassName = Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec);
                Assert.Equal(t.StronglyTypedClassName, stronglyTypedClassName);
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                Utilities.AssertStateFileWasWritten(t);
                // Files written should contain STR class file
                string stronglyTypedFileName = Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs");
                Assert.Equal(t.FilesWritten[2].ItemSpec, stronglyTypedFileName);
                Assert.True(File.Exists(stronglyTypedFileName));

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);

                string typeName = null;
                if (t.StronglyTypedNamespace != null)
                {
                    typeName = t.StronglyTypedNamespace + ".";
                }
                else
                {
                    typeName = "";
                }

                typeName += t.StronglyTypedClassName;

                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", stronglyTypedFileName);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  STR without references yields proper output, message
        /// </summary>
        [Fact]
        public void StronglyTypedResourcesUpToDate()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            GenerateResource t2 = Utilities.CreateTask(_output);
            try
            {
                string textFile = Utilities.WriteTestText(null, null);

                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                // STR class name should have been generated from the output
                string stronglyTypedClassName = Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec);
                Assert.Equal(t.StronglyTypedClassName, stronglyTypedClassName);
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);
                // Files written should contain STR class file
                string stronglyTypedFileName = Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs");
                Assert.Equal(t.FilesWritten[2].ItemSpec, stronglyTypedFileName);
                Assert.True(File.Exists(stronglyTypedFileName));

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);

                string typeName = null;
                if (t.StronglyTypedNamespace != null)
                {
                    typeName = t.StronglyTypedNamespace + ".";
                }
                else
                {
                    typeName = "";
                }

                typeName += t.StronglyTypedClassName;

                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", stronglyTypedFileName);

                // Now that we have done it, do it again to make sure that we don't do
                t2.StateFile = new TaskItem(t.StateFile);

                t2.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t2.StronglyTypedLanguage = "CSharp";

                Utilities.ExecuteTask(t2);

                Assert.Equal(t2.OutputResources[0].ItemSpec, resourcesFile);
                Assert.Equal(t2.FilesWritten[0].ItemSpec, resourcesFile);

                Utilities.AssertStateFileWasWritten(t2);
                Assert.Equal(t2.FilesWritten[2].ItemSpec, Path.ChangeExtension(t2.Sources[0].ItemSpec, ".cs"));
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
                foreach (ITaskItem item in t2.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        /// STR class file is out of date, but resources are up to date. Should still generate it.
        /// </summary>
        [Fact]
        public void StronglyTypedResourcesOutOfDate()
        {
            string resxFile = null;
            string resourcesFile = null;
            string strFile = null;
            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                resxFile = Utilities.WriteTestResX(false, null, null);
                resourcesFile = Utilities.GetTempFileName(".resources");
                strFile = Path.ChangeExtension(resourcesFile, ".cs"); // STR filename should be generated from output not input filename
                string stateFile = Utilities.GetTempFileName(".cache");

                // Make sure the .cs file isn't already there.
                File.Delete(strFile);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.OutputResources = new ITaskItem[] { new TaskItem(resourcesFile) };
                t.StronglyTypedLanguage = "C#";
                t.StateFile = new TaskItem(stateFile);
                Utilities.ExecuteTask(t);

                // STR class name generated from output resource file name
                string stronglyTypedClassName = Path.GetFileNameWithoutExtension(resourcesFile);
                Assert.Equal(t.StronglyTypedClassName, stronglyTypedClassName);
                resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                Assert.True(File.Exists(resourcesFile));
                Assert.Equal(t.FilesWritten[2].ItemSpec, strFile);
                Assert.True(File.Exists(strFile));

                // Repeat. It should not update either file.
                // First move both the timestamps back so they're still up to date,
                // but we'd know if they were updated (this is quicker than sleeping and okay as there's no cache being used)
                Utilities.MoveBackTimestamp(resxFile, 1);
                DateTime strTime = Utilities.MoveBackTimestamp(strFile, 1);
                t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.OutputResources = new ITaskItem[] { new TaskItem(resourcesFile) };
                t.StronglyTypedLanguage = "C#";
                t.StateFile = new TaskItem(stateFile);
                Utilities.ExecuteTask(t);
                Utilities.AssertLogContainsResource(t, "GenerateResource.NothingOutOfDate", "");
                Assert.False(Utilities.FileUpdated(strFile, strTime)); // Was not updated

                // OK, now delete the STR class file
                File.Delete(strFile);

                // Repeat. It should recreate the STR class file
                t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.OutputResources = new ITaskItem[] { new TaskItem(resourcesFile) };
                t.StronglyTypedLanguage = "C#";
                t.StateFile = new TaskItem(stateFile);
                Utilities.ExecuteTask(t);
                Assert.True(Utilities.FileUpdated(strFile, strTime)); // Was updated
                Assert.Equal(t.OutputResources[0].ItemSpec, resourcesFile);
                Assert.True(File.Exists(resourcesFile));
                Assert.Equal(t.FilesWritten[2].ItemSpec, strFile);
                Assert.True(File.Exists(strFile));

                // OK, now delete the STR class file again
                File.Delete(strFile);

                // Repeat, but specify the filename this time, instead of having it generated from the output resources
                // It should recreate the STR class file again
                t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.OutputResources = new ITaskItem[] { new TaskItem(resourcesFile) };
                t.StronglyTypedLanguage = "C#";
                t.StronglyTypedFileName = strFile;
                Utilities.ExecuteTask(t);
                Assert.True(File.Exists(strFile));
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }

                if (strFile != null)
                {
                    File.Delete(strFile);
                }
            }
        }

        /// <summary>
        /// Verify STR generation with a specified specific filename
        /// </summary>
        [Fact]
        public void StronglyTypedResourcesWithFilename()
        {
            string txtFile = null;
            string strFile = null;
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);

                txtFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedLanguage = "CSharp";
                strFile = FileUtilities.GetTemporaryFile();
                t.StronglyTypedFileName = strFile;

                Utilities.ExecuteTask(t);

                // Check resources is output
                resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                Assert.Single(t.OutputResources);
                Assert.Equal(".resources", Path.GetExtension(t.FilesWritten[0].ItemSpec));
                Assert.True(File.Exists(resourcesFile));

                // Check STR file is output
                Assert.Equal(t.FilesWritten[1].ItemSpec, strFile);
                Assert.Equal(t.StronglyTypedFileName, strFile);
                Assert.True(File.Exists(strFile));

                // Check log
                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", txtFile, t.OutputResources[0].ItemSpec);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, txtFile);

                string typeName = "";
                if (t.StronglyTypedNamespace != null)
                {
                    typeName = t.StronglyTypedNamespace + ".";
                }

                typeName += t.StronglyTypedClassName;
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", strFile);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }

                if (strFile != null)
                {
                    File.Delete(strFile);
                }
            }
        }

        /// <summary>
        ///  STR with VB
        /// </summary>
        [Fact]
        public void StronglyTypedResourcesVB()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "VB";
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                // FilesWritten should contain STR class file
                string stronglyTypedFileName = Path.ChangeExtension(t.Sources[0].ItemSpec, ".vb");
                Assert.Equal(t.FilesWritten[2].ItemSpec, stronglyTypedFileName);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);
                Assert.True(File.Exists(stronglyTypedFileName));

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);

                string typeName = null;
                if (t.StronglyTypedNamespace != null)
                {
                    typeName = t.StronglyTypedNamespace + ".";
                }
                else
                {
                    typeName = "";
                }

                typeName += t.StronglyTypedClassName;

                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", stronglyTypedFileName);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  STR namespace can be empty
        /// </summary>
        [Fact]
        public void StronglyTypedResourcesWithoutNamespaceOrClassOrFilename()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                // Should have defaulted the STR filename to the bare output resource name + ".cs"
                string STRfile = Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs");
                Assert.Equal(t.StronglyTypedFileName, STRfile);
                Assert.True(File.Exists(STRfile));

                // Should have defaulted the class name to the bare output resource name
                Assert.Equal(t.StronglyTypedClassName, Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec));

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);

                // Should not have used a namespace
                Assert.DoesNotContain("namespace", File.ReadAllText(t.StronglyTypedFileName));
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        /// STR-emitted code has the correct types.
        /// </summary>
        /// <remarks>
        /// Regression test for legacy-codepath-resources case of https://github.com/dotnet/msbuild/issues/4582
        /// </remarks>
        [WindowsFullFrameworkOnlyFact(additionalMessage: "https://github.com/dotnet/msbuild/issues/2272")]
        public void StronglyTypedResourcesEmitTypeIntoClass()
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();
            string resxFile = Utilities.WriteTestResX(false, bitmap, null, false);

            GenerateResource t = Utilities.CreateTask(_output);
            try
            {
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                // Should have defaulted the STR filename to the bare output resource name + ".cs"
                string STRfile = Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs");
                Assert.Equal(t.StronglyTypedFileName, STRfile);
                Assert.True(File.Exists(STRfile));

                // Should have defaulted the class name to the bare output resource name
                Assert.Equal(t.StronglyTypedClassName, Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec));

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", resxFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 2, resxFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);

                string generatedSource = File.ReadAllText(t.StronglyTypedFileName);

                generatedSource.ShouldNotContain("object Image1", customMessage: "Strongly-typed resource accessor is returning type `object` instead of `System.Drawing.Bitmap`");
                generatedSource.ShouldContain("Bitmap Image1");

                generatedSource.ShouldNotContain("object MyString", customMessage: "Strongly-typed resource accessor is returning type `object` instead of `string`");
                generatedSource.ShouldContain("static string MyString");
                generatedSource.ShouldMatch("//.*Looks up a localized string similar to MyValue", "Couldn't find a comment in the usual format for a string resource.");
            }
            finally
            {
                // Done, so clean up.
                FileUtilities.DeleteNoThrow(bitmap);
                FileUtilities.DeleteNoThrow(resxFile);

                FileUtilities.DeleteNoThrow(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    FileUtilities.DeleteNoThrow(item.ItemSpec);
                }
            }
        }

        /// <summary>
        ///  STR with resource namespace yields proper output, message (CS)
        /// </summary>
        [Fact]
        public void STRWithResourcesNamespaceCS()
        {
            Utilities.STRNamespaceTestHelper("CSharp", "MyResourcesNamespace", null, _output);
        }

        /// <summary>
        ///  STR with resource namespace yields proper output, message (VB)
        /// </summary>
        [Fact]
        public void STRWithResourcesNamespaceVB()
        {
            Utilities.STRNamespaceTestHelper("VB", "MyResourcesNamespace", null, _output);
        }

        /// <summary>
        ///  STR with resource namespace and STR namespace yields proper output, message (CS)
        /// </summary>
        [Fact]
        public void STRWithResourcesNamespaceAndSTRNamespaceCS()
        {
            Utilities.STRNamespaceTestHelper("CSharp", "MyResourcesNamespace", "MySTClassNamespace", _output);
        }

        /// <summary>
        ///  STR with resource namespace and STR namespace yields proper output, message (CS)
        /// </summary>
        [Fact]
        public void STRWithResourcesNamespaceAndSTRNamespaceVB()
        {
            Utilities.STRNamespaceTestHelper("VB", "MyResourcesNamespace", "MySTClassNamespace", _output);
        }
    }

    public sealed class TransformationErrors
    {
        private readonly ITestOutputHelper _output;

        public TransformationErrors(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        ///  Text input failures, no name, no '=', 'strings' token, invalid token, invalid escape
        /// </summary>
        [Fact]
        public void TextToResourcesBadFormat()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing TextToResourcesBadFormat() test");

            // The first string in each row is passed into the text block that's created in the file
            // The second string is a fragment of the expected error message
            string[][] tests = new string[][] {
                // invalid token in file, "unsupported square bracket keyword"
                new string[] {   "[goober]", "MSB3563" },
                // no '=', "resource line without an equals sign"
                new string[] {   "abcdefaghha", "MSB3564" },
                // no name, "resource line without a name"
                new string[] {   "=abced", "MSB3565" },
                // invalid escape, "unsupported or invalid escape character"
                new string[] {   "abc=de\\efght", "MSB3566" },
                // another invalid escape, this one more serious, "unsupported or invalid escape character"
                new string[] {   @"foo=\ujjjjbar", "MSB3569"},
            };
            GenerateResource t;
            string textFile;
            foreach (string[] test in tests)
            {
                t = Utilities.CreateTask(_output);

                textFile = Utilities.WriteTestText(null, test[0]);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.Execute();
                Utilities.AssertLogContains(t, test[1]);

                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    File.Delete(item.ItemSpec);
                }
            }

            // text file uses the strings token; since it's only a warning we have to have special asserts
            t = Utilities.CreateTask(_output);

            textFile = Utilities.WriteTestText(null, "[strings]");
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            bool success = t.Execute();
            // Task should have succeeded (it was just a warning)
            Assert.True(success);
            // warning that 'strings' is an obsolete tag
            Utilities.AssertLogContains(t, "MSB3562");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Cause failures in ResXResourceReader
        /// </summary>
        [Fact]
        public void FailedResXReader()
        {
            string resxFile1 = null;
            string resxFile2 = null;
            string resourcesFile1 = null;
            string resourcesFile2 = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                // Invalid one
                resxFile1 = Utilities.WriteTestResX(false, null, "  <data name='ack!'>>>>>>\xd\xa    <valueAB>Assembly</value>\xd\xa  </data>\xd\xa", false);
                // Also include a valid one. It should still get processed
                resxFile2 = Utilities.WriteTestResX(false, null, null);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile1), new TaskItem(resxFile2) };
                resourcesFile1 = Path.ChangeExtension(resxFile1, ".resources");
                resourcesFile2 = Path.ChangeExtension(resxFile2, ".resources");
                File.Delete(resourcesFile1);
                File.Delete(resourcesFile2);
                bool success = t.Execute();
                // Task should have failed

                /* Unmerged change from project 'Microsoft.Build.Tasks.UnitTests (net7.0)'
                Before:
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);
                After:
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);
                */
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);

                // Should not have created an output for the invalid resx
                // Should have created the other file
                Assert.False(File.Exists(resourcesFile1));
                Assert.Equal(t.OutputResources[0].ItemSpec, resourcesFile2);
                Assert.Single(t.OutputResources);
                Assert.Equal(t.FilesWritten[0].ItemSpec, resourcesFile2);
                Assert.True(File.Exists(resourcesFile2));

                // "error in resource file" with exception from the framework
                Utilities.AssertLogContains(t, "MSB3103");
            }
            finally
            {
                if (resxFile1 != null)
                {
                    File.Delete(resxFile1);
                }

                if (resxFile2 != null)
                {
                    File.Delete(resxFile2);
                }

                if (resourcesFile1 != null)
                {
                    File.Delete(resourcesFile1);
                }

                if (resourcesFile2 != null)
                {
                    File.Delete(resourcesFile2);
                }
            }
        }

        /// <summary>
        ///  Cause failures in ResXResourceReader, different codepath
        /// </summary>
        [Fact]
        public void FailedResXReaderWithAllOutputResourcesSpecified()
        {
            string resxFile1 = null;
            string resxFile2 = null;
            string resourcesFile1 = null;
            string resourcesFile2 = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                // Invalid one
                resxFile1 = Utilities.WriteTestResX(false, null, "  <data name='ack!'>>>>>>\xd\xa    <valueAB>Assembly</value>\xd\xa  </data>\xd\xa", false);
                // Also include a valid one. It should still get processed
                resxFile2 = Utilities.WriteTestResX(false, null, null);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile1), new TaskItem(resxFile2) };
                resourcesFile1 = Path.ChangeExtension(resxFile1, ".resources");
                resourcesFile2 = Path.ChangeExtension(resxFile2, ".resources");
                File.Delete(resourcesFile1);
                File.Delete(resourcesFile2);
                t.OutputResources = new ITaskItem[] { new TaskItem(resourcesFile1), new TaskItem(resourcesFile2) };

                bool success = t.Execute();
                // Task should have failed

                /* Unmerged change from project 'Microsoft.Build.Tasks.UnitTests (net7.0)'
                Before:
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);
                After:
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);
                */
                Assert.False(success);

                Utilities.AssertStateFileWasWritten(t);

                // Should not have created an output for the invalid resx
                // Should have created the other file
                Assert.False(File.Exists(resourcesFile1));
                Assert.Equal(t.OutputResources[0].ItemSpec, resourcesFile2);
                Assert.Single(t.OutputResources);
                Assert.Equal(t.FilesWritten[0].ItemSpec, resourcesFile2);
                Assert.True(File.Exists(resourcesFile2));

                // "error in resource file" with exception from the framework
                Utilities.AssertLogContains(t, "MSB3103");

                // so just look for the unlocalizable portions
                Utilities.AssertLogContains(t, "valueAB");
                Utilities.AssertLogContains(t, "value");
            }
            finally
            {
                if (resxFile1 != null)
                {
                    File.Delete(resxFile1);
                }

                if (resxFile2 != null)
                {
                    File.Delete(resxFile2);
                }

                if (resourcesFile1 != null)
                {
                    File.Delete(resourcesFile1);
                }

                if (resourcesFile2 != null)
                {
                    File.Delete(resourcesFile2);
                }
            }
        }

        /// <summary>
        ///  Duplicate resource names
        /// </summary>
        [Fact]
        public void DuplicateResourceNames()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            string textFile = Utilities.WriteTestText(null, "Marley=some guy from Jamaica");
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            bool success = t.Execute();
            // Task should have succeeded (it was just a warning)
            Assert.True(success);

            // "duplicate resource name"
            Utilities.AssertLogContains(t, "MSB3568");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Non-string resource with text output
        /// </summary>
        [Fact]
        public void UnsupportedTextType()
        {
            string bitmap = Utilities.CreateWorldsSmallestBitmap();
            string resxFile = Utilities.WriteTestResX(false, bitmap, null, false);

            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));
            t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(Path.ChangeExtension(resxFile, ".txt")) };
            bool success = t.Execute();
            // Task should have failed
            Assert.False(success);
            // "only strings can be written to a .txt file"
            Utilities.AssertLogContains(t, "MSB3556");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            File.Delete(bitmap);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        /// Can't write the statefile
        /// </summary>
        [Fact]
        public void InvalidStateFile()
        {
            string resxFile = null;
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                resxFile = Utilities.WriteTestResX(false, null, null);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.StateFile = new TaskItem("||//invalid filename||");

                // Should still succeed
                Assert.True(t.Execute());

                resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                Assert.Equal(t.FilesWritten[0].ItemSpec, t.OutputResources[0].ItemSpec);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", resxFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 1, resxFile);
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }
            }
        }

        [Fact]
        public void GenerateResourceWarnsWhenUsingBinaryFormatter()
        {
            using TestEnvironment env = TestEnvironment.Create();
            TransientTestFile resource = env.CreateFile(".resx", @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <data name=""$this.Icon"" type=""System.Drawing.Icon, System.Drawing"" mimetype=""application/x-microsoft.net.object.binary.base64"">
    <value>
        AAABAAEAEBAAAAAAIABoBAAAFgAAACgAAAAQAAAAIAAAAAEAIAAAAAAAQAQAAAAAAAAAAAAAAAAAAAAA
        AAD///8BoqKiDaKiotmioqL5oqKiK////wH///8B////Af///wH///8B////AaKioiGioqLxoqKi5aKi
        ohn///8B////AbS0tBW0tLTz29vb/7Ozsu18Wi+Be1gswXtYLO17WCzte1gswXtYLIGzs7Lz2dnZ/7S0
        tPu0tLQj////Af///wH///8BxsbGQdPT0//Cv739nGs7/6ZsNf+ubzf/rm83/6ZsNf+hdkr/xcTD/8bG
        xf/GxsY/////Af///wH///8B////AYxlNmejiGn1r3hE/7uMXv/Ck3H/xJF0/8OPcf+/kGz/uIpd/7SG
        Wf+hhWT1jGU2Z////wH///8B////AZZtOzWWbTvVs31G/8KZcf/Yqon/79/P//r28//69fP/79/R/9en
        hf++lGz/s31G/5ZtO9WWbTs1////Af///wGhdUGBsIBK/8abb//Zqoj///7r///67v///fL///7y///8
        7////ev/2aN6/8KZbP+wgEr/oXVBgf///wH///8BrH5Iwb+PWP/No4H/8NvB///35v/68uP/xcC2//Ht
        3v///Oj///Xf/+/Ur//ImXL/v49Y/6x+SMH///8B////AbeHTu3JnGb/z5+A//rz4v/99un/8vDj/42M
        hP+Bf3f/0s/C///76//67Mz/x5Bt/8mcZv+3h07t////Af///wHCkFTtzqZx/9Glif/69un//fju////
        +f+BgHn/sa6k/4F/d//Jxrr/+vDT/8mWcv/OpnH/wpBU7f///wH///8BzZlbwdOsdf/Zt5j/8ePW//77
        9f/19fP/n56V//Dw6f/4+PL/vrmt//Dawv/Sqof/06x1/82ZW8H///8B////AbOddIvTrXf/38Sa/969
        qv//////8PDu/+fl2v////f////3///+8//ctJj/28CW/8Kqfv/Gn2qF////AQCZ3T0KmtjZLpzF9d6/
        iv/iyaf/37+u//Hj3P/z8ez/9PHr//Hi2f/cuqP/38Oe/4yxqf84ptH5DprWzwCZ3ScAoON9fNHy7WHD
        6O86pMb74seS/+bRqf/gwqb/1a6W/9Wrkv/evaD/5M+m/7/Bnv9Hstf9q+P2/Smw6NkAoOMnAKfpe13J
        8eW16Pn/Ycfr7zqqzPPsxIj/6cuU/+fQnf/n0J3/6cuU/97Cjv8yqtD1gdPw9XPQ8+sAp+nNAKfpBQCu
        7wUAru+LW8v05b/s+v9cy/HpTbLJxfq8dMH6vHTt+rx07fq8dMFRssjDac/y7XzW9u0Aru/JAK7vHf//
        /wH///8BALX0AwC19IEAtfTRALX0ywC19Af///8B////Af///wH///8BALX0FwC19NEAtfTJALX0J///
        /wH///8BAAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA
        //8AAP//AAD//w==
</value>
  </data>
</root>
");

            GenerateResource gr = Utilities.CreateTask(_output, usePreserialized: true, env: env);
            gr.Sources = new ITaskItem[] { new TaskItem(resource.Path) };
            gr.WarnOnBinaryFormatterUse = true;

            gr.Execute().ShouldBeTrue();

            Utilities.AssertLogContainsResource(gr, "GenerateResource.BinaryFormatterUse", "$this.Icon");
        }

        /// <summary>
        ///  Cause failures in ResourceReader
        /// </summary>
        [WindowsFullFrameworkOnlyFact(additionalMessage: ".NET Core MSBuild doesn't try to read binary input resources.")]
        public void FailedResourceReader()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            // to cause a failure, we're going to transform a bad .resources file to a .resx
            // the simplest thing is to create a .resx, but call it .resources
            string resxFile = Utilities.WriteTestResX(false, null, null);
            string resourcesFile = Path.ChangeExtension(resxFile, ".resources");
            File.Move(resxFile, resourcesFile);
            t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(resxFile) };

            bool success = t.Execute();
            // Task should have failed
            Assert.False(success);

            // "error in resource file" with exception from the framework
            Utilities.AssertLogContains(t, "MSB3103");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        [DotNetOnlyTheory(additionalMessage: "This error is .NET Core only.")]
        [InlineData(".resources")]
        [InlineData(".dll")]
        public void ResourceReaderRejectsNonCoreCompatFormats(string inputExtension)
        {
            using var env = TestEnvironment.Create(_output);

            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(env.GetTempFile(".cache").Path);

            // file contents aren't required since the extension is checked first
            var resourcesFile = env.CreateFile(inputExtension).Path;

            t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(env.GetTempFile(".resources").Path) };

            t.Execute().ShouldBeFalse();

            Utilities.AssertLogContains(t, "MSB3824");
        }

        /// <summary>
        ///  Invalid STR Class name
        /// </summary>
        [Fact]
        public void FailedSTRProperty()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            string textFile = Utilities.WriteTestText(null, "object=some string");
            string resourcesFile = Path.ChangeExtension(textFile, ".resources");

            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            t.StronglyTypedLanguage = "CSharp";
            // Invalid class name
            t.StronglyTypedClassName = "~!@#$%^&amp;*(";

            bool success = t.Execute();
            // Task should have failed
            Assert.False(success);

            Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
            Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 5, textFile);
            Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);
            Utilities.AssertLogContains(t, "MSB3570");
            Utilities.AssertLogContains(t, t.StronglyTypedFileName);

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            File.Delete(t.StronglyTypedFileName);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        /// Reference passed in that can't be loaded should error
        /// </summary>
        [WindowsFullFrameworkOnlyFact(additionalMessage: ".NET Core MSBuild doesn't load refs so it pushes this failure to runtime.")]
        public void InvalidReference()
        {
            string txtFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);

                // Create resx with invalid ref "INVALID"
                txtFile = Utilities.WriteTestResX(false, null, null, true /*data with invalid type*/);
                string resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.References = new TaskItem[] { new TaskItem("INVALID") };

                bool result = t.Execute();
                // Task should have failed
                Assert.False(result);

                // Should have not written any files
                Assert.True(t.FilesWritten?.Length == 0);
                Assert.False(File.Exists(resourcesFile));
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }
            }
        }
    }

    public sealed class PropertyHandling
    {
        private readonly ITestOutputHelper _output;

        public PropertyHandling(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        ///  Sources attributes are copied to given OutputResources
        /// </summary>
        [Fact]
        public void AttributeForwarding()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing AttributeForwarding() test");

            GenerateResource t = Utilities.CreateTask(_output);

            string resxFile = Utilities.WriteTestResX(false, null, null);
            ITaskItem i = new TaskItem(resxFile);
            i.SetMetadata("Locale", "en-GB");
            t.Sources = new ITaskItem[] { i };

            ITaskItem o = new TaskItem("MyAlternateResource.resources");
            o.SetMetadata("Locale", "fr");
            o.SetMetadata("Flavor", "Pumpkin");
            t.OutputResources = new ITaskItem[] { o };

            Utilities.ExecuteTask(t);

            // Locale was forward from source item and should overwrite the 'fr'
            // locale that the output item originally had.
            Assert.Equal("fr", t.OutputResources[0].GetMetadata("Locale"));

            // Output ItemSpec should not be overwritten.
            Assert.Equal("MyAlternateResource.resources", t.OutputResources[0].ItemSpec);

            // Attributes not on Sources should be left untouched.
            Assert.Equal("Pumpkin", t.OutputResources[0].GetMetadata("Flavor"));

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Sources attributes copied to computed OutputResources
        /// </summary>
        [Fact]
        public void AttributeForwardingOnEmptyOutputs()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            string resxFile = Utilities.WriteTestResX(false, null, null);
            ITaskItem i = new TaskItem(resxFile);
            i.SetMetadata("Locale", "en-GB");
            t.Sources = new ITaskItem[] { i };

            Utilities.ExecuteTask(t);

            // Output ItemSpec should be computed from input
            string resourcesFile = Path.ChangeExtension(resxFile, ".resources");
            Assert.Equal(resourcesFile, t.OutputResources[0].ItemSpec);

            // Attribute from source should be copied to output
            Assert.Equal("en-GB", t.OutputResources[0].GetMetadata("Locale"));

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  OutputFiles used for output, and also are synthesized if not set on input
        /// </summary>
        [Fact]
        public void OutputFilesNotSpecified()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            t.Sources = new ITaskItem[] {
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
            };

            Utilities.ExecuteTask(t);

            // Output ItemSpec should be computed from input
            for (int i = 0; i < t.Sources.Length; i++)
            {
                string outputFile = Path.ChangeExtension(t.Sources[i].ItemSpec, ".resources");
                Assert.Equal(outputFile, t.OutputResources[i].ItemSpec);
            }

            // Done, so clean up.
            foreach (ITaskItem item in t.Sources)
            {
                File.Delete(item.ItemSpec);
            }

            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  FilesWritten contains OutputResources + StateFile
        /// </summary>
        [Fact]
        public void FilesWrittenSet()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            t.Sources = new ITaskItem[] {
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
                new TaskItem( Utilities.WriteTestResX(false, null, null)),
            };

            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            Utilities.ExecuteTask(t);

            int i;

            // should be four files written, not including the tlogs
            for (i = 0; i < 4; i++)
            {
                Assert.Equal(t.FilesWritten[i].ItemSpec, t.OutputResources[i].ItemSpec);
                Assert.True(File.Exists(t.FilesWritten[i].ItemSpec));
            }

            Utilities.AssertStateFileWasWritten(t);

            // Done, so clean up.
            File.Delete(t.StateFile.ItemSpec);
            foreach (ITaskItem item in t.Sources)
            {
                File.Delete(item.ItemSpec);
            }
            foreach (ITaskItem item in t.FilesWritten)
            {
                File.Delete(item.ItemSpec);
            }
        }

        /// <summary>
        ///  Resource transformation fails on 3rd of 4 inputs, inputs 1 & 2 & 4 are in outputs and fileswritten.
        /// </summary>
        [Fact]
        public void OutputFilesPartialInputs()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            try
            {
                t.Sources = new ITaskItem[] {
                    new TaskItem( Utilities.WriteTestText(null, null)),
                    new TaskItem( Utilities.WriteTestText(null, null)),
                    new TaskItem( Utilities.WriteTestText("goober", null)),
                    new TaskItem( Utilities.WriteTestText(null, null)),
                };

                foreach (ITaskItem taskItem in t.Sources)
                {
                    File.Delete(Path.ChangeExtension(taskItem.ItemSpec, ".resources"));
                }

                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                string outputFile = Path.ChangeExtension(t.Sources[0].ItemSpec, ".resources");
                Assert.Equal(outputFile, t.OutputResources[0].ItemSpec);
                Assert.True(File.Exists(t.OutputResources[0].ItemSpec));
                outputFile = Path.ChangeExtension(t.Sources[1].ItemSpec, ".resources");
                Assert.Equal(outputFile, t.OutputResources[1].ItemSpec);
                Assert.True(File.Exists(t.OutputResources[1].ItemSpec));
                // Sources[2] should NOT have been converted and should not be in OutputResources
                outputFile = Path.ChangeExtension(t.Sources[2].ItemSpec, ".resources");
                Assert.False(File.Exists(outputFile));
                // Sources[3] should have been converted
                outputFile = Path.ChangeExtension(t.Sources[3].ItemSpec, ".resources");
                Assert.Equal(outputFile, t.OutputResources[2].ItemSpec);
                Assert.True(File.Exists(t.OutputResources[2].ItemSpec));

                // FilesWritten should contain only the 3 successfully output .resources and the cache
                Assert.Equal(t.FilesWritten[0].ItemSpec, Path.ChangeExtension(t.Sources[0].ItemSpec, ".resources"));
                Assert.Equal(t.FilesWritten[1].ItemSpec, Path.ChangeExtension(t.Sources[1].ItemSpec, ".resources"));

                /* Unmerged change from project 'Microsoft.Build.Tasks.UnitTests (net7.0)'
                Before:
                Assert.Equal(t.FilesWritten[2].ItemSpec, Path.ChangeExtension(t.Sources[3].ItemSpec, ".resources"));

                Utilities.AssertStateFileWasWritten(t);
                After:
                Assert.Equal(t.FilesWritten[2].ItemSpec, Path.ChangeExtension(t.Sources[3].ItemSpec, ".resources"));

                Utilities.AssertStateFileWasWritten(t);
                */
                Assert.Equal(t.FilesWritten[2].ItemSpec, Path.ChangeExtension(t.Sources[3].ItemSpec, ".resources"));

                Utilities.AssertStateFileWasWritten(t);

                // Make sure there was an error on the second resource
                // "unsupported square bracket keyword"
                Utilities.AssertLogContains(t, "MSB3563");
                Utilities.AssertLogContains(t, "[goober]");
            }
            finally
            {
                // Done, so clean up.
                foreach (ITaskItem item in t.Sources)
                {
                    File.Delete(item.ItemSpec);
                }
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  STR class name derived from output file transformation
        /// </summary>
        [Fact]

        public void StronglyTypedClassName()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            try
            {
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StronglyTypedFileName = "somefile.cs";
                t.PublicClass = true;
                t.OutputResources = new ITaskItem[] { new TaskItem("somefile.resources") };

                Utilities.ExecuteTask(t);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, t.OutputResources[0].ItemSpec);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);
                Assert.Equal(t.StronglyTypedClassName, Path.GetFileNameWithoutExtension(t.StronglyTypedFileName));
                // Verify class was public, as we specified
                Assert.Contains("public class " + t.StronglyTypedClassName, File.ReadAllText(t.StronglyTypedFileName));

                Utilities.AssertStateFileWasWritten(t);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  STR class file name derived from class name transformation
        /// </summary>
        [Fact]
        public void StronglyTypedFileName()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StronglyTypedLanguage = "CSharp";
                File.Delete(Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs"));
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                Utilities.ExecuteTask(t);

                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, t.OutputResources[0].ItemSpec);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);

                Assert.Equal(t.StronglyTypedFileName, Path.ChangeExtension(t.Sources[0].ItemSpec, ".cs"));
                Assert.True(File.Exists(t.StronglyTypedFileName));

                Utilities.AssertStateFileWasWritten(t);

                // Verify class was internal, since we didn't specify a preference
                Assert.Contains("internal class " + t.StronglyTypedClassName, File.ReadAllText(t.StronglyTypedFileName));
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);

                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }
    }

    public sealed class PropertyErrors
    {
        private readonly ITestOutputHelper _output;

        public PropertyErrors(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        ///  Empty Sources yields message, success
        /// </summary>
        [Fact]
        public void EmptySources()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing EmptySources() test");

            GenerateResource t = Utilities.CreateTask(_output);
            Utilities.ExecuteTask(t);
            Utilities.AssertLogContainsResource(t, "GenerateResource.NoSources", "");
            if (t.FilesWritten != null)
            {
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (item.ItemSpec != null)
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  References with invalid assemblies yields warning
        /// </summary>
        [Fact]
        public void ReferencesToBadAssemblies()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            string textFile = null;

            try
            {
                textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.References = new ITaskItem[] { new TaskItem("some non-existent DLL name goes here.dll") };
                bool success = t.Execute();
                // Task should have succeeded, because the bad reference was never consumed.
                Assert.True(success);
            }
            finally
            {
                // Done, so clean up.
                if (textFile != null)
                {
                    File.Delete(textFile);
                    File.Delete(Path.ChangeExtension(textFile, ".resources"));
                }
            }
        }

        /// <summary>
        ///  Source item not found
        /// </summary>
        [Fact]
        public void SourceItemMissing()
        {
            string txtFile = null;
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                txtFile = Utilities.WriteTestText(null, null);
                resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                t.Sources = new ITaskItem[] { new TaskItem("non-existent.resx"), new TaskItem(txtFile) };
                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                // "Resource file cannot be found"
                Utilities.AssertLogContains(t, "MSB3552");

                // Should have processed remaining file
                Assert.Single(t.OutputResources);
                Assert.Equal(t.OutputResources[0].ItemSpec, resourcesFile);
                Assert.True(File.Exists(resourcesFile));
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }
            }
        }

        /// <summary>
        ///  Read-only StateFile yields message
        /// </summary>
        [WindowsOnlyFact]
        public void StateFileUnwritable()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            try
            {
                string textFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                t.StateFile = new TaskItem(FileUtilities.GetTemporaryFile());
                File.SetAttributes(t.StateFile.ItemSpec, FileAttributes.ReadOnly);
                t.Execute();

                // "cannot read state file (opening for read/write)"
                Utilities.AssertLogContainsResourceWithUnspecifiedReplacements(t, "General.CouldNotReadStateFileMessage");
                // "cannot write state file (opening for read/write)"
                Utilities.AssertLogContains(t, "MSB3101");
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.SetAttributes(t.StateFile.ItemSpec, FileAttributes.Normal);
                if (t.FilesWritten != null)
                {
                    foreach (ITaskItem item in t.FilesWritten)
                    {
                        if (item.ItemSpec != null)
                        {
                            File.Delete(item.ItemSpec);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///  Bad file extension on input
        /// </summary>
        [Fact]
        public void InputFileExtension()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            string textFile = Utilities.WriteTestText(null, null);
            string newTextFile = Path.ChangeExtension(textFile, ".foo");
            File.Move(textFile, newTextFile);
            t.Sources = new ITaskItem[] { new TaskItem(newTextFile) };

            t.Execute();

            // "unsupported file extension"
            Utilities.AssertLogContains(t, "MSB3558");
            Utilities.AssertLogNotContainsResource(t, "GenerateResource.ReadResourceMessage", 4, newTextFile);

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            if (t.FilesWritten != null)
            {
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (item.ItemSpec != null)
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  Bad file extension on output
        /// </summary>
        [Fact]
        public void OutputFileExtension()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            string textFile = Utilities.WriteTestText(null, null);
            string resxFile = Path.ChangeExtension(textFile, ".foo");
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(resxFile) };

            t.Execute();

            // "unsupported file extension"
            Utilities.AssertLogContains(t, "MSB3558");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            if (t.FilesWritten != null)
            {
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (item.ItemSpec != null)
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  Sources and OutputResources different # of elements
        /// </summary>
        [Fact]
        public void SourcesMatchesOutputResources()
        {
            GenerateResource t = Utilities.CreateTask(_output);

            string textFile = Utilities.WriteTestText(null, null);
            string resxFile = Path.ChangeExtension(textFile, ".resources");
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            t.OutputResources = new ITaskItem[] { new TaskItem(resxFile), new TaskItem("someother.resources") };

            t.Execute();

            // "two vectors must have the same length"
            Utilities.AssertLogContains(t, "MSB3094");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            if (t.FilesWritten != null)
            {
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (item.ItemSpec != null)
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        ///  Invalid StronglyTypedLanguage yields CodeDOM exception
        /// </summary>
        [Fact]
        public void UnknownStronglyTypedLanguage()
        {
            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            string textFile = Utilities.WriteTestText(null, null);
            t.Sources = new ITaskItem[] { new TaskItem(textFile) };
            t.StronglyTypedLanguage = "AkbarAndJeff";

            t.Execute();

            // "the codedom provider failed"
            Utilities.AssertLogContains(t, "MSB3559");

            // Done, so clean up.
            File.Delete(t.Sources[0].ItemSpec);
            if (t.FilesWritten != null)
            {
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (item.ItemSpec != null)
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        /// <summary>
        /// StronglyTypedLanguage, but more than one resources file
        /// </summary>
        [Fact]
        public void StronglyTypedResourceWithMoreThanOneInputResourceFile()
        {
            string resxFile = null;
            string resxFile2 = null;

            try
            {
                resxFile = Utilities.WriteTestResX(false, null, null);
                resxFile2 = Utilities.WriteTestResX(false, null, null);

                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile), new TaskItem(resxFile2) };
                t.StronglyTypedLanguage = "VisualBasic";

                Assert.False(t.Execute());

                // "str language but more than one source file"
                Utilities.AssertLogContains(t, "MSB3573");

                Assert.Empty(t.FilesWritten);
                Assert.True(t.OutputResources == null || t.OutputResources.Length == 0);
            }
            finally
            {
                if (resxFile != null)
                {
                    File.Delete(resxFile);
                }

                if (resxFile2 != null)
                {
                    File.Delete(resxFile2);
                }

                if (resxFile != null)
                {
                    File.Delete(Path.ChangeExtension(resxFile, ".resources"));
                }

                if (resxFile2 != null)
                {
                    File.Delete(Path.ChangeExtension(resxFile2, ".resources"));
                }
            }
        }

        /// <summary>
        ///  STR class name derived from output file transformation
        /// </summary>
        [Fact]
        public void BadStronglyTypedFilename()
        {
            string txtFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

                txtFile = Utilities.WriteTestText(null, null);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedLanguage = "CSharp";
                t.StronglyTypedClassName = "cc";
                t.StronglyTypedFileName = NativeMethodsShared.IsWindows ? "||" : "\0";
                t.OutputResources = new ITaskItem[] { new TaskItem("somefile.resources") };

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                // Cannot create strongly typed resource file
                Utilities.AssertLogContains(t, "MSB3570");

                // it didn't write the STR class successfully, but it did still do some processing, so the
                // state file is still around.
                Assert.Single(t.FilesWritten);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }
            }
        }

        /// <summary>
        /// Verify that passing a STR class without a language, errors
        /// </summary>
        [Fact]
        public void StronglyTypedResourceClassWithoutLanguage()
        {
            string txtFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                txtFile = Utilities.WriteTestText(null, null);
                string resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedClassName = "myclassname";
                // no language

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                Utilities.AssertLogContainsResource(t, "GenerateResource.STRClassNamespaceOrFilenameWithoutLanguage");

                // Even the .resources wasn't created
                Assert.False(File.Exists(resourcesFile));
                Assert.Empty(t.FilesWritten);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }
            }
        }

        /// <summary>
        /// Verify that passing a STR namespace without a language, errors
        /// </summary>
        [Fact]
        public void StronglyTypedResourceNamespaceWithoutLanguage()
        {
            string txtFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                txtFile = Utilities.WriteTestText(null, null);
                string resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedNamespace = "mynamespace";
                // no language

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                Utilities.AssertLogContainsResource(t, "GenerateResource.STRClassNamespaceOrFilenameWithoutLanguage");

                // Even the .resources wasn't created
                Assert.False(File.Exists(resourcesFile));
                Assert.Empty(t.FilesWritten);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }
            }
        }

        /// <summary>
        /// Verify that passing a STR filename without a language, errors
        /// </summary>
        [Fact]
        public void StronglyTypedResourceFilenameWithoutLanguage()
        {
            string txtFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                txtFile = Utilities.WriteTestText(null, null);
                string resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedFileName = "myfile";
                // no language

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                Utilities.AssertLogContainsResource(t, "GenerateResource.STRClassNamespaceOrFilenameWithoutLanguage");

                // Even the .resources wasn't created
                Assert.False(File.Exists(resourcesFile));
                Assert.Empty(t.FilesWritten);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }
            }
        }

        /// <summary>
        /// Verify that passing a STR language with more than 1 sources errors
        /// </summary>
        [Fact]
        public void StronglyTypedResourceFileIsExistingDirectory()
        {
            string dir = null;
            string txtFile = null;
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                txtFile = Utilities.WriteTestText(null, null);
                resourcesFile = Path.ChangeExtension(txtFile, ".resources");
                File.Delete(resourcesFile);
                string csFile = Path.ChangeExtension(txtFile, ".cs");
                File.Delete(csFile);
                t.Sources = new ITaskItem[] { new TaskItem(txtFile) };
                t.StronglyTypedLanguage = "C#";
                dir = Path.Combine(Path.GetTempPath(), "directory");
                Directory.CreateDirectory(dir);
                t.StronglyTypedFileName = dir;

                bool success = t.Execute();
                // Task should have failed
                Assert.False(success);

                Utilities.AssertLogContains(t, "MSB3570");
                Utilities.AssertLogContains(t, t.StronglyTypedClassName);

                // Since STR creation fails, doesn't create the .resources file either
                Assert.False(File.Exists(resourcesFile));
                Assert.False(File.Exists(csFile));
                Assert.Empty(t.FilesWritten);
            }
            finally
            {
                if (txtFile != null)
                {
                    File.Delete(txtFile);
                }

                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }

                if (dir != null)
                {
                    FileUtilities.DeleteWithoutTrailingBackslash(dir);
                }
            }
        }

        [WindowsFullFrameworkOnlyFact(additionalMessage: ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void Regress25163_OutputResourcesContainsInvalidPathCharacters()
        {
            string resourcesFile = null;

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                resourcesFile = Utilities.WriteTestResX(false, null, null);

                t.Sources = new ITaskItem[] { new TaskItem(resourcesFile) };
                t.OutputResources = new ITaskItem[] { new TaskItem("||") };

                bool success = t.Execute();

                Assert.False(success); // "Task should have failed."

                Utilities.AssertLogContains(t, "MSB3553");
            }
            finally
            {
                if (resourcesFile != null)
                {
                    File.Delete(resourcesFile);
                }
            }
        }
    }

    public class References
    {
        private readonly ITestOutputHelper _output;

        public References(ITestOutputHelper output)
        {
            _output = output;
        }

        [WindowsFullFrameworkOnlyFact(additionalMessage: "Linked resources not supported on Core: https://github.com/dotnet/msbuild/issues/4094")]
        public void DontLockP2PReferenceWhenResolvingSystemTypes()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing DontLockP2PReferenceWhenResolvingSystemTypes() test");

            // -------------------------------------------------------------------------------
            // Need to produce a .DLL assembly on disk, so we can pass it in as a reference to
            // GenerateResource.
            // -------------------------------------------------------------------------------
            ObjectModelHelpers.DeleteTempProjectDirectory();

            ObjectModelHelpers.CreateFileInTempProjectDirectory("lib1.csproj", $@"
                    <Project DefaultTargets=`Build` xmlns=`msbuildnamespace`>
                        <PropertyGroup>
                            <ProjectType>Local</ProjectType>
                            <Configuration Condition=` '$(Configuration)' == '' `>Debug</Configuration>
                            <Platform Condition=` '$(Platform)' == '' `>AnyCPU</Platform>
                            <AssemblyName>lib1</AssemblyName>
                            <TargetFrameworkVersion>{MSBuildConstants.StandardTestTargetFrameworkVersion}</TargetFrameworkVersion>
                            <OutputType>Library</OutputType>
                            <RootNamespace>lib1</RootNamespace>
                        </PropertyGroup>
                        <PropertyGroup Condition=` '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' `>
                            <OutputPath>bin\Debug\</OutputPath>
                            <DebugSymbols>true</DebugSymbols>
                            <Optimize>false</Optimize>
                        </PropertyGroup>
                        <PropertyGroup Condition=` '$(Configuration)|$(Platform)' == 'Release|AnyCPU' `>
                            <OutputPath>bin\Release\</OutputPath>
                            <DebugSymbols>false</DebugSymbols>
                            <Optimize>true</Optimize>
                        </PropertyGroup>
                        <ItemGroup>
                            <Reference Include=`System`/>
                            <Compile Include=`Class1.cs`/>
                        </ItemGroup>
                        <Import Project=`$(MSBuildBinPath)\Microsoft.CSharp.targets` />
                    </Project>

                ");

            ObjectModelHelpers.CreateFileInTempProjectDirectory("Class1.cs", @"
                    public class Class1
                    {
                    }
                ");

            MockLogger logger = new MockLogger(_output);
            ObjectModelHelpers.BuildTempProjectFileExpectSuccess("lib1.csproj", logger);

            string p2pReference = Path.Combine(ObjectModelHelpers.TempProjectDir, "bin", "debug", "lib1.dll");
            Assert.True(File.Exists(p2pReference)); // "lib1.dll doesn't exist."

            // -------------------------------------------------------------------------------
            // Done producing an assembly on disk.
            // -------------------------------------------------------------------------------

            // Create a .RESX that references unqualified (without an assembly name) System types.
            ObjectModelHelpers.CreateFileInTempProjectDirectory(@"MyStrings.resx", @"

                    <root>
                        <xsd:schema id=`root` xmlns=`` xmlns:xsd=`http://www.w3.org/2001/XMLSchema` xmlns:msdata=`urn:schemas-microsoft-com:xml-msdata`>
                            <xsd:element name=`root` msdata:IsDataSet=`true`>
                                <xsd:complexType>
                                    <xsd:choice maxOccurs=`unbounded`>
                                        <xsd:element name=`data`>
                                            <xsd:complexType>
                                                <xsd:sequence>
                                                    <xsd:element name=`value` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`1` />
                                                    <xsd:element name=`comment` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`2` />
                                                </xsd:sequence>
                                                <xsd:attribute name=`name` type=`xsd:string` />
                                                <xsd:attribute name=`type` type=`xsd:string` />
                                                <xsd:attribute name=`mimetype` type=`xsd:string` />
                                            </xsd:complexType>
                                        </xsd:element>
                                        <xsd:element name=`resheader`>
                                            <xsd:complexType>
                                                <xsd:sequence>
                                                    <xsd:element name=`value` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`1` />
                                                </xsd:sequence>
                                                <xsd:attribute name=`name` type=`xsd:string` use=`required` />
                                            </xsd:complexType>
                                        </xsd:element>
                                    </xsd:choice>
                                </xsd:complexType>
                            </xsd:element>
                        </xsd:schema>
                        <resheader name=`ResMimeType`>
                            <value>text/microsoft-resx</value>
                        </resheader>
                        <resheader name=`Version`>
                            <value>1.0.0.0</value>
                        </resheader>
                        <resheader name=`Reader`>
                            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                        </resheader>
                        <resheader name=`Writer`>
                            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                        </resheader>
                        <data name=`GraphLegend` type=`System.String`>
                            <value>Graph Legend</value>
                            <comment>Used in reports to label the graph legend that pops up</comment>
                        </data>
                        <data name=`ccResponses` type=`System.String`>
                            <value>{0}'s Responses</value>
                            <comment>Used in challenge checklist tables</comment>
                        </data>
                        <data name=`ccStrength` type=`System.String`>
                            <value>Strength Area</value>
                            <comment>Used in challenge checklist tables</comment>
                        </data>
                        <data name=`ccNeutral` type=`System.String`>
                            <value>Neutral Area</value>
                            <comment>Used in challenge checklist tables</comment>
                        </data>
                        <data name=`ccChallenge` type=`System.String`>
                            <value>Challenge Area</value>
                            <comment>Used in challenge checklist tables</comment>
                        </data>
                        <data name=`calculation` type=`System.String`>
                            <value>Click here for scale calculation</value>
                            <comment>Used in Profile Scale area of main report to point to resource section scale tables.</comment>
                        </data>
                        <data name=`PageNumber` type=`System.String`>
                            <value>Page </value>
                            <comment>In footer of PDF report, and used in PDF links</comment>
                        </data>
                        <data name=`TOC` type=`System.String`>
                            <value>Table of Contents</value>
                            <comment>On second page of PDF report</comment>
                        </data>
                        <data name=`ParticipantListingAnd`>
                            <value>and</value>
                            <comment>On title page of PDF, joining two participants in a list</comment>
                        </data>
                    </root>

                ");

            // Run the GenerateResource task on the above .RESX file, passing in an unused reference
            // to lib1.dll.
            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] { new TaskItem(Path.Combine(ObjectModelHelpers.TempProjectDir, "MyStrings.resx")) };
            t.UseSourcePath = false;
            t.NeverLockTypeAssemblies = false;
            t.References = new ITaskItem[]
                {
                    new TaskItem(p2pReference),
#if !RUNTIME_TYPE_NETCORE
                    // Path to System.dll
                    new TaskItem(new Uri((typeof(string)).Assembly.EscapedCodeBase).LocalPath)
#else
#endif
                };

            bool success = t.Execute();

            // Make sure the resource was built.
            Assert.True(success); // "GenerateResource failed"
            ObjectModelHelpers.AssertFileExistsInTempProjectDirectory("MyStrings.resources");

            // Make sure the P2P reference is not locked after calling GenerateResource.
            File.Delete(p2pReference);
        }

        /// <summary>
        /// A reference is being passed into the GenerateResource task, but it's specified
        /// using a relative path.  GenerateResource was failing on this, because in the
        /// ResolveAssembly handler, it was calling Assembly.LoadFile on that relative path,
        /// which fails (LoadFile requires an absolute path).  The fix was to use
        /// Assembly.LoadFrom instead.
        /// </summary>
        [WindowsFullFrameworkOnlyFact(additionalMessage: "Linked resources not supported on Core: https://github.com/dotnet/msbuild/issues/4094")]
        public void ReferencedAssemblySpecifiedUsingRelativePath()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing ReferencedAssemblySpecifiedUsingRelativePath() test");

            // -------------------------------------------------------------------------------
            // Need to produce a .DLL assembly on disk, so we can pass it in as a reference to
            // GenerateResource.
            // -------------------------------------------------------------------------------
            ObjectModelHelpers.DeleteTempProjectDirectory();

            ObjectModelHelpers.CreateFileInTempProjectDirectory("ClassLibrary20.csproj", $@"
                    <Project DefaultTargets=`Build` xmlns=`msbuildnamespace`>
                        <PropertyGroup>
                            <ProjectType>Local</ProjectType>
                            <Configuration Condition=` '$(Configuration)' == '' `>Debug</Configuration>
                            <Platform Condition=` '$(Platform)' == '' `>AnyCPU</Platform>
                            <TargetFrameworkVersion>{MSBuildConstants.StandardTestTargetFrameworkVersion}</TargetFrameworkVersion>
                            <AssemblyName>ClassLibrary20</AssemblyName>
                            <OutputType>Library</OutputType>
                            <RootNamespace>lib1</RootNamespace>
                        </PropertyGroup>
                        <PropertyGroup Condition=` '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' `>
                            <OutputPath>bin\Debug\</OutputPath>
                            <DebugSymbols>true</DebugSymbols>
                            <Optimize>false</Optimize>
                        </PropertyGroup>
                        <PropertyGroup Condition=` '$(Configuration)|$(Platform)' == 'Release|AnyCPU' `>
                            <OutputPath>bin\Release\</OutputPath>
                            <DebugSymbols>false</DebugSymbols>
                            <Optimize>true</Optimize>
                        </PropertyGroup>
                        <ItemGroup>
                            <Reference Include=`System`/>
                            <Compile Include=`Class1.cs`/>
                        </ItemGroup>
                        <Import Project=`$(MSBuildBinPath)\Microsoft.CSharp.targets` />
                    </Project>

                ");

            ObjectModelHelpers.CreateFileInTempProjectDirectory("Class1.cs", @"
                    using System;
                    using System.Collections.Generic;
                    using System.Text;

                    namespace ClassLibrary20
                    {
                        [Serializable]
                        public class Class1
                        {
                            public string foo;
                        }
                    }
                ");

            MockLogger logger = new MockLogger(_output);
            ObjectModelHelpers.BuildTempProjectFileExpectSuccess("ClassLibrary20.csproj", logger);

            // -------------------------------------------------------------------------------
            // Done producing an assembly on disk.
            // -------------------------------------------------------------------------------

            // Create a .RESX that references a type from ClassLibrary20.dll
            ObjectModelHelpers.CreateFileInTempProjectDirectory(@"MyStrings.resx", @"

                    <root>
                        <xsd:schema id=`root` xmlns=`` xmlns:xsd=`http://www.w3.org/2001/XMLSchema` xmlns:msdata=`urn:schemas-microsoft-com:xml-msdata`>
                            <xsd:element name=`root` msdata:IsDataSet=`true`>
                                <xsd:complexType>
                                    <xsd:choice maxOccurs=`unbounded`>
                                        <xsd:element name=`data`>
                                            <xsd:complexType>
                                                <xsd:sequence>
                                                    <xsd:element name=`value` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`1` />
                                                    <xsd:element name=`comment` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`2` />
                                                </xsd:sequence>
                                                <xsd:attribute name=`name` type=`xsd:string` />
                                                <xsd:attribute name=`type` type=`xsd:string` />
                                                <xsd:attribute name=`mimetype` type=`xsd:string` />
                                            </xsd:complexType>
                                        </xsd:element>
                                        <xsd:element name=`resheader`>
                                            <xsd:complexType>
                                                <xsd:sequence>
                                                    <xsd:element name=`value` type=`xsd:string` minOccurs=`0` msdata:Ordinal=`1` />
                                                </xsd:sequence>
                                                <xsd:attribute name=`name` type=`xsd:string` use=`required` />
                                            </xsd:complexType>
                                        </xsd:element>
                                    </xsd:choice>
                                </xsd:complexType>
                            </xsd:element>
                        </xsd:schema>
                        <resheader name=`ResMimeType`>
                            <value>text/microsoft-resx</value>
                        </resheader>
                        <resheader name=`Version`>
                            <value>1.0.0.0</value>
                        </resheader>
                        <resheader name=`Reader`>
                            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                        </resheader>
                        <resheader name=`Writer`>
                            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                        </resheader>
                        <data name=`Image1` type=`ClassLibrary20.Class1, ClassLibrary20, version=1.0.0.0, Culture=neutral, PublicKeyToken=null`>
                            <value>blah</value>
                        </data>
                    </root>

                ");

            // Run the GenerateResource task on the above .RESX file, passing in an unused reference
            // to lib1.dll.
            GenerateResource t = Utilities.CreateTask(_output);
            t.Sources = new ITaskItem[] { new TaskItem(Path.Combine(ObjectModelHelpers.TempProjectDir, "MyStrings.resx")) };
            t.UseSourcePath = false;
            t.NeverLockTypeAssemblies = false;

            TaskItem reference = new TaskItem(@"bin\debug\ClassLibrary20.dll");
            reference.SetMetadata("FusionName", "ClassLibrary20, version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

            t.References = new ITaskItem[] { reference };

            // Set the current working directory to the location of ClassLibrary20.csproj.
            // This is what allows us to pass in a relative path to the referenced assembly.
            string originalCurrentDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(ObjectModelHelpers.TempProjectDir);

            try
            {
                bool success = t.Execute();
                // Make sure the resource was built.
                Assert.True(success); // "GenerateResource failed"
                ObjectModelHelpers.AssertFileExistsInTempProjectDirectory("MyStrings.resources");
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCurrentDirectory);
            }
        }
    }

    public class MiscTests
    {
        private readonly ITestOutputHelper _output;

        public MiscTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ResgenCommandLineLogging()
        {
            // This WriteLine is a hack.  On a slow machine, the Tasks unittest fails because remoting
            // times out the object used for remoting console writes.  Adding a write in the middle of
            // keeps remoting from timing out the object.
            Console.WriteLine("Performing ResgenCommandLineLogging() test");

            // we use this to check if paths need quoting
            CommandLineBuilderHelper commandLineBuilderHelper = new CommandLineBuilderHelper();

            string resxFile = Utilities.WriteTestResX(false, null, null);
            string resourcesFile = Path.ChangeExtension(resxFile, ".resources");
            File.Delete(resourcesFile);

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.UseSourcePath = false;
                t.NeverLockTypeAssemblies = false;
                t.Execute();

                string possiblyQuotedResxFile = resxFile;
                string possiblyQuotedResourcesFile = resourcesFile;

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resxFile))
                {
                    possiblyQuotedResxFile = "\"" + resxFile + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resourcesFile))
                {
                    possiblyQuotedResourcesFile = "\"" + resourcesFile + "\"";
                }

                Utilities.AssertLogContains(
                    t,
                    "/compile " + possiblyQuotedResxFile + ","
                    + possiblyQuotedResourcesFile);
            }
            finally
            {
                File.Delete(resxFile);
                File.Delete(resourcesFile);
            }

            resxFile = Utilities.WriteTestResX(false, null, null);
            resourcesFile = Path.ChangeExtension(resxFile, ".resources");
            File.Delete(resourcesFile);

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.References = new ITaskItem[] { new TaskItem("baz"), new TaskItem("jazz") };
                t.UseSourcePath = true;
                t.PublicClass = true;
                t.StronglyTypedLanguage = "C#";
                t.NeverLockTypeAssemblies = false;
                t.Execute();

                string possiblyQuotedResxFile = resxFile;
                string possiblyQuotedResourcesFile = resourcesFile;

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resxFile))
                {
                    possiblyQuotedResxFile = "\"" + resxFile + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resourcesFile))
                {
                    possiblyQuotedResourcesFile = "\"" + resourcesFile + "\"";
                }

                Utilities.AssertLogContains(
                    t,
                    "/useSourcePath "
                    + "/publicClass "
                    + "/r:baz "
                    + "/r:jazz " + possiblyQuotedResxFile + " "
                    + possiblyQuotedResourcesFile + " " + "/str:\"C#\",,,");
            }
            finally
            {
                File.Delete(resxFile);
                File.Delete(resourcesFile);
                File.Delete(Path.ChangeExtension(resxFile, ".cs"));
            }

            resxFile = Utilities.WriteTestResX(false, null, null);
            resourcesFile = Path.ChangeExtension(resxFile, ".resources");
            File.Delete(resourcesFile);

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };
                t.References = new ITaskItem[] { new TaskItem("baz"), new TaskItem("jazz") };
                t.UseSourcePath = true;
                t.StronglyTypedLanguage = "C#";
                t.StronglyTypedClassName = "wagwag";
                t.StronglyTypedFileName = "boo";
                t.NeverLockTypeAssemblies = false;
                t.Execute();

                string possiblyQuotedResxFile = resxFile;
                string possiblyQuotedResourcesFile = resourcesFile;

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resxFile))
                {
                    possiblyQuotedResxFile = "\"" + resxFile + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resourcesFile))
                {
                    possiblyQuotedResourcesFile = "\"" + resourcesFile + "\"";
                }

                Utilities.AssertLogContains(
                    t,
                    "/useSourcePath "
                    + "/r:baz "
                    + "/r:jazz " + possiblyQuotedResxFile + " "
                    + possiblyQuotedResourcesFile + " "
                    + "/str:\"C#\",,wagwag,boo");
            }
            finally
            {
                File.Delete(resxFile);
                File.Delete(resourcesFile);
            }

            resxFile = Utilities.WriteTestResX(false, null, null);
            resourcesFile = Path.ChangeExtension(resxFile, ".myresources");
            File.Delete(resourcesFile);
            string resxFile1 = Utilities.WriteTestResX(false, null, null);
            string resourcesFile1 = Path.ChangeExtension(resxFile1, ".myresources");
            File.Delete(resourcesFile1);

            try
            {
                GenerateResource t = Utilities.CreateTask(_output);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile), new TaskItem(resxFile1) };
                t.OutputResources = new ITaskItem[]
                                    {
                                        new TaskItem(resourcesFile),
                                        new TaskItem(resourcesFile1)
                                    };
                t.NeverLockTypeAssemblies = false;
                t.Execute();

                string possiblyQuotedResxFile = resxFile;
                string possiblyQuotedResourcesFile = resourcesFile;
                string possiblyQuotedResxFile1 = resxFile1;
                string possiblyQuotedResourcesFile1 = resourcesFile1;

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resxFile))
                {
                    possiblyQuotedResxFile = "\"" + resxFile + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resourcesFile))
                {
                    possiblyQuotedResourcesFile = "\"" + resourcesFile + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resxFile1))
                {
                    possiblyQuotedResxFile1 = "\"" + resxFile1 + "\"";
                }

                if (commandLineBuilderHelper.DoesPathNeedQuotes(resourcesFile1))
                {
                    possiblyQuotedResourcesFile1 = "\"" + resourcesFile1 + "\"";
                }

                Utilities.AssertLogContains(t,
                    "/compile " +
                    possiblyQuotedResxFile +
                    "," +
                    possiblyQuotedResourcesFile +
                    " " +
                    possiblyQuotedResxFile1 +
                    "," +
                    possiblyQuotedResourcesFile1);
            }
            finally
            {
                File.Delete(resxFile);
                File.Delete(resourcesFile);
                File.Delete(resxFile1);
                File.Delete(resourcesFile1);
            }
        }

        /// <summary>
        /// In order to make GenerateResource multitargetable, a property, ExecuteAsTool, was added.
        /// In order to have correct behavior when using pre-4.0
        /// toolsversions, ExecuteAsTool must default to true, and the paths to the tools will be the
        /// v3.5 path.  It is difficult to verify the tool paths in a unit test, however, so
        /// this was done by ad hoc testing and will be maintained by the dev suites.
        /// </summary>
        [Fact]
        public void MultiTargetingDefaultsSetCorrectly()
        {
            GenerateResource t = new GenerateResource();

            Assert.True(t.ExecuteAsTool); // "ExecuteAsTool should default to true"
        }

        // Regression test for https://github.com/dotnet/msbuild/issues/2206
        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("\r")]
        public void ResxValueNewlines(string newline)
        {
            string resxValue = "First line" + newline + "second line" + newline;
            string resxDataName = "DataWithNewline";
            string data = "<data name=\"" + resxDataName + "\">" + newline +
                "<value>" + resxValue + "</value>" + newline + "</data>";
            GenerateResource t = Utilities.CreateTask(_output);
            t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));

            try
            {
                string resxFile = Utilities.WriteTestResX(false, null, data);
                t.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                Utilities.ExecuteTask(t);

                string resourcesFile = t.OutputResources[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Dictionary<string, object> valuesFromResource = new Dictionary<string, object>();
                using (var resourceReader = new System.Resources.ResourceReader(resourcesFile))
                {
                    IDictionaryEnumerator resEnum = resourceReader.GetEnumerator();
                    while (resEnum.MoveNext())
                    {
                        string name = (string)resEnum.Key;
                        object value = resEnum.Value;
                        valuesFromResource[name] = value;
                    }
                }

                Assert.True(valuesFromResource.ContainsKey(resxDataName));
                Assert.Equal(resxValue, valuesFromResource[resxDataName]);
            }
            finally
            {
                File.Delete(t.Sources[0].ItemSpec);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        [Fact]
        public void ShouldNotRegenResourcesWhenRebuildingInPresenceOfFileRefWithWindowsPath()
        {
            using (var env = TestEnvironment.Create())
            {
                env.SetCurrentDirectory(env.DefaultTestDirectory.Path);

                string fileRef = "<data name=\"TextFile1\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\">" +
                                $"<value>.\\tmp_dir\\test_file.txt;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;Windows-1252</value></data>";

                env.CreateFile(
                        env.CreateFolder(Path.Combine(env.DefaultTestDirectory.Path, "tmp_dir")),
                        "test_file.txt", "xyz");

                string resxFile = env.CreateFile("test.resx").Path;
                Utilities.WriteTestResX(false, null, fileRef, false, resxFile);

                GenerateResource ExecuteTask()
                {
                    GenerateResource task = Utilities.CreateTask(_output);
                    task.Sources = new ITaskItem[] { new TaskItem(resxFile) };

                    Utilities.ExecuteTask(task);

                    string outputResourceFile = task.OutputResources[0].ItemSpec;
                    task.OutputResources[0].ItemSpec.ShouldBe(task.FilesWritten[0].ItemSpec);
                    Path.GetExtension(outputResourceFile).ShouldBe(".resources");

                    return task;
                }

                GenerateResource t = ExecuteTask();
                string resourcesFile = t.OutputResources[0].ItemSpec;
                DateTime initialWriteTime = File.GetLastWriteTime(resourcesFile);

                // fs granularity on HFS is 1 sec!
                System.Threading.Thread.Sleep(NativeMethodsShared.IsOSX ? 1000 : 100);

                // Rebuild, it shouldn't regen .resources file since the sources
                // haven't changed
                t = ExecuteTask();
                resourcesFile = t.OutputResources[0].ItemSpec;

                Utilities.FileUpdated(resourcesFile, initialWriteTime).ShouldBeFalse();
            }
        }

        /// <summary>
        /// https://github.com/dotnet/msbuild/issues/9199
        /// </summary>
        [Fact]
        public void NotValidSources()
        {
            GenerateResource t = new GenerateResource { BuildEngine = new MockEngine(_output) };
            t.Sources = new ITaskItem[] { new TaskItem("non-existent") };
            t.OutputResources = new ITaskItem[] { new TaskItem("out") };
            Assert.False(t.Execute());
            ((MockEngine)t.BuildEngine).AssertLogContains("MSB3552");
            Assert.Equal(1, ((MockEngine)t.BuildEngine).Errors);
        }
    }
}

namespace Microsoft.Build.UnitTests.GenerateResource_Tests
{
    /// <summary>
    /// This Utilities class provides some static helper methods for resource tests
    /// </summary>
    internal sealed partial class Utilities
    {
        /// <summary>
        /// Set the last write time to be n minutes back in time.
        /// </summary>
        public static DateTime MoveBackTimestamp(string fileName, int minutes)
        {
            DateTime newTime = File.GetLastWriteTime(fileName) - new TimeSpan(0, minutes, 0);
            File.SetLastWriteTime(fileName, newTime);
            return newTime;
        }

        /// <summary>
        /// Return whether the file was written to since the specified time.
        /// </summary>
        public static bool FileUpdated(string fileName, DateTime previousWriteTime)
        {
            return File.GetLastWriteTime(fileName) > previousWriteTime;
        }

        /// <summary>
        /// Looks for a message in the output log for the task execution, including formatted parameters.
        /// </summary>
        public static void AssertLogContainsResource(GenerateResource t, string messageID, params object[] replacements)
        {
            Assert.Contains(
                String.Format(AssemblyResources.GetString(messageID), replacements),
                ((MockEngine)t.BuildEngine).Log);
        }

        /// <summary>
        /// Looks for a formatted message in the output log for the task execution, with unknown formatted parameters.
        /// If verifies that all constant segments of unformatted message are present.
        /// </summary>
        public static void AssertLogContainsResourceWithUnspecifiedReplacements(GenerateResource t, string messageID)
        {
            var unformattedMessage = AssemblyResources.GetString(messageID);
            var matches = Regex.Matches(unformattedMessage, @"\{\d+.*?\}");
            if (matches.Count > 0)
            {
                var sb = new StringBuilder();
                int i = 0;

                foreach (Match match in matches)
                {
                    string segment = unformattedMessage.Substring(i, match.Index - i);
                    sb.Append(Regex.Escape(segment));
                    sb.Append(".*");

                    i = match.Index + match.Length;
                }
                if (i < unformattedMessage.Length)
                {
                    sb.Append(Regex.Escape(unformattedMessage.Substring(i)));
                }

                Assert.Matches(sb.ToString(), ((MockEngine)t.BuildEngine).Log);
            }
            else
            {
                Assert.Contains(unformattedMessage, ((MockEngine)t.BuildEngine).Log);
            }
        }

        /// <summary>
        /// Looks for a message in the output log for the task execution., including formatted parameters.
        /// </summary>
        public static void AssertLogContains(GenerateResource t, string message)
        {
            Assert.Contains(message, ((MockEngine)t.BuildEngine).Log);
        }

        /// <summary>
        /// Looks for a message in the output log for the task execution, including formatted parameters.
        /// </summary>
        public static void AssertLogNotContainsResource(GenerateResource t, string messageID, params object[] replacements)
        {
            Assert.DoesNotContain(String.Format(AssemblyResources.GetString(messageID), replacements), ((MockEngine)t.BuildEngine).Log);
        }

        /// <summary>
        /// Looks for a message in the output log for the task execution., including formatted parameters.
        /// </summary>
        public static void AssertLogNotContains(GenerateResource t, string message)
        {
            Assert.DoesNotContain(message, ((MockEngine)t.BuildEngine).Log);
        }

        /// <summary>
        /// Given an array of ITaskItems, checks to make sure that at least one read tlog and at least one
        /// write tlog exist, and that they were written to disk.  If that is not true, asserts.
        /// </summary>
        public static void AssertStateFileWasWritten(GenerateResource t)
        {
            Assert.NotNull(t.FilesWritten); // "The state file should have been written, but there aren't any."
            Assert.NotNull(t.StateFile); // "State file should be defined"
            Assert.True(File.Exists(t.StateFile.ItemSpec)); // "State file should exist"

            bool foundStateFile = false;

            // start from the end because the statefile is usually marked as a written file fairly late in the process
            for (int i = t.FilesWritten.Length - 1; i >= 0; i--)
            {
                if (t.StateFile.ItemSpec.Equals(t.FilesWritten[i].ItemSpec))
                {
                    foundStateFile = true;
                    break;
                }
            }

            Assert.True(foundStateFile); // "Expected there to be a state file, but there wasn't"
        }

        /// <summary>
        /// </summary>
        public static string CreateBasicResourcesFile(bool useResX, ITestOutputHelper output)
        {
            GenerateResource t = CreateTask(output);

            string sourceFile;
            if (useResX)
            {
                sourceFile = WriteTestResX(false, null, null);
            }
            else
            {
                sourceFile = WriteTestText(null, null);
            }

            t.Sources = new ITaskItem[] { new TaskItem(sourceFile) };

            // phase 1, generate the .resources file (we don't care about outcomes)
            Utilities.ExecuteTask(t);

            File.Delete(sourceFile);
            return t.OutputResources[0].ItemSpec;
        }

        /// <summary>
        /// </summary>
        public static string ReadFileContent(string fileName)
        {
            return File.ReadAllText(fileName);
        }

        /// <summary>
        /// ExecuteTask performs the task Execute method and asserts basic success criteria
        /// </summary>
        public static void ExecuteTask(GenerateResource t)
        {
            bool success = t.Execute();
            Assert.True(success);

            if (t.OutputResources?[0] != null && t.Sources[0] != null)
            {
                File.GetLastWriteTime(t.OutputResources[0].ItemSpec).ShouldBeGreaterThanOrEqualTo(File.GetLastWriteTime(t.Sources[0].ItemSpec), $"we're talking here about {t.OutputResources[0].ItemSpec} and {t.Sources[0].ItemSpec}");
            }
        }

        /// <summary>
        /// This method creates a GenerateResource task and performs basic setup on it, e.g. BuildEngine
        /// </summary>
        /// <param name="output"></param>
        public static GenerateResource CreateTask(
            ITestOutputHelper output,
            bool usePreserialized = false,
            TestEnvironment env = null,
            bool? useSystemResourcesExtensions = null)
        {
            // always use the internal ctor that says don't perform separate app domain check
            GenerateResource t = new GenerateResource();
            t.BuildEngine = new MockEngine(output);

            // Make the task execute in-proc
            t.ExecuteAsTool = false;

            if (usePreserialized)
            {
                t.UsePreserializedResources = usePreserialized;
            }

            if (useSystemResourcesExtensions ?? usePreserialized)
            {
                // Synthesize a reference that looks close enough to System.Resources.Extensions
                // to pass the "is it ok to use preserialized resources?" check

                var folder = env.CreateFolder(true);
                var dll = folder.CreateFile("System.Resource.Extensions.dll");

                // Make sure the reference looks old relative to all the other inputs
                File.SetLastWriteTime(dll.Path, DateTime.Now - TimeSpan.FromDays(30));

                var referenceItem = new TaskItem(dll.Path);
                referenceItem.SetMetadata(ItemMetadataNames.fusionName, "System.Resources.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");

                t.References = new ITaskItem[] {
                    referenceItem
                };
            }

            return t;
        }

        /// <summary>
        /// This method creates and returns a string that is the contents of a canonical .txt resource file.
        /// <param name="tagName">Gives the opportunity to create a warning/error in the text by specifying a [tag] value, null for nothing.</param>
        /// <param name="oneLine">Gives the opportunity to add one name-value pair to the text.  Null for nothing.</param>
        /// </summary>
        /// <returns>The content of the text blob as a string</returns>
        public static string GetTestTextContent(string tagName, string oneLine)
        {
            return GetTestTextContent(tagName, oneLine, false);
        }

        /// <summary>
        /// Allows test to get the cleaned up resources, as they would be expected after being transformed
        /// back and forth.
        /// </summary>
        /// <param name="tagName"></param>
        /// <param name="oneLine"></param>
        /// <param name="cleanedUp"></param>
        /// <returns></returns>
        public static string GetTestTextContent(string tagName, string oneLine, bool cleanedUp)
        {
            // Make sure these are in alpha order by name, as the round trip will sort them
            string textFileContents;

            if (!cleanedUp)
            {
                textFileContents =
                    "\nMalade=There is trouble in the hen\\n house\xd\xa"
                   + "# this is a comment\xd\xa"
                   + "Marley=The man, the myth, \\rthe legend\xd\xa"
                   + "Name2 = Put the li\u1111me in the \\tcoconut and drink 'em both up\xd\xa"
                   + "Name1=Some S\\\\tring Comes \\\"Here\xd\xa";
            }
            else
            {
                // Content as it would be expected after being transformed and transformed back
                textFileContents =
                    "Malade=There is trouble in the hen\\n house\xd\xa"
                   + "Marley=The man, the myth, \\rthe legend\xd\xa"
                   + "Name2=Put the li\u1111me in the \\tcoconut and drink 'em both up\xd\xa"
                   + "Name1=Some S\\\\tring Comes \"Here\xd\xa";
            }

            StringBuilder txt = new StringBuilder();

            if (tagName != null)
            {
                txt.Append('[');
                txt.Append(tagName);
                txt.Append("]\xd\xa");
            }

            txt.Append(textFileContents);

            if (oneLine != null)
            {
                txt.Append(oneLine);
                txt.Append("\xd\xa");
            }

            return txt.ToString();
        }

        /// <summary>
        /// This method creates a temporary file based on the canonical .txt resource file.
        /// <param name="tagName">Gives the opportunity to create a warning/error in the text by specifying a [tag] value, null for nothing.</param>
        /// <param name="oneLine">Gives the opportunity to add one name-value pair to the text.  Null for nothing.</param>
        /// </summary>
        public static string WriteTestText(string tagName, string oneLine)
        {
            string textFile = Utilities.GetTempFileName(".txt");
            File.Delete(textFile);
            File.WriteAllText(textFile, GetTestTextContent(tagName, oneLine));
            return textFile;
        }

        /// <summary>
        /// Write a test .resx file to a temporary location.
        /// </summary>
        /// <param name="useType">Indicates whether to include an enum to test type-specific resource encoding with assembly references</param>
        /// <param name="linkedBitmap">The name of a linked-in bitmap.  use 'null' for no bitmap.</param>
        /// <returns>The content of the resx blob as a string</returns>
        /// <returns>The name of the text file</returns>
        public static string GetTestResXContent(bool useType, string linkedBitmap, string extraToken, bool useInvalidType)
        {
            StringBuilder resgenFileContents = new StringBuilder();

            resgenFileContents.Append(
                 "<root>\xd\xa"
                + "  <resheader name='resmimetype'>\xd\xa"
                + "    <value>text/microsoft-resx</value>\xd\xa"
                + "  </resheader>\xd\xa"
                + "  <resheader name='version'>\xd\xa"
                + "    <value>2.0</value>\xd\xa"
                + "  </resheader>\xd\xa"
                + "  <resheader name='reader'>\xd\xa"
                + "    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\xd\xa"
                + "  </resheader>\xd\xa"
                + "  <resheader name='writer'>\xd\xa"
                + "    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\xd\xa"
                + "  </resheader>\xd\xa");

            resgenFileContents.Append(
                 // A plain old string value.
                 "  <data name=\"MyString\">\xd\xa"
                + "    <value>MyValue</value>\xd\xa"
                + "  </data>\xd\xa");

            if (extraToken != null)
            {
                resgenFileContents.Append(extraToken);
            }

            if (useType)
            {
                // A non-standard type. In this case, an enum.
                resgenFileContents.Append(
                     "  <data name='Label.Modifiers' type='System.CodeDom.MemberAttributes, System'>\xd\xa"
                    + "    <value>Assembly</value>\xd\xa"
                    + "  </data>\xd\xa");
            }

            if (useInvalidType)
            {
                // A type that won't be resolved.. oops!
                resgenFileContents.Append(
                     "  <data name='xx' type='X, INVALID'>\xd\xa"
                    + "    <value>1</value>\xd\xa"
                    + "  </data>\xd\xa");
            }

            if (linkedBitmap != null)
            {
                // A linked-in bitmap.
                resgenFileContents.Append(
                     "  <data name='Image1' type='System.Resources.ResXFileRef, System.Windows.Forms'>\xd\xa"
                    + "    <value>");

                // The linked file may have a different case than reported by the filesystem
                // simulate this by lower-casing our file before writing it into the resx.
                resgenFileContents.Append(
                    NativeMethodsShared.IsWindows
                        ? linkedBitmap.ToUpperInvariant()
                        : linkedBitmap);

                resgenFileContents.Append(
                     ";System.Drawing.Bitmap, System.Drawing, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a</value>\xd\xa"
                    + "  </data>\xd\xa");
            }

            resgenFileContents.Append("</root>\xd\xa");

            return resgenFileContents.ToString();
        }

        /// <summary>
        /// Write a test .resx file to a temporary location.
        /// </summary>
        /// <param name="useType">Indicates whether to include an enum to test type-specific resource encoding with assembly references</param>
        /// <param name="linkedBitmap">The name of a linked-in bitmap.  use 'null' for no bitmap.</param>
        /// <returns>The name of the resx file</returns>
        public static string WriteTestResX(bool useType, string linkedBitmap, string extraToken, string resxFileToWrite = null, TestEnvironment env = null)
        {
            return WriteTestResX(useType, linkedBitmap, extraToken, useInvalidType: false, resxFileToWrite: resxFileToWrite);
        }

        /// <summary>
        /// Write a test .resx file to a temporary location.
        /// </summary>
        /// <param name="useType">Indicates whether to include an enum to test type-specific resource encoding with assembly references</param>
        /// <param name="linkedBitmap">The name of a linked-in bitmap.  use 'null' for no bitmap.</param>
        /// <returns>The name of the resx file</returns>
        public static string WriteTestResX(bool useType, string linkedBitmap, string extraToken, bool useInvalidType, string resxFileToWrite = null, TestEnvironment env = null)
        {
            string resgenFile = resxFileToWrite;

            string contents = GetTestResXContent(useType, linkedBitmap, extraToken, useInvalidType);

            if (env == null)
            {
                if (string.IsNullOrEmpty(resgenFile))
                {
                    resgenFile = GetTempFileName(".resx");
                }

                File.WriteAllText(resgenFile, contents);
            }
            else
            {
                resgenFile = env.CreateFile(".resx", contents).Path;
            }

            return resgenFile;
        }

        /// <summary>
        /// Copy system.dll (so we can later touch it) to a temporary location.
        /// </summary>
        /// <returns>The name of the copied file.</returns>
        public static string GetPathToCopiedSystemDLL()
        {
            string tempSystemDLL = Utilities.GetTempFileName(".dll");

            string pathToSystemDLL =
#if FEATURE_INSTALLED_MSBUILD
                ToolLocationHelper.GetPathToDotNetFrameworkFile("System.dll", TargetDotNetFrameworkVersion.Version45);
#else
                Path.Combine(BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory, "System.dll");
#endif

            File.Copy(pathToSystemDLL, tempSystemDLL);
            return tempSystemDLL;
        }

        /// <summary>
        /// Create a tiny bitmap at a temporary location.
        /// </summary>
        /// <returns>The name of the bitmap.</returns>
        public static string CreateWorldsSmallestBitmap()
        {
            string smallestBitmapFile = Utilities.GetTempFileName(".bmp");

            byte[] bmp = new byte[66];
            bmp[0x00] = 0x42; bmp[0x01] = 0x4D; bmp[0x02] = 0x42;
            bmp[0x0a] = 0x3E; bmp[0x0e] = 0x28; bmp[0x12] = 0x01; bmp[0x16] = 0x01;
            bmp[0x1a] = 0x01; bmp[0x1c] = 0x01; bmp[0x22] = 0x04;
            bmp[0x3a] = 0xFF; bmp[0x3b] = 0xFF; bmp[0x3c] = 0xFF;
            bmp[0x3e] = 0x80;

            File.Delete(smallestBitmapFile);
            File.WriteAllBytes(
                NativeMethodsShared.IsWindows ? smallestBitmapFile.ToUpperInvariant() : smallestBitmapFile,
                bmp);
            return smallestBitmapFile;
        }

        /// <summary>
        /// </summary>
        public static MethodInfo GetPrivateMethod(object o, string methodName)
        {
            return o.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>
        /// Since GetTempFileName creates an empty file, it's bad mojo to just append a new extension
        /// because when you clean up your modified filename, you'll leave behind the original .tmp
        /// file.  This method gives you a unique filename with your desired extension, but also
        /// deletes the original root file.  It's not perfect, but...
        /// </summary>
        public static string GetTempFileName(string extension)
        {
            string f = FileUtilities.GetTemporaryFileName();
            string filename = Path.ChangeExtension(f, extension);
            // Make sure that the new file doesn't already exist, since the test is probably
            // expecting it not to
            File.Delete(filename);
            return filename;
        }

        /// <summary>
        /// Helper method to test STRNamespace parameter of Generate Resource task
        /// </summary>
        /// <param name="strLanguage"></param>
        /// <param name="resourcesNamespace"></param>
        /// <param name="classNamespace"></param>
        /// <param name="output"></param>
        public static void STRNamespaceTestHelper(string strLanguage, string resourcesNamespace, string classNamespace, ITestOutputHelper output)
        {
            // these two parameters should not be null
            Assert.NotNull(strLanguage);
            Assert.NotNull(resourcesNamespace);
            // Generate Task
            GenerateResource t = Utilities.CreateTask(output);
            try
            {
                t.StateFile = new TaskItem(Utilities.GetTempFileName(".cache"));
                // Create an input text file
                string textFile = Utilities.WriteTestText(null, null);
                // set the Sources parameter
                t.Sources = new ITaskItem[] { new TaskItem(textFile) };
                // Set the StronglyTypedLanguage parameter
                t.StronglyTypedLanguage = strLanguage;
                // Set the StronglyTypedManifestPrefix parameter
                t.StronglyTypedManifestPrefix = resourcesNamespace;

                // Set the StronglyTypedNamespace parameter
                t.StronglyTypedNamespace = classNamespace;

                string codeFileExtension = null;
                if (strLanguage == "CSharp")
                {
                    codeFileExtension = ".cs";
                }
                else if (strLanguage == "VB")
                {
                    codeFileExtension = ".vb";
                }

                // Execute task
                Utilities.ExecuteTask(t);

                // Get the OutputResources
                string resourcesFile = t.OutputResources[0].ItemSpec;

                // Verify that the OutputResources has the same name as Sources (=textFile)
                Assert.Equal(Path.GetFileNameWithoutExtension(textFile), Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec));

                // Verify that STR class name should have been generated from the output
                string stronglyTypedClassName = Path.GetFileNameWithoutExtension(t.OutputResources[0].ItemSpec);
                Assert.Equal(t.StronglyTypedClassName, stronglyTypedClassName);

                // Verify that the extension of the resource file is .resources
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                // Verify that the 1st item in FilesWritten property is the .resource file generated
                resourcesFile = t.FilesWritten[0].ItemSpec;
                Assert.Equal(".resources", Path.GetExtension(resourcesFile));

                Utilities.AssertStateFileWasWritten(t);

                // Files written should contain STR class file
                Assert.Equal(Path.ChangeExtension(t.Sources[0].ItemSpec, codeFileExtension), t.StronglyTypedFileName);
                Assert.Equal(t.FilesWritten[2].ItemSpec, t.StronglyTypedFileName);

                // Verify that the STR File is generated
                Assert.True(File.Exists(t.StronglyTypedFileName));

                // Verify that the STR File was generated correctly
                string STRFile = Path.ChangeExtension(textFile, codeFileExtension);
                // Verify that the ResourceManager in the STR class is instantiated correctly
                Assert.Contains("ResourceManager(\"" + resourcesNamespace + "." + t.StronglyTypedClassName, Utilities.ReadFileContent(STRFile));
                // Verify that the class name of the STR class is as expected
                Assert.Contains("class " + Path.GetFileNameWithoutExtension(textFile).ToLower(), Utilities.ReadFileContent(STRFile).ToLower());
                // Verify that the namespace of the STR class is as expected

                Assert.DoesNotContain("namespace " + resourcesNamespace.ToLower(), Utilities.ReadFileContent(STRFile).ToLower());
                if (classNamespace != null)
                {
                    Assert.Contains("namespace " + classNamespace.ToLower(), Utilities.ReadFileContent(STRFile).ToLower());
                }

                // Verify log is as expected
                Utilities.AssertLogContainsResource(t, "GenerateResource.ProcessingFile", textFile, resourcesFile);
                Utilities.AssertLogContainsResource(t, "GenerateResource.ReadResourceMessage", 4, textFile);

                string typeName;
                if (t.StronglyTypedNamespace != null)
                {
                    typeName = t.StronglyTypedNamespace + ".";
                }
                else
                {
                    typeName = "";
                }

                typeName += t.StronglyTypedClassName;
                // Verify that the type is generated correctly
                Utilities.AssertLogContainsResource(t, "GenerateResource.CreatingSTR", t.StronglyTypedFileName);
            }
            finally
            {
                // Done, so clean up.
                File.Delete(t.Sources[0].ItemSpec);
                File.Delete(t.StronglyTypedFileName);
                foreach (ITaskItem item in t.FilesWritten)
                {
                    if (File.Exists(item.ItemSpec))
                    {
                        File.Delete(item.ItemSpec);
                    }
                }
            }
        }

        public static IEnumerable<object[]> UsePreserializedResourceStates()
        {
            // All MSBuilds should be able to use the new resource codepaths
            yield return new object[] { true };

#if FEATURE_RESXREADER_LIVEDESERIALIZATION
            // But the old get-live-objects codepath is supported only on full framework.
            yield return new object[] { false };
#endif
        }
    }

    /// <summary>
    /// Extends the CommandLineBuilderClass to get at its protected methods.
    /// </summary>
    internal sealed class CommandLineBuilderHelper : CommandLineBuilder
    {
        /// <summary>
        /// Redirects to the protected method IsQuotingRequired().
        /// </summary>
        /// <returns>true, if given path needs to be quoted.</returns>
        internal bool DoesPathNeedQuotes(string path)
        {
            return base.IsQuotingRequired(path);
        }
    }
}
