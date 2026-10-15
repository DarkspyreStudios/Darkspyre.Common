using RoyalTerminal.Terminal;

namespace Darkspyre.Common.Avalonia.Terminal;

/// <summary>
/// The terminal's session when the host owns the process: output the host writes goes to the screen through
/// <see cref="DataReceived"/>, and the screen's encoded keystrokes and size changes come back to the host.
/// </summary>
internal sealed class HostTransport(Action<ReadOnlyMemory<byte>> input, Action<TerminalSessionDimensions> resized) : ITerminalTransport
{
    /// <summary>The identity the options and the provider share.</summary>
    public const string Id = "darkspyre-host";

    public event Action<byte[], int>? DataReceived;

    // The host process ends separately; the terminal never learns of an exit from this transport.
    public event Action<int>? ProcessExited
    {
        add { }
        remove { }
    }

    public bool IsRunning { get; private set; }

    /// <summary>Delivers the host process's output to the screen.</summary>
    public void Deliver(byte[] output) => this.DataReceived?.Invoke(output, output.Length);

    public ValueTask StartAsync(ITerminalTransportOptions options, CancellationToken cancellationToken = default)
    {
        this.IsRunning = true;
        return ValueTask.CompletedTask;
    }

    public void SendInput(ReadOnlySpan<byte> utf8) => input(utf8.ToArray());

    public void Resize(TerminalSessionDimensions dimensions) => resized(dimensions);

    public ValueTask StopAsync()
    {
        this.IsRunning = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose() => this.IsRunning = false;

    /// <summary>The options that select this transport.</summary>
    internal sealed record Options(TerminalSessionDimensions Dimensions) : ITerminalTransportOptions
    {
        public string TransportId => Id;
    }

    /// <summary>A factory that hands the terminal this one transport.</summary>
    internal sealed class Factory(HostTransport transport) : ITerminalTransportFactory
    {
        public ITerminalTransport Create(ITerminalTransportOptions options) =>
            options.TransportId == Id ? transport : throw new NotSupportedException($"Transport {options.TransportId} is not offered.");
    }
}
