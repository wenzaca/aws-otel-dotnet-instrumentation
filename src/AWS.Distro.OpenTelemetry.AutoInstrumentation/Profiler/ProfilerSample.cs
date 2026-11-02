// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// One stack sample as handed over by the CLR profiler. Frames are already symbolized to
/// <c>Class.Method(args)</c> strings by the native side and arrive leaf-first.
/// </summary>
internal sealed class ProfilerSample
{
    internal ProfilerSample(long timestampMilliseconds, long traceIdHigh, long traceIdLow, long spanId, string? threadName)
    {
        this.TimestampNanoseconds = timestampMilliseconds * 1_000_000;
        this.TraceIdHigh = traceIdHigh;
        this.TraceIdLow = traceIdLow;
        this.SpanId = spanId;
        this.ThreadName = threadName;
    }

    internal long TimestampNanoseconds { get; }

    internal long TraceIdHigh { get; }

    internal long TraceIdLow { get; }

    internal long SpanId { get; }

    internal string? ThreadName { get; }

    /// <summary>Gets or sets the allocated byte count, for allocation samples only.</summary>
    internal long AllocationSizeBytes { get; set; }

    internal List<string> Frames { get; } = new();

    internal bool HasSpan => this.SpanId != 0 || this.TraceIdHigh != 0 || this.TraceIdLow != 0;
}
