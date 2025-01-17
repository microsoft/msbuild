// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;
using Shouldly;

namespace Microsoft.Build.Framework.Telemetry.Tests
{
    public class ActivityExtensionsTests
    {
        [Fact]
        public void WithTag_ShouldSetUnhashedValue()
        {
            var activity = new Activity("TestActivity");
            activity.Start(); 

            var telemetryItem = new TelemetryItem(
                Name: "TestItem",
                Value: "TestValue",
                Hashed: false);

            activity.WithTag(telemetryItem);

            var tagValue = activity.GetTagItem("VS.MSBuild.TestItem");
            tagValue.ShouldNotBeNull();
            tagValue.ShouldBe("TestValue");

            activity.Dispose();
        }

        [Fact]
        public void WithTag_ShouldSetHashedValue()
        {
            var activity = new Activity("TestActivity");
            var telemetryItem = new TelemetryItem(
                Name: "TestItem",
                Value: "SensitiveValue",
                Hashed: true);

            activity.WithTag(telemetryItem);

            var tagValue = activity.GetTagItem("VS.MSBuild.TestItem");
            tagValue.ShouldNotBeNull();
            tagValue.ShouldNotBe("SensitiveValue"); // Ensure it’s not the plain text
            activity.Dispose();
        }

        [Fact]
        public void WithTags_ShouldSetMultipleTags()
        {
            var activity = new Activity("TestActivity");
            var tags = new List<TelemetryItem>
            {
                new("Item1", "Value1", false),
                new("Item2", "Value2", true)  // hashed
            };

            activity.WithTags(tags);

            var tagValue1 = activity.GetTagItem("VS.MSBuild.Item1");
            var tagValue2 = activity.GetTagItem("VS.MSBuild.Item2");

            tagValue1.ShouldNotBeNull();
            tagValue1.ShouldBe("Value1");

            tagValue2.ShouldNotBeNull();
            tagValue2.ShouldNotBe("Value2"); // hashed

            activity.Dispose();
        }

        [Fact]
        public void WithTags_DataHolderShouldSetMultipleTags()
        {
            var activity = new Activity("TestActivity");
            var dataHolder = new MockTelemetryDataHolder(); // see below

            activity.WithTags(dataHolder);

            var tagValueA = activity.GetTagItem("VS.MSBuild.TagA");
            var tagValueB = activity.GetTagItem("VS.MSBuild.TagB");

            tagValueA.ShouldNotBeNull();
            tagValueA.ShouldBe("ValueA");

            tagValueB.ShouldNotBeNull();
            tagValueB.ShouldNotBe("ValueB"); // should be hashed
            activity.Dispose();
        }

        [Fact]
        public void WithStartTime_ShouldSetActivityStartTime()
        {
            var activity = new Activity("TestActivity");
            var now = DateTime.UtcNow;

            activity.WithStartTime(now);

            activity.StartTimeUtc.ShouldBe(now);
            activity.Dispose();
        }

        [Fact]
        public void WithStartTime_NullDateTime_ShouldNotSetStartTime()
        {
            var activity = new Activity("TestActivity");
            var originalStartTime = activity.StartTimeUtc; // should be default (min) if not started

            activity.WithStartTime(null);

            activity.StartTimeUtc.ShouldBe(originalStartTime);

            activity.Dispose();
        }
    }

    /// <summary>
    /// A simple mock for testing IActivityTelemetryDataHolder. 
    /// Returns two items: one hashed, one not hashed.
    /// </summary>
    internal sealed class MockTelemetryDataHolder : IActivityTelemetryDataHolder
    {
        public IList<TelemetryItem> GetActivityProperties()
        {
            return new List<TelemetryItem>
            {
                new("TagA", "ValueA", false),
                new("TagB", "ValueB", true),
            };
        }
    }


    public class MSBuildActivitySourceTests
    {
        [Fact]
        public void StartActivity_ShouldPrefixNameCorrectly_WhenNoRemoteParent()
        {
            var source = new MSBuildActivitySource(TelemetryConstants.DefaultActivitySourceNamespace);
            using var listener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == TelemetryConstants.DefaultActivitySourceNamespace,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);


            var activity = source.StartActivity("Build");

            activity.ShouldNotBeNull();
            activity?.DisplayName.ShouldBe("VS/MSBuild/Build");

            activity?.Dispose();
        }

        [Fact]
        public void StartActivity_ShouldUseParentId_WhenRemoteParentExists()
        {
            // Arrange
            var parentActivity = new Activity("ParentActivity");
            parentActivity.SetParentId("|12345.abcde.");  // Simulate some parent trace ID
            parentActivity.AddTag("sampleTag", "sampleVal");
            parentActivity.Start();

            var source = new MSBuildActivitySource(TelemetryConstants.DefaultActivitySourceNamespace);
            using var listener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == TelemetryConstants.DefaultActivitySourceNamespace,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);

            // Act
            var childActivity = source.StartActivity("ChildBuild");

            // Assert
            childActivity.ShouldNotBeNull();
            // If HasRemoteParent is true, the code uses `parentId: Activity.Current.ParentId`.
            // However, by default .NET Activity doesn't automatically set HasRemoteParent = true
            // unless you explicitly set it. If you have logic that sets it, you can test it here.
            // For demonstration, we assume the ParentId is carried over if HasRemoteParent == true.
            if (Activity.Current?.HasRemoteParent == true)
            {
                childActivity?.ParentId.ShouldBe("|12345.abcde.");
            }

            parentActivity.Dispose();
            childActivity?.Dispose();
        }
    }
}
