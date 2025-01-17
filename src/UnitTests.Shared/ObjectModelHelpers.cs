// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    /*
     * Class:   ObjectModelHelpers
     *
     * Utility methods for unit tests that work through the object model.
     *
     */
    public static class ObjectModelHelpers
    {
        private const string msbuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";
        private static string s_msbuildDefaultToolsVersion = MSBuildConstants.CurrentToolsVersion;
        private static string s_msbuildAssemblyVersion = MSBuildConstants.CurrentAssemblyVersion;
        private static string s_currentVisualStudioVersion = MSBuildConstants.CurrentVisualStudioVersion;

        /// <summary>
        /// Return the current Visual Studio version
        /// </summary>
        public static string CurrentVisualStudioVersion
        {
            get
            {
                return s_currentVisualStudioVersion;
            }
        }

        /// <summary>
        /// Return the default tools version
        /// </summary>
        public static string MSBuildDefaultToolsVersion
        {
            get
            {
                return s_msbuildDefaultToolsVersion;
            }
        }

        /// <summary>
        /// Return the current assembly version
        /// </summary>
        public static string MSBuildAssemblyVersion
        {
            get
            {
                return s_msbuildAssemblyVersion;
            }
        }

        /// <summary>
        /// Helper method to tell us whether a particular metadata name is an MSBuild well-known metadata
        /// (e.g., "RelativeDir", "FullPath", etc.)
        /// </summary>
        private static Hashtable s_builtInMetadataNames;
        private static bool IsBuiltInItemMetadataName(string metadataName)
        {
            if (s_builtInMetadataNames == null)
            {
                s_builtInMetadataNames = new Hashtable();

                Utilities.TaskItem dummyTaskItem = new Utilities.TaskItem();
                foreach (string builtInMetadataName in dummyTaskItem.MetadataNames)
                {
                    s_builtInMetadataNames[builtInMetadataName] = string.Empty;
                }
            }

            return s_builtInMetadataNames.Contains(metadataName);
        }

        /// <summary>
        /// Gets an item list from the project and assert that it contains
        /// exactly one item with the supplied name.
        /// </summary>
        public static ProjectItem AssertSingleItem(Project p, string type, string itemInclude)
        {
            ProjectItem[] items = p.GetItems(type).ToArray();
            int count = 0;
            foreach (ProjectItem item in items)
            {
                Assert.Equal(itemInclude.ToUpperInvariant(), item.EvaluatedInclude.ToUpperInvariant());
                ++count;
            }

            Assert.Equal(1, count);

            return items[0];
        }

        public static void AssertItemEvaluationFromProject([StringSyntax(StringSyntaxAttribute.Xml)] string projectContents, string[] inputFiles, string[] expectedInclude, Dictionary<string, string>[] expectedMetadataPerItem = null, bool normalizeSlashes = false, bool makeExpectedIncludeAbsolute = false)
        {
            AssertItemEvaluationFromGenericItemEvaluator((p, c) =>
                {
                    return new Project(p, new Dictionary<string, string>(), MSBuildConstants.CurrentToolsVersion, c)
                        .Items
                        .Select(i => (ITestItem)new ProjectItemTestItemAdapter(i))
                        .ToList();
                },
            projectContents,
            inputFiles,
            expectedInclude,
            makeExpectedIncludeAbsolute,
            expectedMetadataPerItem,
            normalizeSlashes);
        }

        public static void AssertItemEvaluationFromGenericItemEvaluator(Func<string, ProjectCollection, IList<ITestItem>> itemEvaluator, [StringSyntax(StringSyntaxAttribute.Xml)] string projectContents, string[] inputFiles, string[] expectedInclude, bool makeExpectedIncludeAbsolute = false, Dictionary<string, string>[] expectedMetadataPerItem = null, bool normalizeSlashes = false)
        {
            using (var env = TestEnvironment.Create())
            using (var collection = new ProjectCollection())
            {
                var testProject = env.CreateTestProjectWithFiles(projectContents, inputFiles);
                var evaluatedItems = itemEvaluator(testProject.ProjectFile, collection);

                if (makeExpectedIncludeAbsolute)
                {
                    expectedInclude = expectedInclude.Select(i => Path.Combine(testProject.TestRoot, i)).ToArray();
                }

                if (expectedMetadataPerItem == null)
                {
                    AssertItems(expectedInclude, evaluatedItems, expectedDirectMetadata: null, normalizeSlashes: normalizeSlashes);
                }
                else
                {
                    AssertItems(expectedInclude, evaluatedItems, expectedMetadataPerItem, normalizeSlashes);
                }
            }
        }

        public static void ShouldHaveSucceeded(this BuildResult result)
        {
            result.OverallResult.ShouldBe(
                BuildResultCode.Success,
                customMessage: result.Exception is not null ? result.Exception.ToString() : string.Empty);
        }

        public static void ShouldHaveSucceeded(this GraphBuildResult result)
        {
            result.OverallResult.ShouldBe(
                BuildResultCode.Success,
                customMessage: result.Exception is not null ? result.Exception.ToString() : string.Empty);
        }

        public static void ShouldHaveFailed(this BuildResult result, string exceptionMessageSubstring = null)
        {
            result.OverallResult.ShouldBe(BuildResultCode.Failure);

            if (exceptionMessageSubstring != null)
            {
                result.Exception.Message.ShouldContain(exceptionMessageSubstring);
            }
        }

        public static void ShouldHaveFailed(this GraphBuildResult result, string exceptionMessageSubstring = null)
        {
            result.OverallResult.ShouldBe(BuildResultCode.Failure);

            if (exceptionMessageSubstring != null)
            {
                result.Exception.Message.ShouldContain(exceptionMessageSubstring);
            }
        }

        public static string NormalizeSlashes(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        // todo Make IItem<M> public and add these new members to it.
        public interface ITestItem
        {
            string EvaluatedInclude { get; }
            int DirectMetadataCount { get; }
            string GetMetadataValue(string key);
        }

        public sealed class ProjectItemTestItemAdapter : ITestItem
        {
            private readonly ProjectItem _projectInstance;

            public ProjectItemTestItemAdapter(ProjectItem projectInstance)
            {
                _projectInstance = projectInstance;
            }

            public string EvaluatedInclude => _projectInstance.EvaluatedInclude;
            public int DirectMetadataCount => _projectInstance.DirectMetadataCount;
            public string GetMetadataValue(string key) => _projectInstance.GetMetadataValue(key);

            public static implicit operator ProjectItemTestItemAdapter(ProjectItem pi)
            {
                return new ProjectItemTestItemAdapter(pi);
            }
        }

        public sealed class ProjectItemInstanceTestItemAdapter : ITestItem
        {
            private readonly ProjectItemInstance _projectInstance;

            public ProjectItemInstanceTestItemAdapter(ProjectItemInstance projectInstance)
            {
                _projectInstance = projectInstance;
            }

            public string EvaluatedInclude => _projectInstance.EvaluatedInclude;
            public int DirectMetadataCount => _projectInstance.DirectMetadataCount;
            public string GetMetadataValue(string key) => _projectInstance.GetMetadataValue(key);

            public static implicit operator ProjectItemInstanceTestItemAdapter(ProjectItemInstance pi)
            {
                return new ProjectItemInstanceTestItemAdapter(pi);
            }
        }

        public static void AssertItems(string[] expectedItems, ICollection<ProjectItem> items, Dictionary<string, string> expectedDirectMetadata = null, bool normalizeSlashes = false)
        {
            var converteditems = items.Select(i => (ITestItem)new ProjectItemTestItemAdapter(i)).ToList();
            AssertItems(expectedItems, converteditems, expectedDirectMetadata, normalizeSlashes);
        }

        /// <summary>
        /// Asserts that the list of items has the specified evaluated includes.
        /// </summary>
        public static void AssertItems(string[] expectedItems, IList<ITestItem> items, Dictionary<string, string> expectedDirectMetadata = null, bool normalizeSlashes = false)
        {
            if (expectedDirectMetadata == null)
            {
                expectedDirectMetadata = new Dictionary<string, string>();
            }

            // all items have the same metadata
            var metadata = new Dictionary<string, string>[expectedItems.Length];

            for (var i = 0; i < metadata.Length; i++)
            {
                metadata[i] = expectedDirectMetadata;
            }

            AssertItems(expectedItems, items, metadata, normalizeSlashes);
        }

        public static void AssertItems(string[] expectedItems, IList<ProjectItem> items, Dictionary<string, string>[] expectedDirectMetadataPerItem, bool normalizeSlashes = false)
        {
            var convertedItems = items.Select(i => (ITestItem)new ProjectItemTestItemAdapter(i)).ToList();
            AssertItems(expectedItems, convertedItems, expectedDirectMetadataPerItem, normalizeSlashes);
        }

        public static void AssertItems(string[] expectedItems, IList<ITestItem> items, Dictionary<string, string>[] expectedDirectMetadataPerItem, bool normalizeSlashes = false)
        {
            if (items.Count != 0 || expectedDirectMetadataPerItem.Length != 0)
            {
                expectedItems.ShouldNotBeEmpty();
            }

            // iterate to the minimum length; if the lengths don't match but there's a prefix match the count assertion below will trigger
            int minimumLength = Math.Min(expectedItems.Length, items.Count);

            for (var i = 0; i < minimumLength; i++)
            {
                if (!normalizeSlashes)
                {
                    items[i].EvaluatedInclude.ShouldBe(expectedItems[i]);
                }
                else
                {
                    var normalizedItem = NormalizeSlashes(expectedItems[i]);
                    items[i].EvaluatedInclude.ShouldBe(normalizedItem);
                }

                AssertItemHasMetadata(expectedDirectMetadataPerItem[i], items[i]);
            }

            items.Count.ShouldBe(expectedItems.Length,
                customMessage: $"got items \"{string.Join(", ", items)}\", expected \"{string.Join(", ", expectedItems)}\"");

            expectedItems.Length.ShouldBe(expectedDirectMetadataPerItem.Length);
        }

        /// <summary>
        /// Amazingly sophisticated :) helper function to determine if the set of ITaskItems returned from
        /// a task match the expected set of ITaskItems.  It can also check that the ITaskItems have the expected
        /// metadata, and that the ITaskItems are returned in the correct order.
        ///
        /// The "expectedItemsString" is a formatted way of easily specifying which items you expect to see.
        /// The format is:
        ///
        ///         itemspec1 :   metadataname1=metadatavalue1 ; metadataname2=metadatavalue2 ; ...
        ///         itemspec2 :   metadataname3=metadatavalue3 ; metadataname4=metadatavalue4 ; ...
        ///         itemspec3 :   metadataname5=metadatavalue5 ; metadataname6=metadatavalue6 ; ...
        ///
        /// (Each item needs to be on its own line.)
        ///
        /// </summary>
        /// <param name="expectedItemsString"></param>
        /// <param name="actualItems"></param>
        public static void AssertItemsMatch(string expectedItemsString, ITaskItem[] actualItems)
        {
            AssertItemsMatch(expectedItemsString, actualItems, true);
        }

        /// <summary>
        /// Amazingly sophisticated :) helper function to determine if the set of ITaskItems returned from
        /// a task match the expected set of ITaskItems.  It can also check that the ITaskItems have the expected
        /// metadata, and that the ITaskItems are returned in the correct order.
        ///
        /// The "expectedItemsString" is a formatted way of easily specifying which items you expect to see.
        /// The format is:
        ///
        ///         itemspec1 :   metadataname1=metadatavalue1 ; metadataname2=metadatavalue2 ; ...
        ///         itemspec2 :   metadataname3=metadatavalue3 ; metadataname4=metadatavalue4 ; ...
        ///         itemspec3 :   metadataname5=metadatavalue5 ; metadataname6=metadatavalue6 ; ...
        ///
        /// (Each item needs to be on its own line.)
        ///
        /// </summary>
        /// <param name="expectedItemsString"></param>
        /// <param name="actualItems"></param>
        /// <param name="orderOfItemsShouldMatch"></param>
        public static void AssertItemsMatch(string expectedItemsString, ITaskItem[] actualItems, bool orderOfItemsShouldMatch)
        {
            List<ITaskItem> expectedItems = ParseExpectedItemsString(expectedItemsString);

            // Form a string of expected item specs.  For logging purposes only.
            StringBuilder expectedItemSpecs = new StringBuilder();
            foreach (ITaskItem expectedItem in expectedItems)
            {
                if (expectedItemSpecs.Length > 0)
                {
                    expectedItemSpecs.Append("; ");
                }

                expectedItemSpecs.Append(expectedItem.ItemSpec);
            }

            // Form a string of expected item specs.  For logging purposes only.
            StringBuilder actualItemSpecs = new StringBuilder();
            foreach (ITaskItem actualItem in actualItems)
            {
                if (actualItemSpecs.Length > 0)
                {
                    actualItemSpecs.Append("; ");
                }

                actualItemSpecs.Append(actualItem.ItemSpec);
            }

            bool outOfOrder = false;

            // Loop through all the actual items.
            for (int actualItemIndex = 0; actualItemIndex < actualItems.Length; actualItemIndex++)
            {
                ITaskItem actualItem = actualItems[actualItemIndex];

                // Loop through all the expected items to find one with the same item spec.
                ITaskItem expectedItem = null;
                int expectedItemIndex;
                for (expectedItemIndex = 0; expectedItemIndex < expectedItems.Count; expectedItemIndex++)
                {
                    if (expectedItems[expectedItemIndex].ItemSpec == actualItem.ItemSpec)
                    {
                        expectedItem = expectedItems[expectedItemIndex];

                        // If the items are expected to be in the same order, then the expected item
                        // should always be found at index zero, because we remove items from the expected
                        // list as we find them.
                        if ((expectedItemIndex != 0) && orderOfItemsShouldMatch)
                        {
                            outOfOrder = true;
                        }

                        break;
                    }
                }

                Assert.NotNull(expectedItem); // String.Format("Item '{0}' was returned but not expected.", actualItem.ItemSpec));

                // Make sure all the metadata on the expected item matches the metadata on the actual item.
                // Don't check built-in metadata ... only check custom metadata.
                foreach (string metadataName in expectedItem.MetadataNames)
                {
                    // This check filters out any built-in item metadata, like "RelativeDir", etc.
                    if (!IsBuiltInItemMetadataName(metadataName))
                    {
                        string expectedMetadataValue = expectedItem.GetMetadata(metadataName);
                        string actualMetadataValue = actualItem.GetMetadata(metadataName);

                        Assert.True(
                                actualMetadataValue.Length > 0 || expectedMetadataValue.Length == 0,
                                string.Format("Item '{0}' does not have expected metadata '{1}'.", actualItem.ItemSpec, metadataName));

                        Assert.True(
                                actualMetadataValue.Length == 0 || expectedMetadataValue.Length > 0,
                                string.Format("Item '{0}' has unexpected metadata {1}={2}.", actualItem.ItemSpec, metadataName, actualMetadataValue));

                        Assert.Equal(expectedMetadataValue, actualMetadataValue);

                        // string.Format
                        //    (
                        //        "Item '{0}' has metadata {1}={2} instead of expected {1}={3}.",
                        //        actualItem.ItemSpec,
                        //        metadataName,
                        //        actualMetadataValue,
                        //        expectedMetadataValue
                        //    )
                    }
                }
                expectedItems.RemoveAt(expectedItemIndex);
            }

            // Log an error for any leftover items in the expectedItems collection.
            foreach (ITaskItem expectedItem in expectedItems)
            {
                Assert.Fail(string.Format("Item '{0}' was expected but not returned.", expectedItem.ItemSpec));
            }

            if (outOfOrder)
            {
                Console.WriteLine("ERROR:  Items were returned in the incorrect order...");
                Console.WriteLine("Expected:  " + expectedItemSpecs);
                Console.WriteLine("Actual:    " + actualItemSpecs);
                Assert.Fail("Items were returned in the incorrect order.  See 'Standard Out' tab for more details.");
            }
        }

        public static void AssertItemHasMetadata(Dictionary<string, string> expected, ProjectItem item)
        {
            AssertItemHasMetadata(expected, new ProjectItemTestItemAdapter(item));
        }

        public static void AssertItemHasMetadata(string key, string value, ProjectItem item)
        {
            item.DirectMetadataCount.ShouldBe(1, customMessage: $"Expected 1 metadata, ({key}), got {item.DirectMetadataCount}");
            item.GetMetadataValue(key).ShouldBe(value);
        }

        public static void AssertItemHasMetadata(Dictionary<string, string> expected, ITestItem item)
        {
            expected ??= new Dictionary<string, string>();

            item.DirectMetadataCount.ShouldBe(expected.Keys.Count, customMessage: $"Expected {expected.Keys.Count} metadata, ({string.Join(", ", expected.Keys)}), got {item.DirectMetadataCount}");

            foreach (var key in expected.Keys)
            {
                item.GetMetadataValue(key).ShouldBe(expected[key]);
            }
        }

        /// <summary>
        /// Used to compare the contents of two arrays.
        /// </summary>
        public static void AssertArrayContentsMatch(object[] expected, object[] actual)
        {
            if (expected == null)
            {
                Assert.Null(actual); // "Expected a null array"
                return;
            }

            Assert.NotNull(actual); // "Result should be non-null."
            Assert.Equal(expected.Length, actual.Length); // "Expected array length of <" + expected.Length + "> but was <" + actual.Length + ">.");

            // Now that we've verified they're both non-null and of the same length, compare each item in the array.
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]); // "At index " + i + " expected " + expected[i].ToString() + " but was " + actual.ToString());
            }
        }

        /// <summary>
        /// Parses the string passed into AssertItemsMatch and returns a list of ITaskItems.
        /// </summary>
        /// <param name="expectedItemsString"></param>
        /// <returns></returns>
        private static List<ITaskItem> ParseExpectedItemsString(string expectedItemsString)
        {
            List<ITaskItem> expectedItems = new List<ITaskItem>();

            // First, parse this massive string that we've been given, and create an ITaskItem[] out of it,
            // so we can more easily compare it against the actual items.
            string[] expectedItemsStringSplit = expectedItemsString.Split(MSBuildConstants.CrLf, StringSplitOptions.RemoveEmptyEntries);
            foreach (string singleExpectedItemString in expectedItemsStringSplit)
            {
                string singleExpectedItemStringTrimmed = singleExpectedItemString.Trim();
                if (singleExpectedItemStringTrimmed.Length > 0)
                {
                    int indexOfColon = singleExpectedItemStringTrimmed.IndexOf(": ");
                    if (indexOfColon == -1)
                    {
                        expectedItems.Add(new Utilities.TaskItem(singleExpectedItemStringTrimmed));
                    }
                    else
                    {
                        // We found a colon, which means there's metadata in there.

                        // The item spec is the part before the colon.
                        string itemSpec = singleExpectedItemStringTrimmed.Substring(0, indexOfColon).Trim();

                        // The metadata is the part after the colon.
                        string itemMetadataString = singleExpectedItemStringTrimmed.Substring(indexOfColon + 1);

                        ITaskItem expectedItem = new Utilities.TaskItem(itemSpec);

                        string[] itemMetadataPieces = itemMetadataString.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string itemMetadataPiece in itemMetadataPieces)
                        {
                            string itemMetadataPieceTrimmed = itemMetadataPiece.Trim();
                            if (itemMetadataPieceTrimmed.Length > 0)
                            {
                                int indexOfEquals = itemMetadataPieceTrimmed.IndexOf('=');
                                Assert.NotEqual(-1, indexOfEquals);

                                string itemMetadataName = itemMetadataPieceTrimmed.Substring(0, indexOfEquals).Trim();
                                string itemMetadataValue = itemMetadataPieceTrimmed.Substring(indexOfEquals + 1).Trim();

                                expectedItem.SetMetadata(itemMetadataName, itemMetadataValue);
                            }
                        }

                        expectedItems.Add(expectedItem);
                    }
                }
            }

            return expectedItems;
        }

        /// <summary>
        /// Assert that a given file exists within the temp project directory.
        /// </summary>
        /// <param name="fileRelativePath"></param>
        public static void AssertFileExistsInTempProjectDirectory(string fileRelativePath)
        {
            AssertFileExistsInTempProjectDirectory(fileRelativePath, null);
        }

        /// <summary>
        /// Assert that a given file exists within the temp project directory.
        /// </summary>
        /// <param name="fileRelativePath"></param>
        /// <param name="message">Can be null.</param>
        public static void AssertFileExistsInTempProjectDirectory(string fileRelativePath, string message)
        {
            if (message == null)
            {
                message = fileRelativePath + " doesn't exist, but it should.";
            }

            Assert.True(FileSystems.Default.FileExists(Path.Combine(TempProjectDir, fileRelativePath)), message);
        }

        /// <summary>
        /// Does certain replacements in a string representing the project file contents.
        /// This makes it easier to write unit tests because the author doesn't have
        /// to worry about escaping double-quotes, etc.
        /// </summary>
        /// <param name="projectFileContents"></param>
        /// <returns></returns>
        public static string CleanupFileContents([StringSyntax(StringSyntaxAttribute.Xml)] string projectFileContents)
        {
            StringBuilder temp = new (projectFileContents);

            // Replace reverse-single-quotes with double-quotes.
            temp.Replace('`', '"');

            // Place the correct MSBuild namespace into the <Project> tag.
            temp.Replace("msbuildnamespace", msbuildNamespace);
            temp.Replace("msbuilddefaulttoolsversion", s_msbuildDefaultToolsVersion);
            temp.Replace("msbuildassemblyversion", s_msbuildAssemblyVersion);

            return temp.ToString();
        }

        public static string Cleanup([StringSyntax(StringSyntaxAttribute.Xml)] this string aString)
        {
            return CleanupFileContents(aString);
        }

        /// <summary>
        /// Normalizes all the whitespace in an xml string so that two documents that
        /// differ only in whitespace can be easily compared to each other for sameness.
        /// </summary>
        public static string NormalizeXmlWhitespace(string xml)
        {
            XmlDocument xmldoc = new XmlDocument();
            xmldoc.LoadXml(xml);

            // Normalize all the whitespace by writing the Xml document out to a
            // string, with PreserveWhitespace=false.
            xmldoc.PreserveWhitespace = false;

            StringBuilder sb = new StringBuilder(xml.Length);
            var writerSettings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Encoding = Encoding.UTF8,
                Indent = true
            };

            using (var writer = XmlWriter.Create(sb, writerSettings))
            {
                xmldoc.WriteTo(writer);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Create an MSBuild project file on disk and return the full path to it.
        /// </summary>
        /// <param name="xml"></param>
        /// <returns></returns>
        public static string CreateTempFileOnDisk(string fileContents, params object[] args)
        {
            return CreateTempFileOnDiskNoFormat(string.Format(fileContents, args));
        }

        /// <summary>
        /// Create an MSBuild project file on disk and return the full path to it.
        /// </summary>
        /// <param name="xml"></param>
        /// <returns></returns>
        public static string CreateTempFileOnDiskNoFormat(string fileContents)
        {
            string projectFilePath = FileUtilities.GetTemporaryFile();

            File.WriteAllText(projectFilePath, CleanupFileContents(fileContents));

            return projectFilePath;
        }

        public static ProjectRootElement CreateInMemoryProjectRootElement([StringSyntax(StringSyntaxAttribute.Xml)] string projectContents, ProjectCollection collection = null, bool preserveFormatting = true)
        {
            var cleanedProject = CleanupFileContents(projectContents);
#pragma warning disable CA2000 // The return object depends on the created XML reader and project collection that should not be disposed in this scope.
            return ProjectRootElement.Create(
                XmlReader.Create(new StringReader(cleanedProject)),
                collection ?? new ProjectCollection(),
                preserveFormatting);
#pragma warning restore CA2000 // The return object depends on the created XML reader and project collection that should not be disposed in this scope.
        }

        /// <summary>
        /// Create a project in memory. Load up the given XML.
        /// </summary>
        /// <param name="xml">the project to be created in string format.</param>
        /// <returns>Returns created <see cref="Project"/>.</returns>
        public static Project CreateInMemoryProject(string xml)
        {
            return CreateInMemoryProject(xml, new ConsoleLogger());
        }

        /// <summary>
        /// Create a project in memory. Load up the given XML.
        /// </summary>
        /// <param name="xml">the project to be created in string format.</param>
        /// <param name="loggers">The array of loggers to attach on project evaluation.</param>
        /// <returns>Returns created <see cref="Project"/>.</returns>
        public static Project CreateInMemoryProject(string xml, params ILogger[] loggers)
        {
#pragma warning disable CA2000 // The return object depends on the project collection that should not be disposed in this scope.
            return CreateInMemoryProject(new ProjectCollection(), xml, loggers);
#pragma warning restore CA2000 // The return object depends on the project collection that should not be disposed in this scope.
        }

        /// <summary>
        /// Create an in-memory project and attach it to the passed-in engine.
        /// </summary>
        /// <param name="projectCollection"><see cref="ProjectCollection"/> to use for project creation.</param>
        /// <param name="xml">the project to be created in string format.</param>
        /// <param name="loggers">The array of loggers to attach on project evaluation. May be null.</param>
        /// <returns>Returns created <see cref="Project"/>.</returns>
        public static Project CreateInMemoryProject(ProjectCollection projectCollection, string xml, params ILogger[] loggers)
        {
            return CreateInMemoryProject(projectCollection, xml, null, loggers);
        }

        /// <summary>
        /// Create an in-memory project and attach it to the passed-in engine.
        /// </summary>
        /// <param name="projectCollection"><see cref="ProjectCollection"/> to use for project creation.</param>
        /// <param name="xml">the project to be created in string format.</param>
        /// <param name="toolsVersion">The tools version to use on project creation. May be null.</param>
        /// <param name="loggers">The array of loggers to attach to project collection before evaluation. May be null.</param>
        /// <returns>Returns created <see cref="Project"/>.</returns>
        public static Project CreateInMemoryProject(
            ProjectCollection projectCollection,
            [StringSyntax(StringSyntaxAttribute.Xml)] string xml,
            string toolsVersion /* may be null */,
            params ILogger[] loggers)
        {
            XmlReaderSettings readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            if (loggers != null)
            {
                foreach (ILogger logger in loggers)
                {
                    projectCollection.RegisterLogger(logger);
                }
            }
#pragma warning disable CA2000 // The return object depends on the created XML reader that should not be disposed in this scope.
            Project project = new Project(
                XmlReader.Create(new StringReader(CleanupFileContents(xml)), readerSettings),
                globalProperties: null,
                toolsVersion,
                projectCollection);
#pragma warning restore CA2000 // The return object depends on the created XML reader that should not be disposed in this scope.

            Guid guid = Guid.NewGuid();
            project.FullPath = Path.Combine(TempProjectDir, "Temporary" + guid.ToString("N") + ".csproj");
            project.ReevaluateIfNecessary();

            return project;
        }

        /// <summary>
        /// Creates a project in memory and builds the default targets.  The build is
        /// expected to succeed.
        /// </summary>
        /// <param name="projectContents">The project file content in string format.</param>
        /// <param name="testOutputHelper"><see cref="ITestOutputHelper"/> to log to.</param>
        /// <param name="loggerVerbosity">The required logging verbosity.</param>
        /// <returns>The <see cref="MockLogger"/> that was used during evaluation and build.</returns>
        public static MockLogger BuildProjectExpectSuccess(
            [StringSyntax(StringSyntaxAttribute.Xml)] string projectContents,
            ITestOutputHelper testOutputHelper = null,
            LoggerVerbosity loggerVerbosity = LoggerVerbosity.Normal)
        {
            MockLogger logger = new MockLogger(testOutputHelper, verbosity: loggerVerbosity);
            BuildProjectExpectSuccess(projectContents, logger);
            return logger;
        }

        /// <summary>
        /// Creates a project in memory and builds the default targets.  The build is
        /// expected to succeed.
        /// </summary>
        /// <param name="projectContents">The project file content in string format.</param>
        /// <param name="loggers">The array of loggers to use.</param>
        public static void BuildProjectExpectSuccess(
            [StringSyntax(StringSyntaxAttribute.Xml)] string projectContents,
            params ILogger[] loggers)
        {
            using ProjectCollection collection = new();
            Project project = CreateInMemoryProject(collection, projectContents, loggers);
            project.Build().ShouldBeTrue();
        }

        /// <summary>
        /// Creates a project in memory and builds the default targets.  The build is
        /// expected to fail.
        /// </summary>
        /// <param name="projectContents">The project file content in string format.</param>
        /// <returns>The <see cref="MockLogger"/> that was used during evaluation and build.</returns>
        public static MockLogger BuildProjectExpectFailure([StringSyntax(StringSyntaxAttribute.Xml)] string projectContents)
        {
            MockLogger logger = new MockLogger();
            BuildProjectExpectFailure(projectContents, logger);
            return logger;
        }

        /// <summary>
        /// Creates a project in memory and builds the default targets.  The build is
        /// expected to fail.
        /// </summary>
        /// <param name="projectContents">The project file content in string format.</param>
        /// <param name="loggers">The array of loggers to use.</param>
        public static void BuildProjectExpectFailure(
            [StringSyntax(StringSyntaxAttribute.Xml)] string projectContents,
            params ILogger[] loggers)
        {
            using ProjectCollection collection = new();
            Project project = CreateInMemoryProject(collection, projectContents, loggers);
            project.Build().ShouldBeFalse("Build succeeded, but shouldn't have.  See test output (Attachments in Azure Pipelines) for details\"");
        }

        /// <summary>
        /// This helper method compares the final project contents with the expected
        /// value.
        /// </summary>
        /// <param name="project"></param>
        /// <param name="newExpectedProjectContents"></param>
        public static void CompareProjectContents(
            Project project,
            [StringSyntax(StringSyntaxAttribute.Xml)] string newExpectedProjectContents)
        {
            // Get the new XML for the project, normalizing the whitespace.
            string newActualProjectContents = project.Xml.RawXml;

            // Replace single-quotes with double-quotes, and normalize whitespace.
            newExpectedProjectContents = NormalizeXmlWhitespace(CleanupFileContents(newExpectedProjectContents));

            // Compare the actual XML with the expected XML.
            Console.WriteLine("================================= EXPECTED ===========================================");
            Console.WriteLine(newExpectedProjectContents);
            Console.WriteLine();
            Console.WriteLine("================================== ACTUAL ============================================");
            Console.WriteLine(newActualProjectContents);
            Console.WriteLine();
            Assert.Equal(newExpectedProjectContents, newActualProjectContents); // "Project XML does not match expected XML.  See 'Standard Out' tab for details."
        }

        private static string s_tempProjectDir;

        /// <summary>
        /// Creates and returns a unique path under temp
        /// </summary>
        public static string TempProjectDir
        {
            get
            {
                if (s_tempProjectDir == null)
                {
                    s_tempProjectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

                    Directory.CreateDirectory(s_tempProjectDir);
                }

                return s_tempProjectDir;
            }
        }

        /// <summary>
        /// Deletes the directory %TEMP%\TempDirForMSBuildUnitTests, and all its contents.
        /// </summary>
        public static void DeleteTempProjectDirectory()
        {
            DeleteDirectory(TempProjectDir);
        }

        /// <summary>
        /// Deletes the directory and all its contents.
        /// </summary>
        public static void DeleteDirectory(string dir)
        {
            // Manually deleting all children, but intentionally leaving the
            // Temp project directory behind due to locking issues which were causing
            // failures in main on Amd64-WOW runs.

            // retries to deal with occasional locking issues where the file / directory can't be deleted to initially
            for (int retries = 0; retries < 5; retries++)
            {
                try
                {
                    if (FileSystems.Default.DirectoryExists(dir))
                    {
                        foreach (string directory in Directory.GetDirectories(dir))
                        {
                            Directory.Delete(directory, true);
                        }

                        foreach (string file in Directory.GetFiles(dir))
                        {
                            File.Delete(file);
                        }
                    }

                    break;
                }
                // After all the retries fail, we fail with the actual problem instead of some difficult-to-understand issue later.
                catch (Exception ex) when (retries < 4)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }

        /// <summary>
        /// Creates a file in the %TEMP%\TempDirForMSBuildUnitTests directory, after cleaning
        /// up the file contents (replacing single-back-quote with double-quote, etc.).
        /// Silently OVERWRITES existing file.
        /// </summary>
        public static string CreateFileInTempProjectDirectory(string fileRelativePath, [StringSyntax(StringSyntaxAttribute.Xml)] string fileContents, Encoding encoding = null)
        {
            Assert.False(string.IsNullOrEmpty(fileRelativePath));
            string fullFilePath = Path.Combine(TempProjectDir, fileRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath));

            // retries to deal with occasional locking issues where the file can't be written to initially
            for (int retries = 0; retries < 5; retries++)
            {
                try
                {
                    if (encoding == null)
                    {
                        // This method uses UTF-8 encoding without a Byte-Order Mark (BOM)
                        // https://msdn.microsoft.com/en-us/library/ms143375(v=vs.110).aspx#Remarks
                        File.WriteAllText(fullFilePath, CleanupFileContents(fileContents));
                    }
                    else
                    {
                        // If it is necessary to include a UTF-8 identifier, such as a byte order mark, at the beginning of a file,
                        // use the WriteAllText(String,?String,?Encoding) method overload with UTF8 encoding.
                        File.WriteAllText(fullFilePath, CleanupFileContents(fileContents), encoding);
                    }
                    break;
                }
                // After all the retries fail, we fail with the actual problem instead of some difficult-to-understand issue later.
                catch (Exception ex) when (retries < 4)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            return fullFilePath;
        }

        /// <summary>
        /// Builds a project file from disk, and asserts if the build does not succeed.
        /// </summary>
        /// <param name="projectFileRelativePath"></param>
        /// <returns></returns>
        public static void BuildTempProjectFileExpectSuccess(string projectFileRelativePath, MockLogger logger)
        {
            BuildTempProjectFileWithTargetsExpectSuccess(projectFileRelativePath, null, null, logger);
        }

        /// <summary>
        /// Builds a project file from disk, and asserts if the build does not succeed.
        /// </summary>
        public static void BuildTempProjectFileWithTargetsExpectSuccess(string projectFileRelativePath, string[] targets, IDictionary<string, string> additionalProperties, MockLogger logger)
        {
            BuildTempProjectFileWithTargets(projectFileRelativePath, targets, additionalProperties, logger)
                .ShouldBeTrue("Build failed.  See test output (Attachments in Azure Pipelines) for details");
        }

        /// <summary>
        /// Builds a project file from disk, and asserts if the build succeeds.
        /// </summary>
        public static void BuildTempProjectFileExpectFailure(string projectFileRelativePath, MockLogger logger)
        {
            BuildTempProjectFileWithTargets(projectFileRelativePath, null, null, logger)
                .ShouldBeFalse("Build unexpectedly succeeded.  See test output (Attachments in Azure Pipelines) for details");
        }

        /// <summary>
        /// Loads a project file from disk
        /// </summary>
        /// <param name="fileRelativePath"></param>
        /// <returns></returns>
        public static Project LoadProjectFileInTempProjectDirectory(string projectFileRelativePath)
        {
            return LoadProjectFileInTempProjectDirectory(projectFileRelativePath, false /* don't touch project*/);
        }

        /// <summary>
        /// Loads a project file from disk
        /// </summary>
        /// <param name="fileRelativePath"></param>
        /// <returns></returns>
        public static Project LoadProjectFileInTempProjectDirectory(string projectFileRelativePath, bool touchProject)
        {
            string projectFileFullPath = Path.Combine(TempProjectDir, projectFileRelativePath);
#pragma warning disable CA2000 // The return object depends on the project collection that should not be disposed in this scope.
            ProjectCollection projectCollection = new ProjectCollection();
#pragma warning restore CA2000 // The return object depends on the project collection that should not be disposed in this scope.

            Project project = new Project(projectFileFullPath, null, null, projectCollection);

            if (touchProject)
            {
                File.SetLastWriteTime(projectFileFullPath, DateTime.Now);
            }

            return project;
        }

        /// <summary>
        /// Builds a project file from disk, and asserts if the build does not succeed.
        /// </summary>
        /// <param name="projectFileRelativePath"></param>
        /// <param name="targets"></param>
        /// <param name="additionalProperties">Can be null.</param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static bool BuildTempProjectFileWithTargets(
            string projectFileRelativePath,
            string[] targets,
            IDictionary<string, string> globalProperties,
            ILogger logger)
        {
            // Build the default targets.
            List<ILogger> loggers = new List<ILogger>(1);
            loggers.Add(logger);

            if (string.Equals(Path.GetExtension(projectFileRelativePath), ".sln"))
            {
                string projectFileFullPath = Path.Combine(TempProjectDir, projectFileRelativePath);
                BuildRequestData data = new BuildRequestData(projectFileFullPath, globalProperties ?? new Dictionary<string, string>(), null, targets, null);
                BuildParameters parameters = new BuildParameters();
                parameters.Loggers = loggers;
                BuildResult result = BuildManager.DefaultBuildManager.Build(parameters, data);
                return result.OverallResult == BuildResultCode.Success;
            }
            else
            {
                Project project = LoadProjectFileInTempProjectDirectory(projectFileRelativePath);

                if (globalProperties != null)
                {
                    // add extra properties
                    foreach (KeyValuePair<string, string> globalProperty in globalProperties)
                    {
                        project.SetGlobalProperty(globalProperty.Key, globalProperty.Value);
                    }
                }

                return project.Build(targets, loggers);
            }
        }

        /// <summary>
        /// Delete any files in the list that currently exist.
        /// </summary>
        /// <param name="files"></param>
        public static void DeleteTempFiles(string[] files)
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (FileSystems.Default.FileExists(files[i]))
                {
                    File.Delete(files[i]);
                }
            }
        }

        /// <summary>
        /// Returns the requested number of temporary files.
        /// </summary>
        public static string[] GetTempFiles(int number)
        {
            return GetTempFiles(number, DateTime.Now);
        }

        /// <summary>
        /// Returns the requested number of temporary files, with the requested write time.
        /// </summary>
        public static string[] GetTempFiles(int number, DateTime lastWriteTime)
        {
            string[] files = new string[number];

            for (int i = 0; i < number; i++)
            {
                files[i] = FileUtilities.GetTemporaryFile();
                File.SetLastWriteTime(files[i], lastWriteTime);
            }
            return files;
        }

        /// <summary>
        /// Get items of item type "i" with using the item xml fragment passed in
        /// </summary>
        public static IList<ProjectItem> GetItemsFromFragment([StringSyntax(StringSyntaxAttribute.Xml)] string fragment, bool allItems = false, bool ignoreCondition = false)
        {
            string content = FormatProjectContentsWithItemGroupFragment(fragment);

            IList<ProjectItem> items = GetItems(content, allItems, ignoreCondition);
            return items;
        }

        public static string GetConcatenatedItemsOfType(this Project project, string itemType, string itemSeparator = ";")
        {
            return string.Join(itemSeparator, project.Items.Where(i => i.ItemType.Equals(itemType)).Select(i => i.EvaluatedInclude));
        }

        /// <summary>
        /// Get the items of type "i" in the project provided
        /// </summary>
        public static IList<ProjectItem> GetItems([StringSyntax(StringSyntaxAttribute.Xml)] string content, bool allItems = false, bool ignoreCondition = false)
        {
            using ProjectRootElementFromString projectRootElementFromString = new(CleanupFileContents(content));
            ProjectRootElement projectXml = projectRootElementFromString.Project;
            Project project = new Project(projectXml);
            IList<ProjectItem> item = Helpers.MakeList(
                ignoreCondition ?
                (allItems ? project.ItemsIgnoringCondition : project.GetItemsIgnoringCondition("i")) :
                (allItems ? project.Items : project.GetItems("i")));

            return item;
        }

        public static string FormatProjectContentsWithItemGroupFragment([StringSyntax(StringSyntaxAttribute.Xml)] string fragment)
        {
            return
                $@"
                    <Project>
                        <ItemGroup>
                            {fragment}
                        </ItemGroup>
                    </Project>
                ";
        }
    }

    /// <summary>
    /// Various generic unit test helper methods
    /// </summary>
    public static partial class Helpers
    {
        public static string Format(this string s, params object[] formatItems)
        {
            ErrorUtilities.VerifyThrowArgumentNull(s);

            return string.Format(s, formatItems);
        }

        public static string GetOSPlatformAsString()
        {
            var currentPlatformString = string.Empty;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                currentPlatformString = "WINDOWS";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                currentPlatformString = "LINUX";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                currentPlatformString = "OSX";
            }
            else
            {
                Assert.Fail("unrecognized current platform");
            }

            return currentPlatformString;
        }

        /// <summary>
        /// Returns the count of objects returned by an enumerator
        /// </summary>
        public static int Count(IEnumerable enumerable)
        {
            if (enumerable is ICollection c)
            {
                return c.Count;
            }

            int i = 0;
            foreach (object _ in enumerable)
            {
                i++;
            }

            return i;
        }

        /// <summary>
        /// Makes a temporary list out of an enumerable
        /// </summary>
        public static List<T> MakeList<T>(IEnumerable<T> enumerable)
        {
            List<T> list = new List<T>();
            foreach (T item in enumerable)
            {
                list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// Gets the first element in the enumeration, or null if there are none
        /// </summary>
        public static T GetFirst<T>(IEnumerable<T> enumerable)
            where T : class
        {
            T first = null;

            foreach (T element in enumerable)
            {
                first = element;
                break;
            }

            return first;
        }

        /// <summary>
        /// Gets the last element in the enumeration, or null if there are none
        /// </summary>
        public static T GetLast<T>(IEnumerable<T> enumerable)
            where T : class
        {
            T last = null;

            foreach (T item in enumerable)
            {
                last = item;
            }

            return last;
        }

        /// <summary>
        /// Makes a temporary dictionary out of an enumerable of keyvaluepairs.
        /// </summary>
        public static Dictionary<string, V> MakeDictionary<V>(IEnumerable<KeyValuePair<string, V>> enumerable)
        {
            Dictionary<string, V> dictionary = new Dictionary<string, V>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, V> item in enumerable)
            {
                dictionary.Add(item.Key, item.Value);
            }
            return dictionary;
        }

        /// <summary>
        /// Verify that the two lists are value identical
        /// </summary>
        public static void AssertListsValueEqual<T>(IList<T> one, IList<T> two)
        {
            Assert.Equal(one.Count, two.Count);

            for (int i = 0; i < one.Count; i++)
            {
                Assert.Equal(one[i], two[i]);
            }
        }

        /// <summary>
        /// Verify that the two collections are value identical
        /// </summary>
        public static void AssertCollectionsValueEqual<T>(ICollection<T> one, ICollection<T> two)
        {
            Assert.Equal(one.Count, two.Count);

            foreach (T item in one)
            {
                Assert.True(two.Contains(item));
            }

            foreach (T item in two)
            {
                Assert.True(one.Contains(item));
            }
        }

        public static void AssertDictionariesEqual<K, V>(IDictionary<K, V> x, IDictionary<K, V> y, Action<KeyValuePair<K, V>, KeyValuePair<K, V>> assertPairsEqual)
        {
            if (x == null || y == null)
            {
                Assert.True(x == null && y == null);
                return;
            }

            Assert.Equal(x.Count, y.Count);

            for (var i = 0; i < x.Count; i++)
            {
                var xPair = x.ElementAt(i);
                var yPair = y.ElementAt(i);

                assertPairsEqual(xPair, yPair);
            }
        }

        public static void AssertDictionariesEqual(IDictionary<string, string> x, IDictionary<string, string> y)
        {
            AssertDictionariesEqual(x, y,
                (xPair, yPair) =>
                {
                    Assert.Equal(xPair.Key, yPair.Key);
                    Assert.Equal(xPair.Value, yPair.Value);
                });
        }

        public static void ShouldBeSameIgnoringOrder<K, V>(this IDictionary<K, V> a, IReadOnlyDictionary<K, V> b)
        {
            a.ShouldBeSubsetOf(b);
            b.ShouldBeSubsetOf(a);
            a.Count.ShouldBe(b.Count);
        }

        public static void ShouldBeSameIgnoringOrder<K>(this IEnumerable<K> a, IEnumerable<K> b)
        {
            a.ShouldBeSubsetOf(b);
            b.ShouldBeSubsetOf(a);
            a.Count().ShouldBe(b.Count());
        }

        public static void ShouldBeSetEquivalentTo<K>(this IEnumerable<K> a, IEnumerable<K> b)
        {
            a.ShouldBeSubsetOf(b);
            b.ShouldBeSubsetOf(a);
        }

        /// <summary>
        /// Verify that the two enumerables are value identical
        /// </summary>
        public static void AssertEnumerationsValueEqual<T>(IEnumerable<T> one, IEnumerable<T> two)
        {
            List<T> listOne = new List<T>();
            List<T> listTwo = new List<T>();

            foreach (T item in one)
            {
                listOne.Add(item);
            }

            foreach (T item in two)
            {
                listTwo.Add(item);
            }

            AssertCollectionsValueEqual(listOne, listTwo);
        }

        /// <summary>
        /// Build a project with the provided content in memory.
        /// Assert that it succeeded, and return the mock logger with the output.
        /// </summary>
        public static MockLogger BuildProjectWithNewOMExpectSuccess(string content, Dictionary<string, string> globalProperties = null, MockLogger logger = null)
        {
            BuildProjectWithNewOM(content, ref logger, out bool result, false, globalProperties);
            Assert.True(result);

            return logger;
        }

        /// <summary>
        /// Build a project in memory using the new OM
        /// </summary>
        private static void BuildProjectWithNewOM([StringSyntax(StringSyntaxAttribute.Xml)] string content, ref MockLogger logger, out bool result, bool allowTaskCrash, Dictionary<string, string> globalProperties = null)
        {
            // Replace the nonstandard quotes with real ones
            content = ObjectModelHelpers.CleanupFileContents(content);
            using ProjectFromString projectFromString = new(content, globalProperties, toolsVersion: null);
            Project project = projectFromString.Project;
            logger ??= new MockLogger
            {
                AllowTaskCrashes = allowTaskCrash
            };
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);
            result = project.Build(loggers);
        }

        public static void BuildProjectWithNewOMAndBinaryLogger([StringSyntax(StringSyntaxAttribute.Xml)] string content, BinaryLogger binaryLogger, out bool result, out string projectDirectory)
        {
            // Replace the nonstandard quotes with real ones
            content = ObjectModelHelpers.CleanupFileContents(content);

            using ProjectFromString projectFromString = new(content, null, toolsVersion: null);
            Project project = projectFromString.Project;

            List<ILogger> loggers = new List<ILogger>() { binaryLogger };

            result = project.Build(loggers);

            projectDirectory = project.DirectoryPath;
        }

        public static MockLogger BuildProjectContentUsingBuildManagerExpectResult([StringSyntax(StringSyntaxAttribute.Xml)] string content, BuildResultCode expectedResult)
        {
            var logger = new MockLogger();

            var result = BuildProjectContentUsingBuildManager(content, logger);

            result.OverallResult.ShouldBe(expectedResult);

            return logger;
        }

        public static BuildResult BuildProjectContentUsingBuildManager([StringSyntax(StringSyntaxAttribute.Xml)] string content, ILogger logger, BuildParameters parameters = null)
        {
            // Replace the nonstandard quotes with real ones
            content = ObjectModelHelpers.CleanupFileContents(content);

            using (var env = TestEnvironment.Create())
            {
                var testProject = env.CreateTestProjectWithFiles(content.Cleanup());

                return BuildProjectFileUsingBuildManager(testProject.ProjectFile, logger, parameters);
            }
        }

        public static BuildResult BuildProjectFileUsingBuildManager(
            string projectFile,
            ILogger logger = null,
            BuildParameters parameters = null,
            IList<string> targetsToBuild = null)
        {
            using (var buildManager = new BuildManager())
            {
                parameters ??= new BuildParameters();

                if (logger != null)
                {
                    parameters.Loggers = parameters.Loggers == null
                        ? new[] { logger }
                        : parameters.Loggers.Concat(new[] { logger });
                }

                var request = new BuildRequestData(
                    projectFile,
                    new Dictionary<string, string>(),
                    MSBuildConstants.CurrentToolsVersion,
                    targetsToBuild?.ToArray() ?? Array.Empty<string>(),
                    null);

                var result = buildManager.Build(
                    parameters,
                    request);

                return result;
            }
        }

        public enum ExpectedBuildResult
        {
            // The build should fail with a logged error upon drive enumerationg wildcard detection and setting of environment variable.
            FailWithError,
            // The build should succeed with a logged warning upon drive enumerating wildcard detection (regardless of environment variable value).
            SucceedWithWarning,
            // The build should succeed with no logged warnings and errors, as there are no drive enumerating wildcards.
            SucceedWithNoErrorsAndWarnings
        }

        /// <summary>
        /// Verify that a drive enumerating wildcard warning is logged or exception is thrown.
        /// </summary>
        public static void CleanContentsAndBuildTargetWithDriveEnumeratingWildcard([StringSyntax(StringSyntaxAttribute.Xml)] string content, string failOnDriveEnumerationEnvVar, string targetName, ExpectedBuildResult expectedBuildResult, ITestOutputHelper testOutput = null)
        {
            using (var env = TestEnvironment.Create(testOutput))
            {
                // Clean file contents by replacing single quotes with double quotes, etc.
                content = ObjectModelHelpers.CleanupFileContents(content);
                var testProject = env.CreateTestProjectWithFiles(content.Cleanup());

                // Reset state
                ResetStateForDriveEnumeratingWildcardTests(env, failOnDriveEnumerationEnvVar);

                // Setup and build test target
                BuildTargetWithDriveEnumeratingWildcardUsingBuildManager(env, testProject.ProjectFile, targetName, expectedBuildResult, testOutput);
            }
        }

        public static void ResetStateForDriveEnumeratingWildcardTests(TestEnvironment env, string setEnvVar)
        {
            ChangeWaves.ResetStateForTests();
            env.SetEnvironmentVariable("MSBUILDFAILONDRIVEENUMERATINGWILDCARD", setEnvVar);
            BuildEnvironmentHelper.ResetInstance_ForUnitTestsOnly();
        }

        public static void BuildTargetWithDriveEnumeratingWildcardUsingBuildManager(TestEnvironment env, string testProjectFile, string targetName, ExpectedBuildResult expectedBuildResult, ITestOutputHelper testOutput = null)
        {
            try
            {
                // Setup build
                MockLogger mockLogger = (testOutput == null) ? new MockLogger() : new MockLogger(testOutput);
                var p = ProjectInstance.FromFile(testProjectFile, new ProjectOptions());
                BuildManager buildManager = BuildManager.DefaultBuildManager;
                BuildRequestData data = new BuildRequestData(p, new[] { targetName });
                BuildParameters parameters = new BuildParameters()
                {
                    Loggers = new ILogger[] { mockLogger },
                };

                // Perform build using build manager
                BuildResult buildResult = buildManager.Build(parameters, data);

                // Verify result based on value of ExpectedBuildResult
                if (expectedBuildResult == ExpectedBuildResult.FailWithError)
                {
                    VerifyErrorLoggedForDriveEnumeratingWildcard(buildResult, mockLogger, targetName, testProjectFile);
                }
                else if (expectedBuildResult == ExpectedBuildResult.SucceedWithWarning)
                {
                    VerifyWarningLoggedForDriveEnumeratingWildcard(buildResult, mockLogger, targetName, testProjectFile);
                }
                else if (expectedBuildResult == ExpectedBuildResult.SucceedWithNoErrorsAndWarnings)
                {
                    VerifyNoErrorsAndWarningsForDriveEnumeratingWildcard(buildResult, mockLogger, targetName);
                }
            }
            finally
            {
                ChangeWaves.ResetStateForTests();
            }
        }

        private static void VerifyErrorLoggedForDriveEnumeratingWildcard(BuildResult buildResult, MockLogger mockLogger, string targetName, string testProjectFile)
        {
            buildResult.OverallResult.ShouldBe(BuildResultCode.Failure);
            buildResult[targetName].ResultCode.ShouldBe(TargetResultCode.Failure);
            mockLogger.ErrorCount.ShouldBe(1);
            mockLogger.Errors[0].Code.ShouldBe("MSB5029");
            mockLogger.Errors[0].Message.ShouldContain(testProjectFile);
        }

        private static void VerifyWarningLoggedForDriveEnumeratingWildcard(BuildResult buildResult, MockLogger mockLogger, string targetName, string testProjectFile)
        {
            VerifySuccessOfBuildAndTargetResults(buildResult, targetName);
            mockLogger.WarningCount.ShouldBe(1);
            mockLogger.Warnings[0].Code.ShouldBe("MSB5029");
            mockLogger.Warnings[0].Message.ShouldContain(testProjectFile);
        }

        private static void VerifyNoErrorsAndWarningsForDriveEnumeratingWildcard(BuildResult buildResult, MockLogger mockLogger, string targetName)
        {
            VerifySuccessOfBuildAndTargetResults(buildResult, targetName);
            mockLogger.WarningCount.ShouldBe(0);
            mockLogger.ErrorCount.ShouldBe(0);
        }

        private static void VerifySuccessOfBuildAndTargetResults(BuildResult buildResult, string targetName)
        {
            buildResult.OverallResult.ShouldBe(BuildResultCode.Success);
            buildResult[targetName].ResultCode.ShouldBe(TargetResultCode.Success);
        }

        /// <summary>
        /// Build a project with the provided content in memory.
        /// Assert that it fails, and return the mock logger with the output.
        /// </summary>
        public static MockLogger BuildProjectWithNewOMExpectFailure(string content, bool allowTaskCrash, MockLogger logger = null)
        {
            bool result;
            BuildProjectWithNewOM(content, ref logger, out result, allowTaskCrash);
            Assert.False(result);
            return logger;
        }

        /// <summary>
        /// Compare the expected project XML to actual project XML, after doing a little normalization
        /// of quotations/whitespace.
        /// </summary>
        /// <param name="newExpectedProjectContents"></param>
        /// <param name="newActualProjectContents"></param>
        public static void CompareProjectXml(
            [StringSyntax(StringSyntaxAttribute.Xml)] string newExpectedProjectContents,
            [StringSyntax(StringSyntaxAttribute.Xml)] string newActualProjectContents)
        {
            // Replace single-quotes with double-quotes, and normalize whitespace.
            newExpectedProjectContents =
                ObjectModelHelpers.NormalizeXmlWhitespace(
                    ObjectModelHelpers.CleanupFileContents(newExpectedProjectContents));

            // Compare the actual XML with the expected XML.
            if (newExpectedProjectContents != newActualProjectContents)
            {
                Console.WriteLine("================================= EXPECTED ===========================================");
                Console.WriteLine(newExpectedProjectContents);
                Console.WriteLine();
                Console.WriteLine("================================== ACTUAL ============================================");
                Console.WriteLine(newActualProjectContents);
                Console.WriteLine();
                Assert.Equal(newExpectedProjectContents, newActualProjectContents); // "Project XML does not match expected XML.  See 'Standard Out' tab for details."
            }
        }

        /// <summary>
        /// Verify that the saved project content matches the provided content
        /// </summary>
        public static void VerifyAssertProjectContent([StringSyntax(StringSyntaxAttribute.Xml)] string expected, Project project)
        {
            VerifyAssertProjectContent(expected, project.Xml);
        }

        /// <summary>
        /// Verify that the saved project content matches the provided content
        /// </summary>
        public static void VerifyAssertProjectContent([StringSyntax(StringSyntaxAttribute.Xml)] string expected, ProjectRootElement project, bool ignoreFirstLineOfActual = true)
        {
            VerifyAssertLineByLine(expected, project.RawXml, ignoreFirstLineOfActual);
        }

        /// <summary>
        /// Verify that the expected content matches the actual content
        /// </summary>
        public static void VerifyAssertLineByLine(string expected, string actual)
        {
            VerifyAssertLineByLine(expected, actual, false /* do not ignore first line */);
        }

        /// <summary>
        /// Write the given <see cref="projectContents"/> in a new temp directory and create the given <see cref="files"/> relative to the project
        /// </summary>
        /// <returns>the path to the temp root directory that contains the project and files</returns>
        public static string CreateProjectInTempDirectoryWithFiles([StringSyntax(StringSyntaxAttribute.Xml)] string projectContents, string[] files, out string createdProjectFile, out string[] createdFiles, string relativePathFromRootToProject = ".")
        {
            var root = GetTempDirectoryWithGuid();
            Directory.CreateDirectory(root);

            var projectDir = Path.Combine(root, relativePathFromRootToProject);
            Directory.CreateDirectory(projectDir);

            createdProjectFile = Path.Combine(projectDir, "build.proj");
            File.WriteAllText(createdProjectFile, ObjectModelHelpers.CleanupFileContents(projectContents));

            createdFiles = CreateFilesInDirectory(root, files);

            return root;
        }

        private static string GetTempDirectoryWithGuid()
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Creates a bunch of temporary files with the specified names and returns
        /// their full paths (so they can ultimately be cleaned up)
        /// </summary>
        public static string[] CreateFiles(params string[] files)
        {
            string directory = GetTempDirectoryWithGuid();
            Directory.CreateDirectory(directory);

            return CreateFilesInDirectory(directory, files);
        }

        /// <summary>
        /// Creates a bunch of temporary files in the given directory with the specified names and returns
        /// their full paths (so they can ultimately be cleaned up)
        /// </summary>
        public static string[] CreateFilesInDirectory(string rootDirectory, params string[] files)
        {
            if (files == null)
            {
                return null;
            }

            Assert.True(FileSystems.Default.DirectoryExists(rootDirectory), $"Directory {rootDirectory} does not exist");

            var result = new string[files.Length];

            for (var i = 0; i < files.Length; i++)
            {
                // On Unix there is the risk of creating one file with '\' in its name instead of directories.
                // Therefore split the arguments into path fragments and recompose the path.
                var fileFragments = SplitPathIntoFragments(files[i]);
                var rootDirectoryFragments = SplitPathIntoFragments(rootDirectory);
                var pathFragments = rootDirectoryFragments.Concat(fileFragments);

                var fullPath = Path.Combine(pathFragments.ToArray());

                var directoryName = Path.GetDirectoryName(fullPath);

                Directory.CreateDirectory(directoryName);
                Assert.True(FileSystems.Default.DirectoryExists(directoryName));

                File.WriteAllText(fullPath, string.Empty);
                Assert.True(FileSystems.Default.FileExists(fullPath));

                result[i] = fullPath;
            }

            return result;
        }

        public delegate TransientTestFile CreateProjectFileDelegate(
            TestEnvironment env,
            int projectNumber,
            int[] projectReferences = null,
            Dictionary<string, string[]> projectReferenceTargets = null,
            string defaultTargets = null,
            string extraContent = null);

        public static TransientTestFile CreateProjectFile(
            TestEnvironment env,
            int projectNumber,
            int[] projectReferences = null,
            Dictionary<string, string[]> projectReferenceTargets = null,
            string defaultTargets = null,
            string extraContent = null)
        {
            var sb = new StringBuilder(64);

            sb.Append(
                defaultTargets == null
                    ? "<Project>"
                    : $"<Project DefaultTargets=\"{defaultTargets}\">");

            sb.Append("<ItemGroup>");

            if (projectReferences != null)
            {
                foreach (int projectReference in projectReferences)
                {
                    sb.AppendFormat("<ProjectReference Include=\"{0}.proj\" />", projectReference);
                }
            }

            if (projectReferenceTargets != null)
            {
                foreach (KeyValuePair<string, string[]> pair in projectReferenceTargets)
                {
                    sb.AppendFormat("<ProjectReferenceTargets Include=\"{0}\" Targets=\"{1}\" />", pair.Key, string.Join(";", pair.Value));
                }
            }

            sb.Append("</ItemGroup>");

            // Ensure there is at least one valid target in the project
            sb.Append("<Target Name='Build'/>");

            foreach (var defaultTarget in (defaultTargets ?? string.Empty).Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append("<Target Name='").Append(defaultTarget).Append("'/>");
            }

            sb.Append(extraContent ?? string.Empty);

            sb.Append("</Project>");

            return env.CreateFile(projectNumber + ".proj", sb.ToString());
        }

        public static ProjectGraph CreateProjectGraph(
            TestEnvironment env,
            // direct dependencies that the kvp.key node has on the nodes represented by kvp.value
            IDictionary<int, int[]> dependencyEdges,
            IDictionary<string, string> globalProperties = null,
            CreateProjectFileDelegate createProjectFile = null,
            IEnumerable<int> entryPoints = null,
            ProjectCollection projectCollection = null,
            IDictionary<int, string> extraContentPerProjectNumber = null,
            string extraContentForAllNodes = null)
        {
            createProjectFile ??= CreateProjectFile;

            var nodes = new Dictionary<int, (bool IsRoot, string ProjectPath)>();

            // add nodes with dependencies
            foreach (var nodeDependencies in dependencyEdges)
            {
                var parent = nodeDependencies.Key;

                if (!nodes.ContainsKey(parent))
                {
                    TransientTestFile file = createProjectFile(
                        env,
                        parent,
                        nodeDependencies.Value,
                        projectReferenceTargets: null,
                        defaultTargets: null,
                        extraContent: GetExtraContent(parent));
                    nodes[parent] = (IsRoot(parent), file.Path);
                }
            }

            // add what's left, nodes without dependencies
            foreach (var nodeDependencies in dependencyEdges)
            {
                if (nodeDependencies.Value == null)
                {
                    continue;
                }

                foreach (var reference in nodeDependencies.Value)
                {
                    if (!nodes.ContainsKey(reference))
                    {
                        TransientTestFile file = createProjectFile(
                            env,
                            reference,
                            projectReferenceTargets: null,
                            defaultTargets: null,
                            extraContent: GetExtraContent(reference));
                        nodes[reference] = (false, file.Path);
                    }
                }
            }

            var entryProjectFiles = entryPoints != null
                            ? nodes.Where(n => entryPoints.Contains(n.Key)).Select(n => n.Value.ProjectPath)
                            : nodes.Where(n => n.Value.IsRoot).Select(n => n.Value.ProjectPath);

            return new ProjectGraph(
                entryProjectFiles,
                globalProperties ?? new Dictionary<string, string>(),
                projectCollection ?? env.CreateProjectCollection()
                    .Collection);

            string GetExtraContent(int projectNum)
            {
                string extraContent = extraContentPerProjectNumber != null && extraContentPerProjectNumber.TryGetValue(projectNum, out string extraContentForProject)
                    ? extraContentForProject
                    : string.Empty;

                extraContent += extraContentForAllNodes ?? string.Empty;

                return extraContent.Cleanup();
            }

            bool IsRoot(int node)
            {
                foreach (var nodeDependencies in dependencyEdges)
                {
                    if (nodeDependencies.Value?.Contains(node) == true)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static string[] SplitPathIntoFragments(string path)
        {
            // Both Path.AltDirectorSeparatorChar and Path.DirectorySeparator char return '/' on OSX,
            // which renders them useless for the following case where I want to split a path that may contain either separator
            var splits = path.Split(MSBuildConstants.ForwardSlashBackslash);

            // if the path is rooted then the first split is either empty (Unix) or 'c:' (Windows)
            // in this case the root must be restored back to '/' (Unix) or 'c:\' (Windows)
            if (Path.IsPathRooted(path))
            {
                splits[0] = Path.GetPathRoot(path);
            }

            return splits;
        }

        /// <summary>
        /// Deletes a bunch of files, including their containing directories
        /// if they become empty
        /// </summary>
        public static void DeleteFiles(params string[] paths)
        {
            // When we delete the file directory which has the sub folder/file firstly, it will not be deleted since not empty.
            // So sort paths descendingly by file directory length, it will delete sub folder/file at first.
            var pathsSortedByDepth = paths.OrderByDescending(x => Path.GetDirectoryName(Path.GetFullPath(x)).Length);

            foreach (string path in pathsSortedByDepth)
            {
                if (FileSystems.Default.FileExists(path))
                {
                    File.Delete(path);
                }

                string directory = Path.GetDirectoryName(path);
                if (FileSystems.Default.DirectoryExists(directory) && (Directory.GetFileSystemEntries(directory).Length == 0))
                {
                    Directory.Delete(directory);
                }
            }
        }

        /// <summary>
        /// Given two methods accepting no parameters and returning none, verifies they
        /// both throw, and throw the same exception type.
        /// </summary>
        public static void VerifyAssertThrowsSameWay(Action method1, Action method2)
        {
            Exception ex1 = null;
            Exception ex2 = null;

            try
            {
                method1();
            }
            catch (Exception ex)
            {
                ex1 = ex;
            }

            try
            {
                method2();
            }
            catch (Exception ex)
            {
                ex2 = ex;
            }

            if (ex1 == null && ex2 == null)
            {
                Assert.Fail("Neither threw");
            }

            Assert.NotNull(ex1); // "First method did not throw, second: {0}", ex2 == null ? "" : ex2.GetType() + ex2.Message);
            Assert.NotNull(ex2); // "Second method did not throw, first: {0}", ex1 == null ? "" : ex1.GetType() + ex1.Message);
            Assert.Equal(ex1.GetType(), ex2.GetType()); // "Both methods threw but the first threw {0} '{1}' and the second threw {2} '{3}'", ex1.GetType(), ex1.Message, ex2.GetType(), ex2.Message);

            Console.WriteLine("COMPARE EXCEPTIONS:\n\n#1: {0}\n\n#2: {1}", ex1.Message, ex2.Message);
        }

        /// <summary>
        /// Verify method throws invalid operation exception.
        /// </summary>
        public static void VerifyAssertThrowsInvalidOperation(Action method)
        {
            Assert.Throws<InvalidOperationException>(method);
        }

        /// <summary>
        /// Verify that the expected content matches the actual content
        /// </summary>
        public static void VerifyAssertLineByLine(string expected, string actual, bool ignoreFirstLineOfActual, ITestOutputHelper testOutput = null)
        {
            Action<string> LogLine = testOutput == null ? (Action<string>)Console.WriteLine : testOutput.WriteLine;

            string[] actualLines = SplitIntoLines(actual);

            if (ignoreFirstLineOfActual)
            {
                // Remove the first line of the actual content we got back,
                // since it's just the xml declaration, which we don't care about
                string[] temporary = new string[actualLines.Length - 1];

                for (int i = 0; i < temporary.Length; i++)
                {
                    temporary[i] = actualLines[i + 1];
                }

                actualLines = temporary;
            }

            string[] expectedLines = SplitIntoLines(expected);

            bool expectedAndActualDontMatch = false;
            for (int i = 0; i < Math.Min(actualLines.Length, expectedLines.Length); i++)
            {
                if (expectedLines[i] != actualLines[i])
                {
                    expectedAndActualDontMatch = true;
                    LogLine("<   " + expectedLines[i] + "\n>   " + actualLines[i] + "\n");
                }
            }

            if (actualLines.Length == expectedLines.Length && expectedAndActualDontMatch)
            {
                string output = "\r\n#################################Expected#################################\n" + string.Join("\r\n", expectedLines);
                output += "\r\n#################################Actual#################################\n" + string.Join("\r\n", actualLines);

                Assert.Fail(output);
            }

            if (actualLines.Length > expectedLines.Length)
            {
                LogLine("\n#################################Expected#################################\n" + string.Join("\n", expectedLines));
                LogLine("#################################Actual#################################\n" + string.Join("\n", actualLines));

                Assert.Fail("Expected content was shorter, actual had this extra line: '" + actualLines[expectedLines.Length] + "'");
            }
            else if (actualLines.Length < expectedLines.Length)
            {
                LogLine("\n#################################Expected#################################\n" + string.Join("\n", expectedLines));
                LogLine("#################################Actual#################################\n" + string.Join("\n", actualLines));

                Assert.Fail("Actual content was shorter, expected had this extra line: '" + expectedLines[actualLines.Length] + "'");
            }
        }

        /// <summary>
        /// Clear the dirty flag of a ProjectRootElement by saving to a dummy writer.
        /// </summary>
        public static void ClearDirtyFlag(ProjectRootElement project)
        {
            using var sw = new StringWriter();
            project.Save(sw);
            Assert.False(project.HasUnsavedChanges);
        }

        /// <summary>
        /// Gets a command that can be used by an Exec task to sleep for the specified amount of time.
        /// </summary>
        /// <param name="timeSpan">A <see cref="TimeSpan"/> representing the amount of time to sleep.</param>
        public static string GetSleepCommand(TimeSpan timeSpan)
        {
            return string.Format(
                GetSleepCommandTemplate(),
                NativeMethodsShared.IsWindows
                    ? timeSpan.TotalMilliseconds // powershell can't handle floating point seconds, so give it milliseconds
                    : timeSpan.TotalSeconds);
        }

        /// <summary>
        /// Gets a command template that can be used by an Exec task to sleep for the specified amount of time. The string has to be formatted with the number of seconds to sleep
        /// </summary>
        public static string GetSleepCommandTemplate()
        {
            return
                NativeMethodsShared.IsWindows
                    ? "@powershell -NoLogo -NoProfile -command &quot;Start-Sleep -Milliseconds {0}&quot; &gt;nul"
                    : "sleep {0}";
        }

        /// <summary>
        /// Break the provided string into an array, on newlines
        /// </summary>
        private static string[] SplitIntoLines(string content)
        {
            string[] result = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            return result;
        }

        /// <summary>
        /// Used for file matching tests
        /// MSBuild does not accept forward slashes on rooted paths, so those are returned unchanged
        /// </summary>
        public static string ToForwardSlash(string path) =>
            Path.IsPathRooted(path)
                ? path
                : path.ToSlash();

        public sealed class ElementLocationComparerIgnoringType : IEqualityComparer<ElementLocation>
        {
            public bool Equals(ElementLocation x, ElementLocation y)
            {
                if (x == null)
                {
                    return y == null;
                }

                if (x.Line != y.Line || x.Column != y.Column)
                {
                    return false;
                }

                if (!string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }

            public int GetHashCode(ElementLocation obj)
            {
                return obj.Line.GetHashCode() ^ obj.Column.GetHashCode() ^ obj.File.GetHashCode();
            }
        }

        public sealed class BuildManagerSession : IDisposable
        {
            private readonly TestEnvironment _env;
            private readonly BuildManager _buildManager;
            private bool _disposed;

            public MockLogger Logger { get; set; }

            public BuildManagerSession(
                TestEnvironment env,
                BuildParameters buildParameters = null,
                bool enableNodeReuse = false,
                IEnumerable<BuildManager.DeferredBuildMessage> deferredMessages = null)
            {
                _env = env;

                Logger = new MockLogger(_env.Output);
                var loggers = new[] { Logger };

                var actualBuildParameters = buildParameters ?? new BuildParameters();

                actualBuildParameters.Loggers = actualBuildParameters.Loggers == null
                    ? loggers
                    : actualBuildParameters.Loggers.Concat(loggers).ToArray();

                actualBuildParameters.ShutdownInProcNodeOnBuildFinish = true;
                actualBuildParameters.EnableNodeReuse = enableNodeReuse;

                _buildManager = new BuildManager();
                _buildManager.BeginBuild(actualBuildParameters, deferredMessages);
            }

            public BuildResult BuildProjectFile(
                string projectFile,
                string[] entryTargets = null,
                Dictionary<string, string> globalProperties = null)
            {
                var buildTask = BuildProjectFileAsync(projectFile, entryTargets, globalProperties);
                return buildTask.Result;
            }

            public async Task<BuildResult> BuildProjectFileAsync(
                string projectFile,
                string[] entryTargets = null,
                Dictionary<string, string> globalProperties = null)
            {
                var buildRequestData = new BuildRequestData(
                    projectFile,
                    globalProperties ?? new Dictionary<string, string>(),
                    MSBuildConstants.CurrentToolsVersion,
                    entryTargets ?? Array.Empty<string>(),
                    null);
                return await BuildAsync(buildRequestData);
            }

            public async Task<BuildResult> BuildAsync(BuildRequestData requestData)
            {
                var completion = new TaskCompletionSource<BuildResult>();

                _buildManager.PendBuildRequest(requestData).ExecuteAsync(submission =>
                {
                    completion.SetResult(submission.BuildResult);
                }, null);

                return await completion.Task;
            }

            public BuildResult Build(BuildRequestData requestData) => _buildManager.BuildRequest(requestData);

            public GraphBuildResult BuildGraphSubmission(GraphBuildRequestData requestData) => _buildManager.BuildRequest(requestData);

            public GraphBuildResult BuildGraph(ProjectGraph graph, string[] entryTargets = null)
                => _buildManager.BuildRequest(new GraphBuildRequestData(graph, entryTargets ?? Array.Empty<string>()));

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                _buildManager.EndBuild();
                _buildManager.Dispose();
            }
        }

        public sealed class LoggingDirectoryCacheFactory : IDirectoryCacheFactory
        {
            public List<LoggingDirectoryCache> DirectoryCaches { get; } = new();

            public IDirectoryCache GetDirectoryCacheForEvaluation(int evaluationId)
            {
                var directoryCache = new LoggingDirectoryCache(evaluationId);
                DirectoryCaches.Add(directoryCache);
                return directoryCache;
            }
        }

        public sealed class LoggingDirectoryCache : IDirectoryCache
        {
            public int EvaluationId { get; }

            public ConcurrentDictionary<string, int> ExistenceChecks { get; } = new();
            public ConcurrentDictionary<string, int> Enumerations { get; } = new();

            public LoggingDirectoryCache(int evaluationId)
            {
                EvaluationId = evaluationId;
            }

            public bool DirectoryExists(string path)
            {
                IncrementExistenceChecks(path);
                return Directory.Exists(path);
            }

            public bool FileExists(string path)
            {
                IncrementExistenceChecks(path);
                return File.Exists(path);
            }

            public IEnumerable<TResult> EnumerateDirectories<TResult>(string path, string pattern, FindPredicate predicate, FindTransform<TResult> transform)
            {
                IncrementEnumerations(path);
                return Enumerable.Empty<TResult>();
            }

            public IEnumerable<TResult> EnumerateFiles<TResult>(string path, string pattern, FindPredicate predicate, FindTransform<TResult> transform)
            {
                IncrementEnumerations(path);
                return Enumerable.Empty<TResult>();
            }

            private void IncrementExistenceChecks(string path)
            {
                ExistenceChecks.AddOrUpdate(path, p => 1, (p, c) => c + 1);
            }

            private void IncrementEnumerations(string path)
            {
                Enumerations.AddOrUpdate(path, p => 1, (p, c) => c + 1);
            }
        }

        public sealed class LoggingFileSystem : MSBuildFileSystemBase
        {
            private int _fileSystemCalls;

            public int FileSystemCalls => _fileSystemCalls;

            public ConcurrentDictionary<string, int> ExistenceChecks { get; } = new ConcurrentDictionary<string, int>();

            public override TextReader ReadFile(string path)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.ReadFile(path);
            }

            public override Stream GetFileStream(string path, FileMode mode, FileAccess access, FileShare share)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.GetFileStream(path, mode, access, share);
            }

            public override string ReadFileAllText(string path)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.ReadFileAllText(path);
            }

            public override byte[] ReadFileAllBytes(string path)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.ReadFileAllBytes(path);
            }

            public override IEnumerable<string> EnumerateFiles(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.EnumerateFiles(path, searchPattern, searchOption);
            }

            public override IEnumerable<string> EnumerateDirectories(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.EnumerateDirectories(path, searchPattern, searchOption);
            }

            public override IEnumerable<string> EnumerateFileSystemEntries(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.EnumerateFileSystemEntries(path, searchPattern, searchOption);
            }

            public override FileAttributes GetAttributes(string path)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.GetAttributes(path);
            }

            public override DateTime GetLastWriteTimeUtc(string path)
            {
                IncrementCalls(ref _fileSystemCalls);

                return base.GetLastWriteTimeUtc(path);
            }

            public override bool DirectoryExists(string path)
            {
                IncrementCalls(ref _fileSystemCalls);
                IncrementExistenceChecks(path);

                return base.DirectoryExists(path);
            }

            public override bool FileExists(string path)
            {
                IncrementCalls(ref _fileSystemCalls);
                IncrementExistenceChecks(path);

                return base.FileExists(path);
            }

            private int _fileOrDirectoryExistsCalls;
            public int FileOrDirectoryExistsCalls => _fileOrDirectoryExistsCalls;

            public override bool FileOrDirectoryExists(string path)
            {
                IncrementCalls(ref _fileSystemCalls);
                IncrementCalls(ref _fileOrDirectoryExistsCalls);
                IncrementExistenceChecks(path);

                return base.FileOrDirectoryExists(path);
            }

            private void IncrementCalls(ref int incremented)
            {
                Interlocked.Increment(ref incremented);
            }

            private void IncrementExistenceChecks(string path)
            {
                ExistenceChecks.AddOrUpdate(path, p => 1, (p, c) => c + 1);
            }
        }
    }
}
