// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Minimal protobuf wire writer, enough to serialize OTLP profiles. Hand-written on purpose: no
/// published OpenTelemetry .NET package compiles the profiles proto, so the alternative is adding
/// Google.Protobuf and a code-generation step to the distro's default load path for one message
/// tree. Field numbers follow opentelemetry-proto v1.10.0.
/// </summary>
internal sealed class ProtoWriter
{
    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;

    private byte[] buffer;
    private int length;

    internal ProtoWriter(int capacity = 4096)
    {
        this.buffer = new byte[capacity];
    }

    internal int Length => this.length;

    internal byte[] ToArray()
    {
        var result = new byte[this.length];
        Array.Copy(this.buffer, result, this.length);
        return result;
    }

    internal void WriteInt32(int field, int value)
    {
        if (value == 0)
        {
            return;
        }

        this.WriteTag(field, WireVarint);

        // Negative int32 is sign-extended to 10 bytes on the wire, matching protobuf.
        this.WriteVarint(value < 0 ? (ulong)(long)value : (ulong)value);
    }

    internal void WriteInt64(int field, long value)
    {
        if (value == 0)
        {
            return;
        }

        this.WriteTag(field, WireVarint);
        this.WriteVarint((ulong)value);
    }

    internal void WriteUInt64(int field, ulong value)
    {
        if (value == 0)
        {
            return;
        }

        this.WriteTag(field, WireVarint);
        this.WriteVarint(value);
    }

    internal void WriteFixed64(int field, ulong value)
    {
        if (value == 0)
        {
            return;
        }

        this.WriteTag(field, WireFixed64);
        this.EnsureCapacity(8);
        for (var i = 0; i < 8; i++)
        {
            this.buffer[this.length++] = (byte)(value >> (8 * i));
        }
    }

    internal void WriteString(int field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        this.WriteBytes(field, bytes);
    }

    /// <summary>
    /// Writes a string field even when empty, for a repeated string table where index position
    /// carries meaning and the entry at index 0 is the empty string.
    /// </summary>
    internal void WriteStringAlways(int field, string value)
    {
        this.WriteBytes(field, Encoding.UTF8.GetBytes(value));
    }

    internal void WriteBytes(int field, byte[] value)
    {
        this.WriteTag(field, WireLengthDelimited);
        this.WriteVarint((ulong)value.Length);
        this.EnsureCapacity(value.Length);
        Array.Copy(value, 0, this.buffer, this.length, value.Length);
        this.length += value.Length;
    }

    /// <summary>
    /// Writes a submessage, always emitting the tag so a present-but-default message (the
    /// zero-value entry every OTLP dictionary table needs at index 0) survives serialization.
    /// </summary>
    internal void WriteMessage(int field, Action<ProtoWriter> write)
    {
        var nested = new ProtoWriter(256);
        write(nested);
        this.WriteBytes(field, nested.ToArray());
    }

    internal void WritePackedInt32(int field, IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var nested = new ProtoWriter(values.Count * 2);
        foreach (var value in values)
        {
            nested.WriteVarint(value < 0 ? (ulong)(long)value : (ulong)value);
        }

        this.WriteBytes(field, nested.ToArray());
    }

    internal void WritePackedInt64(int field, IReadOnlyList<long> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var nested = new ProtoWriter(values.Count * 4);
        foreach (var value in values)
        {
            nested.WriteVarint((ulong)value);
        }

        this.WriteBytes(field, nested.ToArray());
    }

    private void WriteTag(int field, int wireType)
    {
        this.WriteVarint((ulong)((field << 3) | wireType));
    }

    private void WriteVarint(ulong value)
    {
        this.EnsureCapacity(10);
        while (value >= 0x80)
        {
            this.buffer[this.length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        this.buffer[this.length++] = (byte)value;
    }

    private void EnsureCapacity(int extra)
    {
        if (this.length + extra <= this.buffer.Length)
        {
            return;
        }

        var size = this.buffer.Length;
        while (size < this.length + extra)
        {
            size *= 2;
        }

        Array.Resize(ref this.buffer, size);
    }
}
