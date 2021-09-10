// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Xunit;



namespace Microsoft.Build.UnitTests
{
    public class ExpressionTreeTest
    {
        /// <summary>
        /// </summary>
        [Fact]
        public void SimpleEvaluationTests()
        {
            Parser p = new Parser();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            AssertParseEvaluate(p, "true", expander, true);
            AssertParseEvaluate(p, "on", expander, true);
            AssertParseEvaluate(p, "yes", expander, true);
            AssertParseEvaluate(p, "false", expander, false);
            AssertParseEvaluate(p, "off", expander, false);
            AssertParseEvaluate(p, "no", expander, false);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void EqualityTests()
        {
            Parser p = new Parser();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            AssertParseEvaluate(p, "true == on", expander, true);
            AssertParseEvaluate(p, "TrUe == On", expander, true);
            AssertParseEvaluate(p, "true != false", expander, true);
            AssertParseEvaluate(p, "true==!false", expander, true);
            AssertParseEvaluate(p, "4 != 5", expander, true);
            AssertParseEvaluate(p, "-4 < 4", expander, true);
            AssertParseEvaluate(p, "5 == +5", expander, true);
            AssertParseEvaluate(p, "4 == 4.0", expander, true);
            AssertParseEvaluate(p, "4 == 4.0", expander, true);
            AssertParseEvaluate(p, ".45 == '.45'", expander, true);
            AssertParseEvaluate(p, "4 == '4'", expander, true);
            AssertParseEvaluate(p, "'0' == '4'", expander, false);
            AssertParseEvaluate(p, "4 == 0x0004", expander, true);
            AssertParseEvaluate(p, "0.0 == 0", expander, true);
            AssertParseEvaluate(p, "simplestring == 'simplestring'", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void RelationalTests()
        {
            Parser p = new Parser();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            AssertParseEvaluate(p, "1234 < 1235", expander, true);
            AssertParseEvaluate(p, "1234 <= 1235", expander, true);
            AssertParseEvaluate(p, "1235 < 1235", expander, false);
            AssertParseEvaluate(p, "1234 <= 1234", expander, true);
            AssertParseEvaluate(p, "1235 <= 1234", expander, false);
            AssertParseEvaluate(p, "1235 > 1234", expander, true);
            AssertParseEvaluate(p, "1235 >= 1235", expander, true);
            AssertParseEvaluate(p, "1235 >= 1234", expander, true);
            AssertParseEvaluate(p, "0.0==0", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void AndandOrTests()
        {
            Parser p = new Parser();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            AssertParseEvaluate(p, "true == on and 1234 < 1235", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void FunctionTests()
        {
            Parser p = new Parser();
            GenericExpressionNode tree;
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>(), new ItemDictionary<ProjectItemInstance>());
            expander.Metadata = new StringMetadataTable(null);
            bool value;

            string fileThatMustAlwaysExist = FileUtilities.GetTemporaryFile();
            File.WriteAllText(fileThatMustAlwaysExist, "foo");
            string command = "Exists('" + fileThatMustAlwaysExist + "')";
            tree = p.Parse(command, ParserOptions.AllowAll, ElementLocation.EmptyLocation);

            ConditionEvaluator.IConditionEvaluationState state =
                            new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                                (
                                    command,
                                    expander,
                                    ExpanderOptions.ExpandAll,
                                    null,
                                    Directory.GetCurrentDirectory(),
                                    ElementLocation.EmptyLocation,
                                    FileSystems.Default
                                );

            value = tree.Evaluate(state);
            Assert.True(value);

            if (File.Exists(fileThatMustAlwaysExist))
            {
                File.Delete(fileThatMustAlwaysExist);
            }

            AssertParseEvaluate(p, "Exists('c:\\IShouldntExist.sys')", expander, false);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void PropertyTests()
        {
            Parser p = new Parser();

            var propertyBag = new PropertyDictionary<ProjectPropertyInstance>();
            propertyBag.Set(ProjectPropertyInstance.Create("foo", "true"));
            propertyBag.Set(ProjectPropertyInstance.Create("bar", "yes"));
            propertyBag.Set(ProjectPropertyInstance.Create("one", "1"));
            propertyBag.Set(ProjectPropertyInstance.Create("onepointzero", "1.0"));
            propertyBag.Set(ProjectPropertyInstance.Create("two", "2"));
            propertyBag.Set(ProjectPropertyInstance.Create("simple", "simplestring"));
            propertyBag.Set(ProjectPropertyInstance.Create("complex", "This is a complex string"));
            propertyBag.Set(ProjectPropertyInstance.Create("c1", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("c2", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("x86", "x86"));
            propertyBag.Set(ProjectPropertyInstance.Create("no", "no"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, new ItemDictionary<ProjectItemInstance>());
            AssertParseEvaluate(p, "$(foo)", expander, true);
            AssertParseEvaluate(p, "!$(foo)", expander, false);
            // Test properties with strings
            AssertParseEvaluate(p, "$(simple) == 'simplestring'", expander, true);
            AssertParseEvaluate(p, "'simplestring' == $(simple)", expander, true);
            AssertParseEvaluate(p, "'foo' != $(simple)", expander, true);
            AssertParseEvaluate(p, "'simplestring' == '$(simple)'", expander, true);
            AssertParseEvaluate(p, "$(simple) == simplestring", expander, true);
            AssertParseEvaluate(p, "$(x86) == x86", expander, true);
            AssertParseEvaluate(p, "$(x86)==x86", expander, true);
            AssertParseEvaluate(p, "x86==$(x86)", expander, true);
            AssertParseEvaluate(p, "$(c1) == $(c2)", expander, true);
            AssertParseEvaluate(p, "'$(c1)' == $(c2)", expander, true);
            AssertParseEvaluate(p, "$(c1) != $(simple)", expander, true);
            AssertParseEvaluate(p, "$(c1) == $(c2)", expander, true);
            // Test properties with numbers
            AssertParseEvaluate(p, "$(one) == $(onepointzero)", expander, true);
            AssertParseEvaluate(p, "$(one) <= $(two)", expander, true);
            AssertParseEvaluate(p, "$(two) > $(onepointzero)", expander, true);
            AssertParseEvaluate(p, "$(one) != $(two)", expander, true);
            AssertParseEvaluate(p, "'$(no)'==false", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void ItemListTests()
        {
            Parser p = new Parser();

            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "foo.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "bar.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "baz.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Boolean", "true", parentProject.FullPath));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>(), itemBag);

            AssertParseEvaluate(p, "@(Compile) == 'foo.cs;bar.cs;baz.cs'", expander, true);
            AssertParseEvaluate(p, "@(Compile,' ') == 'foo.cs bar.cs baz.cs'", expander, true);
            AssertParseEvaluate(p, "@(Compile,'') == 'foo.csbar.csbaz.cs'", expander, true);
            AssertParseEvaluate(p, "@(Compile->'%(Filename)') == 'foo;bar;baz'", expander, true);
            AssertParseEvaluate(p, "@(Compile -> 'temp\\%(Filename).xml', ' ') == 'temp\\foo.xml temp\\bar.xml temp\\baz.xml'", expander, true);
            AssertParseEvaluate(p, "@(Compile->'', '') == ''", expander, true);
            AssertParseEvaluate(p, "@(Compile->'') == ';;'", expander, true);
            AssertParseEvaluate(p, "@(Compile->'%(Nonexistent)', '') == ''", expander, true);
            AssertParseEvaluate(p, "@(Compile->'%(Nonexistent)') == ';;'", expander, true);
            AssertParseEvaluate(p, "@(Boolean)", expander, true);
            AssertParseEvaluate(p, "@(Boolean) == true", expander, true);
            AssertParseEvaluate(p, "'@(Empty, ';')' == ''", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void StringExpansionTests()
        {
            Parser p = new Parser();

            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "foo.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "bar.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "baz.cs", parentProject.FullPath));

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();
            propertyBag.Set(ProjectPropertyInstance.Create("foo", "true"));
            propertyBag.Set(ProjectPropertyInstance.Create("bar", "yes"));
            propertyBag.Set(ProjectPropertyInstance.Create("one", "1"));
            propertyBag.Set(ProjectPropertyInstance.Create("onepointzero", "1.0"));
            propertyBag.Set(ProjectPropertyInstance.Create("two", "2"));
            propertyBag.Set(ProjectPropertyInstance.Create("simple", "simplestring"));
            propertyBag.Set(ProjectPropertyInstance.Create("complex", "This is a complex string"));
            propertyBag.Set(ProjectPropertyInstance.Create("c1", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("c2", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("TestQuote", "Contains'Quote'"));
            propertyBag.Set(ProjectPropertyInstance.Create("AnotherTestQuote", "Here's Johnny!"));
            propertyBag.Set(ProjectPropertyInstance.Create("Atsign", "Test the @ replacement"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, itemBag);

            AssertParseEvaluate(p, "'simplestring: true foo.cs;bar.cs;baz.cs' == '$(simple): $(foo) @(compile)'", expander, true);
            AssertParseEvaluate(p, "'$(c1) $(c2)' == 'Another (complex) one. Another (complex) one.'", expander, true);
            AssertParseEvaluate(p, "'CONTAINS%27QUOTE%27' == '$(TestQuote)'", expander, true);
            AssertParseEvaluate(p, "'Here%27s Johnny!' == '$(AnotherTestQuote)'", expander, true);
            AssertParseEvaluate(p, "'Test the %40 replacement' == $(Atsign)", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void ComplexTests()
        {
            Parser p = new Parser();
            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "foo.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "bar.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "baz.cs", parentProject.FullPath));

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();
            propertyBag.Set(ProjectPropertyInstance.Create("foo", "true"));
            propertyBag.Set(ProjectPropertyInstance.Create("bar", "yes"));
            propertyBag.Set(ProjectPropertyInstance.Create("one", "1"));
            propertyBag.Set(ProjectPropertyInstance.Create("onepointzero", "1.0"));
            propertyBag.Set(ProjectPropertyInstance.Create("two", "2"));
            propertyBag.Set(ProjectPropertyInstance.Create("simple", "simplestring"));
            propertyBag.Set(ProjectPropertyInstance.Create("complex", "This is a complex string"));
            propertyBag.Set(ProjectPropertyInstance.Create("c1", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("c2", "Another (complex) one."));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, itemBag);

            AssertParseEvaluate(p, "(($(foo) != 'two' and $(bar)) and 5 >= 1) or $(one) == 1", expander, true);
            AssertParseEvaluate(p, "(($(foo) != 'twoo' or !$(bar)) and 5 >= 1) or $(two) == 1", expander, true);
            AssertParseEvaluate(p, "!((($(foo) != 'twoo' or !$(bar)) and 5 >= 1) or $(two) == 1)", expander, false);
        }


        /// <summary>
        /// Make sure when a non number is used in an expression which expects a numeric value that a error is emitted.
        /// </summary>
        [Fact]
        public void InvalidItemInConditionEvaluation()
        {
            Parser p = new Parser();
            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "a", parentProject.FullPath));

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, itemBag);

            AssertParseEvaluateThrow(p, "@(Compile) > 0", expander, null);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void OldSyntaxTests()
        {
            Parser p = new Parser();
            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "foo.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "bar.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "baz.cs", parentProject.FullPath));

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();

            propertyBag.Set(ProjectPropertyInstance.Create("foo", "true"));
            propertyBag.Set(ProjectPropertyInstance.Create("bar", "yes"));
            propertyBag.Set(ProjectPropertyInstance.Create("one", "1"));
            propertyBag.Set(ProjectPropertyInstance.Create("onepointzero", "1.0"));
            propertyBag.Set(ProjectPropertyInstance.Create("two", "2"));
            propertyBag.Set(ProjectPropertyInstance.Create("simple", "simplestring"));
            propertyBag.Set(ProjectPropertyInstance.Create("complex", "This is a complex string"));
            propertyBag.Set(ProjectPropertyInstance.Create("c1", "Another (complex) one."));
            propertyBag.Set(ProjectPropertyInstance.Create("c2", "Another (complex) one."));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, itemBag);

            AssertParseEvaluate(p, "(($(foo) != 'two' and $(bar)) and 5 >= 1) or $(one) == 1", expander, true);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void ConditionedPropertyUpdateTests()
        {
            Parser p = new Parser();
            ProjectInstance parentProject = new ProjectInstance(ProjectRootElement.Create());
            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "foo.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "bar.cs", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "Compile", "baz.cs", parentProject.FullPath));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>(), itemBag);
            Dictionary<string, List<string>> conditionedProperties = new Dictionary<string, List<string>>();
            ConditionEvaluator.IConditionEvaluationState state =
                               new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                                   (
                                       String.Empty,
                                       expander,
                                       ExpanderOptions.ExpandAll,
                                       conditionedProperties,
                                       Directory.GetCurrentDirectory(),
                                       ElementLocation.EmptyLocation,
                                       FileSystems.Default
                                   );
            AssertParseEvaluate(p, "'0' == '1'", expander, false, state);
            Assert.Empty(conditionedProperties);

            AssertParseEvaluate(p, "$(foo) == foo", expander, false, state);
            Assert.Single(conditionedProperties);
            List<string> properties = conditionedProperties["foo"];
            Assert.Single(properties);

            AssertParseEvaluate(p, "'$(foo)' != 'bar'", expander, true, state);
            Assert.Single(conditionedProperties);
            properties = conditionedProperties["foo"];
            Assert.Equal(2, properties.Count);

            AssertParseEvaluate(p, "'$(branch)|$(build)|$(platform)' == 'lab22dev|debug|x86'", expander, false, state);
            Assert.Equal(4, conditionedProperties.Count);
            properties = conditionedProperties["foo"];
            Assert.Equal(2, properties.Count);
            properties = conditionedProperties["branch"];
            Assert.Single(properties);
            properties = conditionedProperties["build"];
            Assert.Single(properties);
            properties = conditionedProperties["platform"];
            Assert.Single(properties);

            AssertParseEvaluate(p, "'$(branch)|$(build)|$(platform)' == 'lab21|debug|x86'", expander, false, state);
            Assert.Equal(4, conditionedProperties.Count);
            properties = conditionedProperties["foo"];
            Assert.Equal(2, properties.Count);
            properties = conditionedProperties["branch"];
            Assert.Equal(2, properties.Count);
            properties = conditionedProperties["build"];
            Assert.Single(properties);
            properties = conditionedProperties["platform"];
            Assert.Single(properties);

            AssertParseEvaluate(p, "'$(branch)|$(build)|$(platform)' == 'lab23|retail|ia64'", expander, false, state);
            Assert.Equal(4, conditionedProperties.Count);
            properties = conditionedProperties["foo"];
            Assert.Equal(2, properties.Count);
            properties = conditionedProperties["branch"];
            Assert.Equal(3, properties.Count);
            properties = conditionedProperties["build"];
            Assert.Equal(2, properties.Count);
            properties = conditionedProperties["platform"];
            Assert.Equal(2, properties.Count);
            DumpDictionary(conditionedProperties);
        }

        private static void DumpDictionary(Dictionary<string, List<string>> propertyDictionary)
        {
            foreach (KeyValuePair<string, List<String>> entry in propertyDictionary)
            {
                Console.Write("  {0}:\t", entry.Key);

                List<String> properties = entry.Value;

                foreach (string property in properties)
                {
                    Console.Write("{0}, ", property);
                }
                Console.WriteLine();
            }
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void NotTests()
        {
            Console.WriteLine("NegationParseTest()");
            Parser p = new Parser();

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();
            propertyBag.Set(ProjectPropertyInstance.Create("foo", "4"));
            propertyBag.Set(ProjectPropertyInstance.Create("bar", "32"));

            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, new ItemDictionary<ProjectItemInstance>());

            AssertParseEvaluate(p, "!true", expander, false);
            AssertParseEvaluate(p, "!(true)", expander, false);
            AssertParseEvaluate(p, "!($(foo) <= 5)", expander, false);
            AssertParseEvaluate(p, "!($(foo) <= 5 and $(bar) >= 15)", expander, false);
        }

        private void AssertParseEvaluate(Parser p, string expression, Expander<ProjectPropertyInstance, ProjectItemInstance> expander, bool expected)
        {
            AssertParseEvaluate(p, expression, expander, expected, null);
        }

        private void AssertParseEvaluate(Parser p, string expression, Expander<ProjectPropertyInstance, ProjectItemInstance> expander, bool expected, ConditionEvaluator.IConditionEvaluationState state)
        {
            if (expander.Metadata == null)
            {
                expander.Metadata = new StringMetadataTable(null);
            }

            GenericExpressionNode tree = p.Parse(expression, ParserOptions.AllowAll, MockElementLocation.Instance);

            if (state == null)
            {
                state =
                new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                    (
                        String.Empty,
                        expander,
                        ExpanderOptions.ExpandAll,
                        null,
                        Directory.GetCurrentDirectory(),
                        ElementLocation.EmptyLocation,
                        FileSystems.Default
                    );
            }

            bool result = tree.Evaluate(state);
            Assert.Equal(expected, result);
        }


        private void AssertParseEvaluateThrow(Parser p, string expression, Expander<ProjectPropertyInstance, ProjectItemInstance> expander)
        {
            AssertParseEvaluateThrow(p, expression, expander, null);
        }

        private void AssertParseEvaluateThrow(Parser p, string expression, Expander<ProjectPropertyInstance, ProjectItemInstance> expander, ConditionEvaluator.IConditionEvaluationState state)
        {
            bool fExceptionCaught;

            if (expander.Metadata == null)
            {
                expander.Metadata = new StringMetadataTable(null);
            }

            try
            {
                fExceptionCaught = false;
                GenericExpressionNode tree = p.Parse(expression, ParserOptions.AllowAll, MockElementLocation.Instance);
                if (state == null)
                {
                    state =
                    new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                        (
                            String.Empty,
                            expander,
                            ExpanderOptions.ExpandAll,
                            null,
                            Directory.GetCurrentDirectory(),
                            ElementLocation.EmptyLocation,
                            FileSystems.Default
                        );
                }
                tree.Evaluate(state);
            }
            catch (InvalidProjectFileException e)
            {
                Console.WriteLine(e.BaseMessage);
                fExceptionCaught = true;
            }

            Assert.True(fExceptionCaught);
        }

        /// <summary>
        /// </summary>
        [Fact]
        public void NegativeTests()
        {
            Parser p = new Parser();
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(new PropertyDictionary<ProjectPropertyInstance>());

            AssertParseEvaluateThrow(p, "foo", expander);
            AssertParseEvaluateThrow(p, "0", expander);
            AssertParseEvaluateThrow(p, "$(platform) == xx > 1==2", expander);
            AssertParseEvaluateThrow(p, "!0", expander);
            AssertParseEvaluateThrow(p, ">", expander);
            AssertParseEvaluateThrow(p, "true!=false==", expander);
            AssertParseEvaluateThrow(p, "()", expander);
            AssertParseEvaluateThrow(p, "!1", expander);
            AssertParseEvaluateThrow(p, "true!=false==true", expander);
            AssertParseEvaluateThrow(p, "'a'>'a'", expander);
            AssertParseEvaluateThrow(p, "=='x'", expander);
            AssertParseEvaluateThrow(p, "==", expander);
            AssertParseEvaluateThrow(p, "1==(2", expander);
            AssertParseEvaluateThrow(p, "'a'==('a'=='a')", expander);
            AssertParseEvaluateThrow(p, "true == on and ''", expander);
            AssertParseEvaluateThrow(p, "'' or 'true'", expander);
        }
    }
}
