// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Microsoft.Build.Framework
{

    #nullable enable
    
    /// <summary>
    /// The value of an item and any associated metadata to be added by an SDK resolver.  See <see cref="SdkResult.ItemsToAdd"/>
    /// </summary>
    public class SdkResultItem
    {
        public string ItemSpec { get; set; }
        public Dictionary<string, string> Metadata { get;}

        public SdkResultItem()
        {
            ItemSpec = "";
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates an <see cref="SdkResultItem"/>
        /// </summary>
        /// <param name="itemSpec">The value (itemspec) for the item</param>
        /// <param name="metadata">A dictionary of item metadata.  This should be created with <see cref="StringComparer.OrdinalIgnoreCase"/> for the comparer.</param>
        public SdkResultItem(string itemSpec, Dictionary<string, string> metadata)
        {
            ItemSpec = itemSpec;
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            if (obj is SdkResultItem item &&
                   ItemSpec == item.ItemSpec &&
                   Metadata?.Count == item.Metadata.Count)
            {
                return Metadata.All(m => item.Metadata.TryGetValue(m.Key, out var itemValue) && itemValue == m.Value);
            }
            return false;
        }

        public override int GetHashCode()
        {
            int hashCode = -849885975;
            hashCode = hashCode ^ ItemSpec.GetHashCode();

            if (Metadata != null)
            {
                foreach (var kvp in Metadata)
                {
                    hashCode ^= $"{kvp.Key.GetHashCode()}: {(kvp.Value ?? "V").GetHashCode()}".GetHashCode();
                }
            }

            return hashCode;
        }
    }
}
