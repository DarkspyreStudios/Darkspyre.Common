using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Reads one bounded frame at a time. The connection owner must serialize writes and discard the connection
/// after a failed or cancelled frame operation because a partial frame cannot be retried on the same stream.
/// </summary>
public static class ServiceFrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Returns null only for a clean EOF before the next header. Truncated frames are errors.</summary>
    public static async ValueTask<ServiceMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[sizeof(uint)];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > ServiceProtocol.MaximumFrameBytes)
            throw new InvalidDataException("The service frame length is outside the protocol limit.");

        byte[] body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceMessage message = JsonSerializer.Deserialize<ServiceMessage>(body, JsonOptions)
                ?? throw new InvalidDataException("The service frame has no message.");
            ValidateChunk(message);
            return message;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The service frame is not a valid protocol message.", error);
        }
    }

    /// <summary>Serializes and bounds the entire message before emitting its header.</summary>
    public static async ValueTask WriteAsync(Stream stream, ServiceMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateChunk(message);
        using var body = new FrameBuffer();
        JsonSerializer.Serialize<ServiceMessage>(body, message, JsonOptions);
        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)body.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateChunk(ServiceMessage message)
    {
        if (message is BlobChunkMessage chunk && (chunk.Data is null || chunk.Data.Length > ServiceProtocol.MaximumChunkBytes))
            throw new InvalidDataException("The service blob chunk exceeds the decoded chunk limit or has no data.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter<ServiceFamily>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ServiceResultStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<StoreHealth>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ServiceErrorCode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class FrameBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckSize(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckSize(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            CheckSize(1);
            base.WriteByte(value);
        }

        private void CheckSize(int count)
        {
            if (count > ServiceProtocol.MaximumFrameBytes - Position)
                throw new InvalidDataException("The serialized service message exceeds the frame limit.");
        }
    }
}
