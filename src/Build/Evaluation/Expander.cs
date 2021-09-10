// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;
using Microsoft.Win32;
using AvailableStaticMethods = Microsoft.Build.Internal.AvailableStaticMethods;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;
using TaskItemFactory = Microsoft.Build.Execution.ProjectItemInstance.TaskItem.TaskItemFactory;

using Microsoft.NET.StringTools;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Indicates to the expander what exactly it should expand.
    /// </summary>
    [Flags]
    internal enum ExpanderOptions
    {
        /// <summary>
        /// Invalid
        /// </summary>
        Invalid = 0x0,

        /// <summary>
        /// Expand bare custom metadata, like %(foo), but not built-in
        /// metadata, such as %(filename) or %(identity)
        /// </summary>
        ExpandCustomMetadata = 0x1,

        /// <summary>
        /// Expand bare built-in metadata, such as %(filename) or %(identity)
        /// </summary>
        ExpandBuiltInMetadata = 0x2,

        /// <summary>
        /// Expand all bare metadata
        /// </summary>
        ExpandMetadata = ExpandCustomMetadata | ExpandBuiltInMetadata,

        /// <summary>
        /// Expand only properties
        /// </summary>
        ExpandProperties = 0x4,

        /// <summary>
        /// Expand only item list expressions
        /// </summary>
        ExpandItems = 0x8,

        /// <summary>
        /// If the expression is going to not be an empty string, break
        /// out early
        /// </summary>
        BreakOnNotEmpty = 0x10,

        /// <summary>
        /// When an error occurs expanding a property, just leave it unexpanded.
        /// </summary>
        /// <remarks>
        /// This should only be used in cases where property evaluation isn't critcal, such as when attempting to log a
        /// message with a best effort expansion of a string, or when discovering partial information during lazy evaluation.
        /// </remarks>
        LeavePropertiesUnexpandedOnError = 0x20,

        /// <summary>
        /// When an expansion occurs, truncate it to Expander.DefaultTruncationCharacterLimit or Expander.DefaultTruncationItemLimit.
        /// </summary>
        Truncate = 0x40,

        /// <summary>
        /// Expand only properties and then item lists
        /// </summary>
        ExpandPropertiesAndItems = ExpandProperties | ExpandItems,

        /// <summary>
        /// Expand only bare metadata and then properties
        /// </summary>
        ExpandPropertiesAndMetadata = ExpandMetadata | ExpandProperties,

        /// <summary>
        /// Expand only bare custom metadata and then properties
        /// </summary>
        ExpandPropertiesAndCustomMetadata = ExpandCustomMetadata | ExpandProperties,

        /// <summary>
        /// Expand bare metadata, then properties, then item expressions
        /// </summary>
        ExpandAll = ExpandMetadata | ExpandProperties | ExpandItems
    }

    /// <summary>
    /// Expands item/property/metadata in expressions.
    /// Encapsulates the data necessary for expansion.
    /// </summary>
    /// <remarks>
    /// Requires the caller to explicitly state what they wish to expand at the point of expansion (explicitly does not have a field for ExpanderOptions). 
    /// Callers typically use a single expander in many locations, and this forces the caller to make explicit what they wish to expand at the point of expansion.
    /// 
    /// Requires the caller to have previously provided the necessary material for the expansion requested.
    /// For example, if the caller requests ExpanderOptions.ExpandItems, the Expander will throw if it was not given items.
    /// </remarks>
    /// <typeparam name="P">Type of the properties used.</typeparam>
    /// <typeparam name="I">Type of the items used.</typeparam>
    internal class Expander<P, I>
        where P : class, IProperty
        where I : class, IItem
    {
        /// <summary>
        /// A helper struct wrapping a <see cref="SpanBasedStringBuilder"/> and providing file path conversion
        /// as used in e.g. property expansion.
        /// </summary>
        /// <remarks>
        /// If exactly one value is added and no concatenation takes places, this value is returned without
        /// conversion. In other cases values are stringified and attempted to be interpreted as file paths
        /// before concatenation.
        /// </remarks>
        private struct SpanBasedConcatenator : IDisposable
        {
            /// <summary>
            /// The backing <see cref="SpanBasedStringBuilder"/>, null until the second value is added.
            /// </summary>
            private SpanBasedStringBuilder _builder;

            /// <summary>
            /// The first value added to the concatenator. Tracked in its own field so it can be returned
            /// without conversion if no concatenation takes place.
            /// </summary>
            private object _firstObject;

            /// <summary>
            /// The first value added to the concatenator if it is a span. Tracked in its own field so the
            /// <see cref="SpanBasedStringBuilder"/> functionality doesn't have to be invoked if no concatenation
            /// takes place.
            /// </summary>
            private ReadOnlyMemory<char> _firstSpan;

            /// <summary>
            /// True if this instance is already disposed.
            /// </summary>
            private bool _disposed;

            /// <summary>
            /// Adds an object to be concatenated.
            /// </summary>
            public void Add(object obj)
            {
                CheckDisposed();
                FlushFirstValueIfNeeded();
                if (_builder != null)
                {
                    _builder.Append(FileUtilities.MaybeAdjustFilePath(obj.ToString()));
                }
                else
                {
                    _firstObject = obj;
                }
            }

            /// <summary>
            /// Adds a span to be concatenated.
            /// </summary>
            public void Add(ReadOnlyMemory<char> span)
            {
                CheckDisposed();
                FlushFirstValueIfNeeded();
                if (_builder != null)
                {
                    _builder.Append(FileUtilities.MaybeAdjustFilePath(span));
                }
                else
                {
                    _firstSpan = span;
                }
            }

            /// <summary>
            /// Returns the result of the concatenation.
            /// </summary>
            /// <returns>
            /// If only one value has been added and it is not a string, it is returned unchanged.
            /// In all other cases (no value, one string value, multiple values) the result is a
            /// concatenation of the string representation of the values, each additionally subjected
            /// to file path adjustment.
            /// </returns>
            public object GetResult()
            {
                CheckDisposed();
                if (_firstObject != null)
                {
                    return (_firstObject is string stringValue) ? FileUtilities.MaybeAdjustFilePath(stringValue) : _firstObject;
                }
                return _firstSpan.IsEmpty
                    ? _builder?.ToString() ?? string.Empty
                    : FileUtilities.MaybeAdjustFilePath(_firstSpan).ToString();
            }

            /// <summary>
            /// Disposes of the struct by delegating the call to the underlying <see cref="SpanBasedStringBuilder"/>.
            /// </summary>
            public void Dispose()
            {
                CheckDisposed();
                _builder?.Dispose();
                _disposed = true;
            }

            /// <summary>
            /// Throws <see cref="ObjectDisposedException"/> if this concatenator is already disposed.
            /// </summary>
            private void CheckDisposed() =>
                ErrorUtilities.VerifyThrowObjectDisposed(!_disposed, nameof(SpanBasedConcatenator));

            /// <summary>
            /// Lazily initializes <see cref="_builder"/> and populates it with the first value
            /// when the second value is being added.
            /// </summary>
            private void FlushFirstValueIfNeeded()
            {
                if (_firstObject != null)
                {
                    _builder = Strings.GetSpanBasedStringBuilder();
                    _builder.Append(FileUtilities.MaybeAdjustFilePath(_firstObject.ToString()));
                    _firstObject = null;
                }
                else if (!_firstSpan.IsEmpty)
                {
                    _builder = Strings.GetSpanBasedStringBuilder();
#if FEATURE_SPAN
                    _builder.Append(FileUtilities.MaybeAdjustFilePath(_firstSpan));
#else
                    _builder.Append(FileUtilities.MaybeAdjustFilePath(_firstSpan.ToString()));
#endif
                    _firstSpan = new ReadOnlyMemory<char>();
                }
            }
        }

        /// <summary>
        /// A limit for truncating string expansions within an evaluated Condition. Properties, item metadata, or item groups will be truncated to N characters such as 'N...'.
        /// Enabled by ExpanderOptions.Truncate.
        /// </summary>
        private const int CharacterLimitPerExpansion = 1024;
        /// <summary>
        /// A limit for truncating string expansions for item groups within an evaluated Condition. N items will be evaluated such as 'A;B;C;...'.
        /// Enabled by ExpanderOptions.Truncate.
        /// </summary>
        private const int ItemLimitPerExpansion = 3;
        private static readonly char[] s_singleQuoteChar = { '\'' };
        private static readonly char[] s_backtickChar = { '`' };
        private static readonly char[] s_doubleQuoteChar = { '"' };

        /// <summary>
        /// Those characters which indicate that an expression may contain expandable
        /// expressions.
        /// </summary>
        private static char[] s_expandableChars = { '$', '%', '@' };

        /// <summary>
        /// The CultureInfo from the invariant culture. Used to avoid allocations for
        /// perfoming IndexOf etc.
        /// </summary>
        private static CompareInfo s_invariantCompareInfo = CultureInfo.InvariantCulture.CompareInfo;

        /// <summary>
        /// Properties to draw on for expansion.
        /// </summary>
        private IPropertyProvider<P> _properties;

        /// <summary>
        /// Items to draw on for expansion.
        /// </summary>
        private IItemProvider<I> _items;

        /// <summary>
        /// Metadata to draw on for expansion.
        /// </summary>
        private IMetadataTable _metadata;

        /// <summary>
        /// Set of properties which are null during expansion.
        /// </summary>
        private UsedUninitializedProperties _usedUninitializedProperties;

        private readonly IFileSystem _fileSystem;

        /// <summary>
        /// Non-null if the expander was constructed for evaluation.
        /// </summary>
        internal EvaluationContext EvaluationContext { get; }

        /// <summary>
        /// Creates an expander passing it some properties to use.
        /// Properties may be null.
        /// </summary>
        internal Expander(IPropertyProvider<P> properties)
        {
            _properties = properties;
            _usedUninitializedProperties = new UsedUninitializedProperties();
            _fileSystem = FileSystems.Default;
        }

        /// <summary>
        /// Creates an expander passing it some properties to use and the evaluation context.
        /// Properties may be null.
        /// </summary>
        internal Expander(IPropertyProvider<P> properties, EvaluationContext evaluationContext)
        {
            _properties = properties;
            _usedUninitializedProperties = new UsedUninitializedProperties();
            _fileSystem = evaluationContext.FileSystem;
            EvaluationContext = evaluationContext;
        }

        /// <summary>
        /// Creates an expander passing it some properties and items to use.
        /// Either or both may be null.
        /// </summary>
        internal Expander(IPropertyProvider<P> properties, IItemProvider<I> items)
            : this(properties)
        {
            _items = items;
        }

        /// <summary>
        /// Creates an expander passing it some properties and items to use, and the evaluation context.
        /// Either or both may be null.
        /// </summary>
        internal Expander(IPropertyProvider<P> properties, IItemProvider<I> items, EvaluationContext evaluationContext)
            : this(properties, evaluationContext)
        {
            _items = items;
        }

        /// <summary>
        /// Creates an expander passing it some properties, items, and/or metadata to use.
        /// Any or all may be null.
        /// </summary>
        internal Expander(IPropertyProvider<P> properties, IItemProvider<I> items, IMetadataTable metadata)
            : this(properties, items)
        {
            _metadata = metadata;
        }

        /// <summary>
        /// Whether to warn when we set a property for the first time, after it was previously used.
        /// Default is false, unless MSBUILDWARNONUNINITIALIZEDPROPERTY is set.
        /// </summary>
        internal bool WarnForUninitializedProperties
        {
            get { return _usedUninitializedProperties.Warn; }
            set { _usedUninitializedProperties.Warn = value; }
        }

        /// <summary>
        /// Accessor for the metadata.
        /// Set temporarily during item metadata evaluation.
        /// </summary>
        internal IMetadataTable Metadata
        {
            get { return _metadata; }
            set { _metadata = value; }
        }

        /// <summary>
        /// If a property is expanded but evaluates to null then it is considered to be un-initialized.
        /// We want to keep track of these properties so that we can warn if the property gets set later on.
        /// </summary>
        internal UsedUninitializedProperties UsedUninitializedProperties
        {
            get { return _usedUninitializedProperties; }
            set { _usedUninitializedProperties = value; }
        }

        /// <summary>
        /// Tests to see if the expression may contain expandable expressions, i.e.
        /// contains $, % or @.
        /// </summary>
        internal static bool ExpressionMayContainExpandableExpressions(string expression)
        {
            return expression.IndexOfAny(s_expandableChars) > -1;
        }

        /// <summary>
        /// Returns true if the expression contains an item vector pattern, else returns false.
        /// Used to flag use of item expressions where they are illegal.
        /// </summary>
        internal static bool ExpressionContainsItemVector(string expression)
        {
            List<ExpressionShredder.ItemExpressionCapture> transforms = ExpressionShredder.GetReferencedItemExpressions(expression);

            return transforms != null;
        }

        /// <summary>
        /// Expands embedded item metadata, properties, and embedded item lists (in that order) as specified in the provided options.
        /// This is the standard form. Before using the expanded value, it must be unescaped, and this does that for you.
        /// 
        /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.        
        /// </summary>
        internal string ExpandIntoStringAndUnescape(string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            string result = ExpandIntoStringLeaveEscaped(expression, options, elementLocation);

            return (result == null) ? null : EscapingUtilities.UnescapeAll(result);
        }

        /// <summary>
        /// Expands embedded item metadata, properties, and embedded item lists (in that order) as specified in the provided options.
        /// Use this form when the result is going to be processed further, for example by matching against the file system,
        /// so literals must be distinguished, and you promise to unescape after that.
        /// 
        /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
        /// </summary>
        internal string ExpandIntoStringLeaveEscaped(string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            if (expression.Length == 0)
            {
                return String.Empty;
            }

            ErrorUtilities.VerifyThrowInternalNull(elementLocation, nameof(elementLocation));

            string result = MetadataExpander.ExpandMetadataLeaveEscaped(expression, _metadata, options, elementLocation);
            result = PropertyExpander<P>.ExpandPropertiesLeaveEscaped(result, _properties, options, elementLocation, _usedUninitializedProperties, _fileSystem);
            result = ItemExpander.ExpandItemVectorsIntoString<I>(this, result, _items, options, elementLocation);
            result = FileUtilities.MaybeAdjustFilePath(result);

            return result;
        }

        /// <summary>
        /// Used only for unit tests. Expands the property expression (including any metadata expressions) and returns
        /// the result typed (i.e. not converted into a string if the result is a function return).
        /// </summary>
        internal object ExpandPropertiesLeaveTypedAndEscaped(string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            if (expression.Length == 0)
            {
                return String.Empty;
            }

            ErrorUtilities.VerifyThrowInternalNull(elementLocation, nameof(elementLocation));

            string metaExpanded = MetadataExpander.ExpandMetadataLeaveEscaped(expression, _metadata, options, elementLocation);
            return PropertyExpander<P>.ExpandPropertiesLeaveTypedAndEscaped(metaExpanded, _properties, options, elementLocation, _usedUninitializedProperties, _fileSystem);
        }

        /// <summary>
        /// Expands embedded item metadata, properties, and embedded item lists (in that order) as specified in the provided options,
        /// then splits on semi-colons into a list of strings.
        /// Use this form when the result is going to be processed further, for example by matching against the file system,
        /// so literals must be distinguished, and you promise to unescape after that.
        /// </summary>
        internal SemiColonTokenizer ExpandIntoStringListLeaveEscaped(string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            ErrorUtilities.VerifyThrow((options & ExpanderOptions.BreakOnNotEmpty) == 0, "not supported");

            return ExpressionShredder.SplitSemiColonSeparatedList(ExpandIntoStringLeaveEscaped(expression, options, elementLocation));
        }

        /// <summary>
        /// Expands embedded item metadata, properties, and embedded item lists (in that order) as specified in the provided options
        /// and produces a list of TaskItems.
        /// If the expression is empty, returns an empty list.
        /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
        /// </summary>
        internal IList<TaskItem> ExpandIntoTaskItemsLeaveEscaped(string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            return ExpandIntoItemsLeaveEscaped(expression, (IItemFactory<I, TaskItem>)TaskItemFactory.Instance, options, elementLocation);
        }

        /// <summary>
        /// Expands embedded item metadata, properties, and embedded item lists (in that order) as specified in the provided options
        /// and produces a list of items of the type for which it was specialized.
        /// If the expression is empty, returns an empty list.
        /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
        /// 
        /// Use this form when the result is going to be processed further, for example by matching against the file system,
        /// so literals must be distinguished, and you promise to unescape after that.
        /// </summary>
        /// <typeparam name="T">Type of items to return.</typeparam>
        internal IList<T> ExpandIntoItemsLeaveEscaped<T>(string expression, IItemFactory<I, T> itemFactory, ExpanderOptions options, IElementLocation elementLocation)
            where T : class, IItem
        {
            if (expression.Length == 0)
            {
                return Array.Empty<T>();
            }

            ErrorUtilities.VerifyThrowInternalNull(elementLocation, nameof(elementLocation));

            expression = MetadataExpander.ExpandMetadataLeaveEscaped(expression, _metadata, options, elementLocation);
            expression = PropertyExpander<P>.ExpandPropertiesLeaveEscaped(expression, _properties, options, elementLocation, _usedUninitializedProperties, _fileSystem);
            expression = FileUtilities.MaybeAdjustFilePath(expression);

            List<T> result = new List<T>();

            if (expression.Length == 0)
            {
                return result;
            }

            var splits = ExpressionShredder.SplitSemiColonSeparatedList(expression);
            foreach (string split in splits)
            {
                bool isTransformExpression;
                IList<T> itemsToAdd = ItemExpander.ExpandSingleItemVectorExpressionIntoItems<I, T>(this, split, _items, itemFactory, options, false /* do not include null items */, out isTransformExpression, elementLocation);

                if ((itemsToAdd == null /* broke out early non empty */ || (itemsToAdd.Count > 0)) && (options & ExpanderOptions.BreakOnNotEmpty) != 0)
                {
                    return null;
                }

                if (itemsToAdd != null)
                {
                    result.AddRange(itemsToAdd);
                }
                else
                {
                    // The expression is not of the form @(itemName).  Therefore, just
                    // treat it as a string, and create a new item from that string.
                    T itemToAdd = itemFactory.CreateItem(split, elementLocation.File);

                    result.Add(itemToAdd);
                }
            }

            return result;
        }

        /// <summary>
        /// This is a specialized method for the use of TargetUpToDateChecker and Evaluator.EvaluateItemXml only.
        /// 
        /// Extracts the items in the given SINGLE item vector.
        /// For example, expands @(Compile->'%(foo)') to a set of items derived from the items in the "Compile" list.
        ///
        /// If there is in fact more than one vector in the expression, throws InvalidProjectFileException.
        /// 
        /// If there are no item expressions in the expression (for example a literal "foo.cpp"), returns null.
        /// If expression expands to no items, returns an empty list.
        /// If item expansion is not allowed by the provided options, returns null.
        /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
        /// 
        /// If the expression is a transform, any transformations to an expression that evaluates to nothing (i.e., because
        /// an item has no value for a piece of metadata) are optionally indicated with a null entry in the list. This means 
        /// that the length of the returned list is always the same as the length of the referenced item list in the input string.
        /// That's important for any correlation the caller wants to do.
        /// 
        /// If expression was a transform, 'isTransformExpression' is true, otherwise false.
        ///
        /// Item type of the items returned is determined by the IItemFactory passed in; if the IItemFactory does not 
        /// have an item type set on it, it will be given the item type of the item vector to use.
        /// </summary>
        /// <typeparam name="T">Type of the items that should be returned.</typeparam>
        internal IList<T> ExpandSingleItemVectorExpressionIntoItems<T>(string expression, IItemFactory<I, T> itemFactory, ExpanderOptions options, bool includeNullItems, out bool isTransformExpression, IElementLocation elementLocation)
            where T : class, IItem
        {
            if (expression.Length == 0)
            {
                isTransformExpression = false;
                return Array.Empty<T>();
            }

            ErrorUtilities.VerifyThrowInternalNull(elementLocation, nameof(elementLocation));

            return ItemExpander.ExpandSingleItemVectorExpressionIntoItems(this, expression, _items, itemFactory, options, includeNullItems, out isTransformExpression, elementLocation);
        }

        internal static ExpressionShredder.ItemExpressionCapture ExpandSingleItemVectorExpressionIntoExpressionCapture(
                string expression, ExpanderOptions options, IElementLocation elementLocation)
        {
            return ItemExpander.ExpandSingleItemVectorExpressionIntoExpressionCapture(expression, options, elementLocation);
        }

        internal IList<T> ExpandExpressionCaptureIntoItems<S, T>(
                ExpressionShredder.ItemExpressionCapture expressionCapture, IItemProvider<S> items, IItemFactory<S, T> itemFactory,
                ExpanderOptions options, bool includeNullEntries, out bool isTransformExpression, IElementLocation elementLocation)
            where S : class, IItem
            where T : class, IItem
        {
            return ItemExpander.ExpandExpressionCaptureIntoItems<S, T>(expressionCapture, this, items, itemFactory, options,
                includeNullEntries, out isTransformExpression, elementLocation);
        }

        internal bool ExpandExpressionCapture(
            ExpressionShredder.ItemExpressionCapture expressionCapture,
            IElementLocation elementLocation,
            ExpanderOptions options,
            bool includeNullEntries,
            out bool isTransformExpression,
            out List<Pair<string, I>> itemsFromCapture)
        {
            return ItemExpander.ExpandExpressionCapture(this, expressionCapture, _items, elementLocation, options, includeNullEntries, out isTransformExpression, out itemsFromCapture);
        }

        /// <summary>
        /// Returns true if the supplied string contains a valid property name.
        /// </summary>
        private static bool IsValidPropertyName(string propertyName)
        {
            if (propertyName.Length == 0 || !XmlUtilities.IsValidInitialElementNameCharacter(propertyName[0]))
            {
                return false;
            }

            for (int n = 1; n < propertyName.Length; n++)
            {
                if (!XmlUtilities.IsValidSubsequentElementNameCharacter(propertyName[n]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true if ExpanderOptions.Truncate is set and EscapeHatches.DoNotTruncateConditions is not set.
        /// </summary>
        private static bool IsTruncationEnabled(ExpanderOptions options)
        {
            return (options & ExpanderOptions.Truncate) != 0 && !Traits.Instance.EscapeHatches.DoNotTruncateConditions;
        }

        /// <summary>
        /// Scan for the closing bracket that matches the one we've already skipped;
        /// essentially, pushes and pops on a stack of parentheses to do this.
        /// Takes the expression and the index to start at.
        /// Returns the index of the matching parenthesis, or -1 if it was not found.
        /// Also returns flags to indicate if a propertyfunction or registry property is likely
        /// to be found in the expression.
        /// </summary>
        private static int ScanForClosingParenthesis(string expression, int index, out bool potentialPropertyFunction, out bool potentialRegistryFunction)
        {
            int nestLevel = 1;
            int length = expression.Length;

            potentialPropertyFunction = false;
            potentialRegistryFunction = false;

            // Scan for our closing ')'
            while (index < length && nestLevel > 0)
            {
                char character = expression[index];
                switch (character)
                {
                    case '\'':
                    case '`':
                    case '"':
                        {
                            index++;
                            index = ScanForClosingQuote(character, expression, index);

                            if (index < 0)
                            {
                                return -1;
                            }
                            break;
                        }
                    case '(':
                        {
                            nestLevel++;
                            break;
                        }
                    case ')':
                        {
                            nestLevel--;
                            break;
                        }
                    case '.':
                    case '[':
                    case '$':
                        {
                            potentialPropertyFunction = true;
                            break;
                        }
                    case ':':
                        {
                            potentialRegistryFunction = true;
                            break;
                        }
                }

                index++;
            }

            // We will have parsed past the ')', so step back one character
            index--;

            return (nestLevel == 0) ? index : -1;
        }

        /// <summary>
        /// Skip all characters until we find the matching quote character.
        /// </summary>
        private static int ScanForClosingQuote(char quoteChar, string expression, int index)
        {
            // Scan for our closing quoteChar
            return expression.IndexOf(quoteChar, index);
        }

        /// <summary>
        /// Add the argument in the StringBuilder to the arguments list, handling nulls
        /// appropriately.
        /// </summary>
        private static void AddArgument(List<string> arguments, SpanBasedStringBuilder argumentBuilder)
        {
            // we reached the end of an argument, add the builder's final result
            // to our arguments.
            argumentBuilder.Trim();
            string argValue = argumentBuilder.ToString();

            // We support passing of null through the argument constant value null
            if (String.Equals("null", argValue, StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add(null);
            }
            else
            {
                if (argValue.Length > 0)
                {
                    if (argValue[0] == '\'' && argValue[argValue.Length - 1] == '\'')
                    {
                        arguments.Add(argValue.Trim(s_singleQuoteChar));
                    }
                    else if (argValue[0] == '`' && argValue[argValue.Length - 1] == '`')
                    {
                        arguments.Add(argValue.Trim(s_backtickChar));
                    }
                    else if (argValue[0] == '"' && argValue[argValue.Length - 1] == '"')
                    {
                        arguments.Add(argValue.Trim(s_doubleQuoteChar));
                    }
                    else
                    {
                        arguments.Add(argValue);
                    }
                }
                else
                {
                    arguments.Add(argValue);
                }
            }
        }

        /// <summary>
        /// Extract the first level of arguments from the content.
        /// Splits the content passed in at commas.
        /// Returns an array of unexpanded arguments.
        /// If there are no arguments, returns an empty array.
        /// </summary>
        private static string[] ExtractFunctionArguments(IElementLocation elementLocation, string expressionFunction, string argumentsString)
        {
            int argumentsContentLength = argumentsString.Length;

            List<string> arguments = new List<string>();

            using SpanBasedStringBuilder argumentBuilder = Strings.GetSpanBasedStringBuilder();
            int? argumentStartIndex = null;

            // We iterate over the string in the for loop below. When we find an argument, instead of adding it to the argument
            // builder one-character-at-a-time, we remember the start index and then call this function when we find the end of
            // the argument. This appends the entire {start, end} span to the builder in one call.
            void FlushCurrentArgumentToArgumentBuilder(int argumentEndIndex)
            {
                if (argumentStartIndex.HasValue)
                {
                    argumentBuilder.Append(argumentsString, argumentStartIndex.Value, argumentEndIndex - argumentStartIndex.Value);
                    argumentStartIndex = null;
                }
            }

            // Iterate over the contents of the arguments extracting the
            // the individual arguments as we go
            for (int n = 0; n < argumentsContentLength; n++)
            {
                // We found a property expression.. skip over all of it.
                if ((n < argumentsContentLength - 1) && (argumentsString[n] == '$' && argumentsString[n + 1] == '('))
                {
                    int nestedPropertyStart = n;
                    n += 2; // skip over the opening '$('

                    // Scan for the matching closing bracket, skipping any nested ones
                    n = ScanForClosingParenthesis(argumentsString, n, out _, out _);

                    if (n == -1)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, AssemblyResources.GetString("InvalidFunctionPropertyExpressionDetailMismatchedParenthesis"));
                    }

                    FlushCurrentArgumentToArgumentBuilder(argumentEndIndex: nestedPropertyStart);
                    argumentBuilder.Append(argumentsString, nestedPropertyStart, (n - nestedPropertyStart) + 1);
                }
                else if (argumentsString[n] == '`' || argumentsString[n] == '"' || argumentsString[n] == '\'')
                {
                    int quoteStart = n;
                    n++; // skip over the opening quote

                    n = ScanForClosingQuote(argumentsString[quoteStart], argumentsString, n);

                    if (n == -1)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, AssemblyResources.GetString("InvalidFunctionPropertyExpressionDetailMismatchedQuote"));
                    }

                    FlushCurrentArgumentToArgumentBuilder(argumentEndIndex: quoteStart);
                    argumentBuilder.Append(argumentsString, quoteStart, (n - quoteStart) + 1);
                }
                else if (argumentsString[n] == ',')
                {
                    FlushCurrentArgumentToArgumentBuilder(argumentEndIndex: n);

                    // We have reached the end of the current argument, go ahead and add it
                    // to our list
                    AddArgument(arguments, argumentBuilder);

                    // Clear out the argument builder ready for the next argument
                    argumentBuilder.Clear();
                }
                else
                {
                    argumentStartIndex ??= n;
                }
            }

            // We reached the end of the string but we may have seen the start but not the end of the last (or only) argument so flush it now.
            FlushCurrentArgumentToArgumentBuilder(argumentEndIndex: argumentsContentLength);

            // This will either be the one and only argument, or the last one
            // so add it to our list
            AddArgument(arguments, argumentBuilder);

            return arguments.ToArray();
        }

        /// <summary>
        /// Expands bare metadata expressions, like %(Compile.WarningLevel), or unqualified, like %(Compile).
        /// </summary>
        /// <remarks>
        /// This is a private nested class, exposed only through the Expander class.
        /// That allows it to hide its private methods even from Expander.
        /// </remarks>
        private static class MetadataExpander
        {
            /// <summary>
            /// Expands all embedded item metadata in the given string, using the bucketed items.
            /// Metadata may be qualified, like %(Compile.WarningLevel), or unqualified, like %(Compile).
            /// </summary>
            /// <param name="expression">The expression containing item metadata references.</param>
            /// <param name="metadata">The metadata to be expanded.</param>
            /// <param name="options">Used to specify what to expand.</param>
            /// <param name="elementLocation">The location information for error reporting purposes.</param>
            /// <returns>The string with item metadata expanded in-place, escaped.</returns>
            internal static string ExpandMetadataLeaveEscaped(string expression, IMetadataTable metadata, ExpanderOptions options, IElementLocation elementLocation)
            {
                try
                {
                    if ((options & ExpanderOptions.ExpandMetadata) == 0)
                    {
                        return expression;
                    }

                    ErrorUtilities.VerifyThrow(metadata != null, "Cannot expand metadata without providing metadata");

                    // PERF NOTE: Regex matching is expensive, so if the string doesn't contain any item metadata references, just bail
                    // out -- pre-scanning the string is actually cheaper than running the Regex, even when there are no matches!
                    if (s_invariantCompareInfo.IndexOf(expression, "%(", CompareOptions.Ordinal) == -1)
                    {
                        return expression;
                    }

                    string result = null;

                    if (s_invariantCompareInfo.IndexOf(expression, "@(", CompareOptions.Ordinal) == -1)
                    {
                        // if there are no item vectors in the string
                        // run a simpler Regex to find item metadata references
                        MetadataMatchEvaluator matchEvaluator = new MetadataMatchEvaluator(metadata, options);
                        result = RegularExpressions.ItemMetadataPattern.Value.Replace(expression, new MatchEvaluator(matchEvaluator.ExpandSingleMetadata));
                    }
                    else
                    {
                        List<ExpressionShredder.ItemExpressionCapture> itemVectorExpressions = ExpressionShredder.GetReferencedItemExpressions(expression);

                        // The most common case is where the transform is the whole expression
                        // Also if there were no valid item vector expressions found, then go ahead and do the replacement on
                        // the whole expression (which is what Orcas did).
                        if (itemVectorExpressions?.Count == 1 && itemVectorExpressions[0].Value == expression && itemVectorExpressions[0].Separator == null)
                        {
                            return expression;
                        }

                        // otherwise, run the more complex Regex to find item metadata references not contained in transforms
                        using SpanBasedStringBuilder finalResultBuilder = Strings.GetSpanBasedStringBuilder();

                        int start = 0;
                        MetadataMatchEvaluator matchEvaluator = new MetadataMatchEvaluator(metadata, options);

                        if (itemVectorExpressions != null)
                        {
                            // Move over the expression, skipping those that have been recognized as an item vector expression
                            // Anything other than an item vector expression we want to expand bare metadata in.
                            for (int n = 0; n < itemVectorExpressions.Count; n++)
                            {
                                string vectorExpression = itemVectorExpressions[n].Value;

                                // Extract the part of the expression that appears before the item vector expression
                                // e.g. the ABC in ABC@(foo->'%(FullPath)')
                                string subExpressionToReplaceIn = expression.Substring(start, itemVectorExpressions[n].Index - start);
                                string replacementResult = RegularExpressions.NonTransformItemMetadataPattern.Value.Replace(subExpressionToReplaceIn, new MatchEvaluator(matchEvaluator.ExpandSingleMetadata));

                                // Append the metadata replacement
                                finalResultBuilder.Append(replacementResult);

                                // Expand any metadata that appears in the item vector expression's separator
                                if (itemVectorExpressions[n].Separator != null)
                                {
                                    vectorExpression = RegularExpressions.NonTransformItemMetadataPattern.Value.Replace(itemVectorExpressions[n].Value, new MatchEvaluator(matchEvaluator.ExpandSingleMetadata), -1, itemVectorExpressions[n].SeparatorStart);
                                }

                                // Append the item vector expression as is
                                // e.g. the @(foo->'%(FullPath)') in ABC@(foo->'%(FullPath)')
                                finalResultBuilder.Append(vectorExpression);

                                // Move onto the next part of the expression that isn't an item vector expression
                                start = (itemVectorExpressions[n].Index + itemVectorExpressions[n].Length);
                            }
                        }

                        // If there's anything left after the last item vector expression
                        // then we need to metadata replace and then append that
                        if (start < expression.Length)
                        {
                            string subExpressionToReplaceIn = expression.Substring(start);
                            string replacementResult = RegularExpressions.NonTransformItemMetadataPattern.Value.Replace(subExpressionToReplaceIn, new MatchEvaluator(matchEvaluator.ExpandSingleMetadata));

                            finalResultBuilder.Append(replacementResult);
                        }

                        result = finalResultBuilder.ToString();
                    }

                    // Don't create more strings
                    if (String.Equals(result, expression, StringComparison.Ordinal))
                    {
                        result = expression;
                    }

                    return result;
                }
                catch (InvalidOperationException ex)
                {
                    ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotExpandItemMetadata", expression, ex.Message);
                }

                return null;
            }

            /// <summary>
            /// A functor that returns the value of the metadata in the match
            /// that is contained in the metadata dictionary it was created with.
            /// </summary>
            private class MetadataMatchEvaluator
            {
                /// <summary>
                /// Source of the metadata.
                /// </summary>
                private IMetadataTable _metadata;

                /// <summary>
                /// Whether to expand built-in metadata, custom metadata, or both kinds.
                /// </summary>
                private ExpanderOptions _options;

                /// <summary>
                /// Constructor taking a source of metadata.
                /// </summary>
                internal MetadataMatchEvaluator(IMetadataTable metadata, ExpanderOptions options)
                {
                    _metadata = metadata;
                    _options = options & (ExpanderOptions.ExpandMetadata | ExpanderOptions.Truncate);

                    ErrorUtilities.VerifyThrow(options != ExpanderOptions.Invalid, "Must be expanding metadata of some kind");
                }

                /// <summary>
                /// Expands a single item metadata, which may be qualified with an item type.
                /// </summary>
                internal string ExpandSingleMetadata(Match itemMetadataMatch)
                {
                    ErrorUtilities.VerifyThrow(itemMetadataMatch.Success, "Need a valid item metadata.");

                    string metadataName = itemMetadataMatch.Groups[RegularExpressions.NameGroup].Value;
                    string itemType = null;

                    // check if the metadata is qualified with the item type
                    if (itemMetadataMatch.Groups[RegularExpressions.ItemSpecificationGroup].Length > 0)
                    {
                        itemType = itemMetadataMatch.Groups[RegularExpressions.ItemTypeGroup].Value;
                    }

                    // look up the metadata - we may not have a value for it
                    string metadataValue = itemMetadataMatch.Value;

                    bool isBuiltInMetadata = FileUtilities.ItemSpecModifiers.IsItemSpecModifier(metadataName);

                    if (
                        (isBuiltInMetadata && ((_options & ExpanderOptions.ExpandBuiltInMetadata) != 0)) ||
                       (!isBuiltInMetadata && ((_options & ExpanderOptions.ExpandCustomMetadata) != 0))
                        )
                    {
                        metadataValue = _metadata.GetEscapedValue(itemType, metadataName);
                        if (IsTruncationEnabled(_options) && metadataValue.Length > CharacterLimitPerExpansion)
                        {
                            metadataValue = metadataValue.Substring(0, CharacterLimitPerExpansion - 3) + "...";
                        }
                    }

                    return metadataValue;
                }
            }
        }

        /// <summary>
        /// Expands property expressions, like $(Configuration) and $(Registry:HKEY_LOCAL_MACHINE\Software\Vendor\Tools@TaskLocation).
        /// </summary>
        /// <remarks>
        /// This is a private nested class, exposed only through the Expander class.
        /// That allows it to hide its private methods even from Expander.
        /// </remarks>
        /// <typeparam name="T">Type of the properties used to expand the expression.</typeparam>
        private static class PropertyExpander<T>
            where T : class, IProperty
        {
            /// <summary>
            /// This method takes a string which may contain any number of
            /// "$(propertyname)" tags in it.  It replaces all those tags with
            /// the actual property values, and returns a new string.  For example,
            ///
            ///     string processedString =
            ///         propertyBag.ExpandProperties("Value of NoLogo is $(NoLogo).");
            ///
            /// This code might produce:
            ///
            ///     processedString = "Value of NoLogo is true."
            ///
            /// If the sourceString contains an embedded property which doesn't
            /// have a value, then we replace that tag with an empty string.
            ///
            /// This method leaves the result escaped.  Callers may need to unescape on their own as appropriate.
            /// </summary>
            internal static string ExpandPropertiesLeaveEscaped(
                string expression,
                IPropertyProvider<T> properties,
                ExpanderOptions options,
                IElementLocation elementLocation,
                UsedUninitializedProperties usedUninitializedProperties,
                IFileSystem fileSystem)
            {
                return
                    ConvertToString(
                        ExpandPropertiesLeaveTypedAndEscaped(
                            expression,
                            properties,
                            options,
                            elementLocation,
                            usedUninitializedProperties,
                            fileSystem));
            }

            /// <summary>
            /// This method takes a string which may contain any number of
            /// "$(propertyname)" tags in it.  It replaces all those tags with
            /// the actual property values, and returns a new string.  For example,
            ///
            ///     string processedString =
            ///         propertyBag.ExpandProperties("Value of NoLogo is $(NoLogo).");
            ///
            /// This code might produce:
            ///
            ///     processedString = "Value of NoLogo is true."
            ///
            /// If the sourceString contains an embedded property which doesn't
            /// have a value, then we replace that tag with an empty string.
            ///
            /// This method leaves the result typed and escaped.  Callers may need to convert to string, and unescape on their own as appropriate.
            /// </summary>
            internal static object ExpandPropertiesLeaveTypedAndEscaped(
                string expression,
                IPropertyProvider<T> properties,
                ExpanderOptions options,
                IElementLocation elementLocation,
                UsedUninitializedProperties usedUninitializedProperties,
                IFileSystem fileSystem)
            {
                if (((options & ExpanderOptions.ExpandProperties) == 0) || String.IsNullOrEmpty(expression))
                {
                    return expression;
                }

                ErrorUtilities.VerifyThrow(properties != null, "Cannot expand properties without providing properties");

                // These are also zero-based indices into the expression, but
                // these tell us where the current property tag begins and ends.
                int propertyStartIndex, propertyEndIndex;

                // If there are no substitutions, then just return the string.
                propertyStartIndex = s_invariantCompareInfo.IndexOf(expression, "$(", CompareOptions.Ordinal);
                if (propertyStartIndex == -1)
                {
                    return expression;
                }

                // We will build our set of results as object components
                // so that we can either maintain the object's type in the event
                // that we have a single component, or convert to a string
                // if concatenation is required.
                using Expander<P, I>.SpanBasedConcatenator results = new Expander<P, I>.SpanBasedConcatenator();

                // The sourceIndex is the zero-based index into the expression,
                // where we've essentially read up to and copied into the target string.
                int sourceIndex = 0;

                // Search for "$(" in the expression.  Loop until we don't find it 
                // any more.
                while (propertyStartIndex != -1)
                {
                    // Append the result with the portion of the expression up to
                    // (but not including) the "$(", and advance the sourceIndex pointer.
                    if (propertyStartIndex - sourceIndex > 0)
                    {
                        results.Add(expression.AsMemory(sourceIndex, propertyStartIndex - sourceIndex));
                    }

                    // Following the "$(" we need to locate the matching ')'
                    // Scan for the matching closing bracket, skipping any nested ones
                    // This is a very complete, fast validation of parenthesis matching including for nested
                    // function calls.
                    propertyEndIndex = ScanForClosingParenthesis(expression, propertyStartIndex + 2, out bool tryExtractPropertyFunction, out bool tryExtractRegistryFunction);

                    if (propertyEndIndex == -1)
                    {
                        // If we didn't find the closing parenthesis, that means this
                        // isn't really a well-formed property tag.  Just literally
                        // copy the remainder of the expression (starting with the "$("
                        // that we found) into the result, and quit.
                        results.Add(expression.AsMemory(propertyStartIndex, expression.Length - propertyStartIndex));
                        sourceIndex = expression.Length;
                    }
                    else
                    {
                        // Aha, we found the closing parenthesis.  All the stuff in
                        // between the "$(" and the ")" constitutes the property body.
                        // Note: Current propertyStartIndex points to the "$", and 
                        // propertyEndIndex points to the ")".  That's why we have to
                        // add 2 for the start of the substring, and subtract 2 for 
                        // the length.
                        string propertyBody;

                        // A property value of null will indicate that we're calling a static function on a type
                        object propertyValue;

                        // Compat: $() should return String.Empty
                        if (propertyStartIndex + 2 == propertyEndIndex)
                        {
                            propertyValue = String.Empty;
                        }
                        else if ((expression.Length - (propertyStartIndex + 2)) > 9 && tryExtractRegistryFunction && s_invariantCompareInfo.IndexOf(expression, "Registry:", propertyStartIndex + 2, 9, CompareOptions.OrdinalIgnoreCase) == propertyStartIndex + 2)
                        {
                            // if FEATURE_WIN32_REGISTRY is off, treat the property value as if there's no Registry value at that location, rather than fail
                            propertyBody = expression.Substring(propertyStartIndex + 2, propertyEndIndex - propertyStartIndex - 2);

                            // If the property body starts with any of our special objects, then deal with them
                            // This is a registry reference, like $(Registry:HKEY_LOCAL_MACHINE\Software\Vendor\Tools@TaskLocation)
                            propertyValue = ExpandRegistryValue(propertyBody, elementLocation); // This func returns an empty string if not FEATURE_WIN32_REGISTRY
                        }

                        // Compat hack: as a special case, $(HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@VSTSDBDirectory) should return String.Empty
                        // In this case, tryExtractRegistryFunction will be false. Note that very few properties are exactly 77 chars, so this check should be fast.
                        else if ((propertyEndIndex - (propertyStartIndex + 2)) == 77 && s_invariantCompareInfo.IndexOf(expression, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\9.0\VSTSDB@VSTSDBDirectory", propertyStartIndex + 2, 77, CompareOptions.OrdinalIgnoreCase) == propertyStartIndex + 2)
                        {
                            propertyValue = String.Empty;
                        }

                        // Compat hack: WebProjects may have an import with a condition like:
                        //       Condition=" '$(Solutions.VSVersion)' == '8.0'" 
                        // These would have been '' in prior versions of msbuild but would be treated as a possible string function in current versions.
                        // Be compatible by returning an empty string here.
                        else if ((propertyEndIndex - (propertyStartIndex + 2)) == 19 && String.Equals(expression, "$(Solutions.VSVersion)", StringComparison.Ordinal))
                        {
                            propertyValue = String.Empty;
                        }
                        else if (tryExtractPropertyFunction)
                        {
                            propertyBody = expression.Substring(propertyStartIndex + 2, propertyEndIndex - propertyStartIndex - 2);

                            // This is likely to be a function expression
                            propertyValue = ExpandPropertyBody(
                                propertyBody,
                                null,
                                properties,
                                options,
                                elementLocation,
                                usedUninitializedProperties,
                                fileSystem);
                        }
                        else // This is a regular property
                        {
                            propertyValue = LookupProperty(properties, expression, propertyStartIndex + 2, propertyEndIndex - 1, elementLocation, usedUninitializedProperties);
                        }

                        if (propertyValue != null)
                        {
                            if (IsTruncationEnabled(options))
                            {
                                var value = propertyValue.ToString();
                                if (value.Length > CharacterLimitPerExpansion)
                                {
                                    propertyValue = value.Substring(0, CharacterLimitPerExpansion - 3) + "...";
                                }
                            }

                            // Record our result, and advance
                            // our sourceIndex pointer to the character just after the closing
                            // parenthesis.
                            results.Add(propertyValue);
                        }
                        sourceIndex = propertyEndIndex + 1;
                    }

                    propertyStartIndex = s_invariantCompareInfo.IndexOf(expression, "$(", sourceIndex, CompareOptions.Ordinal);
                }

                // If we couldn't find any more property tags in the expression just copy the remainder into the result.
                if (expression.Length - sourceIndex > 0)
                {
                    results.Add(expression.AsMemory(sourceIndex, expression.Length - sourceIndex));
                }

                return results.GetResult();
            }

            /// <summary>
            /// Expand the body of the property, including any functions that it may contain.
            /// </summary>
            internal static object ExpandPropertyBody(
                string propertyBody,
                object propertyValue,
                IPropertyProvider<T> properties,
                ExpanderOptions options,
                IElementLocation elementLocation,
                UsedUninitializedProperties usedUninitializedProperties,
                IFileSystem fileSystem)
            {
                Function<T> function = null;
                string propertyName = propertyBody;

                // Trim the body for compatibility reasons:
                // Spaces are not valid property name chars, but $( Foo ) is allowed, and should always expand to BLANK.
                // Do a very fast check for leading and trailing whitespace, and trim them from the property body if we have any.
                // But we will do a property name lookup on the propertyName that we held onto.
                if (Char.IsWhiteSpace(propertyBody[0]) || Char.IsWhiteSpace(propertyBody[propertyBody.Length - 1]))
                {
                    propertyBody = propertyBody.Trim();
                }

                // If we don't have a clean propertybody then we'll do deeper checks to see
                // if what we have is a function
                if (!IsValidPropertyName(propertyBody))
                {
                    if (propertyBody.Contains(".") || propertyBody[0] == '[')
                    {
                        if (BuildParameters.DebugExpansion)
                        {
                            Console.WriteLine("Expanding: {0}", propertyBody);
                        }

                        // This is a function
                        function = Function<T>.ExtractPropertyFunction(
                            propertyBody,
                            elementLocation,
                            propertyValue,
                            usedUninitializedProperties,
                            fileSystem);

                        // We may not have been able to parse out a function
                        if (function != null)
                        {
                            // We will have either extracted the actual property name
                            // or realised that there is none (static function), and have recorded a null
                            propertyName = function.Receiver;
                        }
                        else
                        {
                            // In the event that we have been handed an unrecognized property body, throw
                            // an invalid function property exception.
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", propertyBody, String.Empty);
                            return null;
                        }
                    }
                    else if (propertyValue == null && propertyBody.Contains("[")) // a single property indexer
                    {
                        int indexerStart = propertyBody.IndexOf('[');
                        int indexerEnd = propertyBody.IndexOf(']');

                        if (indexerStart < 0 || indexerEnd < 0)
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", propertyBody, AssemblyResources.GetString("InvalidFunctionPropertyExpressionDetailMismatchedSquareBrackets"));
                        }
                        else
                        {
                            propertyValue = LookupProperty(properties, propertyBody, 0, indexerStart - 1, elementLocation, usedUninitializedProperties);
                            propertyBody = propertyBody.Substring(indexerStart);

                            // recurse so that the function representing the indexer can be executed on the property value
                            return ExpandPropertyBody(
                                propertyBody,
                                propertyValue,
                                properties,
                                options,
                                elementLocation,
                                usedUninitializedProperties,
                                fileSystem);
                        }
                    }
                    else
                    {
                        // In the event that we have been handed an unrecognized property body, throw
                        // an invalid function property exception.
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", propertyBody, String.Empty);
                        return null;
                    }
                }

                // Find the property value in our property collection.  This 
                // will automatically return "" (empty string) if the property
                // doesn't exist in the collection, and we're not executing a static function
                if (!String.IsNullOrEmpty(propertyName))
                {
                    propertyValue = LookupProperty(properties, propertyName, elementLocation, usedUninitializedProperties);
                }

                if (function != null)
                {
                    try
                    {
                        // Because of the rich expansion capabilities of MSBuild, we need to keep things
                        // as strings, since property expansion & string embedding can happen anywhere
                        // propertyValue can be null here, when we're invoking a static function
                        propertyValue = function.Execute(propertyValue, properties, options, elementLocation);
                    }
                    catch (Exception) when (options.HasFlag(ExpanderOptions.LeavePropertiesUnexpandedOnError))
                    {
                        propertyValue = propertyBody;
                    }
                }

                return propertyValue;
            }

            /// <summary>
            /// Convert the object into an MSBuild friendly string
            /// Arrays are supported.
            /// Will not return NULL.
            /// </summary>
            internal static string ConvertToString(object valueToConvert)
            {
                if (valueToConvert == null)
                {
                    return String.Empty;
                }
                // If the value is a string, then there is nothing to do
                if (valueToConvert is string stringValue)
                {
                    return stringValue;
                }

                string convertedString;
                if (valueToConvert is IDictionary dictionary)
                {
                    // If the return type is an IDictionary, then we convert this to
                    // a semi-colon delimited set of A=B pairs.
                    // Key and Value are converted to string and escaped
                    if (dictionary.Count > 0)
                    {
                        using SpanBasedStringBuilder builder = Strings.GetSpanBasedStringBuilder();

                        foreach (DictionaryEntry entry in dictionary)
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(";");
                            }

                            // convert and escape each key and value in the dictionary entry
                            builder.Append(EscapingUtilities.Escape(ConvertToString(entry.Key)));
                            builder.Append("=");
                            builder.Append(EscapingUtilities.Escape(ConvertToString(entry.Value)));
                        }

                        convertedString = builder.ToString();
                    }
                    else
                    {
                        convertedString = string.Empty;
                    }
                }
                else if (valueToConvert is IEnumerable enumerable)
                {
                    // If the return is enumerable, then we'll convert to semi-colon delimited elements
                    // each of which must be converted, so we'll recurse for each element
                    using SpanBasedStringBuilder builder = Strings.GetSpanBasedStringBuilder();

                    foreach (object element in enumerable)
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append(";");
                        }

                        // we need to convert and escape each element of the array
                        builder.Append(EscapingUtilities.Escape(ConvertToString(element)));
                    }

                    convertedString = builder.ToString();
                }
                else
                {
                    // The fall back is always to just convert to a string directly.
                    convertedString = valueToConvert.ToString();
                }

                return convertedString;
            }

            /// <summary>
            /// Look up a simple property reference by the name of the property, e.g. "Foo" when expanding $(Foo).
            /// </summary>
            private static object LookupProperty(IPropertyProvider<T> properties, string propertyName, IElementLocation elementLocation, UsedUninitializedProperties usedUninitializedProperties)
            {
                return LookupProperty(properties, propertyName, 0, propertyName.Length - 1, elementLocation, usedUninitializedProperties);
            }

            /// <summary>
            /// Look up a simple property reference by the name of the property, e.g. "Foo" when expanding $(Foo).
            /// </summary>
            private static object LookupProperty(IPropertyProvider<T> properties, string propertyName, int startIndex, int endIndex, IElementLocation elementLocation, UsedUninitializedProperties usedUninitializedProperties)
            {
                T property = properties.GetProperty(propertyName, startIndex, endIndex);

                object propertyValue;

                if (property == null && ((endIndex - startIndex) >= 7) && MSBuildNameIgnoreCaseComparer.Default.Equals("MSBuild", propertyName, startIndex, 7))
                {
                    // It could be one of the MSBuildThisFileXXXX properties,
                    // whose values vary according to the file they are in.
                    if (startIndex != 0 || endIndex != propertyName.Length)
                    {
                        propertyValue = ExpandMSBuildThisFileProperty(propertyName.Substring(startIndex, endIndex - startIndex + 1), elementLocation);
                    }
                    else
                    {
                        propertyValue = ExpandMSBuildThisFileProperty(propertyName, elementLocation);
                    }
                }
                else if (property == null)
                {
                    // We have evaluated a property to null. We now need to see if we need to add it to the list of properties which are used before they have been initialized
                    // 
                    // We also do not want to add the property to the list if the environment variable is not set, also we do not want to add the property to the list if we are currently 
                    // evaluating a condition because a common pattern for msbuild projects is to see if the property evaluates to empty and then set a value as this would cause a considerable number of false positives.   <A Condition="'$(A)' == ''">default</A>
                    // 
                    // Another pattern used is where a property concatonates with other values,  <a>$(a);something</a> however we do not want to add the a element to the list because again this would make a number of 
                    // false positives. Therefore we check to see what element we are currently evaluating and if it is the same as our property we do not add the property to the list.
                    if (usedUninitializedProperties.Warn && usedUninitializedProperties.CurrentlyEvaluatingPropertyElementName != null)
                    {
                        // Check to see if the property name does not match the property we are currently evaluating, note the property we are currently evaluating in the element name, this means no $( or )
                        if (!MSBuildNameIgnoreCaseComparer.Default.Equals(usedUninitializedProperties.CurrentlyEvaluatingPropertyElementName, propertyName, startIndex, endIndex - startIndex + 1))
                        {
                            string propertyTrimed = propertyName.Substring(startIndex, endIndex - startIndex + 1);
                            if (!usedUninitializedProperties.Properties.ContainsKey(propertyTrimed))
                            {
                                usedUninitializedProperties.Properties.Add(propertyTrimed, elementLocation);
                            }
                        }
                    }

                    propertyValue = String.Empty;
                }
                else
                {
                    propertyValue = property.EvaluatedValueEscaped;
                }

                return propertyValue;
            }

            /// <summary>
            /// If the property name provided is one of the special
            /// per file properties named "MSBuildThisFileXXXX" then returns the value of that property.
            /// If the location provided does not have a path (eg., if it comes from a file that has
            /// never been saved) then returns empty string.
            /// If the property name is not one of those properties, returns empty string.
            /// </summary>
            private static object ExpandMSBuildThisFileProperty(string propertyName, IElementLocation elementLocation)
            {
                if (!ReservedPropertyNames.IsReservedProperty(propertyName))
                {
                    return String.Empty;
                }

                if (elementLocation.File.Length == 0)
                {
                    return String.Empty;
                }

                string value = String.Empty;

                // Because String.Equals checks the length first, and these strings are almost
                // all different lengths, this sequence is efficient.
                if (String.Equals(propertyName, ReservedPropertyNames.thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    value = Path.GetFileName(elementLocation.File);
                }
                else if (String.Equals(propertyName, ReservedPropertyNames.thisFileName, StringComparison.OrdinalIgnoreCase))
                {
                    value = Path.GetFileNameWithoutExtension(elementLocation.File);
                }
                else if (String.Equals(propertyName, ReservedPropertyNames.thisFileFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    value = FileUtilities.NormalizePath(elementLocation.File);
                }
                else if (String.Equals(propertyName, ReservedPropertyNames.thisFileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    value = Path.GetExtension(elementLocation.File);
                }
                else if (String.Equals(propertyName, ReservedPropertyNames.thisFileDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    value = FileUtilities.EnsureTrailingSlash(Path.GetDirectoryName(elementLocation.File));
                }
                else if (String.Equals(propertyName, ReservedPropertyNames.thisFileDirectoryNoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string directory = Path.GetDirectoryName(elementLocation.File);
                    int rootLength = Path.GetPathRoot(directory).Length;
                    value = FileUtilities.EnsureTrailingNoLeadingSlash(directory, rootLength);
                }

                return value;
            }

#if FEATURE_WIN32_REGISTRY
            /// <summary>
            /// Given a string like "Registry:HKEY_LOCAL_MACHINE\Software\Vendor\Tools@TaskLocation", return the value at that location
            /// in the registry. If the value isn't found, returns String.Empty.
            /// Properties may refer to a registry location by using the syntax for example
            /// "$(Registry:HKEY_LOCAL_MACHINE\Software\Vendor\Tools@TaskLocation)", where "HKEY_LOCAL_MACHINE\Software\Vendor\Tools" is the key and
            /// "TaskLocation" is the name of the value.  The name of the value and the preceding "@" may be omitted if
            /// the default value is desired.
            /// </summary>
            private static string ExpandRegistryValue(string registryExpression, IElementLocation elementLocation)
            {
                // Remove "Registry:" prefix
                string registryLocation = registryExpression.Substring(9);

                // Split off the value name -- the part after the "@" sign. If there's no "@" sign, then it's the default value name
                // we want.
                int firstAtSignOffset = registryLocation.IndexOf('@');
                int lastAtSignOffset = registryLocation.LastIndexOf('@');

                ProjectErrorUtilities.VerifyThrowInvalidProject(firstAtSignOffset == lastAtSignOffset, elementLocation, "InvalidRegistryPropertyExpression", "$(" + registryExpression + ")", String.Empty);

                string valueName = lastAtSignOffset == -1 || lastAtSignOffset == registryLocation.Length - 1
                    ? null : registryLocation.Substring(lastAtSignOffset + 1);

                // If there's no '@', or '@' is first, then we'll use null or String.Empty for the location; otherwise
                // the location is the part before the '@'
                string registryKeyName = lastAtSignOffset != -1 ? registryLocation.Substring(0, lastAtSignOffset) : registryLocation;

                string result = String.Empty;
                if (registryKeyName != null)
                {
                    // We rely on the '@' character to delimit the key and its value, but the registry
                    // allows this character to be used in the names of keys and the names of values.
                    // Hence we use our standard escaping mechanism to allow users to access such keys
                    // and values.
                    registryKeyName = EscapingUtilities.UnescapeAll(registryKeyName);

                    if (valueName != null)
                    {
                        valueName = EscapingUtilities.UnescapeAll(valueName);
                    }

                    try
                    {
                        // Unless we are running under Windows, don't bother with anything but the user keys
                        if (!NativeMethodsShared.IsWindows && !registryKeyName.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                        {
                            // Fake common requests to HKLM that we can resolve

                            // This is the base path of the framework
                            if (registryKeyName.StartsWith(
                                @"HKEY_LOCAL_MACHINE\Software\Microsoft\.NETFramework",
                                StringComparison.OrdinalIgnoreCase) &&
                                valueName.Equals("InstallRoot", StringComparison.OrdinalIgnoreCase))
                            {
                                return NativeMethodsShared.FrameworkBasePath + Path.DirectorySeparatorChar;
                            }

                            return string.Empty;
                        }

                        object valueFromRegistry = Registry.GetValue(registryKeyName, valueName, null /* default if key or value name is not found */);

                        if (valueFromRegistry != null)
                        {
                            // Convert the result to a string that is reasonable for MSBuild
                            result = ConvertToString(valueFromRegistry);
                        }
                        else
                        {
                            // This means either the key or value was not found in the registry.  In this case,
                            // we simply expand the property value to String.Empty to imitate the behavior of
                            // normal properties.
                            result = String.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ExceptionHandling.NotExpectedRegistryException(ex))
                        {
                            throw;
                        }

                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidRegistryPropertyExpression", "$(" + registryExpression + ")", ex.Message);
                    }
                }

                return result;
            }
#else
            /// <summary>
            /// Given a string like "Registry:HKEY_LOCAL_MACHINE\Software\Vendor\Tools@TaskLocation", returns String.Empty, as FEATURE_WIN32_REGISTRY is off.
            /// </summary>
            private static string ExpandRegistryValue(string registryExpression, IElementLocation elementLocation)
            {
                return String.Empty;
            }
#endif
        }

        /// <summary>
        /// Expands item expressions, like @(Compile), possibly with transforms and/or separators.
        ///
        /// Item vectors are composed of a name, an optional transform, and an optional separator i.e.
        /// 
        ///     @(&lt;name&gt;->'&lt;transform&gt;','&lt;separator&gt;')
        ///     
        /// If a separator is not specified it defaults to a semi-colon. The transform expression is also optional, but if
        /// specified, it allows each item in the vector to have its item-spec converted to a different form. The transform
        /// expression can reference any custom metadata defined on the item, as well as the pre-defined item-spec modifiers.
        /// 
        /// NOTE:
        /// 1) white space between &lt;name&gt;, &lt;transform&gt; and &lt;separator&gt; is ignored
        ///    i.e. @(&lt;name&gt;, '&lt;separator&gt;') is valid
        /// 2) the separator is not restricted to be a single character, it can be a string
        /// 3) the separator can be an empty string i.e. @(&lt;name&gt;,'')
        /// 4) specifying an empty transform is NOT the same as specifying no transform -- the former will reduce all item-specs
        ///    to empty strings
        ///
        /// if @(files) is a vector for the files a.txt and b.txt, then:
        /// 
        ///     "my list: @(files)"                                 expands to string     "my list: a.txt;b.txt"
        /// 
        ///     "my list: @(files,' ')"                             expands to string      "my list: a.txt b.txt"
        /// 
        ///     "my list: @(files, '')"                             expands to string      "my list: a.txtb.txt"
        /// 
        ///     "my list: @(files, '; ')"                           expands to string      "my list: a.txt; b.txt"
        /// 
        ///     "my list: @(files->'%(Filename)')"                  expands to string      "my list: a;b"
        /// 
        ///     "my list: @(files -> 'temp\%(Filename).xml', ' ')   expands to string      "my list: temp\a.xml temp\b.xml"
        /// 
        ///     "my list: @(files->'')                              expands to string      "my list: ;".
        /// </summary>
        /// <remarks>
        /// This is a private nested class, exposed only through the Expander class.
        /// That allows it to hide its private methods even from Expander.
        /// </remarks>
        private static class ItemExpander
        {
            /// <summary>
            /// Execute the list of transform functions.
            /// </summary>
            /// <typeparam name="S">class, IItem.</typeparam>
            internal static IEnumerable<Pair<string, S>> Transform<S>(Expander<P, I> expander, bool includeNullEntries, Stack<TransformFunction<S>> transformFunctionStack, IEnumerable<Pair<string, S>> itemsOfType)
                where S : class, IItem
            {
                // If we have transforms on our stack, then we'll execute those first
                // This effectively runs backwards through the set
                if (transformFunctionStack.Count > 0)
                {
                    TransformFunction<S> function = transformFunctionStack.Pop();

                    foreach (Pair<string, S> item in Transform(expander, includeNullEntries, transformFunctionStack, function.Execute(expander, includeNullEntries, itemsOfType)))
                    {
                        yield return item;
                    }
                }
                else
                {
                    // When we have no more tranforms on the stack, iterate over the items
                    // that we have to return them
                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        yield return item;
                    }
                }
            }

            /// <summary>
            /// Expands any item vector in the expression into items.
            /// 
            /// For example, expands @(Compile->'%(foo)') to a set of items derived from the items in the "Compile" list.
            /// 
            /// If there is no item vector in the expression (for example a literal "foo.cpp"), returns null.
            /// If the item vector expression expands to no items, returns an empty list.
            /// If item expansion is not allowed by the provided options, returns null.
            /// If there is an item vector but concatenated with something else, throws InvalidProjectFileException.
            /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
            /// 
            /// If the expression is a transform, any transformations to an expression that evaluates to nothing (i.e., because
            /// an item has no value for a piece of metadata) are optionally indicated with a null entry in the list. This means 
            /// that the length of the returned list is always the same as the length of the referenced item list in the input string.
            /// That's important for any correlation the caller wants to do.
            /// 
            /// If expression was a transform, 'isTransformExpression' is true, otherwise false.
            ///
            /// Item type of the items returned is determined by the IItemFactory passed in; if the IItemFactory does not 
            /// have an item type set on it, it will be given the item type of the item vector to use.
            /// </summary>
            /// <typeparam name="S">Type of the items provided by the item source used for expansion.</typeparam>
            /// <typeparam name="T">Type of the items that should be returned.</typeparam>
            internal static IList<T> ExpandSingleItemVectorExpressionIntoItems<S, T>(
                    Expander<P, I> expander, string expression, IItemProvider<S> items, IItemFactory<S, T> itemFactory, ExpanderOptions options,
                    bool includeNullEntries, out bool isTransformExpression, IElementLocation elementLocation)
                where S : class, IItem
                where T : class, IItem
            {
                isTransformExpression = false;

                var expressionCapture = ExpandSingleItemVectorExpressionIntoExpressionCapture(expression, options, elementLocation);
                if (expressionCapture == null)
                {
                    return null;
                }

                return ExpandExpressionCaptureIntoItems(expressionCapture, expander, items, itemFactory, options, includeNullEntries,
                    out isTransformExpression, elementLocation);
            }

            internal static ExpressionShredder.ItemExpressionCapture ExpandSingleItemVectorExpressionIntoExpressionCapture(
                    string expression, ExpanderOptions options, IElementLocation elementLocation)
            {
                if (((options & ExpanderOptions.ExpandItems) == 0) || (expression.Length == 0))
                {
                    return null;
                }

                List<ExpressionShredder.ItemExpressionCapture> matches;
                if (expression.IndexOf('@') == -1)
                {
                    return null;
                }
                else
                {
                    matches = ExpressionShredder.GetReferencedItemExpressions(expression);

                    if (matches == null)
                    {
                        return null;
                    }
                }

                ExpressionShredder.ItemExpressionCapture match = matches[0];

                // We have a single valid @(itemlist) reference in the given expression.
                // If the passed-in expression contains exactly one item list reference,
                // with nothing else concatenated to the beginning or end, then proceed
                // with itemizing it, otherwise error.
                ProjectErrorUtilities.VerifyThrowInvalidProject(match.Value == expression, elementLocation, "EmbeddedItemVectorCannotBeItemized", expression);
                ErrorUtilities.VerifyThrow(matches.Count == 1, "Expected just one item vector");

                return match;
            }

            internal static IList<T> ExpandExpressionCaptureIntoItems<S, T>(
                    ExpressionShredder.ItemExpressionCapture expressionCapture, Expander<P, I> expander, IItemProvider<S> items, IItemFactory<S, T> itemFactory,
                    ExpanderOptions options, bool includeNullEntries, out bool isTransformExpression, IElementLocation elementLocation)
                where S : class, IItem
                where T : class, IItem
            {
                ErrorUtilities.VerifyThrow(items != null, "Cannot expand items without providing items");
                isTransformExpression = false;
                bool brokeEarlyNonEmpty;

                // If the incoming factory doesn't have an item type that it can use to 
                // create items, it's our indication that the caller wants its items to have the type of the
                // expression being expanded. For example, items from expanding "@(Compile") should
                // have the item type "Compile".
                if (itemFactory.ItemType == null)
                {
                    itemFactory.ItemType = expressionCapture.ItemType;
                }


                IList<T> result;
                if (expressionCapture.Separator != null)
                {
                    // Reference contains a separator, for example @(Compile, ';').
                    // We need to flatten the list into 
                    // a scalar and then create a single item. Basically we need this
                    // to be able to convert item lists with user specified separators into properties.
                    string expandedItemVector;
                    using SpanBasedStringBuilder builder = Strings.GetSpanBasedStringBuilder();
                    brokeEarlyNonEmpty = ExpandExpressionCaptureIntoStringBuilder(expander, expressionCapture, items, elementLocation, builder, options);

                    if (brokeEarlyNonEmpty)
                    {
                        return null;
                    }

                    expandedItemVector = builder.ToString();

                    result = new List<T>(1);

                    if (expandedItemVector.Length > 0)
                    {
                        T newItem = itemFactory.CreateItem(expandedItemVector, elementLocation.File);

                        result.Add(newItem);
                    }

                    return result;
                }

                List<Pair<string, S>> itemsFromCapture;
                brokeEarlyNonEmpty = ExpandExpressionCapture(expander, expressionCapture, items, elementLocation /* including null items */, options, true, out isTransformExpression, out itemsFromCapture);

                if (brokeEarlyNonEmpty)
                {
                    return null;
                }

                result = new List<T>(itemsFromCapture.Count);

                foreach (var itemTuple in itemsFromCapture)
                {
                    var itemSpec = itemTuple.Key;
                    var originalItem = itemTuple.Value;

                    if (itemSpec != null && originalItem == null)
                    {
                        // We have an itemspec, but no base item
                        result.Add(itemFactory.CreateItem(itemSpec, elementLocation.File));
                    }
                    else if (itemSpec != null && originalItem != null)
                    {
                        result.Add(itemSpec.Equals(originalItem.EvaluatedIncludeEscaped)
                            ? itemFactory.CreateItem(originalItem, elementLocation.File) // itemspec came from direct item reference, no transforms
                            : itemFactory.CreateItem(itemSpec, originalItem, elementLocation.File)); // itemspec came from a transform and is different from its original item
                    }
                    else if (includeNullEntries)
                    {
                        // The itemspec is null and the base item doesn't matter
                        result.Add(null);
                    }
                }

                return result;
            }

            /// <summary>
            /// Expands an expression capture into a list of items
            /// If the capture uses a separator, then all the items are concatenated into one string using that separator.
            /// 
            /// Returns true if ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and so it broke out early.
            /// </summary>
            /// <param name="isTransformExpression"></param>
            /// <param name="itemsFromCapture">
            /// List of items.
            /// 
            /// Item1 represents the item string, escaped
            /// Item2 represents the original item.
            /// 
            /// Item1 differs from Item2's string when it is coming from a transform.
            /// 
            /// </param>
            /// <param name="expander">The expander whose state will be used to expand any transforms.</param>
            /// <param name="expressionCapture">The <see cref="ExpandSingleItemVectorExpressionIntoExpressionCapture"/> representing the structure of an item expression.</param>
            /// <param name="evaluatedItems"><see cref="IItemProvider{T}"/> to provide the inital items (which may get subsequently transformed, if <paramref name="expressionCapture"/> is a transform expression)>.</param>
            /// <param name="elementLocation">Location of the xml element containing the <paramref name="expressionCapture"/>.</param>
            /// <param name="options">expander options.</param>
            /// <param name="includeNullEntries">Wether to include items that evaluated to empty / null.</param>
            internal static bool ExpandExpressionCapture<S>(
                Expander<P, I> expander,
                ExpressionShredder.ItemExpressionCapture expressionCapture,
                IItemProvider<S> evaluatedItems,
                IElementLocation elementLocation,
                ExpanderOptions options,
                bool includeNullEntries,
                out bool isTransformExpression,
                out List<Pair<string, S>> itemsFromCapture
                )
                where S : class, IItem
            {
                ErrorUtilities.VerifyThrow(evaluatedItems != null, "Cannot expand items without providing items");
                // There's something wrong with the expression, and we ended up with a blank item type
                ProjectErrorUtilities.VerifyThrowInvalidProject(!string.IsNullOrEmpty(expressionCapture.ItemType), elementLocation, "InvalidFunctionPropertyExpression");

                isTransformExpression = false;

                var itemsOfType = evaluatedItems.GetItems(expressionCapture.ItemType);

                // If there are no items of the given type, then bail out early
                if (itemsOfType.Count == 0)
                {
                    // .. but only if there isn't a function "Count()", since that will want to return something (zero) for an empty list
                    if (expressionCapture.Captures?.Any(capture => string.Equals(capture.FunctionName, "Count", StringComparison.OrdinalIgnoreCase)) != true)
                    {
                        itemsFromCapture = new List<Pair<string, S>>();
                        return false;
                    }
                }

                if (expressionCapture.Captures != null)
                {
                    isTransformExpression = true;
                }

                itemsFromCapture = new List<Pair<string, S>>(itemsOfType.Count);

                if (!isTransformExpression)
                {
                    // No transform: expression is like @(Compile), so include the item spec without a transform base item
                    foreach (S item in itemsOfType)
                    {
                        if ((item.EvaluatedIncludeEscaped.Length > 0) && (options & ExpanderOptions.BreakOnNotEmpty) != 0)
                        {
                            return true;
                        }

                        itemsFromCapture.Add(new Pair<string, S>(item.EvaluatedIncludeEscaped, item));
                    }
                }
                else
                {
                    Stack<TransformFunction<S>> transformFunctionStack = PrepareTransformStackFromMatch<S>(elementLocation, expressionCapture);

                    // iterate over the tranform chain, creating the final items from its results
                    foreach (Pair<string, S> itemTuple in Transform<S>(expander, includeNullEntries, transformFunctionStack, IntrinsicItemFunctions<S>.GetItemPairEnumerable(itemsOfType)))
                    {
                        if (!string.IsNullOrEmpty(itemTuple.Key) && (options & ExpanderOptions.BreakOnNotEmpty) != 0)
                        {
                            return true; // broke out early; result cannot be trusted
                        }

                        itemsFromCapture.Add(itemTuple);
                    }
                }

                if (expressionCapture.Separator != null)
                {
                    var joinedItems = string.Join(expressionCapture.Separator, itemsFromCapture.Select(i => i.Key));
                    itemsFromCapture.Clear();
                    itemsFromCapture.Add(new Pair<string, S>(joinedItems, null));
                }

                return false; // did not break early
            }

            /// <summary>
            /// Expands all item vectors embedded in the given expression into a single string.
            /// If the expression is empty, returns empty string.
            /// If ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and it broke out early, returns null. Otherwise the result can be trusted.
            /// </summary>
            /// <typeparam name="T">Type of the items provided.</typeparam>
            internal static string ExpandItemVectorsIntoString<T>(Expander<P, I> expander, string expression, IItemProvider<T> items, ExpanderOptions options, IElementLocation elementLocation)
                where T : class, IItem
            {
                if ((options & ExpanderOptions.ExpandItems) == 0 || expression.Length == 0)
                {
                    return expression;
                }

                ErrorUtilities.VerifyThrow(items != null, "Cannot expand items without providing items");

                List<ExpressionShredder.ItemExpressionCapture> matches = ExpressionShredder.GetReferencedItemExpressions(expression);

                if (matches == null)
                {
                    return expression;
                }

                using SpanBasedStringBuilder builder = Strings.GetSpanBasedStringBuilder();
                // As we walk through the matches, we need to copy out the original parts of the string which
                // are not covered by the match.  This preserves original behavior which did not trim whitespace
                // from between separators.
                int lastStringIndex = 0;
                for (int i = 0; i < matches.Count; i++)
                {
                    if (matches[i].Index > lastStringIndex)
                    {
                        if ((options & ExpanderOptions.BreakOnNotEmpty) != 0)
                        {
                            return null;
                        }

                        builder.Append(expression, lastStringIndex, matches[i].Index - lastStringIndex);
                    }

                    bool brokeEarlyNonEmpty = ExpandExpressionCaptureIntoStringBuilder(expander, matches[i], items, elementLocation, builder, options);

                    if (brokeEarlyNonEmpty)
                    {
                        return null;
                    }

                    lastStringIndex = matches[i].Index + matches[i].Length;
                }

                builder.Append(expression, lastStringIndex, expression.Length - lastStringIndex);

                return builder.ToString();
            }

            /// <summary>
            /// Prepare the stack of transforms that will be executed on a given set of items.
            /// </summary>
            /// <typeparam name="S">class, IItem.</typeparam>
            private static Stack<TransformFunction<S>> PrepareTransformStackFromMatch<S>(IElementLocation elementLocation, ExpressionShredder.ItemExpressionCapture match)
                where S : class, IItem
            {
                // There's something wrong with the expression, and we ended up with no function names
                ProjectErrorUtilities.VerifyThrowInvalidProject(match.Captures.Count > 0, elementLocation, "InvalidFunctionPropertyExpression");

                Stack<TransformFunction<S>> transformFunctionStack = new Stack<TransformFunction<S>>(match.Captures.Count);

                // Create a TransformFunction for each transform in the chain by extracting the relevant information
                // from the regex parsing results
                // Each will be pushed onto a stack in right to left order (i.e. the inner/right most will be on the
                // bottom of the stack, the outer/left most will be on the top
                for (int n = match.Captures.Count - 1; n >= 0; n--)
                {
                    string function = match.Captures[n].Value;
                    string functionName = match.Captures[n].FunctionName;
                    string argumentsExpression = match.Captures[n].FunctionArguments;

                    string[] arguments = null;

                    if (functionName == null)
                    {
                        functionName = "ExpandQuotedExpressionFunction";
                        arguments = new string[] { function };
                    }
                    else if (argumentsExpression != null)
                    {
                        arguments = ExtractFunctionArguments(elementLocation, argumentsExpression, argumentsExpression);
                    }

                    IntrinsicItemFunctions<S>.ItemTransformFunction transformFunction = IntrinsicItemFunctions<S>.GetItemTransformFunction(elementLocation, functionName, typeof(S));

                    // Push our tranform on to the stack
                    transformFunctionStack.Push(new TransformFunction<S>(elementLocation, functionName, transformFunction, arguments));
                }

                return transformFunctionStack;
            }

            /// <summary>
            /// Expand the match provided into a string, and append that to the provided InternableString.
            /// Returns true if ExpanderOptions.BreakOnNotEmpty was passed, expression was going to be non-empty, and so it broke out early.
            /// </summary>
            /// <typeparam name="S">Type of source items.</typeparam>
            private static bool ExpandExpressionCaptureIntoStringBuilder<S>(
                Expander<P, I> expander,
                ExpressionShredder.ItemExpressionCapture capture,
                IItemProvider<S> evaluatedItems,
                IElementLocation elementLocation,
                SpanBasedStringBuilder builder,
                ExpanderOptions options
                )
                where S : class, IItem
            {
                List<Pair<string, S>> itemsFromCapture;
                bool throwaway;
                var brokeEarlyNonEmpty = ExpandExpressionCapture(expander, capture, evaluatedItems, elementLocation /* including null items */, options, true, out throwaway, out itemsFromCapture);

                if (brokeEarlyNonEmpty)
                {
                    return true;
                }

                int startLength = builder.Length;
                bool truncate = IsTruncationEnabled(options);

                // if the capture.Separator is not null, then ExpandExpressionCapture would have joined the items using that separator itself
                for (int i = 0; i < itemsFromCapture.Count; i++)
                {
                    var item = itemsFromCapture[i];
                    if (truncate)
                    {
                        if (i >= ItemLimitPerExpansion)
                        {
                            builder.Append("...");
                            return false;
                        }
                        int currentLength = builder.Length - startLength;
                        if (!string.IsNullOrEmpty(item.Key) && currentLength + item.Key.Length > CharacterLimitPerExpansion)
                        {
                            int truncateIndex = CharacterLimitPerExpansion - currentLength - 3;
                            if (truncateIndex > 0)
                            {
                                builder.Append(item.Key, 0, truncateIndex);
                            }
                            builder.Append("...");
                            return false;
                        }
                    }
                    builder.Append(item.Key);
                    if (i < itemsFromCapture.Count - 1)
                    {
                        builder.Append(";");
                    }
                }
                
                return false;
            }

            /// <summary>
            /// The set of functions that called during an item transformation, e.g. @(CLCompile->ContainsMetadata('MetaName', 'metaValue')).
            /// </summary>
            /// <typeparam name="S">class, IItem.</typeparam>
            internal static class IntrinsicItemFunctions<S>
                where S : class, IItem
            {
                /// <summary>
                /// A cache of previously created item function delegates.
                /// </summary>
                private static ConcurrentDictionary<string, ItemTransformFunction> s_transformFunctionDelegateCache = new ConcurrentDictionary<string, ItemTransformFunction>(StringComparer.OrdinalIgnoreCase);

                /// <summary>
                /// Delegate that represents the signature of all item transformation functions
                /// This is used to support calling the functions by name.
                /// </summary>
                public delegate IEnumerable<Pair<string, S>> ItemTransformFunction(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments);

                /// <summary>
                /// Get a delegate to the given item transformation function by supplying the name and the
                /// Item type that should be used.
                /// </summary>
                internal static ItemTransformFunction GetItemTransformFunction(IElementLocation elementLocation, string functionName, Type itemType)
                {
                    ItemTransformFunction transformFunction = null;
                    string qualifiedFunctionName = itemType.FullName + "::" + functionName;

                    // We may have seen this delegate before, if so grab the one we already created
                    if (!s_transformFunctionDelegateCache.TryGetValue(qualifiedFunctionName, out transformFunction))
                    {
                        if (FileUtilities.ItemSpecModifiers.IsDerivableItemSpecModifier(functionName))
                        {
                            // Create a delegate to the function we're going to call
                            transformFunction = new ItemTransformFunction(ItemSpecModifierFunction);
                        }
                        else
                        {
                            MethodInfo itemFunctionInfo = typeof(IntrinsicItemFunctions<S>).GetMethod(functionName, BindingFlags.IgnoreCase | BindingFlags.NonPublic | BindingFlags.Static);

                            if (itemFunctionInfo == null)
                            {
                                functionName = "ExecuteStringFunction";
                                itemFunctionInfo = typeof(IntrinsicItemFunctions<S>).GetMethod(functionName, BindingFlags.IgnoreCase | BindingFlags.NonPublic | BindingFlags.Static);
                                if (itemFunctionInfo == null)
                                {
                                    ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "UnknownItemFunction", functionName);
                                    return null;
                                }
                            }
                            try
                            {
                                // Create a delegate to the function we're going to call
                                transformFunction = (ItemTransformFunction)itemFunctionInfo.CreateDelegate(typeof(ItemTransformFunction));
                            }
                            catch (ArgumentException)
                            {
                                //  Prior to porting to .NET Core, this code was passing false as the throwOnBindFailure parameter to Delegate.CreateDelegate.
                                //  Since MethodInfo.CreateDelegate doesn't have this option, we catch the ArgumentException to preserve the previous behavior
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "UnknownItemFunction", functionName);
                            }
                        }

                        if (transformFunction == null)
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "UnknownItemFunction", functionName);
                            return null;
                        }

                        // record our delegate for future use
                        s_transformFunctionDelegateCache[qualifiedFunctionName] = transformFunction;
                    }

                    return transformFunction;
                }

                /// <summary>
                /// Create an enumerator from a base IEnumerable of items into an enumerable
                /// of transformation result which includes the new itemspec and the base item.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> GetItemPairEnumerable(IEnumerable<S> itemsOfType)
                {
                    // iterate over the items, and yield out items in the tuple format
                    foreach (var item in itemsOfType)
                    {
                        if (Traits.Instance.UseLazyWildCardEvaluation)
                        {
                            foreach (
                                var resultantItem in
                                EngineFileUtilities.GetFileListEscaped(
                                    item.ProjectDirectory,
                                    item.EvaluatedIncludeEscaped,
                                    forceEvaluate: true))
                            {
                                yield return new Pair<string, S>(resultantItem, item);
                            }
                        }
                        else
                        {
                            yield return new Pair<string, S>(item.EvaluatedIncludeEscaped, item);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns the number of items in the list.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Count(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    yield return new Pair<string, S>(Convert.ToString(itemsOfType.Count(), CultureInfo.InvariantCulture), null /* no base item */);
                }

                /// <summary>
                /// Intrinsic function that returns the specified built-in modifer value of the items in itemsOfType
                /// Tuple is {current item include, item under transformation}.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> ItemSpecModifierFunction(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        // If the item include has become empty,
                        // this is the end of the pipeline for this item
                        if (String.IsNullOrEmpty(item.Key))
                        {
                            continue;
                        }

                        string result = null;

                        try
                        {
                            // If we're not a ProjectItem or ProjectItemInstance, then ProjectDirectory will be null.
                            // In that case, we're safe to get the current directory as we'll be running on TaskItems which
                            // only exist within a target where we can trust the current directory
                            string directoryToUse = item.Value.ProjectDirectory ?? Directory.GetCurrentDirectory();
                            string definingProjectEscaped = item.Value.GetMetadataValueEscaped(FileUtilities.ItemSpecModifiers.DefiningProjectFullPath);

                            result = FileUtilities.ItemSpecModifiers.GetItemSpecModifier(directoryToUse, item.Key, definingProjectEscaped, functionName);
                        }
                        catch (Exception e) // Catching Exception, but rethrowing unless it's a well-known exception.
                        {
                            // InvalidOperationException is how GetItemSpecModifier communicates invalid conditions upwards, so 
                            // we do not want to rethrow in that case.  
                            if (ExceptionHandling.NotExpectedException(e) && !(e is InvalidOperationException))
                            {
                                throw;
                            }

                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidItemFunctionExpression", functionName, item.Key, e.Message);
                        }

                        if (!String.IsNullOrEmpty(result))
                        {
                            // GetItemSpecModifier will have returned us an escaped string
                            // there is nothing more to do than yield it into the pipeline
                            yield return new Pair<string, S>(result, item.Value);
                        }
                        else if (includeNullEntries)
                        {
                            yield return new Pair<string, S>(null, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns the subset of items that actually exist on disk.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Exists(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (String.IsNullOrEmpty(item.Key))
                        {
                            continue;
                        }

                        // Unescape as we are passing to the file system
                        string unescapedPath = EscapingUtilities.UnescapeAll(item.Key);

                        string rootedPath = null;
                        try
                        {
                            // If we're a projectitem instance then we need to get
                            // the project directory and be relative to that
                            if (Path.IsPathRooted(unescapedPath))
                            {
                                rootedPath = unescapedPath;
                            }
                            else
                            {
                                // If we're not a ProjectItem or ProjectItemInstance, then ProjectDirectory will be null.
                                // In that case, we're safe to get the current directory as we'll be running on TaskItems which
                                // only exist within a target where we can trust the current directory
                                string baseDirectoryToUse = item.Value.ProjectDirectory ?? String.Empty;
                                rootedPath = Path.Combine(baseDirectoryToUse, unescapedPath);
                            }
                        }
                        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidItemFunctionExpression", functionName, item.Key, e.Message);
                        }

                        if (File.Exists(rootedPath) || Directory.Exists(rootedPath))
                        {
                            yield return item;
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that combines the existing paths of the input items with a given relative path.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Combine(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 1, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    string relativePath = arguments[0];

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (String.IsNullOrEmpty(item.Key))
                        {
                            continue;
                        }

                        // Unescape as we are passing to the file system
                        string unescapedPath = EscapingUtilities.UnescapeAll(item.Key);
                        string combinedPath = Path.Combine(unescapedPath, relativePath);
                        string escapedPath = EscapingUtilities.Escape(combinedPath);
                        yield return new Pair<string, S>(escapedPath, null);
                    }
                }

                /// <summary>
                /// Intrinsic function that returns all ancestor directories of the given items.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> GetPathsOfAllDirectoriesAbove(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    // Phase 1: find all the applicable directories.

                    SortedSet<string> directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (String.IsNullOrEmpty(item.Key))
                        {
                            continue;
                        }

                        string directoryName = null;

                        // Unescape as we are passing to the file system
                        string unescapedPath = EscapingUtilities.UnescapeAll(item.Key);

                        try
                        {
                            string rootedPath;

                            // If we're a projectitem instance then we need to get
                            // the project directory and be relative to that
                            if (Path.IsPathRooted(unescapedPath))
                            {
                                rootedPath = unescapedPath;
                            }
                            else
                            {
                                // If we're not a ProjectItem or ProjectItemInstance, then ProjectDirectory will be null.
                                // In that case, we're safe to get the current directory as we'll be running on TaskItems which
                                // only exist within a target where we can trust the current directory
                                string baseDirectoryToUse = item.Value.ProjectDirectory ?? String.Empty;
                                rootedPath = Path.Combine(baseDirectoryToUse, unescapedPath);
                            }

                            // Normalize the path to remove elements like "..".
                            // Otherwise we run the risk of returning two or more different paths that represent the
                            // same directory.
                            rootedPath = FileUtilities.NormalizePath(rootedPath);
                            directoryName = Path.GetDirectoryName(rootedPath);
                        }
                        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidItemFunctionExpression", functionName, item.Key, e.Message);
                        }

                        while (!String.IsNullOrEmpty(directoryName))
                        {
                            if (directories.Contains(directoryName))
                            {
                                // We've already got this directory (and all its ancestors) in the set.
                                break;
                            }

                            directories.Add(directoryName);
                            directoryName = Path.GetDirectoryName(directoryName);
                        }
                    }

                    // Phase 2: Go through the directories and return them in order

                    foreach (string directoryPath in directories)
                    {
                        string escapedDirectoryPath = EscapingUtilities.Escape(directoryPath);
                        yield return new Pair<string, S>(escapedDirectoryPath, null);
                    }
                }

                /// <summary>
                /// Intrinsic function that returns the DirectoryName of the items in itemsOfType
                /// UNDONE: This can be removed in favor of a built-in %(DirectoryName) metadata in future.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> DirectoryName(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    Dictionary<string, string> directoryNameTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        // If the item include has become empty,
                        // this is the end of the pipeline for this item
                        if (String.IsNullOrEmpty(item.Key))
                        {
                            continue;
                        }

                        string directoryName;
                        if (!directoryNameTable.TryGetValue(item.Key, out directoryName))
                        {
                            // Unescape as we are passing to the file system
                            string unescapedPath = EscapingUtilities.UnescapeAll(item.Key);

                            try
                            {
                                string rootedPath;

                                // If we're a projectitem instance then we need to get
                                // the project directory and be relative to that
                                if (Path.IsPathRooted(unescapedPath))
                                {
                                    rootedPath = unescapedPath;
                                }
                                else
                                {
                                    // If we're not a ProjectItem or ProjectItemInstance, then ProjectDirectory will be null.
                                    // In that case, we're safe to get the current directory as we'll be running on TaskItems which
                                    // only exist within a target where we can trust the current directory
                                    string baseDirectoryToUse = item.Value.ProjectDirectory ?? String.Empty;
                                    rootedPath = Path.Combine(baseDirectoryToUse, unescapedPath);
                                }

                                directoryName = Path.GetDirectoryName(rootedPath);
                            }
                            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidItemFunctionExpression", functionName, item.Key, e.Message);
                            }

                            // Escape as this is going back into the engine
                            directoryName = EscapingUtilities.Escape(directoryName);
                            directoryNameTable[unescapedPath] = directoryName;
                        }

                        if (!String.IsNullOrEmpty(directoryName))
                        {
                            // return a result through the enumerator
                            yield return new Pair<string, S>(directoryName, item.Value);
                        }
                        else if (includeNullEntries)
                        {
                            yield return new Pair<string, S>(null, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns the contents of the metadata in specified in argument[0].
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Metadata(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 1, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    string metadataName = arguments[0];

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (item.Value != null)
                        {
                            string metadataValue = null;

                            try
                            {
                                metadataValue = item.Value.GetMetadataValueEscaped(metadataName);
                            }
                            catch (ArgumentException ex) // Blank metadata name
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                            }
                            catch (InvalidOperationException ex)
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                            }

                            if (!String.IsNullOrEmpty(metadataValue))
                            {
                                // It may be that the itemspec has unescaped ';'s in it so we need to split here to handle
                                // that case.
                                if (metadataValue.IndexOf(';') >= 0)
                                {
                                    var splits = ExpressionShredder.SplitSemiColonSeparatedList(metadataValue);

                                    foreach (string itemSpec in splits)
                                    {
                                        // return a result through the enumerator
                                        yield return new Pair<string, S>(itemSpec, item.Value);
                                    }
                                }
                                else
                                {
                                    // return a result through the enumerator
                                    yield return new Pair<string, S>(metadataValue, item.Value);
                                }
                            }
                            else if (metadataValue != String.Empty && includeNullEntries)
                            {
                                yield return new Pair<string, S>(metadataValue, item.Value);
                            }
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns only the items from itemsOfType that have distinct Item1 in the Tuple
                /// Using a case sensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> DistinctWithCase(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    return DistinctWithComparer(expander, elementLocation, includeNullEntries, functionName, itemsOfType, arguments, StringComparer.Ordinal);
                }

                /// <summary>
                /// Intrinsic function that returns only the items from itemsOfType that have distinct Item1 in the Tuple
                /// Using a case insensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Distinct(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    return DistinctWithComparer(expander, elementLocation, includeNullEntries, functionName, itemsOfType, arguments, StringComparer.OrdinalIgnoreCase);
                }

                /// <summary>
                /// Intrinsic function that returns only the items from itemsOfType that have distinct Item1 in the Tuple
                /// Using a case insensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> DistinctWithComparer(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments, StringComparer comparer)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    // This dictionary will ensure that we only return one result per unique itemspec
                    Dictionary<string, S> seenItems = new Dictionary<string, S>(comparer);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (item.Key != null && !seenItems.ContainsKey(item.Key))
                        {
                            seenItems[item.Key] = item.Value;

                            yield return new Pair<string, S>(item.Key, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function reverses the item list.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> Reverse(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);
                    foreach (Pair<String, S> item in itemsOfType.Reverse())
                    {
                        yield return new Pair<string, S>(item.Key, item.Value);
                    }
                }

                /// <summary>
                /// Intrinsic function that transforms expressions like the %(foo) in @(Compile->'%(foo)').
                /// </summary>
                internal static IEnumerable<Pair<string, S>> ExpandQuotedExpressionFunction(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 1, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        MetadataMatchEvaluator matchEvaluator;
                        string include = null;

                        // If we've been handed a null entry by an uptream tranform
                        // then we don't want to try to tranform it with an itempec modification.
                        // Simply allow the null to be passed along (if, we are including nulls as specified by includeNullEntries
                        if (item.Key != null)
                        {
                            matchEvaluator = new MetadataMatchEvaluator(item.Key, item.Value, elementLocation);

                            include = RegularExpressions.ItemMetadataPattern.Value.Replace(arguments[0], matchEvaluator.GetMetadataValueFromMatch);
                        }

                        // Include may be empty. Historically we have created items with empty include
                        // and ultimately set them on tasks, but we don't do that anymore as it's broken.
                        // Instead we optionally add a null, so that input and output lists are the same length; this allows
                        // the caller to possibly do correlation.

                        // We pass in the existing item so we can copy over its metadata
                        if (!string.IsNullOrEmpty(include))
                        {
                            yield return new Pair<string, S>(include, item.Value);
                        }
                        else if (includeNullEntries)
                        {
                            yield return new Pair<string, S>(null, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that transforms expressions by invoking methods of System.String on the itemspec
                /// of the item in the pipeline.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> ExecuteStringFunction(
                    Expander<P, I> expander,
                    IElementLocation elementLocation,
                    bool includeNullEntries,
                    string functionName,
                    IEnumerable<Pair<string, S>> itemsOfType,
                    string[] arguments)
                {
                    // Transform: expression is like @(Compile->'%(foo)'), so create completely new items,
                    // using the Include from the source items
                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        Function<P> function = new Function<P>(
                            typeof(string),
                            item.Key,
                            item.Key,
                            functionName,
                            arguments,
                            BindingFlags.Public | BindingFlags.InvokeMethod,
                            string.Empty,
                            expander.UsedUninitializedProperties,
                            expander._fileSystem);

                        object result = function.Execute(item.Key, expander._properties, ExpanderOptions.ExpandAll, elementLocation);

                        string include = Expander<P, I>.PropertyExpander<P>.ConvertToString(result);

                        // We pass in the existing item so we can copy over its metadata
                        if (include.Length > 0)
                        {
                            yield return new Pair<string, S>(include, item.Value);
                        }
                        else if (includeNullEntries)
                        {
                            yield return new Pair<string, S>(null, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns the items from itemsOfType with their metadata cleared, i.e. only the itemspec is retained.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> ClearMetadata(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments == null || arguments.Length == 0, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (includeNullEntries || item.Key != null)
                        {
                            yield return new Pair<string, S>(item.Key, null);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns only those items that have a not-blank value for the metadata specified
                /// Using a case insensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> HasMetadata(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 1, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    string metadataName = arguments[0];

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        string metadataValue = null;

                        try
                        {
                            metadataValue = item.Value.GetMetadataValueEscaped(metadataName);
                        }
                        catch (ArgumentException ex) // Blank metadata name
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                        }
                        catch (InvalidOperationException ex)
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                        }

                        // GetMetadataValueEscaped returns empty string for missing metadata,
                        // but IItem specifies it should return null
                        if (!string.IsNullOrEmpty(metadataValue))
                        {
                            // return a result through the enumerator
                            yield return new Pair<string, S>(item.Key, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns only those items have the given metadata value
                /// Using a case insensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> WithMetadataValue(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 2, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    string metadataName = arguments[0];
                    string metadataValueToFind = arguments[1];

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        string metadataValue = null;

                        try
                        {
                            metadataValue = item.Value.GetMetadataValueEscaped(metadataName);
                        }
                        catch (ArgumentException ex) // Blank metadata name
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                        }
                        catch (InvalidOperationException ex)
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                        }

                        if (metadataValue != null && String.Equals(metadataValue, metadataValueToFind, StringComparison.OrdinalIgnoreCase))
                        {
                            // return a result through the enumerator
                            yield return new Pair<string, S>(item.Key, item.Value);
                        }
                    }
                }

                /// <summary>
                /// Intrinsic function that returns a boolean to indicate if any of the items have the given metadata value
                /// Using a case insensitive comparison.
                /// </summary>
                internal static IEnumerable<Pair<string, S>> AnyHaveMetadataValue(Expander<P, I> expander, IElementLocation elementLocation, bool includeNullEntries, string functionName, IEnumerable<Pair<string, S>> itemsOfType, string[] arguments)
                {
                    ProjectErrorUtilities.VerifyThrowInvalidProject(arguments?.Length == 2, elementLocation, "InvalidItemFunctionSyntax", functionName, arguments == null ? 0 : arguments.Length);

                    string metadataName = arguments[0];
                    string metadataValueToFind = arguments[1];
                    bool metadataFound = false;

                    foreach (Pair<string, S> item in itemsOfType)
                    {
                        if (item.Value != null)
                        {
                            string metadataValue = null;

                            try
                            {
                                metadataValue = item.Value.GetMetadataValueEscaped(metadataName);
                            }
                            catch (ArgumentException ex) // Blank metadata name
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                            }
                            catch (InvalidOperationException ex)
                            {
                                ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "CannotEvaluateItemMetadata", metadataName, ex.Message);
                            }

                            if (metadataValue != null && String.Equals(metadataValue, metadataValueToFind, StringComparison.OrdinalIgnoreCase))
                            {
                                metadataFound = true;

                                // return a result through the enumerator
                                yield return new Pair<string, S>("true", item.Value);

                                // break out as soon as we found a match
                                yield break;
                            }
                        }
                    }

                    if (!metadataFound)
                    {
                        // We did not locate an item with the required metadata
                        yield return new Pair<string, S>("false", null);
                    }
                }
            }

            /// <summary>
            /// Represents all the components of a transform function, including the ability to execute it. 
            /// </summary>
            /// <typeparam name="S">class, IItem.</typeparam>
            internal class TransformFunction<S>
                where S : class, IItem
            {
                /// <summary>
                /// The delegate that points to the transform function.
                /// </summary>
                private IntrinsicItemFunctions<S>.ItemTransformFunction _transform;

                /// <summary>
                /// Arguments to pass to the transform function as parsed out of the project file.
                /// </summary>
                private string[] _arguments;

                /// <summary>
                /// The element location of the transform expression.
                /// </summary>
                private IElementLocation _elementLocation;

                /// <summary>
                /// The name of the function that this class will call.
                /// </summary>
                private string _functionName;

                /// <summary>
                /// TransformFunction constructor.
                /// </summary>
                public TransformFunction(IElementLocation elementLocation, string functionName, IntrinsicItemFunctions<S>.ItemTransformFunction transform, string[] arguments)
                {
                    _elementLocation = elementLocation;
                    _functionName = functionName;
                    _transform = transform;
                    _arguments = arguments;
                }

                /// <summary>
                /// Arguments to pass to the transform function as parsed out of the project file.
                /// </summary>
                public string[] Arguments
                {
                    get { return _arguments; }
                }

                /// <summary>
                /// The element location of the transform expression.
                /// </summary>
                public IElementLocation ElementLocation
                {
                    get { return _elementLocation; }
                }

                /// <summary>
                /// Execute this transform function with the arguments contained within this TransformFunction instance.
                /// </summary>
                public IEnumerable<Pair<string, S>> Execute(Expander<P, I> expander, bool includeNullEntries, IEnumerable<Pair<string, S>> itemsOfType)
                {
                    // Execute via the delegate
                    return _transform(expander, _elementLocation, includeNullEntries, _functionName, itemsOfType, _arguments);
                }
            }

            /// <summary>
            /// A functor that returns the value of the metadata in the match
            /// that is on the item it was created with.
            /// </summary>
            private class MetadataMatchEvaluator
            {
                /// <summary>
                /// The current ItemSpec of the item being matched.
                /// </summary>
                private string _itemSpec;

                /// <summary>
                /// Item used as the source of metadata.
                /// </summary>
                private IItem _sourceOfMetadata;

                /// <summary>
                /// Location of the match.
                /// </summary>
                private IElementLocation _elementLocation;

                /// <summary>
                /// Constructor.
                /// </summary>
                internal MetadataMatchEvaluator(string itemSpec, IItem sourceOfMetadata, IElementLocation elementLocation)
                {
                    _itemSpec = itemSpec;
                    _sourceOfMetadata = sourceOfMetadata;
                    _elementLocation = elementLocation;
                }

                /// <summary>
                /// Expands the metadata in the match provided into a string result.
                /// The match is expected to be the content of a transform.
                /// For example, representing "%(Filename.obj)" in the original expression "@(Compile->'%(Filename.obj)')".
                /// </summary>
                internal string GetMetadataValueFromMatch(Match match)
                {
                    string name = match.Groups[RegularExpressions.NameGroup].Value;

                    ProjectErrorUtilities.VerifyThrowInvalidProject(match.Groups[RegularExpressions.ItemSpecificationGroup].Length == 0, _elementLocation, "QualifiedMetadataInTransformNotAllowed", match.Value, name);

                    string value = null;
                    try
                    {
                        if (FileUtilities.ItemSpecModifiers.IsDerivableItemSpecModifier(name))
                        {
                            // If we're not a ProjectItem or ProjectItemInstance, then ProjectDirectory will be null.
                            // In that case, we're safe to get the current directory as we'll be running on TaskItems which
                            // only exist within a target where we can trust the current directory
                            string directoryToUse = _sourceOfMetadata.ProjectDirectory ?? Directory.GetCurrentDirectory();
                            string definingProjectEscaped = _sourceOfMetadata.GetMetadataValueEscaped(FileUtilities.ItemSpecModifiers.DefiningProjectFullPath);

                            value = FileUtilities.ItemSpecModifiers.GetItemSpecModifier(directoryToUse, _itemSpec, definingProjectEscaped, name);
                        }
                        else
                        {
                            value = _sourceOfMetadata.GetMetadataValueEscaped(name);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(_elementLocation, "CannotEvaluateItemMetadata", name, ex.Message);
                    }

                    return value;
                }
            }
        }

        /// <summary>
        /// Regular expressions used by the expander.
        /// The expander currently uses regular expressions rather than a parser to do its work.
        /// </summary>
        private static class RegularExpressions
        {
            /**************************************************************************************************************************
            * WARNING: The regular expressions below MUST be kept in sync with the expressions in the ProjectWriter class -- if the
            * description of an item vector changes, the expressions must be updated in both places.
            *************************************************************************************************************************/

            /// <summary>
            /// Regular expression used to match item metadata references embedded in strings.
            /// For example, %(Compile.DependsOn) or %(DependsOn).
            /// </summary> 
            internal static readonly Lazy<Regex> ItemMetadataPattern = new Lazy<Regex>(
                () => new Regex(ItemMetadataSpecification,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture | RegexOptions.Compiled));

            /// <summary>
            /// Name of the group matching the "name" of a metadatum.
            /// </summary>
            internal const string NameGroup = "NAME";

            /// <summary>
            /// Name of the group matching the prefix on a metadata expression, for example "Compile." in "%(Compile.Object)".
            /// </summary>
            internal const string ItemSpecificationGroup = "ITEM_SPECIFICATION";

            /// <summary>
            /// Name of the group matching the item type in an item expression or metadata expression.
            /// </summary>
            internal const string ItemTypeGroup = "ITEM_TYPE";

            /// <summary>
            /// regular expression used to match item metadata references outside of item vector transforms.
            /// </summary>
            /// <remarks>PERF WARNING: this Regex is complex and tends to run slowly.</remarks>
            internal static readonly Lazy<Regex> NonTransformItemMetadataPattern = new Lazy<Regex>(
                () => new Regex
                    (
                    @"((?<=" + ItemVectorWithTransformLHS + @")" + ItemMetadataSpecification + @"(?!" +
                    ItemVectorWithTransformRHS + @")) | ((?<!" + ItemVectorWithTransformLHS + @")" +
                    ItemMetadataSpecification + @"(?=" + ItemVectorWithTransformRHS + @")) | ((?<!" +
                    ItemVectorWithTransformLHS + @")" + ItemMetadataSpecification + @"(?!" +
                    ItemVectorWithTransformRHS + @"))",
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture | RegexOptions.Compiled
                    ));

            /// <summary>
            /// Complete description of an item metadata reference, including the optional qualifying item type.
            /// For example, %(Compile.DependsOn) or %(DependsOn).
            /// </summary> 
            private const string ItemMetadataSpecification = @"%\(\s* (?<ITEM_SPECIFICATION>(?<ITEM_TYPE>" + ProjectWriter.itemTypeOrMetadataNameSpecification + @")\s*\.\s*)? (?<NAME>" + ProjectWriter.itemTypeOrMetadataNameSpecification + @") \s*\)";

            /// <summary>
            /// description of an item vector with a transform, left hand side. 
            /// </summary> 
            private const string ItemVectorWithTransformLHS = @"@\(\s*" + ProjectWriter.itemTypeOrMetadataNameSpecification + @"\s*->\s*'[^']*";

            /// <summary>
            /// description of an item vector with a transform, right hand side. 
            /// </summary> 
            private const string ItemVectorWithTransformRHS = @"[^']*'(\s*,\s*'[^']*')?\s*\)";

            /**************************************************************************************************************************
             * WARNING: The regular expressions above MUST be kept in sync with the expressions in the ProjectWriter class.
             *************************************************************************************************************************/
        }

        private struct FunctionBuilder<T>
            where T : class, IProperty
        {
            /// <summary>
            /// The type of this function's receiver.
            /// </summary>
            public Type ReceiverType { get; set; }

            /// <summary>
            /// The name of the function.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// The arguments for the function.
            /// </summary>
            public string[] Arguments { get; set; }

            /// <summary>
            /// The expression that this function is part of.
            /// </summary>
            public string Expression { get; set; }

            /// <summary>
            /// The property name that this function is applied on.
            /// </summary>
            public string Receiver { get; set; }

            /// <summary>
            /// The binding flags that will be used during invocation of this function.
            /// </summary>
            public BindingFlags BindingFlags { get; set; }

            /// <summary>
            /// The remainder of the body once the function and arguments have been extracted.
            /// </summary>
            public string Remainder { get; set; }

            public IFileSystem FileSystem { get; set; }

            /// <summary>
            /// List of properties which have been used but have not been initialized yet.
            /// </summary>
            public UsedUninitializedProperties UsedUninitializedProperties { get; set; }

            internal Function<T> Build()
            {
                return new Function<T>(
                    ReceiverType,
                    Expression,
                    Receiver,
                    Name,
                    Arguments,
                    BindingFlags,
                    Remainder,
                    UsedUninitializedProperties,
                    FileSystem
                    );
            }
        }

        /// <summary>
        /// This class represents the function as extracted from an expression
        /// It is also responsible for executing the function.
        /// </summary>
        /// <typeparam name="T">Type of the properties used to expand the expression.</typeparam>
        private class Function<T>
            where T : class, IProperty
        {
            /// <summary>
            /// The type of this function's receiver.
            /// </summary>
            private Type _receiverType;

            /// <summary>
            /// The name of the function.
            /// </summary>
            private string _methodMethodName;

            /// <summary>
            /// The arguments for the function.
            /// </summary>
            private string[] _arguments;

            /// <summary>
            /// The expression that this function is part of.
            /// </summary>
            private string _expression;

            /// <summary>
            /// The property name that this function is applied on.
            /// </summary>
            private string _receiver;

            /// <summary>
            /// The binding flags that will be used during invocation of this function.
            /// </summary>
            private BindingFlags _bindingFlags;

            /// <summary>
            /// The remainder of the body once the function and arguments have been extracted.
            /// </summary>
            private string _remainder;

            /// <summary>
            /// List of properties which have been used but have not been initialized yet.
            /// </summary>
            private UsedUninitializedProperties _usedUninitializedProperties;

            private IFileSystem _fileSystem;

            /// <summary>
            /// Construct a function that will be executed during property evaluation.
            /// </summary>
            internal Function(
                Type receiverType,
                string expression,
                string receiver,
                string methodName,
                string[] arguments,
                BindingFlags bindingFlags,
                string remainder,
                UsedUninitializedProperties usedUninitializedProperties,
                IFileSystem fileSystem)
            {
                _methodMethodName = methodName;
                if (arguments == null)
                {
                    _arguments = Array.Empty<string>();
                }
                else
                {
                    _arguments = arguments;
                }

                _receiver = receiver;
                _expression = expression;
                _receiverType = receiverType;
                _bindingFlags = bindingFlags;
                _remainder = remainder;
                _usedUninitializedProperties = usedUninitializedProperties;
                _fileSystem = fileSystem;
            }

            /// <summary>
            /// Part of the extraction may result in the name of the property
            /// This accessor is used by the Expander
            /// Examples of expression root:
            ///     [System.Diagnostics.Process]::Start
            ///     SomeMSBuildProperty.
            /// </summary>
            internal string Receiver
            {
                get { return _receiver; }
            }

            /// <summary>
            /// Extract the function details from the given property function expression.
            /// </summary>
            internal static Function<T> ExtractPropertyFunction(
                string expressionFunction,
                IElementLocation elementLocation,
                object propertyValue,
                UsedUninitializedProperties usedUnInitializedProperties,
                IFileSystem fileSystem)
            {
                // Used to aggregate all the components needed for a Function
                FunctionBuilder<T> functionBuilder = new FunctionBuilder<T> {FileSystem = fileSystem};

                // By default the expression root is the whole function expression
                ReadOnlySpan<char> expressionRoot = expressionFunction == null ? ReadOnlySpan<char>.Empty : expressionFunction.AsSpan();

                // The arguments for this function start at the first '('
                // If there are no arguments, then we're a property getter
                var argumentStartIndex = expressionFunction.IndexOf('(');

                // If we have arguments, then we only want the content up to but not including the '('
                if (argumentStartIndex > -1)
                {
                    expressionRoot = expressionRoot.Slice(0, argumentStartIndex);
                }

                // In case we ended up with something we don't understand
                ProjectErrorUtilities.VerifyThrowInvalidProject(!expressionRoot.IsEmpty, elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, String.Empty);

                functionBuilder.Expression = expressionFunction;
                functionBuilder.UsedUninitializedProperties = usedUnInitializedProperties;

                // This is a static method call
                // A static method is the content that follows the last "::", the rest being the type
                if (propertyValue == null && expressionRoot[0] == '[')
                {
                    var typeEndIndex = expressionRoot.IndexOf(']');

                    if (typeEndIndex < 1)
                    {
                        // We ended up with something other than a function expression
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionStaticMethodSyntax", expressionFunction, String.Empty);
                    }

                    var typeName = expressionRoot.Slice(1, typeEndIndex - 1).ToString();
                    var methodStartIndex = typeEndIndex + 1;

                    if (expressionRoot.Length > methodStartIndex + 2 && expressionRoot[methodStartIndex] == ':' && expressionRoot[methodStartIndex + 1] == ':')
                    {
                        // skip over the "::"
                        methodStartIndex += 2;
                    }
                    else
                    {
                        // We ended up with something other than a static function expression
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionStaticMethodSyntax", expressionFunction, String.Empty);
                    }

                    ConstructFunction(elementLocation, expressionFunction, argumentStartIndex, methodStartIndex, ref functionBuilder);

                    // Locate a type that matches the body of the expression.
                    var receiverType = GetTypeForStaticMethod(typeName, functionBuilder.Name);

                    if (receiverType == null)
                    {
                        // We ended up with something other than a type
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionTypeUnavailable", expressionFunction, typeName);
                    }

                    functionBuilder.ReceiverType = receiverType;
                }
                else if (expressionFunction[0] == '[') // We have an indexer
                {
                    var indexerEndIndex = expressionFunction.IndexOf(']', 1);
                    if (indexerEndIndex < 1)
                    {
                        // We ended up with something other than a function expression
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, AssemblyResources.GetString("InvalidFunctionPropertyExpressionDetailMismatchedSquareBrackets"));
                    }

                    var methodStartIndex = indexerEndIndex + 1;

                    functionBuilder.ReceiverType = propertyValue.GetType();

                    ConstructIndexerFunction(expressionFunction, elementLocation, propertyValue, methodStartIndex, indexerEndIndex, ref functionBuilder);
                }
                else // This could be a property reference, or a chain of function calls
                {
                    // Look for an instance function call next, such as in SomeStuff.ToLower()
                    var methodStartIndex = expressionRoot.IndexOf('.');
                    if (methodStartIndex == -1)
                    {
                        // We don't have a function invocation in the expression root, return null
                        return null;
                    }

                    // skip over the '.';
                    methodStartIndex++;

                    var rootEndIndex = expressionRoot.IndexOf('.');

                    // If this is an instance function rather than a static, then we'll capture the name of the property referenced
                    var functionReceiver = expressionRoot.Slice(0, rootEndIndex).Trim().ToString();

                    // If propertyValue is null (we're not recursing), then we're expecting a valid property name
                    if (propertyValue == null && !IsValidPropertyName(functionReceiver))
                    {
                        // We extracted something that wasn't a valid property name, fail.
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, String.Empty);
                    }

                    // If we are recursively acting on a type that has been already produced then pass that type inwards (e.g. we are interpreting a function call chain)
                    // Otherwise, the receiver of the function is a string
                    var receiverType = propertyValue?.GetType() ?? typeof(string);

                    functionBuilder.Receiver = functionReceiver;
                    functionBuilder.ReceiverType = receiverType;

                    ConstructFunction(elementLocation, expressionFunction, argumentStartIndex, methodStartIndex, ref functionBuilder);
                }

                return functionBuilder.Build();
            }

            /// <summary>
            /// Execute the function on the given instance.
            /// </summary>
            internal object Execute(object objectInstance, IPropertyProvider<T> properties, ExpanderOptions options, IElementLocation elementLocation)
            {
                object functionResult = String.Empty;
                object[] args = null;

                try
                {
                    // If there is no object instance, then the method invocation will be a static
                    if (objectInstance == null)
                    {
                        // Check that the function that we're going to call is valid to call
                        if (!IsStaticMethodAvailable(_receiverType, _methodMethodName))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionMethodUnavailable", _methodMethodName, _receiverType.FullName);
                        }

                        _bindingFlags |= BindingFlags.Static;

                        // For our intrinsic function we need to support calling of internal methods
                        // since we don't want them to be public
                        if (_receiverType == typeof(Microsoft.Build.Evaluation.IntrinsicFunctions))
                        {
                            _bindingFlags |= BindingFlags.NonPublic;
                        }
                    }
                    else
                    {
                        // Check that the function that we're going to call is valid to call
                        if (!IsInstanceMethodAvailable(_methodMethodName))
                        {
                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionMethodUnavailable", _methodMethodName, _receiverType.FullName);
                        }

                        _bindingFlags |= BindingFlags.Instance;

                        // The object that we're about to call methods on may have escaped characters
                        // in it, we want to operate on the unescaped string in the function, just as we
                        // want to pass arguments that are unescaped (see below)
                        if (objectInstance is string)
                        {
                            objectInstance = EscapingUtilities.UnescapeAll((string)objectInstance);
                        }
                    }

                    // We have a methodinfo match, need to plug in the arguments
                    args = new object[_arguments.Length];

                    // Assemble our arguments ready for passing to our method
                    for (int n = 0; n < _arguments.Length; n++)
                    {
                        object argument = PropertyExpander<T>.ExpandPropertiesLeaveTypedAndEscaped(
                            _arguments[n],
                            properties,
                            options,
                            elementLocation,
                            _usedUninitializedProperties,
                            _fileSystem);

                        if (argument is string argumentValue)
                        {
                            // Unescape the value since we're about to send it out of the engine and into
                            // the function being called. If a file or a directory function, fix the path
                            if (_receiverType == typeof(System.IO.File) || _receiverType == typeof(System.IO.Directory)
                                || _receiverType == typeof(System.IO.Path))
                            {
                                argumentValue = FileUtilities.FixFilePath(argumentValue);
                            }

                            args[n] = EscapingUtilities.UnescapeAll(argumentValue);
                        }
                        else
                        {
                            args[n] = argument;
                        }
                    }

                    // Handle special cases where the object type needs to affect the choice of method
                    // The default binder and method invoke, often chooses the incorrect Equals and CompareTo and 
                    // fails the comparison, because what we have on the right is generally a string.
                    // This special casing is to realize that its a comparison that is taking place and handle the
                    // argument type coercion accordingly; effectively pre-preparing the argument type so 
                    // that it matches the left hand side ready for the default binder’s method invoke.
                    if (objectInstance != null && args.Length == 1 && (String.Equals("Equals", _methodMethodName, StringComparison.OrdinalIgnoreCase) || String.Equals("CompareTo", _methodMethodName, StringComparison.OrdinalIgnoreCase)))
                    {
                        // change the type of the final unescaped string into the destination
                        args[0] = Convert.ChangeType(args[0], objectInstance.GetType(), CultureInfo.InvariantCulture);
                    }

                    if (_receiverType == typeof(IntrinsicFunctions))
                    {
                        // Special case a few methods that take extra parameters that can't be passed in by the user
                        //

                        if (_methodMethodName.Equals("GetPathOfFileAbove") && args.Length == 1)
                        {
                            // Append the IElementLocation as a parameter to GetPathOfFileAbove if the user only
                            // specified the file name.  This is syntactic sugar so they don't have to always
                            // include $(MSBuildThisFileDirectory) as a parameter.
                            //
                            string startingDirectory = String.IsNullOrWhiteSpace(elementLocation.File) ? String.Empty : Path.GetDirectoryName(elementLocation.File);

                            args = new []
                            {
                                args[0],
                                startingDirectory,
                            };
                        }
                    }

                    // If we've been asked to construct an instance, then we
                    // need to locate an appropriate constructor and invoke it
                    if (String.Equals("new", _methodMethodName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryExecuteWellKnownConstructorNoThrow(out functionResult, args))
                        {
                            functionResult = LateBindExecute(null /* no previous exception */, BindingFlags.Public | BindingFlags.Instance, null /* no instance for a constructor */, args, true /* is constructor */);
                        }
                    }
                    else
                    {
                        bool wellKnownFunctionSuccess = false;

                        try
                        {
                            // First attempt to recognize some well-known functions to avoid binding
                            // and potential first-chance MissingMethodExceptions
                            wellKnownFunctionSuccess = TryExecuteWellKnownFunction(out functionResult, objectInstance, args);
                        }
                        // we need to preserve the same behavior on exceptions as the actual binder
                        catch (Exception ex)
                        {
                            string partiallyEvaluated = GenerateStringOfMethodExecuted(_expression, objectInstance, _methodMethodName, args);
                            if (options.HasFlag(ExpanderOptions.LeavePropertiesUnexpandedOnError))
                            {
                                return partiallyEvaluated;
                            }

                            ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", partiallyEvaluated, ex.Message.Replace("\r\n", " "));
                        }

                        if (!wellKnownFunctionSuccess)
                        {
                            // Execute the function given converted arguments
                            // The only exception that we should catch to try a late bind here is missing method
                            // otherwise there is the potential of running a function twice!
                            try
                            {
                                // First use InvokeMember using the standard binder - this will match and coerce as needed
                                functionResult = _receiverType.InvokeMember(_methodMethodName, _bindingFlags, Type.DefaultBinder, objectInstance, args, CultureInfo.InvariantCulture);
                            }
                            catch (MissingMethodException ex) // Don't catch and retry on any other exception
                            {
                                // If we're invoking a method, then there are deeper attempts that
                                // can be made to invoke the method
                                if ((_bindingFlags & BindingFlags.InvokeMethod) == BindingFlags.InvokeMethod)
                                {
                                    // The standard binder failed, so do our best to coerce types into the arguments for the function
                                    // This may happen if the types need coercion, but it may also happen if the object represents a type that contains open type parameters, that is, ContainsGenericParameters returns true. 
                                    functionResult = LateBindExecute(ex, _bindingFlags, objectInstance, args, false /* is not constructor */);
                                }
                                else
                                {
                                    // We were asked to get a property or field, and we found that we cannot
                                    // locate it. Since there is no further argument coersion possible
                                    // we'll throw right now.
                                    throw;
                                }
                            }
                        }
                    }

                    // If the result of the function call is a string, then we need to escape the result
                    // so that we maintain the "engine contains escaped data" state.
                    // The exception is that the user is explicitly calling MSBuild::Unescape or MSBuild::Escape
                    if (functionResult is string && !String.Equals("Unescape", _methodMethodName, StringComparison.OrdinalIgnoreCase) && !String.Equals("Escape", _methodMethodName, StringComparison.OrdinalIgnoreCase))
                    {
                        functionResult = EscapingUtilities.Escape((string)functionResult);
                    }

                    // We have nothing left to parse, so we'll return what we have
                    if (String.IsNullOrEmpty(_remainder))
                    {
                        return functionResult;
                    }

                    // Recursively expand the remaining property body after execution
                    return PropertyExpander<T>.ExpandPropertyBody(
                        _remainder,
                        functionResult,
                        properties,
                        options,
                        elementLocation,
                        _usedUninitializedProperties,
                        _fileSystem);
                }

                // Exceptions coming from the actual function called are wrapped in a TargetInvocationException
                catch (TargetInvocationException ex)
                {
                    // We ended up with something other than a function expression
                    string partiallyEvaluated = GenerateStringOfMethodExecuted(_expression, objectInstance, _methodMethodName, args);
                    if (options.HasFlag(ExpanderOptions.LeavePropertiesUnexpandedOnError))
                    {
                        // If the caller wants to ignore errors (in a log statement for example), just return the partially evaluated value
                        return partiallyEvaluated;
                    }
                    ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", partiallyEvaluated, ex.InnerException.Message.Replace("\r\n", " "));
                    return null;
                }

                // Any other exception was thrown by trying to call it
                catch (Exception ex)
                {
                    if (ExceptionHandling.NotExpectedFunctionException(ex))
                    {
                        throw;
                    }

                    // If there's a :: in the expression, they were probably trying for a static function
                    // invocation. Give them some more relevant info in that case
                    if (s_invariantCompareInfo.IndexOf(_expression, "::", CompareOptions.OrdinalIgnoreCase) > -1)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionStaticMethodSyntax", _expression, ex.Message.Replace("Microsoft.Build.Evaluation.IntrinsicFunctions.", "[MSBuild]::"));
                    }
                    else
                    {
                        // We ended up with something other than a function expression
                        string partiallyEvaluated = GenerateStringOfMethodExecuted(_expression, objectInstance, _methodMethodName, args);
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", partiallyEvaluated, ex.Message);
                    }

                    return null;
                }
            }

            /// <summary>
            /// Shortcut to avoid calling into binding if we recognize some most common functions.
            /// Binding is expensive and throws first-chance MissingMethodExceptions, which is
            /// bad for debugging experience and has a performance cost.
            /// A typical binding operation with exception can take ~1.500 ms; this call is ~0.050 ms
            /// (rough numbers just for comparison).
            /// See https://github.com/Microsoft/msbuild/issues/2217.
            /// </summary>
            /// <param name="returnVal">The value returned from the function call.</param>
            /// <param name="objectInstance">Object that the function is called on.</param>
            /// <param name="args">arguments.</param>
            /// <returns>True if the well known function call binding was successful.</returns>
            private bool TryExecuteWellKnownFunction(out object returnVal, object objectInstance, object[] args)
            {
                returnVal = null;

                if (objectInstance is string text)
                {
                    if (string.Equals(_methodMethodName, nameof(string.StartsWith), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string arg0))
                        {
                            returnVal = text.StartsWith(arg0);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.Replace), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArgs(args, out string arg0, out string arg1))
                        {
                            returnVal = text.Replace(arg0, arg1);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.Contains), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string arg0))
                        {
                            returnVal = text.Contains(arg0);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.ToUpperInvariant), StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length == 0)
                        {
                            returnVal = text.ToUpperInvariant();
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.ToLowerInvariant), StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length == 0)
                        {
                            returnVal = text.ToLowerInvariant();
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.EndsWith), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string arg0))
                        {
                            returnVal = text.EndsWith(arg0);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.ToLower), StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length == 0)
                        {
                            returnVal = text.ToLower();
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.IndexOf), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArgs(args, out string arg0, out StringComparison arg1))
                        {
                            returnVal = text.IndexOf(arg0, arg1);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.IndexOfAny), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string arg0))
                        {
                            returnVal = text.IndexOfAny(arg0.ToCharArray());
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.LastIndexOf), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string arg0))
                        {
                            returnVal = text.LastIndexOf(arg0);
                            return true;
                        }
                        else if (TryGetArgs(args, out arg0, out int startIndex))
                        {
                            returnVal = text.LastIndexOf(arg0, startIndex);
                            return true;
                        }
                        else if (TryGetArgs(args, out arg0, out StringComparison arg1))
                        {
                            returnVal = text.LastIndexOf(arg0, arg1);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.Length), StringComparison.OrdinalIgnoreCase))
                    {
                        if (args.Length == 0)
                        {
                            returnVal = text.Length;
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.Substring), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out int startIndex))
                        {
                            returnVal = text.Substring(startIndex);
                            return true;
                        }
                        else if (TryGetArgs(args, out startIndex, out int length))
                        {
                            returnVal = text.Substring(startIndex, length);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.Split), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string separator) && separator.Length == 1)
                        {
                            returnVal = text.Split(separator[0]);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.PadLeft), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out int totalWidth))
                        {
                            returnVal = text.PadLeft(totalWidth);
                            return true;
                        }
                        else if (TryGetArgs(args, out totalWidth, out string paddingChar) && paddingChar.Length == 1)
                        {
                            returnVal = text.PadLeft(totalWidth, paddingChar[0]);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.PadRight), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out int totalWidth))
                        {
                            returnVal = text.PadRight(totalWidth);
                            return true;
                        }
                        else if (TryGetArgs(args, out totalWidth, out string paddingChar) && paddingChar.Length == 1)
                        {
                            returnVal = text.PadRight(totalWidth, paddingChar[0]);
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.TrimStart), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string trimChars) && trimChars.Length > 0)
                        {
                            returnVal = text.TrimStart(trimChars.ToCharArray());
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, nameof(string.TrimEnd), StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out string trimChars) && trimChars.Length > 0)
                        {
                            returnVal = text.TrimEnd(trimChars.ToCharArray());
                            return true;
                        }
                    }
                    else if (string.Equals(_methodMethodName, "get_Chars", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out int index))
                        {
                            returnVal = text[index];
                            return true;
                        }
                    }
                }
                else if (objectInstance is string[])
                {
                    string[] stringArray = (string[])objectInstance;
                    if (string.Equals(_methodMethodName, "GetValue", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetArg(args, out int index))
                        {
                            returnVal = stringArray[index];
                            return true;
                        }
                    }
                }
                else if (objectInstance == null) // Calling a well-known static function
                {
                    if (_receiverType == typeof(string))
                    {
                        if (string.Equals(_methodMethodName, nameof(string.IsNullOrWhiteSpace), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = string.IsNullOrWhiteSpace(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(string.IsNullOrEmpty), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = string.IsNullOrEmpty(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(string.Copy), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = arg0;
                                return true;
                            }
                        }
                    }
                    else if (_receiverType == typeof(Math))
                    {
                        if (string.Equals(_methodMethodName, nameof(Math.Max), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out var arg0, out double arg1))
                            {
                                returnVal = Math.Max(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Math.Min), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out double arg0, out var arg1))
                            {
                                returnVal = Math.Min(arg0, arg1);
                                return true;
                            }
                        }
                    }
                    else if (_receiverType == typeof(IntrinsicFunctions))
                    {
                        if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.EnsureTrailingSlash), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.EnsureTrailingSlash(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.ValueOrDefault), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.ValueOrDefault(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.NormalizePath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (ElementsOfType(args, typeof(string)))
                            {
                                returnVal = IntrinsicFunctions.NormalizePath(Array.ConvertAll(args, o => (string) o));
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetDirectoryNameOfFileAbove), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.GetDirectoryNameOfFileAbove(arg0, arg1, _fileSystem);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetRegistryValueFromView), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length >= 4 &&
                                TryGetArgs(args, out string arg0, out string arg1, enforceLength: false))
                            {
                                returnVal = IntrinsicFunctions.GetRegistryValueFromView(arg0, arg1, args[2], new ArraySegment<object>(args, 3, args.Length - 3));
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.IsRunningFromVisualStudio), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.IsRunningFromVisualStudio();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.Escape), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.Escape(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetPathOfFileAbove), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.GetPathOfFileAbove(arg0, arg1, _fileSystem);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.Add), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out double arg0, out double arg1))
                            {
                                returnVal = arg0 + arg1;
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.Subtract), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out double arg0, out double arg1))
                            {
                                returnVal = arg0 - arg1;
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.Multiply), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out double arg0, out double arg1))
                            {
                                returnVal = arg0 * arg1;
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.Divide), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out double arg0, out double arg1))
                            {
                                returnVal = arg0 / arg1;
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetCurrentToolsDirectory), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetCurrentToolsDirectory();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetToolsDirectory32), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetToolsDirectory32();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetToolsDirectory64), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetToolsDirectory64();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetMSBuildSDKsPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetMSBuildSDKsPath();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetVsInstallRoot), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetVsInstallRoot();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetMSBuildExtensionsPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetMSBuildExtensionsPath();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetProgramFiles32), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = IntrinsicFunctions.GetProgramFiles32();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionEquals), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionEquals(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionNotEquals), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionNotEquals(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionGreaterThan), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionGreaterThan(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionGreaterThanOrEquals), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionGreaterThanOrEquals(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionLessThan), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionLessThan(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.VersionLessThanOrEquals), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.VersionLessThanOrEquals(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetTargetFrameworkIdentifier), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.GetTargetFrameworkIdentifier(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetTargetFrameworkVersion), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.GetTargetFrameworkVersion(arg0);
                                return true;
                            }
                            if (TryGetArgs(args, out string arg1, out int arg2))
                            {
                                returnVal = IntrinsicFunctions.GetTargetFrameworkVersion(arg1, arg2);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.IsTargetFrameworkCompatible), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArgs(args, out string arg0, out string arg1))
                            {
                                returnVal = IntrinsicFunctions.IsTargetFrameworkCompatible(arg0, arg1);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetTargetPlatformIdentifier), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.GetTargetPlatformIdentifier(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.GetTargetPlatformVersion), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.GetTargetPlatformVersion(arg0);
                                return true;
                            }
                            if (TryGetArgs(args, out string arg1, out int arg2))
                            {
                                returnVal = IntrinsicFunctions.GetTargetPlatformVersion(arg1, arg2);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.StableStringHash), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = IntrinsicFunctions.StableStringHash(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(IntrinsicFunctions.AreFeaturesEnabled), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out Version arg0))
                            {
                                returnVal = IntrinsicFunctions.AreFeaturesEnabled(arg0);
                                return true;
                            }
                        }
                    }
                    else if (_receiverType == typeof(Path))
                    {
                        if (string.Equals(_methodMethodName, nameof(Path.Combine), StringComparison.OrdinalIgnoreCase))
                        {
                            string arg0, arg1, arg2, arg3;

                            // Combine has fast implementations for up to 4 parameters: https://github.com/dotnet/corefx/blob/2c55db90d622fa6279184e6243f0470a3755d13c/src/Common/src/CoreLib/System/IO/Path.cs#L293-L317
                            switch (args.Length)
                            {
                                case 0:
                                    return false;
                                case 1:
                                    if (TryGetArg(args, out arg0))
                                    {
                                        returnVal = Path.Combine(arg0);
                                        return true;
                                    }
                                    break;
                                case 2:
                                    if (TryGetArgs(args, out arg0, out arg1))
                                    {
                                        returnVal = Path.Combine(arg0, arg1);
                                        return true;
                                    }
                                    break;
                                case 3:
                                    if (TryGetArgs(args, out arg0, out arg1, out arg2))
                                    {
                                        returnVal = Path.Combine(arg0, arg1, arg2);
                                        return true;
                                    }
                                    break;
                                case 4:
                                    if (TryGetArgs(args, out arg0, out arg1, out arg2, out arg3))
                                    {
                                        returnVal = Path.Combine(arg0, arg1, arg2, arg3);
                                        return true;
                                    }
                                    break;
                                default:
                                    if (ElementsOfType(args, typeof(string)))
                                    {
                                        returnVal = Path.Combine(Array.ConvertAll(args, o => (string) o));
                                        return true;
                                    }
                                    break;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = Path.DirectorySeparatorChar;
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.GetFullPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = Path.GetFullPath(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.IsPathRooted), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = Path.IsPathRooted(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.GetTempPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = Path.GetTempPath();
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.GetFileName), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = Path.GetFileName(arg0);
                                return true;
                            }
                        }
                        else if (string.Equals(_methodMethodName, nameof(Path.GetDirectoryName), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = Path.GetDirectoryName(arg0);
                                return true;
                            }
                        }
                    }
                    else if (_receiverType == typeof(Version))
                    {
                        if (string.Equals(_methodMethodName, nameof(Version.Parse), StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetArg(args, out string arg0))
                            {
                                returnVal = Version.Parse(arg0);
                                return true;
                            }
                        }
                    }
                    else if (_receiverType == typeof(System.Guid))
                    {
                        if (string.Equals(_methodMethodName, nameof(Guid.NewGuid), StringComparison.OrdinalIgnoreCase))
                        {
                            if (args.Length == 0)
                            {
                                returnVal = Guid.NewGuid();
                                return true;
                            }
                        }
                    }
                }

                if (Traits.Instance.LogPropertyFunctionsRequiringReflection)
                {
                    LogFunctionCall("PropertyFunctionsRequiringReflection", objectInstance, args);
                }

                return false;
            }

            /// <summary>
            /// Shortcut to avoid calling into binding if we recognize some most common constructors.
            /// Analogous to TryExecuteWellKnownFunction but guaranteed to not throw.
            /// </summary>
            /// <param name="returnVal">The instance as created by the constructor call.</param>
            /// <param name="args">Arguments.</param>
            /// <returns>True if the well known constructor call binding was successful.</returns>
            private bool TryExecuteWellKnownConstructorNoThrow(out object returnVal, object[] args)
            {
                returnVal = null;

                if (_receiverType == typeof(string))
                {
                    if (args.Length == 0)
                    {
                        returnVal = String.Empty;
                        return true;
                    }
                    if (TryGetArg(args, out string arg0))
                    {
                        returnVal = arg0;
                        return true;
                    }
                }
                return false;
            }

            private bool ElementsOfType(object[] args, Type type)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i].GetType() != type)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool TryGetArgs(object[] args, out string arg0, out string arg1, bool enforceLength = true)
            {
                arg0 = null;
                arg1 = null;

                if (enforceLength && args.Length != 2)
                {
                    return false;
                }

                if (args[0] is string value0 &&
                    args[1] is string value1)
                {
                    arg0 = value0;
                    arg1 = value1;

                    return true;
                }

                return false;
            }

            private bool TryGetArgs(object[] args, out string arg0, out string arg1, out string arg2)
            {
                arg0 = null;
                arg1 = null;
                arg2 = null;

                if (args.Length != 3)
                {
                    return false;
                }

                if (args[0] is string value0 &&
                    args[1] is string value1 &&
                    args[2] is string value2
                    )
                {
                    arg0 = value0;
                    arg1 = value1;
                    arg2 = value2;

                    return true;
                }

                return false;
            }

            private bool TryGetArgs(object[] args, out string arg0, out string arg1, out string arg2, out string arg3)
            {
                arg0 = null;
                arg1 = null;
                arg2 = null;
                arg3 = null;

                if (args.Length != 4)
                {
                    return false;
                }

                if (args[0] is string value0 &&
                    args[1] is string value1 &&
                    args[2] is string value2 &&
                    args[3] is string value3
                    )
                {
                    arg0 = value0;
                    arg1 = value1;
                    arg2 = value2;
                    arg3 = value3;

                    return true;
                }

                return false;
            }

            private bool TryGetArgs(object[] args, out string arg0, out string arg1)
            {
                arg0 = null;
                arg1 = null;

                if (args.Length != 2)
                {
                    return false;
                }

                if (args[0] is string value0 &&
                    args[1] is string value1)
                {
                    arg0 = value0;
                    arg1 = value1;

                    return true;
                }

                return false;
            }

            private static bool TryGetArg(object[] args, out int arg0)
            {
                if (args.Length != 1)
                {
                    arg0 = 0;
                    return false;
                }

                return TryConvertToInt(args[0], out arg0);
            }

            private static bool TryGetArg(object[] args, out Version arg0)
            {
                if (args.Length != 1)
                {
                    arg0 = default;
                    return false;
                }

                return TryConvertToVersion(args[0], out arg0);
            }

            private static bool TryConvertToVersion(object value, out Version arg0)
            {
                string val = value as string;

                if (string.IsNullOrEmpty(val) || !Version.TryParse(val, out arg0))
                {
                    arg0 = default;
                    return false;
                }

                return true;
            }

            private static bool TryConvertToInt(object value, out int arg0)
            {
                switch (value)
                {
                    case double d:
                        arg0 = Convert.ToInt32(d);
                        return arg0 == d;
                    case int i:
                        arg0 = i;
                        return true;
                    case string s when int.TryParse(s, out arg0):
                        return true;
                }

                arg0 = 0;
                return false;
            }

            private static bool TryConvertToDouble(object value, out double arg)
            {
                if (value is double unboxed)
                {
                    arg = unboxed;
                    return true;
                }
                else if (value is string str && double.TryParse(str, out arg))
                {
                    return true;
                }

                arg = 0;
                return false;
            }

            private static bool TryGetArg(object[] args, out string arg0)
            {
                if (args.Length != 1)
                {
                    arg0 = null;
                    return false;
                }

                arg0 = args[0] as string;
                return arg0 != null;
            }

            private static bool TryGetArgs(object[] args, out string arg0, out StringComparison arg1)
            {
                if (args.Length != 2)
                {
                    arg0 = null;
                    arg1 = default;

                    return false;
                }

                arg0 = args[0] as string;

                // reject enums as ints. In C# this would require a cast, which is not supported in msbuild expressions
                if (arg0 == null || !(args[1] is string comparisonTypeName) || int.TryParse(comparisonTypeName, out _))
                {
                    arg1 = default;
                    return false;
                }

                // Allow fully-qualified enum, e.g. "System.StringComparison.OrdinalIgnoreCase"
                if (comparisonTypeName.Contains('.'))
                {
                    comparisonTypeName = comparisonTypeName.Replace("System.StringComparison.", "").Replace("StringComparison.", "");
                }

                return Enum.TryParse(comparisonTypeName, out arg1);
            }

            private static bool TryGetArgs(object[] args, out int arg0, out int arg1)
            {
                arg0 = 0;
                arg1 = 0;

                if (args.Length != 2)
                {
                    return false;
                }

                return TryConvertToInt(args[0], out arg0) &&
                       TryConvertToInt(args[1], out arg1);
            }

            private static bool TryGetArgs(object[] args, out double arg0, out double arg1)
            {
                arg0 = 0;
                arg1 = 0;

                if (args.Length != 2)
                {
                    return false;
                }

                return TryConvertToDouble(args[0], out arg0) &&
                       TryConvertToDouble(args[1], out arg1);
            }

            private static bool TryGetArgs(object[] args, out int arg0, out string arg1)
            {
                arg0 = 0;
                arg1 = null;

                if (args.Length != 2)
                {
                    return false;
                }

                arg1 = args[1] as string;
                if (arg1 == null && args[1] is char ch)
                {
                    arg1 = ch.ToString();
                }

                if (TryConvertToInt(args[0], out arg0) &&
                    arg1 != null)
                {
                    return true;
                }

                return false;
            }

            private static bool TryGetArgs(object[] args, out string arg0, out int arg1)
            {
                arg0 = null;
                arg1 = 0;

                if (args.Length != 2)
                {
                    return false;
                }

                var value1 = args[1] as string;
                arg0 = args[0] as string;
                if (value1 != null &&
                    arg0 != null &&
                    int.TryParse(value1, out arg1))
                {
                    return true;
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void LogFunctionCall(string fileName, object objectInstance, object[] args)
            {
                var logFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                var argSignature = args != null
                    ? string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"))
                    : string.Empty;

                File.AppendAllText(logFile, $"ReceiverType={_receiverType?.FullName}; ObjectInstanceType={objectInstance?.GetType().FullName}; MethodName={_methodMethodName}({argSignature})\n");
            }

            /// <summary>
            /// Given a type name and method name, try to resolve the type.
            /// </summary>
            /// <param name="typeName">May be full name or assembly qualified name.</param>
            /// <param name="simpleMethodName">simple name of the method.</param>
            /// <returns></returns>
            private static Type GetTypeForStaticMethod(string typeName, string simpleMethodName)
            {
                Type receiverType;
                Tuple<string, Type> cachedTypeInformation;

                // If we don't have a type name, we already know that we won't be able to find a type.  
                // Go ahead and return here -- otherwise the Type.GetType() calls below will throw.
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    return null;
                }

                // Check if the type is in the whitelist cache. If it is, use it or load it.
                cachedTypeInformation = AvailableStaticMethods.GetTypeInformationFromTypeCache(typeName, simpleMethodName);
                if (cachedTypeInformation != null)
                {
                    // We need at least one of these set
                    ErrorUtilities.VerifyThrow(cachedTypeInformation.Item1 != null || cachedTypeInformation.Item2 != null, "Function type information needs either string or type represented.");

                    // If we have the type information in Type form, then just return that
                    if (cachedTypeInformation.Item2 != null)
                    {
                        return cachedTypeInformation.Item2;
                    }
                    else if (cachedTypeInformation.Item1 != null)
                    {
                        // This is a case where the Type is not available at compile time, so
                        // we are forced to bind by name instead
                        var assemblyQualifiedTypeName = cachedTypeInformation.Item1;

                        // Get the type from the assembly qualified type name from AvailableStaticMethods
                        receiverType = Type.GetType(assemblyQualifiedTypeName, false /* do not throw TypeLoadException if not found */, true /* ignore case */);

                        // If the type information from the cache is not loadable, it means the cache information got corrupted somehow
                        // Throw here to prevent adding null types in the cache
                        ErrorUtilities.VerifyThrowInternalNull(receiverType, $"Type information for {typeName} was present in the whitelist cache as {assemblyQualifiedTypeName} but the type could not be loaded.");

                        // If we've used it once, chances are that we'll be using it again
                        // We can record the type here since we know it's available for calling from the fact that is was in the AvailableStaticMethods table
                        AvailableStaticMethods.TryAdd(typeName, simpleMethodName, new Tuple<string, Type>(assemblyQualifiedTypeName, receiverType));

                        return receiverType;
                    }
                }

                // Get the type from mscorlib (or the currently running assembly)
                receiverType = Type.GetType(typeName, false /* do not throw TypeLoadException if not found */, true /* ignore case */);

                if (receiverType != null)
                {
                    // DO NOT CACHE THE TYPE HERE!
                    // We don't add the resolved type here in the AvailableStaticMethods table. This is because that table is used
                    // during function parse, but only later during execution do we check for the ability to call specific methods on specific types.
                    // Caching it here would load any type into the white list.
                    return receiverType;
                }

                // Note the following code path is only entered when MSBUILDENABLEALLPROPERTYFUNCTIONS == 1.
                // This environment variable must not be cached - it should be dynamically settable while the application is executing.
                if (Environment.GetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS") == "1")
                {
                    // We didn't find the type, so go probing. First in System
                    receiverType = GetTypeFromAssembly(typeName, "System");

                    // Next in System.Core
                    if (receiverType == null)
                    {
                        receiverType = GetTypeFromAssembly(typeName, "System.Core");
                    }

                    // We didn't find the type, so try to find it using the namespace
                    if (receiverType == null)
                    {
                        receiverType = GetTypeFromAssemblyUsingNamespace(typeName);
                    }

                    if (receiverType != null)
                    {
                        // If we've used it once, chances are that we'll be using it again
                        // We can cache the type here, since all functions are enabled
                        AvailableStaticMethods.TryAdd(typeName, new Tuple<string, Type>(typeName, receiverType));
                    }
                }

                return receiverType;
            }

            /// <summary>
            /// Gets the specified type using the namespace to guess the assembly that its in.
            /// </summary>
            private static Type GetTypeFromAssemblyUsingNamespace(string typeName)
            {
                string baseName = typeName;
                int assemblyNameEnd = baseName.Length;

                // If the string has no dot, or is nothing but a dot, we have no
                // namespace to look for, so we can't help.
                if (assemblyNameEnd <= 0)
                {
                    return null;
                }

                // We will work our way up the namespace looking for an assembly that matches
                while (assemblyNameEnd > 0)
                {
                    string candidateAssemblyName = baseName.Substring(0, assemblyNameEnd);

                    // Try to load the assembly with the computed name
                    Type foundType = GetTypeFromAssembly(typeName, candidateAssemblyName);

                    if (foundType != null)
                    {
                        // We have a match, so get the type from that assembly
                        return foundType;
                    }
                    else
                    {
                        // Keep looking as we haven't found a match yet
                        baseName = candidateAssemblyName;
                        assemblyNameEnd = baseName.LastIndexOf('.');
                    }
                }

                // We didn't find it, so we need to give up
                return null;
            }

            /// <summary>
            /// Get the specified type from the assembly partial name supplied.
            /// </summary>
            [SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadWithPartialName", Justification = "Necessary since we don't have the full assembly name. ")]
            private static Type GetTypeFromAssembly(string typeName, string candidateAssemblyName)
            {
                Type objectType = null;

                // Try to load the assembly with the computed name
#if FEATURE_GAC
#pragma warning disable 618, 612
                // Unfortunately Assembly.Load is not an alternative to LoadWithPartialName, since
                // Assembly.Load requires the full assembly name to be passed to it.
                // Therefore we must ignore the deprecated warning.
                Assembly candidateAssembly = Assembly.LoadWithPartialName(candidateAssemblyName);
#pragma warning restore 618, 612
#else
                Assembly candidateAssembly = null;
                try
                {
                    candidateAssembly = Assembly.Load(new AssemblyName(candidateAssemblyName));
                }
                catch (FileNotFoundException)
                {
                    // Swallow the error; LoadWithPartialName returned null when the partial name
                    // was not found but Load throws.  Either way we'll provide a nice "couldn't
                    // resolve this" error later.
                }
#endif

                if (candidateAssembly != null)
                {
                    objectType = candidateAssembly.GetType(typeName, false /* do not throw TypeLoadException if not found */, true /* ignore case */);
                }

                return objectType;
            }

            /// <summary>
            /// Extracts the name, arguments, binding flags, and invocation type for an indexer
            /// Also extracts the remainder of the expression that is not part of this indexer.
            /// </summary>
            private static void ConstructIndexerFunction(string expressionFunction, IElementLocation elementLocation, object propertyValue, int methodStartIndex, int indexerEndIndex, ref FunctionBuilder<T> functionBuilder)
            {
                string argumentsContent = expressionFunction.Substring(1, indexerEndIndex - 1);
                string remainder = expressionFunction.Substring(methodStartIndex);
                string functionName;
                string[] functionArguments;

                // If there are no arguments, then just create an empty array
                if (String.IsNullOrEmpty(argumentsContent))
                {
                    functionArguments = Array.Empty<string>();
                }
                else
                {
                    // We will keep empty entries so that we can treat them as null
                    functionArguments = ExtractFunctionArguments(elementLocation, expressionFunction, argumentsContent);
                }

                // choose the name of the function based on the type of the object that we
                // are using.
                if (propertyValue is Array)
                {
                    functionName = "GetValue";
                }
                else if (propertyValue is string)
                {
                    functionName = "get_Chars";
                }
                else // a regular indexer
                {
                    functionName = "get_Item";
                }

                functionBuilder.Name = functionName;
                functionBuilder.Arguments = functionArguments;
                functionBuilder.BindingFlags = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.InvokeMethod;
                functionBuilder.Remainder = remainder;
            }

            /// <summary>
            /// Extracts the name, arguments, binding flags, and invocation type for a static or instance function.
            /// Also extracts the remainder of the expression that is not part of this function.
            /// </summary>
            private static void ConstructFunction(IElementLocation elementLocation, string expressionFunction, int argumentStartIndex, int methodStartIndex, ref FunctionBuilder<T> functionBuilder)
            {
                // The unevaluated and unexpanded arguments for this function
                string[] functionArguments;

                // The name of the function that will be invoked
                ReadOnlySpan<char> functionName;

                // What's left of the expression once the function has been constructed
                ReadOnlySpan<char> remainder = ReadOnlySpan<char>.Empty;

                // The binding flags that we will use for this function's execution
                BindingFlags defaultBindingFlags = BindingFlags.IgnoreCase | BindingFlags.Public;

                ReadOnlySpan<char> expressionFunctionAsSpan = expressionFunction.AsSpan();
                
                ReadOnlySpan<char> expressionSubstringAsSpan = argumentStartIndex > -1 ? expressionFunctionAsSpan.Slice(methodStartIndex, argumentStartIndex - methodStartIndex) : ReadOnlySpan<char>.Empty;

                // There are arguments that need to be passed to the function
                if (argumentStartIndex > -1 && !expressionSubstringAsSpan.Contains(".".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    // separate the function and the arguments
                    functionName = expressionSubstringAsSpan.Trim();

                    // Skip the '('
                    argumentStartIndex++;

                    // Scan for the matching closing bracket, skipping any nested ones
                    int argumentsEndIndex = ScanForClosingParenthesis(expressionFunction, argumentStartIndex, out _, out _);

                    if (argumentsEndIndex == -1)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, AssemblyResources.GetString("InvalidFunctionPropertyExpressionDetailMismatchedParenthesis"));
                    }

                    // We have been asked for a method invocation
                    defaultBindingFlags |= BindingFlags.InvokeMethod;

                    // It may be that there are '()' but no actual arguments content
                    if (argumentStartIndex == expressionFunction.Length - 1)
                    {
                        functionArguments = Array.Empty<string>();
                    }
                    else
                    {
                        // we have content within the '()' so let's extract and deal with it
                        string argumentsContent = expressionFunction.Substring(argumentStartIndex, argumentsEndIndex - argumentStartIndex);

                        // If there are no arguments, then just create an empty array
                        if (string.IsNullOrEmpty(argumentsContent))
                        {
                            functionArguments = Array.Empty<string>();
                        }
                        else
                        {
                            // We will keep empty entries so that we can treat them as null
                            functionArguments = ExtractFunctionArguments(elementLocation, expressionFunction, argumentsContent);
                        }

                        remainder = expressionFunctionAsSpan.Slice(argumentsEndIndex + 1).Trim();
                    }
                }
                else
                {
                    int nextMethodIndex = expressionFunction.IndexOf('.', methodStartIndex);
                    int methodLength = expressionFunction.Length - methodStartIndex;
                    int indexerIndex = expressionFunction.IndexOf('[', methodStartIndex);

                    // We don't want to consume the indexer
                    if (indexerIndex >= 0 && indexerIndex < nextMethodIndex)
                    {
                        nextMethodIndex = indexerIndex;
                    }

                    functionArguments = Array.Empty<string>();

                    if (nextMethodIndex > 0)
                    {
                        methodLength = nextMethodIndex - methodStartIndex;
                        remainder = expressionFunctionAsSpan.Slice(nextMethodIndex).Trim();
                    }

                    ReadOnlySpan<char> netPropertyName = expressionFunctionAsSpan.Slice(methodStartIndex, methodLength).Trim();

                    ProjectErrorUtilities.VerifyThrowInvalidProject(netPropertyName.Length > 0, elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, String.Empty);

                    // We have been asked for a property or a field
                    defaultBindingFlags |= (BindingFlags.GetProperty | BindingFlags.GetField);

                    functionName = netPropertyName;
                }

                // either there are no functions left or what we have is another function or an indexer
                if (remainder.IsEmpty || remainder[0] == '.' || remainder[0] == '[')
                {
                    functionBuilder.Name = functionName.ToString();
                    functionBuilder.Arguments = functionArguments;
                    functionBuilder.BindingFlags = defaultBindingFlags;
                    functionBuilder.Remainder = remainder.ToString();
                }
                else
                {
                    // We ended up with something other than a function expression
                    ProjectErrorUtilities.ThrowInvalidProject(elementLocation, "InvalidFunctionPropertyExpression", expressionFunction, String.Empty);
                }
            }

            /// <summary>
            /// Coerce the arguments according to the parameter types
            /// Will only return null if the coercion didn't work due to an InvalidCastException.
            /// </summary>
            private static object[] CoerceArguments(object[] args, ParameterInfo[] parameters)
            {
                object[] coercedArguments = new object[args.Length];

                try
                {
                    // Do our best to coerce types into the arguments for the function
                    for (int n = 0; n < parameters.Length; n++)
                    {
                        if (args[n] == null)
                        {
                            // We can't coerce (object)null -- that's as general
                            // as it can get!
                            continue;
                        }

                        // Here we have special case conversions on a type basis
                        if (parameters[n].ParameterType == typeof(char[]))
                        {
                            coercedArguments[n] = args[n].ToString().ToCharArray();
                        }
                        else if (parameters[n].ParameterType.GetTypeInfo().IsEnum && args[n] is string && ((string)args[n]).Contains("."))
                        {
                            Type enumType = parameters[n].ParameterType;
                            string typeLeafName = enumType.Name + ".";
                            string typeFullName = enumType.FullName + ".";

                            // Enum.parse expects commas between enum components
                            // We'll support the C# type | syntax too
                            // We'll also allow the user to specify the leaf or full type name on the enum
                            string argument = args[n].ToString().Replace('|', ',').Replace(typeFullName, "").Replace(typeLeafName, "");

                            // Parse the string representation of the argument into the destination enum                                
                            coercedArguments[n] = Enum.Parse(enumType, argument);
                        }
                        else
                        {
                            // change the type of the final unescaped string into the destination
                            coercedArguments[n] = Convert.ChangeType(args[n], parameters[n].ParameterType, CultureInfo.InvariantCulture);
                        }
                    }
                }
                // The coercion failed therefore we return null
                catch (InvalidCastException)
                {
                    return null;
                }
                catch (FormatException)
                {
                    return null;
                }
                catch (OverflowException)
                {
                    // https://github.com/Microsoft/msbuild/issues/2882
                    // test: PropertyFunctionMathMaxOverflow
                    return null;
                }

                return coercedArguments;
            }

            /// <summary>
            /// Make an attempt to create a string showing what we were trying to execute when we failed.
            /// This will show any intermediate evaluation which may help the user figure out what happened.
            /// </summary>
            private string GenerateStringOfMethodExecuted(string expression, object objectInstance, string name, object[] args)
            {
                string parameters = String.Empty;
                if (args != null)
                {
                    foreach (object arg in args)
                    {
                        if (arg == null)
                        {
                            parameters += "null";
                        }
                        else
                        {
                            string argString = arg.ToString();
                            if (arg is string && argString.Length == 0)
                            {
                                parameters += "''";
                            }
                            else
                            {
                                parameters += arg.ToString();
                            }
                        }

                        parameters += ", ";
                    }

                    if (parameters.Length > 2)
                    {
                        parameters = parameters.Substring(0, parameters.Length - 2);
                    }
                }

                if (objectInstance == null)
                {
                    string typeName = _receiverType.FullName;

                    // We don't want to expose the real type name of our intrinsics
                    // so we'll replace it with "MSBuild"
                    if (_receiverType == typeof(Microsoft.Build.Evaluation.IntrinsicFunctions))
                    {
                        typeName = "MSBuild";
                    }
                    if ((_bindingFlags & BindingFlags.InvokeMethod) == BindingFlags.InvokeMethod)
                    {
                        return "[" + typeName + "]::" + name + "(" + parameters + ")";
                    }
                    else
                    {
                        return "[" + typeName + "]::" + name;
                    }
                }
                else
                {
                    string propertyValue = $"\"{objectInstance as string}\"";

                    if ((_bindingFlags & BindingFlags.InvokeMethod) == BindingFlags.InvokeMethod)
                    {
                        return propertyValue + "." + name + "(" + parameters + ")";
                    }
                    else
                    {
                        return propertyValue + "." + name;
                    }
                }
            }

            /// <summary>
            /// Check the property function whitelist whether this method is available.
            /// </summary>
            private static bool IsStaticMethodAvailable(Type receiverType, string methodName)
            {
                if (receiverType == typeof(Microsoft.Build.Evaluation.IntrinsicFunctions))
                {
                    // These are our intrinsic functions, so we're OK with those
                    return true;
                }

                if (Traits.Instance.EnableAllPropertyFunctions)
                {
                    // anything goes
                    return true;
                }

                return AvailableStaticMethods.GetTypeInformationFromTypeCache(receiverType.FullName, methodName) != null;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsInstanceMethodAvailable(string methodName)
            {
                if (Traits.Instance.EnableAllPropertyFunctions)
                {
                    // anything goes
                    return true;
                }

                // This could be expanded to an allow / deny list.
                return methodName != "GetType";
            }

            /// <summary>
            /// Construct and instance of objectType based on the constructor or method arguments provided.
            /// Arguments must never be null.
            /// </summary>
            private object LateBindExecute(Exception ex, BindingFlags bindingFlags, object objectInstance /* null unless instance method */, object[] args, bool isConstructor)
            {

                // First let's try for a method where all arguments are strings..
                Type[] types = new Type[_arguments.Length];
                for (int n = 0; n < _arguments.Length; n++)
                {
                    types[n] = typeof(string);
                }

                MethodBase memberInfo;
                if (isConstructor)
                {
                    memberInfo = _receiverType.GetConstructor(bindingFlags, null, types, null);
                }
                else
                {
                    memberInfo = _receiverType.GetMethod(_methodMethodName, bindingFlags, null, types, null);
                }

                // If we didn't get a match on all string arguments,
                // search for a method with the right number of arguments
                if (memberInfo == null)
                {
                    MethodBase[] members;
                    // Gather all methods that may match
                    if (isConstructor)
                    {
                        members = _receiverType.GetConstructors(bindingFlags);
                    }
                    else
                    {
                        members = _receiverType.GetMethods(bindingFlags);
                    }

                    foreach (MethodBase member in members)
                    {
                        ParameterInfo[] parameters = member.GetParameters();

                        // Simple match on name and number of params, we will be case insensitive
                        if (parameters.Length == _arguments.Length)
                        {
                            if (isConstructor || String.Equals(member.Name, _methodMethodName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Try to find a method with the right name, number of arguments and
                                // compatible argument types
                                // we have a match on the name and argument number
                                // now let's try to coerce the arguments we have
                                // into the arguments on the matching method
                                object[] coercedArguments = CoerceArguments(args, parameters);

                                if (coercedArguments != null)
                                {
                                    // We have a complete match
                                    memberInfo = member;
                                    args = coercedArguments;
                                    break;
                                }
                            }
                        }
                    }
                }

                object functionResult = null;

                // We have a match and coerced arguments, let's construct..
                if (memberInfo != null && args != null)
                {
                    if (isConstructor)
                    {
                        functionResult = ((ConstructorInfo)memberInfo).Invoke(args);
                    }
                    else
                    {
                        functionResult = ((MethodInfo)memberInfo).Invoke(objectInstance /* null if static method */, args);
                    }
                }
                else if (!isConstructor)
                {
                    throw ex;
                }

                if (functionResult == null && isConstructor)
                {
                    throw new TargetInvocationException(new MissingMethodException());
                }

                return functionResult;
            }
        }
    }

    /// <summary>
    /// This class wraps information about properties which have been used before they are initialized. 
    /// </summary>
    internal class UsedUninitializedProperties
    {
        /// <summary>
        /// This class wraps information about properties which have been used before they are initialized.
        /// </summary>
        internal UsedUninitializedProperties()
        {
            Properties = new Dictionary<string, IElementLocation>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hash set of properties which have been used before being initialized.
        /// </summary>
        internal IDictionary<string, IElementLocation> Properties
        {
            get;
            set;
        }

        /// <summary>
        ///  Are we currently supposed to warn if we used an uninitialized property.
        /// </summary>
        internal bool Warn
        {
            get;
            set;
        }

        /// <summary>
        ///  What is the currently evaluating property element, this is so that we do not add a un initialized property if we are evaluating that property.
        /// </summary>
        internal string CurrentlyEvaluatingPropertyElementName
        {
            get;
            set;
        }
    }
}
