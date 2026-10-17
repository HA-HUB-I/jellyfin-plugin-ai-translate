using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AiTranslate.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>AI Translate: subtitles from the audio track (speech recognition + LLM translation) as a Jellyfin subtitle provider.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("4cad7295-fa8b-46c9-a2e0-b6e5abe8a234");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "AI Translate";

    public override Guid Id => PluginId;

    public override string Description =>
        "AI subtitles in your language: translates an existing subtitle or recognises the film's audio (OpenAI, Google Gemini) and translates it with an LLM, in Jellyfin's subtitle search with the price before you choose. Also makes a TV-style AI voice-over track.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "AI Translate",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),

                // Shortcut in the dashboard menu where the web client shows plugin pages.
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "translate",
            }
        ];
    }
}
