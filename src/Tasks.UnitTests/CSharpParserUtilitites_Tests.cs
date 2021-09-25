// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Tasks;
using Xunit;

namespace Microsoft.Build.UnitTests
{
    sealed public class CSharpParserUtilititesTests
    {
        // Try just and empty file
        [Fact]
        public void EmptyFile()
        {
            AssertParse("", null);
        }

        // Simplest case of getting a fully-qualified class name from
        // a c# file.
        [Theory]
        [InlineData("namespace MyNamespace { class MyClass {} }")]
        [InlineData("namespace MyNamespace ; class MyClass {} ")] // file-scoped namespaces
        public void Simple(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.MyClass");
        }

        [Theory]
        [InlineData("namespace /**/ MyNamespace /**/ { /**/ class /**/ MyClass/**/{}} //")]
        [InlineData("namespace /**/ MyNamespace /**/ ; /**/ class /**/ MyClass/**/{} //")] // file-scoped namespaces
        public void EmbeddedComment(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.MyClass");
        }

        [Theory]
        [InlineData("namespace MyNamespace{class MyClass{}}")]
        [InlineData("namespace MyNamespace;class MyClass{}")] // file-scoped namespaces
        public void MinSpace(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.MyClass");
        }

        [Fact]
        public void NoNamespace()
        {
            AssertParse("class MyClass{}", "MyClass");
        }

        [Theory]
        [InlineData("/*namespace MyNamespace { */ class MyClass {} /* } */")]
        [InlineData("/*namespace MyNamespace ; */ class MyClass {}")] // file-scoped namespaces
        public void SneakyComment(string fileContents)
        {
            AssertParse(fileContents, "MyClass");
        }

        [Theory]
        [InlineData("namespace MyNamespace.Feline { class MyClass {} }")]
        [InlineData("namespace MyNamespace.Feline ; class MyClass {} ")] // file-scoped namespaces
        public void CompoundNamespace(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.Feline.MyClass");
        }

        [Fact]
        public void NestedNamespace()
        {
            AssertParse("namespace MyNamespace{ namespace Feline {class MyClass {} }}", "MyNamespace.Feline.MyClass");
        }

        [Fact]
        public void NestedNamespace2()
        {
            AssertParse("namespace MyNamespace{ namespace Feline {namespace Bovine{public sealed class MyClass {} }} }", "MyNamespace.Feline.Bovine.MyClass");
        }

        [Fact]
        public void NestedCompoundNamespace()
        {
            AssertParse("namespace MyNamespace/**/.A{ namespace Feline . B {namespace Bovine.C {sealed class MyClass {} }} }", "MyNamespace.A.Feline.B.Bovine.C.MyClass");
        }

        [Theory]
        [InlineData("namespace MyNamespace{class Feline{}class Bovine}")]
        [InlineData("namespace MyNamespace;class Feline{}class Bovine")] // file-scoped namespaces
        public void DoubleClass(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.Feline");
        }

        [Theory]
        [InlineData("namespace MyNamespace{class @class{}}")]
        [InlineData("namespace MyNamespace;class @class{}")] // file-scoped namespaces
        public void EscapedKeywordClass(string fileContents)
        {
            AssertParse(fileContents, "MyNamespace.class");
        }

        [Fact]
        public void LeadingUnderscore()
        {
            AssertParse("namespace _MyNamespace{class _MyClass{}}", "_MyNamespace._MyClass");
        }

        [Fact]
        public void InterveningNamespaces()
        {
            AssertParse("namespace MyNamespace { namespace XXX {} class MyClass {} }", "MyNamespace.MyClass");
        }


        [Fact]
        public void SkipPeerNamespaces()
        {
            AssertParse("namespace XXX {} namespace MyNamespace {  class MyClass {} }", "MyNamespace.MyClass");
        }

        [Fact]
        public void SolitaryNamespaceSyntaxError()
        {
            AssertParse("namespace", null);
        }

        [Fact]
        public void NamespaceNamespaceSyntaxError()
        {
            AssertParse("namespace namespace", null);
        }

        [Fact(Skip = "This should be a syntax error. But we can't tell because the preprocessor doesn't work yet.")]
        public void NamelessNamespaceSyntaxError()
        {
            AssertParse("namespace { class MyClass {} }", null);
        }

        [Fact]
        public void ScopelessNamespaceClassSyntaxError()
        {
            AssertParse("namespace class {}", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void NamespaceDotDotSyntaxError()
        {
            AssertParse("namespace poo..i { class MyClass {} }", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void DotNamespaceSyntaxError()
        {
            AssertParse("namespace .i { class MyClass {} }", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void NamespaceDotNamespaceSyntaxError()
        {
            AssertParse("namespace i { namespace .j {class MyClass {}} }", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void NamespaceClassDotClassSyntaxError()
        {
            AssertParse("namespace i { namespace j {class a.b {}} }", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void NamespaceCloseScopeSyntaxError()
        {
            AssertParse("namespace i } class a {} }", null);
        }

        [Fact(Skip = "If we went to the trouble of tracking open and closing scopes, we really should do something like build up a parse tree. Too much hassle, just for this simple function.")]
        public void NamespaceEmbeddedScopeSyntaxError()
        {
            AssertParse("namespace i { {} class a {} }", null);
        }

        [Fact(Skip = "This should be a syntax error, but since the preprocessor isn't working, we can't be sure.")]
        public void ScopelessNamespaceSyntaxError()
        {
            AssertParse("namespace i; namespace j { class a {} }", null);
        }

        [Fact]
        public void AssemblyAttributeBool()
        {
            AssertParse("[assembly :AssemblyDelaySign(false)] namespace i { class a { } }", "i.a");
        }

        [Theory]
        [InlineData("[assembly :MyString(\"namespace\")] namespace i { class a { } }")]
        [InlineData("[assembly :MyString(\"namespace\")] namespace i; class a { }")]
        public void AssemblyAttributeString(string fileContents)
        {
            AssertParse(fileContents, "i.a");
        }

        [Fact]
        public void AssemblyAttributeInt()
        {
            AssertParse("[assembly :MyInt(55)] namespace i { class a { } }", "i.a");
        }

        [Fact]
        public void AssemblyAttributeReal()
        {
            AssertParse("[assembly :MyReal(5.5)] namespace i { class a { } }", "i.a");
        }

        [Fact]
        public void AssemblyAttributeNull()
        {
            AssertParse("[assembly :MyNull(null)] namespace i { class a { } }", "i.a");
        }

        [Fact]
        public void AssemblyAttributeChar()
        {
            AssertParse("[assembly :MyChar('a')] namespace i { class a { } }", "i.a");
        }


        [Fact]
        public void ClassAttributeBool()
        {
            AssertParse("namespace i { [ClassDelaySign(false)] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeString()
        {
            AssertParse("namespace i { [MyString(\"class b\")] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeInt()
        {
            AssertParse("namespace i { [MyInt(55)] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeReal()
        {
            AssertParse("namespace i { [MyReal(5.5)] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeNull()
        {
            AssertParse("[namespace i { MyNull(null)] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeChar()
        {
            AssertParse("namespace i { [MyChar('a')] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeCharIsCloseScope()
        {
            AssertParse("namespace i { [MyChar('\x0000')] class a { } }", "i.a");
        }

        [Fact]
        public void ClassAttributeStringIsCloseScope()
        {
            AssertParse("namespace i { [MyString(\"}\")] class a { } }", "i.a");
        }

        [Theory]
        [InlineData("namespace n { public struct s {  enum e {} } class c {} }")]
        [InlineData("namespace n; public struct s {  enum e {} } class c {}")] // file-scoped namespace
        public void NameSpaceStructEnum(string fileContents)
        {
            AssertParse(fileContents, "n.c");
        }

        [Fact]
        public void PreprocessorControllingTwoNamespaces()
        {
            // This works by coincidence since preprocessor directives are currently ignored.
            // Note: If the condition were #if (true), the result would still be n1.c
            AssertParse(@"
#if (false)
namespace n1
#else
namespace n2
#endif    
{ class c {} }
                ", "n2.c");
        }

        /// <summary>
        /// The test "PreprocessorControllingTwoNamespaces" reveals that preprocessor directives are ignored.
        /// This means that in the case of many namespaces before curly braces (despite that being invalid C#)
        /// the last namespace would win. This test explicitly tests that.
        /// </summary>
        [Theory]
        [InlineData(@"
namespace n1
    namespace n2
    namespace n3
    namespace n4
    { class c { } }", "n4.c")]
        [InlineData(@"
namespace n1;
namespace n2;
namespace n3;
namespace n4;
class c {} ", "n1.n2.n3.n4.c")]
        public void MultipleNamespaces_InvalidCSharp(string fileContents, string expected)
        {
            // This works by coincidence since preprocessor directives are currently ignored.
            AssertParse(fileContents, expected);
        }

        /// <summary>
        /// Note: Preprocessor conditions are not implemented
        /// </summary>
        [Theory]
        [InlineData(@"
#if (false)
namespace n1
#else
using a=b;
namespace n2
#endif    
{ class c {} }", "n2.c")]
        [InlineData(@"
#if (false)
namespace n1;
#else
using a=b;
namespace n2;
#endif    
{ class c {} }", "n1.n2.c")]
        public void PreprocessorControllingTwoNamespacesWithInterveningKeyword(string fileContents, string expected)
        {
            AssertParse(fileContents, expected);
        }

        [Theory]
        [InlineData(@"
#if MY_CONSTANT                
namespace i 
{
    #region Put the class in a region
    class a 
    {
    }     
    #endregion
}
#endif // MY_CONSTANT ")]
        [InlineData(@"
#if MY_CONSTANT                
namespace i;
    #region Put the class in a region
    class a 
    {
    }     
    #endregion
#endif // MY_CONSTANT")]
        public void Preprocessor(string fileContents)
        {
            AssertParse(fileContents, "i.a");
        }

        [Fact(Skip = "Preprocessor is not yet implemented.")]
        public void PreprocessorNamespaceInFalsePreprocessorBlock()
        {
            AssertParse
            (
                @"
#if (false)             
namespace i 
{
#endif
    class a 
    {
    }     
#if (false)             
namespace i 
}
#endif
                ", "a");
        }



        [Theory]
        [InlineData(@"
namespace n2
// namespace n1
{ class c {} }")]
        [InlineData(@"
namespace n2;
// namespace n1
class c {}")]
        public void Regress_Mutation_SingleLineCommentsShouldBeIgnored(string fileContents)
        {
            AssertParse(fileContents, "n2.c");
        }

        /*
        * Method:  AssertParse
        *
        * Parse 'source' as C# source code and get the first class name fully-qualified
        * with namespace information. That class name must match the expected class name.
        */
        private static void AssertParse(string source, string expectedClassName)
        {
            ExtractedClassName className = CSharpParserUtilities.GetFirstClassNameFullyQualified
            (
                StreamHelpers.StringToStream(source)
            );

            Assert.Equal(expectedClassName, className.Name);
        }
    }
}



