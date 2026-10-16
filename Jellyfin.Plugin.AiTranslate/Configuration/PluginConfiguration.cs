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

    /// <summary>
    /// Gets or sets the Gemini Transcribe input-token limit per minute of the account's tier (Tier 1: 10 000).
    /// Audio costs 25 tokens per second, so this sets chunk size and pacing.
    /// </summary>
    public int GeminiAudioTokensPerMinute { get; set; } = 10000;

    /// <summary>
    /// Gets or sets the Gemini thinking level for translation ("low", "medium", "high"; empty = model default).
    /// Measured: "low" uses about a third of the output tokens of the default with the same result.
    /// </summary>
    public string GeminiThinkingLevel { get; set; } = "low";

    /// <summary>Gets or sets the OpenAI reasoning effort for translation ("low", "medium", "high"; empty = model default).</summary>
    public string OpenAiReasoningEffort { get; set; } = "low";

    // --- Behaviour ---

    /// <summary>Gets or sets the ISO 639-1 languages (comma separated) offered as AI subtitles. Empty = any language Jellyfin asks for.</summary>
    public string TargetLanguages { get; set; } = "bg";

    /// <summary>
    /// Gets or sets a value indicating whether AI generation is offered only when the film has no subtitles in that language
    /// and the other subtitle providers found none. Finished AI subtitles are always listed.
    /// </summary>
    public bool OnlyWhenNoOtherSubtitles { get; set; } = true;

    // --- Translating existing subtitles (fast and cheap) ---

    /// <summary>Gets or sets a value indicating whether the audio option is hidden when a text subtitle in another language can be translated.</summary>
    public bool PreferExistingSubtitles { get; set; } = true;

    /// <summary>Gets or sets the preferred source languages for translation, best first (ISO 639-1, comma separated).</summary>
    public string SourceLanguages { get; set; } = "en";

    /// <summary>Gets or sets a value indicating whether other subtitle providers are searched for a source in <see cref="SourceLanguages"/> when the film has none.</summary>
    public bool UseOtherProvidersAsSource { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether hearing-impaired annotations ([music], (sighs), JOHN:) are removed before translating.</summary>
    public bool StripHearingImpaired { get; set; } = true;

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
