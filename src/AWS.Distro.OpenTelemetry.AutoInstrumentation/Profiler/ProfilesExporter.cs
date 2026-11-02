// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net.Http;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Profiler;

/// <summary>
/// Posts an encoded <c>ExportProfilesServiceRequest</c> over OTLP/HTTP protobuf, optionally
/// signing it in process with SigV4 so no collector hop is needed.
/// </summary>
internal sealed class ProfilesExporter : IDisposable
{
    private const string ProtobufContentType = "application/x-protobuf";

    private readonly ProfilerConfig config;
    private readonly HttpClient client;

    internal ProfilesExporter(ProfilerConfig config)
    {
        this.config = config;

        HttpMessageHandler handler = new HttpClientHandler();
        if (config.SigV4)
        {
            var authConfig = new AwsAuthConfig(config.Endpoint, config.SigV4Region ?? string.Empty, config.SigV4Service);
            handler = new AwsAuthHttpHandler(new AwsAuthHeaderSupplier(authConfig), handler);
        }

        this.client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(config.ExportTimeoutMs),
        };
    }

    public void Dispose() => this.client.Dispose();

    /// <summary>
    /// Sends one request. Returns an error description, or null on success. Never throws: a
    /// profiler failure must not reach the application.
    /// </summary>
    internal string? Export(byte[] payload, CancellationToken cancellationToken)
    {
        if (this.config.MaxPayloadBytes > 0 && payload.Length > this.config.MaxPayloadBytes)
        {
            return "payload of " + payload.Length + " bytes exceeds OTEL_AWS_PROFILER_MAX_PAYLOAD_BYTES="
                + this.config.MaxPayloadBytes + "; window dropped";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, this.config.Endpoint);
            var body = this.config.Gzip ? Gzip(payload) : payload;
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", ProtobufContentType);
            if (this.config.Gzip)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            }

            using var response = this.client.Send(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var detail = ReadBodyPreview(response);
            return "HTTP " + (int)response.StatusCode + (detail.Length > 0 ? " " + detail : string.Empty);
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    // Ingest errors arrive protobuf-framed, so the readable sentence is preceded by a few binary
    // bytes. Keep the printable tail and cap it.
    private static string ReadBodyPreview(HttpResponseMessage response)
    {
        try
        {
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var printable = bytes.Where(b => b >= 0x20 && b < 0x7F).Select(b => (char)b).ToArray();
            var text = new string(printable).Trim();
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
        catch
        {
            return string.Empty;
        }
    }
}
