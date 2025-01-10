﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#if NETFRAMEWORK
extern alias clientext;   // for Microsoft.VisualStudio.OpenTelemetry.ClientExtensions

using clientext::Microsoft.VisualStudio.OpenTelemetry.ClientExtensions;
using clientext::Microsoft.VisualStudio.OpenTelemetry.ClientExtensions.Exporters;
#else
using System.Security.Cryptography;
using System.Text;
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;



namespace Microsoft.Build.Framework.Telemetry
{

    internal static class ActivityExtensions
    {
        public static Activity WithTags(this Activity activity, IActivityTelemetryDataHolder dataHolder)
        {
            activity.WithTags(dataHolder.GetActivityProperties());
            return activity;
        }

        public static Activity WithTags(this Activity activity, IList<TelemetryItem> tags)
        {
            foreach (var tag in tags)
            {
                activity.WithTag(tag);
            }
            return activity;
        }

        public static Activity WithTag(this Activity activity, TelemetryItem item)
        {
            object value = item.Hashed ? GetHashed(item.Value) : item.Value;
            activity.SetTag($"{TelemetryConstants.PropertyPrefix}{item.Name}", value);
            return activity;
        }

        public static Activity WithStartTime(this Activity activity, DateTime? startTime)
        {
            if (startTime.HasValue)
            {
                activity.SetStartTime(startTime.Value);
            }
            return activity;
        }

        private static object GetHashed(object value)
        {
#if NETFRAMEWORK
                        return new clientext::Microsoft.VisualStudio.Telemetry.TelemetryHashedProperty(value);
#else
            return Sha256Hasher.Hash(value.ToString() ?? "");
#endif
        }


        // https://github.com/dotnet/sdk/blob/8bd19a2390a6bba4aa80d1ac3b6c5385527cc311/src/Cli/Microsoft.DotNet.Cli.Utils/Sha256Hasher.cs + workaround for netstandard2.0
#if NET || NETSTANDARD2_0_OR_GREATER
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
#endif
    }
}
