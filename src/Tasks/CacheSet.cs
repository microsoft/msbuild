// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Tasks
{
    public class CacheSet : TaskExtension
    {
        [Required]
        public string Key { get; set; }

        [Required]
        public string Value { get; set; }

        public override bool Execute()
        {
            BuildEngine4.RegisterTaskObject(Key, Value, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: true);
            return true;
        }
    }
}
