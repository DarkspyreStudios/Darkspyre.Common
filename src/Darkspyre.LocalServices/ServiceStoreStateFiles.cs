namespace Darkspyre.LocalServices;

/// <summary>The owner's fixed opaque committed record, separate from transport discovery and startup input.</summary>
internal static class ServiceStoreStateFiles
{
    private const string FileName = "owner-state.json";

    internal static Task<ReadOnlyMemory<byte>?> ReadAsync(ServiceFileSystem files, string storePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ServiceDirectory store = files.OpenExistingPrivateDirectory(storePath)
            ?? throw new DirectoryNotFoundException("The owned store directory is missing.");
        byte[]? bytes = store.ReadPrivateFile(FileName, maximumBytes: null);
        return Task.FromResult<ReadOnlyMemory<byte>?>(bytes is null ? (ReadOnlyMemory<byte>?)null : bytes.AsMemory());
    }

    internal static Task RemoveOwnerFolderAsync(ServiceFileSystem files, string ownerPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The caller holds the inspection lease and suppresses subscriptions to this exact retired owner.
        using (ServiceDirectory? directory = files.OpenExistingPrivateDirectory(ownerPath))
        {
            if (directory is null) return Task.CompletedTask;
        }
        files.RemoveTree(ownerPath);
        return Task.CompletedTask;
    }

    internal static Task PublishAsync(ServiceFileSystem files, string storePath, ReadOnlyMemory<byte> state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ServiceDirectory store = files.OpenExistingPrivateDirectory(storePath)
            ?? throw new DirectoryNotFoundException("The owned store directory is missing.");
        // The caller supplies owned bytes, not untrusted discovery. Cancellation cannot interrupt replacement after it starts.
        store.PublishPrivateFile(FileName, state.Span, maximumBytes: null);
        return Task.CompletedTask;
    }
}
