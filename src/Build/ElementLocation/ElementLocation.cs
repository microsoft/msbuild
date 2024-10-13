// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Construction
{
    /// <summary>
    /// The location of an XML node in a file.
    /// Any editing of the project XML through the MSBuild API's will invalidate locations in that XML until the XML is reloaded.
    /// </summary>
    /// <remarks>
    /// This object is IMMUTABLE, so that it can be passed around arbitrarily.
    /// DO NOT make these objects any larger. There are huge numbers of them and they are transmitted between nodes.
    /// 
    /// Although this class is called "element" location, it is also used for other XML node types, such as in <see cref="XmlAttributeWithLocation"/>.
    /// </remarks>
    [Serializable]
    public abstract class ElementLocation : IElementLocation, ITranslatable, IImmutable
    {
        /// <summary>
        /// Gets the empty element location.
        /// This is not to be used when something is "missing": that should have a null location.
        /// It is to be used for the project location when the project has not been given a name.
        /// In that case, it exists, but can't have a specific location.
        /// </summary>
        public static ElementLocation EmptyLocation { get; } = new EmptyElementLocation();

        /// <summary>
        /// Gets the file from which this particular element originated.  It may
        /// differ from the ProjectFile if, for instance, it was part of
        /// an import or originated in a targets file.
        /// If not known, returns empty string.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public abstract string File { get; }

        /// <summary>
        /// Gets the line number where this element exists in its file.
        /// The first line is numbered 1.
        /// Zero indicates "unknown location".
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public abstract int Line { get; }

        /// <summary>
        /// Gets the column number where this element exists in its file.
        /// The first column is numbered 1.
        /// Zero indicates "unknown location".
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public abstract int Column { get; }

        /// <summary>
        /// Gets the location in a form suitable for replacement
        /// into a message.
        /// Example: "c:\foo\bar.csproj (12,34)"
        /// Calling this creates and formats a new string.
        /// PREFER TO PUT THE LOCATION INFORMATION AT THE START OF THE MESSAGE INSTEAD.
        /// Only in rare cases should the location go within the message itself.
        /// </summary>
        public string LocationString
        {
            get
            {
                int line = Line;
                int column = Column;
                return (line, column) switch
                {
                    (not 0, not 0) => ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("FileLocation", File, line, column),
                    (not 0, 0) => $"{File} ({line})",
                    _ => File,
                };
            }
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // We don't include the file path in the hash
            return (Line * 397) ^ Column;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            if (obj is not IElementLocation that)
            {
                return false;
            }

            if (this.Line != that.Line || this.Column != that.Column)
            {
                return false;
            }

            if (!String.Equals(this.File, that.File, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return LocationString;
        }

        /// <inheritdoc />
        void ITranslatable.Translate(ITranslator translator)
        {
            ErrorUtilities.VerifyThrow(translator.Mode == TranslationDirection.WriteToStream, "write only");

            // Translate int, even if ushort is being used.
            // Internally, the translator uses a variable length (prefix) encoding.
            string file = File;
            int line = Line;
            int column = Column;
            translator.Translate(ref file);
            translator.Translate(ref line);
            translator.Translate(ref column);
        }

        /// <summary>
        /// Factory for serialization.
        /// Custom factory is needed because this class is abstract and uses a factory pattern.
        /// </summary>
        internal static ElementLocation FactoryForDeserialization(ITranslator translator)
        {
            string? file = null;
            int line = 0;
            int column = 0;
            translator.Translate(ref file);
            translator.Translate(ref line);
            translator.Translate(ref column);

            return Create(file, line, column);
        }

        /// <summary>
        /// Constructor for when we only know the file and nothing else.
        /// This is the case when we are creating a new item, for example, and it has
        /// not been evaluated from some XML.
        /// </summary>
        internal static ElementLocation Create(string? file)
        {
            return Create(file, 0, 0);
        }

        private static string[] s_fileByIndex = new string[32];
        private static int s_nextFileIndex;
        private static ImmutableDictionary<string, int> s_indexByFile = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        internal static void DangerousInternalResetFileIndex()
        {
            s_nextFileIndex = 0;
            s_fileByIndex = new string[32];
            s_indexByFile = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Constructor for the case where we have most or all information.
        /// Numerical values must be 1-based, non-negative; 0 indicates unknown
        /// File may be null, indicating the file was not loaded from disk.
        /// </summary>
        /// <remarks>
        /// In AG there are 600 locations that have a file but zero line and column.
        /// In theory yet another derived class could be made for these to save 4 bytes each.
        /// </remarks>
        internal static ElementLocation Create(string? filePath, int line, int column)
        {
            // Combine line and column values with bitwise OR so we can perform various
            // checks on both values in a single comparison, reducing the amount of branching
            // in the code.
            int combinedValue = line | column;

            if (string.IsNullOrEmpty(filePath) && combinedValue == 0)
            {
                // When combinedValue is zero, it implies that both line and column are zero.
                return EmptyElementLocation.Instance;
            }

            // When combinedValue is negative, it implies that either line or column were negative.
            ErrorUtilities.VerifyThrow(combinedValue > -1, "Use zero for unknown");

            // TODO store the last run's value and check if this is for the same file. If so, skip the dictionary lookup (tree walk).
            int fileIndex = GetOrAddFileIndex(filePath);

#if DEBUG
            string lookedUpFilePath = LookupFileByIndex(fileIndex);
            if (!StringComparer.OrdinalIgnoreCase.Equals(filePath ?? "", lookedUpFilePath))
            {
                Debug.Fail($"File index {fileIndex} returned for path '{filePath}', but lookup for that index returns '{lookedUpFilePath}'.");
            }
#endif

            // We use multiple packing schemes for this data. TypeSize below excludes the CLR's per-object overhead.
            //
            // Name                         TypeSize  FileIndex      Line           Column
            //
            // EmptyElementLocation         0         0 (max 0)       0 (max 0)       0 (max 0)
            //
            // SmallFileElementLocation     8         4 (max 65,535)  2 (max 256)     2 (max 256)
            // SmallLineElementLocation     8         2 (max 256)     4 (max 65,535)  2 (max 256)
            // SmallColumnElementLocation   8         2 (max 256)     2 (max 256)     4 (max 65,535)
            //
            // LargeFileElementLocation     16        8 (max 2b)      4 (max 65,535)  4 (max 65,535)
            // LargeLineElementLocation     16        4 (max 65,535)  8 (max 2b)      4 (max 65,535)
            // LargeColumnElementLocation   16        4 (max 65,535)  4 (max 65,535)  8 (max 2b)
            //
            // FullElementLocation          24        8 (max 2b)      8 (max 2b)      8 (max 2b)

            // Check for empty first
            if (fileIndex is 0 && line is 0 && column is 0)
            {
                return EmptyElementLocation.Instance;
            }

            combinedValue |= fileIndex;

            // We want class sizes as multiples of 8 on 64-bit architectures, for alignment reasons.
            // Any space used between these multiples leads to unused bytes in padding. We want to
            // take advantage of these sizes effectively, so we check for various requirements and
            // choose.
            //
            // Search in popularity order to reduce the number of branches taken through this code.
            //
            // When combinedValue is less than a threshold, it implies that both line and column are less
            // than that threshold.

            // Handle cases that fit in 0xFF and 0XFFFF.
            if (combinedValue <= byte.MaxValue)
            {
                // All values fit within a byte. Pick one of the 8-byte types.
                return new SmallFileElementLocation((ushort)fileIndex, (byte)line, (byte)column);
            }
            else if (combinedValue <= ushort.MaxValue)
            {
                // At least one value needs ushort. Try to use an 8-byte type all the same.
                if (line <= byte.MaxValue && column <= byte.MaxValue)
                {
                    // Only fileIndex needs 4 bytes
                    return new SmallFileElementLocation((ushort)fileIndex, (byte)line, (byte)column);
                }
                else if (fileIndex <= byte.MaxValue && column <= byte.MaxValue)
                {
                    // Only line needs 4 bytes
                    return new SmallLineElementLocation((byte)fileIndex, (ushort)line, (byte)column);
                }
                else if (fileIndex <= byte.MaxValue && line <= byte.MaxValue)
                {
                    // Only column needs 4 bytes
                    return new SmallColumnElementLocation((byte)fileIndex, (byte)line, (ushort)column);
                }
                else
                {
                    // All three values need ushort. Choose an implementation that gives the file
                    // index an easily-read value (i.e. within 4 bytes) to simplify reads. The
                    // assumption is that if you need line, you probably also need column.
                    return new LargeFileElementLocation(fileIndex, (ushort)line, (ushort)column);
                }
            }
            else
            {
                // At least one value needs int.
                if (fileIndex <= short.MaxValue && column <= short.MaxValue)
                {
                    // Only line needs 8 bytes
                    return new LargeLineElementLocation((ushort)fileIndex, line, (ushort)column);
                }
                else if (line <= short.MaxValue && column <= short.MaxValue)
                {
                    // Only fileIndex needs 8 bytes
                    return new LargeFileElementLocation(fileIndex, (ushort)line, (ushort)column);
                }
                else if (fileIndex <= short.MaxValue && line <= short.MaxValue)
                {
                    // Only column needs 8 bytes
                    return new LargeColumnElementLocation((ushort)fileIndex, (ushort)line, column);
                }
                else
                {
                    return new FullElementLocation(fileIndex, line, column);
                }
            }

            static int GetOrAddFileIndex(string? file)
            {
                if (file is null)
                {
                    return 0;
                }

                if (s_indexByFile.TryGetValue(file, out int index))
                {
                    return index + 1;
                }

                return AddFile();

                int AddFile()
                {
                    int index = Interlocked.Increment(ref s_nextFileIndex) - 1;

                    SetValue(index);

                    _ = ImmutableInterlocked.TryAdd(ref s_indexByFile, file, index);

                    return index + 1;
                }

                void SetValue(int index)
                {
                    while (true)
                    {
                        string[] array = Volatile.Read(ref s_fileByIndex);

                        if (index < array.Length)
                        {
                            array[index] = file;
                            return;
                        }

                        // Need to grow the array

                        // Wait for the last value to be non-null, so that we have all values to copy
                        while (array[array.Length - 1] is null)
                        {
                            Thread.SpinWait(100);
                        }

                        int newArrayLength = array.Length * 2;

                        while (index >= newArrayLength)
                        {
                            newArrayLength *= 2;
                        }

                        string[] newArray = new string[newArrayLength];
                        array.AsSpan().CopyTo(newArray);
                        newArray[index] = file;

                        string[] exchanged = Interlocked.CompareExchange(ref s_fileByIndex, newArray, array);

                        if (ReferenceEquals(exchanged, array))
                        {
                            // We replaced it
                            return;
                        }

                        // Otherwise, loop around again. We can't just return exchanged here,
                        // as theoretically the array might have been grown more than once.
                    }
                }
            }
        }

        internal static string LookupFileByIndex(int index)
        {
            if (index is 0)
            {
                return "";
            }

            index -= 1;

            Thread.MemoryBarrier();

            string[] array = Volatile.Read(ref s_fileByIndex);

            while (index >= array.Length)
            {
                // Data race! Spin.
                array = Volatile.Read(ref s_fileByIndex);
            }

            return array[index];
        }

        #region Element implementations

#pragma warning disable SA1516 // Elements should be separated by blank line

        private sealed class EmptyElementLocation() : ElementLocation
        {
            /// <summary>
            /// Gets the singleton, immutable empty element location.
            /// </summary>
            /// <remarks>
            /// Not to be be used when something is "missing". Use a <see langword="null"/> location for that.
            /// Use only for the project location when the project has not been given a name.
            /// In that case, it exists, but can't have a specific location.
            /// </remarks>
            public static EmptyElementLocation Instance { get; } = new();

            public override string File => "";
            public override int Line => 0;
            public override int Column => 0;
        }

        private sealed class SmallFileElementLocation(ushort file, byte line, byte column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }
        
        private sealed class SmallLineElementLocation(byte file, ushort line, byte column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }
        
        private sealed class SmallColumnElementLocation(byte file, byte line, ushort column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }

        private sealed class LargeFileElementLocation(int file, ushort line, ushort column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }

        private sealed class LargeLineElementLocation(ushort file, int line, ushort column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }

        private sealed class LargeColumnElementLocation(ushort file, ushort line, int column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }

        private sealed class FullElementLocation(int file, int line, int column) : ElementLocation
        {
            public override string File => LookupFileByIndex(file);
            public override int Line => line;
            public override int Column => column;
        }

#pragma warning restore SA1516 // Elements should be separated by blank line

        #endregion
    }
}
