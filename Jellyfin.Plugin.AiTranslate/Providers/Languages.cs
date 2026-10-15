using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AiTranslate.Providers;

public static class Languages
{
    /// <summary>Maps whisper's language names ("english") to ISO 639-1 ("en").</summary>
    public static string? FromEnglishName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Length == 2)
        {
            return name.ToLowerInvariant();
        }

        return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.TwoLetterISOLanguageName.Length == 2 && string.Equals(c.EnglishName, name, StringComparison.OrdinalIgnoreCase))
            ?.TwoLetterISOLanguageName;
    }

    /// <summary>English name of a language code ("bg" or "bul") for prompts, e.g. "Bulgarian".</summary>
    public static string EnglishName(string code)
    {
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => string.Equals(c.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));
        return culture?.EnglishName ?? code;
    }

    /// <summary>Normalizes "bul"/"bg"/"BG" to "bg".</summary>
    public static string ToTwoLetter(string code)
    {
        if (code.Length == 2)
        {
            return code.ToLowerInvariant();
        }

        // ISO 639-2/B codes that differ from the terminology codes .NET knows.
        string normalized = code.ToLowerInvariant() switch
        {
            "ger" => "deu",
            "fre" => "fra",
            "chi" => "zho",
            "cze" => "ces",
            "dut" => "nld",
            "gre" => "ell",
            "rum" => "ron",
            "slo" => "slk",
            "alb" => "sqi",
            "mac" => "mkd",
            "per" => "fas",
            "arm" => "hye",
            "geo" => "kat",
            "ice" => "isl",
            "may" => "msa",
            "bur" => "mya",
            "wel" => "cym",
            "baq" => "eus",
            var other => other,
        };
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.ThreeLetterISOLanguageName == normalized)?.TwoLetterISOLanguageName ?? code.ToLowerInvariant();
    }
}
