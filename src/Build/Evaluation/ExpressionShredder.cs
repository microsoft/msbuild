// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
#if FEATURE_SECURITY_PERMISSIONS
using System.Security.Permissions;
#endif
using Microsoft.Build.Shared;
using System.Collections.Generic;
using Microsoft.Build.Collections;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// What the shredder should be looking for.
    /// </summary>
    [Flags]
    internal enum ShredderOptions
    {
        /// <summary>
        /// Don't use
        /// </summary>
        Invalid = 0x0,

        /// <summary>
        /// Shred item types
        /// </summary>
        ItemTypes = 0x1,

        /// <summary>
        /// Shred metadata not contained inside of a transform.
        /// </summary>
        MetadataOutsideTransforms = 0x2,

        /// <summary>
        /// Shred both items and metadata not contained in a transform.
        /// </summary>
        All = ItemTypes | MetadataOutsideTransforms
    }

    /// <summary>
    /// A class which interprets and splits MSBuild expressions
    /// </summary>
    internal static class ExpressionShredder
    {
        /// <summary>
        /// Splits an expression into fragments at semi-colons, except where the
        /// semi-colons are in a macro or separator expression.
        /// Fragments are trimmed and empty fragments discarded.
        /// </summary>
        /// <remarks>
        /// See <see cref="SemiColonTokenizer"/> for rules.
        /// </remarks>
        /// <param name="expression">List expression to split</param>
        /// <returns>Array of non-empty strings from split list.</returns>
        internal static SemiColonTokenizer SplitSemiColonSeparatedList(string expression)
        {
            return new SemiColonTokenizer(expression);
        }

        /// <summary>
        /// Given a list of expressions that may contain item list expressions,
        /// returns a pair of tables of all item names found, as K=Name, V=String.Empty;
        /// and all metadata not in transforms, as K=Metadata key, V=MetadataReference,
        /// where metadata key is like "itemname.metadataname" or "metadataname".
        /// PERF: Tables are null if there are no entries, because this is quite a common case.
        /// </summary>
        internal static ItemsAndMetadataPair GetReferencedItemNamesAndMetadata(IEnumerable<string> expressions)
        {
            ItemsAndMetadataPair pair = new ItemsAndMetadataPair(null, null);

            foreach (string expression in expressions)
            {
                GetReferencedItemNamesAndMetadata(expression, 0, expression.Length, ref pair, ShredderOptions.All);
            }

            return pair;
        }

        /// <summary>
        /// Returns true if there is a metadata expression (outside of a transform) in the expression.
        /// </summary>
        internal static bool ContainsMetadataExpressionOutsideTransform(string expression)
        {
            ItemsAndMetadataPair pair = new ItemsAndMetadataPair(null, null);

            GetReferencedItemNamesAndMetadata(expression, 0, expression.Length, ref pair, ShredderOptions.MetadataOutsideTransforms);

            bool result = (pair.Metadata?.Count > 0);

            return result;
        }

        /// <summary>
        /// Given a subexpression, finds referenced sub transform expressions
        /// itemName and separator will be null if they are not found
        /// return value will be null if no transform expressions are found
        /// </summary>
        internal static List<ItemExpressionCapture> GetReferencedItemExpressions(string expression)
        {
            return GetReferencedItemExpressions(expression, 0, expression.Length);
        }

        /// <summary>
        /// Given a subexpression, finds referenced sub transform expressions
        /// itemName and separator will be null if they are not found
        /// return value will be null if no transform expressions are found
        /// </summary>
        internal static List<ItemExpressionCapture> GetReferencedItemExpressions(string expression, int start, int end)
        {
            List<ItemExpressionCapture> subExpressions = null;

            int startInd = expression.IndexOf('@', start, end - start);

            if (startInd < 0)
            {
                return null;
            }

            for (int i = startInd; i < end; i++)
            {
                int restartPoint;
                int startPoint;

                if (Sink(expression, ref i, end, '@', '('))
                {
                    List<ItemExpressionCapture> transformExpressions = null;
                    string separator = null;
                    int separatorStart = -1;

                    // Start of a possible item list expression

                    // Store the index to backtrack to if this doesn't turn out to be a well
                    // formed expression. (Subtract one for the increment when we loop around.)
                    restartPoint = i - 1;

                    // Store the expression's start point
                    startPoint = i - 2;

                    SinkWhitespace(expression, ref i);

                    int startOfName = i;

                    if (!SinkValidName(expression, ref i, end))
                    {
                        i = restartPoint;
                        continue;
                    }

                    // '-' is a legitimate char in an item name, but we should match '->' as an arrow
                    // in '@(foo->'x')' rather than as the last char of the item name.
                    // The old regex accomplished this by being "greedy"
                    if (end > i && expression[i - 1] == '-' && expression[i] == '>')
                    {
                        i--;
                    }

                    // Grab the name, but continue to verify it's a well-formed expression
                    // before we store it.
                    string name = expression.Substring(startOfName, i - startOfName);

                    // return the item that we're working with
                    string itemName = name;

                    SinkWhitespace(expression, ref i);
                    bool transformOrFunctionFound = true;

                    // If there's an '->' eat it and the subsequent quoted expression or transform function
                    while (Sink(expression, ref i, end, '-', '>') && transformOrFunctionFound)
                    {
                        SinkWhitespace(expression, ref i);
                        int startTransform = i;

                        bool isQuotedTransform = SinkSingleQuotedExpression(expression, ref i, end);
                        if (isQuotedTransform)
                        {
                            int startQuoted = startTransform + 1;
                            int endQuoted = i - 1;
                            if (transformExpressions == null)
                            {
                                transformExpressions = new List<ItemExpressionCapture>();
                            }

                            transformExpressions.Add(new ItemExpressionCapture(startQuoted, endQuoted - startQuoted, expression.Substring(startQuoted, endQuoted - startQuoted)));
                            continue;
                        }

                        startTransform = i;
                        ItemExpressionCapture functionCapture = SinkItemFunctionExpression(expression, startTransform, ref i, end);
                        if (functionCapture != null)
                        {
                            if (transformExpressions == null)
                            {
                                transformExpressions = new List<ItemExpressionCapture>();
                            }

                            transformExpressions.Add(functionCapture);
                            continue;
                        }

                        if (!isQuotedTransform && functionCapture == null)
                        {
                            i = restartPoint;
                            transformOrFunctionFound = false;
                        }
                    }

                    if (!transformOrFunctionFound)
                    {
                        continue;
                    }

                    SinkWhitespace(expression, ref i);

                    // If there's a ',', eat it and the subsequent quoted expression
                    if (Sink(expression, ref i, ','))
                    {
                        SinkWhitespace(expression, ref i);

                        if (!Sink(expression, ref i, '\''))
                        {
                            i = restartPoint;
                            continue;
                        }

                        int closingQuote = expression.IndexOf('\'', i);
                        if (closingQuote == -1)
                        {
                            i = restartPoint;
                            continue;
                        }

                        separatorStart = i - startPoint;
                        separator = expression.Substring(i, closingQuote - i);

                        i = closingQuote + 1;
                    }

                    SinkWhitespace(expression, ref i);

                    if (!Sink(expression, ref i, ')'))
                    {
                        i = restartPoint;
                        continue;
                    }

                    int endPoint = i;
                    i--;

                    if (subExpressions == null)
                    {
                        subExpressions = new List<ItemExpressionCapture>();
                    }

                    // Create an expression capture that encompases the entire expression between the @( and the )
                    // with the item name and any separator contained within it
                    // and each transform expression contained within it (i.e. each ->XYZ)
                    ItemExpressionCapture expressionCapture = new ItemExpressionCapture(startPoint, endPoint - startPoint, expression.Substring(startPoint, endPoint - startPoint), itemName, separator, separatorStart, transformExpressions);
                    subExpressions.Add(expressionCapture);

                    continue;
                }
            }

            return subExpressions;
        }

        /// <summary>
        /// Given a subexpression, finds referenced item names and inserts them into the table
        /// as K=Name, V=String.Empty.
        /// </summary>
        /// <remarks>
        /// We can ignore any semicolons in the expression, since we're not itemizing it.
        /// </remarks>
        private static void GetReferencedItemNamesAndMetadata(string expression, int start, int end, ref ItemsAndMetadataPair pair, ShredderOptions whatToShredFor)
        {
            for (int i = start; i < end; i++)
            {
                int restartPoint;

                if (Sink(expression, ref i, end, '@', '('))
                {
                    // Start of a possible item list expression

                    // Store the index to backtrack to if this doesn't turn out to be a well
                    // formed metadata expression. (Subtract one for the increment when we loop around.)
                    restartPoint = i - 1;

                    SinkWhitespace(expression, ref i);

                    int startOfName = i;

                    if (!SinkValidName(expression, ref i, end))
                    {
                        i = restartPoint;
                        continue;
                    }

                    // '-' is a legitimate char in an item name, but we should match '->' as an arrow
                    // in '@(foo->'x')' rather than as the last char of the item name.
                    // The old regex accomplished this by being "greedy"
                    if (end > i && expression[i - 1] == '-' && expression[i] == '>')
                    {
                        i--;
                    }

                    // Grab the name, but continue to verify it's a well-formed expression
                    // before we store it.
                    string name = expression.Substring(startOfName, i - startOfName);

                    SinkWhitespace(expression, ref i);

                    bool transformOrFunctionFound = true;

                    // If there's an '->' eat it and the subsequent quoted expression or transform function
                    while (Sink(expression, ref i, end, '-', '>') && transformOrFunctionFound)
                    {
                        SinkWhitespace(expression, ref i);
                        int startTransform = i;

                        bool isQuotedTransform = SinkSingleQuotedExpression(expression, ref i, end);
                        if (isQuotedTransform)
                        {
                            continue;
                        }

                        ItemExpressionCapture functionCapture = SinkItemFunctionExpression(expression, startTransform, ref i, end);
                        if (functionCapture != null)
                        {
                            continue;
                        }

                        if (!isQuotedTransform && functionCapture == null)
                        {
                            i = restartPoint;
                            transformOrFunctionFound = false;
                        }
                    }

                    if (!transformOrFunctionFound)
                    {
                        continue;
                    }

                    SinkWhitespace(expression, ref i);

                    // If there's a ',', eat it and the subsequent quoted expression
                    if (Sink(expression, ref i, ','))
                    {
                        SinkWhitespace(expression, ref i);

                        if (!Sink(expression, ref i, '\''))
                        {
                            i = restartPoint;
                            continue;
                        }

                        int closingQuote = expression.IndexOf('\'', i);
                        if (closingQuote == -1)
                        {
                            i = restartPoint;
                            continue;
                        }

                        // Look for metadata in the separator expression
                        // e.g., @(foo, '%(bar)') contains batchable metadata 'bar'
                        GetReferencedItemNamesAndMetadata(expression, i, closingQuote, ref pair, ShredderOptions.MetadataOutsideTransforms);

                        i = closingQuote + 1;
                    }

                    SinkWhitespace(expression, ref i);

                    if (!Sink(expression, ref i, ')'))
                    {
                        i = restartPoint;
                        continue;
                    }

                    // If we've got this far, we know the item expression was
                    // well formed, so make sure the name's in the table
                    if ((whatToShredFor & ShredderOptions.ItemTypes) != 0)
                    {
                        pair.Items ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
                        pair.Items.Add(name);
                    }

                    i--;

                    continue;
                }

                if (Sink(expression, ref i, end, '%', '('))
                {
                    // Start of a possible metadata expression

                    // Store the index to backtrack to if this doesn't turn out to be a well
                    // formed metadata expression. (Subtract one for the increment when we loop around.)
                    restartPoint = i - 1;

                    SinkWhitespace(expression, ref i);

                    int startOfText = i;

                    if (!SinkValidName(expression, ref i, end))
                    {
                        i = restartPoint;
                        continue;
                    }

                    // Grab this, but we don't know if it's an item or metadata name yet
                    string firstPart = expression.Substring(startOfText, i - startOfText);
                    string itemName = null;
                    string metadataName;
                    string qualifiedMetadataName;

                    SinkWhitespace(expression, ref i);

                    bool qualified = Sink(expression, ref i, '.');

                    if (qualified)
                    {
                        SinkWhitespace(expression, ref i);

                        startOfText = i;

                        if (!SinkValidName(expression, ref i, end))
                        {
                            i = restartPoint;
                            continue;
                        }

                        itemName = firstPart;
                        metadataName = expression.Substring(startOfText, i - startOfText);
                        qualifiedMetadataName = itemName + "." + metadataName;
                    }
                    else
                    {
                        metadataName = firstPart;
                        qualifiedMetadataName = metadataName;
                    }

                    SinkWhitespace(expression, ref i);

                    if (!Sink(expression, ref i, ')'))
                    {
                        i = restartPoint;
                        continue;
                    }

                    if ((whatToShredFor & ShredderOptions.MetadataOutsideTransforms) != 0)
                    {
                        pair.Metadata ??= new Dictionary<string, MetadataReference>(MSBuildNameIgnoreCaseComparer.Default);
                        pair.Metadata[qualifiedMetadataName] = new MetadataReference(itemName, metadataName);
                    }

                    i--;
                }
            }
        }

        /// <summary>
        /// Returns true if a single quoted subexpression begins at the specified index
        /// and ends before the specified end index.
        /// Leaves index one past the end of the second quote.
        /// </summary>
        private static bool SinkSingleQuotedExpression(string expression, ref int i, int end)
        {
            if (!Sink(expression, ref i, '\''))
            {
                return false;
            }

            while (i < end && expression[i] != '\'')
            {
                i++;
            }

            i++;

            if (end <= i)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scan for the closing bracket that matches the one we've already skipped;
        /// essentially, pushes and pops on a stack of parentheses to do this.
        /// Takes the expression and the index to start at.
        /// Returns the index of the matching parenthesis, or -1 if it was not found.
        /// </summary>
        private static bool SinkArgumentsInParentheses(string expression, ref int i, int end)
        {
            int nestLevel = 0;
            int length = expression.Length;
            int restartPoint;

            unsafe
            {
                fixed (char* pchar = expression)
                {
                    if (pchar[i] == '(')
                    {
                        nestLevel++;
                        i++;
                    }
                    else
                    {
                        return false;
                    }

                    // Scan for our closing ')'
                    while (i < length && i < end && nestLevel > 0)
                    {
                        char character = pchar[i];

                        if (character == '\'' || character == '`' || character == '"')
                        {
                            restartPoint = i;
                            if (!SinkUntilClosingQuote(character, expression, ref i, end))
                            {
                                i = restartPoint;
                                return false;
                            }
                        }
                        else if (character == '(')
                        {
                            nestLevel++;
                        }
                        else if (character == ')')
                        {
                            nestLevel--;
                        }

                        i++;
                    }
                }
            }

            if (nestLevel == 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Skip all characters until we find the matching quote character
        /// </summary>
        private static bool SinkUntilClosingQuote(char quoteChar, string expression, ref int i, int end)
        {
            unsafe
            {
                fixed (char* pchar = expression)
                {
                    // We have already checked the first quote
                    i++;

                    // Scan for our closing quoteChar
                    while (i < expression.Length && i < end)
                    {
                        if (pchar[i] == quoteChar)
                        {
                            return true;
                        }

                        i++;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if a item function subexpression begins at the specified index
        /// and ends before the specified end index.
        /// Leaves index one past the end of the closing paren.
        /// </summary>
        private static ItemExpressionCapture SinkItemFunctionExpression(string expression, int startTransform, ref int i, int end)
        {
            if (SinkValidName(expression, ref i, end))
            {
                int endFunctionName = i;

                // Eat any whitespace between the function name and its arguments
                SinkWhitespace(expression, ref i);
                int startFunctionArguments = i + 1;

                if (SinkArgumentsInParentheses(expression, ref i, end))
                {
                    int endFunctionArguments = i - 1;

                    ItemExpressionCapture capture = new ItemExpressionCapture(startTransform, i - startTransform, expression.Substring(startTransform, i - startTransform));
                    capture.FunctionName = expression.Substring(startTransform, endFunctionName - startTransform);

                    if (endFunctionArguments > startFunctionArguments)
                    {
                        capture.FunctionArguments = expression.Substring(startFunctionArguments, endFunctionArguments - startFunctionArguments);
                    }

                    return capture;
                }

                return null;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Returns true if a valid name begins at the specified index.
        /// Leaves index one past the end of the name.
        /// </summary>
        private static bool SinkValidName(string expression, ref int i, int end)
        {
            if (end <= i || !XmlUtilities.IsValidInitialElementNameCharacter(expression[i]))
            {
                return false;
            }

            i++;

            while (end > i && XmlUtilities.IsValidSubsequentElementNameCharacter(expression[i]))
            {
                i++;
            }

            return true;
        }

        /// <summary>
        /// Returns true if the character at the specified index 
        /// is the specified char. 
        /// Leaves index one past the character.
        /// </summary>
        private static bool Sink(string expression, ref int i, char c)
        {
            if (i < expression.Length && expression[i] == c)
            {
                i++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the next two characters at the specified index
        /// are the specified sequence.
        /// Leaves index one past the second character.
        /// </summary>
        private static bool Sink(string expression, ref int i, int end, char c1, char c2)
        {
            if (i < end - 1 && expression[i] == c1 && expression[i + 1] == c2)
            {
                i += 2;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Moves past all whitespace starting at the specified index.
        /// Returns the next index, possibly the string length.
        /// </summary>
        /// <remarks>
        /// Char.IsWhitespace() is not identical in behavior to regex's \s character class,
        /// but it's extremely close, and it's what we use in conditional expressions.
        /// </remarks>
        /// <param name="expression">The expression to process.</param>
        /// <param name="i">The start location for skipping whitespace, contains the next non-whitespace character on exit.</param>
        private static void SinkWhitespace(string expression, ref int i)
        {
            while (i < expression.Length && Char.IsWhiteSpace(expression[i]))
            {
                i++;
            }
        }

        /// <summary>
        /// Represents one substring for a single successful capture.
        /// </summary>
        internal class ItemExpressionCapture
        {
            /// <summary>
            /// Captures within this capture
            /// </summary>
            private readonly List<ItemExpressionCapture> _captures;

            /// <summary>
            /// The position in the original string where the first character of the captured
            /// substring was found.
            /// </summary>
            private readonly int _index;

            /// <summary>
            /// The length of the captured substring.
            /// </summary>
            private readonly int _length;

            /// <summary>
            /// The captured substring from the input string.
            /// </summary>
            private readonly string _value;

            /// <summary>
            /// The type of the item within this expression
            /// </summary>
            private readonly string _itemType;

            /// <summary>
            /// The separator, if any, within this expression
            /// </summary>
            private readonly string _separator;

            /// <summary>
            /// The starting character of the separator within the expression
            /// </summary>
            private readonly int _separatorStart;

            /// <summary>
            /// The function name, if any, within this expression
            /// </summary>
            private string _functionName;

            /// <summary>
            /// The function arguments, if any, within this expression
            /// </summary>
            private string _functionArguments;

            /// <summary>
            /// Create an Expression Capture instance
            /// Represents a sub expression, shredded from a larger expression
            /// </summary>
            public ItemExpressionCapture(int index, int length, string subExpression) : this(index, length, subExpression, null, null, -1, null)
            {
            }

            /// <summary>
            /// Create an Expression Capture instance
            /// Represents a sub expression, shredded from a larger expression
            /// </summary>
            public ItemExpressionCapture(int index, int length, string subExpression, string itemType, string separator, int separatorStart, List<ItemExpressionCapture> captures)
            {
                _index = index;
                _length = length;
                _value = subExpression;
                _itemType = itemType;
                _separator = separator;
                _separatorStart = separatorStart;
                _captures = captures;
            }

            /// <summary>
            /// Captures within this capture
            /// </summary>
            public List<ItemExpressionCapture> Captures
            {
                get { return _captures; }
            }

            /// <summary>
            /// The position in the original string where the first character of the captured
            /// substring was found.
            /// </summary>
            public int Index
            {
                get { return _index; }
            }

            /// <summary>
            /// The length of the captured substring.
            /// </summary>
            public int Length
            {
                get { return _length; }
            }

            /// <summary>
            /// Gets the captured substring from the input string.
            /// </summary>
            public string Value
            {
                get { return _value; }
            }

            /// <summary>
            /// Gets the captured itemtype.
            /// </summary>
            public string ItemType
            {
                get { return _itemType; }
            }

            /// <summary>
            /// Gets the captured itemtype.
            /// </summary>
            public string Separator
            {
                get { return _separator; }
            }

            /// <summary>
            /// The starting character of the separator.
            /// </summary>
            public int SeparatorStart
            {
                get { return _separatorStart; }
            }

            /// <summary>
            /// The function name, if any, within this expression
            /// </summary>
            public string FunctionName
            {
                get { return _functionName; }
                set { _functionName = value; }
            }

            /// <summary>
            /// The function arguments, if any, within this expression
            /// </summary>
            public string FunctionArguments
            {
                get { return _functionArguments; }
                set { _functionArguments = value; }
            }

            /// <summary>
            /// Gets the captured substring from the input string.
            /// </summary>
            public override string ToString()
            {
                return _value;
            }
        }
    }
}
