using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiTranslate.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // --- Keys (stored in Jellyfin's plugin configuration on the server) ---

    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Gemini API key (Google AI Studio).</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    // --- Active providers: one per function by default; only providers with a key can be chosen ---

    /// <summary>Gets or sets the provider used for translation ("gemini", "openai").</summary>
    public string ActiveTranslator { get; set; } = "gemini";

    /// <summary>Gets or sets the provider used for speech recognition ("gemini", "openai").</summary>
    public string ActiveTranscriber { get; set; } = "gemini";

    /// <summary>Gets or sets a value indicating whether the subtitle search lists every provider with a key (to compare), not only the active ones.</summary>
    public bool CompareProviders { get; set; }

    // --- Providers allowed when comparing ---

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

    // --- Voice-over (dubbing) ---

    public string GeminiTtsModel { get; set; } = "gemini-3.8-flash-tts";

    /// <summary>Gets or sets the voice provider used by default ("gemini" or "openai").</summary>
    public string DubProvider { get; set; } = "gemini";

    public string OpenAiTtsModel { get; set; } = "gpt-4o-mini-tts";

    /// <summary>Gets or sets the OpenAI TTS requests-per-minute pace (the model allows 2000 at the first paid tier).</summary>
    public int OpenAiTtsRequestsPerMinute { get; set; } = 300;

    /// <summary>Gets or sets the estimated OpenAI TTS price per minute of speech (audio output $12 per 1M tokens).</summary>
    public double PriceOpenAiTtsPerMinute { get; set; } = 0.015;

    public double PriceOpenAiTtsInputPerMTok { get; set; } = 0.6;

    /// <summary>Gets or sets the prebuilt voice (Charon, Kore, Puck, Zephyr, …).</summary>
    public string DubVoice { get; set; } = "Charon";

    /// <summary>Gets or sets a value indicating whether lines get a male or female voice by the pitch of the original speaker.</summary>
    public bool DubSplitByGender { get; set; } = true;

    /// <summary>Gets or sets the voice for women's lines (DubVoice is used for men and when undecided).</summary>
    public string DubVoiceFemale { get; set; } = "Kore";

    /// <summary>Gets or sets the pitch below which a speaker counts as male (Hz).</summary>
    public double DubMaleBelowHz { get; set; } = 165;

    /// <summary>Gets or sets the pitch above which a speaker counts as female (Hz).</summary>
    public double DubFemaleAboveHz { get; set; } = 185;

    /// <summary>Gets or sets the delivery style sent with every line.</summary>
    public string DubStyle { get; set; } = "calm Bulgarian TV voice-over narrator, neutral and clear, slightly fast";

    /// <summary>Gets or sets a value indicating whether lines that are only song lyrics (♪ … ♪) are left to the original audio.</summary>
    public bool DubSkipSongs { get; set; } = true;

    /// <summary>Gets or sets how much a line may be sped up to fit its time slot.</summary>
    public double DubMaxTempo { get; set; } = 1.3;

    /// <summary>Gets or sets how many lines are synthesized at the same time.</summary>
    public int DubParallelism { get; set; } = 3;

    /// <summary>Gets or sets the TTS requests-per-minute limit of the account's tier (Gemini Tier 1: 10). Requests are paced to it.</summary>
    public int GeminiTtsRequestsPerMinute { get; set; } = 10;

    /// <summary>Gets or sets the TTS requests-per-day limit of the account's tier (Gemini Tier 1: 100).</summary>
    public int GeminiTtsRequestsPerDay { get; set; } = 100;

    /// <summary>Gets or sets how many subtitle lines are spoken in one TTS request (split again by the pauses between them).</summary>
    public int DubLinesPerRequest { get; set; } = 10;

    /// <summary>Gets or sets the marker placed between lines in one request so the voice pauses there.</summary>
    public string DubLineSeparator { get; set; } = " <long pause> ";

    /// <summary>Gets or sets the TTS text input price (USD per 1M tokens).</summary>
    public double PriceGeminiTtsInputPerMTok { get; set; } = 0.5;

    /// <summary>Gets or sets the TTS audio output price (USD per 1M tokens; 25 tokens per second). Google doubles it from 2027-01-01.</summary>
    public double PriceGeminiTtsAudioPerMTok { get; set; } = 9.0;

    // --- Disk ---

    /// <summary>Gets or sets after how many days without use a spoken line of a voice-over is deleted (0 = keep).</summary>
    public int DubCacheDays { get; set; } = 30;

    /// <summary>Gets or sets the most space the spoken lines may take, in MB (0 = no limit); the least recently used go first.</summary>
    public int DubCacheMaxMb { get; set; } = 2000;
}
