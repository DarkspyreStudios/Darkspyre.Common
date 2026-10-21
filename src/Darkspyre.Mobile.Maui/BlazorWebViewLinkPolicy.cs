using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.ApplicationModel;

namespace Darkspyre.Mobile.Maui;

/// <summary>Keeps a BlazorWebView on the app's own pages and opens a tapped link outside the app.</summary>
public static class BlazorWebViewLinkPolicy
{
    /// <summary>For every URL outside the app's origin, sets <see cref="UrlLoadingStrategy.CancelLoad"/>; an http, https
    /// or mailto URL is then opened with <see cref="Launcher.Default"/>. Every other scheme, including tel:, other apps'
    /// schemes and the app's own redirect scheme, opens nothing.</summary>
    public static BlazorWebView UseDarkspyreLinkPolicy(this BlazorWebView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.UrlLoading += (_, loading) =>
        {
            // The web view marks a URL of the app's own origin OpenInWebView before raising the event.
            if (loading.UrlLoadingStrategy == UrlLoadingStrategy.OpenInWebView) return;
            loading.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            if (loading.Url.Scheme is "http" or "https" or "mailto") _ = Launcher.Default.OpenAsync(loading.Url);
        };
        return view;
    }
}
