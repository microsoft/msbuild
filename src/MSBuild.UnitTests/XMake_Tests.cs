// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.Build.CommandLine;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests.Shared;
using Xunit;
using Xunit.Abstractions;
using Shouldly;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.UnitTests
{
    public class XMakeAppTests : IDisposable
    {
#if USE_MSBUILD_DLL_EXTN
        private const string MSBuildExeName = "MSBuild.dll";
#else
        private const string MSBuildExeName = "MSBuild.exe";
#endif

        private readonly ITestOutputHelper _output;
        private readonly TestEnvironment _env;

        public XMakeAppTests(ITestOutputHelper output)
        {
            _output = output;
            _env = UnitTests.TestEnvironment.Create(_output);
        }

        private const string AutoResponseFileName = "MSBuild.rsp";

        [Fact]
        public void GatherCommandLineSwitchesTwoProperties()
        {
            CommandLineSwitches switches = new CommandLineSwitches();

            var arguments = new List<string>();
            arguments.AddRange(new[] { "/p:a=b", "/p:c=d" });

            MSBuildApp.GatherCommandLineSwitches(arguments, switches);

            string[] parameters = switches[CommandLineSwitches.ParameterizedSwitch.Property];
            parameters[0].ShouldBe("a=b");
            parameters[1].ShouldBe("c=d");
        }

        [Fact]
        public void GatherCommandLineSwitchesMaxCpuCountWithArgument()
        {
            CommandLineSwitches switches = new CommandLineSwitches();

            var arguments = new List<string>();
            arguments.AddRange(new[] { "/m:2" });

            MSBuildApp.GatherCommandLineSwitches(arguments, switches);

            string[] parameters = switches[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount];
            parameters[0].ShouldBe("2");
            parameters.Length.ShouldBe(1);

            switches.HaveErrors().ShouldBeFalse();
        }

        [Fact]
        public void GatherCommandLineSwitchesMaxCpuCountWithoutArgument()
        {
            CommandLineSwitches switches = new CommandLineSwitches();

            var arguments = new List<string>();
            arguments.AddRange(new[] { "/m:3", "/m" });

            MSBuildApp.GatherCommandLineSwitches(arguments, switches);

            string[] parameters = switches[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount];
            parameters[1].ShouldBe(Convert.ToString(NativeMethodsShared.GetLogicalCoreCount()));
            parameters.Length.ShouldBe(2);

            switches.HaveErrors().ShouldBeFalse();
        }

        /// <summary>
        ///  /m: should be an error, unlike /m:1 and /m
        /// </summary>
        [Fact]
        public void GatherCommandLineSwitchesMaxCpuCountWithoutArgumentButWithColon()
        {
            CommandLineSwitches switches = new CommandLineSwitches();

            var arguments = new List<string>();
            arguments.AddRange(new[] { "/m:" });

            MSBuildApp.GatherCommandLineSwitches(arguments, switches);

            string[] parameters = switches[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount];
            parameters.Length.ShouldBe(0);

            switches.HaveErrors().ShouldBeTrue();
        }

        /*
         * Quoting Rules:
         *
         * A string is considered quoted if it is enclosed in double-quotes. A double-quote can be escaped with a backslash, or it
         * is automatically escaped if it is the last character in an explicitly terminated quoted string. A backslash itself can
         * be escaped with another backslash IFF it precedes a double-quote, otherwise it is interpreted literally.
         *
         * e.g.
         *      abc"cde"xyz         --> "cde" is quoted
         *      abc"xyz             --> "xyz" is quoted (the terminal double-quote is assumed)
         *      abc"xyz"            --> "xyz" is quoted (the terminal double-quote is explicit)
         *
         *      abc\"cde"xyz        --> "xyz" is quoted (the terminal double-quote is assumed)
         *      abc\\"cde"xyz       --> "cde" is quoted
         *      abc\\\"cde"xyz      --> "xyz" is quoted (the terminal double-quote is assumed)
         *
         *      abc"""xyz           --> """ is quoted
         *      abc""""xyz          --> """ and "xyz" are quoted (the terminal double-quote is assumed)
         *      abc"""""xyz         --> """ is quoted
         *      abc""""""xyz        --> """ and """ are quoted
         *      abc"cde""xyz        --> "cde"" is quoted
         *      abc"xyz""           --> "xyz"" is quoted (the terminal double-quote is explicit)
         *
         *      abc""xyz            --> nothing is quoted
         *      abc""cde""xyz       --> nothing is quoted
         */

        [Fact]
        public void SplitUnquotedTest()
        {
            // nothing quoted
            var sa = QuotingUtilities.SplitUnquoted("abcdxyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abcdxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abcc dxyz");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abcc");
            sa[1].ShouldBe("dxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abcc;dxyz", ';');
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abcc");
            sa[1].ShouldBe("dxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abc,c;dxyz", ';', ',');
            sa.Count.ShouldBe(3);
            sa[0].ShouldBe("abc");
            sa[1].ShouldBe("c");
            sa[2].ShouldBe("dxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abc,c;dxyz", 2, false, false, out var emptySplits, ';', ',');
            emptySplits.ShouldBe(0);
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc");
            sa[1].ShouldBe("c;dxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abc,,;dxyz", int.MaxValue, false, false, out emptySplits, ';', ',');
            emptySplits.ShouldBe(2);
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc");
            sa[1].ShouldBe("dxyz");

            // nothing quoted
            sa = QuotingUtilities.SplitUnquoted("abc,,;dxyz", int.MaxValue, true, false, out emptySplits, ';', ',');
            emptySplits.ShouldBe(0);
            sa.Count.ShouldBe(4);
            sa[0].ShouldBe("abc");
            sa[1].ShouldBe(string.Empty);
            sa[2].ShouldBe(string.Empty);
            sa[3].ShouldBe("dxyz");

            // "c d" is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"c d\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"c d\"xyz");

            // "x z" is quoted (the terminal double-quote is assumed)
            sa = QuotingUtilities.SplitUnquoted("abc\"x z");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"x z");

            // "x z" is quoted (the terminal double-quote is explicit)
            sa = QuotingUtilities.SplitUnquoted("abc\"x z\"");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"x z\"");

            // "x z" is quoted (the terminal double-quote is assumed)
            sa = QuotingUtilities.SplitUnquoted("abc\\\"cde\"x z");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\\\"cde\"x z");

            // "x z" is quoted (the terminal double-quote is assumed)
            // "c e" is not quoted
            sa = QuotingUtilities.SplitUnquoted("abc\\\"c e\"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\\\"c");
            sa[1].ShouldBe("e\"x z");

            // "c e" is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\\\\\"c e\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\\\\\"c e\"xyz");

            // "c e" is quoted
            // "x z" is not quoted
            sa = QuotingUtilities.SplitUnquoted("abc\\\\\"c e\"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\\\\\"c e\"x");
            sa[1].ShouldBe("z");

            // "x z" is quoted (the terminal double-quote is assumed)
            sa = QuotingUtilities.SplitUnquoted("abc\\\\\\\"cde\"x z");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\\\\\\\"cde\"x z");

            // "xyz" is quoted (the terminal double-quote is assumed)
            // "c e" is not quoted
            sa = QuotingUtilities.SplitUnquoted("abc\\\\\\\"c e\"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\\\\\\\"c");
            sa[1].ShouldBe("e\"x z");

            // """ is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"\"\"xyz");

            // " "" is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\" \"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\" \"\"xyz");

            // "x z" is quoted (the terminal double-quote is assumed)
            sa = QuotingUtilities.SplitUnquoted("abc\"\" \"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\"\"");
            sa[1].ShouldBe("\"x z");

            // " "" and "xyz" are quoted (the terminal double-quote is assumed)
            sa = QuotingUtilities.SplitUnquoted("abc\" \"\"\"x z");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\" \"\"\"x z");

            // """ is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\"\"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"\"\"\"\"xyz");

            // """ is quoted
            // "x z" is not quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\"\"\"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\"\"\"\"\"x");
            sa[1].ShouldBe("z");

            // " "" is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\" \"\"\"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\" \"\"\"\"xyz");

            // """ and """ are quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\"\"\"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"\"\"\"\"\"xyz");

            // " "" and " "" are quoted
            sa = QuotingUtilities.SplitUnquoted("abc\" \"\"\" \"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\" \"\"\" \"\"xyz");

            // """ and """ are quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\" \"\"\"xyz");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\"\"\"");
            sa[1].ShouldBe("\"\"\"xyz");

            // """ and """ are quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"\"\" \"\"\"x z");
            sa.Count.ShouldBe(3);
            sa[0].ShouldBe("abc\"\"\"");
            sa[1].ShouldBe("\"\"\"x");
            sa[2].ShouldBe("z");

            // "c e"" is quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"c e\"\"xyz");
            sa.Count.ShouldBe(1);
            sa[0].ShouldBe("abc\"c e\"\"xyz");

            // "c e"" is quoted
            // "x z" is not quoted
            sa = QuotingUtilities.SplitUnquoted("abc\"c e\"\"x z");
            sa.Count.ShouldBe(2);
            sa[0].ShouldBe("abc\"c e\"\"x");
            sa[1].ShouldBe("z");

            // nothing is quoted
            sa = QuotingUtilities.SplitUnquoted("a c\"\"x z");
            sa.Count.ShouldBe(3);
            sa[0].ShouldBe("a");
            sa[1].ShouldBe("c\"\"x");
            sa[2].ShouldBe("z");

            // nothing is quoted
            sa = QuotingUtilities.SplitUnquoted("a c\"\"c e\"\"x z");
            sa.Count.ShouldBe(4);
            sa[0].ShouldBe("a");
            sa[1].ShouldBe("c\"\"c");
            sa[2].ShouldBe("e\"\"x");
            sa[3].ShouldBe("z");
        }

        [Fact]
        public void UnquoteTest()
        {
            // "cde" is quoted
            QuotingUtilities.Unquote("abc\"cde\"xyz", out var doubleQuotesRemoved).ShouldBe("abccdexyz");
            doubleQuotesRemoved.ShouldBe(2);

            // "xyz" is quoted (the terminal double-quote is assumed)
            QuotingUtilities.Unquote("abc\"xyz", out doubleQuotesRemoved).ShouldBe("abcxyz");
            doubleQuotesRemoved.ShouldBe(1);

            // "xyz" is quoted (the terminal double-quote is explicit)
            QuotingUtilities.Unquote("abc\"xyz\"", out doubleQuotesRemoved).ShouldBe("abcxyz");
            doubleQuotesRemoved.ShouldBe(2);

            // "xyz" is quoted (the terminal double-quote is assumed)
            QuotingUtilities.Unquote("abc\\\"cde\"xyz", out doubleQuotesRemoved).ShouldBe("abc\"cdexyz");
            doubleQuotesRemoved.ShouldBe(1);

            // "cde" is quoted
            QuotingUtilities.Unquote("abc\\\\\"cde\"xyz", out doubleQuotesRemoved).ShouldBe("abc\\cdexyz");
            doubleQuotesRemoved.ShouldBe(2);

            // "xyz" is quoted (the terminal double-quote is assumed)
            QuotingUtilities.Unquote("abc\\\\\\\"cde\"xyz", out doubleQuotesRemoved).ShouldBe("abc\\\"cdexyz");
            doubleQuotesRemoved.ShouldBe(1);

            // """ is quoted
            QuotingUtilities.Unquote("abc\"\"\"xyz", out doubleQuotesRemoved).ShouldBe("abc\"xyz");
            doubleQuotesRemoved.ShouldBe(2);

            // """ and "xyz" are quoted (the terminal double-quote is assumed)
            QuotingUtilities.Unquote("abc\"\"\"\"xyz", out doubleQuotesRemoved).ShouldBe("abc\"xyz");
            doubleQuotesRemoved.ShouldBe(3);

            // """ is quoted
            QuotingUtilities.Unquote("abc\"\"\"\"\"xyz", out doubleQuotesRemoved).ShouldBe("abc\"xyz");
            doubleQuotesRemoved.ShouldBe(4);

            // """ and """ are quoted
            QuotingUtilities.Unquote("abc\"\"\"\"\"\"xyz", out doubleQuotesRemoved).ShouldBe("abc\"\"xyz");
            doubleQuotesRemoved.ShouldBe(4);

            // "cde"" is quoted
            QuotingUtilities.Unquote("abc\"cde\"\"xyz", out doubleQuotesRemoved).ShouldBe("abccde\"xyz");
            doubleQuotesRemoved.ShouldBe(2);

            // "xyz"" is quoted (the terminal double-quote is explicit)
            QuotingUtilities.Unquote("abc\"xyz\"\"", out doubleQuotesRemoved).ShouldBe("abcxyz\"");
            doubleQuotesRemoved.ShouldBe(2);

            // nothing is quoted
            QuotingUtilities.Unquote("abc\"\"xyz", out doubleQuotesRemoved).ShouldBe("abcxyz");
            doubleQuotesRemoved.ShouldBe(2);

            // nothing is quoted
            QuotingUtilities.Unquote("abc\"\"cde\"\"xyz", out doubleQuotesRemoved).ShouldBe("abccdexyz");
            doubleQuotesRemoved.ShouldBe(4);
        }

        [Fact]
        public void ExtractSwitchParametersTest()
        {
            string commandLineArg = "\"/p:foo=\"bar";
            string unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out var doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":\"foo=\"bar");
            doubleQuotesRemovedFromArg.ShouldBe(2);

            commandLineArg = "\"/p:foo=bar\"";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar");
            doubleQuotesRemovedFromArg.ShouldBe(2);

            commandLineArg = "/p:foo=bar";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar");
            doubleQuotesRemovedFromArg.ShouldBe(0);

            commandLineArg = "\"\"/p:foo=bar\"";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar\"");
            doubleQuotesRemovedFromArg.ShouldBe(3);

            // this test is totally unreal -- we'd never attempt to extract switch parameters if the leading character is not a
            // switch indicator (either '-' or '/') -- here the leading character is a double-quote
            commandLineArg = "\"\"\"/p:foo=bar\"";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "/p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar\"");
            doubleQuotesRemovedFromArg.ShouldBe(3);

            commandLineArg = "\"/pr\"operty\":foo=bar";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "property", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar");
            doubleQuotesRemovedFromArg.ShouldBe(3);

            commandLineArg = "\"/pr\"op\"\"erty\":foo=bar\"";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "property", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":foo=bar");
            doubleQuotesRemovedFromArg.ShouldBe(6);

            commandLineArg = "/p:\"foo foo\"=\"bar bar\";\"baz=onga\"";
            unquotedCommandLineArg = QuotingUtilities.Unquote(commandLineArg, out doubleQuotesRemovedFromArg);
            MSBuildApp.ExtractSwitchParameters(commandLineArg, unquotedCommandLineArg, doubleQuotesRemovedFromArg, "p", unquotedCommandLineArg.IndexOf(':')).ShouldBe(":\"foo foo\"=\"bar bar\";\"baz=onga\"");
            doubleQuotesRemovedFromArg.ShouldBe(6);
        }

        [Fact]
        public void GetLengthOfSwitchIndicatorTest()
        {
            var commandLineSwitchWithSlash = "/Switch";
            var commandLineSwitchWithSingleDash = "-Switch";
            var commandLineSwitchWithDoubleDash = "--Switch";

            var commandLineSwitchWithNoneOrIncorrectIndicator = "zSwitch";

            MSBuildApp.GetLengthOfSwitchIndicator(commandLineSwitchWithSlash).ShouldBe(1);
            MSBuildApp.GetLengthOfSwitchIndicator(commandLineSwitchWithSingleDash).ShouldBe(1);
            MSBuildApp.GetLengthOfSwitchIndicator(commandLineSwitchWithDoubleDash).ShouldBe(2);

            MSBuildApp.GetLengthOfSwitchIndicator(commandLineSwitchWithNoneOrIncorrectIndicator).ShouldBe(0);
        }

        [Theory]
        [InlineData("-?")]
        [InlineData("-h")]
        [InlineData("--help")]
        [InlineData(@"/h")]
        public void Help(string indicator)
        {
            MSBuildApp.Execute(@$"c:\bin\msbuild.exe {indicator} ").ShouldBe(MSBuildApp.ExitType.Success);
        }

        [Fact]
        public void ErrorCommandLine()
        {
            MSBuildApp.Execute(@"c:\bin\msbuild.exe -junk").ShouldBe(MSBuildApp.ExitType.SwitchError);

            MSBuildApp.Execute(@"msbuild.exe -t").ShouldBe(MSBuildApp.ExitType.SwitchError);

            MSBuildApp.Execute(@"msbuild.exe @bogus.rsp").ShouldBe(MSBuildApp.ExitType.InitializationError);
        }

        [Fact]
        public void ValidVerbosities()
        {
            MSBuildApp.ProcessVerbositySwitch("Q").ShouldBe(LoggerVerbosity.Quiet);
            MSBuildApp.ProcessVerbositySwitch("quiet").ShouldBe(LoggerVerbosity.Quiet);
            MSBuildApp.ProcessVerbositySwitch("m").ShouldBe(LoggerVerbosity.Minimal);
            MSBuildApp.ProcessVerbositySwitch("minimal").ShouldBe(LoggerVerbosity.Minimal);
            MSBuildApp.ProcessVerbositySwitch("N").ShouldBe(LoggerVerbosity.Normal);
            MSBuildApp.ProcessVerbositySwitch("normal").ShouldBe(LoggerVerbosity.Normal);
            MSBuildApp.ProcessVerbositySwitch("d").ShouldBe(LoggerVerbosity.Detailed);
            MSBuildApp.ProcessVerbositySwitch("detailed").ShouldBe(LoggerVerbosity.Detailed);
            MSBuildApp.ProcessVerbositySwitch("diag").ShouldBe(LoggerVerbosity.Diagnostic);
            MSBuildApp.ProcessVerbositySwitch("DIAGNOSTIC").ShouldBe(LoggerVerbosity.Diagnostic);
        }

        [Fact]
        public void InvalidVerbosity()
        {
            Should.Throw<CommandLineSwitchException>(() =>
            {
                MSBuildApp.ProcessVerbositySwitch("loquacious");
            }
           );
        }
        [Fact]
        public void ValidMaxCPUCountSwitch()
        {
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "1" }).ShouldBe(1);
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "2" }).ShouldBe(2);
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "3" }).ShouldBe(3);
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "4" }).ShouldBe(4);
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "8" }).ShouldBe(8);
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "63" }).ShouldBe(63);

            // Should pick last value
            MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "8", "4" }).ShouldBe(4);
        }

        [Fact]
        public void InvalidMaxCPUCountSwitch1()
        {
            Should.Throw<CommandLineSwitchException>(() =>
            {
                MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "-1" });
            }
           );
        }

        [Fact]
        public void InvalidMaxCPUCountSwitch2()
        {
            Should.Throw<CommandLineSwitchException>(() =>
            {
                MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "0" });
            }
           );
        }

        [Fact]
        public void InvalidMaxCPUCountSwitch3()
        {
            Should.Throw<CommandLineSwitchException>(() =>
            {
                // Too big
                MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "foo" });
            }
           );
        }

        [Fact]
        public void InvalidMaxCPUCountSwitch4()
        {
            Should.Throw<CommandLineSwitchException>(() =>
            {
                MSBuildApp.ProcessMaxCPUCountSwitch(new[] { "1025" });
            }
           );
        }

#if FEATURE_CULTUREINFO_CONSOLE_FALLBACK
        /// <summary>
        /// Regression test for bug where the MSBuild.exe command-line app
        /// would sometimes set the UI culture to just "en" which is considered a "neutral" UI
        /// culture, which didn't allow for certain kinds of string formatting/parsing.
        /// </summary>
        /// <remarks>
        /// fr-FR, de-DE, and fr-CA are guaranteed to be available on all BVTs, so we must use one of these
        /// </remarks>
        [Fact]
        public void SetConsoleUICulture()
        {
            Thread thisThread = Thread.CurrentThread;

            // Save the current UI culture, so we can restore it at the end of this unit test.
            CultureInfo originalUICulture = thisThread.CurrentUICulture;

            thisThread.CurrentUICulture = new CultureInfo("fr-FR");
            MSBuildApp.SetConsoleUI();

            // Make sure this doesn't throw an exception.
            string bar = string.Format(CultureInfo.CurrentUICulture, "{0}", 1);

            // Restore the current UI culture back to the way it was at the beginning of this unit test.
            thisThread.CurrentUICulture = originalUICulture;
        }
#endif

#if FEATURE_SYSTEM_CONFIGURATION
        /// <summary>
        /// Invalid configuration file should not dump stack.
        /// </summary>
        [Fact]
        public void ConfigurationInvalid()
        {
            string startDirectory = null;
            string output = null;
            string oldValueForMSBuildOldOM = null;

            try
            {
                oldValueForMSBuildOldOM = Environment.GetEnvironmentVariable("MSBuildOldOM");
                Environment.SetEnvironmentVariable("MSBuildOldOM", "");

                startDirectory = CopyMSBuild();
                var msbuildExeName = Path.GetFileName(RunnerUtilities.PathToCurrentlyRunningMsBuildExe);
                var newPathToMSBuildExe = Path.Combine(startDirectory, msbuildExeName);
                var pathToConfigFile = Path.Combine(startDirectory, msbuildExeName + ".config");

                string configContent = @"<?xml version =""1.0""?>
                                            <configuration>
                                                <configSections>
                                                    <section name=""msbuildToolsets"" type=""Microsoft.Build.Evaluation.ToolsetConfigurationSection, Microsoft.Build, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"" />
                                                    <foo/>
                                                </configSections>
                                                <startup>
                                                    <supportedRuntime version=""v4.0""/>
                                                </startup>
                                                <foo/>
                                                <msbuildToolsets default=""X"">
                                                <foo/>
                                                    <toolset toolsVersion=""X"">
                                                        <foo/>
                                                    <property name=""MSBuildBinPath"" value=""Y""/>
                                                    <foo/>
                                                    </toolset>
                                                <foo/>
                                                </msbuildToolsets>
                                                <foo/>
                                            </configuration>";
                File.WriteAllText(pathToConfigFile, configContent);

                var pathToProjectFile = Path.Combine(startDirectory, "foo.proj");
                string projectString =
                   "<?xml version='1.0' encoding='utf-8'?>" +
                    "<Project xmlns='http://schemas.microsoft.com/developer/msbuild/2003' ToolsVersion='X'>" +
                    "<Target Name='t'></Target>" +
                    "</Project>";
                File.WriteAllText(pathToProjectFile, projectString);

                var msbuildParameters = "\"" + pathToProjectFile + "\"";

                output = RunnerUtilities.ExecMSBuild(newPathToMSBuildExe, msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeFalse();
            }
            catch (Exception ex)
            {
                _output.WriteLine(ex.ToString());
                throw;
            }
            finally
            {
                if (output != null)
                {
                    _output.WriteLine(output);
                }

                try
                {
                    // Process does not let go its lock on the exe file until about 1 millisecond after
                    // p.WaitForExit() returns. Do I know why? No I don't.
                    RobustDelete(startDirectory);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("MSBuildOldOM", oldValueForMSBuildOldOM);
                }
            }

            // If there's a space in the %TEMP% path, the config file is read in the static constructor by the URI class and we catch there;
            // if there's not, we will catch when we try to read the toolsets. Either is fine; we just want to not crash.
            (output.Contains("MSB1043") || output.Contains("MSB4136")).ShouldBeTrue("Output should contain 'MSB1043' or 'MSB4136'");

        }
#endif

        /// <summary>
        /// Try hard to delete a file or directory specified
        /// </summary>
        private void RobustDelete(string path)
        {
            if (path != null)
            {
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            FileUtilities.DeleteWithoutTrailingBackslash(path, true /*and files*/);
                        }
                        else if (File.Exists(path))
                        {
                            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); // make writable
                            File.Delete(path);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(10);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Tests that the environment gets passed on to the node during build.
        /// </summary>
        [Fact]
        public void TestEnvironment()
        {
            string projectString = ObjectModelHelpers.CleanupFileContents(
                   @"<?xml version=""1.0"" encoding=""utf-8""?>
                    <Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace"">
                    <Target Name=""t""><Error Text='Error' Condition=""'$(MyEnvVariable)' == ''""/></Target>
                    </Project>");
            string tempdir = Path.GetTempPath();
            string projectFileName = Path.Combine(tempdir, "msbEnvironmenttest.proj");
            string quotedProjectFileName = "\"" + projectFileName + "\"";

            try
            {
                Environment.SetEnvironmentVariable("MyEnvVariable", "1");
                using (StreamWriter sw = FileUtilities.OpenWrite(projectFileName, false))
                {
                    sw.WriteLine(projectString);
                }
                //Should pass
                MSBuildApp.Execute(@"c:\bin\msbuild.exe " + quotedProjectFileName).ShouldBe(MSBuildApp.ExitType.Success);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MyEnvVariable", null);
                File.Delete(projectFileName);
            }
        }

        [Fact]
        public void MSBuildEngineLogger()
        {
            string projectString =
                   "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
                    "<Target Name=\"t\"><Message Text=\"[Hello]\"/></Target>" +
                    "</Project>";
            string tempdir = Path.GetTempPath();
            string projectFileName = Path.Combine(tempdir, "msbLoggertest.proj");
            string logFile = Path.Combine(tempdir, "logFile");
            string quotedProjectFileName = "\"" + projectFileName + "\"";

            try
            {
                using (StreamWriter sw = FileUtilities.OpenWrite(projectFileName, false))
                {
                    sw.WriteLine(projectString);
                }
                //Should pass
                MSBuildApp.Execute(@$"c:\bin\msbuild.exe /logger:FileLogger,""Microsoft.Build, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"";""LogFile={logFile}"" /verbosity:detailed " + quotedProjectFileName).ShouldBe(MSBuildApp.ExitType.Success);
                File.Exists(logFile).ShouldBeTrue();

                var logFileContents = File.ReadAllText(logFile);

                logFileContents.ShouldContain("Process = ");
                logFileContents.ShouldContain("MSBuild executable path = ");
                logFileContents.ShouldContain("Command line arguments = ");
                logFileContents.ShouldContain("Current directory = ");
                logFileContents.ShouldContain("MSBuild version = ");
                logFileContents.ShouldContain("[Hello]");
            }
            finally
            {
                File.Delete(projectFileName);
                File.Delete(logFile);
            }
        }

        private readonly string _pathToArbitraryBogusFile = NativeMethodsShared.IsWindows // OK on 64 bit as well
                                                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")
                                                        : "/bin/cat";

        /// <summary>
        /// Basic case
        /// </summary>
        [Fact]
        public void GetCommandLine()
        {
            var msbuildParameters = "\"" + _pathToArbitraryBogusFile + "\"" + (NativeMethodsShared.IsWindows ? " /v:diag" : " -v:diag");
            File.Exists(_pathToArbitraryBogusFile).ShouldBeTrue();

            string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
            successfulExit.ShouldBeFalse();

            output.ShouldContain(RunnerUtilities.PathToCurrentlyRunningMsBuildExe + (NativeMethodsShared.IsWindows ? " /v:diag " : " -v:diag ") + _pathToArbitraryBogusFile, Case.Insensitive);
        }

        /// <summary>
        /// Quoted path
        /// </summary>
        [Fact]
        public void GetCommandLineQuotedExe()
        {
            var msbuildParameters = "\"" + _pathToArbitraryBogusFile + "\"" + (NativeMethodsShared.IsWindows ? " /v:diag" : " -v:diag");
            File.Exists(_pathToArbitraryBogusFile).ShouldBeTrue();

            string pathToMSBuildExe = RunnerUtilities.PathToCurrentlyRunningMsBuildExe;
            // This @pathToMSBuildExe is used directly with Process, so don't quote it on
            // Unix
            if (NativeMethodsShared.IsWindows)
            {
                pathToMSBuildExe = "\"" + pathToMSBuildExe + "\"";
            }

            string output = RunnerUtilities.ExecMSBuild(pathToMSBuildExe, msbuildParameters, out var successfulExit);
            successfulExit.ShouldBeFalse();

            output.ShouldContain(RunnerUtilities.PathToCurrentlyRunningMsBuildExe + (NativeMethodsShared.IsWindows ? " /v:diag " : " -v:diag ") + _pathToArbitraryBogusFile, Case.Insensitive);
        }

        /// <summary>
        /// On path
        /// </summary>
        [Fact]
        public void GetCommandLineQuotedExeOnPath()
        {
            string output;
            string current = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(RunnerUtilities.PathToCurrentlyRunningMsBuildExe));

                var msbuildParameters = "\"" + _pathToArbitraryBogusFile + "\"" + (NativeMethodsShared.IsWindows ? " /v:diag" : " -v:diag");

                output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeFalse();
            }
            finally
            {
                Directory.SetCurrentDirectory(current);
            }

            output.ShouldContain(RunnerUtilities.PathToCurrentlyRunningMsBuildExe + (NativeMethodsShared.IsWindows ? " /v:diag " : " -v:diag ") + _pathToArbitraryBogusFile, Case.Insensitive);
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read, and should
        /// take priority over any other response files.
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryFoundImplicitly()
        {
            string directory = _env.DefaultTestDirectory.Path;
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, AutoResponseFileName);

            string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
            File.WriteAllText(projectPath, content);

            string rspContent = "/p:A=1";
            File.WriteAllText(rspPath, rspContent);

            // Find the project in the current directory
            _env.SetCurrentDirectory(directory);

            string output = RunnerUtilities.ExecMSBuild(string.Empty, out var successfulExit);
            successfulExit.ShouldBeTrue();

            output.ShouldContain("[A=1]");
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read, and should
        /// take priority over any other response files.
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryExplicit()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, AutoResponseFileName);

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                string rspContent = "/p:A=1";
                File.WriteAllText(rspPath, rspContent);

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=1]");
            }
            finally
            {
                File.Delete(projectPath);
                File.Delete(rspPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read, and not any random .rsp
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryRandomName()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, "foo.rsp");

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                string rspContent = "/p:A=1";
                File.WriteAllText(rspPath, rspContent);

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=]");
            }
            finally
            {
                File.Delete(projectPath);
                File.Delete(rspPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read,
        /// but lower precedence than the actual command line
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryCommandLineWins()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, AutoResponseFileName);

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                string rspContent = "/p:A=1";
                File.WriteAllText(rspPath, rspContent);

                var msbuildParameters = "\"" + projectPath + "\"" + " /p:A=2";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=2]");
            }
            finally
            {
                File.Delete(projectPath);
                File.Delete(rspPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read,
        /// but lower precedence than the actual command line and higher than the msbuild.rsp next to msbuild.exe
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryWinsOverMainMSBuildRsp()
        {
            string directory = null;
            string exeDirectory = null;

            try
            {
                directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string projectPath = Path.Combine(directory, "my.proj");
                string rspPath = Path.Combine(directory, AutoResponseFileName);

                exeDirectory = CopyMSBuild();
                string exePath = Path.Combine(exeDirectory, MSBuildExeName);
                string mainRspPath = Path.Combine(exeDirectory, AutoResponseFileName);

                Directory.CreateDirectory(exeDirectory);

                File.WriteAllText(mainRspPath, "/p:A=0");

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                File.WriteAllText(rspPath, "/p:A=1");

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(exePath, msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=1]");
            }
            finally
            {
                RobustDelete(directory);
                RobustDelete(exeDirectory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read,
        /// but not if it's the same as the msbuild.exe directory
        /// </summary>
        [Fact]
        public void ProjectDirectoryIsMSBuildExeDirectory()
        {
            string directory = null;

            try
            {
                directory = CopyMSBuild();
                string projectPath = Path.Combine(directory, "my.proj");
                string rspPath = Path.Combine(directory, AutoResponseFileName);
                string exePath = Path.Combine(directory, MSBuildExeName);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                File.WriteAllText(rspPath, "/p:A=1");

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(exePath, msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=1]");
            }
            finally
            {
                RobustDelete(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution with /noautoresponse in, is an error
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryItselfWithNoAutoResponseSwitch()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, AutoResponseFileName);

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                string rspContent = "/p:A=1 /noautoresponse";
                File.WriteAllText(rspPath, rspContent);

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeFalse();

                output.ShouldContain("MSB1027"); // msbuild.rsp cannot have /noautoresponse in it
            }
            finally
            {
                File.Delete(projectPath);
                File.Delete(rspPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be ignored if cmd line has /noautoresponse
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryButCommandLineNoAutoResponseSwitch()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");
            string rspPath = Path.Combine(directory, AutoResponseFileName);

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                string rspContent = "/p:A=1 /noautoresponse";
                File.WriteAllText(rspPath, rspContent);

                var msbuildParameters = "\"" + projectPath + "\" /noautoresponse";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=]");
            }
            finally
            {
                File.Delete(projectPath);
                File.Delete(rspPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// Any msbuild.rsp in the directory of the specified project/solution should be read, and should
        /// take priority over any other response files. Sanity test when there isn't one.
        /// </summary>
        [Fact]
        public void ResponseFileInProjectDirectoryNullCase()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(directory, "my.proj");

            try
            {
                Directory.CreateDirectory(directory);

                string content = ObjectModelHelpers.CleanupFileContents("<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");
                File.WriteAllText(projectPath, content);

                var msbuildParameters = "\"" + projectPath + "\"";

                string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
                successfulExit.ShouldBeTrue();

                output.ShouldContain("[A=]");
            }
            finally
            {
                File.Delete(projectPath);
                FileUtilities.DeleteWithoutTrailingBackslash(directory);
            }
        }

        /// <summary>
        /// A response file should support path replacement (%MSBuildThisFileDirectory% becomes full path to the
        /// rsp file directory).
        /// </summary>
        [Fact]
        public void ResponseFileSupportsThisFileDirectory()
        {
            var content = ObjectModelHelpers.CleanupFileContents(
                "<Project ToolsVersion='msbuilddefaulttoolsversion' xmlns='msbuildnamespace'><Target Name='t'><Warning Text='[A=$(A)]'/></Target></Project>");

            var directory = _env.CreateFolder();
            directory.CreateFile("Directory.Build.rsp", "/p:A=%MSBuildThisFileDirectory%");
            var projectPath = directory.CreateFile("my.proj", content).Path;

            var msbuildParameters = "\"" + projectPath + "\"";

            string output = RunnerUtilities.ExecMSBuild(msbuildParameters, out var successfulExit);
            successfulExit.ShouldBeTrue();

            output.ShouldContain($"[A={directory.Path}{Path.DirectorySeparatorChar}]");
        }

        /// <summary>
        /// Test that low priority builds actually execute with low priority.
        /// </summary>
        [Fact(Skip = "https://github.com/microsoft/msbuild/issues/5229")]
        public void LowPriorityBuild()
        {
            RunPriorityBuildTest(expectedPrority: ProcessPriorityClass.BelowNormal, arguments: "/low");
        }

        /// <summary>
        /// Test that normal builds execute with normal priority.
        /// </summary>
        [Fact(Skip = "https://github.com/microsoft/msbuild/issues/5229")]
        public void NormalPriorityBuild()
        {
            // In case we are already running at a  different priority, validate
            // the build runs as the current priority, and not some hard coded priority.
            ProcessPriorityClass currentPriority = Process.GetCurrentProcess().PriorityClass;
            RunPriorityBuildTest(expectedPrority: currentPriority);
        }

        private void RunPriorityBuildTest(ProcessPriorityClass expectedPrority, params string[] arguments)
        {
            string[] aggregateArguments = arguments.Union(new[] { " /nr:false /v:diag "}).ToArray();

            string contents = ObjectModelHelpers.CleanupFileContents(@"
<Project DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
 <Target Name=""Build"">
    <Message Text=""Task priority is '$([System.Diagnostics.Process]::GetCurrentProcess().PriorityClass)'""/>
 </Target>
</Project>
");
            // Set our test environment variables:
            //  - Disable in proc build to make sure priority is inherited by subprocesses.
            //  - Enable property functions so we can easily read our priority class.
            IDictionary<string, string> environmentVars = new Dictionary<string, string>
            {
                { "MSBUILDNOINPROCNODE", "1"},
                { "DISABLECONSOLECOLOR", "1"},
                { "MSBUILDENABLEALLPROPERTYFUNCTIONS", "1" },
            };

            string logContents = ExecuteMSBuildExeExpectSuccess(contents, envsToCreate: environmentVars, arguments: aggregateArguments);

            string expected = $@"Task priority is '{expectedPrority}'";
            logContents.ShouldContain(expected, () => logContents);
        }

        /// <summary>
        /// Test the default file to build in cases involving at least one solution filter file.
        /// </summary>
        [Theory]
        [InlineData(new[] { "my.proj", "my.sln", "my.slnf" }, "my.sln")]
        [InlineData(new[] { "abc.proj", "bcd.csproj", "slnf.slnf", "other.slnf" }, "abc.proj")]
        [InlineData(new[] { "abc.sln", "slnf.slnf", "abc.slnf" }, "abc.sln")]
        [InlineData(new[] { "abc.csproj", "abc.slnf", "not.slnf" }, "abc.csproj")]
        [InlineData(new[] { "abc.slnf" }, "abc.slnf")]
        public void TestDefaultBuildWithSolutionFilter(string[] projects, string answer)
        {
            string[] extensionsToIgnore = Array.Empty<string>();
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(Array.Empty<string>(), extensionsToIgnore, projectHelper.GetFiles).ShouldBe(answer, StringCompareShould.IgnoreCase);
        }

        #region IgnoreProjectExtensionTests

        /// <summary>
        /// Test the case where the extension is a valid extension but is not a project
        /// file extension. In this case no files should be ignored
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchOneProjNotFoundExtension()
        {
            string[] projects = { "my.proj" };
            string[] extensionsToIgnore = { ".phantomextension" };
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
        }

        /// <summary>
        /// Test the case where two identical extensions are asked to be ignored
        /// </summary>
        [Fact]
        public void TestTwoIdenticalExtensionsToIgnore()
        {
            string[] projects = { "my.proj" };
            string[] extensionsToIgnore = { ".phantomextension", ".phantomextension" };
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
        }

        /// <summary>
        /// Pass a null and an empty list of project extensions to ignore, this simulates the switch not being set on the commandline
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchNullandEmptyProjectsToIgnore()
        {
            string[] projects = { "my.proj" };
            string[] extensionsToIgnore = null;
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"

            extensionsToIgnore = new string[] { };
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
        }

        /// <summary>
        /// Pass in one extension and a null value
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchNullInList()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "my.proj" };
                string[] extensionsToIgnore = { ".phantomextension", null };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
            }
           );
        }

        /// <summary>
        /// Pass in one extension and an empty string
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchEmptyInList()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "my.proj" };
                string[] extensionsToIgnore = { ".phantomextension", string.Empty };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
            }
           );
        }
        /// <summary>
        /// If only a dot is specified then the extension is invalid
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchExtensionWithoutDot()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "my.proj" };
                string[] extensionsToIgnore = { "phantomextension" };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase);
            }
           );
        }
        /// <summary>
        /// Put some junk into the extension, in this case there should be an exception
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchMalformed()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "my.proj" };
                string[] extensionsToIgnore = { ".C:\\boocatmoo.a" };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("my.proj", StringCompareShould.IgnoreCase); // "Expected my.proj to be only project found"
            }
           );
        }
        /// <summary>
        /// Test what happens if there are no project or solution files in the directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchWildcards()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "my.proj" };
                string[] extensionsToIgnore = { ".proj*", ".nativeproj?" };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        [Fact]
        public void TestProcessProjectSwitch()
        {
            string[] projects = { "test.nativeproj", "test.vcproj" };
            string[] extensionsToIgnore = { ".phantomextension", ".vcproj" };
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.nativeproj", StringCompareShould.IgnoreCase); // "Expected test.nativeproj to be only project found"

            projects = new[] { "test.nativeproj", "test.vcproj", "test.proj" };
            extensionsToIgnore = new[] { ".phantomextension", ".vcproj" };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.proj", StringCompareShould.IgnoreCase); // "Expected test.proj to be only project found"

            projects = new[] { "test.nativeproj", "test.vcproj" };
            extensionsToIgnore = new[] { ".vcproj" };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.nativeproj", StringCompareShould.IgnoreCase); // "Expected test.nativeproj to be only project found"

            projects = new[] { "test.proj", "test.sln" };
            extensionsToIgnore = new[] { ".vcproj" };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.sln", StringCompareShould.IgnoreCase); // "Expected test.sln to be only solution found"

            projects = new[] { "test.proj", "test.sln", "test.proj~", "test.sln~" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.sln", StringCompareShould.IgnoreCase); // "Expected test.sln to be only solution found"

            projects = new[] { "test.proj" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.proj", StringCompareShould.IgnoreCase); // "Expected test.proj to be only project found"

            projects = new[] { "test.proj", "test.proj~" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.proj", StringCompareShould.IgnoreCase); // "Expected test.proj to be only project found"

            projects = new[] { "test.sln" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.sln", StringCompareShould.IgnoreCase); // "Expected test.sln to be only solution found"

            projects = new[] { "test.sln", "test.sln~" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.sln", StringCompareShould.IgnoreCase); // "Expected test.sln to be only solution found"

            projects = new[] { "test.sln~", "test.sln" };
            extensionsToIgnore = new string[] { };
            projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.sln", StringCompareShould.IgnoreCase); // "Expected test.sln to be only solution found"
        }

        /// <summary>
        /// Ignore .sln and .vcproj files to replicate Building_DF_LKG functionality
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchReplicateBuildingDFLKG()
        {
            string[] projects = { "test.proj", "test.sln", "Foo.vcproj" };
            string[] extensionsToIgnore = { ".sln", ".vcproj" };
            IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
            MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles).ShouldBe("test.proj"); // "Expected test.proj to be only project found"
        }

        /// <summary>
        /// Test the case where we remove all of the project extensions that exist in the directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchRemovedAllprojects()
        {
            Should.Throw<InitializationException>(() =>
            {
                var projects = new[] { "test.nativeproj", "test.vcproj" };
                var extensionsToIgnore = new[] { ".nativeproj", ".vcproj" };
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Test the case where there is a solution and a project in the same directory but they have different names
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchSlnProjDifferentNames()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "test.proj", "Different.sln" };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Test the case where we have two proj files in the same directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchTwoProj()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "test.proj", "Different.proj" };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Test the case where we have two native project files in the same directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchTwoNative()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "test.nativeproj", "Different.nativeproj" };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Test when there are two solutions in the same directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchTwoSolutions()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "test.sln", "Different.sln" };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Check the case where there are more than two projects in the directory and one is a proj file
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchMoreThenTwoProj()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { "test.nativeproj", "Different.csproj", "Another.proj" };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Test what happens if there are no project or solution files in the directory
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchNoProjectOrSolution()
        {
            Should.Throw<InitializationException>(() =>
            {
                string[] projects = { };
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(projects);
                MSBuildApp.ProcessProjectSwitch(new string[] { }, extensionsToIgnore, projectHelper.GetFiles);
            }
           );
        }
        /// <summary>
        /// Helper class to simulate directory work for ignore project extensions
        /// </summary>
        internal class IgnoreProjectExtensionsHelper
        {
            private readonly List<string> _directoryFileNameList;

            /// <summary>
            /// Takes in a list of file names to simulate as being in a directory
            /// </summary>
            /// <param name="filesInDirectory"></param>
            internal IgnoreProjectExtensionsHelper(string[] filesInDirectory)
            {
                _directoryFileNameList = new List<string>();
                foreach (string file in filesInDirectory)
                {
                    _directoryFileNameList.Add(file);
                }
            }

            /// <summary>
            /// Mocks System.IO.GetFiles. Takes in known search patterns and returns files which
            /// are provided through the constructor
            /// </summary>
            /// <param name="path">not used</param>
            /// <param name="searchPattern">Pattern of files to return</param>
            /// <returns></returns>
            internal string[] GetFiles(string path, string searchPattern)
            {
                List<string> fileNamesToReturn = new List<string>();
                foreach (string file in _directoryFileNameList)
                {
                    if (string.Equals(searchPattern, "*.sln", StringComparison.OrdinalIgnoreCase))
                    {
                        if (FileUtilities.IsSolutionFilename(file))
                        {
                            fileNamesToReturn.Add(file);
                        }
                    }
                    else if (string.Equals(searchPattern, "*.*proj", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Path.GetExtension(file).Contains("proj"))
                        {
                            fileNamesToReturn.Add(file);
                        }
                    }
                }
                return fileNamesToReturn.ToArray();
            }
        }

        /// <summary>
        /// Verifies that when a directory is specified that a project can be found.
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchDirectory()
        {
            string projectDirectory = Directory.CreateDirectory(Path.Combine(ObjectModelHelpers.TempProjectDir, Guid.NewGuid().ToString("N"))).FullName;

            try
            {
                string expectedProject = "project1.proj";
                string[] extensionsToIgnore = null;
                IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(new[] { expectedProject });
                string actualProject = MSBuildApp.ProcessProjectSwitch(new[] { projectDirectory }, extensionsToIgnore, projectHelper.GetFiles);

                actualProject.ShouldBe(expectedProject);
            }
            finally
            {
                RobustDelete(projectDirectory);
            }
        }

        /// <summary>
        /// Verifies that when a directory is specified and there are multiple projects that the correct error is thrown.
        /// </summary>
        [Fact]
        public void TestProcessProjectSwitchDirectoryMultipleProjects()
        {
            string projectDirectory = Directory.CreateDirectory(Path.Combine(ObjectModelHelpers.TempProjectDir, Guid.NewGuid().ToString("N"))).FullName;

            try
            {
                InitializationException exception = Should.Throw<InitializationException>(() =>
                {
                    string[] extensionsToIgnore = null;
                    IgnoreProjectExtensionsHelper projectHelper = new IgnoreProjectExtensionsHelper(new[] { "project1.proj", "project2.proj" });
                    MSBuildApp.ProcessProjectSwitch(new[] { projectDirectory }, extensionsToIgnore, projectHelper.GetFiles);
                });

                exception.Message.ShouldBe(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("AmbiguousProjectDirectoryError", projectDirectory));
            }
            finally
            {
                RobustDelete(projectDirectory);
            }
        }
#endregion

#region ProcessFileLoggerSwitches
        /// <summary>
        /// Test the case where no file logger switches are given, should be no file loggers attached
        /// </summary>
        [Fact]
        public void TestProcessFileLoggerSwitch1()
        {
            bool distributedFileLogger = false;
            string[] fileLoggerParameters = null;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();

            var loggers = new List<ILogger>();
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
        }

        /// <summary>
        /// Test the case where a central logger and distributed logger are attached
        /// </summary>
        [Fact]
        public void TestProcessFileLoggerSwitch2()
        {
            bool distributedFileLogger = true;
            string[] fileLoggerParameters = null;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();

            var loggers = new List<ILogger>();
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected one distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
        }

        /// <summary>
        /// Test the case where a central file logger is attached but no distributed logger
        /// </summary>
        [Fact]
        public void TestProcessFileLoggerSwitch3()
        {
            bool distributedFileLogger = false;
            string[] fileLoggerParameters = null;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();

            var loggers = new List<ILogger>();
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected a central loggers to be attached"

            // add a set of parameters and make sure the logger has those parameters
            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "Parameter" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"

            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "Parameter1", "Parameter" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
        }

        /// <summary>
        /// Test the case where a distributed file logger is attached but no central logger
        /// </summary>
        [Fact]
        public void TestProcessFileLoggerSwitch4()
        {
            bool distributedFileLogger = true;
            string[] fileLoggerParameters = null;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();

            var loggers = new List<ILogger>();
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($"logFile={Path.Combine(Directory.GetCurrentDirectory(), "MSBuild.log")}", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            // Not add a set of parameters and make sure the logger has those parameters
            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "verbosity=Normal;" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($"{fileLoggerParameters[0]};logFile={Path.Combine(Directory.GetCurrentDirectory(), "MSBuild.log")}", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            // Not add a set of parameters and make sure the logger has those parameters
            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "verbosity=Normal", "" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($"{fileLoggerParameters[0]};logFile={Path.Combine(Directory.GetCurrentDirectory(), "MSBuild.log")}", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            // Not add a set of parameters and make sure the logger has those parameters
            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "", "Parameter1" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($";Parameter1;logFile={Path.Combine(Directory.GetCurrentDirectory(), "MSBuild.log")}", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            // Not add a set of parameters and make sure the logger has those parameters
            distributedLoggerRecords = new List<DistributedLoggerRecord>();

            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "Parameter1", "verbosity=Normal;logfile=" + (NativeMethodsShared.IsWindows ? "c:\\temp\\cat.log" : "/tmp/cat.log") };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe(fileLoggerParameters[0] + ";" + fileLoggerParameters[1], StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            distributedLoggerRecords = new List<DistributedLoggerRecord>();
            loggers = new List<ILogger>();
            fileLoggerParameters = new[] { "Parameter1", "verbosity=Normal;logfile=" + Path.Combine("..", "cat.log") + ";Parameter1" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($"Parameter1;verbosity=Normal;logFile={Path.Combine(Directory.GetCurrentDirectory(), "..", "cat.log")};Parameter1", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"

            loggers = new List<ILogger>();
            distributedLoggerRecords = new List<DistributedLoggerRecord>();
            fileLoggerParameters = new[] { "Parameter1", ";Parameter;", "", ";", ";Parameter", "Parameter;" };
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           2
                       );
            distributedLoggerRecords[0].ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe($"Parameter1;Parameter;;;Parameter;Parameter;logFile={Path.Combine(Directory.GetCurrentDirectory(), "msbuild.log")}", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"
        }

        /// <summary>
        /// Verify when in single proc mode the file logger enables mp logging and does not show eventId
        /// </summary>
        [Fact]
        public void TestProcessFileLoggerSwitch5()
        {
            bool distributedFileLogger = false;
            string[] fileLoggerParameters = null;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();

            var loggers = new List<ILogger>();
            MSBuildApp.ProcessDistributedFileLogger
                       (
                           distributedFileLogger,
                           fileLoggerParameters,
                           distributedLoggerRecords,
                           loggers,
                           1
                       );
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
        }
#endregion

#region ProcessConsoleLoggerSwitches
        [Fact]
        public void ProcessConsoleLoggerSwitches()
        {
            var loggers = new List<ILogger>();
            LoggerVerbosity verbosity = LoggerVerbosity.Normal;
            List<DistributedLoggerRecord> distributedLoggerRecords = new List<DistributedLoggerRecord>();
            string[] consoleLoggerParameters = { "Parameter1", ";Parameter;", "", ";", ";Parameter", "Parameter;" };

            MSBuildApp.ProcessConsoleLoggerSwitch
                       (
                           true,
                           consoleLoggerParameters,
                           distributedLoggerRecords,
                           verbosity,
                           1,
                           loggers
                       );
            loggers.Count.ShouldBe(0); // "Expected no central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(0); // "Expected no distributed loggers to be attached"

            MSBuildApp.ProcessConsoleLoggerSwitch
                       (
                           false,
                           consoleLoggerParameters,
                           distributedLoggerRecords,
                           verbosity,
                           1,
                           loggers
                       );
            loggers.Count.ShouldBe(1); // "Expected a central loggers to be attached"
            loggers[0].Parameters.ShouldBe("EnableMPLogging;SHOWPROJECTFILE=TRUE;Parameter1;Parameter;;;parameter;Parameter", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameters passed in"

            MSBuildApp.ProcessConsoleLoggerSwitch
                       (
                          false,
                          consoleLoggerParameters,
                          distributedLoggerRecords,
                          verbosity,
                          2,
                          loggers
                      );
            loggers.Count.ShouldBe(1); // "Expected a central loggers to be attached"
            distributedLoggerRecords.Count.ShouldBe(1); // "Expected a distributed logger to be attached"
            DistributedLoggerRecord distributedLogger = distributedLoggerRecords[0];
            distributedLogger.CentralLogger.Parameters.ShouldBe("SHOWPROJECTFILE=TRUE;Parameter1;Parameter;;;parameter;Parameter", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameters passed in"
            distributedLogger.ForwardingLoggerDescription.LoggerSwitchParameters.ShouldBe("SHOWPROJECTFILE=TRUE;Parameter1;Parameter;;;Parameter;Parameter", StringCompareShould.IgnoreCase); // "Expected parameter in logger to match parameter passed in"
        }
#endregion

        [Fact]
        public void RestoreFirstReevaluatesImportGraph()
        {
            string guid = Guid.NewGuid().ToString("N");

            string projectContents = ObjectModelHelpers.CleanupFileContents($@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

  <PropertyGroup>
    <RestoreFirstProps>{Guid.NewGuid():N}.props</RestoreFirstProps>
  </PropertyGroup>

  <Import Project=""$(RestoreFirstProps)"" Condition=""Exists($(RestoreFirstProps))""/>

  <Target Name=""Build"">
    <Error Text=""PropertyA does not have a value defined"" Condition="" '$(PropertyA)' == '' "" />
    <Message Text=""PropertyA's value is &quot;$(PropertyA)&quot;"" />
  </Target>

  <Target Name=""Restore"">
    <ItemGroup>
      <Lines Include=""&lt;Project ToolsVersion=&quot;Current&quot; xmlns=&quot;http://schemas.microsoft.com/developer/msbuild/2003&quot;&gt;&lt;PropertyGroup&gt;&lt;PropertyA&gt;{guid}&lt;/PropertyA&gt;&lt;/PropertyGroup&gt;&lt;/Project&gt;"" />
    </ItemGroup>

    <WriteLinesToFile File=""$(RestoreFirstProps)"" Lines=""@(Lines)"" Overwrite=""true"" />
  </Target>

</Project>");

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, arguments: "/restore");

            logContents.ShouldContain(guid);
        }

        [Fact]
        public void RestoreFirstClearsProjectRootElementCache()
        {
            string guid1 = Guid.NewGuid().ToString("N");
            string guid2 = Guid.NewGuid().ToString("N");
            string restoreFirstProps = $"{Guid.NewGuid():N}.props";

            string projectContents = ObjectModelHelpers.CleanupFileContents($@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

  <PropertyGroup>
    <RestoreFirstProps>{restoreFirstProps}</RestoreFirstProps>
  </PropertyGroup>

  <Import Project=""$(RestoreFirstProps)"" Condition=""Exists($(RestoreFirstProps))""/>

  <Target Name=""Build"">
    <Error Text=""PropertyA does not have a value defined"" Condition="" '$(PropertyA)' == '' "" />
    <Message Text=""PropertyA's value is &quot;$(PropertyA)&quot;"" />
  </Target>

  <Target Name=""Restore"">
    <Message Text=""PropertyA's value is &quot;$(PropertyA)&quot;"" />
    <ItemGroup>
      <Lines Include=""&lt;Project ToolsVersion=&quot;Current&quot; xmlns=&quot;http://schemas.microsoft.com/developer/msbuild/2003&quot;&gt;&lt;PropertyGroup&gt;&lt;PropertyA&gt;{guid2}&lt;/PropertyA&gt;&lt;/PropertyGroup&gt;&lt;/Project&gt;"" />
    </ItemGroup>

    <WriteLinesToFile File=""$(RestoreFirstProps)"" Lines=""@(Lines)"" Overwrite=""true"" />
  </Target>

</Project>");

            IDictionary<string, string> preExistingProps = new Dictionary<string, string>
            {
                { restoreFirstProps, $@"<Project ToolsVersion=""Current"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <PropertyA>{guid1}</PropertyA>
  </PropertyGroup>
</Project>"
                }
            };

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, filesToCreate: preExistingProps, arguments: "/restore");

            logContents.ShouldContain(guid1);
            logContents.ShouldContain(guid2);
        }

        [Fact]
        public void RestoreIgnoresMissingImports()
        {
            string guid1 = Guid.NewGuid().ToString("N");
            string guid2 = Guid.NewGuid().ToString("N");
            string restoreFirstProps = $"{Guid.NewGuid():N}.props";

            string projectContents = ObjectModelHelpers.CleanupFileContents($@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

  <PropertyGroup>
    <RestoreFirstProps>{restoreFirstProps}</RestoreFirstProps>
  </PropertyGroup>

  <Import Project=""$(RestoreFirstProps)"" />

  <Target Name=""Build"">
    <Error Text=""PropertyA does not have a value defined"" Condition="" '$(PropertyA)' == '' "" />
    <Message Text=""PropertyA's value is &quot;$(PropertyA)&quot;"" />
  </Target>

  <Target Name=""Restore"">
    <Message Text=""PropertyA's value is &quot;$(PropertyA)&quot;"" />
    <ItemGroup>
      <Lines Include=""&lt;Project ToolsVersion=&quot;Current&quot; xmlns=&quot;http://schemas.microsoft.com/developer/msbuild/2003&quot;&gt;&lt;PropertyGroup&gt;&lt;PropertyA&gt;{guid2}&lt;/PropertyA&gt;&lt;/PropertyGroup&gt;&lt;/Project&gt;"" />
    </ItemGroup>

    <WriteLinesToFile File=""$(RestoreFirstProps)"" Lines=""@(Lines)"" Overwrite=""true"" />
  </Target>

</Project>");

            IDictionary<string, string> preExistingProps = new Dictionary<string, string>
            {
                { restoreFirstProps, $@"<Project ToolsVersion=""Current"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <PropertyA>{guid1}</PropertyA>
  </PropertyGroup>
</Project>"
                }
            };

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, filesToCreate: preExistingProps, arguments: "/restore");

            logContents.ShouldContain(guid1);
            logContents.ShouldContain(guid2);
        }

        /// <summary>
        /// When specifying /t:restore, fail when an SDK can't be resolved.  Previous behavior was to try and continue anyway but then "restore" would succeed and build workflows continue on.
        /// </summary>
        [Fact]
        public void RestoreFailsOnUnresolvedSdk()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(
$@"<Project>
  <Sdk Name=""UnresolvedSdk"" />
  <Target Name=""Restore"">
    <Message Text=""Restore target ran"" />
  </Target>
</Project>");

            string logContents = ExecuteMSBuildExeExpectFailure(projectContents, arguments: "/t:restore");

            logContents.ShouldContain("error MSB4236: The SDK 'UnresolvedSdk' specified could not be found.");
        }

        /// <summary>
        /// Verifies restore will run InitialTargets.
        /// </summary>
        [Fact]
        public void RestoreRunsInitialTargets()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(
                @"<Project DefaultTargets=""Build"" InitialTargets=""InitialTarget"">
  <Target Name=""InitialTarget"">
    <Message Text=""InitialTarget target ran&quot;"" />
  </Target>
  <Target Name=""Restore"">
    <Message Text=""Restore target ran&quot;"" />
  </Target>
  <Target Name=""Build"">
    <Message Text=""Build target ran&quot;"" />
  </Target>
</Project>");

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, arguments: "/t:restore");

            logContents.ShouldContain("InitialTarget target ran");
            logContents.ShouldContain("Restore target ran");
        }

        /// <summary>
        /// We check if there is only one target name specified and this logic caused a regression: https://github.com/Microsoft/msbuild/issues/3317
        /// </summary>
        [Fact]
        public void MultipleTargetsDoesNotCrash()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents($@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Target Name=""Target1"">
    <Message Text=""7514CB1641A948D0A3930C5EC2DC1940"" />
  </Target>
  <Target Name=""Target2"">
    <Message Text=""E2C73B5843F94B63B067D9BEB2C4EC52"" />
  </Target>
</Project>");

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, arguments: "/t:Target1 /t:Target2");

            logContents.ShouldContain("7514CB1641A948D0A3930C5EC2DC1940", () => logContents);
            logContents.ShouldContain("E2C73B5843F94B63B067D9BEB2C4EC52", () => logContents);
        }

        [Theory]
        [InlineData("-logger:,\"nonExistentlogger.dll\",IsOptional;Foo")]
        [InlineData("-logger:ClassA,\"nonExistentlogger.dll\",IsOptional;Foo")]
        [InlineData("-logger:,\"nonExistentlogger.dll\",IsOptional,OptionB,OptionC")]
        [InlineData("-distributedlogger:,\"nonExistentlogger.dll\",IsOptional;Foo")]
        [InlineData("-distributedlogger:ClassA,\"nonExistentlogger.dll\",IsOptional;Foo")]
        [InlineData("-distributedlogger:,\"nonExistentlogger.dll\",IsOptional,OptionB,OptionC")]
        public void MissingOptionalLoggersAreIgnored(string logger)
        {
            string projectString =
                "<Project>" +
                "<Target Name=\"t\"><Message Text=\"Hello\"/></Target>" +
                "</Project>";
            var tempDir = _env.CreateFolder();
            var projectFile = tempDir.CreateFile("missingloggertest.proj", projectString);

            var parametersLoggerOptional = $"{logger} -verbosity:diagnostic \"{projectFile.Path}\"";

            var output = RunnerUtilities.ExecMSBuild(parametersLoggerOptional, out bool successfulExit, _output);
            successfulExit.ShouldBe(true);
            output.ShouldContain("Hello", output);
            output.ShouldContain("The specified logger could not be created and will not be used.", output);
        }

        [Theory]
        [InlineData("/interactive")]
        [InlineData("/p:NuGetInteractive=true")]
        [InlineData("/interactive /p:NuGetInteractive=true")]
        public void InteractiveSetsBuiltInProperty(string arguments)
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

  <Target Name=""Build"">
    <Message Text=""MSBuildInteractive = [$(MSBuildInteractive)]"" />
  </Target>

</Project>");

            string logContents = ExecuteMSBuildExeExpectSuccess(projectContents, arguments: arguments);

            logContents.ShouldContain("MSBuildInteractive = [true]");
        }

        /// <summary>
        /// Regression test for https://github.com/microsoft/msbuild/issues/4631
        /// </summary>
        [Fact]
        public void BinaryLogContainsImportedFiles()
        {
            var testProject = _env.CreateFile("Importer.proj", ObjectModelHelpers.CleanupFileContents(@"
            <Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
                <Import Project=""TestProject.proj"" />

                <Target Name=""Build"">
                </Target>

            </Project>"));

            _env.CreateFile("TestProject.proj", @"
            <Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
              <Target Name=""Build"">
                <Message Text=""Hello from TestProject!"" />
              </Target>
            </Project>
            ");

            string binLogLocation = _env.DefaultTestDirectory.Path;

            string output = RunnerUtilities.ExecMSBuild($"\"{testProject.Path}\" \"/bl:{binLogLocation}/output.binlog\"", out var success, _output);

            success.ShouldBeTrue(output);

            RunnerUtilities.ExecMSBuild($"\"{binLogLocation}/output.binlog\" \"/bl:{binLogLocation}/replay.binlog;ProjectImports=ZipFile\"", out success, _output);

            using ZipArchive archive = ZipFile.OpenRead($"{binLogLocation}/replay.ProjectImports.zip");
            archive.Entries.ShouldContain(e => e.FullName.EndsWith(".proj", StringComparison.OrdinalIgnoreCase), 2);
        }

        [Fact]
        public void EndToEndWarnAsErrors()
        {
            string projectContents = ObjectModelHelpers.CleanupFileContents(@"<Project>

  <Target Name=""IssueWarning"">
    <Warning Text=""Warning!"" />
  </Target>

</Project>");

            TransientTestProjectWithFiles testProject = _env.CreateTestProjectWithFiles(projectContents);

            RunnerUtilities.ExecMSBuild($"\"{testProject.ProjectFile}\" -warnaserror", out bool success, _output);

            success.ShouldBeFalse();
        }

        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        [Fact]
        public void BuildSlnOutOfProc()
        {
            string solutionFileContents =
@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 16
Project('{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}') = 'TestProject', 'TestProject.proj', '{6185CC21-BE89-448A-B3C0-D1C27112E595}'
EndProject
Global
GlobalSection(SolutionConfigurationPlatforms) = preSolution
    Debug|Mixed Platforms = Debug|Mixed Platforms
    Release|Any CPU = Release|Any CPU
EndGlobalSection
GlobalSection(ProjectConfigurationPlatforms) = postSolution
    {6185CC21-BE89-448A-B3C0-D1C27112E595}.Debug|Mixed Platforms.ActiveCfg = CSConfig1|Any CPU
    {6185CC21-BE89-448A-B3C0-D1C27112E595}.Debug|Mixed Platforms.Build.0 = CSConfig1|Any CPU
EndGlobalSection
EndGlobal
                ".Replace("'", "\"");

            var testSolution = _env.CreateFile("TestSolution.sln", ObjectModelHelpers.CleanupFileContents(solutionFileContents));

            string testMessage = "Hello from TestProject!";
            _env.CreateFile("TestProject.proj", @$"
            <Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
              <Target Name=""Build"">
                <Message Text=""{testMessage}"" />
              </Target>
            </Project>
            ");

            _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");

            string output = RunnerUtilities.ExecMSBuild($"\"{testSolution.Path}\" /p:Configuration=Debug", out var success, _output);

            success.ShouldBeTrue(output);
            output.ShouldContain(testMessage);
        }

        /// <summary>
        /// Helper task used by <see cref="EndToEndMinimumMessageImportance"/> to verify <see cref="TaskLoggingHelper.LogsMessagesOfImportance"/>.
        /// </summary>
        public class MessageImportanceCheckingTask : Task
        {
            public int ExpectedMinimumMessageImportance { get; set; }

            public override bool Execute()
            {
                bool shouldLogHigh = Log.LogsMessagesOfImportance(MessageImportance.High);
                bool shouldLogNormal = Log.LogsMessagesOfImportance(MessageImportance.Normal);
                bool shouldLogLow = Log.LogsMessagesOfImportance(MessageImportance.Low);
                return (MessageImportance)ExpectedMinimumMessageImportance switch
                {
                    MessageImportance.High - 1 => !shouldLogHigh && !shouldLogNormal && !shouldLogLow,
                    MessageImportance.High => shouldLogHigh && !shouldLogNormal && !shouldLogLow,
                    MessageImportance.Normal => shouldLogHigh && shouldLogNormal && !shouldLogLow,
                    MessageImportance.Low => shouldLogHigh && shouldLogNormal && shouldLogLow,
                    _ => false
                };
            }
        }

        [Theory]
        [InlineData("/v:diagnostic", MessageImportance.Low)]
        [InlineData("/v:detailed", MessageImportance.Low)]
        [InlineData("/v:normal", MessageImportance.Normal)]
        [InlineData("/v:minimal", MessageImportance.High)]
        [InlineData("/v:quiet", MessageImportance.High - 1)]
        [InlineData("/v:diagnostic /bl", MessageImportance.Low)]
        [InlineData("/v:detailed /bl", MessageImportance.Low)]
        [InlineData("/v:normal /bl", MessageImportance.Low)] // v:normal but with binary logger so everything must be logged
        [InlineData("/v:minimal /bl", MessageImportance.Low)] // v:minimal but with binary logger so everything must be logged
        [InlineData("/v:quiet /bl", MessageImportance.Low)] // v:quiet but with binary logger so everything must be logged
        public void EndToEndMinimumMessageImportance(string arguments, MessageImportance expectedMinimumMessageImportance)
        {
            using TestEnvironment testEnvironment = UnitTests.TestEnvironment.Create();

            string projectContents = ObjectModelHelpers.CleanupFileContents(@"<Project>

  <UsingTask TaskName=""" + typeof(MessageImportanceCheckingTask).FullName + @""" AssemblyFile=""" + Assembly.GetExecutingAssembly().Location + @"""/>

  <Target Name=""CheckMessageImportance"">
    <MessageImportanceCheckingTask ExpectedMinimumMessageImportance=""" + (int)expectedMinimumMessageImportance + @""" />
  </Target>

</Project>");

            TransientTestProjectWithFiles testProject = testEnvironment.CreateTestProjectWithFiles(projectContents);

            // Build in-proc.
            RunnerUtilities.ExecMSBuild($"{arguments} \"{testProject.ProjectFile}\"", out bool success, _output);
            success.ShouldBeTrue();

            // Build out-of-proc to exercise both logging code paths.
            testEnvironment.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");
            testEnvironment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
            RunnerUtilities.ExecMSBuild($"{arguments} \"{testProject.ProjectFile}\"", out success, _output);
            success.ShouldBeTrue();
        }

#if FEATURE_ASSEMBLYLOADCONTEXT
        /// <summary>
        /// Ensure that tasks get loaded into their own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
        /// </summary>
        /// <remarks>
        /// When loading a task from a test assembly in a test within that assembly, the assembly is already loaded
        /// into the default context. So put the test here and isolate the task into an MSBuild that runs in its
        /// own process, causing it to newly load the task (test) assembly in a new ALC.
        /// </remarks>
        [Fact]
        public void TasksGetAssemblyLoadContexts()
        {
            string customTaskPath = Assembly.GetExecutingAssembly().Location;

            string projectContents = $@"<Project ToolsVersion=`msbuilddefaulttoolsversion` xmlns=`msbuildnamespace`>
  <UsingTask TaskName=`ValidateAssemblyLoadContext` AssemblyFile=`{customTaskPath}` />

  <Target Name=`Build`>
    <ValidateAssemblyLoadContext />
  </Target>
</Project>";

            ExecuteMSBuildExeExpectSuccess(projectContents);
        }

#endif

        private string CopyMSBuild()
        {
            string dest = null;
            try
            {
                string source = Path.GetDirectoryName(RunnerUtilities.PathToCurrentlyRunningMsBuildExe);
                dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(dest);

                // Copy MSBuild.exe & dependent files (they will not be in the GAC so they must exist next to msbuild.exe)
                var filesToCopy = Directory
                    .EnumerateFiles(source);

                var directoriesToCopy = Directory
                    .EnumerateDirectories(source)
                    .Where(d =>
                    {
                        if (Path.GetFileName(d).Equals("TestTemp", StringComparison.InvariantCultureIgnoreCase))
                        {
                            return false;
                        }
                        return true;
                    });

                foreach (var file in filesToCopy)
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
                }

                foreach (var directory in directoriesToCopy)
                {
                    string dirName = Path.GetFileName(directory);
                    string destSubDir = Path.Combine(dest, dirName);
                    FileUtilities.CopyDirectory(directory, destSubDir);
                }

                return dest;
            }
            catch (Exception)
            {
                RobustDelete(dest);
                throw;
            }
        }

        private string ExecuteMSBuildExeExpectSuccess(string projectContents, IDictionary<string, string> filesToCreate = null,  IDictionary<string, string> envsToCreate = null, params string[] arguments)
        {
            (bool result, string output) = ExecuteMSBuildExe(projectContents, filesToCreate, envsToCreate, arguments);

            result.ShouldBeTrue(() => output);

            return output;
        }

        private string ExecuteMSBuildExeExpectFailure(string projectContents, IDictionary<string, string> filesToCreate = null, IDictionary<string, string> envsToCreate = null, params string[] arguments)
        {
            (bool result, string output) = ExecuteMSBuildExe(projectContents, filesToCreate, envsToCreate, arguments);

            result.ShouldBeFalse(() => output);

            return output;
        }

        private (bool result, string output) ExecuteMSBuildExe(string projectContents, IDictionary<string, string> filesToCreate = null, IDictionary<string, string> envsToCreate = null, params string[] arguments)
        {
            TransientTestProjectWithFiles testProject = _env.CreateTestProjectWithFiles(projectContents, new string[0]);

            if (filesToCreate != null)
            {
                foreach (var item in filesToCreate)
                {
                    File.WriteAllText(Path.Combine(testProject.TestRoot, item.Key), item.Value);
                }
            }

            if (envsToCreate != null)
            {
                foreach (var env in envsToCreate)
                {
                    _env.SetEnvironmentVariable(env.Key, env.Value);
                }
            }

            string output = RunnerUtilities.ExecMSBuild($"\"{testProject.ProjectFile}\" {string.Join(" ", arguments)}", out var success, _output);

            return (success, output);
        }

        public void Dispose()
        {
            _env.Dispose();
        }
    }
}
