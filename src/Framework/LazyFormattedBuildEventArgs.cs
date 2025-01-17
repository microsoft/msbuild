// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// Stores strings for parts of a message delaying the formatting until it needs to be shown
    /// </summary>
    [Serializable]
    public class LazyFormattedBuildEventArgs : BuildEventArgs
    {
        /// <summary>
        /// Stores the message arguments.
        /// </summary>
        private volatile object? argumentsOrFormattedMessage;

        /// <summary>
        /// Exposes the underlying arguments field to serializers.
        /// </summary>
        internal object[]? RawArguments
        {
            get => (argumentsOrFormattedMessage is object[] arguments) ? arguments : null;
        }

        /// <summary>
        /// Exposes the formatted message string to serializers.
        /// </summary>
        private protected override string? FormattedMessage
        {
            get => (argumentsOrFormattedMessage is string formattedMessage) ? formattedMessage : base.FormattedMessage;
        }

        /// <summary>
        /// This constructor allows all event data to be initialized.
        /// </summary>
        /// <param name="message">text message.</param>
        /// <param name="helpKeyword">help keyword.</param>
        /// <param name="senderName">name of event sender.</param>
        public LazyFormattedBuildEventArgs(
            [StringSyntax(StringSyntaxAttribute.CompositeFormat)] string? message,
            string? helpKeyword,
            string? senderName)
            : this(message, helpKeyword, senderName, DateTime.Now, null)
        {
        }

        /// <summary>
        /// This constructor that allows message arguments that are lazily formatted.
        /// </summary>
        /// <param name="message">text message.</param>
        /// <param name="helpKeyword">help keyword.</param>
        /// <param name="senderName">name of event sender.</param>
        /// <param name="eventTimestamp">Timestamp when event was created.</param>
        /// <param name="messageArgs">Message arguments.</param>
        public LazyFormattedBuildEventArgs(
            [StringSyntax(StringSyntaxAttribute.CompositeFormat)] string? message,
            string? helpKeyword,
            string? senderName,
            DateTime eventTimestamp,
            params object[]? messageArgs)
            : base(message, helpKeyword, senderName, eventTimestamp)
        {
            argumentsOrFormattedMessage = messageArgs;
        }

        /// <summary>
        /// Default constructor.
        /// </summary>
        protected LazyFormattedBuildEventArgs()
            : base()
        {
        }

        /// <summary>
        /// Gets the formatted message.
        /// </summary>
        public override string? Message
        {
            get
            {
                object? argsOrMessage = argumentsOrFormattedMessage;
                if (argsOrMessage is string formattedMessage)
                {
                    return formattedMessage;
                }

                if (argsOrMessage is object[] arguments && arguments.Length > 0 && base.Message is not null)
                {
                    formattedMessage = FormatString(base.Message, arguments);
                    argumentsOrFormattedMessage = formattedMessage;
                    return formattedMessage;
                }

                return base.Message;
            }
        }

        /// <summary>
        /// Serializes to a stream through a binary writer.
        /// </summary>
        /// <param name="writer">Binary writer which is attached to the stream the event will be serialized into.</param>
        internal override void WriteToStream(BinaryWriter writer)
        {
            object? argsOrMessage = argumentsOrFormattedMessage;
            if (argsOrMessage is object[] arguments && arguments.Length > 0)
            {
                base.WriteToStreamWithExplicitMessage(writer, base.Message);
                writer.Write(arguments.Length);

                foreach (object argument in arguments)
                {
                    // Arguments may be ints, etc, so explicitly convert
                    // Convert.ToString returns String.Empty when it cannot convert, rather than throwing
                    // It returns null if the input is null.
                    string argValue;
                    try
                    {
                        argValue = Convert.ToString(argument, CultureInfo.CurrentCulture) ?? "";
                    }
                    // Let's grace handle case where custom ToString implementation (that Convert.ToString fallbacks to) throws.
                    catch (Exception e)
                    {
                        argValue = $"Argument conversion to string failed{Environment.NewLine}{e}";
                    }
                    writer.Write(argValue);
                }
            }
            else
            {
                base.WriteToStreamWithExplicitMessage(writer, (argsOrMessage is string formattedMessage) ? formattedMessage : base.Message);
                writer.Write(-1);
            }
        }

        /// <summary>
        /// Deserializes from a stream through a binary reader.
        /// </summary>
        /// <param name="reader">Binary reader which is attached to the stream the event will be deserialized from.</param>
        /// <param name="version">The version of the runtime the message packet was created from</param>
        internal override void CreateFromStream(BinaryReader reader, Int32 version)
        {
            base.CreateFromStream(reader, version);

            if (version > 20)
            {
                string[]? messageArgs = null;
                int numArguments = reader.ReadInt32();

                if (numArguments >= 0)
                {
                    messageArgs = new string[numArguments];

                    for (int numRead = 0; numRead < numArguments; numRead++)
                    {
                        messageArgs[numRead] = reader.ReadString();
                    }
                }

                argumentsOrFormattedMessage = messageArgs;
            }
        }

        /// <summary>
        /// Formats the given string using the variable arguments passed in.
        ///
        /// PERF WARNING: calling a method that takes a variable number of arguments is expensive, because memory is allocated for
        /// the array of arguments -- do not call this method repeatedly in performance-critical scenarios
        /// </summary>
        /// <remarks>This method is thread-safe.</remarks>
        /// <param name="unformatted">The string to format.</param>
        /// <param name="args">Optional arguments for formatting the given string.</param>
        /// <returns>The formatted string.</returns>
        private static string FormatString([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string unformatted, params object[] args)
        {
            // Based on the one in Shared/ResourceUtilities.
            string formatted = unformatted;

            // NOTE: String.Format() does not allow a null arguments array
            if ((args?.Length > 0))
            {
#if DEBUG
                // If you accidentally pass some random type in that can't be converted to a string,
                // FormatResourceString calls ToString() which returns the full name of the type!
                foreach (object param in args)
                {
                    // Check against a list of types that we know have
                    // overridden ToString() usefully. If you want to pass
                    // another one, add it here.
                    if (param != null && param.ToString() == param.GetType().FullName)
                    {
                        throw new InvalidOperationException(string.Format("Invalid type for message formatting argument, was {0}", param.GetType().FullName));
                    }
                }
#endif
                // Format the string, using the variable arguments passed in.
                // NOTE: all String methods are thread-safe
                try
                {
                    formatted = string.Format(unformatted, args);
                }
                catch (FormatException ex)
                {
                    // User task may have logged something with too many format parameters
                    // We don't have resources in this assembly, and we generally log stack for task failures so they can be fixed by the owner
                    // However, we don't want to crash the logger and stop the build.
                    // Error will look like this (it's OK to not localize subcategory). It's not too bad, although there's no file.
                    //
                    //       Task "Crash"
                    //          (16,14):  error : "This message logged from a task {1} has too few formatting parameters."
                    //             at System.Text.StringBuilder.AppendFormat(IFormatProvider provider, String format, Object[] args)
                    //             at System.String.Format(String format, Object[] args)
                    //             at Microsoft.Build.Framework.LazyFormattedBuildEventArgs.FormatString(String unformatted, Object[] args) in d:\W8T_Refactor\src\vsproject\xmake\Framework\LazyFormattedBuildEventArgs.cs:line 263
                    //          Done executing task "Crash".
                    //
                    // T
                    formatted = string.Format("\"{0}\"\n{1}", unformatted, ex.ToString());
                }
            }

            return formatted;
        }
    }
}
