using Jellyfin.Plugin.AiTranslate.Providers;
using Xunit;

namespace Jellyfin.Plugin.AiTranslate.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void GeminiModelsAreSortedByUse()
    {
        string[] gen = ["generateContent", "countTokens"];
        var lists = ModelCatalog.ClassifyGemini(
        [
            ("models/gemini-3.8-flash", gen),
            ("models/gemini-3.5-flash-lite", gen),
            ("models/gemini-3.5-transcribe", gen),
            ("models/gemini-3.5-transcribe-live", gen),
            ("models/gemini-3.8-flash-tts", gen),
            ("models/gemini-3.8-flash-lite-tts", gen),
            ("models/gemini-3.1-flash-lite-image", gen),
            ("models/gemini-embedding-001", ["embedContent"]),
            ("models/imagen-4.0-generate-001", ["predict"]),
        ]);

        Assert.Equal(["gemini-3.8-flash", "gemini-3.5-flash-lite"], lists.Text);
        Assert.Equal(["gemini-3.5-transcribe"], lists.Transcribe);
        Assert.Equal(["gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts"], lists.Tts);
    }

    [Fact]
    public void OpenAiModelsAreSortedByUse()
    {
        var lists = ModelCatalog.ClassifyOpenAi(
        [
            "gpt-6.1-sol", "gpt-6-luna", "o5-mini", "gpt-4o-mini-tts", "tts-1-hd", "whisper-1", "gpt-transcribe",
            "gpt-4o-realtime-preview", "gpt-image-2", "text-embedding-3-large", "gpt-4o-audio-preview", "dall-e-3",
        ]);

        Assert.Equal(["o5-mini", "gpt-6.1-sol", "gpt-6-luna"], lists.Text);
        Assert.Equal(["whisper-1", "gpt-transcribe"], lists.Transcribe);
        Assert.Equal(["tts-1-hd", "gpt-4o-mini-tts"], lists.Tts);
    }
}
