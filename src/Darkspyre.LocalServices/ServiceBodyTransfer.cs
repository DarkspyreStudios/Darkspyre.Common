using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

internal sealed class ServiceBodyTransfer : IDisposable
{
    private readonly Stream stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long? total;
    private long received;
    private int sequence;
    internal ServiceBodyTransfer(Guid transferId, Stream stream)
    {
        if (transferId == Guid.Empty) throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        this.TransferId = transferId;
        this.stream = stream;
    }
    internal Guid TransferId { get; }
    internal bool Complete { get; private set; }
    internal Stream Body => this.Complete ? this.stream : throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
    internal async Task AppendAsync(BlobChunkMessage chunk, CancellationToken token)
    {
        if (this.Complete || chunk.TransferId != this.TransferId || chunk.Sequence != this.sequence
            || chunk.TotalBytes < 0 || (this.total is { } expected && expected != chunk.TotalBytes)
            || chunk.Data.LongLength > chunk.TotalBytes - this.received)
            throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        this.total = chunk.TotalBytes;
        await this.stream.WriteAsync(chunk.Data, token).ConfigureAwait(false);
        this.hash.AppendData(chunk.Data);
        this.received += chunk.Data.LongLength;
        this.sequence = checked(this.sequence + 1);
        if (chunk.FinalSha256 is null) return;
        if (this.received != this.total || !chunk.FinalSha256.AsSpan().SequenceEqual(this.hash.GetHashAndReset()))
            throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        this.stream.Position = 0;
        this.Complete = true;
    }
    internal static Guid ReadId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1
            || !payload.TryGetProperty("bodyTransferId", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || !id.TryGetGuid(out Guid value) || value == Guid.Empty)
            throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        return value;
    }
    internal static JsonElement Reference(Guid transferId) => JsonSerializer.SerializeToElement(new { bodyTransferId = transferId });
    internal static async Task SendAsync(Guid epoch, Guid requestId, Guid transferId, Stream input,
        Func<ServiceMessage, CancellationToken, Task> send, CancellationToken token)
    {
        MemoryStream? buffered = null;
        try
        {
            if (!input.CanSeek)
            {
                buffered = new MemoryStream();
                await input.CopyToAsync(buffered, token).ConfigureAwait(false);
                buffered.Position = 0;
                input = buffered;
            }
            long total = input.Length - input.Position;
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[ServiceProtocol.MaximumChunkBytes];
            long sent = 0;
            int sequence = 0;
            do
            {
                int count = (int)Math.Min(buffer.Length, total - sent);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                digest.AppendData(buffer.AsSpan(0, count));
                sent += count;
                byte[]? final = sent == total ? digest.GetHashAndReset() : null;
                await send(new BlobChunkMessage(epoch, requestId, transferId, sequence++, total, buffer[..count], final), token).ConfigureAwait(false);
            } while (sent < total);
        }
        finally { buffered?.Dispose(); }
    }
    public void Dispose() { this.hash.Dispose(); this.stream.Dispose(); }
}
