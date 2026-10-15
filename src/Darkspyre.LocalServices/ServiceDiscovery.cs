using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>Public discovery identity. The capability is a separate owner-only file, not part of this record.</summary>
/// <param name="Service">The fixed service family.</param>
/// <param name="ProtocolVersion">The supported wire protocol version.</param>
/// <param name="Epoch">The service process epoch.</param>
/// <param name="StoreId">The durable store identity.</param>
/// <param name="BuildDigest">The verified service package digest.</param>
/// <param name="LayoutId">The installed layout identity.</param>
public sealed record ServiceDiscovery(ServiceFamily Service, int ProtocolVersion, Guid Epoch, Guid StoreId,
    string BuildDigest, string LayoutId)
{
    /// <summary>The epoch-specific capability filename.</summary>
    [JsonIgnore] public string CapabilityName => $"{this.Epoch:N}.key";
}

internal static class ServiceDiscoveryMetadata
{
    internal const string FileName = "service.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    internal static ServiceDiscovery? Read(byte[]? bytes)
    {
        if (bytes is null) return null;
        ServiceDiscovery discovery = JsonSerializer.Deserialize<ServiceDiscovery>(bytes, Options)
            ?? throw new InvalidDataException("The discovery file has no service identity.");
        Validate(discovery);
        return discovery;
    }

    internal static byte[] Serialize(ServiceDiscovery discovery)
    {
        Validate(discovery);
        return JsonSerializer.SerializeToUtf8Bytes(discovery, Options);
    }

    internal static void Validate(ServiceDiscovery discovery)
    {
        if (!Enum.IsDefined(discovery.Service) || discovery.ProtocolVersion < 1 || discovery.Epoch == Guid.Empty || discovery.StoreId == Guid.Empty
            || discovery.BuildDigest is not { Length: 64 } || !discovery.BuildDigest.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(discovery.LayoutId) || discovery.LayoutId.Length > 128 || discovery.LayoutId.Any(char.IsControl))
            throw new InvalidDataException("The discovery identity is invalid.");
    }
}
