using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MaRogAlly.Core;

public static partial class Text
{
    /// <summary>Clé de comparaison : minuscules, sans accents, sans ™®©, sans ponctuation.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) || c is '-' or '_' or ':') sb.Append(' ');
        }
        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Nettoie un titre pour l'affichage et la recherche en ligne.</summary>
    public static string CleanTitle(string s)
        => MultiSpace().Replace(s.Replace("™", "").Replace("®", "").Replace("©", ""), " ").Trim();

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = BreakTags().Replace(html, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Titres à essayer pour une recherche en ligne, du plus fidèle au plus simplifié :
    /// titre nettoyé, puis mots collés séparés et mentions annexes retirées, puis sans sous-titre.
    /// </summary>
    public static IEnumerable<string> SearchVariants(string title)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Le cache d'Epic remplace espaces insécables et accents par « ? » (« Trine?2 ») : Steam ne s'y retrouve pas.
        var clean = MultiSpace().Replace(CleanTitle(title).Replace('?', ' '), " ").Trim();
        if (seen.Add(clean)) yield return clean;

        var split = CamelCase().Replace(clean, " ");                       // KillingFloor2Beta → Killing Floor 2 Beta
        var simplified = MultiSpace().Replace(NoiseWords().Replace(split, " "), " ").Trim(' ', '-', ':', '–');
        if (simplified.Length >= 3 && seen.Add(simplified)) yield return simplified;

        var cut = simplified.IndexOfAny([':', '–']) is var i and > 3 ? simplified[..i] : simplified.Split(" - ")[0];
        cut = cut.Trim();
        if (cut.Length >= 3 && seen.Add(cut)) yield return cut;
    }

    /// <summary>Initiale utilisée pour le saut alphabétique (# pour les chiffres et symboles).</summary>
    public static string Initial(string title)
    {
        var n = Normalize(title);
        return n.Length > 0 && char.IsLetter(n[0]) ? char.ToUpperInvariant(n[0]).ToString() : "#";
    }

    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])")] private static partial Regex CamelCase();
    [GeneratedRegex(@"\b(beta|demo|playtest|trial|preview|early access|test server|pts|(game of the year|goty|definitive|deluxe|complete|ultimate|enhanced|anniversary|standard|premium|gold|special) edition|edition|director'?s cut|remastered)\b|\(.*?\)",
        RegexOptions.IgnoreCase)] private static partial Regex NoiseWords();
    [GeneratedRegex(@"<\s*(br|/p|/li|/h\d)\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\n\s*\n+")] private static partial Regex BlankLines();
}
