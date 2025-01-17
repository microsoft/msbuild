// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Build.Framework.Telemetry
{
    /// <summary>
    /// Extension methods for <see cref="Activity"/>. usage in VS OpenTelemetry.
    /// </summary>
    internal static class ActivityExtensions
    {
        /// <summary>
        /// Add tags to the activity from a <see cref="IActivityTelemetryDataHolder"/>.
        /// </summary>
        public static Activity WithTags(this Activity activity, IActivityTelemetryDataHolder dataHolder)
        {
            activity.WithTags(dataHolder.GetActivityProperties());
            return activity;
        }

        /// <summary>
        /// Add tags to the activity from a list of TelemetryItems.
        /// </summary>
        public static Activity WithTags(this Activity activity, IList<TelemetryItem> tags)
        {
            foreach (var tag in tags)
            {
                activity.WithTag(tag);
            }
            return activity;
        }
        /// <summary>
        /// Add a tag to the activity from a <see cref="TelemetryItem"/>.
        /// </summary>
        public static Activity WithTag(this Activity activity, TelemetryItem item)
        {
            object value = item.Hashed ? GetHashed(item.Value) : item.Value;
            activity.SetTag($"{TelemetryConstants.PropertyPrefix}{item.Name}", value);
            return activity;
        }

        /// <summary>
        /// Set the start time of the activity.
        /// </summary>
        public static Activity WithStartTime(this Activity activity, DateTime? startTime)
        {
            if (startTime.HasValue)
            {
                activity.SetStartTime(startTime.Value);
            }
            return activity;
        }

        /// <summary>
        /// Depending on the platform, hash the value using an available mechanism.
        /// </summary>
        private static object GetHashed(object value)
        {
            return Sha256Hasher.Hash(value.ToString() ?? "");
        }

        // https://github.com/dotnet/sdk/blob/8bd19a2390a6bba4aa80d1ac3b6c5385527cc311/src/Cli/Microsoft.DotNet.Cli.Utils/Sha256Hasher.cs + workaround for netstandard2.0
        private static class Sha256Hasher
        {
            /// <summary>
            /// The hashed mac address needs to be the same hashed value as produced by the other distinct sources given the same input. (e.g. VsCode)
            /// </summary>
            public static string Hash(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
#if NET
                byte[] hash = SHA256.HashData(bytes);
#if NET9_0_OR_GREATER
                return Convert.ToHexStringLower(hash);
#else
            return Convert.ToHexString(hash).ToLowerInvariant();
#endif

#else
            // Create the SHA256 object and compute the hash
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(bytes);

                // Convert the hash bytes to a lowercase hex string (manual loop approach)
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.AppendFormat("{0:x2}", b);
                }

                return sb.ToString();
            }
#endif
            }

            public static string HashWithNormalizedCasing(string text)
            {
                return Hash(text.ToUpperInvariant());
            }
        }
    }
}
