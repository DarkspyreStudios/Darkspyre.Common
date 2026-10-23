using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>An authenticated transport. Subscription and operation/context admission remain separate steps.</summary>
public sealed class ServicePeer : IAsyncDisposable
{
    internal ServicePeer(Stream stream, HelloMessage hello, WelcomeMessage welcome)
    {
        this.Stream = stream;
        this.Hello = hello;
        this.Welcome = welcome;
    }
    /// <summary>The authenticated transport owned by this peer.</summary>
    public Stream Stream { get; }
    /// <summary>The authenticated client identity.</summary>
    public HelloMessage Hello { get; }
    /// <summary>The negotiated service and connection identity.</summary>
    public WelcomeMessage Welcome { get; }
    /// <summary>Closes the owned transport.</summary>
    public ValueTask DisposeAsync() => this.Stream.DisposeAsync();
}

/// <summary>A protocol failure with an optional diagnostic cause.</summary>
public sealed class ServiceProtocolException : IOException
{
    /// <summary>Creates a protocol error of <paramref name="code"/>.</summary>
    public ServiceProtocolException(ServiceErrorCode code) : this(code, $"Local service protocol failed: {code}.") { }

    /// <summary>Creates the protocol error a service reported, followed by its reason when it gave one.</summary>
    public ServiceProtocolException(ServiceError error)
        : this((error ?? throw new ArgumentNullException(nameof(error))).Code, error.Message is { } reason
            ? $"Local service protocol failed: {error.Code}. The service failed {reason}"
            : $"Local service protocol failed: {error.Code}.") { }

    internal ServiceProtocolException(ServiceErrorCode code, string message, Exception? inner = null) : base(message, inner) => this.Code = code;
    /// <summary>The stable failure kind.</summary>
    public ServiceErrorCode Code { get; }
}
