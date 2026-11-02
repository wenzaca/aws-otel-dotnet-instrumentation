// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Resolved <c>OTEL_AWS_PROFILER_*</c> configuration. Names match ADOT Java and ADOT Python.
/// </summary>
internal sealed class ProfilerConfig
{
    private const string ProfilesPath = "/v1development/profiles";
    private const int WindowSecondsMin = 5;
    private const int WindowSecondsMax = 300;

    private ProfilerConfig()
    {
    }

    internal bool Enabled { get; private init; }

    internal string? DisabledReason { get; private init; }

    internal int WindowSeconds { get; private init; }

    internal uint CpuIntervalMs { get; private init; }

    internal bool MemoryEnabled { get; private init; }

    /// <summary>
    /// Gets a value indicating whether to keep only samples taken while a span was active.
    /// Off by default. On, it removes idle thread-pool and socket-wait threads, which have no
    /// active span, at the cost of also removing genuine background work.
    /// </summary>
    internal bool SpanSamplesOnly { get; private init; }

    internal uint MaxMemorySamplesPerMinute { get; private init; }

    internal Uri Endpoint { get; private init; } = new("http://localhost:4318" + ProfilesPath);

    internal bool Gzip { get; private init; }

    internal int ExportTimeoutMs { get; private init; }

    internal int MaxPayloadBytes { get; private init; }

    internal int MaxSamples { get; private init; }

    internal bool SigV4 { get; private init; }

    internal string SigV4Service { get; private init; } = "monitoring";

    internal string? SigV4Region { get; private init; }

    internal string? RegionWarning { get; private init; }

    internal TimeSpan DrainInterval { get; private init; }

    internal static ProfilerConfig FromEnvironment(Func<string, string?> read)
    {
        var disabledReason = DisabledBecause(read);
        var windowSeconds = Clamp(ReadInt(read, "OTEL_AWS_PROFILER_WINDOW_SECONDS", 60), WindowSecondsMin, WindowSecondsMax);
        var endpoint = ResolveEndpoint(read);
        var explicitRegion = Trimmed(read("OTEL_AWS_PROFILER_SIGV4_REGION"));
        var (region, regionWarning) = ResolveRegion(explicitRegion, endpoint, read);

        // Drain the 200 KiB native buffer well inside a window so the native side never has to drop
        // a batch, then aggregate in managed memory and emit one profile per window.
        var drainSeconds = Math.Min(windowSeconds, 5);

        return new ProfilerConfig
        {
            Enabled = disabledReason == null,
            DisabledReason = disabledReason,
            WindowSeconds = windowSeconds,

            // _WALL_INTERVAL_MS is the accurate name and matches ADOT Python; _CPU_INTERVAL_MS is
            // accepted for parity with the Node.js port.
            CpuIntervalMs = (uint)Clamp(
                ReadInt(read, "OTEL_AWS_PROFILER_WALL_INTERVAL_MS", ReadInt(read, "OTEL_AWS_PROFILER_CPU_INTERVAL_MS", 1000)),
                10,
                60_000),
            MemoryEnabled = ReadBool(read, "OTEL_AWS_PROFILER_MEMORY_ENABLED", false),
            SpanSamplesOnly = ReadBool(read, "OTEL_AWS_PROFILER_SPAN_SAMPLES_ONLY", false),
            MaxMemorySamplesPerMinute = (uint)Clamp(ReadInt(read, "OTEL_AWS_PROFILER_MAX_MEMORY_SAMPLES_PER_MINUTE", 200), 1, 100_000),
            Endpoint = endpoint,
            Gzip = string.Equals(Trimmed(read("OTEL_AWS_PROFILER_EXPORT_COMPRESSION")) ?? "gzip", "gzip", StringComparison.OrdinalIgnoreCase),
            ExportTimeoutMs = Clamp(ReadInt(read, "OTEL_AWS_PROFILER_EXPORT_TIMEOUT_MS", 10_000), 1_000, 120_000),
            MaxPayloadBytes = Math.Max(0, ReadInt(read, "OTEL_AWS_PROFILER_MAX_PAYLOAD_BYTES", 32 * 1024 * 1024)),
            MaxSamples = Clamp(ReadInt(read, "OTEL_AWS_PROFILER_MAX_SAMPLES", 16_000), 1, 16_000),
            SigV4 = ReadBool(read, "OTEL_AWS_PROFILER_SIGV4", false),
            SigV4Service = Trimmed(read("OTEL_AWS_PROFILER_SIGV4_SERVICE")) ?? "monitoring",
            SigV4Region = region,
            RegionWarning = regionWarning,
            DrainInterval = TimeSpan.FromSeconds(drainSeconds),
        };
    }

    /// <summary>
    /// Extracts the region label from an endpoint hostname of the form
    /// <c>service.region.amazonaws.com</c>. Handles three-part regions such as us-gov-west-1.
    /// </summary>
    internal static string? RegionFromHost(string host)
    {
        var labels = host.Split('.');
        for (var i = 1; i < labels.Length; i++)
        {
            if (LooksLikeRegion(labels[i]))
            {
                return labels[i];
            }
        }

        return null;
    }

    private static string? DisabledBecause(Func<string, string?> read)
    {
        if (!ReadBool(read, "OTEL_AWS_PROFILER_ENABLED", false))
        {
            return "OTEL_AWS_PROFILER_ENABLED is not true";
        }

        if (ReadBool(read, "OTEL_SDK_DISABLED", false))
        {
            return "OTEL_SDK_DISABLED is true";
        }

        // A profiling window outlives an invocation: the execution environment freezes between
        // invocations and is torn down without notice, so windows would routinely never export.
        // Matches the Python, Node.js and Rust ports.
        if (!string.IsNullOrEmpty(Trimmed(read("AWS_LAMBDA_FUNCTION_NAME"))))
        {
            return "running on AWS Lambda";
        }

        var mode = Trimmed(read("OTEL_AWS_PROFILER_MODE"));
        if (mode != null && !string.Equals(mode, "wall", StringComparison.OrdinalIgnoreCase))
        {
            // The CLR profiler suspends the runtime and walks every managed thread, and the sample
            // it hands back carries no thread state and no CPU or wait time
            // (continuous_profiler.cpp, ThreadState holds only the thread name). A blocked thread
            // is therefore indistinguishable from a running one, so only wall-clock can be
            // reported honestly. Measured: on an idle-ish web service roughly 80 percent of samples
            // sit in thread-pool and socket waits.
            return "OTEL_AWS_PROFILER_MODE=" + mode + " is not supported on .NET; the CLR profiler"
                + " samples all managed threads with no thread state, so only wall is available";
        }

        return null;
    }

    private static Uri ResolveEndpoint(Func<string, string?> read)
    {
        var explicitEndpoint = Trimmed(read("OTEL_AWS_PROFILER_ENDPOINT"));
        if (explicitEndpoint != null && Uri.TryCreate(explicitEndpoint, UriKind.Absolute, out var parsed))
        {
            return parsed;
        }

        var baseEndpoint = Trimmed(read("OTEL_EXPORTER_OTLP_ENDPOINT")) ?? "http://localhost:4318";
        return Uri.TryCreate(baseEndpoint.TrimEnd('/') + ProfilesPath, UriKind.Absolute, out var combined)
            ? combined
            : new Uri("http://localhost:4318" + ProfilesPath);
    }

    // The endpoint beats the ambient region on purpose, and no region is ever defaulted: an app in
    // one region posting to another must sign the endpoint's region, and hardcoding today's beta
    // region would silently route cross-region once prod is enabled.
    private static (string? Region, string? Warning) ResolveRegion(string? explicitRegion, Uri endpoint, Func<string, string?> read)
    {
        var hostRegion = RegionFromHost(endpoint.Host);

        if (explicitRegion != null)
        {
            var warning = hostRegion != null && !string.Equals(hostRegion, explicitRegion, StringComparison.OrdinalIgnoreCase)
                ? "OTEL_AWS_PROFILER_SIGV4_REGION=" + explicitRegion + " contradicts the region in endpoint host " + endpoint.Host
                : null;
            return (explicitRegion, warning);
        }

        if (hostRegion != null)
        {
            return (hostRegion, null);
        }

        var ambient = Trimmed(read("AWS_REGION")) ?? Trimmed(read("AWS_DEFAULT_REGION"));
        return ambient != null
            ? (ambient, null)
            : (null, "no signing region resolved from OTEL_AWS_PROFILER_SIGV4_REGION, the endpoint host, or AWS_REGION");
    }

    private static bool LooksLikeRegion(string label)
    {
        // aws partition region labels are <area>-<direction...>-<number>, e.g. us-east-1,
        // ap-southeast-3, us-gov-west-1, us-iso-east-1. Match on that shape rather than a list.
        var parts = label.Split('-');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        return parts[0].Length is 2 or 3
            && parts[0].All(char.IsLetter)
            && parts[^1].Length > 0
            && parts[^1].All(char.IsDigit)
            && parts.Skip(1).Take(parts.Length - 2).All(part => part.Length > 0 && part.All(char.IsLetter));
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool ReadBool(Func<string, string?> read, string name, bool fallback)
    {
        var value = Trimmed(read(name));
        return value == null ? fallback : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(Func<string, string?> read, string name, int fallback)
    {
        var value = Trimmed(read(name));
        return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
