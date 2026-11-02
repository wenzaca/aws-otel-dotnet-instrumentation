// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Builds an <c>ExportProfilesServiceRequest</c> (opentelemetry-proto v1.10.0,
/// profiles/v1development) from CLR profiler samples.
/// </summary>
/// <remarks>
/// Every dictionary table carries a zero-value entry at index 0 -- an empty table is rejected with
/// HTTP 400. Stacks are leaf-first, which is the order the CLR's stack-snapshot callback already
/// delivers. Samples are aggregated by stack, link and attributes before encoding so a busy
/// process does not exceed the per-profile sample limit.
/// </remarks>
internal sealed class OtlpProfilesEncoder
{
    private const string FrameTypeAttribute = "profile.frame.type";
    private const string FrameTypeValue = "dotnet";
    private const string ThreadNameAttribute = "thread.name";
    private const string OperationAttribute = "operation";

    private readonly List<string> strings = new() { string.Empty };
    private readonly Dictionary<string, int> stringIndex = new() { [string.Empty] = 0 };
    private readonly List<int> functionNameIndices = new() { 0 };
    private readonly Dictionary<string, int> functionIndex = new();
    private readonly List<int> locationFunctionIndices = new() { 0 };
    private readonly Dictionary<string, int> locationIndex = new();
    private readonly List<byte[]> linkTraceIds = new() { Array.Empty<byte>() };
    private readonly List<byte[]> linkSpanIds = new() { Array.Empty<byte>() };
    private readonly Dictionary<(long High, long Low, long Span), int> linkIndex = new();
    private readonly List<(int KeyIndex, string Value)> attributes = new() { (0, string.Empty) };
    private readonly Dictionary<(string Key, string Value), int> attributeIndex = new();
    private readonly List<List<int>> stacks = new() { new List<int>() };
    private readonly Dictionary<string, int> stackIndex = new();
    private readonly List<ProfileEntry> profiles = new();

    private readonly int frameTypeAttributeIndex;
    private readonly int mappingFilenameIndex;

    internal OtlpProfilesEncoder(string runtimeName)
    {
        this.frameTypeAttributeIndex = this.AddAttribute(FrameTypeAttribute, FrameTypeValue);

        // Frames arrive pre-symbolized with no addresses, so there is nothing to build a real
        // mapping from. One synthetic mapping for the runtime, pointed at by every location.
        this.mappingFilenameIndex = this.AddString(runtimeName);
    }

    internal bool HasProfiles => this.profiles.Count > 0;

    internal int ProfileCount => this.profiles.Count;

    internal int SampleCount => this.profiles.Sum(profile => profile.Samples.Count);

    /// <summary>
    /// Adds one profile for a sample type. <paramref name="aggregate"/> maps each sample to its
    /// value in the profile's unit; values for identical (stack, link, attributes) are summed.
    /// </summary>
    internal void AddProfile(
        string sampleType,
        string sampleUnit,
        long periodNanoseconds,
        long startUnixNanoseconds,
        long durationNanoseconds,
        IEnumerable<ProfilerSample> samples,
        Func<ProfilerSample, long> aggregate,
        Func<long, string?> operationForSpan,
        int maxSamples)
    {
        var merged = new Dictionary<string, AggregatedSample>();
        var ordered = new List<string>();

        foreach (var sample in samples)
        {
            if (sample.Frames.Count == 0)
            {
                continue;
            }

            var stack = this.AddStack(sample.Frames);
            var link = sample.HasSpan ? this.AddLink(sample.TraceIdHigh, sample.TraceIdLow, sample.SpanId) : 0;

            var sampleAttributes = new List<int>();
            if (!string.IsNullOrEmpty(sample.ThreadName))
            {
                sampleAttributes.Add(this.AddAttribute(ThreadNameAttribute, sample.ThreadName!));
            }

            var operation = sample.HasSpan ? operationForSpan(sample.SpanId) : null;
            if (!string.IsNullOrEmpty(operation))
            {
                sampleAttributes.Add(this.AddAttribute(OperationAttribute, operation!));
            }

            var key = stack + "|" + link + "|" + string.Join(",", sampleAttributes);
            if (!merged.TryGetValue(key, out var existing))
            {
                if (merged.Count >= maxSamples)
                {
                    continue;
                }

                existing = new AggregatedSample(stack, link, sampleAttributes);
                merged[key] = existing;
                ordered.Add(key);
            }

            existing.Value += aggregate(sample);
        }

        if (merged.Count == 0)
        {
            return;
        }

        this.profiles.Add(new ProfileEntry
        {
            SampleTypeIndex = this.AddString(sampleType),
            SampleUnitIndex = this.AddString(sampleUnit),
            PeriodTypeIndex = this.AddString(sampleType),
            PeriodUnitIndex = this.AddString(sampleUnit),
            Period = periodNanoseconds,
            TimeUnixNano = (ulong)startUnixNanoseconds,
            DurationNano = (ulong)durationNanoseconds,
            Samples = ordered.Select(key => merged[key]).ToList(),
        });
    }

    internal byte[] Build(IReadOnlyList<KeyValuePair<string, string>> resourceAttributes, string scopeName, string scopeVersion)
    {
        var writer = new ProtoWriter(64 * 1024);

        // ExportProfilesServiceRequest.resource_profiles = 1
        writer.WriteMessage(1, resourceProfiles =>
        {
            // ResourceProfiles.resource = 1
            resourceProfiles.WriteMessage(1, resource =>
            {
                foreach (var attribute in resourceAttributes)
                {
                    // Resource.attributes = 1 -> KeyValue { key = 1, value = 2 }
                    resource.WriteMessage(1, keyValue =>
                    {
                        keyValue.WriteString(1, attribute.Key);
                        keyValue.WriteMessage(2, anyValue => anyValue.WriteString(1, attribute.Value));
                    });
                }
            });

            // ResourceProfiles.scope_profiles = 2
            resourceProfiles.WriteMessage(2, scopeProfiles =>
            {
                // ScopeProfiles.scope = 1 -> InstrumentationScope { name = 1, version = 2 }
                scopeProfiles.WriteMessage(1, scope =>
                {
                    scope.WriteString(1, scopeName);
                    scope.WriteString(2, scopeVersion);
                });

                foreach (var profile in this.profiles)
                {
                    scopeProfiles.WriteMessage(2, p => WriteProfile(p, profile));
                }
            });
        });

        // ExportProfilesServiceRequest.dictionary = 2
        writer.WriteMessage(2, this.WriteDictionary);

        return writer.ToArray();
    }

    private static void WriteProfile(ProtoWriter writer, ProfileEntry profile)
    {
        // Profile.sample_type = 1 -> ValueType { type_strindex = 1, unit_strindex = 2 }
        writer.WriteMessage(1, valueType =>
        {
            valueType.WriteInt32(1, profile.SampleTypeIndex);
            valueType.WriteInt32(2, profile.SampleUnitIndex);
        });

        foreach (var sample in profile.Samples)
        {
            // Profile.samples = 2
            writer.WriteMessage(2, s =>
            {
                s.WriteInt32(1, sample.StackIndex);
                s.WritePackedInt32(2, sample.AttributeIndices);
                s.WriteInt32(3, sample.LinkIndex);
                s.WritePackedInt64(4, new[] { sample.Value });
            });
        }

        writer.WriteFixed64(3, profile.TimeUnixNano);
        writer.WriteUInt64(4, profile.DurationNano);
        writer.WriteMessage(5, periodType =>
        {
            periodType.WriteInt32(1, profile.PeriodTypeIndex);
            periodType.WriteInt32(2, profile.PeriodUnitIndex);
        });
        writer.WriteInt64(6, profile.Period);
    }

    private static void WriteBigEndian(byte[] target, int offset, long value)
    {
        for (var i = 0; i < 8; i++)
        {
            target[offset + i] = (byte)(value >> (8 * (7 - i)));
        }
    }

    private void WriteDictionary(ProtoWriter dictionary)
    {
        // mapping_table = 1. Index 0 is the zero value; index 1 is the synthetic runtime mapping.
        dictionary.WriteMessage(1, _ => { });
        dictionary.WriteMessage(1, mapping => mapping.WriteInt32(4, this.mappingFilenameIndex));

        // location_table = 2
        for (var i = 0; i < this.locationFunctionIndices.Count; i++)
        {
            var functionIdx = this.locationFunctionIndices[i];
            if (i == 0)
            {
                dictionary.WriteMessage(2, _ => { });
                continue;
            }

            dictionary.WriteMessage(2, location =>
            {
                location.WriteInt32(1, 1);
                location.WriteMessage(3, line => line.WriteInt32(1, functionIdx));
                location.WritePackedInt32(4, new[] { this.frameTypeAttributeIndex });
            });
        }

        // function_table = 3
        for (var i = 0; i < this.functionNameIndices.Count; i++)
        {
            var nameIdx = this.functionNameIndices[i];
            dictionary.WriteMessage(3, function => function.WriteInt32(1, nameIdx));
        }

        // link_table = 4
        for (var i = 0; i < this.linkTraceIds.Count; i++)
        {
            var traceId = this.linkTraceIds[i];
            var spanId = this.linkSpanIds[i];
            dictionary.WriteMessage(4, link =>
            {
                if (traceId.Length > 0)
                {
                    link.WriteBytes(1, traceId);
                    link.WriteBytes(2, spanId);
                }
            });
        }

        // string_table = 5
        foreach (var value in this.strings)
        {
            dictionary.WriteStringAlways(5, value);
        }

        // attribute_table = 6 -> KeyValueAndUnit { key_strindex = 1, value = 2 }
        for (var i = 0; i < this.attributes.Count; i++)
        {
            var (keyIndex, value) = this.attributes[i];
            if (i == 0)
            {
                dictionary.WriteMessage(6, _ => { });
                continue;
            }

            dictionary.WriteMessage(6, attribute =>
            {
                attribute.WriteInt32(1, keyIndex);
                attribute.WriteMessage(2, anyValue => anyValue.WriteString(1, value));
            });
        }

        // stack_table = 7
        foreach (var stack in this.stacks)
        {
            dictionary.WriteMessage(7, s => s.WritePackedInt32(1, stack));
        }
    }

    private int AddString(string value)
    {
        if (this.stringIndex.TryGetValue(value, out var existing))
        {
            return existing;
        }

        this.strings.Add(value);
        var index = this.strings.Count - 1;
        this.stringIndex[value] = index;
        return index;
    }

    private int AddFunction(string name)
    {
        if (this.functionIndex.TryGetValue(name, out var existing))
        {
            return existing;
        }

        this.functionNameIndices.Add(this.AddString(name));
        var index = this.functionNameIndices.Count - 1;
        this.functionIndex[name] = index;
        return index;
    }

    private int AddLocation(string frame)
    {
        if (this.locationIndex.TryGetValue(frame, out var existing))
        {
            return existing;
        }

        this.locationFunctionIndices.Add(this.AddFunction(frame));
        var index = this.locationFunctionIndices.Count - 1;
        this.locationIndex[frame] = index;
        return index;
    }

    private int AddStack(List<string> frames)
    {
        var locations = new List<int>(frames.Count);
        foreach (var frame in frames)
        {
            locations.Add(this.AddLocation(frame));
        }

        var key = string.Join(",", locations);
        if (this.stackIndex.TryGetValue(key, out var existing))
        {
            return existing;
        }

        this.stacks.Add(locations);
        var index = this.stacks.Count - 1;
        this.stackIndex[key] = index;
        return index;
    }

    private int AddLink(long traceIdHigh, long traceIdLow, long spanId)
    {
        var key = (traceIdHigh, traceIdLow, spanId);
        if (this.linkIndex.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // The native side derives these from the W3C hex: trace id high is the first 8 bytes and
        // low the last 8, each big-endian, and the span id likewise. Write them back big-endian so
        // the ids match the trace they belong to. BitConverter would reverse them on x64 and arm64.
        var traceId = new byte[16];
        WriteBigEndian(traceId, 0, traceIdHigh);
        WriteBigEndian(traceId, 8, traceIdLow);
        var span = new byte[8];
        WriteBigEndian(span, 0, spanId);

        this.linkTraceIds.Add(traceId);
        this.linkSpanIds.Add(span);
        var index = this.linkTraceIds.Count - 1;
        this.linkIndex[key] = index;
        return index;
    }

    private int AddAttribute(string key, string value)
    {
        var cacheKey = (key, value);
        if (this.attributeIndex.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        this.attributes.Add((this.AddString(key), value));
        var index = this.attributes.Count - 1;
        this.attributeIndex[cacheKey] = index;
        return index;
    }

    private sealed class AggregatedSample
    {
        internal AggregatedSample(int stackIndex, int linkIndex, List<int> attributeIndices)
        {
            this.StackIndex = stackIndex;
            this.LinkIndex = linkIndex;
            this.AttributeIndices = attributeIndices;
        }

        internal int StackIndex { get; }

        internal int LinkIndex { get; }

        internal List<int> AttributeIndices { get; }

        internal long Value { get; set; }
    }

    private sealed class ProfileEntry
    {
        internal int SampleTypeIndex { get; init; }

        internal int SampleUnitIndex { get; init; }

        internal int PeriodTypeIndex { get; init; }

        internal int PeriodUnitIndex { get; init; }

        internal long Period { get; init; }

        internal ulong TimeUnixNano { get; init; }

        internal ulong DurationNano { get; init; }

        internal List<AggregatedSample> Samples { get; init; } = new();
    }
}
