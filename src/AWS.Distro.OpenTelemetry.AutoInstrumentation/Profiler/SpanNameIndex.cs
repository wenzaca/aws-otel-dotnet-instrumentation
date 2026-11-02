// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Maps a span id to its operation name.
/// </summary>
/// <remarks>
/// <para>
/// The CLR profiler stamps each sample with the active span's ids but not its name, so the name
/// has to come from the managed side. The join is exact rather than time-based: samples carry the
/// span id, and span ids are what this records. That is simpler and more accurate than the
/// timeline join the Node.js port needs, where the CDP profile has no per-sample attribute slot.
/// The native side derives the id with <c>ulong.Parse(SpanId.ToHexString(), HexNumber)</c>, and
/// this uses the same conversion so the keys match.
/// </para>
/// <para>
/// The Activity is held rather than its name read eagerly. <c>Activity.CurrentChanged</c> fires
/// when the activity becomes current, which for ASP.NET Core is before the instrumentation
/// rewrites <c>DisplayName</c> to the matched route -- reading it there yields
/// <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c> rather than <c>GET /rate</c>. The name is
/// read on lookup instead, which happens when a window is exported and so after the span has
/// ended, and the reference is dropped at that point.
/// </para>
/// </remarks>
internal sealed class SpanNameIndex
{
    private readonly int capacity;
    private readonly Dictionary<long, Entry> entries;
    private readonly Queue<long> insertionOrder;
    private readonly object gate = new();

    internal SpanNameIndex(int capacity = 4096)
    {
        this.capacity = capacity;
        this.entries = new Dictionary<long, Entry>(capacity);
        this.insertionOrder = new Queue<long>(capacity);
    }

    internal void Start()
    {
        Activity.CurrentChanged += this.OnCurrentChanged;
    }

    internal void Stop()
    {
        Activity.CurrentChanged -= this.OnCurrentChanged;
    }

    internal string? Lookup(long spanId)
    {
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(spanId, out var entry))
            {
                return null;
            }

            if (entry.Name == null && entry.Activity != null)
            {
                entry.Name = entry.Activity.DisplayName;
                entry.Activity = null;
            }

            return entry.Name;
        }
    }

    internal void Record(Activity activity)
    {
        if (!TryConvert(activity.SpanId, out var key))
        {
            return;
        }

        lock (this.gate)
        {
            if (this.entries.ContainsKey(key))
            {
                return;
            }

            if (this.insertionOrder.Count >= this.capacity)
            {
                this.entries.Remove(this.insertionOrder.Dequeue());
            }

            this.entries[key] = new Entry { Activity = activity };
            this.insertionOrder.Enqueue(key);
        }
    }

    /// <summary>Records a name directly, for tests and for callers without an Activity.</summary>
    internal void Record(ActivitySpanId spanId, string operationName)
    {
        if (!TryConvert(spanId, out var key) || string.IsNullOrEmpty(operationName))
        {
            return;
        }

        lock (this.gate)
        {
            if (this.entries.ContainsKey(key))
            {
                return;
            }

            if (this.insertionOrder.Count >= this.capacity)
            {
                this.entries.Remove(this.insertionOrder.Dequeue());
            }

            this.entries[key] = new Entry { Name = operationName };
            this.insertionOrder.Enqueue(key);
        }
    }

    private static bool TryConvert(ActivitySpanId spanId, out long key)
    {
        if (ulong.TryParse(spanId.ToHexString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            key = unchecked((long)parsed);
            return true;
        }

        key = 0;
        return false;
    }

    private void OnCurrentChanged(object? sender, ActivityChangedEventArgs args)
    {
        var current = args.Current;
        if (current != null)
        {
            this.Record(current);
        }
    }

    private sealed class Entry
    {
        internal Activity? Activity { get; set; }

        internal string? Name { get; set; }
    }
}
