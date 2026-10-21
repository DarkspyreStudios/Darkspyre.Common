using Darkspyre.OAuthBroker.Client;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace Darkspyre.Mobile.Maui;

/// <summary>Runs <see cref="BrokerSignIn"/>'s browser step through MAUI's <see cref="WebAuthenticator"/>.</summary>
/// <remarks>
/// Pass one instance as both the browser launcher and the callback factory of one <see cref="BrokerSignIn"/>.
/// <see cref="OpenAsync"/> starts the system sign-in sheet on the main thread and returns without waiting for it; the
/// callback's <see cref="ISignInCallback.WaitAsync"/> waits for the sheet and returns the redirect's query parameters.
/// When the person cancels the sheet, <see cref="ISignInCallback.WaitAsync"/> throws
/// <see cref="OperationCanceledException"/>.
/// </remarks>
/// <gotcha>The token passed to WaitAsync cannot dismiss the system sign-in sheet; the person closes it.</gotcha>
/// <param name="redirect">The app's own redirect, such as <c>darkspyre-phone://auth/callback</c>. Its scheme ends the
/// sheet.</param>
public sealed class WebAuthenticatorSignIn(Uri redirect) : IBrowserLauncher, ISignInCallbackFactory
{
    private Task<WebAuthenticatorResult>? authentication;

    /// <inheritdoc />
    public Task<ISignInCallback> CreateAsync(string expectedState, CancellationToken cancellationToken) =>
        Task.FromResult<ISignInCallback>(new Callback(this, redirect));

    /// <inheritdoc />
    public Task OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        // BrokerSignIn calls this off the main thread, and MAUI's iOS sheet needs the main thread.
        this.authentication = MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(url, redirect));
        return Task.CompletedTask;
    }

    private sealed class Callback(WebAuthenticatorSignIn owner, Uri redirect) : ISignInCallback
    {
        public Uri RedirectUri { get; } = redirect;

        public async Task<IReadOnlyDictionary<string, string>> WaitAsync(CancellationToken cancellationToken)
        {
            Task<WebAuthenticatorResult> pending = owner.authentication
                ?? throw new InvalidOperationException("The sign-in sheet has not been opened.");
            // A cancelled sheet throws TaskCanceledException, which is an OperationCanceledException.
            WebAuthenticatorResult result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result.Properties;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
