using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared.FileSystem;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Build.UnitTests
{
    public class ExpressionTest : IDisposable
    {
        private readonly ITestOutputHelper output;


        private static readonly string[] FilesWithExistenceChecks = { "a", "c", "a;b", "a'b", ";", "'" };

        private readonly Expander<ProjectPropertyInstance, ProjectItemInstance> _expander;

        public static readonly IEnumerable<object[]> TrueTests = new []
        {
            "true or (SHOULDNOTEVALTHIS)", // short circuit
            "(true and false) or true",
            "false or true or false",
            "(true) and (true)",
            "false or !false",
            "($(a) or true)",
            "('$(c)'==1 and (!false))",
            "@(z -> '%(filename).z', '$')=='xxx.z$yyy.z'",
            "@(w -> '%(definingprojectname).barproj') == 'foo.barproj'",
            "false or (false or (false or (false or (false or (true)))))",
            "!(true and false)",
            "$(and)=='and'",
            "0x1==1.0",
            "0xa==10",
            "0<0.1",
            "+4>-4",
            "'-$(c)'==-1",
            "$(a)==faLse",
            "$(a)==oFF",
            "$(a)==no",
            "$(a)!=true",
            "$(b)== True",
            "$(b)==on",
            "$(b)==yes",
            "$(b)!=1",
            "$(c)==1",
            "$(d)=='xxx'",
            "$(d)==$(e)",
            "$(d)=='$(e)'",
            "@(y)==$(d)",
            "'@(z)'=='xxx;yyy'",
            "$(a)==$(a)",
            "'1'=='1'",
            "'1'==1",
            "1\n==1",
            "1\t==\t\r\n1",
            "123=='0123.0'",
            "123==123",
            "123==0123",
            "123==0123.0",
            "123!=0123.01",
            "1.2.3<=1.2.3.0",
            "12.23.34==12.23.34",
            "0.8.0.0<8.0.0",
            "1.1.2>1.0.1.2",
            "8.1>8.0.16.23",
            "8.0.0>=8",
            "6<=6.0.0.1",
            "7>6.8.2",
            "4<5.9.9135.4",
            "3!=3.0.0",
            "1.2.3.4.5.6.7==1.2.3.4.5.6.7",
            "00==0",
            "0==0.0",
            "1\n\t==1",
            "+4==4",
            "44==+44.0 and -44==-44.0",
            "false==no",
            "true==yes",
            "true==!false",
            "yes!=no",
            "false!=1",
            "$(c)>0",
            "!$(a)",
            "$(b)",
            "($(d)==$(e))",
            "!true==false",
            "a_a==a_a",
            "a_a=='a_a'",
            "_a== _a",
            "@(y -> '%(filename)')=='xxx'",
            "@(z -> '%(filename)', '!')=='xxx!yyy'",
            "'xxx!yyy'==@(z -> '%(filename)', '!')",
            "'$(a)'==(false)",
            "('$(a)'==(false))",
            "1>0",
            "2<=2",
            "2<=3",
            "1>=1",
            "1>=-1",
            "-1==-1",
            "-1  <  0",
            "(1==1)and('a'=='a')",
            "(true) and ($(a)==off)",
            "(true) and ($(d)==xxx)",
            "(false)     or($(d)==xxx)",
            "!(false)and!(false)",
            "'and'=='AND'",
            "$(d)=='XxX'",
            "true or true or false",
            "false or true or !true or'1'",
            "$(a) or $(b)",
            "$(a) or true",
            "!!true",
            "'$(e)1@(y)'=='xxx1xxx'",
            "0x11==17",
            "0x01a==26",
            "0xa==0x0A",
            "@(x)",
            "'%77'=='w'",
            "'%zz'=='%zz'",
            "true or 1",
            "true==!false",
            "(!(true))=='off'",
            "@(w)>0",
            "1<=@(w)",
            "%(culture)=='FRENCH'",
            "'%(culture) fries' == 'FRENCH FRIES' ",
            @"'%(HintPath)' == ''",
            @"%(HintPath) != 'c:\myassemblies\foo.dll'",
            "exists('a')",
            "exists(a)",
            "exists('a%3bb')", /* semicolon */
            "exists('a%27b')", /* apostrophe */
            "exists('a;c')", /* items */
            "exists($(a_semi_c))",
            "exists($(a_escapedsemi_b))",
            "exists('$(a_escapedsemi_b)')",
            "exists($(a_escapedapos_b))",
            "exists('$(a_escapedapos_b)')",
            "exists($(a_apos_b))",
            "exists('$(a_apos_b)')",
            "exists(@(v))",
            "exists('@(v)')",
            "exists('%3b')",
            "exists('%27')",
            "exists('@(v);@(nonexistent)')",
            @"HASTRAILINGSLASH('foo\')",
            @"!HasTrailingSlash('foo')",
            @"HasTrailingSlash('foo/')",
            @"HasTrailingSlash($(has_trailing_slash))",
            "'59264.59264' == '59264.59264'",
            "1" + new String('0', 500) + "==" + "1" + new String('0', 500), /* too big for double, eval as string */
            "'1" + new String('0', 500) + "'=='" + "1" + new String('0', 500) + "'" /* too big for double, eval as string */
        }.Select(s => new[] {s});

        public static readonly IEnumerable<object[]> FalseTests = new [] {
            "false and SHOULDNOTEVALTHIS", // short circuit
            "$(a)!=no",
            "$(b)==1.1",
            "$(c)==$(a)",
            "$(d)!=$(e)",
            "!$(b)",
            "false or false or false",
            "false and !((true and false))",
            "on and off",
            "(true) and (false)",
            "false or (false or (false or (false or (false or (false)))))",
            "!$(b)and true",
            "1==a",
            "!($(d)==$(e))",
            "$(a) and true",
            "true==1",
            "false==0",
            "(!(true))=='x'",
            "oops==false",
            "oops==!false",
            "%(culture) == 'english'",
            "'%(culture) fries' == 'english fries' ",
            @"'%(HintPath)' == 'c:\myassemblies\foo.dll'",
            @"%(HintPath) == 'c:\myassemblies\foo.dll'",
            "exists('')",
            "exists(' ')",
            "exists($(nonexistent))",  // DDB #141195
            "exists('$(nonexistent)')",  // DDB #141195
            "exists(@(nonexistent))",  // DDB #141195
            "exists('@(nonexistent)')",  // DDB #141195
            "exists('\t')",
            "exists('@(u)')",
            "exists('$(foo_apos_foo)')",
            "!exists('a')",
            "!exists('a;c')",
            "!exists('$(a_semi_c)')",
            "exists('a;b;c')", /* a and c exist but b does not */
            "exists('b;c;a')",
            "exists('$(a_semi_c);b')",
            "!!!exists(a)",
            "exists('|||||')",
            @"hastrailingslash('foo')",
            @"hastrailingslash('')",
            @"HasTrailingSlash($(nonexistent))",
            "'59264.59264' == '59264.59265'",
            "1.2.0==1.2",
            "$(f)!=$(f)",
            "1.3.5.8>1.3.6.8",
            "0.8.0.0>=1.0",
            "8.0.0<=8.0",
            "8.1.2<8",
            "1" + new String('0', 500) + "==2", /* too big for double, eval as string */
            "'1" + new String('0', 500) + "'=='2'", /* too big for double, eval as string */
            "'1" + new String('0', 500) + "'=='01" + new String('0', 500) + "'" /* too big for double, eval as string */
        }.Select(s => new[] { s });

        public static readonly IEnumerable<object[]> ErrorTests = new [] {
            "$",
            "$(",
            "$()",
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
            "%00",
            "\n",
            "\t",
            "+-1==1",
            "1==-+1",
            "1==+0xa",
            "!$(c)",
            "'a'==('a'=='a')",
            "'a'!=('a'=='a')",
            "('a'=='a')!=a",
            "('a'=='a')==a",
            "!'x'",
            "!'$(d)'",
            "ab#==ab#",
            "#!=#",
            "$(d)$(e)=='xxxxxx'",
            "1=1=1",
            "'a'=='a'=='a'",
            "1 > 'x'",
            "x1<=1",
            "1<=x",
            "1>x",
            "x<x",
            "@(x)<x",
            "x>x",
            "x>=x",
            "x<=x",
            "x>1",
            "x>=1",
            "1>=x",
            "@(y)<=1",
            "1<=@(z)",
            "1>$(d)",
            "$(c)@(y)>1",
            "'$(c)@(y)'>1",
            "$(d)>=1",
            "1>=$(b)",
            "1> =0",
            "or true",
            "1 and",
            "and",
            "or",
            "not",
            "not true",
            "()",
            "(a)",
            "!",
            "or=or",
            "1==",
            "1= =1",
            "=",
            "'true",
            "'false''",
            "'a'=='a",
            "('a'=='a'",
            "('a'=='a'))",
            "'a'=='a')",
            "!and",
            "@(a)@(x)!=1",
            "@(a) @(x)!=1",
            "$(a==off",
            "=='x'",
            "==",
            "!0",
            ">",
            "true!=false==",
            "true!=false==true",
            "()",
            "!1",
            "1==(2",
            "$(a)==x>1==2",
            "'a'>'a'",
            "0",
            "$(a)>0",
            "!$(e)",
            "1<=1<=1",
            "true $(and) true",
            "--1==1",
            "$(and)==and",
            "!@#$%^&*",
            "-($(c))==-1",
            "a==b or $(d)",
            "false or $()",
            "$(d) or true",
            "%(Culture) or true",
            "@(nonexistent) and true",
            "$(nonexistent) and true",
            "@(nonexistent)",
            "$(nonexistent)",
            "@(z) and true",
            "@() and true",
            "@()",
            "$()",
            "1",
            "1 or true",
            "false or 1",
            "1 and true",
            "true and 1",
            "!1",
            "false or !1",
            "false or 'aa'",
            "true blah",
            "existsX",
            "!",
            "nonexistentfunction('xyz')",
            "exists(@(v)x)",
            "exists(@(v)$(nonexistent))",
            "exists('@(v)$(a)')",
            "exists(|||||)",
            "HasTrailingSlash(a,'b')",
            "HasTrailingSlash(,,)",
            "1.2.3==1,2,3"
        }.Select(s => new[] { s });

        /// <summary>
        /// Set up expression tests by creating files for existence checks.
        /// </summary>
        public ExpressionTest(ITestOutputHelper output)
        {
            this.output = output;

            ItemDictionary<ProjectItemInstance> itemBag = new ItemDictionary<ProjectItemInstance>();

            // Dummy project instance to own the items.
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.FullPath = @"c:\abc\foo.proj";

            ProjectInstance parentProject = new ProjectInstance(xml);

            itemBag.Add(new ProjectItemInstance(parentProject, "u", "a'b;c", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "v", "a", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "w", "1", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "x", "true", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "y", "xxx", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "z", "xxx", parentProject.FullPath));
            itemBag.Add(new ProjectItemInstance(parentProject, "z", "yyy", parentProject.FullPath));

            PropertyDictionary<ProjectPropertyInstance> propertyBag = new PropertyDictionary<ProjectPropertyInstance>();

            propertyBag.Set(ProjectPropertyInstance.Create("a", "no"));
            propertyBag.Set(ProjectPropertyInstance.Create("b", "true"));
            propertyBag.Set(ProjectPropertyInstance.Create("c", "1"));
            propertyBag.Set(ProjectPropertyInstance.Create("d", "xxx"));
            propertyBag.Set(ProjectPropertyInstance.Create("e", "xxx"));
            propertyBag.Set(ProjectPropertyInstance.Create("f", "1.9.5"));
            propertyBag.Set(ProjectPropertyInstance.Create("and", "and"));
            propertyBag.Set(ProjectPropertyInstance.Create("a_semi_c", "a;c"));
            propertyBag.Set(ProjectPropertyInstance.Create("a_apos_b", "a'b"));
            propertyBag.Set(ProjectPropertyInstance.Create("foo_apos_foo", "foo'foo"));
            propertyBag.Set(ProjectPropertyInstance.Create("a_escapedsemi_b", "a%3bb"));
            propertyBag.Set(ProjectPropertyInstance.Create("a_escapedapos_b", "a%27b"));
            propertyBag.Set(ProjectPropertyInstance.Create("has_trailing_slash", @"foo\"));

            Dictionary<string, string> metadataDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadataDictionary["Culture"] = "french";
            StringMetadataTable itemMetadata = new StringMetadataTable(metadataDictionary);

            _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(propertyBag, itemBag, itemMetadata);

            foreach (string file in FilesWithExistenceChecks)
            {
                using (File.CreateText(file)) { }
            }
        }

        /// <summary>
        /// Clean up files created for these tests.
        /// </summary>
        public void Dispose()
        {
            foreach (string file in FilesWithExistenceChecks)
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        /// <summary>
        /// A whole bunch of conditionals that should be true
        /// (many coincidentally like existing QA tests) to give breadth coverage.
        /// Please add more cases as they arise.
        /// </summary>
        [Theory]
        [MemberData(nameof(TrueTests))]
        public void EvaluateAVarietyOfTrueExpressions(string expression)
        {
            Parser p = new Parser();
            GenericExpressionNode tree = p.Parse(expression, ParserOptions.AllowAll, ElementLocation.EmptyLocation);
            ConditionEvaluator.IConditionEvaluationState state =
                new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                    (
                    expression,
                    _expander,
                    ExpanderOptions.ExpandAll,
                    null,
                    Directory.GetCurrentDirectory(),
                    ElementLocation.EmptyLocation,
                    FileSystems.Default
                    );

            Assert.True(tree.Evaluate(state), "expected true from '" + expression + "'");
        }

        /// <summary>
        /// A whole bunch of conditionals that should be false
        /// (many coincidentally like existing QA tests) to give breadth coverage.
        /// Please add more cases as they arise.
        /// </summary>
        [Theory]
        [MemberData(nameof(FalseTests))]
        public void EvaluateAVarietyOfFalseExpressions(string expression)
        {
            Parser p = new Parser();
            GenericExpressionNode tree = p.Parse(expression, ParserOptions.AllowAll, ElementLocation.EmptyLocation);
            ConditionEvaluator.IConditionEvaluationState state =
                new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                    (
                    expression,
                    _expander,
                    ExpanderOptions.ExpandAll,
                    null,
                    Directory.GetCurrentDirectory(),
                    ElementLocation.EmptyLocation,
                    FileSystems.Default
                    );

            Assert.False(tree.Evaluate(state), "expected false from '" + expression + "' and got true");
        }

        /// <summary>
        /// A whole bunch of conditionals that should produce errors
        /// (many coincidentally like existing QA tests) to give breadth coverage.
        /// Please add more cases as they arise.
        /// </summary>
        [Theory]
        [MemberData(nameof(ErrorTests))]
        public void EvaluateAVarietyOfErrorExpressions(string expression)
        {
            // If an expression is invalid,
            //      - Parse may throw, or
            //      - Evaluate may throw, or
            //      - Evaluate may return false causing its caller EvaluateCondition to throw
            bool caughtException = false;
            try
            {
                Parser p = new Parser();
                var tree = p.Parse(expression, ParserOptions.AllowAll, ElementLocation.EmptyLocation);

                ConditionEvaluator.IConditionEvaluationState state =
                    new ConditionEvaluator.ConditionEvaluationState<ProjectPropertyInstance, ProjectItemInstance>
                        (
                        expression,
                        _expander,
                        ExpanderOptions.ExpandAll,
                        null,
                        Directory.GetCurrentDirectory(),
                        ElementLocation.EmptyLocation,
                        FileSystems.Default
                        );

                tree.Evaluate(state);
            }
            catch (InvalidProjectFileException ex)
            {
                output.WriteLine(expression + " caused '" + ex.Message + "'");
                caughtException = true;
            }
            Assert.True(caughtException,
                "expected '" + expression + "' to not parse or not be evaluated");
        }
    }
}
