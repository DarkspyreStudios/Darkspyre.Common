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

/// <summary>A safe protocol failure with an optional diagnostic cause.</summary>
public sealed class ServiceProtocolException : IOException
{
    /// <summary>Creates a safe protocol error.</summary>
    public ServiceProtocolException(ServiceErrorCode code) : this(code, $"Local service protocol failed: {code}.") { }
    internal ServiceProtocolException(ServiceErrorCode code, string message, Exception? inner = null) : base(message, inner) => this.Code = code;
    /// <summary>The stable failure kind.</summary>
    public ServiceErrorCode Code { get; }
}
