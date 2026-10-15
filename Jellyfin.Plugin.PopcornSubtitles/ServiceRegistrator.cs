using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PopcornSubtitles;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<PopcornApi>();
        services.AddSingleton<SubtitleCatalog>();
        Decorate<IMediaSourceManager>(services, (provider, inner) =>
            new PopcornMediaSourceManager(inner, provider.GetRequiredService<SubtitleCatalog>(), provider.GetRequiredService<ILibraryManager>()));
        Decorate<ISubtitleEncoder>(services, (provider, inner) =>
            new PopcornSubtitleEncoder(inner, provider.GetRequiredService<IMediaSourceManager>(), provider.GetRequiredService<SubtitleCatalog>()));
    }

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> factory) where T : class
    {
        var original = services.Last(s => s.ServiceType == typeof(T));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(T), provider =>
        {
            var inner = (T)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!));
            return factory(provider, inner);
        }, original.Lifetime));
    }
}
