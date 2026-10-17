using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Reads and writes one bounded frame at a time. A frame is a four-byte little-endian length, a kind byte and a
/// payload. Kind 0 carries a UTF-8 JSON <see cref="ServiceMessage"/>. Kind 1 carries a <see cref="BlobChunkMessage"/>
/// as a binary header followed by the raw chunk bytes. The connection owner must serialize writes and discard the
/// connection after a failed or cancelled frame operation because a partial frame cannot be retried on the same stream.
/// </summary>
public static class ServiceFrameCodec
{
    private const byte MessageKind = 0;
    private const byte ChunkKind = 1;
    private const int GuidBytes = 16;
    private const int DigestBytes = 32;
    // Epoch, RequestId and TransferId (16 each), Sequence (4), TotalBytes (8) and Final (1).
    private const int ChunkHeaderBytes = (3 * GuidBytes) + sizeof(int) + sizeof(long) + 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Returns null only for a clean EOF before the next header. Truncated frames are errors.</summary>
    public static async ValueTask<ServiceMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(uint) + 1];
        if (await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is < 2 or > ServiceProtocol.MaximumFrameBytes + 1)
            throw new InvalidDataException("The service frame length is outside the protocol limit.");
        int payloadLength = (int)length - 1;
        return prefix[sizeof(uint)] switch
        {
            MessageKind => await ReadMessageAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false),
            ChunkKind => await ReadChunkAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("The service frame kind is not a protocol frame kind."),
        };
    }

    /// <summary>Encodes and bounds the entire message before emitting its header.</summary>
    public static async ValueTask WriteAsync(Stream stream, ServiceMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message is BlobChunkMessage chunk)
        {
            ValidateChunk(chunk);
            byte[] header = new byte[sizeof(uint) + 1 + ChunkHeaderBytes + (chunk.FinalSha256 is null ? 0 : DigestBytes)];
            WritePrefix(header, header.Length - sizeof(uint) + chunk.Data.Length, ChunkKind);
            Span<byte> fields = header.AsSpan(sizeof(uint) + 1);
            chunk.Epoch.TryWriteBytes(fields[..GuidBytes]);
            chunk.RequestId.TryWriteBytes(fields.Slice(GuidBytes, GuidBytes));
            chunk.TransferId.TryWriteBytes(fields.Slice(2 * GuidBytes, GuidBytes));
            BinaryPrimitives.WriteInt32LittleEndian(fields[(3 * GuidBytes)..], chunk.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(fields[((3 * GuidBytes) + sizeof(int))..], chunk.TotalBytes);
            fields[ChunkHeaderBytes - 1] = chunk.FinalSha256 is null ? (byte)0 : (byte)1;
            chunk.FinalSha256?.CopyTo(fields[ChunkHeaderBytes..]);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var body = new FrameBuffer();
        JsonSerializer.Serialize<ServiceMessage>(body, message, JsonOptions);
        byte[] prefix = new byte[sizeof(uint) + 1];
        WritePrefix(prefix, checked((int)body.Length + 1), MessageKind);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ServiceMessage> ReadMessageAsync(Stream stream, int payloadLength, CancellationToken cancellationToken)
    {
        byte[] body = new byte[payloadLength];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ServiceMessage>(body, JsonOptions)
                ?? throw new InvalidDataException("The service frame has no message.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The service frame is not a valid protocol message.", error);
        }
    }

    private static async ValueTask<BlobChunkMessage> ReadChunkAsync(Stream stream, int payloadLength, CancellationToken cancellationToken)
    {
        if (payloadLength < ChunkHeaderBytes)
            throw new InvalidDataException("The service blob chunk frame is shorter than its header.");
        byte[] header = new byte[ChunkHeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        byte final = header[ChunkHeaderBytes - 1];
        if (final > 1 || (final == 1 && payloadLength < ChunkHeaderBytes + DigestBytes))
            throw new InvalidDataException("The service blob chunk header is not valid.");
        byte[]? digest = null;
        if (final == 1)
        {
            digest = new byte[DigestBytes];
            await stream.ReadExactlyAsync(digest, cancellationToken).ConfigureAwait(false);
        }
        byte[] data = new byte[payloadLength - ChunkHeaderBytes - (digest?.Length ?? 0)];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        var chunk = new BlobChunkMessage(
            new Guid(header.AsSpan(0, GuidBytes)),
            new Guid(header.AsSpan(GuidBytes, GuidBytes)),
            new Guid(header.AsSpan(2 * GuidBytes, GuidBytes)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(3 * GuidBytes)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan((3 * GuidBytes) + sizeof(int))),
            data,
            digest);
        ValidateChunk(chunk);
        return chunk;
    }

    private static void WritePrefix(byte[] prefix, int length, byte kind)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)length));
        prefix[sizeof(uint)] = kind;
    }

    private static void ValidateChunk(BlobChunkMessage chunk)
    {
        if (chunk.Data is null || chunk.Data.Length > ServiceProtocol.MaximumChunkBytes)
            throw new InvalidDataException("The service blob chunk exceeds the chunk limit or has no data.");
        if (chunk.FinalSha256 is { Length: not DigestBytes })
            throw new InvalidDataException("The service blob chunk digest is not a SHA-256 digest.");
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
