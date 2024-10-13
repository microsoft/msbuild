// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests.BackEnd;
using Xunit;

#pragma warning disable CA3075 // Insecure DTD processing in XML
#pragma warning disable IDE0022 // Use expression body for method
#pragma warning disable IDE0058 // Expression value is never used
#pragma warning disable SA1010 // Opening square brackets should be spaced correctly

namespace Microsoft.Build.UnitTests.Construction
{
    /// <summary>
    /// Unit tests for <see cref="ElementLocation"/>.
    /// </summary>
    [Collection("ElementLocation")]
    public class ElementLocation_Tests
    {
        /// <summary>
        /// Reset the file path cache index to zero. We have tests which validate that
        /// <see cref="ElementLocation.Create"/> returns a specific storage type, and
        /// that requires the index to be within certain ranges.
        /// </summary>
        public ElementLocation_Tests() => ElementLocation.DangerousInternalResetFileIndex();

        [Theory]
        [MemberData(nameof(GetCreateTestCases))]
        public void Create(string? file, int line, int column, string typeName)
        {
            ElementLocation location = ElementLocation.Create(file, line, column);

            Assert.Equal(file ?? "", location.File);
            Assert.Equal(line, location.Line);
            Assert.Equal(column, location.Column);

            Assert.Contains(typeName, location.GetType().FullName);
        }

        [Theory]
        [InlineData("file", -1, 0)]
        [InlineData("file", 0, -1)]
        [InlineData("file", int.MaxValue, -1)]
        [InlineData("file", -1, int.MaxValue)]
        [InlineData("file", -1, -1)]
        [InlineData("file", int.MinValue, 0)]
        [InlineData("file", 0, int.MinValue)]
        [InlineData("file", int.MinValue, int.MinValue)]
        public void Create_NegativeValuesThrow(string file, int line, int column)
        {
            _ = Assert.Throws<InternalErrorException>(
                () => ElementLocation.Create(file, line, column));
        }

        [Fact]
        public void Create_FileIndexPacking()
        {
            int i = 0;

            for (int j = 0; j < ushort.MaxValue; j++)
            {
                Assert.Contains("SmallFileElementLocation", Next());
            }

            // If the file index exceed 65,535 items, we use a larger storage type.
            Assert.Contains("LargeFileElementLocation", Next());

            string? Next() => ElementLocation.Create("file" + i++, 0, 0).GetType().FullName;
        }

        [Fact]
        public void Create_NullFile()
        {
            ElementLocation location = ElementLocation.Create(null);

            Assert.Equal("", location.File);
            Assert.Equal(0, location.Line);
            Assert.Equal(0, location.Column);
        }

        [Theory]
        [MemberData(nameof(GetCreateTestCases))]
        public void RoundTripSerialisation(string? file, int line, int column, string typeName)
        {
            ElementLocation location = ElementLocation.Create(file, line, column);

            TranslationHelpers.GetWriteTranslator().Translate(ref location, ElementLocation.FactoryForDeserialization);
            ElementLocation? deserializedLocation = null;
            TranslationHelpers.GetReadTranslator().Translate(ref deserializedLocation, ElementLocation.FactoryForDeserialization);
            Assert.NotNull(deserializedLocation);

            Assert.Equal(file ?? "", deserializedLocation.File);
            Assert.Equal(line, deserializedLocation.Line);
            Assert.Equal(column, deserializedLocation.Column);

            Assert.Contains(typeName, deserializedLocation.GetType().FullName);
        }

        public static IEnumerable<object?[]> GetCreateTestCases()
        {
            yield return [null, 0, 0, "EmptyElementLocation"];
            yield return ["", 0, 0, "EmptyElementLocation"];
            yield return ["file", byte.MaxValue, 0, "SmallFileElementLocation"];
            yield return ["file", byte.MaxValue + 1, 0, "SmallLineElementLocation"];
            yield return ["file", 0, byte.MaxValue, "SmallFileElementLocation"];
            yield return ["file", 0, byte.MaxValue + 1, "SmallColumnElementLocation"];
            yield return ["file", ushort.MaxValue, 0, "SmallLineElementLocation"];
            yield return ["file", ushort.MaxValue + 1, 0, "LargeLineElementLocation"];
            yield return ["file", 0, ushort.MaxValue, "SmallColumnElementLocation"];
            yield return ["file", 0, ushort.MaxValue + 1, "LargeColumnElementLocation"];
            yield return ["file", ushort.MaxValue, ushort.MaxValue, "LargeFileElementLocation"];
            yield return ["file", ushort.MaxValue + 1, ushort.MaxValue, "FullElementLocation"];
            yield return ["file", ushort.MaxValue, ushort.MaxValue + 1, "FullElementLocation"];
            yield return ["file", ushort.MaxValue + 1, ushort.MaxValue + 1, "FullElementLocation"];
        }

        [Fact]
        public void Equality()
        {
            ElementLocation location1 = ElementLocation.Create("file", line: 65536, column: 65537);
            ElementLocation location2 = ElementLocation.Create("file", line: 0, column: 1);
            ElementLocation location3 = ElementLocation.Create("file", line: 0, column: 65537);
            ElementLocation location4 = ElementLocation.Create("file", line: 65536, column: 1);
            ElementLocation location5 = ElementLocation.Create("file", line: 0, column: 1);
            ElementLocation location6 = ElementLocation.Create("file", line: 65536, column: 65537);

            Assert.True(location1.Equals(location6));
            Assert.True(location2.Equals(location5));
            Assert.False(location3.Equals(location1));
            Assert.False(location4.Equals(location2));
            Assert.False(location4.Equals(location6));
        }

        /// <summary>
        /// Check it will use large element location when it should.
        /// </summary>
        [Fact]
        public void TestLargeElementLocationUsedLargeColumn()
        {
            StringBuilder xml = new(capacity: 71_000);

            xml.AppendLine(ObjectModelHelpers.CleanupFileContents(
                """
                <Project xmlns="msbuildnamespace" ToolsVersion="msbuilddefaulttoolsversion">
                  <ItemGroup>
                """));
            xml.Append(' ', 70_000);
            xml.Append("""
                <x/>
                  </ItemGroup>
                </Project>
                """);

            Assert.Equal(71_000, xml.Capacity);

            XmlDocumentWithLocation doc = new();
            doc.LoadXml(xml.ToString());

            InvalidProjectFileException ex = Assert.Throws<InvalidProjectFileException>(
                () => ProjectRootElement.Open(doc));

            Assert.Equal(70_001, ex.ColumnNumber);
            Assert.Equal(3, ex.LineNumber);
        }

        /// <summary>
        /// Check it will use large element location when it should.
        /// </summary>
        [Fact]
        public void TestLargeElementLocationUsedLargeLine()
        {
            StringBuilder xml = new(capacity: 71_000);

            xml.Append(ObjectModelHelpers.CleanupFileContents(
                """
                <Project xmlns="msbuildnamespace" ToolsVersion="msbuilddefaulttoolsversion">
                  <ItemGroup>
                """));

            xml.Append('\n', 70_000);

            xml.Append(
                """
                    <x/>
                  </ItemGroup>
                </Project>
                """);

            Assert.Equal(71_000, xml.Capacity);

            XmlDocumentWithLocation doc = new();
            doc.LoadXml(xml.ToString());

            InvalidProjectFileException ex = Assert.Throws<InvalidProjectFileException>(
                () => ProjectRootElement.Open(doc));

            Assert.Equal(70_002, ex.LineNumber);
            Assert.Equal(5, ex.ColumnNumber);
        }

        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void LocationsMatchWhenReadOnlyOrWriteable()
        {
            string content = ObjectModelHelpers.CleanupFileContents(
                """
                <Project ToolsVersion="msbuilddefaulttoolsversion" xmlns="msbuildnamespace">
                        <UsingTask TaskName="t" AssemblyName="a" Condition="true"/>
                        <UsingTask TaskName="t" AssemblyFile="a" Condition="true"/>
                        <ItemDefinitionGroup Condition="true" Label="l">
                            <m Condition="true">  foo  bar
                </m>
                    </ItemDefinitionGroup>
                    <ItemGroup>
                        <i Include="i" Condition="true" Exclude="r">
                            <m Condition="true"/>
                        </i>
                    </ItemGroup>
                    <PropertyGroup>
                        <p Condition="true"/>
                    </PropertyGroup>
                    <!-- A comment -->
                    <Target Name="Build" Condition="true" Inputs="i" Outputs="o">
                        <ItemGroup>
                            <i Include="i" Condition="true" Exclude="r">
                                <m Condition="true"/>
                            </i>
                            <i Remove="r"/>
                        </ItemGroup>
                        <PropertyGroup xml:space= "preserve">             <x/>
                            <p     Condition="true"/>
                        </PropertyGroup>
                        <Error Text="xyz" ContinueOnError="true" Importance="high"/>
                    </Target>
                    <Import Project="p" Condition="false"/>
                </Project>
                """);

            string readWriteLoadLocations = GetLocations(content, readOnly: false);
            string readOnlyLoadLocations = GetLocations(content, readOnly: true);

            Helpers.VerifyAssertLineByLine(readWriteLoadLocations, readOnlyLoadLocations);

            static string GetLocations(string content, bool readOnly)
            {
                string file = FileUtilities.GetTemporaryFileName();

                XmlDocumentWithLocation doc = new(loadAsReadOnly: readOnly, fullPath: file);
                doc.LoadXml(content);

                Assert.Equal(readOnly, doc.IsReadOnly);

                XmlNodeList? allNodes = doc.SelectNodes("//*|//@*");

                Assert.NotNull(allNodes);

                StringBuilder locations = new();

                foreach (object node in allNodes)
                {
                    PropertyInfo? property = node.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    var value = (ElementLocation?)property?.GetValue(node, null);

                    if (value is not null) // null means attribute is not present
                    {
                        locations.Append(((XmlNode)node).Name).Append("==").Append(((XmlNode)node).Value ?? "").Append(":  ").Append(value.LocationString.Replace(file, """c:\foo\bar.csproj""")).Append("\r\n");
                    }
                }

                return locations.ToString();
            }
        }

        [Fact]
        public void SaveReadOnly_FilePath_Throws()
        {
            XmlDocumentWithLocation doc = new(loadAsReadOnly: true);

            Assert.True(doc.IsReadOnly);

            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                doc.Save(FileUtilities.GetTemporaryFile());
            });
        }

        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void SaveReadOnly_Stream_Throws()
        {
            XmlDocumentWithLocation doc = new(loadAsReadOnly: true);

            Assert.True(doc.IsReadOnly);

            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                doc.Save(new MemoryStream());
            });
        }

        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void SaveReadOnly_TextWriter_Throws()
        {
            XmlDocumentWithLocation doc = new(loadAsReadOnly: true);

            Assert.True(doc.IsReadOnly);

            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                doc.Save(new StringWriter());
            });
        }

        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void SaveReadOnly_XmlWriter_Throws()
        {
            XmlDocumentWithLocation doc = new(loadAsReadOnly: true);

            Assert.True(doc.IsReadOnly);

            using XmlWriter wr = XmlWriter.Create(new MemoryStream());

            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                doc.Save(wr);
            });
        }
    }
}
