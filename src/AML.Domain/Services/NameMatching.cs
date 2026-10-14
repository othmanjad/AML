using System.Globalization;
using System.Text;

namespace AML.Domain.Services;

/// <summary>
/// Arabic / Latin name normalization: alef/hamza forms, taa marbuta/haa, alef maqsura/yaa, diacritics and tatweel,
/// titles/honorifics, connectors (bin, ibn, bint, abu, al-) and legal-form indicators.
/// The original name is never modified; normalization only produces comparison keys.
/// </summary>
public static class ArabicNameNormalizer
{
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "dr", "doctor", "eng", "engineer", "sheikh", "shaikh", "sheik", "shaykh", "haj", "hajj", "hajji",
        "prince", "princess", "sayed", "sayyid", "sir",
        "شيخ", "الشيخ", "دكتور", "الدكتور", "د", "م", "مهندس", "المهندس", "حاج", "الحاج", "السيد", "سيد", "السيدة", "الامير", "امير"
    };

    private static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "ibn", "bint", "ben", "b", "al", "el", "aal", "ul", "ad", "ar", "as", "at", "an", "az", "ash",
        "بن", "ابن", "بنت", "ال", "اال"
    };

    private static readonly HashSet<string> LegalForms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ltd", "limited", "llc", "inc", "co", "corp", "company", "plc", "est", "establishment", "group", "sa", "sal", "fze", "fzco",
        "شركه", "مؤسسه", "موسسه", "ذمم", "محدوده", "مساهمه", "عامه"
    };

    public static bool ContainsArabic(string? s) => !string.IsNullOrEmpty(s) && s.Any(c => c >= '؀' && c <= 'ۿ');

    /// <summary>Normalizes Arabic letters (without removing words).</summary>
    public static string NormalizeArabicLetters(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch is >= 'ً' and <= 'ٟ' || ch == 'ٰ' || ch == 'ـ') continue; // diacritics, superscript alef, tatweel
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ء' => ' ',
                _ => ch
            });
        }
        return sb.ToString();
    }

    public static string NormalizeLatin(string input)
    {
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Full normalization into comparison tokens (titles, connectors and legal forms removed).</summary>
    public static List<string> Tokens(string? name, bool removeLegalForms = true)
    {
        if (string.IsNullOrWhiteSpace(name)) return new List<string>();
        var s = NormalizeLatin(NormalizeArabicLetters(name));
        var cleaned = new StringBuilder(s.Length);
        foreach (var ch in s) cleaned.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var raw = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var tokens = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            if (Titles.Contains(t)) continue;
            if (removeLegalForms && LegalForms.Contains(t)) continue;
            // Join "abd"/"عبد" with the following token (Abd Allah == Abdullah).
            if ((t == "abd" || t == "abdel" || t == "abdul" || t == "abdal" || t == "عبد") && i + 1 < raw.Count)
            {
                var next = raw[i + 1];
                if (next.StartsWith("ال")) next = next[2..];
                tokens.Add((t == "عبد" ? "عبدال" : "abdal") + StripLatinArticle(next));
                i++;
                continue;
            }
            if (Connectors.Contains(t)) continue;
            // Arabic family prefix "آل" normalized to "ال" as a separate word is already removed; strip attached definite article.
            if (t.StartsWith("ال") && t.Length > 3 && !t.StartsWith("الله")) t = t[2..];
            if (t.StartsWith("عبد") && t.Length > 3 && !t.StartsWith("عبدال")) t = "عبدال" + t[3..].TrimStart('ا', 'ل');
            tokens.Add(NormalizeLatinAbd(t));
        }
        return tokens;
    }

    private static string StripLatinArticle(string t) =>
        t.StartsWith("al") && t.Length > 4 ? t[2..] : t.StartsWith("el") && t.Length > 4 ? t[2..] : t;

    private static string NormalizeLatinAbd(string t)
    {
        foreach (var p in new[] { "abdul", "abdel", "abdal", "abdol" })
            if (t.StartsWith(p) && t.Length > p.Length + 1) return "abdal" + StripLatinArticle(t[p.Length..]);
        if (t.StartsWith("abdu") && t.Length > 5) return "abdal" + t[4..];
        if (t.StartsWith("abd") && t.Length > 4 && !"aeiou".Contains(t[3])) return "abdal" + t[3..];
        return t;
    }

    /// <summary>Search key stored for customers/watchlist entries: normalized tokens joined by space, Arabic and Latin variants concatenated with '|'.</summary>
    public static string SearchKey(params string?[] names)
    {
        var parts = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => string.Join(' ', Tokens(n))).Where(p => p.Length > 0);
        return string.Join("|", parts);
    }

    /// <summary>Consonant skeleton used to compare across Arabic and Latin scripts (transliteration-variant tolerant).</summary>
    public static string Skeleton(string token)
    {
        var sb = new StringBuilder();
        if (ContainsArabic(token))
        {
            foreach (var ch in token)
            {
                var m = ch switch
                {
                    'ب' => "b", 'ت' => "t", 'ث' => "t", 'ج' => "j", 'ح' => "h", 'خ' => "k", 'د' => "d", 'ذ' => "d",
                    'ر' => "r", 'ز' => "z", 'س' => "s", 'ش' => "s", 'ص' => "s", 'ض' => "d", 'ط' => "t", 'ظ' => "z",
                    'غ' => "g", 'ف' => "f", 'ق' => "k", 'ك' => "k", 'ل' => "l", 'م' => "m", 'ن' => "n", 'ه' => "h",
                    _ => ""
                };
                sb.Append(m);
            }
        }
        else
        {
            var t = token.ToLowerInvariant()
                .Replace("kh", "k").Replace("gh", "g").Replace("sh", "s").Replace("th", "t").Replace("dh", "d")
                .Replace("ph", "f").Replace("ck", "k").Replace('q', 'k').Replace('c', 'k').Replace('x', 'k')
                .Replace('v', 'f').Replace('p', 'b').Replace('g', 'j').Replace("jj", "j");
            foreach (var ch in t)
                if (!"aeiouyw'".Contains(ch) && char.IsLetter(ch)) sb.Append(ch);
        }
        // Arabic ghain mapped to g; harmonize with latin g->j above
        var s = sb.ToString().Replace('g', 'j');
        // collapse doubles
        var collapsed = new StringBuilder();
        foreach (var ch in s)
            if (collapsed.Length == 0 || collapsed[^1] != ch) collapsed.Append(ch);
        var result = collapsed.ToString();
        if (result.Length > 2 && result.EndsWith('h')) result = result[..^1];
        return result;
    }

    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;
        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = s2Matches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;
        double t = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) t++;
            k++;
        }
        t /= 2.0;
        double m = matches;
        var jaro = (m / s1.Length + m / s2.Length + (m - t) / m) / 3.0;
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }
}

public sealed record NameMatchResult(decimal Score, string Method, string Explanation);

/// <summary>Pluggable name matching interface (an in-house Arabic/Latin engine can replace the default).</summary>
public interface INameMatcher
{
    NameMatchResult Match(string subjectName, string candidateName);
}

public sealed class DefaultNameMatcher : INameMatcher
{
    public NameMatchResult Match(string subjectName, string candidateName)
    {
        var a = ArabicNameNormalizer.Tokens(subjectName);
        var b = ArabicNameNormalizer.Tokens(candidateName);
        if (a.Count == 0 || b.Count == 0) return new NameMatchResult(0, "None", "Empty name after normalization");

        var (shorter, longer) = a.Count <= b.Count ? (a, b) : (b, a);
        var used = new bool[longer.Count];
        double total = 0;
        var parts = new List<string>();
        var methods = new HashSet<string>();
        foreach (var t in shorter)
        {
            double best = 0;
            var bestIdx = -1;
            var bestMethod = "";
            for (var j = 0; j < longer.Count; j++)
            {
                if (used[j]) continue;
                var (sim, method) = TokenSimilarity(t, longer[j]);
                if (sim > best) { best = sim; bestIdx = j; bestMethod = method; }
            }
            if (bestIdx >= 0)
            {
                used[bestIdx] = true;
                parts.Add($"{t}~{longer[bestIdx]}={best:0.00}({bestMethod})");
                methods.Add(bestMethod);
            }
            total += best;
        }
        // Penalize unmatched tokens in the longer name lightly (extra middle names are common).
        var unmatched = longer.Count - shorter.Count;
        var score = total / shorter.Count;
        score *= Math.Pow(0.95, unmatched);
        if (shorter.Count == 1 && longer.Count > 2) score *= 0.85;
        var dec = Math.Round((decimal)score, 4);
        return new NameMatchResult(dec, string.Join("+", methods.OrderBy(x => x)),
            $"Normalized tokens [{string.Join(' ', a)}] vs [{string.Join(' ', b)}]; " + string.Join("; ", parts) +
            (unmatched > 0 ? $"; {unmatched} unmatched token(s) penalized" : ""));
    }

    private static (double, string) TokenSimilarity(string x, string y)
    {
        if (x == y) return (1.0, "exact-normalized");
        var xa = ArabicNameNormalizer.ContainsArabic(x);
        var ya = ArabicNameNormalizer.ContainsArabic(y);
        var sx = ArabicNameNormalizer.Skeleton(x);
        var sy = ArabicNameNormalizer.Skeleton(y);
        if (xa == ya)
        {
            var jw = ArabicNameNormalizer.JaroWinkler(x, y);
            if (sx.Length > 0 && sx == sy) return (Math.Max(jw, 0.95), "phonetic-skeleton");
            return (jw, "jaro-winkler");
        }
        if (sx.Length == 0 || sy.Length == 0) return (0, "transliteration");
        if (sx == sy) return (0.96, "transliteration-skeleton");
        return (ArabicNameNormalizer.JaroWinkler(sx, sy) * 0.92, "transliteration-fuzzy");
    }
}
