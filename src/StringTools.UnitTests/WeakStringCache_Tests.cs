// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET35_UNITTEST
extern alias StringToolsNet35;
#endif

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Shouldly;
using Xunit;

#if NET35_UNITTEST
using StringToolsNet35::Microsoft.NET.StringTools;
#endif

#nullable disable

namespace Microsoft.NET.StringTools.Tests
{
    public class WeakStringCache_Tests : IDisposable
    {
        /// <summary>
        /// The weak string cache under test.
        /// </summary>
        private WeakStringCache _cache = new WeakStringCache();

        public void Dispose()
        {
            _cache.Dispose();
        }

        /// <summary>
        /// Adds a string to the cache under test.
        /// </summary>
        /// <param name="strPart1">Part one of the string (split to prevent runtime interning and unintended GC roots).</param>
        /// <param name="strPart2">Part two of the string (split to prevent runtime interning and unintended GC roots).</param>
        /// <param name="callbackToRunWithTheStringAlive">Callback to be invoked after the string has been added but before the strong GC ref is released.</param>
        /// <returns>The hash code of the string as calculated by WeakStringCache.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int AddString(string strPart1, string strPart2, Action<string> callbackToRunWithTheStringAlive)
        {
            // Compose the string with SB so it doesn't get interned by the runtime.
            string testString = new StringBuilder(strPart1).Append(strPart2).ToString();
            InternableString testStringTarget = new InternableString(testString);

            int hashCode = testStringTarget.GetHashCode();

            string cachedString = _cache.GetOrCreateEntry(ref testStringTarget, out bool cacheHit);
            cacheHit.ShouldBeFalse();
            cachedString.ShouldBeSameAs(testString);

            callbackToRunWithTheStringAlive(cachedString);

            // Verify that the string is really in the cache and the cache returns the interned instance.
            string testStringCopy = new StringBuilder(strPart1).Append(strPart2).ToString();
            InternableString testStringCopyTarget = new InternableString(testStringCopy);
            cachedString = _cache.GetOrCreateEntry(ref testStringCopyTarget, out cacheHit);
            cacheHit.ShouldBeTrue();
            cachedString.ShouldBeSameAs(testString);

            // Trigger full GC and verify that nothing has changed since we're still keeping testString alive.
            GC.Collect();

            callbackToRunWithTheStringAlive(cachedString);

            testStringCopyTarget = new InternableString(testStringCopy);
            cachedString = _cache.GetOrCreateEntry(ref testStringCopyTarget, out cacheHit);
            cacheHit.ShouldBeTrue();
            cachedString.ShouldBeSameAs(testString);

            return hashCode;
        }

        /// <summary>
        /// Adds strings that are known to have a hash code collision to the cache under test.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AddStringsWithSameHashCode(int numberOfStrings)
        {
            string[] cachedStrings = new string[numberOfStrings];
            int[] hashCodes = new int[numberOfStrings];

            for (int i = 0; i < numberOfStrings; i++)
            {
                string strPart2 = string.Concat(Enumerable.Repeat("100570862200", i + 100));
                hashCodes[i] = AddString(string.Empty, strPart2, (string cachedString) =>
                {
                    _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
                    {
                        LiveStringCount = 1,
                        CollectedStringCount = 0,
                    });
                    cachedStrings[i] = cachedString;
                });

                if (i > 0)
                {
                    // The strings have been carefully constructed to have the same hash code.
                    hashCodes[i].ShouldBe(hashCodes[i - 1]);
                }
            }

            // There are no cache hits when iterating over our strings again because the last one always wins and steals the slot.
            for (int i = 0; i < numberOfStrings; i++)
            {
                InternableString stringCopy = new InternableString(new string(cachedStrings[i].ToCharArray()));
                string cachedStringFromCache = _cache.GetOrCreateEntry(ref stringCopy, out bool cacheHit);
                cacheHit.ShouldBeFalse();
                cachedStringFromCache.ShouldNotBeSameAs(cachedStrings[i]);
            }
        }

        /// <summary>
        /// Simple test case to verify that:
        /// 1. A string added to the cache stays in the cache as long as it's alive.
        /// 2. The string is no longer retrievable after all strong GC refs are gone.
        /// 3. The cache completely removes the handle after calling Scavenge on it.
        /// </summary>
        [Fact]
        public void RetainsStringUntilCollected()
        {
            // Add a string to the cache using a non-inlinable method to make sure it's not reachable from a GC root.
            AddString(new string('r', 500), "test", (string cachedString) =>
            {
                _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
                {
                    LiveStringCount = 1,
                    CollectedStringCount = 0,
                });
            });

            // Trigger full GC.
            GC.Collect();

            // The handle is still in the cache but it's unused now as the string has been collected.
            _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
            {
                LiveStringCount = 0,
                CollectedStringCount = 1,
            });

            // Ask the cache to get rid of unused handles.
            _cache.Scavenge();

            // The cache should be empty now.
            _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
            {
                LiveStringCount = 0,
                CollectedStringCount = 0,
            });
        }

        /// <summary>
        /// Same as RetainsStringUntilCollected but with multiple strings sharing the same hash code.
        /// </summary>
        [Fact]
        public void RetainsLastStringWithGivenHashCode()
        {
            // Add 3 strings with the same hash code.
            AddStringsWithSameHashCode(3);

            // Trigger full GC.
            GC.Collect();

            // The handle is still in the cache but it's unused now as the strings have been collected.
            _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
            {
                LiveStringCount = 0,
                CollectedStringCount = 1,
            });

            // Ask the cache to get rid of unused handles.
            _cache.Scavenge();

            // The cache should be empty now.
            _cache.GetDebugInfo().ShouldBe(new WeakStringCache.DebugInfo()
            {
                LiveStringCount = 0,
                CollectedStringCount = 0,
            });
        }
    }
}
