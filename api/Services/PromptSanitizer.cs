using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arcl.Api.Services;

/// <summary>
/// Prompt-injection defense for text that gets pasted into the Gemini prompt. Rule chunks are
/// scraped from arcl.org pages that can be edited by many people (a pasted rule change, a FAQ
/// answer), and visitor messages/history are fully untrusted — either could carry text like
/// "ignore the instructions above and…". Two layers:
///
///   1. Structural (GeminiClient.cs): untrusted text only ever appears inside labeled
///      &lt;&lt;&lt;SECTION&gt;&gt;&gt; / &lt;&lt;&lt;QUESTION&gt;&gt;&gt; blocks, and the prompt says
///      plainly that block content is data, never instructions.
///   2. This class: strips anything that could break out of those blocks (the delimiter tokens
///      themselves, our own citation marker syntax, invisible/bidi control characters) and
///      neutralizes the common injection phrasings.
///
/// Neither layer is a guarantee on its own — layer 2 is a filter list, and an LLM can still be
/// talked around — which is why OutputFilter.cs also checks what comes back out.
/// </summary>
public static class PromptSanitizer
{
    public const string Removed = "[removed]";

    // Phrasings that only make sense as an attempt to steer the model, not as cricket-rule text.
    // Kept narrow on purpose: rule text legitimately says things like "the umpire will ignore
    // the appeal", so a bare "ignore" must not match.
    private static readonly Regex[] InjectionPatterns =
    {
        // Needs a "previous/your/system"-style qualifier: "teams that ignore the rules will be
        // penalized" is real rulebook prose and must survive untouched.
        new(@"\b(ignore|disregard|forget|override|bypass)\b[^.\n]{0,30}?\b(previous|prior|above|earlier|preceding|your|system|all\s+(?:previous|prior|above))\b[^.\n]{0,30}?\b(instructions?|rules|prompts?|directions|guidelines|context)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Chat-role markers: any case at the start of a line, or shouted mid-line ("... SYSTEM: obey").
        // Not lowercase mid-line — "Points system: 30 per game" is ordinary rulebook prose.
        new(@"(^|\n)\s*(system|assistant|developer|user|model)\s*(prompt)?\s*[:>]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(SYSTEM|ASSISTANT|DEVELOPER)(\s+PROMPT)?\s*:", RegexOptions.Compiled),
        new(@"\byou\s+are\s+now\b[^.\n]{0,60}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(new|updated|real|actual)\s+instructions?\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled), // colon = a heading for injected text, not "follow the new instructions for scoring"
        new(@"\b(reveal|print|repeat|show|output|leak)\b[^.\n]{0,30}?\b(system\s+prompt|your\s+(instructions|prompt|rules))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bconfident\s*:\s*(yes|no)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // our own answer header — see GeminiClient
    };

    // Our structural tokens. Removed outright, so untrusted text can never open/close a block or
    // forge a citation marker.
    private static readonly Regex DelimiterPattern = new(@"<<<|>>>|\[§", RegexOptions.Compiled);

    // A run of mojibake replacement characters — arcl.org serves some pages with literal U+FFFD
    // where smart quotes used to be, which then also gets double-decoded as "ï¿½".
    private static readonly Regex ReplacementCharPattern = new(@"(ï¿½|�)+", RegexOptions.Compiled);

    /// <summary>Cleans one scraped rule chunk. Returns how many injection phrasings were neutralized,
    /// so the caller can log it — a nonzero count on real rulebook text is worth a human look.</summary>
    public static (string Text, int Neutralized) SanitizeContext(string text, int maxChars = 6000)
    {
        var cleaned = CleanCharacters(text);
        var neutralized = 0;
        foreach (var pattern in InjectionPatterns)
        {
            cleaned = pattern.Replace(cleaned, m =>
            {
                neutralized++;
                // Keep a leading newline if the pattern consumed one, so line structure survives.
                return (m.Value.StartsWith('\n') ? "\n" : "") + Removed;
            });
        }
        return (Truncate(cleaned, maxChars), neutralized);
    }

    /// <summary>Cleans visitor-supplied text (the question, and prior turns of history).</summary>
    public static string SanitizeUserText(string text, int maxChars = 1000)
    {
        var cleaned = CleanCharacters(text);
        foreach (var pattern in InjectionPatterns)
            cleaned = pattern.Replace(cleaned, Removed);
        return Truncate(cleaned.Trim(), maxChars);
    }

    private static string CleanCharacters(string text)
    {
        // Between letters, a replacement char was almost always an apostrophe ("don�t");
        // elsewhere, a quote mark ("click �Update Player�").
        text = ReplacementCharPattern.Replace(text, m =>
        {
            var before = m.Index > 0 ? text[m.Index - 1] : ' ';
            var after = m.Index + m.Length < text.Length ? text[m.Index + m.Length] : ' ';
            return char.IsLetter(before) && char.IsLetter(after) ? "'" : "\"";
        });

        // NFKC folds look-alike/full-width characters ("ｉｇｎｏｒｅ") onto plain ASCII so the
        // patterns above can't be dodged with them.
        text = text.Normalize(NormalizationForm.FormKC);

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Drop control characters (except newline/tab) and every "format" character — zero-width
            // spaces/joiners and bidi overrides, the usual way to hide text from a human reviewer.
            if (c is '\n' or '\t') { sb.Append(c); continue; }
            if (char.IsControl(c)) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c);
        }

        return DelimiterPattern.Replace(sb.ToString(), "");
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
