using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Darkspyre.Mobile.Maui;

/// <summary>Registers what every Darkspyre mobile app needs.</summary>
public static partial class DarkspyreMobileSetup
{
    /// <summary>Adds the Blazor WebView and the platform's <see cref="SecureStore.ICredentialStore"/> for
    /// <paramref name="credentialService"/>.</summary>
    /// <param name="builder">The app's builder.</param>
    /// <param name="credentialService">The app's own stable credential service name, such as its bundle id. It addresses
    /// every secret the app has stored, so it never changes.</param>
    public static MauiAppBuilder AddDarkspyreMobile(this MauiAppBuilder builder, string credentialService)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialService);
        builder.Services.AddMauiBlazorWebView();
        AddPlatformCredentials(builder.Services, credentialService);
        return builder;
    }

    /// <summary>Registers this platform's credential store. Each platform folder implements it.</summary>
    private static partial void AddPlatformCredentials(IServiceCollection services, string credentialService);
}
