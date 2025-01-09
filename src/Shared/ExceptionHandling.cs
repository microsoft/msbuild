// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

#if BUILDINGAPPXTASKS
namespace Microsoft.Build.AppxPackage.Shared
#else
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Build.Shared.FileSystem;
using System.Xml.Schema;
using System.Runtime.Serialization;
#if !CLR2COMPATIBILITY && !MICROSOFT_BUILD_ENGINE_OM_UNITTESTS
using Microsoft.Build.Shared.Debugging;
#endif
using Microsoft.Build.Framework;

namespace Microsoft.Build.Shared
#endif
{
    /// <summary>
    /// Utility methods for classifying and handling exceptions.
    /// </summary>
    internal static class ExceptionHandling
    {
        private static readonly string s_debugDumpPath = GetDebugDumpPath();

        /// <summary>
        /// Gets the location of the directory used for diagnostic log files.
        /// </summary>
        /// <returns></returns>
        private static string GetDebugDumpPath()
        {
            string debugPath =

                /* Unmerged change from project 'Microsoft.Build.Engine.OM.UnitTests (net7.0)'
                Before:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                After:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                */
                /* Unmerged change from project 'Microsoft.Build.Engine.OM.UnitTests (net472)'
                Before:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                After:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                */
                /* Unmerged change from project 'MSBuildTaskHost'
                Before:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                After:
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
                */
                // Cannot access change wave logic from these assemblies (https://github.com/dotnet/msbuild/issues/6707)
#if CLR2COMPATIBILITY || MICROSOFT_BUILD_ENGINE_OM_UNITTESTS
                Environment.GetEnvironmentVariable("MSBUILDDEBUGPATH");
#else
                DebugUtils.DebugPath;
#endif

            return !string.IsNullOrEmpty(debugPath)
                    ? debugPath
                    : FileUtilities.TempFileDirectory;
        }

        /// <summary>
        /// The directory used for diagnostic log files.
        /// </summary>
        internal static string DebugDumpPath => s_debugDumpPath;

        /// <summary>
        /// The file used for diagnostic log files.
        /// </summary>
        internal static string DumpFilePath => s_dumpFileName;

#if !BUILDINGAPPXTASKS
        /// <summary>
        /// The filename that exceptions will be dumped to
        /// </summary>
        private static string s_dumpFileName;
#endif
        /// <summary>
        /// If the given exception is "ignorable under some circumstances" return false.
        /// Otherwise it's "really bad", and return true.
        /// This makes it possible to catch(Exception ex) without catching disasters.
        /// </summary>
        /// <param name="e"> The exception to check. </param>
        /// <returns> True if exception is critical. </returns>
        internal static bool IsCriticalException(Exception e)
        {
            if (e is OutOfMemoryException
             || e is StackOverflowException
             || e is ThreadAbortException
             || e is ThreadInterruptedException
             || e is AccessViolationException
#if !TASKHOST
             || e is CriticalTaskException
#endif
#if !BUILDINGAPPXTASKS
             || e is InternalErrorException
#endif
             )
            {
                // Ideally we would include NullReferenceException, because it should only ever be thrown by CLR (use ArgumentNullException for arguments)
                // but we should handle it if tasks and loggers throw it.

                // ExecutionEngineException has been deprecated by the CLR
                return true;
            }

#if !CLR2COMPATIBILITY
            // Check if any critical exceptions
            var aggregateException = e as AggregateException;

            if (aggregateException != null)
            {
                // If the aggregate exception contains a critical exception it is considered a critical exception
                if (aggregateException.InnerExceptions.Any(innerException => IsCriticalException(innerException)))
                {
                    return true;
                }
            }
#endif

            return false;
        }

        /// <summary>
        /// If the given exception is file IO related or expected return false.
        /// Otherwise, return true.
        /// </summary>
        /// <param name="e">The exception to check.</param>
        /// <returns>True if exception is not IO related or expected otherwise false.</returns>
        internal static bool NotExpectedException(Exception e)
        {
            return !IsIoRelatedException(e);
        }

        /// <summary>
        /// Determine whether the exception is file-IO related.
        /// </summary>
        /// <param name="e">The exception to check.</param>
        /// <returns>True if exception is IO related.</returns>
        internal static bool IsIoRelatedException(Exception e)
        {
            // These all derive from IOException
            //     DirectoryNotFoundException
            //     DriveNotFoundException
            //     EndOfStreamException
            //     FileLoadException
            //     FileNotFoundException
            //     PathTooLongException
            //     PipeException
            return e is UnauthorizedAccessException
                   || e is NotSupportedException
                   || (e is ArgumentException && !(e is ArgumentNullException))
                   || e is SecurityException
                   || e is IOException;
        }

        /// <summary> Checks if the exception is an XML one. </summary>
        /// <param name="e"> Exception to check. </param>
        /// <returns> True if exception is related to XML parsing. </returns>
        internal static bool IsXmlException(Exception e)
        {
            return e is XmlException
#if FEATURE_SECURITY_PERMISSIONS
                || e is System.Security.XmlSyntaxException
#endif
                || e is XmlSchemaException
                || e is UriFormatException; // XmlTextReader for example uses this under the covers
        }

        /// <summary> Extracts line and column numbers from the exception if it is XML-related one. </summary>
        /// <param name="e"> XML-related exception. </param>
        /// <returns> Line and column numbers if available, (0,0) if not. </returns>
        /// <remarks> This function works around the fact that XmlException and XmlSchemaException are not directly related. </remarks>
        internal static LineAndColumn GetXmlLineAndColumn(Exception e)
        {
            var line = 0;
            var column = 0;

            var xmlException = e as XmlException;
            if (xmlException != null)
            {
                line = xmlException.LineNumber;
                column = xmlException.LinePosition;
            }
            else
            {
                var schemaException = e as XmlSchemaException;
                if (schemaException != null)
                {
                    line = schemaException.LineNumber;
                    column = schemaException.LinePosition;
                }
            }

            return new LineAndColumn
            {
                Line = line,
                Column = column
            };
        }

#if !BUILDINGAPPXTASKS

        /// <summary>
        /// If the given exception is file IO related or Xml related return false.
        /// Otherwise, return true.
        /// </summary>
        /// <param name="e">The exception to check.</param>
        internal static bool NotExpectedIoOrXmlException(Exception e)
        {
            if
            (
                IsXmlException(e)
                || !NotExpectedException(e))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// If the given exception is reflection-related return false.
        /// Otherwise, return true.
        /// </summary>
        /// <param name="e">The exception to check.</param>
        internal static bool NotExpectedReflectionException(Exception e)
        {
            // We are explicitly not handling TargetInvocationException. Those are just wrappers around
            // exceptions thrown by the called code (such as a task or logger) which callers will typically
            // want to treat differently.
            if
            (
                e is TypeLoadException                  // thrown when the common language runtime cannot find the assembly, the type within the assembly, or cannot load the type
                || e is MethodAccessException           // thrown when a class member is not found or access to the member is not permitted
                || e is MissingMethodException          // thrown when code in a dependent assembly attempts to access a missing method in an assembly that was modified
                || e is MemberAccessException           // thrown when a class member is not found or access to the member is not permitted
                || e is BadImageFormatException         // thrown when the file image of a DLL or an executable program is invalid
                || e is ReflectionTypeLoadException     // thrown by the Module.GetTypes method if any of the classes in a module cannot be loaded
                || e is TargetParameterCountException   // thrown when the number of parameters for an invocation does not match the number expected
                || e is InvalidCastException
                || e is AmbiguousMatchException         // thrown when binding to a member results in more than one member matching the binding criteria
                || e is CustomAttributeFormatException  // thrown if a custom attribute on a data type is formatted incorrectly
                || e is InvalidFilterCriteriaException  // thrown in FindMembers when the filter criteria is not valid for the type of filter you are using
                || e is TargetException                 // thrown when an attempt is made to invoke a non-static method on a null object.  This may occur because the caller does not
                                                        //     have access to the member, or because the target does not define the member, and so on.
                || e is MissingFieldException           // thrown when code in a dependent assembly attempts to access a missing field in an assembly that was modified.
                || !NotExpectedException(e))             // Reflection can throw IO exceptions if the assembly cannot be opened
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Serialization has been observed to throw TypeLoadException as
        /// well as SerializationException and IO exceptions. (Obviously
        /// it has to do reflection but it ought to be wrapping the exceptions.)
        /// </summary>
        internal static bool NotExpectedSerializationException(Exception e)
        {
            if
            (
                e is SerializationException ||
                !NotExpectedReflectionException(e))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns false if this is a known exception thrown by the registry API.
        /// </summary>
        internal static bool NotExpectedRegistryException(Exception e)
        {
            if (e is SecurityException
             || e is UnauthorizedAccessException
             || e is IOException
             || e is ObjectDisposedException
             || e is ArgumentException)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns false if this is a known exception thrown by function evaluation
        /// </summary>
        internal static bool NotExpectedFunctionException(Exception e)
        {
            if (e is InvalidCastException
             || e is ArgumentNullException
             || e is FormatException
             || e is InvalidOperationException
             || !NotExpectedReflectionException(e))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Dump any unhandled exceptions to a file so they can be diagnosed
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "It is called by the CLR")]
        internal static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            DumpExceptionToFile(ex);
        }

        /// <summary>
        /// Dump the exception information to a file
        /// </summary>
        internal static void DumpExceptionToFile(Exception ex)
        {
            try
            {
                // Locking on a type is not recommended.  However, we are doing it here to be extra cautious about compatibility because
                //  this method previously had a [MethodImpl(MethodImplOptions.Synchronized)] attribute, which does lock on the type when
                //  applied to a static method.
                lock (typeof(ExceptionHandling))
                {
                    if (s_dumpFileName == null)
                    {
                        Guid guid = Guid.NewGuid();

                        // For some reason we get Watson buckets because GetTempPath gives us a folder here that doesn't exist.
                        // Either because %TMP% is misdefined, or because they deleted the temp folder during the build.
                        // If this throws, no sense catching it, we can't log it now, and we're here
                        // because we're a child node with no console to log to, so die
                        Directory.CreateDirectory(DebugDumpPath);

                        var pid = EnvironmentUtilities.CurrentProcessId;
                        // This naming pattern is assumed in ReadAnyExceptionFromFile
                        s_dumpFileName = Path.Combine(DebugDumpPath, $"MSBuild_pid-{pid}_{guid:n}.failure.txt");

                        using (StreamWriter writer = FileUtilities.OpenWrite(s_dumpFileName, append: true))
                        {
                            writer.WriteLine("UNHANDLED EXCEPTIONS FROM PROCESS {0}:", pid);
                            writer.WriteLine("=====================");
                        }
                    }

                    using (StreamWriter writer = FileUtilities.OpenWrite(s_dumpFileName, append: true))
                    {
                        // "G" format is, e.g., 6/15/2008 9:15:07 PM
                        writer.WriteLine(DateTime.Now.ToString("G", CultureInfo.CurrentCulture));
                        writer.WriteLine(ex.ToString());
                        writer.WriteLine("===================");
                    }
                }
            }
            
            // Some customers experience exceptions such as 'OutOfMemory' errors when msbuild attempts to log errors to a local file.
            // This catch helps to prevent the application from crashing in this best-effort dump-diagnostics path,
            // but doesn't prevent the overall crash from going to Watson.
            catch
            {
            }
        }

        /// <summary>
        /// Returns the content of any exception dump files modified
        /// since the provided time, otherwise returns an empty string.
        /// </summary>
        internal static string ReadAnyExceptionFromFile(DateTime fromTimeUtc)
        {
            var builder = new StringBuilder();
            IEnumerable<string> files = FileSystems.Default.EnumerateFiles(DebugDumpPath, "MSBuild*failure.txt");

            foreach (string file in files)
            {
                if (File.GetLastWriteTimeUtc(file) >= fromTimeUtc)
                {
                    builder.Append(Environment.NewLine);
                    builder.Append(file);
                    builder.Append(':');
                    builder.Append(Environment.NewLine);
                    builder.Append(File.ReadAllText(file));
                    builder.Append(Environment.NewLine);
                }
            }

            return builder.ToString();
        }
#endif

        /// <summary> Line and column pair. </summary>
        internal struct LineAndColumn
        {
            /// <summary> Gets or sets line number. </summary>
            internal int Line { get; set; }

            /// <summary> Gets or sets column position. </summary>
            internal int Column { get; set; }
        }
    }
}
