# AI Translate for Jellyfin

Subtitles for films that have none: Jellyfin extracts the audio, a speech-recognition model writes down
what is said (with timings), and an LLM translates it into your language, as a film subtitler would.

It plugs into Jellyfin's normal **subtitle search**: open a film → **⋮ → Edit subtitles → Search**, and
next to the usual results you get entries like

```
AI · OpenAI whisper-1 + OpenAI gpt-6.1-sol · ≈ $1.05
AI · Google Gemini gemini-3.5-transcribe + Google Gemini gemini-3.8-flash · ≈ $0.68
✓ Готови · openai + gemini · 16.10.2026 22:14          ← already generated, free
```

Nothing is charged until you pick one. Automatic subtitle downloads (library scans) **never** start a paid
job — they only get results that already exist.

## Two ways to get subtitles

**1. Translate an existing subtitle (fast, cheap — preferred).** If the film has a text subtitle in another
language — embedded (MKV/MP4), an external `.srt/.ass/.vtt`, or a result of another Jellyfin subtitle provider
(OpenSubtitles, Subbuzz, …) — its lines are translated with their original timings. Measured: 2250 lines in
88 s for $0.19 (Gemini 3.8 Flash). Hearing-impaired notes (`[music]`, `(sighs)`, `JOHN:`) are removed first,
dialogue keeps one speaker per line. Image subtitles (DVD/Blu-ray) are not used.

**2. Generate from the audio track (slow).** Only offered when no text subtitle exists (configurable).
Measured on a 98-minute film: $0.74 and ~20 minutes with Gemini on Tier 1 rate limits.

By default AI options appear only when the film has no subtitles in that language and the other providers
found none. Finished AI subtitles are always listed (free).

## How the audio path works

1. **Audio**: Jellyfin's ffmpeg extracts the default audio track as mono 16 kHz MP3 chunks (any container
   Jellyfin plays: MKV, MP4, AVI, TS, M2TS, …).
2. **Speech → text with timings**
   - OpenAI `whisper-1` (segment timestamps), or
   - Google `gemini-3.5-transcribe` (word timestamps).
   The transcript is cached: a second translator or another language only pays for the translation.
3. **Translation** in batches of 50 lines with the preceding lines as context, structured JSON output,
   validated line by line (no merged or dropped lines), retried on errors. The film can be in any language;
   if it is already in the target language, translation is skipped.
4. **Subtitles**: lines are wrapped to ≤ 2 × 42 characters and handed to Jellyfin, which saves them next to
   the film (or in its metadata folder) like any downloaded subtitle.

If the client gives up waiting, the job keeps running and Jellyfin still saves the subtitle when it finishes.

## Costs

Measured (October 2026 list prices, editable in the settings):

| Job | Result |
|---|---|
| Translate 2250 English lines → Bulgarian, Gemini 3.8 Flash, thinking `low` | **$0.19**, 88 s |
| Same with the model's default thinking | $0.59, 274 s |
| Audio 98 min → Bulgarian, Gemini Transcribe + Gemini 3.8 Flash | **$0.74**, ~20 min (Tier 1) |

Gemini Transcribe on Tier 1 is limited to 10 000 audio tokens per minute (≈ 6.5 min of audio per minute);
the plugin sizes chunks and paces requests to that limit (configurable when your tier is higher).
Translation uses a low thinking/reasoning level by default: about a third of the billed tokens, same result.

The settings page shows this month's spend per provider/model (audio minutes, tokens) and a **monthly
limit**; a job that would exceed it is refused. The provider's invoice is what counts — the plugin's numbers
are estimates from token counts and list prices.

## Install

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/HA-HUB-I/jellyfin-plugin-ai-translate/main/manifest.json`
2. Catalog → **AI Translate** → Install → restart Jellyfin.
3. Dashboard → Plugins → AI Translate: paste an **OpenAI** and/or **Google Gemini** API key, choose the
   subtitle languages (e.g. `bg`), set a monthly limit.
4. Enable the provider for your libraries: Library → Manage library → *Subtitle downloaders* → **AI Translate**.

Requires Jellyfin 12.2+.

## Privacy

The film's audio is uploaded to the provider you pick (OpenAI or Google) for recognition; subtitle text is
sent for translation. Gemini files are deleted right after use. API keys are stored in Jellyfin's plugin
configuration on your server.

## Roadmap

- AI voice-over / dubbing for selected films (Google Cloud TTS `bg-BG` Chirp3-HD voices, OpenAI TTS) as an
  extra audio track — manual, per film.
- More providers (Deepgram, ElevenLabs, Anthropic, DeepL).

## Български

Добавка за Jellyfin, която генерира субтитри за филми без такива: разпознава говора от аудиото (OpenAI или
Google Gemini) и превежда с езиков модел на български (или друг език). Появява се в търсенето на субтитри
на Jellyfin с прогнозна цена за всеки вариант; таксува се само при избор. Има месечен лимит и дневник на
разходите в настройките.

## License

GPL-3.0, like Jellyfin.
