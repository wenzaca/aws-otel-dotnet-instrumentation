// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Decodes the CLR profiler's pause-time-optimized sample buffer: a versioned stream of
/// big-endian integers and UTF-16 length-prefixed strings, with a per-buffer string code table.
/// Ported from the reference parser in opentelemetry-dotnet-instrumentation's
/// TestApplication.ContinuousProfiler.
/// </summary>
internal static class NativeSampleParser
{
    /// <summary>The native sampler's own thread, excluded from every profile.</summary>
    internal const string SamplerThreadName = "OpenTelemetry Continuous Profiler Thread";

    private const byte OpStartBatch = 0x01;
    private const byte OpStartSample = 0x02;
    private const byte OpEndBatch = 0x06;
    private const byte OpBatchStats = 0x07;
    private const byte OpAllocationSample = 0x08;
    private const int SupportedVersion = 1;

    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false);

    internal static List<ProfilerSample> ParseThreadSamples(byte[] buffer, int read)
    {
        var samples = new List<ProfilerSample>();
        var codes = new Dictionary<int, string>();
        var position = 0;
        long batchMillis = 0;

        while (position < read)
        {
            var op = buffer[position++];
            if (op == OpStartBatch)
            {
                if (ReadInt32(buffer, ref position) != SupportedVersion)
                {
                    return samples;
                }

                batchMillis = ReadInt64(buffer, ref position);
            }
            else if (op == OpStartSample)
            {
                var threadName = ReadString(buffer, ref position);
                var traceIdHigh = ReadInt64(buffer, ref position);
                var traceIdLow = ReadInt64(buffer, ref position);
                var spanId = ReadInt64(buffer, ref position);

                var code = ReadInt16(buffer, ref position);
                if (code == 0)
                {
                    continue;
                }

                var sample = new ProfilerSample(batchMillis, traceIdHigh, traceIdLow, spanId, threadName);
                ReadFrames(code, sample, codes, buffer, ref position);

                if (threadName != SamplerThreadName)
                {
                    samples.Add(sample);
                }
            }
            else if (op != OpEndBatch)
            {
                if (op == OpBatchStats)
                {
                    // microsSuspended, numThreads, totalFrames, numCacheMisses
                    position += 16;
                }
                else
                {
                    // Unknown opcode: the rest of the buffer cannot be positioned, so stop here
                    // rather than read garbage.
                    break;
                }
            }
        }

        return samples;
    }

    internal static List<ProfilerSample> ParseAllocationSamples(byte[] buffer, int read)
    {
        var samples = new List<ProfilerSample>();
        var position = 0;

        while (position < read)
        {
            if (buffer[position++] != OpAllocationSample)
            {
                break;
            }

            var timestampMillis = ReadInt64(buffer, ref position);
            var allocatedSize = ReadInt64(buffer, ref position);
            ReadString(buffer, ref position); // allocated type name, folded into the leaf frame below
            var threadName = ReadString(buffer, ref position);
            var traceIdHigh = ReadInt64(buffer, ref position);
            var traceIdLow = ReadInt64(buffer, ref position);
            var spanId = ReadInt64(buffer, ref position);

            var sample = new ProfilerSample(timestampMillis, traceIdHigh, traceIdLow, spanId, threadName)
            {
                AllocationSizeBytes = allocatedSize,
            };

            // Each allocation sample codes its strings independently.
            ReadFrames(ReadInt16(buffer, ref position), sample, new Dictionary<int, string>(), buffer, ref position);

            if (threadName != SamplerThreadName)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private static void ReadFrames(short code, ProfilerSample sample, Dictionary<int, string> codes, byte[] buffer, ref int position)
    {
        while (code != 0)
        {
            string value;
            if (code < 0)
            {
                value = ReadString(buffer, ref position);
                codes[-code] = value;
            }
            else if (!codes.TryGetValue(code, out value!))
            {
                return;
            }

            sample.Frames.Add(value);
            code = ReadInt16(buffer, ref position);
        }
    }

    private static string ReadString(byte[] buffer, ref int position)
    {
        var length = ReadInt16(buffer, ref position);
        var value = Utf16.GetString(buffer, position, length * 2);
        position += length * 2;
        return value;
    }

    private static short ReadInt16(byte[] buffer, ref int position)
    {
        var value = (short)(((buffer[position] & 0xFF) << 8) | (buffer[position + 1] & 0xFF));
        position += 2;
        return value;
    }

    private static int ReadInt32(byte[] buffer, ref int position)
    {
        var value = ((buffer[position] & 0xFF) << 24)
            | ((buffer[position + 1] & 0xFF) << 16)
            | ((buffer[position + 2] & 0xFF) << 8)
            | (buffer[position + 3] & 0xFF);
        position += 4;
        return value;
    }

    private static long ReadInt64(byte[] buffer, ref int position)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | (uint)(buffer[position + i] & 0xFF);
        }

        position += 8;
        return value;
    }
}
