// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Framework;

/// <summary>
/// Holder for project execution stats
/// It is not intended to be serialized into binlog nor shared after the build execution is done.
/// It is populated only if telemetry collection is active for current build and tasks/targets stats are regarded sampled-in.
/// </summary>
internal class ProjectBuildStats
{
    // Future: These might be configurable e.g. via telemetry sensitivity level?
    internal static TimeSpan DurationThresholdForTopN { get; set; } = TimeSpan.FromMilliseconds(100);
    private const int TopNTasksToReport = 5;
    internal static bool CollectCustomTaskNames { get; set; } = false;
    private const int MaxCustomTasksCsvLength = 400;
    private const int MaxSingleTaskNameLength = 40;

    public ProjectBuildStats(bool isDeserialized)
    {
        if (!isDeserialized)
        {
            _topTasksByCumulativeExecution =
                // sorted in descending order, plus we cannot return 0 on equality as SortedList would throw
                new SortedList<TimeSpan, string>(Comparer<TimeSpan>.Create((a, b) => b >= a ? 1 : -1));
        }
    }

    public void AddTask(string name, TimeSpan cumulativeExectionTime, short executionsCount, bool isCustom)
    {
        if (TopNTasksToReport > 0 && cumulativeExectionTime > DurationThresholdForTopN)
        {
            if (_topTasksByCumulativeExecution!.Count == 0 ||
                _topTasksByCumulativeExecution.Last().Key < cumulativeExectionTime)
            {
                _topTasksByCumulativeExecution.Add(cumulativeExectionTime, (isCustom ? "Custom:" : null) + name);
            }

            while (_topTasksByCumulativeExecution!.Count > TopNTasksToReport)
            {
                _topTasksByCumulativeExecution.RemoveAt(_topTasksByCumulativeExecution.Count - 1);
            }
        }

        TotalTasksCount++;
        TotalTasksExecution += cumulativeExectionTime;
        TotalTasksExecutionsCount += executionsCount;
        if (executionsCount > 0)
        {
            TotalExecutedTasksCount++;
        }

        if (isCustom)
        {
            CustomTasksCount++;
            TotalCustomTasksExecution += cumulativeExectionTime;
            CustomTasksExecutionsCount += executionsCount;
            if (executionsCount > 0)
            {
                ExecutedCustomTasksCount++;
            }

            if (CollectCustomTaskNames && CustomTasksCsv?.Length < MaxCustomTasksCsvLength)
            {
                CustomTasksCsv += "," + name.Substring(Math.Max(0, name.Length - MaxSingleTaskNameLength));
            }
        }
    }

    /// <summary>
    /// Total number of tasks registered for execution of this project.
    /// </summary>
    public short TotalTasksCount { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalTasksCount"/> that were not regarded to be produced by Microsoft.
    /// </summary>
    public short CustomTasksCount { get; set; }

    /// <summary>
    /// Total number of time any task was executed. All executions of any task counts (even if executed multiple times).
    /// </summary>
    public short TotalTasksExecutionsCount { get; set; }

    /// <summary>
    /// Total number of tasks that were executed. Multiple executions of single task counts just once.
    /// </summary>
    public short TotalExecutedTasksCount { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalTasksExecutionsCount"/> that were performed on tasks not regarded to be produced by Microsoft.
    /// </summary>
    public short CustomTasksExecutionsCount { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalExecutedTasksCount"/> that were performed on tasks not regarded to be produced by Microsoft.
    /// </summary>
    public short ExecutedCustomTasksCount { get; set; }

    /// <summary>
    /// Total cumulative time spent in execution of tasks for this project request.
    /// </summary>
    public TimeSpan TotalTasksExecution { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalTasksExecution"/> for executions that were performed on tasks not regarded to be produced by Microsoft.
    /// </summary>
    public TimeSpan TotalCustomTasksExecution { get; set; }

    /// <summary>
    /// Total number of targets registered for execution of this project.
    /// </summary>
    public short TotalTargetsCount { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalTargetsCount"/> that were not regarded to be produced by Microsoft.
    /// </summary>
    public short CustomTargetsCount { get; set; }

    /// <summary>
    /// Total number of time any target was executed. Each target is counted at most once - as multiple executions of single target per project are not allowed.
    /// </summary>
    public short TotalTargetsExecutionsCount { get; set; }

    /// <summary>
    /// Subset of <see cref="TotalTargetsExecutionsCount"/> for executions that were not regarded to be produced by Microsoft.
    /// </summary>
    public short ExecutedCustomTargetsCount { get; set; }

    /// <summary>
    /// Csv list of names of custom tasks.
    /// </summary>
    public string? CustomTasksCsv { get; set; }

    /// <summary>
    /// Top N (<see cref="TopNTasksToReport"/>) tasks by cumulative execution time.
    /// Custom tasks names are prefixed by "Custom:" prefix
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<TimeSpan, string>> TopTasksByCumulativeExecution =>
        _topTasksByCumulativeExecution ?? _topTasksDeserialized ?? [];

    internal void SetDeserializedTopN(IReadOnlyCollection<KeyValuePair<TimeSpan, string>> topNTasks)
    {
        _topTasksDeserialized = topNTasks;
    }

    private IReadOnlyCollection<KeyValuePair<TimeSpan, string>>? _topTasksDeserialized;
    private readonly SortedList<TimeSpan, string>? _topTasksByCumulativeExecution;

    internal static void WriteToStream(BinaryWriter writer, ProjectBuildStats? stats)
    {
        if (stats != null)
        {
            writer.Write((byte)1);
            writer.Write7BitEncodedInt(stats.TotalTasksCount);
            writer.Write7BitEncodedInt(stats.CustomTasksCount);
            writer.Write7BitEncodedInt(stats.TotalTasksExecutionsCount);
            writer.Write7BitEncodedInt(stats.TotalExecutedTasksCount);
            writer.Write7BitEncodedInt(stats.CustomTasksExecutionsCount);
            writer.Write7BitEncodedInt(stats.ExecutedCustomTasksCount);

            writer.Write(stats.TotalTasksExecution.Ticks);
            writer.Write(stats.TotalCustomTasksExecution.Ticks);

            writer.Write7BitEncodedInt(stats.TotalTargetsCount);
            writer.Write7BitEncodedInt(stats.CustomTargetsCount);
            writer.Write7BitEncodedInt(stats.TotalTargetsExecutionsCount);
            writer.Write7BitEncodedInt(stats.ExecutedCustomTargetsCount);
            writer.WriteOptionalString(stats.CustomTasksCsv);

            writer.Write7BitEncodedInt(stats.TopTasksByCumulativeExecution.Count);
            foreach (var pair in stats.TopTasksByCumulativeExecution)
            {
                writer.Write(pair.Key.Ticks);
                writer.Write(pair.Value);
            }
        }
        else
        {
            writer.Write((byte)0);
        }
    }

    internal static ProjectBuildStats? ReadFromStream(BinaryReader reader, int version)
    {
        if (reader.ReadByte() == 0)
        {
            return null;
        }

        ProjectBuildStats stats = new(true)
        {
            TotalTasksCount = (short)reader.Read7BitEncodedInt(),
            CustomTasksCount = (short)reader.Read7BitEncodedInt(),
            TotalTasksExecutionsCount = (short)reader.Read7BitEncodedInt(),
            TotalExecutedTasksCount = (short)reader.Read7BitEncodedInt(),
            CustomTasksExecutionsCount = (short)reader.Read7BitEncodedInt(),
            ExecutedCustomTasksCount = (short)reader.Read7BitEncodedInt(),
            TotalTasksExecution = TimeSpan.FromTicks(reader.ReadInt64()),
            TotalCustomTasksExecution = TimeSpan.FromTicks(reader.ReadInt64()),
            TotalTargetsCount = (short)reader.Read7BitEncodedInt(),
            CustomTargetsCount = (short)reader.Read7BitEncodedInt(),
            TotalTargetsExecutionsCount = (short)reader.Read7BitEncodedInt(),
            ExecutedCustomTargetsCount = (short)reader.Read7BitEncodedInt(),
            CustomTasksCsv = reader.ReadOptionalString(),
        };

        stats.SetDeserializedTopN(ReadTaskStats(reader));

        return stats;
    }

    private static IReadOnlyCollection<KeyValuePair<TimeSpan, string>> ReadTaskStats(BinaryReader reader)
    {
        int cnt = reader.Read7BitEncodedInt();
        List<KeyValuePair<TimeSpan, string>> list = new(cnt);
        for (int _ = 0; _ < cnt; _++)
        {
            list.Add(new KeyValuePair<TimeSpan, string>(TimeSpan.FromTicks(reader.ReadInt64()), reader.ReadString()));
        }

        return list;
    }
}
