using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Errors;

/// <summary>A region failure handed to the application's log.</summary>
/// <param name="Region">The failed region's name, or <see cref="UiRegion.NoRegion"/> when no region enclosed the failure.</param>
/// <param name="Scope">What the region showed when it failed, such as a session id, or null.</param>
/// <param name="Error">The failure.</param>
public sealed record UiFailure(string Region, string? Scope, Exception Error);

/// <summary>
/// One region of a window. A failure inside it, reported through <see cref="Report"/> or thrown while its content is
/// laid out, replaces the region with a failure card offering Reload this area and the error details. The rest of the
/// window keeps working. A failure belongs to the <see cref="Scope"/> that was showing: another scope shows the content
/// again, and the failed scope shows its card again until Reload this area.
/// </summary>
public sealed class UiRegion : ContentControl
{
    /// <summary>The <see cref="UiFailure.Region"/> of a failure no region encloses.</summary>
    public const string NoRegion = "none";

    // Stands for a null scope in the failure map.
    private static readonly object NoScope = new();
    private readonly Func<Control> build;
    private readonly string region;
    private readonly Dictionary<object, Exception> failedScopes = [];
    private object? scope;
    private bool failed;

    /// <summary>Creates the region <paramref name="region"/> whose content <paramref name="build"/> creates, now and on each reload.</summary>
    public UiRegion(string region, Func<Control> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNull(build);
        this.region = region;
        this.build = build;
        this.Classes.Add("ui-region");
        this.Reload();
    }

    /// <summary>Receives every failure a region shows or no region encloses. Null logs nothing.</summary>
    /// <gotcha>The application sets it once at startup; it is called on the UI thread and must not throw.</gotcha>
    public static Action<UiFailure>? Log { get; set; }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>What the region shows now, such as a session id, or null. A failure belongs to the scope that was showing.</summary>
    public object? Scope
    {
        get => this.scope;
        set
        {
            if (Equals(this.scope, value)) return;
            this.scope = value;
            if (this.failedScopes.TryGetValue(this.CurrentScope, out Exception? failure)) this.ShowFailure(failure);
            else if (this.failed) this.Rebuild();
        }
    }

    private object CurrentScope => this.scope ?? NoScope;

    /// <summary>Shows <paramref name="error"/> in the nearest region enclosing <paramref name="source"/>, or logs it when none does.</summary>
    public static void Report(Visual source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(error);
        if (source.FindAncestorOfType<UiRegion>(includeSelf: true) is { } owner) owner.Fail(error);
        else Log?.Invoke(new UiFailure(NoRegion, null, error));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        try { return base.MeasureOverride(availableSize); }
        catch (Exception error) when (!this.failed)
        {
            this.Fail(error);
            return default;
        }
    }

    // A failure can arrive while the content is attached or laid out, such as a view whose first refresh fails inside
    // its OnAttachedToVisualTree. Replacing the content then breaks Avalonia's walk over the tree it is attaching or
    // measuring, so the failure is recorded now and the card replaces the content in a later dispatcher job.
    private void Fail(Exception error)
    {
        this.failedScopes[this.CurrentScope] = error;
        Log?.Invoke(new UiFailure(this.region, this.scope?.ToString(), error));
        Dispatcher.UIThread.Post(() =>
        {
            // A reload or another scope chosen in between leaves nothing to show.
            if (this.failedScopes.TryGetValue(this.CurrentScope, out Exception? failure)) this.ShowFailure(failure);
        });
    }

    private void ShowFailure(Exception error)
    {
        this.failed = true;
        this.Content = new FailureView(string.Empty, "This area stopped working.",
            [("Area", this.region), ("Exception", error.GetType().FullName ?? error.GetType().Name), ("Message", error.Message)],
            error.StackTrace, [Forms.Button("Reload this area", "secondary", this.Reload)], kind: "region-failure-card");
    }

    private void Reload()
    {
        this.failedScopes.Remove(this.CurrentScope);
        this.Rebuild();
    }

    private void Rebuild()
    {
        this.failed = false;
        this.Content = this.build();
    }
}
