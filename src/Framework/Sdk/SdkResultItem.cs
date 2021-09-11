// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// The value of an item and any associated metadata to be added by an SDK resolver.  See <see cref="SdkResult.ItemsToAdd"/>
    /// </summary>
    public class SdkResultItem
    {
        public string ItemSpec { get; set; }
        public Dictionary<string, string> Metadata { get;}

        public SdkResultItem()
        {
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
            Metadata = metadata;
        }

        public override bool Equals(object obj)
        {
            if (obj is SdkResultItem item &&
                   ItemSpec == item.ItemSpec &&
                   Metadata.Count == item.Metadata?.Count)
            {
                foreach (var kvp in Metadata)
                {
                    var itemValue = "";
                    if (!item.Metadata.TryGetValue(kvp.Key, out itemValue))
                    {
                        return false;
                    }
                    else
                    {
                        if (kvp.Value != itemValue)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            int hashCode = -849885975;
            hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(ItemSpec);

            if (Metadata != null)
            {
                foreach (var kvp in Metadata)
                {
                    hashCode = (hashCode * -1521134295) + kvp.Key.GetHashCode();
                    hashCode = (hashCode * -1521134295) + kvp.Value.GetHashCode();
                }
            }

            return hashCode;
        }
    }
}
