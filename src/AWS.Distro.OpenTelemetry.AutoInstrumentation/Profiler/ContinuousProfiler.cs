// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Receives CLR profiler samples, aggregates them into fixed windows, and exports each window as
/// OTLP profiles.
/// </summary>
/// <remarks>
/// The upstream auto-instrumentation binds to this type by reflection: it looks for public
/// <c>ExportThreadSamples</c> and <c>ExportAllocationSamples</c> methods, each taking
/// <c>(byte[] buffer, int read, CancellationToken)</c>. Keeping that shape in one place means a
/// future upstream version that switches to <c>IContinuousProfilerExporter</c> is a localized
/// change.
/// </remarks>
public sealed class ContinuousProfiler
{
    // Ingest rejects an instrumentation scope name beginning with "aws" as reserved
    // (HTTP 400 "Instrumentation scope names with \"aws\" are reserved for AWS"). ADOT Python and
    // the Node.js port both use this name.
    private const string ScopeName = "serviceevents";
    private const string WallSampleType = "wall";
    private const string WallSampleUnit = "nanoseconds";
    private const string AllocSampleType = "alloc_space";
    private const string AllocSampleUnit = "bytes";

#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<ContinuousProfiler>();
    private static Resource? capturedResource;

    private readonly ProfilerConfig config;
    private readonly ProfilesExporter exporter;
    private readonly SpanNameIndex spanNames = new();
    private readonly List<ProfilerSample> cpuSamples = new();
    private readonly List<ProfilerSample> allocationSamples = new();
    private readonly object gate = new();
    private readonly long sampleValueNanoseconds;

    private long windowStartTicks;
    private IReadOnlyList<KeyValuePair<string, string>>? resourceAttributes;

    private ContinuousProfiler(ProfilerConfig config)
    {
        this.config = config;
        this.exporter = new ProfilesExporter(config);
        this.sampleValueNanoseconds = config.CpuIntervalMs * 1_000_000L;
        this.windowStartTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Captures the distro's detected Resource so profiles carry the same attributes as the rest
    /// of the telemetry. <c>service.name</c> is required by ingest.
    /// </summary>
    /// <param name="builder">The resource builder the distro has configured.</param>
    public static void CaptureResource(ResourceBuilder builder)
    {
        try
        {
            capturedResource = builder.Build();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Profiler could not capture the resource; profiles will fall back to environment attributes.");
        }
    }

    /// <summary>
    /// Builds the continuous profiler configuration the auto-instrumentation asks for. Returns a
    /// disabled configuration when profiling is off, which leaves the native sampler idle.
    /// </summary>
    /// <returns>
    /// Thread sampling enabled, thread sampling interval in ms, allocation sampling enabled, max
    /// allocation samples per minute, export interval, export timeout, and the exporter instance.
    /// </returns>
    public static Tuple<bool, uint, bool, uint, TimeSpan, TimeSpan, object> GetConfiguration()
    {
        var disabled = Tuple.Create(false, 0u, false, 0u, TimeSpan.Zero, TimeSpan.Zero, new object());

        try
        {
            var config = ProfilerConfig.FromEnvironment(Environment.GetEnvironmentVariable);
            if (!config.Enabled)
            {
                Logger.Log(LogLevel.Debug, "AWS profiler disabled: {0}", config.DisabledReason);
                return disabled;
            }

            if (config.SigV4 && config.RegionWarning != null)
            {
                Logger.LogWarning("AWS profiler: {0}", config.RegionWarning);
            }

            var profiler = new ContinuousProfiler(config);
            profiler.spanNames.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => profiler.Flush();

            Logger.Log(
                LogLevel.Information,
                "AWS profiler enabled: wall every {0}ms, {1}s windows, endpoint {2}, sigv4 {3}",
                config.CpuIntervalMs,
                config.WindowSeconds,
                config.Endpoint,
                config.SigV4);

            return Tuple.Create(
                true,
                config.CpuIntervalMs,
                config.MemoryEnabled,
                config.MaxMemorySamplesPerMinute,
                config.DrainInterval,
                TimeSpan.FromMilliseconds(config.ExportTimeoutMs),
                (object)profiler);
        }
        catch (Exception ex)
        {
            // Fail open: a profiler that cannot start must leave the application running.
            Logger.LogWarning(ex, "AWS profiler failed to start; continuing without profiling.");
            return disabled;
        }
    }

    /// <summary>
    /// Receives one batch of CPU thread samples drained from the native buffer.
    /// </summary>
    /// <param name="buffer">The native sample buffer.</param>
    /// <param name="read">How many bytes of the buffer are in use.</param>
    /// <param name="cancellationToken">Cancellation for this batch.</param>
    public void ExportThreadSamples(byte[] buffer, int read, CancellationToken cancellationToken)
    {
        this.Accept(this.cpuSamples, () => NativeSampleParser.ParseThreadSamples(buffer, read), cancellationToken);
    }

    /// <summary>
    /// Receives one batch of allocation samples drained from the native buffer.
    /// </summary>
    /// <param name="buffer">The native sample buffer.</param>
    /// <param name="read">How many bytes of the buffer are in use.</param>
    /// <param name="cancellationToken">Cancellation for this batch.</param>
    public void ExportAllocationSamples(byte[] buffer, int read, CancellationToken cancellationToken)
    {
        this.Accept(this.allocationSamples, () => NativeSampleParser.ParseAllocationSamples(buffer, read), cancellationToken);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ResolveResourceAttributes()
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        var resource = capturedResource;
        if (resource != null)
        {
            foreach (var attribute in resource.Attributes)
            {
                if (attribute.Value is not null)
                {
                    resolved[attribute.Key] = Convert.ToString(attribute.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
        }

        // service.name is required by ingest. If the resource was never captured (for example the
        // tracer provider was not configured), fall back to the environment the SDK would have read.
        if (!resolved.ContainsKey("service.name"))
        {
            var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
            resolved["service.name"] = string.IsNullOrWhiteSpace(serviceName) ? "unknown_service:dotnet" : serviceName!;
        }

        return resolved.ToList();
    }

    private void Accept(List<ProfilerSample> target, Func<List<ProfilerSample>> parse, CancellationToken cancellationToken)
    {
        try
        {
            var parsed = parse();
            bool flush;
            lock (this.gate)
            {
                target.AddRange(parsed);
                flush = DateTime.UtcNow.Ticks - this.windowStartTicks >= this.config.WindowSeconds * TimeSpan.TicksPerSecond;
            }

            if (flush)
            {
                this.Export(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "AWS profiler failed to accept a sample batch.");
        }
    }

    private void Flush() => this.Export(CancellationToken.None);

    private void Export(CancellationToken cancellationToken)
    {
        List<ProfilerSample> cpu;
        List<ProfilerSample> allocations;
        long startTicks;
        long endTicks = DateTime.UtcNow.Ticks;

        lock (this.gate)
        {
            if (this.cpuSamples.Count == 0 && this.allocationSamples.Count == 0)
            {
                this.windowStartTicks = endTicks;
                return;
            }

            cpu = new List<ProfilerSample>(this.cpuSamples);
            allocations = new List<ProfilerSample>(this.allocationSamples);
            startTicks = this.windowStartTicks;
            this.cpuSamples.Clear();
            this.allocationSamples.Clear();
            this.windowStartTicks = endTicks;
        }

        try
        {
            this.resourceAttributes ??= ResolveResourceAttributes();

            var startNanos = (startTicks - DateTime.UnixEpoch.Ticks) * 100L;
            var durationNanos = (endTicks - startTicks) * 100L;

            var encoder = new OtlpProfilesEncoder(".NET " + Environment.Version);
            encoder.AddProfile(
                WallSampleType,
                WallSampleUnit,
                this.sampleValueNanoseconds,
                startNanos,
                durationNanos,
                this.config.SpanSamplesOnly ? cpu.Where(sample => sample.HasSpan).ToList() : cpu,
                _ => this.sampleValueNanoseconds,
                this.spanNames.Lookup,
                this.config.MaxSamples);

            if (allocations.Count > 0)
            {
                encoder.AddProfile(
                    AllocSampleType,
                    AllocSampleUnit,
                    0,
                    startNanos,
                    durationNanos,
                    allocations,
                    sample => sample.AllocationSizeBytes,
                    this.spanNames.Lookup,
                    this.config.MaxSamples);
            }

            if (!encoder.HasProfiles)
            {
                return;
            }

            var payload = encoder.Build(this.resourceAttributes, ScopeName, Version.version);
            var error = this.exporter.Export(payload, cancellationToken);
            if (error != null)
            {
                Logger.LogWarning("AWS profiler export failed: {0}", error);
            }
            else
            {
                Logger.Log(
                    LogLevel.Debug,
                    "AWS profiler exported {0} profile(s), {1} sample(s), {2} bytes",
                    encoder.ProfileCount,
                    encoder.SampleCount,
                    payload.Length);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "AWS profiler failed to export a window.");
        }
    }
}
