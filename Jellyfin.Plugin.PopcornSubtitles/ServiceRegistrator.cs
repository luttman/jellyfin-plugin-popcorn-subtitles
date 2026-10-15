using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<PopcornApi>();
        services.AddSingleton<SubtitleCatalog>();
        var missing = new[] { typeof(IMediaSourceManager), typeof(ISubtitleEncoder) }
            .Where(type => services.LastOrDefault(s => s.ServiceType == type) is null).ToArray();
        if (missing.Length > 0)
        {
            services.AddSingleton<IHostedService>(provider => new MissingServicesWarning(provider.GetRequiredService<ILogger<ServiceRegistrator>>(), missing));
            return;
        }
        Decorate<IMediaSourceManager>(services, (provider, inner) =>
            new PopcornMediaSourceManager(inner, provider.GetRequiredService<SubtitleCatalog>(), provider.GetRequiredService<ILibraryManager>()));
        Decorate<ISubtitleEncoder>(services, (provider, inner) =>
            new PopcornSubtitleEncoder(inner, provider.GetRequiredService<IMediaSourceManager>(), provider.GetRequiredService<SubtitleCatalog>()));
    }

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> factory) where T : class
    {
        var original = services.LastOrDefault(s => s.ServiceType == typeof(T));
        if (original is null) return;
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(T), provider =>
        {
            var inner = (T)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!));
            return factory(provider, inner);
        }, original.Lifetime));
    }

    private sealed class MissingServicesWarning(ILogger logger, Type[] missing) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning("Popcorn Subtitles playback integration is disabled: Jellyfin services are missing: {Services}", string.Join(", ", missing.Select(t => t.Name)));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
