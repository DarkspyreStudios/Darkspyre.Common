using Microsoft.Extensions.DependencyInjection;
using SecureStore;

namespace Darkspyre.Mobile.Maui;

// The iOS credential store: the app's device-only Keychain.
public static partial class DarkspyreMobileSetup
{
    private static partial void AddPlatformCredentials(IServiceCollection services, string credentialService) =>
        services.AddSecureStore(options =>
        {
            options.ServiceName = credentialService;
            options.Fallback = () => new IosKeychainCredentialStore(options);
        });
}
