// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

using Microsoft.Build.Shared;
using Microsoft.Build.Execution;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Collections;
using Microsoft.Build.Shared.FileSystem;

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// This class represents a collection of items that are homogeneous w.r.t.
    /// a certain set of metadata.
    /// </summary>
    internal sealed class ItemBucket : IComparable
    {
        #region Member data

        /// <summary>
        /// This single object contains all of the data necessary to perform expansion of metadata, properties,
        /// and items.
        /// </summary>
        private Expander<ProjectPropertyInstance, ProjectItemInstance> _expander;

        /// <summary>
        /// Metadata in this bucket
        /// </summary>
        private Dictionary<string, string> _metadata;

        /// <summary>
        /// The items for this bucket.
        /// </summary>
        private Lookup _lookup;

        /// <summary>
        /// When buckets are being created for batching purposes, this indicates which order the 
        /// buckets were created in, so that the target/task being batched gets called with the items
        /// in the same order as they were declared in the project file.  For example, the first
        /// bucket created gets bucketSequenceNumber=0, the second bucket created gets 
        /// bucketSequenceNumber=1, etc.
        /// </summary>
        private int _bucketSequenceNumber;

        /// <summary>
        /// The entry we enter when we create the bucket.
        /// </summary>
        private Lookup.Scope _lookupEntry;

        #endregion

        #region Constructors

        /// <summary>
        /// Private default constructor disallows parameterless instantiation.
        /// </summary>
        private ItemBucket()
        {
            // do nothing
        }

        /// <summary>
        /// Creates an instance of this class using the given bucket data.
        /// </summary>
        /// <param name="itemNames">Item types being batched on: null indicates no batching is occurring</param>
        /// <param name="metadata">Hashtable of item metadata values: null indicates no batching is occurring</param>
        /// <param name="lookup">The <see cref="Lookup"/> to use for the items in the bucket.</param>
        /// <param name="bucketSequenceNumber">A sequence number indication what order the buckets were created in.</param>
        internal ItemBucket
        (
            ICollection<string> itemNames,
            Dictionary<string, string> metadata,
            Lookup lookup,
            int bucketSequenceNumber
        )
        {
            ErrorUtilities.VerifyThrow(lookup != null, "Need lookup.");

            // Create our own lookup just for this bucket
            _lookup = lookup.Clone();

            // Push down the items, so that item changes in this batch are not visible to parallel batches
            _lookupEntry = _lookup.EnterScope("ItemBucket()");

            // Add empty item groups for each of the item names, so that (unless items are added to this bucket) there are
            // no item types visible in this bucket among the item types being batched on
            if (itemNames != null)
            {
                foreach (string name in itemNames)
                {
                    _lookup.PopulateWithItems(name, new List<ProjectItemInstance>());
                }
            }

            _metadata = metadata;
            _expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(_lookup, _lookup, new StringMetadataTable(metadata));

            _bucketSequenceNumber = bucketSequenceNumber;
        }

        #endregion

        #region Comparison methods

        /// <summary>
        /// Compares this item bucket against the given one. The comparison is
        /// solely based on the values of the item metadata in the buckets.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>
        /// -1, if this bucket is "less than" the second one
        ///  0, if this bucket is equivalent to the second one
        /// +1, if this bucket is "greater than" the second one
        /// </returns>
        public int CompareTo(object obj)
        {
            return HashTableUtility.Compare(_metadata, ((ItemBucket)obj)._metadata);
        }

        /// <summary>
        /// Constructs a token bucket object that can be compared against other
        /// buckets. This dummy bucket is a patently invalid bucket, and cannot
        /// be used for any other operations besides comparison.
        /// </summary>
        /// <remarks>
        /// PERF NOTE: A dummy bucket is intentionally very light-weight, and it
        /// allocates a minimum of memory compared to a real bucket.
        /// </remarks>
        /// <returns>An item bucket that is invalid for everything except comparisons.</returns>
        internal static ItemBucket GetDummyBucketForComparisons(Dictionary<string, string> metadata)
        {
            ItemBucket bucket = new ItemBucket();
            bucket._metadata = metadata;

            return bucket;
        }

        #endregion

        #region Properties
        /// <summary>
        /// Returns the object that knows how to handle all kinds of expansion for this bucket.
        /// </summary>
        internal Expander<ProjectPropertyInstance, ProjectItemInstance> Expander
        {
            get
            {
                return _expander;
            }
        }


        /// <summary>
        /// When buckets are being created for batching purposes, this indicates which order the 
        /// buckets were created in, so that the target/task being batched gets called with the items
        /// in the same order as they were declared in the project file.  For example, the first
        /// bucket created gets bucketSequenceNumber=0, the second bucket created gets 
        /// bucketSequenceNumber=1, etc.
        /// </summary>
        internal int BucketSequenceNumber
        {
            get
            {
                return _bucketSequenceNumber;
            }
        }

        /// <summary>
        /// The items for this bucket.
        /// </summary>
        internal Lookup Lookup
        {
            get
            {
                return _lookup;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Adds a new item to this bucket.
        /// </summary>
        internal void AddItem(ProjectItemInstance item)
        {
            _lookup.PopulateWithItem(item);
        }

        /// <summary>
        /// Leaves the lookup scope created for this bucket.
        /// </summary>
        internal void LeaveScope()
        {
            _lookupEntry.LeaveScope();
        }

        #endregion
    }
}
