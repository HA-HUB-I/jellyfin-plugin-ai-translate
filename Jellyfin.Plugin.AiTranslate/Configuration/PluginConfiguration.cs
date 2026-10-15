using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiTranslate.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // --- Keys (stored in Jellyfin's plugin configuration on the server) ---

    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Gemini API key (Google AI Studio).</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    // --- Providers offered in the subtitle search ---

    public bool EnableOpenAiTranscription { get; set; } = true;

    public bool EnableGeminiTranscription { get; set; } = true;

    public bool EnableOpenAiTranslation { get; set; } = true;

    public bool EnableGeminiTranslation { get; set; } = true;

    // --- Models (change when providers release new ones) ---

    /// <summary>Gets or sets the OpenAI transcription model; it must return segment timestamps (whisper-1 does).</summary>
    public string OpenAiTranscribeModel { get; set; } = "whisper-1";

    public string OpenAiTextModel { get; set; } = "gpt-6.1-sol";

    public string GeminiTranscribeModel { get; set; } = "gemini-3.5-transcribe";

    public string GeminiTextModel { get; set; } = "gemini-3.8-flash";

    // --- Behaviour ---

    /// <summary>Gets or sets the ISO 639-1 languages (comma separated) offered as AI subtitles. Empty = any language Jellyfin asks for.</summary>
    public string TargetLanguages { get; set; } = "bg";

    /// <summary>Gets or sets the monthly spending limit in USD (estimated). 0 disables the limit.</summary>
    public double MonthlyBudgetUsd { get; set; } = 10;

    /// <summary>Gets or sets free-form context for the translator (tone, glossary).</summary>
    public string TranslationInstructions { get; set; } = string.Empty;

    // --- Prices used for estimates and the cost log (USD). Update them from the providers' price pages. ---

    public double PriceOpenAiTranscribePerMinute { get; set; } = 0.006;

    public double PriceGeminiTranscribePerMinute { get; set; } = 0.005;

    public double PriceOpenAiTextInputPerMTok { get; set; } = 2.0;

    public double PriceOpenAiTextOutputPerMTok { get; set; } = 10.0;

    public double PriceGeminiTextInputPerMTok { get; set; } = 0.75;

    public double PriceGeminiTextOutputPerMTok { get; set; } = 3.75;
}
