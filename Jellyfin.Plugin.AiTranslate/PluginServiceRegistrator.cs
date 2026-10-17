using System;
using Jellyfin.Plugin.AiTranslate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AiTranslate;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public const string HttpClientName = "AiTranslate";

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Transcribing 20 minutes of audio can take a while.
        serviceCollection.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15));
        serviceCollection.AddSingleton<OpenAiProvider>();
        serviceCollection.AddSingleton<GeminiProvider>();
        serviceCollection.AddSingleton<SubtitleJobService>();
        serviceCollection.AddSingleton<Notifier>();
        serviceCollection.AddSingleton<GeminiTts>();
        serviceCollection.AddSingleton<OpenAiTts>();
        serviceCollection.AddSingleton<ModelCatalog>();
        serviceCollection.AddSingleton<Dubbing.DubService>();
        serviceCollection.AddSingleton<CacheCleaner>();
        serviceCollection.AddSingleton<ISubtitleProvider, AiSubtitleProvider>();
    }
}
