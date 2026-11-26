using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Enrichment;
using Jellyfin.Plugin.SendToKindle.Settings;
using Jellyfin.Plugin.SendToKindle.WebIntegration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SendToKindle;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RemoteDeliveryClient>();
        serviceCollection.AddSingleton<IKindleSettingsStore, KindleSettingsStore>();
        serviceCollection.AddSingleton(provider => new DeliveryJournal(Path.Combine(
            provider.GetRequiredService<IApplicationPaths>().DataPath, "send-to-kindle", "delivery-journal")));
        serviceCollection.AddSingleton<StandaloneSmtpTransport>();
        serviceCollection.AddSingleton<ISmtpConnectionTester>(provider => provider.GetRequiredService<StandaloneSmtpTransport>());
        serviceCollection.AddSingleton<IStandaloneSmtpTransport>(provider => provider.GetRequiredService<StandaloneSmtpTransport>());
        serviceCollection.AddSingleton<GoogleBooksHttpClient>();
        serviceCollection.AddSingleton(provider => new BookEnricher(provider.GetRequiredService<GoogleBooksHttpClient>()));
        serviceCollection.AddSingleton<StandaloneDelivery>();
        serviceCollection.AddSingleton<DeliveryRouter>();
        serviceCollection.AddSingleton<IDeliveryClient>(provider => provider.GetRequiredService<DeliveryRouter>());
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<DeliveryRouter>());

        serviceCollection.AddSingleton<WebIntegrationStatus>();
        serviceCollection.AddSingleton<IWebIntegrationStatus>(provider => provider.GetRequiredService<WebIntegrationStatus>());
        serviceCollection.AddHostedService<FileTransformationRegistrationService>();
    }

    private sealed class GoogleBooksHttpClient() : HttpClient(RemoteDeliveryClient.CreateTransport())
    {
    }
}
