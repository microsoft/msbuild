// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;
using Microsoft.Win32;



using ProjectHelpers = Microsoft.Build.UnitTests.BackEnd.ProjectHelpers;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using ProjectItemInstanceFactory = Microsoft.Build.Execution.ProjectItemInstance.TaskItem.ProjectItemInstanceFactory;

using Xunit;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Shared.FileSystem;
using Shouldly;

namespace Microsoft.Build.UnitTests.Evaluation
{
    public class Expander_Tests
    {
        private string _dateToParse = new DateTime(2010, 12, 25).ToString(CultureInfo.CurrentCulture);
        private static readonly string s_rootPathPrefix = NativeMethodsShared.IsWindows ? "C:\\" : Path.VolumeSeparatorChar.ToString();

        [Fact]
        public void ExpandAllIntoTaskItems0()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            IList<TaskItem> itemsOut = expander.ExpandIntoTaskItemsLeaveEscaped("", ExpanderOptions.ExpandProperties, null);

            ObjectModelHelpers.AssertItemsMatch("", GetTaskArrayFromItemList(itemsOut));
        }

        [Fact]
        public void ExpandAllIntoTaskItems1()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            IList<TaskItem> itemsOut = expander.ExpandIntoTaskItemsLeaveEscaped("foo", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            ObjectModelHelpers.AssertItemsMatch(@"foo", GetTaskArrayFromItemList(itemsOut));
        }

        [Fact]
        public void ExpandAllIntoTaskItems2()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            IList<TaskItem> itemsOut = expander.ExpandIntoTaskItemsLeaveEscaped("foo;bar", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            ObjectModelHelpers.AssertItemsMatch(@"
                foo
                bar
                ", GetTaskArrayFromItemList(itemsOut));
        }

        [Fact]
        public void ExpandAllIntoTaskItems3()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            List<ProjectItemInstance> ig = new List<ProjectItemInstance>();
            ig.Add(new ProjectItemInstance(project, "Compile", "foo.cs", project.FullPath));
            ig.Add(new ProjectItemInstance(project, "Compile", "bar.cs", project.FullPath));

            List<ProjectItemInstance> ig2 = new List<ProjectItemInstance>();
            ig2.Add(new ProjectItemInstance(project, "Resource", "bing.resx", project.FullPath));

            ItemDictionary<ProjectItemInstance> itemsByType = new ItemDictionary<ProjectItemInstance>();
            itemsByType.ImportItems(ig);
            itemsByType.ImportItems(ig2);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg, itemsByType);

            IList<TaskItem> itemsOut = expander.ExpandIntoTaskItemsLeaveEscaped("foo;bar;@(compile);@(resource)", ExpanderOptions.ExpandPropertiesAndItems, MockElementLocation.Instance);

            ObjectModelHelpers.AssertItemsMatch(@"
                foo
                bar
                foo.cs
                bar.cs
                bing.resx
                ", GetTaskArrayFromItemList(itemsOut));
        }

        [Fact]
        public void ExpandAllIntoTaskItems4()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("a", "aaa"));
            pg.Set(ProjectPropertyInstance.Create("b", "bbb"));
            pg.Set(ProjectPropertyInstance.Create("c", "cc;dd"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            IList<TaskItem> itemsOut = expander.ExpandIntoTaskItemsLeaveEscaped("foo$(a);$(b);$(c)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            ObjectModelHelpers.AssertItemsMatch(@"
                fooaaa
                bbb
                cc
                dd
                ", GetTaskArrayFromItemList(itemsOut));
        }

        /// <summary>
        /// Expand property expressions into ProjectPropertyInstance items
        /// </summary>
        [Fact]
        public void ExpandPropertiesIntoProjectPropertyInstances()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("a", "aaa"));
            pg.Set(ProjectPropertyInstance.Create("b", "bbb"));
            pg.Set(ProjectPropertyInstance.Create("c", "cc;dd"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");
            IList<ProjectItemInstance> itemsOut = expander.ExpandIntoItemsLeaveEscaped("foo$(a);$(b);$(c);$(d", itemFactory, ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(5, itemsOut.Count);
        }

        /// <summary>
        /// Expand property expressions into ProjectPropertyInstance items
        /// </summary>
        [Fact]
        public void ExpandEmptyPropertyExpressionToEmpty()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$()", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal(String.Empty, result);
        }

        /// <summary>
        /// Expand an item vector into items of the specified type
        /// </summary>
        [Fact]
        public void ExpandItemVectorsIntoProjectItemInstancesSpecifyingItemType()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "j");

            IList<ProjectItemInstance> items = expander.ExpandIntoItemsLeaveEscaped("@(i)", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(2, items.Count);
            Assert.Equal("j", items[0].ItemType);
            Assert.Equal("j", items[1].ItemType);
            Assert.Equal("i0", items[0].EvaluatedInclude);
            Assert.Equal("i1", items[1].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector into items of the type of the item vector
        /// </summary>
        [Fact]
        public void ExpandItemVectorsIntoProjectItemInstancesWithoutSpecifyingItemType()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project);

            IList<ProjectItemInstance> items = expander.ExpandIntoItemsLeaveEscaped("@(i)", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(2, items.Count);
            Assert.Equal("i", items[0].ItemType);
            Assert.Equal("i", items[1].ItemType);
            Assert.Equal("i0", items[0].EvaluatedInclude);
            Assert.Equal("i1", items[1].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector function AnyHaveMetadataValue
        /// </summary>
        [Fact]
        public void ExpandItemVectorFunctionsAnyHaveMetadataValue()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->AnyHaveMetadataValue('Even', 'true'))", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Single(itemsTrue);
            Assert.Equal("i", itemsTrue[0].ItemType);
            Assert.Equal("true", itemsTrue[0].EvaluatedInclude);

            IList<ProjectItemInstance> itemsFalse = expander.ExpandIntoItemsLeaveEscaped("@(i->AnyHaveMetadataValue('Even', 'goop'))", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Single(itemsFalse);
            Assert.Equal("i", itemsFalse[0].ItemType);
            Assert.Equal("false", itemsFalse[0].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector function Metadata()->DirectoryName()->Distinct()
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void ExpandItemVectorFunctionsGetDirectoryNameOfMetadataValueDistinct()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta0')->DirectoryName()->Distinct())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Single(itemsTrue);
            Assert.Equal("i", itemsTrue[0].ItemType);
            Assert.Equal(Path.Combine(s_rootPathPrefix, "firstdirectory", "seconddirectory"), itemsTrue[0].EvaluatedInclude);

            IList<ProjectItemInstance> itemsDir = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta9')->DirectoryName()->Distinct())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Single(itemsDir);
            Assert.Equal("i", itemsDir[0].ItemType);
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), @"seconddirectory"), itemsDir[0].EvaluatedInclude);
        }

        /// <summary>
        /// /// Expand an item vector function that is an itemspec modifier
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void ExpandItemVectorFunctionsItemSpecModifier()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta0')->Directory())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(Path.Combine("firstdirectory", "seconddirectory") + Path.DirectorySeparatorChar, itemsTrue[5].EvaluatedInclude);

            itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta0')->Filename())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(@"file0", itemsTrue[5].EvaluatedInclude);

            itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta0')->Extension())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(@".ext", itemsTrue[5].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item expression (that isn't a real expression) but includes a property reference nested within a metadata reference
        /// </summary>
        [Fact]
        public void ExpandItemVectorFunctionsInvalid1()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped("[@(type-&gt;'%($(a)), '%'')]", ExpanderOptions.ExpandAll, MockElementLocation.Instance);

            Assert.Equal(@"[@(type-&gt;'%(filename), '%'')]", result);
        }

        /// <summary>
        /// Expand an item expression (that isn't a real expression) but includes a metadata reference that till needs to be expanded
        /// </summary>
        [Fact]
        public void ExpandItemVectorFunctionsInvalid2()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped("[@(i->'%(Meta9))']", ExpanderOptions.ExpandAll, MockElementLocation.Instance);

            Assert.Equal(@"[@(i->')']", result);
        }

        /// <summary>
        /// Expand an item vector function that is chained into a string
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void ExpandItemVectorFunctionsChained1()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped("@(i->'%(Meta0)'->'%(Directory)'->Distinct())", ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(Path.Combine("firstdirectory", "seconddirectory") + Path.DirectorySeparatorChar, result);
        }

        /// <summary>
        /// Expand an item vector function that is chained and has constants into a string
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void ExpandItemVectorFunctionsChained2()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped("[@(i->'%(Meta0)'->'%(Directory)'->Distinct())]", ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(@"[firstdirectory\seconddirectory\]", result);
        }

        /// <summary>
        /// Expand an item vector function that is chained and has constants into a string
        /// </summary>
        [Fact]
        public void ExpandItemVectorFunctionsChained3()
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped("@(i->'%(MetaBlank)'->'%(Directory)'->Distinct())", ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(@"", result);
        }

        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void ExpandItemVectorFunctionsChainedProject1()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
<Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>

    <ItemGroup>
        <Compile Include=`a.cpp`>
            <SomeMeta>C:\Value1\file1.txt</SomeMeta>
            <A>||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||</A>
            <B>##</B>
        </Compile>
        <Compile Include=`b.cpp`>
            <SomeMeta>C:\Value2\file2.txt</SomeMeta>
            <A>||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||</A>
            <B>##</B>
        </Compile>
        <Compile Include=`c.cpp`>
            <SomeMeta>C:\Value2\file3.txt</SomeMeta>
            <A>||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||</A>
            <B>##</B>
        </Compile>
        <Compile Include=`c.cpp`>
            <SomeMeta>C:\Value2\file3.txt</SomeMeta>
            <A>||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||</A>
            <B>##</B>
        </Compile>
    </ItemGroup>

    <Target Name=`Build`>      
        <Message Text=`DirChain0: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct())`/>
        <Message Text=`DirChain1: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct(), '%(A)')`/>
        <Message Text=`DirChain2: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct(), '%(A)%(B)')`/>
        <Message Text=`DirChain3: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct(), '%(A)$%(B)')`/>
        <Message Text=`DirChain4: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct(), '$%(A)$%(B)')`/>
        <Message Text=`DirChain5: @(Compile->'%(SomeMeta)'->'%(Directory)'->Distinct(), '$%(A)$%(B)$')`/>
    </Target>
</Project>
                ");

            logger.AssertLogContains(@"DirChain0: Value1\;Value2\");
            logger.AssertLogContains(@"DirChain1: Value1\||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||Value2\");
            logger.AssertLogContains(@"DirChain2: Value1\||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||##Value2\");
            logger.AssertLogContains(@"DirChain3: Value1\||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||$##Value2\");
            logger.AssertLogContains(@"DirChain4: Value1\$||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||$##Value2\");
            logger.AssertLogContains(@"DirChain5: Value1\$||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||$##$Value2\");
        }

        [Fact]
        public void ExpandItemVectorFunctionsCount1()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
                <J Include=`;`/>
            </ItemGroup>

            <Message Text=`[@(I->Count())][@(J->Count())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogContains("[2][0]");
        }

        [Fact]
        public void ExpandItemVectorFunctionsCount2()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
                <J Include=`;`/>
                <K Include=`@(I->Count());@(J->Count())`/>
            </ItemGroup>

            <Message Text=`@(K)` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogContains("2;0");
        }

        [Fact]
        public void ExpandItemVectorFunctionsCountOperatingOnEmptyResult1()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
                <J Include=`;`/>
            </ItemGroup>

            <Message Text=`[@(I->Metadata('foo')->Count())][@(J->Metadata('foo')->Count())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogContains("[0][0]");
        }

        [Fact]
        public void ExpandItemVectorFunctionsCountOperatingOnEmptyResult2()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
                <J Include=`;`/>
                <K Include=`@(I->Metadata('foo')->Count());@(J->Metadata('foo')->Count())`/>
            </ItemGroup>

            <Message Text=`@(K)` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogContains("0;0");
        }

        [Fact]
        public void ExpandItemVectorFunctionsBuiltIn1()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
            </ItemGroup>

            <Message Text=`[@(I->FullPath())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            var current = Directory.GetCurrentDirectory();
            log.AssertLogContains(String.Format(@"[{0}foo;{0}bar]", current + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void ExpandItemVectorFunctionsBuiltIn2()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar`/>
            </ItemGroup>

            <Message Text=`[@(I->FullPath()->Distinct())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            var current = Directory.GetCurrentDirectory();
            log.AssertLogContains(String.Format(@"[{0}foo;{0}bar]", current + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void ExpandItemVectorFunctionsBuiltIn3()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar;foo;bar;foo`/>
            </ItemGroup>

            <Message Text=`[@(I->FullPath()->Distinct())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            var current = Directory.GetCurrentDirectory();
            log.AssertLogContains(String.Format(@"[{0}foo;{0}bar]", current + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void ExpandItemVectorFunctionsBuiltIn4()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`foo;bar;foo;bar;foo`/>
            </ItemGroup>

            <Message Text=`[@(I->Identity()->Distinct())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogContains("[foo;bar]");
        }

        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsMaxPathLegacyWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, "https://github.com/microsoft/msbuild/issues/4363")]
        public void ExpandItemVectorFunctionsBuiltIn_PathTooLongError()
        {
            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`fooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo`/>
            </ItemGroup>

            <Message Text=`[@(I->FullPath())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectFailure(content, false /* no crashes */);
            log.AssertLogContains("MSB4198");
        }

        [Fact]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void ExpandItemVectorFunctionsBuiltIn_InvalidCharsError()
        {
            if (!NativeMethodsShared.IsWindows)
            {
                return; // "Cannot have invalid characters in file name on Unix"
            }

            string content = @"
 <Project DefaultTargets=`t` xmlns=`http://schemas.microsoft.com/developer/msbuild/2003`>
 
        <Target Name=`t`>
            <ItemGroup>
                <I Include=`aaa|||bbb\ccc.txt`/>
            </ItemGroup>

            <Message Text=`[@(I->Directory())]` />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectFailure(content, false /* no crashes */);
            log.AssertLogContains("MSB4198");
        }

        /// <summary>
        /// /// Expand an item vector function that is an itemspec modifier
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void ExpandItemVectorFunctionsItemSpecModifier2()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->'%(Meta0)'->'%(Directory)')", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(Path.Combine("firstdirectory", "seconddirectory") + Path.DirectorySeparatorChar, itemsTrue[5].EvaluatedInclude);

            itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->'%(Meta0)'->'%(Filename)')", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(@"file0", itemsTrue[5].EvaluatedInclude);

            itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->'%(Meta0)'->'%(Extension)'->Distinct())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Single(itemsTrue);
            Assert.Equal("i", itemsTrue[0].ItemType);
            Assert.Equal(@".ext", itemsTrue[0].EvaluatedInclude);

            itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->'%(Meta0)'->'%(Filename)'->Substring($(Val)))", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(@"le0", itemsTrue[5].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector function Metadata()->DirectoryName()
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void ExpandItemVectorFunctionsGetDirectoryNameOfMetadataValue()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> itemsTrue = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta0')->DirectoryName())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, itemsTrue.Count);
            Assert.Equal("i", itemsTrue[5].ItemType);
            Assert.Equal(Path.Combine(s_rootPathPrefix, "firstdirectory", "seconddirectory"), itemsTrue[5].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector function Metadata() that contains semi-colon delimited sub-items
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void ExpandItemVectorFunctionsMetadataValueMultiItem()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> items = expander.ExpandIntoItemsLeaveEscaped("@(i->Metadata('Meta10')->DirectoryName())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(20, items.Count);
            Assert.Equal("i", items[5].ItemType);
            Assert.Equal("i", items[6].ItemType);
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), @"secondd;rectory"), items[5].EvaluatedInclude);
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), @"someo;herplace"), items[6].EvaluatedInclude);
        }

        /// <summary>
        /// Expand an item vector function Items->ClearMetadata()
        /// </summary>
        [Fact]
        public void ExpandItemVectorFunctionsClearMetadata()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            ProjectItemInstanceFactory itemFactory = new ProjectItemInstanceFactory(project, "i");

            IList<ProjectItemInstance> items = expander.ExpandIntoItemsLeaveEscaped("@(i->ClearMetadata())", itemFactory, ExpanderOptions.ExpandItems, MockElementLocation.Instance);

            Assert.Equal(10, items.Count);
            Assert.Equal("i", items[5].ItemType);
            Assert.Empty(items[5].Metadata);
        }

        /// <summary>
        /// Creates an expander populated with some ProjectPropertyInstances and ProjectPropertyItems.
        /// </summary>
        /// <returns></returns>
        private Expander<ProjectPropertyInstance, ProjectItemInstance> CreateItemFunctionExpander()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("p", "v0"));
            pg.Set(ProjectPropertyInstance.Create("p", "v1"));
            pg.Set(ProjectPropertyInstance.Create("Val", "2"));
            pg.Set(ProjectPropertyInstance.Create("a", "filename"));

            ItemDictionary<ProjectItemInstance> ig = new ItemDictionary<ProjectItemInstance>();

            for (int n = 0; n < 10; n++)
            {
                ProjectItemInstance pi = new ProjectItemInstance(project, "i", "i" + n.ToString(), project.FullPath);
                for (int m = 0; m < 5; m++)
                {
                    pi.SetMetadata("Meta" + m.ToString(), Path.Combine(s_rootPathPrefix, "firstdirectory", "seconddirectory", "file") + m.ToString() + ".ext");
                }
                pi.SetMetadata("Meta9", Path.Combine("seconddirectory", "file.ext"));
                pi.SetMetadata("Meta10", String.Format(";{0};{1};", Path.Combine("someo%3bherplace", "foo.txt"), Path.Combine("secondd%3brectory", "file.ext")));
                pi.SetMetadata("MetaBlank", @"");

                if (n % 2 > 0)
                {
                    pi.SetMetadata("Even", "true");
                    pi.SetMetadata("Odd", "false");
                }
                else
                {
                    pi.SetMetadata("Even", "false");
                    pi.SetMetadata("Odd", "true");
                }
                ig.Add(pi);
            }

            Dictionary<string, string> itemMetadataTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            itemMetadataTable["Culture"] = "abc%253bdef;$(Gee_Aych_Ayee)";
            itemMetadataTable["Language"] = "english";
            IMetadataTable itemMetadata = new StringMetadataTable(itemMetadataTable);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg, ig, itemMetadata);

            return expander;
        }

        /// <summary>
        /// Creates an expander populated with some ProjectPropertyInstances and ProjectPropertyItems.
        /// </summary>
        /// <returns></returns>
        private Expander<ProjectPropertyInstance, ProjectItemInstance> CreateExpander()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("p", "v0"));
            pg.Set(ProjectPropertyInstance.Create("p", "v1"));

            ItemDictionary<ProjectItemInstance> ig = new ItemDictionary<ProjectItemInstance>();
            ProjectItemInstance i0 = new ProjectItemInstance(project, "i", "i0", project.FullPath);
            ProjectItemInstance i1 = new ProjectItemInstance(project, "i", "i1", project.FullPath);
            ig.Add(i0);
            ig.Add(i1);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg, ig);

            return expander;
        }

        /// <summary>
        /// Regression test for bug when there are literally zero items declared
        /// in the project, we should continue to expand item list references to empty-string
        /// rather than not expand them at all.
        /// </summary>
        [Fact]
        public void ZeroItemsInProjectExpandsToEmpty()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

                    <Target Name=`Build` Condition=`'@(foo)'!=''` >
                        <Message Text=`This target should NOT run.`/>
                    </Target>

                </Project>
                ");

            logger.AssertLogDoesntContain("This target should NOT run.");

            logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

                    <ItemGroup>
                        <foo Include=`abc` Condition=` '@(foo)' == '' ` />
                    </ItemGroup>

                    <Target Name=`Build`>
                        <Message Text=`Item list foo contains @(foo)`/>
                    </Target>

                </Project>
                ");

            logger.AssertLogContains("Item list foo contains abc");
        }

        [Fact]
        public void ItemIncludeContainsMultipleItemReferences()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project DefaultTarget=`ShowProps` ToolsVersion=`msbuilddefaulttoolsversion` xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"" >
                    <PropertyGroup>
                        <OutputType>Library</OutputType>
                    </PropertyGroup>
                    <ItemGroup>
                        <CFiles Include=`foo.c;bar.c`/>
                        <ObjFiles Include=`@(CFiles->'%(filename).obj')`/>
                        <ObjFiles Include=`@(CPPFiles->'%(filename).obj')`/>
                        <CleanFiles Condition=`'$(OutputType)'=='Library'` Include=`@(ObjFiles);@(TargetLib)`/>
                    </ItemGroup>
                    <Target Name=`ShowProps`>
                        <Message Text=`Property OutputType=$(OutputType)`/>
                        <Message Text=`Item ObjFiles=@(ObjFiles)`/>
                        <Message Text=`Item CleanFiles=@(CleanFiles)`/>
                    </Target>
                </Project>
                ");

            logger.AssertLogContains("Property OutputType=Library");
            logger.AssertLogContains("Item ObjFiles=foo.obj;bar.obj");
            logger.AssertLogContains("Item CleanFiles=foo.obj;bar.obj");
        }

#if FEATURE_LEGACY_GETFULLPATH
        /// <summary>
        /// Bad path when getting metadata through ->Metadata function
        /// </summary>
        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsMaxPathLegacyWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void InvalidPathAndMetadataItemFunctionPathTooLong()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + new string('x', 250) + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->Metadata('FullPath'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }
#endif

        /// <summary>
        /// Bad path with illegal windows chars when getting metadata through ->Metadata function
        /// </summary>
        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void InvalidPathAndMetadataItemFunctionInvalidWindowsPathChars()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + ":|?*" + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->Metadata('FullPath'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

        /// <summary>
        /// Asking for blank metadata
        /// </summary>
        [Fact]
        public void InvalidMetadataName()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='x'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->Metadata(''))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

#if FEATURE_LEGACY_GETFULLPATH
        /// <summary>
        /// Bad path when getting metadata through ->WithMetadataValue function
        /// </summary>
        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsMaxPathLegacyWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void InvalidPathAndMetadataItemFunctionPathTooLong2()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + new string('x', 250) + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->WithMetadataValue('FullPath', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }
#endif

        /// <summary>
        /// Bad path with illegal windows chars when getting metadata through ->WithMetadataValue function
        /// </summary>
        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void InvalidPathAndMetadataItemFunctionInvalidWindowsPathChars2()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + ":|?*" + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->WithMetadataValue('FullPath', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

        /// <summary>
        /// Asking for blank metadata with ->WithMetadataValue
        /// </summary>
        [Fact]
        public void InvalidMetadataName2()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='x'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->WithMetadataValue('', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

#if FEATURE_LEGACY_GETFULLPATH
        /// <summary>
        /// Bad path when getting metadata through ->AnyHaveMetadataValue function
        /// </summary>
        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsMaxPathLegacyWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        public void InvalidPathAndMetadataItemFunctionPathTooLong3()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + new string('x', 250) + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->AnyHaveMetadataValue('FullPath', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }
#endif

        /// <summary>
        /// Bad path with illegal windows chars when getting metadata through ->AnyHaveMetadataValue function
        /// </summary>
        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void InvalidPathAndMetadataItemInvalidWindowsPathChars3()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + ":|?*" + @"'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->AnyHaveMetadataValue('FullPath', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, ".NET Core 2.1+ no longer validates paths: https://github.com/dotnet/corefx/issues/27779#issuecomment-371253486")]
        public void InvalidPathInDirectMetadata()
        {
            var logger = Helpers.BuildProjectContentUsingBuildManagerExpectResult(
                @"<Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include=':|?*'>
                            <m>%(FullPath)</m>
                        </x>
                    </ItemGroup>
                </Project>",
                BuildResultCode.Failure);

            logger.AssertLogContains("MSB4248");
        }

        [ConditionalFact(typeof(NativeMethodsShared), nameof(NativeMethodsShared.IsMaxPathLegacyWindows))]
        [PlatformSpecific(TestPlatforms.Windows)]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, "new enough dotnet.exe transparently opts into long paths")]
        public void PathTooLongInDirectMetadata()
        {
            var logger = Helpers.BuildProjectContentUsingBuildManagerExpectResult(
                @"<Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='" + new string('x', 250) + @"'>
                            <m>%(FullPath)</m>
                        </x>
                    </ItemGroup>
                </Project>",
                BuildResultCode.Failure);

            logger.AssertLogContains("MSB4248");
        }

        /// <summary>
        /// Asking for blank metadata with ->AnyHaveMetadataValue
        /// </summary>
        [Fact]
        public void InvalidMetadataName3()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <ItemGroup>
                        <x Include='x'/>
                    </ItemGroup>
                    <Target Name='Build'>
                        <Message Text=""@(x->AnyHaveMetadataValue('', 'x'))"" />
                    </Target>
                </Project>", false);

            logger.AssertLogContains("MSB4023");
        }

        /// <summary>
        /// Filter by metadata presence
        /// </summary>
        [Fact]
        public void HasMetadata()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

  <ItemGroup>
    <_Item Include=""One"">
      <A>aa</A>
      <B>bb</B>
      <C>cc</C>
    </_Item>
    <_Item Include=""Two"">
      <B>bb</B>
      <C>cc</C>
    </_Item>
    <_Item Include=""Three"">
      <A>aa</A>
      <C>cc</C>
    </_Item>
    <_Item Include=""Four"">
      <A>aa</A>
      <B>bb</B>
      <C>cc</C>
    </_Item>
    <_Item Include=""Five"">
      <A></A>
    </_Item>
  </ItemGroup>

  <Target Name=""AfterBuild"">
    <Message Text=""[@(_Item->HasMetadata('a'), '|')]""/>
  </Target>


</Project>");

            logger.AssertLogContains("[One|Three|Four]");
        }

        [Fact]
        public void DirectItemMetadataReferenceShouldBeCaseInsensitive()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project>
                  <ItemGroup>
                    <Foo Include=`Foo`>
                      <SENSITIVE>X</SENSITIVE>
                    </Foo>
                  </ItemGroup>
                  <Target Name=`Build`>
                    <Message Importance=`high` Text=`QualifiedNotMatchCase %(Foo.FileName)=%(Foo.sensitive)`/>
                    <Message Importance=`high` Text=`QualifiedMatchCase %(Foo.FileName)=%(Foo.SENSITIVE)`/>
                    
                    <Message Importance=`high` Text=`UnqualifiedNotMatchCase %(Foo.FileName)=%(sensitive)`/>
                    <Message Importance=`high` Text=`UnqualifiedMatchCase %(Foo.FileName)=%(SENSITIVE)`/>
                  </Target>
                </Project>
                ");

            logger.AssertLogContains("QualifiedNotMatchCase Foo=X");
            logger.AssertLogContains("QualifiedMatchCase Foo=X");
            logger.AssertLogContains("UnqualifiedNotMatchCase Foo=X");
            logger.AssertLogContains("UnqualifiedMatchCase Foo=X");
        }

        [Fact]
        public void ItemDefinitionGroupMetadataReferenceShouldBeCaseInsensitive()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project>
                  <ItemDefinitionGroup>
                    <Foo>
                        <SENSITIVE>X</SENSITIVE>
                    </Foo>
                  </ItemDefinitionGroup>
                  <ItemGroup>
                    <Foo Include=`Foo`/>
                  </ItemGroup>
                  <Target Name=`Build`>
                    <Message Importance=`high` Text=`QualifiedNotMatchCase %(Foo.FileName)=%(Foo.sensitive)`/>
                    <Message Importance=`high` Text=`QualifiedMatchCase %(Foo.FileName)=%(Foo.SENSITIVE)`/>
                    
                    <Message Importance=`high` Text=`UnqualifiedNotMatchCase %(Foo.FileName)=%(sensitive)`/>
                    <Message Importance=`high` Text=`UnqualifiedMatchCase %(Foo.FileName)=%(SENSITIVE)`/>
                  </Target>
                </Project>
                ");

            logger.AssertLogContains("QualifiedNotMatchCase Foo=X");
            logger.AssertLogContains("QualifiedMatchCase Foo=X");
            logger.AssertLogContains("UnqualifiedNotMatchCase Foo=X");
            logger.AssertLogContains("UnqualifiedMatchCase Foo=X");
        }

        [Fact]
        public void WellKnownMetadataReferenceShouldBeCaseInsensitive()
        {
            MockLogger logger = Helpers.BuildProjectWithNewOMExpectSuccess(@"
                <Project>
                  <ItemGroup>
                    <Foo Include=`Foo`/>
                  </ItemGroup>
                  <Target Name=`Build`>
                    <Message Importance=`high` Text=`QualifiedNotMatchCase %(Foo.Identity)=%(Foo.FILENAME)`/>
                    <Message Importance=`high` Text=`QualifiedMatchCase %(Foo.Identity)=%(Foo.FileName)`/>
                    
                    <Message Importance=`high` Text=`UnqualifiedNotMatchCase %(Foo.Identity)=%(FILENAME)`/>
                    <Message Importance=`high` Text=`UnqualifiedMatchCase %(Foo.Identity)=%(FileName)`/>
                  </Target>
                </Project>
                ");

            logger.AssertLogContains("QualifiedNotMatchCase Foo=Foo");
            logger.AssertLogContains("QualifiedMatchCase Foo=Foo");
            logger.AssertLogContains("UnqualifiedNotMatchCase Foo=Foo");
            logger.AssertLogContains("UnqualifiedMatchCase Foo=Foo");
        }

        /// <summary>
        /// Verify when there is an error due to an attempt to use a static method that we report the method name
        /// </summary>
        [Fact]
        public void StaticMethodErrorMessageHaveMethodName()
        {
            try
            {
                Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <PropertyGroup>
                        <Function>$([System.IO.Path]::Combine(null,''))</Function>
                    </PropertyGroup>
                    <Target Name='Build'>
                        <Message Text='[ $(Function) ]' />
                    </Target>
                </Project>", false);
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException e)
            {
                Assert.NotEqual(-1, e.Message.IndexOf("[System.IO.Path]::Combine(null, '')", StringComparison.OrdinalIgnoreCase));
                return;
            }

            Assert.True(false);
        }

        /// <summary>
        /// Verify when there is an error due to an attempt to use a static method that we report the method name
        /// </summary>
        [Fact]
        public void StaticMethodErrorMessageHaveMethodName1()
        {
            try
            {
                Helpers.BuildProjectWithNewOMExpectFailure(@"
                <Project DefaultTargets='Build' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
                    <PropertyGroup>
                        <Function>$(System.IO.Path::Combine('a','b'))</Function>
                    </PropertyGroup>
                    <Target Name='Build'>
                        <Message Text='[ $(Function) ]' />
                    </Target>
                </Project>", false);
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException e)
            {
                Assert.NotEqual(-1, e.Message.IndexOf("System.IO.Path::Combine('a','b')", StringComparison.OrdinalIgnoreCase));
                return;
            }

            Assert.True(false);
        }
        /// <summary>
        /// Creates a set of complicated item metadata and properties, and items to exercise
        /// the Expander class.  The data here contains escaped characters, metadata that
        /// references properties, properties that reference items, and other complex scenarios.
        /// </summary>
        /// <param name="pg"></param>
        /// <param name="primaryItemsByName"></param>
        /// <param name="secondaryItemsByName"></param>
        /// <param name="itemMetadata"></param>
        private void CreateComplexPropertiesItemsMetadata
            (
            out Lookup readOnlyLookup,
            out StringMetadataTable itemMetadata
            )
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            Dictionary<string, string> itemMetadataTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            itemMetadataTable["Culture"] = "abc%253bdef;$(Gee_Aych_Ayee)";
            itemMetadataTable["Language"] = "english";
            itemMetadata = new StringMetadataTable(itemMetadataTable);

            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Gee_Aych_Ayee", "ghi"));
            pg.Set(ProjectPropertyInstance.Create("OutputPath", @"\jk ; l\mno%253bpqr\stu"));
            pg.Set(ProjectPropertyInstance.Create("TargetPath", "@(IntermediateAssembly->'%(RelativeDir)')"));

            List<ProjectItemInstance> intermediateAssemblyItemGroup = new List<ProjectItemInstance>();
            ProjectItemInstance i1 = new ProjectItemInstance(project, "IntermediateAssembly",
                NativeMethodsShared.IsWindows ? @"subdir1\engine.dll" : "subdir1/engine.dll", project.FullPath);
            intermediateAssemblyItemGroup.Add(i1);
            i1.SetMetadata("aaa", "111");
            ProjectItemInstance i2 = new ProjectItemInstance(project, "IntermediateAssembly",
                NativeMethodsShared.IsWindows ? @"subdir2\tasks.dll" : "subdir2/tasks.dll", project.FullPath);
            intermediateAssemblyItemGroup.Add(i2);
            i2.SetMetadata("bbb", "222");

            List<ProjectItemInstance> contentItemGroup = new List<ProjectItemInstance>();
            ProjectItemInstance i3 = new ProjectItemInstance(project, "Content", "splash.bmp", project.FullPath);
            contentItemGroup.Add(i3);
            i3.SetMetadata("ccc", "333");

            List<ProjectItemInstance> resourceItemGroup = new List<ProjectItemInstance>();
            ProjectItemInstance i4 = new ProjectItemInstance(project, "Resource", "string$(p).resx", project.FullPath);
            resourceItemGroup.Add(i4);
            i4.SetMetadata("ddd", "444");
            ProjectItemInstance i5 = new ProjectItemInstance(project, "Resource", "dialogs%253b.resx", project.FullPath);
            resourceItemGroup.Add(i5);
            i5.SetMetadata("eee", "555");

            List<ProjectItemInstance> contentItemGroup2 = new List<ProjectItemInstance>();
            ProjectItemInstance i6 = new ProjectItemInstance(project, "Content", "about.bmp", project.FullPath);
            contentItemGroup2.Add(i6);
            i6.SetMetadata("fff", "666");

            ItemDictionary<ProjectItemInstance> secondaryItemsByName = new ItemDictionary<ProjectItemInstance>();
            secondaryItemsByName.ImportItems(resourceItemGroup);
            secondaryItemsByName.ImportItems(contentItemGroup2);

            Lookup lookup = new Lookup(secondaryItemsByName, pg);

            // Add primary items
            lookup.EnterScope("x");
            lookup.PopulateWithItems("IntermediateAssembly", intermediateAssemblyItemGroup);
            lookup.PopulateWithItems("Content", contentItemGroup);

            readOnlyLookup = lookup;
        }

        /// <summary>
        /// Exercises ExpandAllIntoTaskItems with a complex set of data.
        /// </summary>
        [Fact]
        public void ExpandAllIntoTaskItemsComplex()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            IList<TaskItem> taskItems = expander.ExpandIntoTaskItemsLeaveEscaped(
                "@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ; $(NonExistent) ; %(NonExistent) ; " +
                "$(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)",
                 ExpanderOptions.ExpandAll, MockElementLocation.Instance);

            // the following items are passed to the TaskItem constructor, and thus their ItemSpecs should be
            // in escaped form.
            ObjectModelHelpers.AssertItemsMatch(@"
                string$(p): ddd=444
                dialogs%253b: eee=555
                splash.bmp: ccc=333
                \jk
                l\mno%253bpqr\stu
                subdir1" + Path.DirectorySeparatorChar + @": aaa=111
                subdir2" + Path.DirectorySeparatorChar + @": bbb=222
                english_abc%253bdef
                ghi
                ", GetTaskArrayFromItemList(taskItems));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with a complex set of data but in a piecemeal fashion
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringComplexPiecemeal()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            string stringToExpand = "@(Resource->'%(Filename)') ;";
            Assert.Equal(
                @"string$(p);dialogs%3b ;",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "@(Content)";
            Assert.Equal(
                @"splash.bmp",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "@(NonExistent)";
            Assert.Equal(
                @"",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "$(NonExistent)";
            Assert.Equal(
                @"",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "%(NonExistent)";
            Assert.Equal(
                @"",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "$(OutputPath)";
            Assert.Equal(
                @"\jk ; l\mno%3bpqr\stu",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "$(TargetPath)";
            Assert.Equal(
                "subdir1" + Path.DirectorySeparatorChar + ";subdir2" + Path.DirectorySeparatorChar,
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            stringToExpand = "%(Language)_%(Culture)";
            Assert.Equal(
                @"english_abc%3bdef;ghi",
                expander.ExpandIntoStringAndUnescape(stringToExpand, ExpanderOptions.ExpandAll, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with an item list using a transform that is empty
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringEmpty()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            XmlAttribute xmlattribute = (new XmlDocument()).CreateAttribute("dummy");
            xmlattribute.Value = "@(IntermediateAssembly->'')";

            Assert.Equal(
                @";",
                expander.ExpandIntoStringAndUnescape(xmlattribute.Value, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            xmlattribute.Value = "@(IntermediateAssembly->'%(goop)')";

            Assert.Equal(
                @";",
                expander.ExpandIntoStringAndUnescape(xmlattribute.Value, ExpanderOptions.ExpandAll, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with a complex set of data.
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringComplex()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            XmlAttribute xmlattribute = (new XmlDocument()).CreateAttribute("dummy");
            xmlattribute.Value = "@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ; $(NonExistent) ; %(NonExistent) ; " +
                "$(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)";

            Assert.Equal(
                @"string$(p);dialogs%3b ; splash.bmp ;  ;  ;  ; \jk ; l\mno%3bpqr\stu ; subdir1" + Path.DirectorySeparatorChar + ";subdir2" +
                Path.DirectorySeparatorChar + " ; english_abc%3bdef;ghi",
                expander.ExpandIntoStringAndUnescape(xmlattribute.Value, ExpanderOptions.ExpandAll, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with a complex set of data.
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringLeaveEscapedComplex()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            XmlAttribute xmlattribute = (new XmlDocument()).CreateAttribute("dummy");
            xmlattribute.Value = "@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ; $(NonExistent) ; %(NonExistent) ; " +
                "$(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)";

            Assert.Equal(
                @"string$(p);dialogs%253b ; splash.bmp ;  ;  ;  ; \jk ; l\mno%253bpqr\stu ; subdir1" + Path.DirectorySeparatorChar + ";subdir2" + Path.DirectorySeparatorChar + " ; english_abc%253bdef;ghi",
                expander.ExpandIntoStringLeaveEscaped(xmlattribute.Value, ExpanderOptions.ExpandAll, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandIntoStringAndUnescape and ExpanderOptions.Truncate
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringTruncated()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            var manySpaces = "".PadLeft(2000);
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("ManySpacesProperty", manySpaces));
            var itemMetadataTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ManySpacesMetadata", manySpaces }
            };
            var itemMetadata = new StringMetadataTable(itemMetadataTable);
            var projectItemGroups = new ItemDictionary<ProjectItemInstance>();
            var itemGroup = new List<ProjectItemInstance>();
            for (int i = 0; i < 50; i++)
            {
                var item = new ProjectItemInstance(project, "ManyItems", $"ThisIsAFairlyLongFileName_{i}.bmp", project.FullPath);
                item.SetMetadata("Foo", $"ThisIsAFairlyLongMetadataValue_{i}");
                itemGroup.Add(item);
            }
            var lookup = new Lookup(projectItemGroups, pg);
            lookup.EnterScope("x");
            lookup.PopulateWithItems("ManySpacesItem", new []
            {
                new ProjectItemInstance (project, "ManySpacesItem", "Foo", project.FullPath),
                new ProjectItemInstance (project, "ManySpacesItem", manySpaces, project.FullPath),
                new ProjectItemInstance (project, "ManySpacesItem", "Bar", project.FullPath),
            });
            lookup.PopulateWithItems("Exactly1024", new[]
            {
                new ProjectItemInstance (project, "Exactly1024", "".PadLeft(1024), project.FullPath),
                new ProjectItemInstance (project, "Exactly1024", "Foo", project.FullPath),
            });
            lookup.PopulateWithItems("ManyItems", itemGroup);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            XmlAttribute xmlattribute = (new XmlDocument()).CreateAttribute("dummy");
            xmlattribute.Value = "'%(ManySpacesMetadata)' != '' and '$(ManySpacesProperty)' != '' and '@(ManySpacesItem)' != '' and '@(Exactly1024)' != '' and '@(ManyItems)' != '' and '@(ManyItems->'%(Foo)')' != '' and '@(ManyItems->'%(Nonexistent)')' != ''";

            var expected =
                $"'{"",1021}...' != '' and " +
                $"'{"",1021}...' != '' and " +
                $"'Foo;{"",1017}...' != '' and " +
                $"'{"",1024};...' != '' and " +
                "'ThisIsAFairlyLongFileName_0.bmp;ThisIsAFairlyLongFileName_1.bmp;ThisIsAFairlyLongFileName_2.bmp;...' != '' and " +
                "'ThisIsAFairlyLongMetadataValue_0;ThisIsAFairlyLongMetadataValue_1;ThisIsAFairlyLongMetadataValue_2;...' != '' and " +
                $"';;;...' != ''";
            // NOTE: semicolons in the last part are *weird* because they don't actually mean anything and you get logging like
            //     Target "Build" skipped, due to false condition; ( '@(I->'%(nonexistent)')' == '' ) was evaluated as ( ';' == '' ).
            // but that goes back to MSBuild 4.something so I'm codifying it in this test. If you're here because you cleaned it up
            // and want to fix the test my current opinion is that's fine.

            Assert.Equal(expected, expander.ExpandIntoStringAndUnescape(xmlattribute.Value, ExpanderOptions.ExpandAll | ExpanderOptions.Truncate, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with a string that does not need expanding.
        /// In this case the expanded string should be reference identical to the passed in string.
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringExpectIdenticalReference()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            XmlAttribute xmlattribute = (new XmlDocument()).CreateAttribute("dummy");

            // Create a *non-literal* string. If we used a literal string, the CLR might (would) intern
            // it, which would mean that Expander would inevitably return a reference to the same string.
            // In real builds, the strings will never be literals, and we want to test the behavior in
            // that situation.
            xmlattribute.Value = "abc123" + new Random().Next();
            string expandedString = expander.ExpandIntoStringLeaveEscaped(xmlattribute.Value, ExpanderOptions.ExpandAll, MockElementLocation.Instance);

#if FEATURE_STRING_INTERN
            // Verify neither string got interned, so that this test is meaningful
            Assert.Null(string.IsInterned(xmlattribute.Value));
            Assert.Null(string.IsInterned(expandedString));
#endif

            // Finally verify Expander indeed didn't create a new string.
            Assert.True(Object.ReferenceEquals(xmlattribute.Value, expandedString));
        }

        /// <summary>
        /// Exercises ExpandAllIntoString with a complex set of data and various expander options
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringExpanderOptions()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            string value = @"@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ; $(NonExistent) ; %(NonExistent) ; $(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)";

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            Assert.Equal(@"@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ;  ; %(NonExistent) ; \jk ; l\mno%3bpqr\stu ; @(IntermediateAssembly->'%(RelativeDir)') ; %(Language)_%(Culture)", expander.ExpandIntoStringAndUnescape(value, ExpanderOptions.ExpandProperties, MockElementLocation.Instance));

            Assert.Equal(@"@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ;  ;  ; \jk ; l\mno%3bpqr\stu ; @(IntermediateAssembly->'%(RelativeDir)') ; english_abc%3bdef;ghi", expander.ExpandIntoStringAndUnescape(value, ExpanderOptions.ExpandPropertiesAndMetadata, MockElementLocation.Instance));

            Assert.Equal(@"string$(p);dialogs%3b ; splash.bmp ;  ;  ;  ; \jk ; l\mno%3bpqr\stu ; subdir1" + Path.DirectorySeparatorChar + ";subdir2" + Path.DirectorySeparatorChar + " ; english_abc%3bdef;ghi", expander.ExpandIntoStringAndUnescape(value, ExpanderOptions.ExpandAll, MockElementLocation.Instance));

            Assert.Equal(@"string$(p);dialogs%3b ; splash.bmp ;  ; $(NonExistent) ; %(NonExistent) ; $(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)", expander.ExpandIntoStringAndUnescape(value, ExpanderOptions.ExpandItems, MockElementLocation.Instance));
        }

        /// <summary>
        /// Exercises ExpandAllIntoStringListLeaveEscaped with a complex set of data.
        /// </summary>
        [Fact]
        public void ExpandAllIntoStringListLeaveEscapedComplex()
        {
            Lookup lookup;
            StringMetadataTable itemMetadata;
            CreateComplexPropertiesItemsMetadata(out lookup, out itemMetadata);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(lookup, lookup, itemMetadata);

            string value = "@(Resource->'%(Filename)') ; @(Content) ; @(NonExistent) ; $(NonExistent) ; %(NonExistent) ; " +
                "$(OutputPath) ; $(TargetPath) ; %(Language)_%(Culture)";

            IList<string> expanded = expander.ExpandIntoStringListLeaveEscaped(value, ExpanderOptions.ExpandAll, MockElementLocation.Instance).ToList();

            Assert.Equal(9, expanded.Count);
            Assert.Equal(@"string$(p)", expanded[0]);
            Assert.Equal(@"dialogs%253b", expanded[1]);
            Assert.Equal(@"splash.bmp", expanded[2]);
            Assert.Equal(@"\jk", expanded[3]);
            Assert.Equal(@"l\mno%253bpqr\stu", expanded[4]);
            Assert.Equal("subdir1" + Path.DirectorySeparatorChar, expanded[5]);
            Assert.Equal("subdir2" + Path.DirectorySeparatorChar, expanded[6]);
            Assert.Equal(@"english_abc%253bdef", expanded[7]);
            Assert.Equal(@"ghi", expanded[8]);
        }

        internal ITaskItem[] GetTaskArrayFromItemList(IList<TaskItem> list)
        {
            ITaskItem[] items = new ITaskItem[list.Count];
            for (int i = 0; i < list.Count; ++i)
            {
                items[i] = list[i];
            }

            return items;
        }

        /// <summary>
        /// v10.0\TeamData\Microsoft.Data.Schema.Common.targets shipped with bad syntax:
        /// $(HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@VSTSDBDirectory)
        /// this was evaluating to blank before, now it errors; we have to special case it to
        /// evaluate to blank.
        /// Note that this still works whether or not the key exists and has a value.
        /// </summary>
        [Fact]
        public void RegistryPropertyInvalidPrefixSpecialCase()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$(HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@VSTSDBDirectory)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(String.Empty, result);
        }

        // Compat hack: WebProjects may have an import with a condition like:
        //       Condition=" '$(Solutions.VSVersion)' == '8.0'"
        // These would have been '' in prior versions of msbuild but would be treated as a possible string function in current versions.
        // Be compatible by returning an empty string here.
        [Fact]
        public void Regress692569()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$(Solutions.VSVersion)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(String.Empty, result);
        }

        /// <summary>
        /// In the general case, we should still error for properties that incorrectly miss the Registry: prefix.
        /// Note that this still fails whether or not the key exists.
        /// </summary>
        [Fact]
        public void RegistryPropertyInvalidPrefixError()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped(@"$(HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@XXXXDBDirectory)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// In the general case, we should still error for properties that incorrectly miss the Registry: prefix, like
        /// the special case, but with extra char on the end.
        /// Note that this still fails whether or not the key exists.
        /// </summary>
        [Fact]
        public void RegistryPropertyInvalidPrefixError2()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped(@"$(HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@VSTSDBDirectoryX)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
#if FEATURE_WIN32_REGISTRY
        [Fact]
        public void RegistryPropertyString()
        {
            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", "String", RegistryValueKind.String);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("String", result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void RegistryPropertyBinary()
        {
            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                UTF8Encoding enc = new UTF8Encoding();
                byte[] utfText = enc.GetBytes("String".ToCharArray());

                key.SetValue("Value", utfText, RegistryValueKind.Binary);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("83;116;114;105;110;103", result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void RegistryPropertyDWord()
        {
            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", 123456, RegistryValueKind.DWord);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("123456", result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void RegistryPropertyExpandString()
        {
            try
            {
                string envVar = NativeMethodsShared.IsWindows ? "TEMP" : "USER";
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", "%" + envVar + "%", RegistryValueKind.ExpandString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Environment.GetEnvironmentVariable(envVar), result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void RegistryPropertyQWord()
        {
            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", (long)123456789123456789, RegistryValueKind.QWord);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("123456789123456789", result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void RegistryPropertyMultiString()
        {
            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", new string[] { "A", "B", "C", "D" }, RegistryValueKind.MultiString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$(Registry:HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test@Value)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("A;B;C;D", result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }
#endif

        [Fact]
        public void TestItemSpecModiferEscaping()
        {
            string content = @"
 <Project DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

        <Target Name=""Build"">
            <WriteLinesToFile Overwrite=""true"" File=""unittest.%28msbuild%29.file"" Lines=""Nothing much here""/>

            <ItemGroup>
                <TestFile Include=""unittest.%28msbuild%29.file"" />
            </ItemGroup>

            <Message Text=""@(TestFile->FullPath())"" />
            <Message Text=""@(TestFile->'%(FullPath)'->Distinct())"" />
            <Delete Files=""unittest.%28msbuild%29.file"" />
        </Target>
</Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogDoesntContain("%28");
            log.AssertLogDoesntContain("%29");
        }

        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void TestGetPathToReferenceAssembliesAsFunction()
        {
            if (ToolLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies(TargetDotNetFrameworkVersion.Version45) == null)
            {
                // if there aren't any reference assemblies installed on the machine in the first place, of course
                // we're not going to find them. :)
                return;
            }

            string content = @"
                <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

                    <PropertyGroup>
                        <TargetFrameworkIdentifier>.NETFramework</TargetFrameworkIdentifier>
                        <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>
                        <TargetFrameworkProfile></TargetFrameworkProfile>
                        <TargetFrameworkMoniker>$(TargetFrameworkIdentifier),Version=$(TargetFrameworkVersion)</TargetFrameworkMoniker>
                    </PropertyGroup>

                    <Target Name=""Build"">
                        <GetReferenceAssemblyPaths
                            Condition="" '$(TargetFrameworkDirectory)' == '' and '$(TargetFrameworkMoniker)' !=''""
                            TargetFrameworkMoniker=""$(TargetFrameworkMoniker)""
                            RootPath=""$(TargetFrameworkRootPath)""
                        >
                            <Output TaskParameter=""ReferenceAssemblyPaths"" PropertyName=""ReferenceAssemblyPathsFromTask""/>
                        </GetReferenceAssemblyPaths>

                        <PropertyGroup>
                            <ReferenceAssemblyPathsFromFunction>$([Microsoft.Build.Utilities.ToolLocationHelper]::GetPathToStandardLibraries($(TargetFrameworkIdentifier), $(TargetFrameworkVersion), $(TargetFrameworkProfile)))\</ReferenceAssemblyPathsFromFunction>
                        </PropertyGroup>

                        <Message Text=""Task:     $(ReferenceAssemblyPathsFromTask)"" Importance=""High"" />
                        <Message Text=""Function: $(ReferenceAssemblyPathsFromFunction)"" Importance=""High"" />

                        <Warning Text=""Reference assembly paths do not match!"" Condition=""'$(ReferenceAssemblyPathsFromFunction)' != '$(ReferenceAssemblyPathsFromTask)'"" />
                    </Target>

                </Project>
                ";

            MockLogger log = Helpers.BuildProjectWithNewOMExpectSuccess(content);

            log.AssertLogDoesntContain("Reference assembly paths do not match");
        }

        /// <summary>
        /// Expand property function that takes a null argument
        /// </summary>
        [Fact]
        public void PropertyFunctionNullArgument()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$([System.Convert]::ChangeType('null',$(SomeStuff.GetTypeCode())))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("null", result);
        }

        /// <summary>
        /// Expand property function that returns a null
        /// </summary>
        [Fact]
        public void PropertyFunctionNullReturn()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            // The null-returning function is the only thing in the expression.
            string result = expander.ExpandIntoStringLeaveEscaped("$([System.Environment]::GetEnvironmentVariable(`_NonExistentVar`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal("", result);

            // The result of the null-returning function is concatenated with a non-empty string.
            result = expander.ExpandIntoStringLeaveEscaped("prefix_$([System.Environment]::GetEnvironmentVariable(`_NonExistentVar`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal("prefix_", result);
        }

        /// <summary>
        /// Expand property function that takes no arguments and returns a string
        /// </summary>
        [Fact]
        public void PropertyFunctionNoArguments()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.ToUpperInvariant())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("THIS IS SOME STUFF", result);
        }

        /// <summary>
        /// Expand property function that takes no arguments and returns a string (trimmed)
        /// </summary>
        [Fact]
        public void PropertyFunctionNoArgumentsTrim()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("FileName", "    foo.ext   "));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(FileName.Trim())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("foo.ext", result);
        }

        /// <summary>
        /// Expand property function that is a get property accessor
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyGet()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.Length)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("18", result);
        }

        /// <summary>
        /// Expand property function which is a manual get property accessor
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyManualGet()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.get_Length())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("18", result);
        }

        /// <summary>
        /// Expand property function which is a manual get property accessor and a concatenation of a constant
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyNoArgumentsConcat()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.ToLowerInvariant())_goop", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("this is some stuff_goop", result);
        }

        /// <summary>
        /// Expand property function with a constant argument
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgument()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.SubString(13))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("STUff", result);
        }

        /// <summary>
        /// Expand property function with a constant argument that contains spaces
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentWithSpaces()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.SubString(8))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("SOME STUff", result);
        }

        /// <summary>
        /// Expand property function with a constant argument
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyPathRootSubtraction()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("RootPath", Path.Combine(s_rootPathPrefix, "this", "is", "the", "root")));
            pg.Set(ProjectPropertyInstance.Create("MyPath", Path.Combine(s_rootPathPrefix, "this", "is", "the", "root", "my", "project", "is", "here.proj")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(MyPath.SubString($(RootPath.Length)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(Path.DirectorySeparatorChar.ToString(), "my", "project", "is", "here.proj"), result);
        }

        /// <summary>
        /// Expand property function with an argument that is a property
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentExpandedProperty()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Value", "3"));
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.SubString(1$(Value)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("STUff", result);
        }

        /// <summary>
        /// Expand property function that has a boolean return value
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentBooleanReturn()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("PathRoot", Path.Combine(s_rootPathPrefix, "goo")));
            pg.Set(ProjectPropertyInstance.Create("PathRoot2", Path.Combine(s_rootPathPrefix, "goop") + Path.DirectorySeparatorChar));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$(PathRoot2.Endswith(" + Path.DirectorySeparatorChar + "))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal("True", result);
            result = expander.ExpandIntoStringLeaveEscaped(@"$(PathRoot.Endswith(" + Path.DirectorySeparatorChar + "))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal("False", result);
        }

        /// <summary>
        /// Expand property function with an argument that is expanded, and a chaining of other functions.
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentNestedAndChainedFunction()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Value", "3"));
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.SubString(1$(Value)).ToLowerInvariant().SubString($(Value)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("ff", result);
        }


        /// <summary>
        /// Expand property function with chained functions on its results
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentChained()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Value", "3"));
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.ToUpperInvariant().ToLowerInvariant())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal("this is some stuff", result);
        }

        /// <summary>
        /// Expand property function with an argument that is a function
        /// </summary>
        [Fact]
        public void PropertyFunctionPropertyWithArgumentNested()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Value", "12345"));
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "1234567890"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.SubString($(Value.get_Length())))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("67890", result);
        }

        /// <summary>
        /// Expand property function that returns an generic list
        /// </summary>
        [Fact]
        public void PropertyFunctionGenericListReturn()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$([MSBuild]::__GetListTest())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("A;B;C;D", result);
        }

        /// <summary>
        /// Expand property function that returns an array
        /// </summary>
        [Fact]
        public void PropertyFunctionArrayReturn()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("List", "A-B-C-D"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(List.Split(-))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("A;B;C;D", result);
        }

        /// <summary>
        /// Expand property function that returns a Dictionary
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void PropertyFunctionDictionaryReturn()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$([System.Environment]::GetEnvironmentVariables())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance).ToUpperInvariant();
            string expected = ("OS=" + Environment.GetEnvironmentVariable("OS")).ToUpperInvariant();

            Assert.Contains(expected, result);
        }

        /// <summary>
        /// Expand property function that returns an array
        /// </summary>
        [Fact]
        public void PropertyFunctionArrayReturnManualSplitter()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("List", "A-B-C-D"));
            pg.Set(ProjectPropertyInstance.Create("Splitter", "-"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(List.Split($(Splitter.ToCharArray())))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("A;B;C;D", result);
        }

        /// <summary>
        /// Expand property function that returns an array
        /// </summary>
        [Fact]
        public void PropertyFunctionInCondition()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("PathRoot", Path.Combine(s_rootPathPrefix, "goo")));
            pg.Set(ProjectPropertyInstance.Create("PathRoot2", Path.Combine(s_rootPathPrefix, "goop") + Path.DirectorySeparatorChar));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            Assert.True(
                ConditionEvaluator.EvaluateCondition(
                    @"'$(PathRoot2.Endswith(`" + Path.DirectorySeparatorChar + "`))' == 'true'",
                    ParserOptions.AllowAll,
                    expander,
                    ExpanderOptions.ExpandProperties,
                    Directory.GetCurrentDirectory(),
                    MockElementLocation.Instance,
                    null,
                    new BuildEventContext(1, 2, 3, 4),
                    FileSystems.Default));
            Assert.True(
                ConditionEvaluator.EvaluateCondition(
                    @"'$(PathRoot.EndsWith(" + Path.DirectorySeparatorChar + "))' == 'false'",
                    ParserOptions.AllowAll,
                    expander,
                    ExpanderOptions.ExpandProperties,
                    Directory.GetCurrentDirectory(),
                    MockElementLocation.Instance,
                    null,
                    new BuildEventContext(1, 2, 3, 4),
                    FileSystems.Default));
        }

        /// <summary>
        /// Expand property function that is invalid - properties don't take arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid1()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("Value", "3"));
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("[$(SomeStuff($(Value)))]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }

        /// <summary>
        /// Expand property function - invalid since properties don't have properties
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid2()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("Value", "3"));
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("[$(SomeStuff.Lgg)]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function - invalid since properties don't have properties and don't support '.' in them
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid3()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("Value", "3"));
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.ToUpperInvariant().Foo)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function - properties don't take arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid4()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("Value", "3"));
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("[$(SomeStuff($(System.DateTime.Now)))]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }

        /// <summary>
        /// Expand property function - invalid expression
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid5()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("$(SomeStuff.ToLowerInvariant()_goop)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function - functions with invalid arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid6()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("[$(SomeStuff.Substring(HELLO!))]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function - functions with invalid arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid7()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("[$(SomeStuff.Substring(-10))]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function that calls a static method with quoted arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalid8()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped("$(([System.DateTime]::Now).ToString(\"MM.dd.yyyy\"))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            }
           );
        }
        /// <summary>
        /// Expand property function - we don't handle metadata functions
        /// </summary>
        [Fact]
        public void PropertyFunctionInvalidNoMetadataFunctions()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("[%(LowerLetterList.Identity.ToUpper())]", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("[%(LowerLetterList.Identity.ToUpper())]", result);
        }

        /// <summary>
        /// Expand property function - properties won't get confused with a type or namespace
        /// </summary>
        [Fact]
        public void PropertyFunctionNoCollisionsOnType()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("System", "The System Namespace"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$(System)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("The System Namespace", result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void PropertyFunctionStaticMethodMakeRelative()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("ParentPath", Path.Combine(s_rootPathPrefix, "abc", "def")));
            pg.Set(ProjectPropertyInstance.Create("FilePath", Path.Combine(s_rootPathPrefix, "abc", "def", "foo.cpp")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::MakeRelative($(ParentPath), `$(FilePath)`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(@"foo.cpp", result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethod1()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("Drive", s_rootPathPrefix));
            pg.Set(ProjectPropertyInstance.Create("File", Path.Combine("foo", "file.txt")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine($(Drive), `$(File)`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo", "file.txt"), result);
        }

        /// <summary>
        /// Expand property function that creates an instance of a type
        /// </summary>
        [Fact]
        public void PropertyFunctionConstructor1()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("ver1", @"1.2.3.4"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            object result = expander.ExpandPropertiesLeaveTypedAndEscaped(@"$([System.Version]::new($(ver1)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.IsType<Version>(result);

            Version v = (Version)result;

            Assert.Equal(1, v.Major);
            Assert.Equal(2, v.Minor);
            Assert.Equal(3, v.Build);
            Assert.Equal(4, v.Revision);
        }

        /// <summary>
        /// Expand property function that creates an instance of a type
        /// </summary>
        [Fact]
        public void PropertyFunctionConstructor2()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("ver1", @"1.2.3.4"));
            pg.Set(ProjectPropertyInstance.Create("ver2", @"2.2.3.4"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Version]::new($(ver1)).CompareTo($([System.Version]::new($(ver2)))))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(@"-1", result);
        }

        /// <summary>
        /// Expand property function that is only available when MSBUILDENABLEALLPROPERTYFUNCTIONS=1
        /// </summary>
        [Fact]
        [SkipOnTargetFramework(TargetFrameworkMonikers.Netcoreapp, "https://github.com/dotnet/coreclr/issues/15662")]
        public void PropertyStaticFunctionAllEnabled()
        {
            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", "1");

                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                try
                {
                    string result = expander.ExpandIntoStringLeaveEscaped("$([System.Type]::GetType(`System.Type`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                    Assert.Equal("System.Type", result);
                }
                finally
                {
                    AvailableStaticMethods.Reset_ForUnitTestsOnly();
                }
            }
        }

        /// <summary>
        /// Expand property function that is defined (on CoreFX) in an assembly named after its full namespace.
        /// </summary>
        [Fact]
        public void PropertyStaticFunctionLocatedFromAssemblyWithNamespaceName()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string env = Environment.GetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS");

            try
            {
                Environment.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", "1");

                string result = expander.ExpandIntoStringLeaveEscaped("$([System.Diagnostics.Process]::GetCurrentProcess().Id)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                int pid;
                Assert.True(int.TryParse(result, out pid));
                Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, pid);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", env);
                AvailableStaticMethods.Reset_ForUnitTestsOnly();
            }
        }

        /// <summary>
        /// Expand property function that is only available when MSBUILDENABLEALLPROPERTYFUNCTIONS=1, but cannot be found
        /// </summary>
        [Fact]
        public void PropertyStaticFunctionUsingNamespaceNotFound()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string env = Environment.GetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS");

            try
            {
                Environment.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", "1");

                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([Microsoft.FOO.FileIO.FileSystem]::CurrentDirectory)", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([Foo.Baz]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([Foo]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([Foo.]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([.Foo]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([.]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
                Assert.Throws<InvalidProjectFileException>(() => expander.ExpandIntoStringLeaveEscaped("$([]::new())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS", env);
                AvailableStaticMethods.Reset_ForUnitTestsOnly();
            }
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void PropertyFunctionStaticMethodQuoted1()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", Path.Combine("foo", "file.txt")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine(`" + s_rootPathPrefix + "`, `$(File)`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo", "file.txt"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted1Spaces()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", "foo goo" + Path.DirectorySeparatorChar + "file.txt"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine(`" +
                Path.Combine(s_rootPathPrefix, "foo goo")  + "`, `$(File)`))",
                ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo goo", "foo goo", "file.txt"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted1Spaces2()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", Path.Combine("foo bar", "baz.txt")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine(`" +
                Path.Combine(s_rootPathPrefix, "foo baz") + @"`, `$(File)`))",
                ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo baz", "foo bar", "baz.txt"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted1Spaces3()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", Path.Combine("foo bar", "baz.txt")));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine(`" +
                Path.Combine(s_rootPathPrefix, "foo baz") + @" `, `$(File)`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo baz ", "foo bar", "baz.txt"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method with quoted arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted2()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string dateTime = "'" + _dateToParse + "'";
            string result = expander.ExpandIntoStringLeaveEscaped("$([System.DateTime]::Parse(" + dateTime + ").ToString(\"yyyy/MM/dd HH:mm:ss\"))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(System.DateTime.Parse(_dateToParse).ToString("yyyy/MM/dd HH:mm:ss"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method with quoted arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted3()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
            string dateTime = "'" + _dateToParse + "'";
            string result = expander.ExpandIntoStringLeaveEscaped("$([System.DateTime]::Parse(" + dateTime + ").ToString(\"MM.dd.yyyy\"))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(System.DateTime.Parse(_dateToParse).ToString("MM.dd.yyyy"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method with quoted arguments
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodQuoted4()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$([System.DateTime]::Now.ToString(\"MM.dd.yyyy\"))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(DateTime.Now.ToString("MM.dd.yyyy"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodNested()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", "foo" + Path.DirectorySeparatorChar + "file.txt"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine(`" +
                s_rootPathPrefix +
                @"`, $([System.IO.Path]::Combine(`foo`,`file.txt`))))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "foo", "file.txt"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method regex
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodRegex1()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", "foo" + Path.DirectorySeparatorChar + "file.txt"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            // Support enum combines as Enum.Parse expects them
            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Text.RegularExpressions.Regex]::IsMatch(`-42`, `^-?\d+(\.\d{2})?$`, `RegexOptions.IgnoreCase,RegexOptions.Singleline`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(@"True", result);

            // We support the C# style enum combining syntax too
            result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Text.RegularExpressions.Regex]::IsMatch(`-42`, `^-?\d+(\.\d{2})?$`, System.Text.RegularExpressions.RegexOptions.IgnoreCase|RegexOptions.Singleline))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(@"True", result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Text.RegularExpressions.Regex]::IsMatch(`100 GBP`, `^-?\d+(\.\d{2})?$`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(@"False", result);
        }

        /// <summary>
        /// Expand property function that calls a static method  with an instance method chained
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodChained()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
            string dateTime = "'" + _dateToParse + "'";
            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.DateTime]::Parse(" + dateTime + ").ToString(`yyyy/MM/dd HH:mm:ss`))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(DateTime.Parse(_dateToParse).ToString("yyyy/MM/dd HH:mm:ss"), result);
        }

        /// <summary>
        /// Expand property function that calls a static method available only on net46 (Environment.GetFolderPath)
        /// </summary>
        [Fact]
        public void PropertyFunctionGetFolderPath()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Environment]::GetFolderPath(SpecialFolder.System))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(System.Environment.GetFolderPath(Environment.SpecialFolder.System), result);
        }

        /// <summary>
        /// The test exercises: RuntimeInformation / OSPlatform usage, static method invocation, static property invocation, method invocation expression as argument, call chain expression as argument
        /// </summary>
        [Theory]
        [InlineData(
            "$([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform($([System.Runtime.InteropServices.OSPlatform]::Create($([System.Runtime.InteropServices.OSPlatform]::$$platform$$.ToString())))))",
            "True"
        )]
        [InlineData(
            @"$([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform($([System.Runtime.InteropServices.OSPlatform]::$$platform$$)))",
            "True"
        )]
        [InlineData(
            "$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)",
            "$$architecture$$"
        )]
        [InlineData(
            "$([MSBuild]::IsOSPlatform($$platform$$))",
            "True"
        )]
        public void PropertyFunctionRuntimeInformation(string propertyFunction, string expectedExpansion)
        {
            Func<string, string, string, string> formatString = (aString, platform, architecture) => aString
                .Replace("$$platform$$", platform)
                .Replace("$$architecture$$", architecture);

            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string currentPlatformString = Helpers.GetOSPlatformAsString();

            var currentArchitectureString = RuntimeInformation.OSArchitecture.ToString();

            propertyFunction = formatString(propertyFunction, currentPlatformString, currentArchitectureString);
            expectedExpansion = formatString(expectedExpansion, currentPlatformString, currentArchitectureString);

            var result = expander.ExpandIntoStringLeaveEscaped(propertyFunction, ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(expectedExpansion, result);
        }

        [Theory]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('x', 1))", "3")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('x45', 1))", "3")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('x', 1, 4))", "3")]
        // 9 is not a valid StringComparison enum value
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('x', 9))", "10")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('X', 'StringComparison.Ordinal'))", "-1")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('X', 'StringComparison.OrdinalIgnoreCase'))", "0")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('X4', 'StringComparison.OrdinalIgnoreCase'))", "3")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('X4', 1, 'StringComparison.OrdinalIgnoreCase'))", "3")]
        [InlineData("AString", "x12x456789x11", "$(AString.IndexOf('X', 1, 3, 'StringComparison.OrdinalIgnoreCase'))", "3")]
        public void StringIndexOfTests(string propertyName, string properyValue, string propertyFunction, string expectedExpansion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>
                {[propertyName] = ProjectPropertyInstance.Create(propertyName, properyValue)};

            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            expander.ExpandIntoStringLeaveEscaped(propertyFunction, ExpanderOptions.ExpandProperties, MockElementLocation.Instance).ShouldBe(expectedExpansion);
        }

        [Fact]
        public void IsOsPlatformShouldBeCaseInsensitiveToParameter()
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            var osPlatformLowerCase = Helpers.GetOSPlatformAsString().ToLower();

            var result = expander.ExpandIntoStringLeaveEscaped($"$([MSBuild]::IsOsPlatform({osPlatformLowerCase}))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("True", result);
        }

        [Theory]
        [InlineData("NotAVersion")]
        [InlineData("1.2.3.4.5")]
        [InlineData("1,2,3,4")]
        public void PropertyFunctionVersionComparisonsFailsWithInvalidArguments(string badVersion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
            string expectedMessage = ResourceUtilities.GetResourceString("InvalidVersionFormat");

            AssertThrows(expander, $"$([MSBuild]::VersionGreaterThan('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionGreaterThan('1.0.0', '{badVersion}'))", expectedMessage);

            AssertThrows(expander, $"$([MSBuild]::VersionGreaterThanOrEquals('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionGreaterThanOrEquals('1.0.0', '{badVersion}'))", expectedMessage);

            AssertThrows(expander, $"$([MSBuild]::VersionLessThan('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionLessThan('1.0.0', '{badVersion}'))", expectedMessage);

            AssertThrows(expander, $"$([MSBuild]::VersionLessThanOrEquals('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionLessThanOrEquals('1.0.0', '{badVersion}'))", expectedMessage);

            AssertThrows(expander, $"$([MSBuild]::VersionEquals('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionEquals('1.0.0', '{badVersion}'))", expectedMessage);

            AssertThrows(expander, $"$([MSBuild]::VersionNotEquals('{badVersion}', '1.0.0'))", expectedMessage);
            AssertThrows(expander, $"$([MSBuild]::VersionNotEquals('1.0.0', '{badVersion}'))", expectedMessage);
        }

        [Theory]
        [InlineData("v1.0", "2.1", -1)]
        [InlineData("3.2", "3.14-pre", -1)]
        [InlineData("3+metadata", "3.0", 0)]
        [InlineData("2.1", "2.1.0", 0)]
        [InlineData("v1.2.3-pre+metadata", "1.2.3.0", 0)]
        [InlineData("3.14", "3.2", 1)]
        [InlineData("42.43.44.45", "42.43.44.5", 1)]
        public void PropertyFunctionVersionComparisons(string a, string b, int expectedSign)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedSign >  0, $"$([MSBuild]::VersionGreaterThan('{a}', '{b}'))");
            AssertSuccess(expander, expectedSign >= 0, $"$([MSBuild]::VersionGreaterThanOrEquals('{a}', '{b}'))");
            AssertSuccess(expander, expectedSign <  0, $"$([MSBuild]::VersionLessThan('{a}', '{b}'))");
            AssertSuccess(expander, expectedSign <= 0, $"$([MSBuild]::VersionLessThanOrEquals('{a}', '{b}'))");
            AssertSuccess(expander, expectedSign == 0, $"$([MSBuild]::VersionEquals('{a}', '{b}'))");
            AssertSuccess(expander, expectedSign != 0, $"$([MSBuild]::VersionNotEquals('{a}', '{b}'))");
        }

        [Theory]
        [InlineData("net45", ".NETFramework", "4.5")]
        [InlineData("netcoreapp3.1", ".NETCoreApp", "3.1")]
        [InlineData("netstandard2.1", ".NETStandard", "2.1")]
        [InlineData("net5.0-ios12.0", ".NETCoreApp", "5.0")]
        [InlineData("foo", "Unsupported", "0.0")]
        public void PropertyFunctionTargetFrameworkParsing(string tfm, string expectedIdentifier, string expectedVersion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedIdentifier, $"$([MSBuild]::GetTargetFrameworkIdentifier('{tfm}'))");
            AssertSuccess(expander, expectedVersion, $"$([MSBuild]::GetTargetFrameworkVersion('{tfm}'))");
        }

        [Theory]
        [InlineData("net45", 2, "4.5")]
        [InlineData("net45", 3, "4.5.0")]
        [InlineData("net472", 3, "4.7.2")]
        [InlineData("net472", 2, "4.7.2")]
        public void PropertyFunctionTargetFrameworkVersionMultipartParsing(string tfm, int versionPartCount, string expectedVersion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedVersion, $"$([MSBuild]::GetTargetFrameworkVersion('{tfm}', {versionPartCount}))");
        }

        [Theory]
        [InlineData("net5.0-windows10.1.2.3", 4, "10.1.2.3")]
        [InlineData("net5.0-windows10.1.2.3", 2, "10.1.2.3")]
        [InlineData("net5.0-windows10.0.0.3", 2, "10.0.0.3")]
        [InlineData("net5.0-windows0.0.0.3", 2, "0.0.0.3")]
        public void PropertyFunctionTargetPlatformVersionMultipartParsing(string tfm, int versionPartCount, string expectedVersion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedVersion, $"$([MSBuild]::GetTargetPlatformVersion('{tfm}', {versionPartCount}))");
        }

        [Theory]
        [InlineData("net5.0-ios12.0", "ios", "12.0")]
        [InlineData("net5.1-android1.1", "android", "1.1")]
        [InlineData("net6.0-windows99.99", "windows", "99.99")]
        [InlineData("net5.0-ios", "ios", "0.0")]
        [InlineData("foo", "", "0.0")]
        public void PropertyFunctionTargetPlatformParsing(string tfm, string expectedIdentifier, string expectedVersion)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedIdentifier, $"$([MSBuild]::GetTargetPlatformIdentifier('{tfm}'))");
            AssertSuccess(expander, expectedVersion, $"$([MSBuild]::GetTargetPlatformVersion('{tfm}'))");
        }

        [Theory]
        [InlineData("net5.0", "net5.0", true)]
        [InlineData("net5.0-windows10.0", "net5.0-windows10.0", true)]
        [InlineData("net5.0-ios", "net5.0-andriod", false)]
        [InlineData("net5.0-ios12.0", "net5.0-ios11.0", true)]
        [InlineData("net5.0-ios11.0", "net5.0-ios12.0", false)]
        [InlineData("net45", "net46", false)]
        [InlineData("net46", "net45", true)]
        [InlineData("netcoreapp3.1", "netcoreapp1.0", true)]
        [InlineData("netstandard1.6", "netstandard2.1", false)]
        [InlineData("netcoreapp3.0", "netstandard2.1", true)]
        [InlineData("net461", "netstandard1.0", true)]
        [InlineData("foo", "netstandard1.0", false)]
        public void PropertyFunctionTargetFrameworkComparisons(string tfm1, string tfm2, bool expectedFrameworkCompatible)
        {
            var pg = new PropertyDictionary<ProjectPropertyInstance>();
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            AssertSuccess(expander, expectedFrameworkCompatible, $"$([MSBuild]::IsTargetFrameworkCompatible('{tfm1}', '{tfm2}'))");
        }

        private void AssertThrows(Expander<ProjectPropertyInstance, ProjectItemInstance> expander, string expression, string expectedMessage)
        {
            var ex = Assert.Throws<InvalidProjectFileException>(
                () => expander.ExpandPropertiesLeaveTypedAndEscaped(
                    expression,
                    ExpanderOptions.ExpandProperties,
                    MockElementLocation.Instance));

            Assert.Contains(expectedMessage, ex.Message);
        }

        private void AssertSuccess(Expander<ProjectPropertyInstance, ProjectItemInstance> expander, object expected, string expression)
        {
            var actual = expander.ExpandPropertiesLeaveTypedAndEscaped(
                expression,
                ExpanderOptions.ExpandProperties,
                MockElementLocation.Instance);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// Expand property function that calls a method with an enum parameter
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodEnumArgument()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped("$([System.String]::Equals(`a`, `A`, StringComparison.OrdinalIgnoreCase))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            Assert.Equal(true.ToString(), result);
        }

        /// <summary>
        /// Expand intrinsic property function to locate the directory of a file above
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodDirectoryNameOfFileAbove()
        {
            string tempPath = Path.GetTempPath();
            string tempFile = Path.GetFileName(FileUtilities.GetTemporaryFile());

            try
            {
                string directoryStart = Path.Combine(tempPath, "one\\two\\three\\four\\five");

                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("StartingDirectory", directoryStart));
                pg.Set(ProjectPropertyInstance.Create("FileToFind", tempFile));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                string result = expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::GetDirectoryNameOfFileAbove($(StartingDirectory), $(FileToFind)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Microsoft.Build.Shared.FileUtilities.EnsureTrailingSlash(tempPath), Microsoft.Build.Shared.FileUtilities.EnsureTrailingSlash(result));

                result = expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::GetDirectoryNameOfFileAbove($(StartingDirectory), Hobbits))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(String.Empty, result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        /// <summary>
        /// Verifies that <see cref="IntrinsicFunctions.GetPathOfFileAbove"/> returns the correct path if a file exists
        /// or an empty string if it doesn't.
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodGetPathOfFileAbove()
        {
            // Set element location to a file deep in the directory structure.  This is where the function should start looking
            //
            MockElementLocation mockElementLocation = new MockElementLocation(Path.Combine(ObjectModelHelpers.TempProjectDir, "one", "two", "three", "four", "five", Path.GetRandomFileName()));

            string fileToFind = FileUtilities.GetTemporaryFile(ObjectModelHelpers.TempProjectDir, ".tmp");

            try
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("FileToFind", Path.GetFileName(fileToFind)));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                string result = expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::GetPathOfFileAbove($(FileToFind)))", ExpanderOptions.ExpandProperties, mockElementLocation);

                Assert.Equal(fileToFind, result);

                result = expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::GetPathOfFileAbove('Hobbits'))", ExpanderOptions.ExpandProperties, mockElementLocation);

                Assert.Equal(String.Empty, result);
            }
            finally
            {
                ObjectModelHelpers.DeleteTempProjectDirectory();
            }
        }

        /// <summary>
        /// Verifies that the usage of GetPathOfFileAbove() within an in-memory project throws an exception.
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodGetPathOfFileAboveInMemoryProject()
        {
            InvalidProjectFileException exception = Assert.Throws<InvalidProjectFileException>(() =>
            {
                ObjectModelHelpers.CreateInMemoryProject("<Project><PropertyGroup><foo>$([MSBuild]::GetPathOfFileAbove('foo'))</foo></PropertyGroup></Project>");
            });

            Assert.StartsWith("The expression \"[MSBuild]::GetPathOfFileAbove(foo, \'\')\" cannot be evaluated.", exception.Message);
        }

        /// <summary>
        /// Verifies that <see cref="IntrinsicFunctions.GetPathOfFileAbove"/> only accepts a file name.
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodGetPathOfFileAboveFileNameOnly()
        {
            string fileWithPath = Path.Combine("foo", "bar", "file.txt");

            InvalidProjectFileException exception = Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("FileWithPath", fileWithPath));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::GetPathOfFileAbove($(FileWithPath)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            });

            Assert.Contains(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidGetPathOfFileAboveParameter", fileWithPath), exception.Message);
        }

        /// <summary>
        /// Expand property function that calls GetCultureInfo
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodGetCultureInfo()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

#if FEATURE_CULTUREINFO_GETCULTURES
            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Globalization.CultureInfo]::GetCultureInfo(`en-US`).ToString())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
#else
            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.Globalization.CultureInfo]::new(`en-US`).ToString())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
#endif

            Assert.Equal(new CultureInfo("en-US").ToString(), result);
        }

        /// <summary>
        /// Expand property function that calls a static arithmetic method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodArithmeticAddInt32()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Add(40, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((40 + 2).ToString(), result);
        }

        /// <summary>
        /// Expand property function that calls a static arithmetic method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodArithmeticAddDouble()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Add(39.9, 2.1))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((39.9 + 2.1).ToString(), result);
        }

        /// <summary>
        /// Expand property function choosing either the value (if not empty) or the default specified
        /// </summary>
        [Fact]
        public void PropertyFunctionValueOrDefault()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::ValueOrDefault('', '42'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("42", result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::ValueOrDefault('42', '43'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("42", result);
        }

        /// <summary>
        /// Expand property function choosing either the value (from the environment) or the default specified
        /// </summary>
        [Fact]
        public void PropertyFunctionValueOrDefaultFromEnvironment()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            pg["BonkersTargetsPath"] = ProjectPropertyInstance.Create("BonkersTargetsPath", "Bonkers");

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::ValueOrDefault('$(BonkersTargetsPath)', '42'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("Bonkers", result);

            pg["BonkersTargetsPath"] = ProjectPropertyInstance.Create("BonkersTargetsPath", String.Empty);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::ValueOrDefault('$(BonkersTargetsPath)', '43'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal("43", result);
        }

#if FEATURE_APPDOMAIN
        /// <summary>
        /// Expand property function that tests for existence of the task host
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void PropertyFunctionDoesTaskHostExist()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::DoesTaskHostExist('CurrentRuntime', 'CurrentArchitecture'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            // This is the current, so it had better be true!
            Assert.Equal("true", result, true);
        }

        /// <summary>
        /// Expand property function that tests for existence of the task host
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void PropertyFunctionDoesTaskHostExist_Whitespace()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::DoesTaskHostExist('   CurrentRuntime    ', 'CurrentArchitecture'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            // This is the current, so it had better be true!
            Assert.Equal("true", result, true);
        }
#endif

        [Fact]
        public void PropertyFunctionNormalizeDirectory()
        {
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>(new[]
            {
                ProjectPropertyInstance.Create("MyPath", "one"),
                ProjectPropertyInstance.Create("MySecondPath", "two"),
            }));

            Assert.Equal(
                $"{Path.GetFullPath("one")}{Path.DirectorySeparatorChar}",
                expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::NormalizeDirectory($(MyPath)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));

            Assert.Equal(
                $"{Path.GetFullPath(Path.Combine("one", "two"))}{Path.DirectorySeparatorChar}",
                expander.ExpandIntoStringAndUnescape(@"$([MSBuild]::NormalizeDirectory($(MyPath), $(MySecondPath)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance));
        }

        /// <summary>
        /// Expand property function that tests for existence of the task host
        /// </summary>
        [Fact]
        public void PropertyFunctionDoesTaskHostExist_Error()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::DoesTaskHostExist('ASDF', 'CurrentArchitecture'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                // We should have failed before now
                Assert.True(false);
            }
           );
        }

#if FEATURE_APPDOMAIN
        /// <summary>
        /// Expand property function that tests for existence of the task host
        /// </summary>
        [Fact]
        [Trait("Category", "mono-osx-failing")]
        public void PropertyFunctionDoesTaskHostExist_Evaluated()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            pg["Runtime"] = ProjectPropertyInstance.Create("Runtime", "CurrentRuntime");
            pg["Architecture"] = ProjectPropertyInstance.Create("Architecture", "CurrentArchitecture");

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::DoesTaskHostExist('$(Runtime)', '$(Architecture)'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            // This is the current, so it had better be true!
            Assert.Equal("true", result, true);
        }
#endif

#if FEATURE_APPDOMAIN
        /// <summary>
        /// Expand property function that tests for existence of the task host
        /// </summary>
        [Fact]
        public void PropertyFunctionDoesTaskHostExist_NonexistentTaskHost()
        {
            string taskHostName = Environment.GetEnvironmentVariable("MSBUILDTASKHOST_EXE_NAME");
            try
            {
                Environment.SetEnvironmentVariable("MSBUILDTASKHOST_EXE_NAME", "asdfghjkl.exe");
                NodeProviderOutOfProcTaskHost.ClearCachedTaskHostPaths();

                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::DoesTaskHostExist('CLR2', 'CurrentArchitecture'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                // CLR has been forced to pretend not to exist, whether it actually does or not
                Assert.Equal("false", result, true);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MSBUILDTASKHOST_EXE_NAME", taskHostName);
                NodeProviderOutOfProcTaskHost.ClearCachedTaskHostPaths();
            }
        }
#endif

        /// <summary>
        /// Expand property function that calls a static bitwise method to retrieve file attribute
        /// </summary>
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Trait("Category", "mono-osx-failing")]
        public void PropertyFunctionStaticMethodFileAttributes()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string tempFile = FileUtilities.GetTemporaryFile();
            try
            {
                File.SetAttributes(tempFile, FileAttributes.ReadOnly | FileAttributes.Archive);

                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::BitwiseAnd(32,$([System.IO.File]::GetAttributes(" + tempFile + "))))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal("32", result);
            }
            finally
            {
                File.SetAttributes(tempFile, FileAttributes.Normal);
                File.Delete(tempFile);
            }
        }

        /// <summary>
        /// Expand intrinsic property function calls a static arithmetic method
        /// </summary>
        [Fact]
        public void PropertyFunctionStaticMethodIntrinsicMaths()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Add(39.9, 2.1))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((39.9 + 2.1).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Add(40, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((40 + 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Subtract(44, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((44 - 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Subtract(42.9, 0.9))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((42.9 - 0.9).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Multiply(21, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((21 * 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Multiply(84.0, 0.5))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((84.0 * 0.5).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Divide(84, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((84 / 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Divide(84.4, 2.0))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((84.4 / 2.0).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Modulo(85, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((85 % 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Modulo(2345.5, 43))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((2345.5 % 43).ToString(), result);

            // test for overflow wrapping
            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::Add(9223372036854775807, 20))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            double expectedResult = 9223372036854775807D + 20D;
            Assert.Equal(expectedResult.ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::BitwiseOr(40, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((40 | 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::BitwiseAnd(42, 2))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((42 & 2).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::BitwiseXor(213, 255))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((213 ^ 255).ToString(), result);

            result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::BitwiseNot(-43))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal((~-43).ToString(), result);
        }

        /// <summary>
        /// Expand a property reference that has whitespace around the property name (should result in empty)
        /// </summary>
        [Fact]
        public void PropertySimpleSpaced()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomeStuff", "This IS SOME STUff"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$( SomeStuff )", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(String.Empty, result);
        }

#if FEATURE_WIN32_REGISTRY
        [Fact]
        public void PropertyFunctionGetRegitryValue()
        {
            try
            {
                string envVar = NativeMethodsShared.IsWindows ? "TEMP" : "USER";
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeProperty", "Value"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue("Value", "%" + envVar + "%", RegistryValueKind.ExpandString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::GetRegistryValue('HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test', '$(SomeProperty)'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Environment.GetEnvironmentVariable(envVar), result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void PropertyFunctionGetRegitryValueDefault()
        {
            try
            {
                string envVar = NativeMethodsShared.IsWindows ? "TEMP" : "USER";
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeProperty", "Value"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue(String.Empty, "%" + envVar + "%", RegistryValueKind.ExpandString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::GetRegistryValue('HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test', null))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Environment.GetEnvironmentVariable(envVar), result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void PropertyFunctionGetRegistryValueFromView1()
        {
            try
            {
                string envVar = NativeMethodsShared.IsWindows ? "TEMP" : "USER";
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeProperty", "Value"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue(String.Empty, "%" + envVar + "%", RegistryValueKind.ExpandString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::GetRegistryValueFromView('HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test', null, null, RegistryView.Default, RegistryView.Default))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Environment.GetEnvironmentVariable(envVar), result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }

        [Fact]
        public void PropertyFunctionGetRegistryValueFromView2()
        {
            try
            {
                string envVar = NativeMethodsShared.IsWindows ? "TEMP" : "USER";
                PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
                pg.Set(ProjectPropertyInstance.Create("SomeProperty", "Value"));

                Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MSBuild_test");

                key.SetValue(String.Empty, "%" + envVar + "%", RegistryValueKind.ExpandString);
                string result = expander.ExpandIntoStringLeaveEscaped(@"$([MSBuild]::GetRegistryValueFromView('HKEY_CURRENT_USER\Software\Microsoft\MSBuild_test', null, null, Microsoft.Win32.RegistryView.Default))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                Assert.Equal(Environment.GetEnvironmentVariable(envVar), result);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\MSBuild_test");
            }
        }
#endif

        /// <summary>
        /// Expand a property function that references item metadata
        /// </summary>
        [Fact]
        public void PropertyFunctionConsumingItemMetadata()
        {
            ProjectInstance project = ProjectHelpers.CreateEmptyProjectInstance();
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            Dictionary<string, string> itemMetadataTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            itemMetadataTable["Compile.Identity"] = "fOo.Cs";
            StringMetadataTable itemMetadata = new StringMetadataTable(itemMetadataTable);

            List<ProjectItemInstance> ig = new List<ProjectItemInstance>();
            pg.Set(ProjectPropertyInstance.Create("SomePath", Path.Combine(s_rootPathPrefix, "some", "path")));
            ig.Add(new ProjectItemInstance(project, "Compile", "fOo.Cs", project.FullPath));

            ItemDictionary<ProjectItemInstance> itemsByType = new ItemDictionary<ProjectItemInstance>();
            itemsByType.ImportItems(ig);

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg, itemsByType, itemMetadata);

            string result = expander.ExpandIntoStringLeaveEscaped(@"$([System.IO.Path]::Combine($(SomePath),%(Compile.Identity)))", ExpanderOptions.ExpandAll, MockElementLocation.Instance);

            Assert.Equal(Path.Combine(s_rootPathPrefix, "some", "path", "fOo.Cs"), result);
        }

        /// <summary>
        /// Expand a property function which is a string constructor referencing item metadata.
        /// </summary>
        /// <remarks>
        /// Note that referencing a non-existent metadatum results in binding to a parameter-less String constructor. This constructor
        /// does not exist in BCL but it is special-cased in the expander logic and handled to return an empty string.
        /// </remarks>
        [Theory]
        [InlineData("language", "english")]
        [InlineData("nonexistent", "")]
        public void PropertyStringConstructorConsumingItemMetadata(string metadatumName, string metadatumValue)
        {
            ProjectHelpers.CreateEmptyProjectInstance();
            var expander = CreateItemFunctionExpander();

            string result = expander.ExpandIntoStringLeaveEscaped($"$([System.String]::new(%({metadatumName})))", ExpanderOptions.ExpandAll, MockElementLocation.Instance);

            result.ShouldBe(metadatumValue);
        }

        [Fact]
        public void PropertyFunctionHashCodeSameOnlyIfStringSame()
        {
            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);
            string[] stringsToHash = {
                "cat1s",
                "cat1z",
                "bat1s",
                "cut1s",
                "cat1so",
                "cats1",
                "acat1s",
                "cat12s",
                "cat1s"
            };
            int[] hashes = stringsToHash.Select(toHash =>
                (int)expander.ExpandPropertiesLeaveTypedAndEscaped($"$([MSBuild]::StableStringHash('{toHash}'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance)
                ).ToArray();
            for (int a = 0; a < hashes.Length; a++)
            {
                for (int b = a; b < hashes.Length; b++)
                {
                    if (stringsToHash[a].Equals(stringsToHash[b]))
                    {
                        hashes[a].ShouldBe(hashes[b], "Identical strings should hash to the same value.");
                    }
                    else
                    {
                        hashes[a].ShouldNotBe(hashes[b], "Different strings should not hash to the same value.");
                    }
                }
            }
        }

        /// <summary>
        /// A whole bunch error check tests
        /// </summary>
        [Fact]
        public void Medley()
        {
            // Make absolutely sure that the static method cache hasn't been polluted by the other tests.
            AvailableStaticMethods.Reset_ForUnitTestsOnly();

            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();
            pg.Set(ProjectPropertyInstance.Create("File", @"foo\file.txt"));

            pg.Set(ProjectPropertyInstance.Create("a", "no"));
            pg.Set(ProjectPropertyInstance.Create("b", "true"));
            pg.Set(ProjectPropertyInstance.Create("c", "1"));
            pg.Set(ProjectPropertyInstance.Create("position", "4"));
            pg.Set(ProjectPropertyInstance.Create("d", "xxx"));
            pg.Set(ProjectPropertyInstance.Create("e", "xxx"));
            pg.Set(ProjectPropertyInstance.Create("and", "and"));
            pg.Set(ProjectPropertyInstance.Create("a_semi_b", "a;b"));
            pg.Set(ProjectPropertyInstance.Create("a_apos_b", "a'b"));
            pg.Set(ProjectPropertyInstance.Create("foo_apos_foo", "foo'foo"));
            pg.Set(ProjectPropertyInstance.Create("a_escapedsemi_b", "a%3bb"));
            pg.Set(ProjectPropertyInstance.Create("a_escapedapos_b", "a%27b"));
            pg.Set(ProjectPropertyInstance.Create("has_trailing_slash", @"foo\"));
            pg.Set(ProjectPropertyInstance.Create("emptystring", @""));
            pg.Set(ProjectPropertyInstance.Create("space", @" "));
            pg.Set(ProjectPropertyInstance.Create("listofthings", @"a;b;c;d;e;f;g;h;i;j;k;l"));
            pg.Set(ProjectPropertyInstance.Create("input", @"EXPORT a"));
            pg.Set(ProjectPropertyInstance.Create("propertycontainingnullasastring", @"null"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            var validTests = new List<string[]> {
                new string[] {"$(input.ToString()[1])", "X"},
                new string[] {"$(input[1])", "X"},
                new string[] {"$(listofthings.Split(';')[$(position)])","e"},
                new string[] {@"$([System.Text.RegularExpressions.Regex]::Match($(Input), `EXPORT\s+(.+)`).Groups[1].Value)","a"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo(3))", "0"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo(3))", "0"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo(3.0))", "0"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo('3'))", "0"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo(3.1))", "-1"},
                new string[] {"$([MSBuild]::Add(1,2).CompareTo(2))", "1"},
                new string[] {"$([MSBuild]::Add(1,2).Equals(3))", "True"},
                new string[] {"$([MSBuild]::Add(1,2).Equals(3.0))", "True"},
                new string[] {"$([MSBuild]::Add(1,2).Equals('3'))", "True"},
                new string[] {"$([MSBuild]::Add(1,2).Equals(3.1))", "False"},
                new string[] {"$(a.Insert(0,'%28'))", "%28no"},
                new string[] {"$(a.Insert(0,'\"'))", "\"no"},
                new string[] {"$(a.Insert(0,'(('))", "%28%28no"},
                new string[] {"$(a.Insert(0,'))'))", "%29%29no"},
                new string[] {"A$(Reg:A)A", "AA"},
                new string[] {"A$(Reg:AA)", "A"},
                new string[] {"$(Reg:AA)", ""},
                new string[] {"$(Reg:AAAA)", ""},
                new string[] {"$(Reg:AAA)", ""},
                new string[] {"$([MSBuild]::Add(2,$([System.Convert]::ToInt64('28', 16))))", "42"},
                new string[] {"$([MSBuild]::Add(2,$([System.Convert]::ToInt64('28', $([System.Convert]::ToInt32(16))))))", "42"},
                new string[] {"$(e.Length.ToString())", "3"},
                new string[] {"$(e.get_Length().ToString())", "3"},
                new string[] {"$(emptystring.Length)", "0" },
                new string[] {"$(space.Length)", "1" },
                new string[] {"$([System.TimeSpan]::Equals(null, null))", "True"}, // constant, unquoted null is a special value
                new string[] {"$([MSBuild]::Add(40,null))", "40"},
                new string[] {"$([MSBuild]::Add( 40 , null ))", "40"},
                new string[] {"$([MSBuild]::Add(null,40))", "40"},
                new string[] {"$([MSBuild]::Escape(';'))", "%3b"},
                new string[] {"$([MSBuild]::UnEscape('%3b'))", ";"},
                new string[] {"$(e.Substring($(e.Length)))", ""},
                new string[] {"$([System.Int32]::MaxValue)", System.Int32.MaxValue.ToString()},
                new string[] {"x$()", "x"},
                new string[] {"A$(Reg:A)A", "AA"},
                new string[] {"A$(Reg:AA)", "A"},
                new string[] {"$(Reg:AA)", ""},
                new string[] {"$(Reg:AAAA)", ""},
                new string[] {"$(Reg:AAA)", ""}
                                   };

            var errorTests = new List<string>{
            "$(input[)",
            "$(input.ToString()])",
            "$(input.ToString()[)",
            "$(input.ToString()[12])",
            "$(input[])",
            "$(input[-1])",
            "$(listofthings.Split(';')[)",
            "$(listofthings.Split(';')['goo'])",
            "$(listofthings.Split(';')[])",
            "$(listofthings.Split(';')[-1])",
            "$([]::())",
                                                      @"
 
$(
 
$(
 
[System.IO]::Path.GetDirectory('c:\foo\bar\baz.txt')
 
).Substring(
 
'$([System.IO]::Path.GetPathRoot(
 
'$([System.IO]::Path.GetDirectory('c:\foo\bar\baz.txt'))'
 
).Length)'
 
 
 
)
 
",
                "$([Microsoft.VisualBasic.FileIO.FileSystem]::CurrentDirectory)", // not allowed
                "$(e.Length..ToString())",
                "$(SomeStuff.get_Length(null))",
                "$(SomeStuff.Substring((1)))",
                "$(b.Substring(-10, $(c)))",
                "$(b.Substring(-10, $(emptystring)))",
                "$(b.Substring(-10, $(space)))",
                "$([MSBuild]::Add.Sub(null,40))",
                "$([MSBuild]::Add( ,40))", // empty parameter is empty string
                "$([MSBuild]::Add('',40))", // empty quoted parameter is empty string
                "$([MSBuild]::Add(40,,,))",
                "$([MSBuild]::Add(40, ,,))",
                "$([MSBuild]::Add(40,)",
                "$([MSBuild]::Add(40,X)",
                "$([MSBuild]::Add(40,",
                "$([MSBuild]::Add(40",
                "$([MSBuild]::Add(,))", // gives "Late bound operations cannot be performed on types or methods for which ContainsGenericParameters is true."
                "$([System.TimeSpan]::Equals(,))", // empty parameter is interpreted as empty string
                "$([System.TimeSpan]::Equals($(space),$(emptystring)))", // empty parameter is interpreted as empty string
                "$([System.TimeSpan]::Equals($(emptystring),$(emptystring)))", // empty parameter is interpreted as empty string
                "$([MSBuild]::Add($(PropertyContainingNullAsAString),40))", // a property containing the word null is a string "null"
                "$([MSBuild]::Add('null',40))", // the word null is a string "null"
                "$(SomeStuff.Substring(-10))",
                "$(.Length)",
                "$(.Substring(1))",
                "$(.get_Length())",
                "$(e.)",
                "$(e..)",
                "$(e..Length)",
                "$(e$(d).Length)",
                "$($(d).Length)",
                "$(e`.Length)",
                "$([System.IO.Path]Combine::Combine(`a`,`b`))",
                "$([System.IO.Path]::Combine((`a`,`b`))",
                "$([System.IO.Path]Combine(::Combine(`a`,`b`))",
                "$([System.IO.Path]Combine(`::Combine(`a`,`b`)`, `b`)`)",
                "$([System.IO.Path]::`Combine(`a`, `b`)`)",
                "$([System.IO.Path]::(`Combine(`a`, `b`)`))",
                "$([System.DateTime]foofoo::Now)",
                "$([System.DateTime].Now)",
                "$([].Now)",
                "$([ ].Now)",
                "$([ .Now)",
                "$([])",
                "$([ )",
                "$([ ])",
                "$([System.Diagnostics.Process]::Start(`NOTEPAD.EXE`))",
                "$([[]]::Start(`NOTEPAD.EXE`))",
                "$([(::Start(`NOTEPAD.EXE`))",
                "$([Goop]::Start(`NOTEPAD.EXE`))",
                "$([System.Threading.Thread]::CurrentThread)",
                "$",
                "$(",
                "$((",
                "@",
                "@(",
                "@()",
                "%",
                "%(",
                "%()",
                "exists",
                "exists(",
                "exists()",
                "exists( )",
                "exists(,)",
                "@(x->'",
                "@(x->''",
                "@(x-",
                "@(x->'x','",
                "@(x->'x',''",
                "@(x->'x','')",
                "-1>x",
                "\n",
                "\t",
                "+-1",
                "$(SomeStuff.)",
                "$(SomeStuff.!)",
                "$(SomeStuff.`)",
                "$(SomeStuff.GetType)",
                "$(goop.baz`)",
                "$(SomeStuff.Substring(HELLO!))",
                "$(SomeStuff.ToLowerInvariant()_goop)",
                "$(SomeStuff($(System.DateTime.Now)))",
                "$(System.Foo.Bar.Lgg)",
                "$(SomeStuff.Lgg)",
                "$(SomeStuff($(Value)))",
                "$(e.$(e.Length))",
                "$(e.Substring($(e.Substring(,)))",
                "$(e.Substring($(e.Substring(a)))",
                "$(e.Substring($([System.IO.Path]::Combine(`a`, `b`))))",
                "$([]::())",
                "$((((",
                "$($())",
                "$",
                "()"
            };

#if !RUNTIME_TYPE_NETCORE
            if (NativeMethodsShared.IsWindows)
            {
                // '|' is only an invalid character in Windows filesystems
                errorTests.Add("$([System.IO.Path]::Combine(`|`,`b`))");
            }
#endif

#if FEATURE_WIN32_REGISTRY
            if (NativeMethodsShared.IsWindows)
            {
                errorTests.Add("$(Registry:X)");
            }
#endif

            if (!NativeMethodsShared.IsWindows)
            {
                // If no registry or not running on windows, this gets expanded to the empty string
                // example: xplat build running on OSX
                validTests.Add(new string[]{"$(Registry:X)", ""});
            }

            string result;
            for (int i = 0; i < validTests.Count; i++)
            {
                result = expander.ExpandIntoStringLeaveEscaped(validTests[i][0], ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

                if (!String.Equals(result, validTests[i][1]))
                {
                    string message = "FAILURE: " + validTests[i][0] + " expanded to '" + result + "' instead of '" + validTests[i][1] + "'";
                    Console.WriteLine(message);
                    Assert.True(false, message);
                }
                else
                {
                    Console.WriteLine(validTests[i][0] + " expanded to '" + result + "'");
                }
            }

            for (int i = 0; i < errorTests.Count; i++)
            {
                // If an expression is invalid,
                //      - Expansion may throw InvalidProjectFileException, or
                //      - return the original unexpanded expression
                bool success = true;
                bool caughtException = false;
                result = String.Empty;
                try
                {
                    result = expander.ExpandIntoStringLeaveEscaped(errorTests[i], ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
                    if (String.Compare(result, errorTests[i]) == 0)
                    {
                        Console.WriteLine(errorTests[i] + " did not expand.");
                        success = false;
                    }
                }
                catch (InvalidProjectFileException ex)
                {
                    Console.WriteLine(errorTests[i] + " caused '" + ex.Message + "'");
                    caughtException = true;
                }
                Assert.True(
                        !success || caughtException,
                        "FAILURE: Expected '" + errorTests[i] + "' to not parse or not be evaluated but it evaluated to '" + result + "'"
                    );
            }
        }

        [Fact]
        public void PropertyFunctionEnsureTrailingSlash()
        {
            string path = Path.Combine("foo", "bar");

            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            pg.Set(ProjectPropertyInstance.Create("SomeProperty", path));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            // Verify a constant expands properly
            string result = expander.ExpandIntoStringLeaveEscaped($"$([MSBuild]::EnsureTrailingSlash('{path}'))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(path + Path.DirectorySeparatorChar, result);

            // Verify that a property expands properly
            result = expander.ExpandIntoStringLeaveEscaped("$([MSBuild]::EnsureTrailingSlash($(SomeProperty)))", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.Equal(path + Path.DirectorySeparatorChar, result);
        }

        [Fact]
        public void PropertyFunctionWithNewLines()
        {
            const string propertyFunction = @"$(SomeProperty
 .Substring(0, 10)
  .ToString()
   .Substring(0, 5)
     .ToString())";

            PropertyDictionary<ProjectPropertyInstance> pg = new PropertyDictionary<ProjectPropertyInstance>();

            pg.Set(ProjectPropertyInstance.Create("SomeProperty", "6C8546D5297C424F962201B0E0E9F142"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(pg);

            string result = expander.ExpandIntoStringLeaveEscaped(propertyFunction, ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            result.ShouldBe("6C854");
        }

        [Fact]
        public void PropertyFunctionStringIndexOfAny()
        {
            TestPropertyFunction("$(prop.IndexOfAny('y'))", "prop", "x-y-z", "2");
        }

        [Fact]
        public void PropertyFunctionStringLastIndexOf()
        {
            TestPropertyFunction("$(prop.LastIndexOf('y'))", "prop", "x-x-y-y-y-z", "8");

            TestPropertyFunction("$(prop.LastIndexOf('y', 7))", "prop", "x-x-y-y-y-z", "6");
        }

        [Fact]
        public void PropertyFunctionStringCopy()
        {
            string propertyFunction = @"$([System.String]::Copy($(X)).LastIndexOf(
                                                '.designer.cs',
                                                System.StringComparison.OrdinalIgnoreCase))";
            TestPropertyFunction(propertyFunction,
                                "X", "test.designer.cs", "4");
        }

        [Fact]
        public void PropertyFunctionVersionParse()
        {
            TestPropertyFunction(@"$([System.Version]::Parse('$(X)').ToString(1))", "X", "4.0", "4");
        }

        [Fact]
        public void PropertyFunctionGuidNewGuid()
        {
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            string result = expander.ExpandIntoStringLeaveEscaped("$([System.Guid]::NewGuid())", ExpanderOptions.ExpandProperties, MockElementLocation.Instance);

            Assert.True(Guid.TryParse(result, out Guid guid));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetCurrentToolsDirectory()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetCurrentToolsDirectory())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetCurrentToolsDirectory()));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetToolsDirectory32()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetToolsDirectory32())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetToolsDirectory32()));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetToolsDirectory64()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetToolsDirectory64())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetToolsDirectory64()));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetMSBuildSDKsPath()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetMSBuildSDKsPath())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetMSBuildSDKsPath()));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetVsInstallRoot()
        {
            string vsInstallRoot = EscapingUtilities.Escape(IntrinsicFunctions.GetVsInstallRoot());

            vsInstallRoot ??= "";

            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetVsInstallRoot())", "X", "_", vsInstallRoot);
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetMSBuildExtensionsPath()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetMSBuildExtensionsPath())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetMSBuildExtensionsPath()));
        }

        [Fact]
        public void PropertyFunctionIntrinsicFunctionGetProgramFiles32()
        {
            TestPropertyFunction("$([Microsoft.Build.Evaluation.IntrinsicFunctions]::GetProgramFiles32())", "X", "_", EscapingUtilities.Escape(IntrinsicFunctions.GetProgramFiles32()));
        }

        [Fact]
        public void PropertyFunctionStringArrayIndexerGetter()
        {
            TestPropertyFunction("$(prop.Split('-')[0])", "prop", "x-y-z", "x");
        }

        [Fact]
        public void PropertyFunctionSubstring1()
        {
            TestPropertyFunction("$(prop.Substring(2))", "prop", "abcdef", "cdef");
        }

        [Fact]
        public void PropertyFunctionSubstring2()
        {
            TestPropertyFunction("$(prop.Substring(2, 3))", "prop", "abcdef", "cde");
        }

        [Fact]
        public void PropertyFunctionStringGetChars()
        {
            TestPropertyFunction("$(prop[0])", "prop", "461", "4");
        }

        [Fact]
        public void PropertyFunctionStringGetCharsError()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                TestPropertyFunction("$(prop[5])", "prop", "461", "4");
            });
        }

        [Fact]
        public void PropertyFunctionStringPadLeft1()
        {
            TestPropertyFunction("$(prop.PadLeft(2))", "prop", "x", " x");
        }

        [Fact]
        public void PropertyFunctionStringPadLeft2()
        {
            TestPropertyFunction("$(prop.PadLeft(2, '0'))", "prop", "x", "0x");
        }

        [Fact]
        public void PropertyFunctionStringPadLeftComplex()
        {
            TestPropertyFunction("$(prop.PadLeft($([MSBuild]::Multiply(1, 2)), '0'))", "prop", "x", "0x");
        }

        [Fact]
        public void PropertyFunctionStringPadLeftChar()
        {
            TestPropertyFunction("$(VersionSuffixBuildOfTheDay.PadLeft(3, $([System.Convert]::ToChar(`0`))))", "VersionSuffixBuildOfTheDay", "4", "004");
        }

        [Fact]
        public void PropertyFunctionStringPadRight1()
        {
            TestPropertyFunction("$(prop.PadRight(2))", "prop", "x", "x ");
        }

        [Fact]
        public void PropertyFunctionStringPadRight2()
        {
            TestPropertyFunction("$(prop.PadRight(2, '0'))", "prop", "x", "x0");
        }

        [Fact]
        public void PropertyFunctionStringTrimEndCharArray()
        {
            TestPropertyFunction("$(prop.TrimEnd('.0123456789'))", "prop", "net461", "net");
        }

        [Fact]
        public void PropertyFunctionStringTrimStart()
        {
            TestPropertyFunction("$(X.TrimStart('vV'))", "X", "v40", "40");
        }

        [Fact]
        public void PropertyFunctionStringTrimStartNoQuotes()
        {
            TestPropertyFunction("$(X.TrimStart(vV))", "X", "v40", "40");
        }

        [Fact]
        public void PropertyFunctionStringTrimEnd1()
        {
            TestPropertyFunction("$(prop.TrimEnd('a'))", "prop", "netaa", "net");
        }

        // https://github.com/Microsoft/msbuild/issues/2882
        [Fact]
        public void PropertyFunctionMathMaxOverflow()
        {
            TestPropertyFunction("$([System.Math]::Max($(X), 0))", "X", "-2010", "0");
        }

        [Fact]
        public void PropertyFunctionStringTrimEnd2()
        {
            Assert.Throws<InvalidProjectFileException>(() =>
            {
                TestPropertyFunction("$(prop.TrimEnd('a', 'b'))", "prop", "stringab", "string");
            });
        }

        [Fact]
        public void PropertyFunctionMathMin()
        {
            TestPropertyFunction("$([System.Math]::Min($(X), 20))", "X", "30", "20");
        }

        [Fact]
        public void PropertyFunctionMSBuildAdd()
        {
            TestPropertyFunction("$([MSBuild]::Add($(X), 5))", "X", "7", "12");
        }

        [Fact]
        public void PropertyFunctionMSBuildAddComplex()
        {
            TestPropertyFunction("$([MSBuild]::Add($(X), $([MSBuild]::Add(2, 3))))", "X", "7", "12");
        }

        [Fact]
        public void PropertyFunctionMSBuildSubtract()
        {
            TestPropertyFunction("$([MSBuild]::Subtract($(X), 20100000))", "X", "20100042", "42");
        }

        [Fact]
        public void PropertyFunctionMSBuildMultiply()
        {
            TestPropertyFunction("$([MSBuild]::Multiply($(X), 8800))", "X", "2", "17600");
        }

        [Fact]
        public void PropertyFunctionMSBuildMultiplyComplex()
        {
            TestPropertyFunction("$([MSBuild]::Multiply($(X), $([MSBuild]::Multiply(1, 8800))))", "X", "2", "17600");
        }

        [Fact]
        public void PropertyFunctionMSBuildDivide()
        {
            TestPropertyFunction("$([MSBuild]::Divide($(X), 10000))", "X", "65536", "6.5536");
        }

        [Fact]
        public void PropertyFunctionConvertToString()
        {
            TestPropertyFunction("$([System.Convert]::ToString(`.`))", "_", "_", ".");
        }

        [Fact]
        public void PropertyFunctionConvertToInt32()
        {
            TestPropertyFunction("$([System.Convert]::ToInt32(42))", "_", "_", "42");
        }

        [Fact]
        public void PropertyFunctionToCharArray()
        {
            TestPropertyFunction("$([System.Convert]::ToString(`.`).ToCharArray())", "_", "_", ".");
        }

        [Fact]
        public void PropertyFunctionStringArrayGetValue()
        {
            TestPropertyFunction("$(X.Split($([System.Convert]::ToString(`.`).ToCharArray())).GetValue($([System.Convert]::ToInt32(0))))", "X", "ab.cd", "ab");
        }

        private void TestPropertyFunction(string expression, string propertyName, string propertyValue, string expected)
        {
            var properties = new PropertyDictionary<ProjectPropertyInstance>();
            properties.Set(ProjectPropertyInstance.Create(propertyName, propertyValue));
            var expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(properties);
            string result = expander.ExpandIntoStringLeaveEscaped(expression, ExpanderOptions.ExpandProperties, MockElementLocation.Instance);
            result.ShouldBe(expected);
        }

        [Fact]
        public void ExpandItemVectorFunctions_GetPathsOfAllDirectoriesAbove()
        {
            // Directory structure:
            // <temp>\
            //    alpha\
            //        .proj
            //        One.cs
            //        beta\
            //            Two.cs
            //            Three.cs
            //        gamma\
            using (var env = TestEnvironment.Create())
            {
                var root = env.CreateFolder();

                var alpha = root.CreateDirectory("alpha");
                var projectFile = env.CreateFile(alpha, ".proj",
                    @"<Project>
  <ItemGroup>
    <Compile Include=""One.cs"" />
    <Compile Include=""beta\Two.cs"" />
    <Compile Include=""beta\Three.cs"" />
  </ItemGroup>
  <ItemGroup>
    <MyDirectories Include=""@(Compile->GetPathsOfAllDirectoriesAbove())"" />
  </ItemGroup>
</Project>");

                var beta = alpha.CreateDirectory("beta");
                var gamma = alpha.CreateDirectory("gamma");

                ProjectInstance projectInstance = new ProjectInstance(projectFile.Path);
                ICollection<ProjectItemInstance> myDirectories = projectInstance.GetItems("MyDirectories");

                var includes = myDirectories.Select(i => i.EvaluatedInclude);
                includes.ShouldBeUnique();
                includes.ShouldContain(root.Path);
                includes.ShouldContain(alpha.Path);
                includes.ShouldContain(beta.Path);
                includes.ShouldNotContain(gamma.Path);
            }
        }

        [Fact]
        public void ExpandItemVectorFunctions_GetPathsOfAllDirectoriesAbove_ReturnCanonicalPaths()
        {
            // Directory structure:
            // <temp>\
            //    alpha\
            //        .proj
            //        One.cs
            //        beta\
            //            Two.cs
            //    gamma\
            //        Three.cs
            using (var env = TestEnvironment.Create())
            {
                var root = env.CreateFolder();

                var alpha = root.CreateDirectory("alpha");
                var projectFile = env.CreateFile(alpha, ".proj",
                    @"<Project>
  <ItemGroup>
    <Compile Include=""One.cs"" />
    <Compile Include=""beta\Two.cs"" />
    <Compile Include=""..\gamma\Three.cs"" />
  </ItemGroup>
  <ItemGroup>
    <MyDirectories Include=""@(Compile->GetPathsOfAllDirectoriesAbove())"" />
  </ItemGroup>
</Project>");

                var beta = alpha.CreateDirectory("beta");
                var gamma = root.CreateDirectory("gamma");

                ProjectInstance projectInstance = new ProjectInstance(projectFile.Path);
                ICollection<ProjectItemInstance> myDirectories = projectInstance.GetItems("MyDirectories");

                var includes = myDirectories.Select(i => i.EvaluatedInclude);
                includes.ShouldBeUnique();
                includes.ShouldContain(root.Path);
                includes.ShouldContain(alpha.Path);
                includes.ShouldContain(beta.Path);
                includes.ShouldContain(gamma.Path);
            }
        }

        [Fact]
        public void ExpandItemVectorFunctions_Combine()
        {
            using (var env = TestEnvironment.Create())
            {
                var root = env.CreateFolder();

                var projectFile = env.CreateFile(root, ".proj",
                    @"<Project>
  <ItemGroup>
    <MyDirectory Include=""Alpha;Beta;Alpha\Gamma"" />
  </ItemGroup>
  <ItemGroup>
    <Squiggle Include=""@(MyDirectory->Combine('.squiggle'))"" />
  </ItemGroup>
</Project>");

                ProjectInstance projectInstance = new ProjectInstance(projectFile.Path);
                ICollection<ProjectItemInstance> squiggles = projectInstance.GetItems("Squiggle");

                var expectedAlphaSquigglePath = Path.Combine("Alpha", ".squiggle");
                var expectedBetaSquigglePath = Path.Combine("Beta", ".squiggle");
                var expectedAlphaGammaSquigglePath = Path.Combine("Alpha", "Gamma", ".squiggle");
                squiggles.Select(i => i.EvaluatedInclude).ShouldBe(new[]
                {
                    expectedAlphaSquigglePath,
                    expectedBetaSquigglePath,
                    expectedAlphaGammaSquigglePath
                }, Case.Insensitive);
            }
        }

        [Fact]
        public void ExpandItemVectorFunctions_Exists_Files()
        {
            // Directory structure:
            // <temp>\
            //    .proj
            //    alpha\
            //        One.cs   // exists
            //        Two.cs   // does not exist
            //        Three.cs // exists
            //        Four.cs  // does not exist
            using (var env = TestEnvironment.Create())
            {
                var root = env.CreateFolder();

                var projectFile = env.CreateFile(root, ".proj",
                    @"<Project>
  <ItemGroup>
    <PotentialCompile Include=""alpha\One.cs"" />
    <PotentialCompile Include=""alpha\Two.cs"" />
    <PotentialCompile Include=""alpha\Three.cs"" />
    <PotentialCompile Include=""alpha\Four.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""@(PotentialCompile->Exists())"" />
  </ItemGroup>
</Project>");

                var alpha = root.CreateDirectory("alpha");
                var one = alpha.CreateFile("One.cs");
                var three = alpha.CreateFile("Three.cs");

                ProjectInstance projectInstance = new ProjectInstance(projectFile.Path);
                ICollection<ProjectItemInstance> squiggleItems = projectInstance.GetItems("Compile");

                var alphaOnePath = Path.Combine("alpha", "One.cs");
                var alphaThreePath = Path.Combine("alpha", "Three.cs");
                squiggleItems.Select(i => i.EvaluatedInclude).ShouldBe(new[] { alphaOnePath, alphaThreePath }, Case.Insensitive);
            }
        }

        [Fact]
        public void ExpandItemVectorFunctions_Exists_Directories()
        {
            // Directory structure:
            // <temp>\
            //    .proj
            //    alpha\
            //        beta\    // exists
            //        gamma\   // does not exist
            //        delta\   // exists
            //        epsilon\ // does not exist
            using (var env = TestEnvironment.Create())
            {
                var root = env.CreateFolder();

                var projectFile = env.CreateFile(root, ".proj",
                    @"<Project>
  <ItemGroup>
    <PotentialDirectory Include=""alpha\beta"" />
    <PotentialDirectory Include=""alpha\gamma"" />
    <PotentialDirectory Include=""alpha\delta"" />
    <PotentialDirectory Include=""alpha\epsilon"" />
  </ItemGroup>
  <ItemGroup>
    <MyDirectory Include=""@(PotentialDirectory->Exists())"" />
  </ItemGroup>
</Project>");

                var alpha = root.CreateDirectory("alpha");
                var beta = alpha.CreateDirectory("beta");
                var delta = alpha.CreateDirectory("delta");

                ProjectInstance projectInstance = new ProjectInstance(projectFile.Path);
                ICollection<ProjectItemInstance> squiggleItems = projectInstance.GetItems("MyDirectory");

                var alphaBetaPath = Path.Combine("alpha", "beta");
                var alphaDeltaPath = Path.Combine("alpha", "delta");
                squiggleItems.Select(i => i.EvaluatedInclude).ShouldBe(new[] { alphaBetaPath, alphaDeltaPath }, Case.Insensitive);
            }
        }
    }
}
