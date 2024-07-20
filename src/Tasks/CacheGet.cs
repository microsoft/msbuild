// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Tasks
{
    public class CacheGet : TaskExtension
    {
        [Required]
        public string Key { get; set; }

        [Output]
        public string Value { get; set; }

        public override bool Execute()
        {
            Value = (string)BuildEngine4.GetRegisteredTaskObject(Key, RegisteredTaskObjectLifetime.Build) ?? "";
            return true;
        }
    }
}
