using System.Runtime.InteropServices;
using Darkspyre.Agent.Common.UI.Errors;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>How large an attachment preview is shown.</summary>
public enum AttachmentPreviewSize
{
    /// <summary>A small fixed-size thumbnail, as the composer and secondary output show it.</summary>
    Thumbnail,

    /// <summary>The transcript image: scaled down to the transcript's width, never above its stored size.</summary>
    Display,
}

/// <summary>A decoded preview image, with its pixel size when the source records one.</summary>
/// <param name="Bitmap">The decoded image the view shows.</param>
/// <param name="Width">The image width in pixels, or null when the source records none.</param>
/// <param name="Height">The image height in pixels, or null when the source records none.</param>
public sealed record PreviewImage(Bitmap Bitmap, int? Width, int? Height);

/// <summary>
/// The decoded attachment preview images one view shows. A row that shows an image holds a lease on it; rows that show
/// the same source share one image. The image is decoded when the first lease is taken and disposed when the last
/// lease is released, so only shown rows hold images: a row removed by paging, a session change or a queue settlement
/// releases its lease. Disposing the images disposes every image left.
/// </summary>
/// <remarks>
/// The last release does not dispose at once: the image is dropped by the next background job on the UI thread, and a
/// lease taken before that job runs reuses the decoded image. A row that is redrawn releases its old view's lease and
/// takes the new view's in the same layout pass, so the redraw does not decode the image again.
/// The captured owner reads retained or draft image bytes. This cache never reads a source file, chooses a
/// conversation, checks permissions or fetches a remote URL.
/// </remarks>
public sealed class AttachmentPreviewImages : IDisposable
{
    private readonly Func<object, CancellationToken, Task<(ReadOnlyMemory<byte> Data, int? Width, int? Height)?>> readImage;
    private readonly object gate = new();
    private readonly Dictionary<object, Entry> entries = [];
    private bool disposed;

    /// <summary>Creates the view's lazy image leases using its captured owner's authorized read.</summary>
    /// <param name="readImage">Reads the bytes and existing dimensions for an immutable owner-bound key.</param>
    public AttachmentPreviewImages(Func<object, CancellationToken, Task<(ReadOnlyMemory<byte> Data, int? Width, int? Height)?>> readImage) =>
        this.readImage = readImage;

    /// <summary>How many sources have a lease now.</summary>
    internal int Leased
    {
        get { lock (this.gate) return this.entries.Count; }
    }

    /// <summary>
    /// A lease on the image of <paramref name="source"/>. Its <see cref="Lease.Image"/> completes with the image, or with
    /// null when the preview is no longer reachable, holds no image, or cannot be decoded.
    /// </summary>
    public Lease Acquire(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (this.gate)
        {
            if (this.disposed) return new Lease(this, source, null);
            if (!this.entries.TryGetValue(source, out Entry? entry))
            {
                entry = new Entry();
                entry.Image = this.MakeAsync(source, entry.Stop.Token);
                this.entries.Add(source, entry);
            }
            entry.Leases++;
            return new Lease(this, source, entry);
        }
    }

    private void Release(object source, Entry entry)
    {
        lock (this.gate)
        {
            if (--entry.Leases > 0) return;
        }
        Dispatcher.UIThread.Post(() => this.DropWhenUnused(source, entry), DispatcherPriority.Background);
    }

    // Runs after the layout pass that released the lease: a lease taken meanwhile keeps the entry.
    private void DropWhenUnused(object source, Entry entry)
    {
        lock (this.gate)
        {
            if (entry.Leases > 0 || !this.entries.TryGetValue(source, out Entry? current) || current != entry) return;
            this.entries.Remove(source);
        }
        _ = Drop(entry);
    }

    private async Task<PreviewImage?> MakeAsync(object source, CancellationToken cancellationToken)
    {
        // Acquisition holds the gate; the read starts after Acquire returns.
        await Task.Yield();
        try
        {
            (ReadOnlyMemory<byte> Data, int? Width, int? Height)? loaded = await this.readImage(source, cancellationToken);
            if (loaded is not { } image || cancellationToken.IsCancellationRequested) return null;
            using MemoryStream stream = StreamOf(image.Data);
            return new PreviewImage(new Bitmap(stream), image.Width, image.Height);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            UiRegion.Log?.Invoke(new UiFailure("attachment-preview", null, error));
            return null;
        }
    }

    private static MemoryStream StreamOf(ReadOnlyMemory<byte> data) =>
        MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);

    // An image decoded after its last lease was released is disposed once it arrives.
    private static async Task Drop(Entry entry)
    {
        await entry.Stop.CancelAsync();
        (await entry.Image.ConfigureAwait(false))?.Bitmap.Dispose();
        entry.Stop.Dispose();
    }

    /// <summary>Cancels pending image loads and disposes every image left. Disposing more than once has no further effect.</summary>
    public void Dispose()
    {
        Entry[] leased;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            leased = [.. this.entries.Values];
            this.entries.Clear();
        }
        foreach (Entry entry in leased) _ = Drop(entry);
    }

    internal sealed class Entry
    {
        public int Leases;
        public CancellationTokenSource Stop { get; } = new();
        public Task<PreviewImage?> Image { get; set; } = Task.FromResult<PreviewImage?>(null);
    }

    /// <summary>One row's hold on an image. Disposing it more than once releases it once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly AttachmentPreviewImages images;
        private readonly object source;
        private Entry? entry;

        internal Lease(AttachmentPreviewImages images, object source, Entry? entry)
        {
            this.images = images;
            this.source = source;
            this.entry = entry;
            this.Image = entry?.Image ?? Task.FromResult<PreviewImage?>(null);
        }

        /// <summary>The decoded image, or null when it cannot be shown.</summary>
        public Task<PreviewImage?> Image { get; }

        /// <summary>Releases the hold on the image.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.entry, null) is { } held) this.images.Release(this.source, held);
        }
    }
}
