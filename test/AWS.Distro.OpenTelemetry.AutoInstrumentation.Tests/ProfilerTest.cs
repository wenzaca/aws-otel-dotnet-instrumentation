// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;
using FluentAssertions;
using OpenTelemetry.Proto.Collector.Profiles.V1Development;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Tests for the continuous profiler's configuration, native buffer parsing and OTLP encoding.
/// <para>
/// The encoder is hand-written against opentelemetry-proto v1.10.0 rather than generated, so these
/// tests decode what it produced with bindings generated from the real proto. That is what makes
/// the wire format validated against the schema rather than against itself.
/// </para>
/// </summary>
public class ProfilerTest
{
    private const byte OpStartBatch = 0x01;
    private const byte OpStartSample = 0x02;
    private const byte OpEndBatch = 0x06;

    [Fact]
    public void Config_IsDisabledByDefault()
    {
        var config = ProfilerConfig.FromEnvironment(_ => null);

        config.Enabled.Should().BeFalse();
        config.DisabledReason.Should().Contain("OTEL_AWS_PROFILER_ENABLED");
    }

    [Fact]
    public void Config_EnabledWithDefaults()
    {
        var config = Build(("OTEL_AWS_PROFILER_ENABLED", "true"));

        config.Enabled.Should().BeTrue();
        config.WindowSeconds.Should().Be(60);
        config.MemoryEnabled.Should().BeFalse();
        config.Gzip.Should().BeTrue();
        config.SigV4.Should().BeFalse();
        config.MaxSamples.Should().Be(16_000);
        config.Endpoint.ToString().Should().Be("http://localhost:4318/v1development/profiles");
    }

    [Fact]
    public void Config_IsOffOnLambda()
    {
        var config = Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("AWS_LAMBDA_FUNCTION_NAME", "my-function"));

        config.Enabled.Should().BeFalse();
        config.DisabledReason.Should().Contain("Lambda");
    }

    [Fact]
    public void Config_RejectsUnsupportedMode()
    {
        // The CLR profiler carries no thread state, so cpu cannot be produced honestly.
        var config = Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_MODE", "cpu"));

        config.Enabled.Should().BeFalse();
        config.DisabledReason.Should().Contain("only wall is available");

        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_MODE", "wall")).Enabled.Should().BeTrue();
    }

    [Fact]
    public void Config_ReadsEitherIntervalName()
    {
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_WALL_INTERVAL_MS", "250")).CpuIntervalMs.Should().Be(250);
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_CPU_INTERVAL_MS", "300")).CpuIntervalMs.Should().Be(300);
        Build(("OTEL_AWS_PROFILER_ENABLED", "true")).CpuIntervalMs.Should().Be(1000);
    }

    [Fact]
    public void Config_SpanSamplesOnlyIsOffByDefault()
    {
        Build(("OTEL_AWS_PROFILER_ENABLED", "true")).SpanSamplesOnly.Should().BeFalse();
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_SPAN_SAMPLES_ONLY", "true")).SpanSamplesOnly.Should().BeTrue();
    }

    [Fact]
    public void Config_ClampsWindowAndSampleCap()
    {
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_WINDOW_SECONDS", "1")).WindowSeconds.Should().Be(5);
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_WINDOW_SECONDS", "9999")).WindowSeconds.Should().Be(300);

        // Never above what ingest accepts, even when asked for more.
        Build(("OTEL_AWS_PROFILER_ENABLED", "true"), ("OTEL_AWS_PROFILER_MAX_SAMPLES", "50000")).MaxSamples.Should().Be(16_000);
    }

    [Fact]
    public void Config_DerivesEndpointFromOtlpBase()
    {
        var config = Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4718/"));

        config.Endpoint.ToString().Should().Be("http://127.0.0.1:4718/v1development/profiles");
    }

    [Fact]
    public void Region_PrefersEndpointHostOverAmbient()
    {
        // An app need not run in the region it posts to: signing with its own region gets a 403.
        var config = Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("OTEL_AWS_PROFILER_ENDPOINT", "https://granite.us-east-1.amazonaws.com/v1development/profiles"),
            ("AWS_REGION", "eu-west-1"));

        config.SigV4Region.Should().Be("us-east-1");
        config.RegionWarning.Should().BeNull();
    }

    [Fact]
    public void Region_ExplicitOverrideWinsAndWarnsOnContradiction()
    {
        var config = Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("OTEL_AWS_PROFILER_ENDPOINT", "https://granite.us-east-1.amazonaws.com/v1development/profiles"),
            ("OTEL_AWS_PROFILER_SIGV4_REGION", "eu-west-1"));

        config.SigV4Region.Should().Be("eu-west-1");
        config.RegionWarning.Should().Contain("contradicts");
    }

    [Fact]
    public void Region_FallsBackToAmbientForRegionlessHostAndWarnsWhenNothingResolves()
    {
        Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("OTEL_AWS_PROFILER_ENDPOINT", "https://granite.amazonaws.com/v1development/profiles"),
            ("AWS_REGION", "us-east-1")).SigV4Region.Should().Be("us-east-1");

        var unresolved = Build(
            ("OTEL_AWS_PROFILER_ENABLED", "true"),
            ("OTEL_AWS_PROFILER_ENDPOINT", "https://granite.amazonaws.com/v1development/profiles"));

        unresolved.SigV4Region.Should().BeNull();
        unresolved.RegionWarning.Should().Contain("no signing region");
    }

    [Theory]
    [InlineData("granite.us-east-1.amazonaws.com", "us-east-1")]
    [InlineData("monitoring.ap-southeast-3.amazonaws.com", "ap-southeast-3")]
    [InlineData("monitoring.us-gov-west-1.amazonaws.com", "us-gov-west-1")]
    [InlineData("monitoring.us-iso-east-1.c2s.ic.gov", "us-iso-east-1")]
    [InlineData("granite.amazonaws.com", null)]
    [InlineData("localhost", null)]
    public void Region_IsReadFromHostLabel(string host, string? expected)
    {
        ProfilerConfig.RegionFromHost(host).Should().Be(expected);
    }

    [Fact]
    public void NativeParser_ReadsFramesLeafFirstAndSkipsTheSamplerThread()
    {
        var buffer = ThreadSampleBatch(
            timestampMillis: 1_700_000_000_000,
            samples: new[]
            {
                ("worker-1", 0x0102030405060708L, 0x1112131415161718L, 0x2122232425262728L, new[] { "App.Hash()", "App.Price()", "App.Main()" }),
                (NativeSampleParser.SamplerThreadName, 0L, 0L, 0L, new[] { "Sampler.Loop()" }),
            });

        var parsed = NativeSampleParser.ParseThreadSamples(buffer.Buffer, buffer.Length);

        parsed.Should().HaveCount(1);
        parsed[0].ThreadName.Should().Be("worker-1");
        parsed[0].Frames.Should().Equal("App.Hash()", "App.Price()", "App.Main()");
        parsed[0].TraceIdHigh.Should().Be(0x0102030405060708L);
        parsed[0].TraceIdLow.Should().Be(0x1112131415161718L);
        parsed[0].SpanId.Should().Be(0x2122232425262728L);
        parsed[0].TimestampNanoseconds.Should().Be(1_700_000_000_000L * 1_000_000);
    }

    [Fact]
    public void NativeParser_ResolvesRepeatedFramesFromTheStringCodeTable()
    {
        // The second sample re-uses the first sample's frame codes rather than re-sending strings.
        var buffer = ThreadSampleBatch(
            timestampMillis: 1,
            samples: new[]
            {
                ("a", 0L, 0L, 0L, new[] { "Shared.Frame()" }),
                ("b", 0L, 0L, 0L, new[] { "Shared.Frame()" }),
            });

        var parsed = NativeSampleParser.ParseThreadSamples(buffer.Buffer, buffer.Length);

        parsed.Should().HaveCount(2);
        parsed[1].Frames.Should().Equal("Shared.Frame()");
    }

    [Fact]
    public void Encoder_ProducesACloudWatchReadableCpuProfile()
    {
        var sample = NewSample("worker-1", 0x0102030405060708L, 0x1112131415161718L, 0x2122232425262728L, "App.Hash()", "App.Price()");
        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");

        encoder.AddProfile(
            "cpu",
            "nanoseconds",
            periodNanoseconds: 1_000_000,
            startUnixNanoseconds: 1_700_000_000_000_000_000,
            durationNanoseconds: 15_000_000_000,
            samples: new[] { sample },
            aggregate: _ => 1_000_000,
            operationForSpan: _ => "GET /price",
            maxSamples: 16_000);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "zw-demo-dotnet-shipping") }, "scope", "1.0"));

        var dictionary = request.Dictionary;

        // An empty table is rejected with HTTP 400, so every table carries a zero-value entry.
        dictionary.MappingTable.Should().NotBeEmpty();
        dictionary.LocationTable.Should().NotBeEmpty();
        dictionary.FunctionTable.Should().NotBeEmpty();
        dictionary.LinkTable.Should().NotBeEmpty();
        dictionary.StringTable.Should().NotBeEmpty();
        dictionary.AttributeTable.Should().NotBeEmpty();
        dictionary.StackTable.Should().NotBeEmpty();
        dictionary.StringTable[0].Should().BeEmpty();
        dictionary.LinkTable[0].TraceId.Should().BeEmpty();
        dictionary.StackTable[0].LocationIndices.Should().BeEmpty();

        var resource = request.ResourceProfiles.Should().ContainSingle().Subject;
        resource.Resource.Attributes.Should().ContainSingle(attribute =>
            attribute.Key == "service.name" && attribute.Value.StringValue == "zw-demo-dotnet-shipping");

        var profile = resource.ScopeProfiles.Should().ContainSingle().Subject
            .Profiles.Should().ContainSingle().Subject;

        dictionary.StringTable[profile.SampleType.TypeStrindex].Should().Be("cpu");
        dictionary.StringTable[profile.SampleType.UnitStrindex].Should().Be("nanoseconds");
        dictionary.StringTable[profile.PeriodType.TypeStrindex].Should().Be("cpu");
        profile.Period.Should().Be(1_000_000);
        profile.TimeUnixNano.Should().Be(1_700_000_000_000_000_000);
        profile.DurationNano.Should().Be(15_000_000_000);

        var encoded = profile.Samples.Should().ContainSingle().Subject;
        encoded.Values.Should().Equal(1_000_000);

        // Stacks are leaf-first, in the order the CLR stack-snapshot callback delivers them.
        var frames = dictionary.StackTable[encoded.StackIndex].LocationIndices
            .Select(index => dictionary.LocationTable[index])
            .Select(location => dictionary.StringTable[dictionary.FunctionTable[location.Lines[0].FunctionIndex].NameStrindex])
            .ToList();
        frames.Should().Equal("App.Hash()", "App.Price()");

        // Locations point at the synthetic runtime mapping rather than at the zero entry.
        var leaf = dictionary.LocationTable[dictionary.StackTable[encoded.StackIndex].LocationIndices[0]];
        leaf.MappingIndex.Should().Be(1);
        dictionary.StringTable[dictionary.MappingTable[1].FilenameStrindex].Should().Be(".NET 8.0.0");

        var sampleAttributes = encoded.AttributeIndices
            .Select(index => dictionary.AttributeTable[index])
            .ToDictionary(entry => dictionary.StringTable[entry.KeyStrindex], entry => entry.Value.StringValue);
        sampleAttributes.Should().Contain(new KeyValuePair<string, string>("thread.name", "worker-1"));
        sampleAttributes.Should().Contain(new KeyValuePair<string, string>("operation", "GET /price"));
    }

    [Fact]
    public void Encoder_WritesTraceAndSpanIdsBigEndian()
    {
        var sample = NewSample("worker-1", 0x0102030405060708L, 0x1112131415161718L, 0x2122232425262728L, "App.Hash()");
        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1, 1, 1, new[] { sample }, _ => 1, _ => null, 16_000);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "svc") }, "scope", "1.0"));

        var link = request.Dictionary.LinkTable[1];

        // The native side derives these from the W3C hex, so the bytes must come back in that
        // order. BitConverter would reverse them on x64 and arm64.
        Convert.ToHexString(link.TraceId.ToByteArray()).ToLowerInvariant()
            .Should().Be("01020304050607081112131415161718");
        Convert.ToHexString(link.SpanId.ToByteArray()).ToLowerInvariant()
            .Should().Be("2122232425262728");
    }

    [Fact]
    public void Encoder_HasNoLinkWhenTheSampleHasNoSpan()
    {
        var sample = NewSample("worker-1", 0, 0, 0, "App.Hash()");
        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1, 1, 1, new[] { sample }, _ => 1, _ => "unused", 16_000);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "svc") }, "scope", "1.0"));

        var profile = request.ResourceProfiles[0].ScopeProfiles[0].Profiles[0];
        profile.Samples[0].LinkIndex.Should().Be(0);
        request.Dictionary.LinkTable.Should().HaveCount(1);

        // No span means no operation attribute either.
        profile.Samples[0].AttributeIndices
            .Select(index => request.Dictionary.StringTable[request.Dictionary.AttributeTable[index].KeyStrindex])
            .Should().NotContain("operation");
    }

    [Fact]
    public void Encoder_SumsIdenticalSamplesIntoOne()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(_ => NewSample("worker-1", 0, 0, 0, "App.Hash()", "App.Price()"))
            .ToList();

        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1_000_000, 1, 1, samples, _ => 1_000_000, _ => null, 16_000);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "svc") }, "scope", "1.0"));

        var profile = request.ResourceProfiles[0].ScopeProfiles[0].Profiles[0];
        profile.Samples.Should().ContainSingle();
        profile.Samples[0].Values.Should().Equal(5_000_000);
    }

    [Fact]
    public void Encoder_StopsAtTheSampleCapRatherThanOverflowingAProfile()
    {
        var samples = Enumerable.Range(0, 50)
            .Select(i => NewSample("worker-" + i, 0, 0, 0, "App.Frame" + i + "()"))
            .ToList();

        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1, 1, 1, samples, _ => 1, _ => null, maxSamples: 10);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "svc") }, "scope", "1.0"));

        request.ResourceProfiles[0].ScopeProfiles[0].Profiles[0].Samples.Should().HaveCount(10);
    }

    [Fact]
    public void Encoder_EmitsAllocationProfileAlongsideCpu()
    {
        var cpu = NewSample("worker-1", 0, 0, 0, "App.Hash()");
        var allocation = NewSample("worker-1", 0, 0, 0, "App.Allocate()");
        allocation.AllocationSizeBytes = 4096;

        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1, 1, 1, new[] { cpu }, _ => 1, _ => null, 16_000);
        encoder.AddProfile("alloc_space", "bytes", 0, 1, 1, new[] { allocation }, s => s.AllocationSizeBytes, _ => null, 16_000);

        var request = ExportProfilesServiceRequest.Parser.ParseFrom(
            encoder.Build(new[] { new KeyValuePair<string, string>("service.name", "svc") }, "scope", "1.0"));

        var profiles = request.ResourceProfiles[0].ScopeProfiles[0].Profiles;
        profiles.Should().HaveCount(2);

        var types = profiles.Select(p => request.Dictionary.StringTable[p.SampleType.TypeStrindex]).ToList();
        types.Should().Equal("cpu", "alloc_space");
        profiles[1].Samples[0].Values.Should().Equal(4096);
    }

    [Fact]
    public void Encoder_SkipsAProfileWithNoUsableSamples()
    {
        var encoder = new OtlpProfilesEncoder(".NET 8.0.0");
        encoder.AddProfile("cpu", "nanoseconds", 1, 1, 1, Array.Empty<ProfilerSample>(), _ => 1, _ => null, 16_000);

        encoder.HasProfiles.Should().BeFalse();
    }

    [Fact]
    public void SpanNameIndex_LooksUpByTheSameIdTheNativeSideReports()
    {
        var index = new SpanNameIndex();
        var spanId = System.Diagnostics.ActivitySpanId.CreateFromString("2122232425262728".AsSpan());

        index.Record(spanId, "GET /price");

        index.Lookup(0x2122232425262728L).Should().Be("GET /price");
        index.Lookup(0x1111111111111111L).Should().BeNull();
    }

    [Fact]
    public void SpanNameIndex_EvictsOldestBeyondCapacity()
    {
        var index = new SpanNameIndex(capacity: 2);
        index.Record(System.Diagnostics.ActivitySpanId.CreateFromString("0000000000000001".AsSpan()), "first");
        index.Record(System.Diagnostics.ActivitySpanId.CreateFromString("0000000000000002".AsSpan()), "second");
        index.Record(System.Diagnostics.ActivitySpanId.CreateFromString("0000000000000003".AsSpan()), "third");

        index.Lookup(1).Should().BeNull();
        index.Lookup(2).Should().Be("second");
        index.Lookup(3).Should().Be("third");
    }

    private static ProfilerConfig Build(params (string Key, string Value)[] environment)
    {
        var map = environment.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return ProfilerConfig.FromEnvironment(name => map.TryGetValue(name, out var value) ? value : null);
    }

    private static ProfilerSample NewSample(string threadName, long traceIdHigh, long traceIdLow, long spanId, params string[] frames)
    {
        var sample = new ProfilerSample(1, traceIdHigh, traceIdLow, spanId, threadName);
        sample.Frames.AddRange(frames);
        return sample;
    }

    /// <summary>
    /// Builds a batch in the native profiler's wire format: big-endian integers, UTF-16
    /// length-prefixed strings, and a per-batch string code table where a negative code introduces
    /// a string and a positive code refers back to one.
    /// </summary>
    private static (byte[] Buffer, int Length) ThreadSampleBatch(
        long timestampMillis,
        (string ThreadName, long TraceIdHigh, long TraceIdLow, long SpanId, string[] Frames)[] samples)
    {
        var bytes = new List<byte>();
        var codes = new Dictionary<string, short>();

        bytes.Add(OpStartBatch);
        bytes.AddRange(Int32BigEndian(1));
        bytes.AddRange(Int64BigEndian(timestampMillis));

        foreach (var sample in samples)
        {
            bytes.Add(OpStartSample);
            bytes.AddRange(Utf16String(sample.ThreadName));
            bytes.AddRange(Int64BigEndian(sample.TraceIdHigh));
            bytes.AddRange(Int64BigEndian(sample.TraceIdLow));
            bytes.AddRange(Int64BigEndian(sample.SpanId));

            foreach (var frame in sample.Frames)
            {
                if (codes.TryGetValue(frame, out var existing))
                {
                    bytes.AddRange(Int16BigEndian(existing));
                }
                else
                {
                    var code = (short)(codes.Count + 1);
                    codes[frame] = code;
                    bytes.AddRange(Int16BigEndian((short)-code));
                    bytes.AddRange(Utf16String(frame));
                }
            }

            bytes.AddRange(Int16BigEndian(0));
        }

        bytes.Add(OpEndBatch);

        var buffer = new byte[bytes.Count + 64];
        bytes.CopyTo(buffer);
        return (buffer, bytes.Count);
    }

    private static byte[] Int16BigEndian(short value) => new[] { (byte)(value >> 8), (byte)value };

    private static byte[] Int32BigEndian(int value) =>
        new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    private static byte[] Int64BigEndian(long value) =>
        Enumerable.Range(0, 8).Select(i => (byte)(value >> (8 * (7 - i)))).ToArray();

    private static byte[] Utf16String(string value)
    {
        var payload = Encoding.Unicode.GetBytes(value);
        return Int16BigEndian((short)value.Length).Concat(payload).ToArray();
    }
}
