using System.Text;
using System.Text.RegularExpressions;

namespace Arcl.Api.Services;

public sealed record OutputFilterResult(string Text, bool Changed, bool PromptLeak);

/// <summary>
/// Last line of defense on what the model says, applied before anything reaches the visitor:
///
///   - Links: only to allow-listed hosts (arcl.org, cricclubs.com by default — Arcl:OutputFilter:
///     AllowedHosts) or to a URL that literally appears in the retrieved rule text. Anything else
///     is either hallucinated or injected, and a chatbot on ARCL's site linking somewhere ARCL
///     never published is the realistic harm here.
///   - Email addresses and phone numbers: same rule — the support address, @arcl.org, or one that
///     appears in the rule text. Everything else is redacted.
///   - Prompt leakage: our own block delimiters or answer header showing up in the answer means
///     the model is echoing its instructions; the whole answer is replaced.
///
/// For streamed answers, CreateStream() filters links/emails token-by-token (a URL or email never
/// contains whitespace, so holding back only the current unfinished word is enough); phone numbers
/// and leak detection need the complete text and are checked once at the end by Filter().
/// </summary>
public sealed class OutputFilter
{
    public const string LinkRemoved = "[link removed]";
    public const string EmailRemoved = "[email removed]";
    public const string PhoneRemoved = "[number removed]";

    private static readonly Regex UrlPattern = new(@"\b(?:https?://|www\.)[^\s<>""'\]\)]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex LeakPattern = new(@"<<<|>>>|\bSECTION\s+id\s*=|\bCONFIDENT\s*:|treat\s+(?:it|everything)\s+(?:inside|in)\s+(?:the\s+)?(?:SECTION|QUESTION)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string[] _allowedHosts;
    private readonly string _supportContact;

    public OutputFilter(IConfiguration config)
    {
        _allowedHosts = config.GetSection("Arcl:OutputFilter:AllowedHosts").Get<string[]>() is { Length: > 0 } hosts
            ? hosts
            : new[] { "arcl.org", "cricclubs.com" };
        _supportContact = config["Arcl:SupportContact"] ?? "";
    }

    public OutputFilterResult Filter(string text, string context)
    {
        if (LeakPattern.IsMatch(text))
            return new OutputFilterResult(text, true, PromptLeak: true);

        var filtered = FilterLinksAndEmails(text, context);
        var contextDigits = Digits(context);
        filtered = PhonePattern.Replace(filtered, m => contextDigits.Contains(Digits(m.Value)) ? m.Value : PhoneRemoved);

        return new OutputFilterResult(filtered, filtered != text, PromptLeak: false);
    }

    public StreamingOutputFilter CreateStream(string context) => new(this, context);

    internal string FilterLinksAndEmails(string text, string context)
    {
        var result = UrlPattern.Replace(text, m => IsAllowedUrl(m.Value, context) ? m.Value : LinkRemoved);
        return EmailPattern.Replace(result, m => IsAllowedEmail(m.Value, context) ? m.Value : EmailRemoved);
    }

    private bool IsAllowedUrl(string url, string context)
    {
        var trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?');
        if (context.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        var withScheme = trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + trimmed : trimmed;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && IsAllowedHost(uri.Host);
    }

    private bool IsAllowedEmail(string email, string context)
    {
        if (email.Equals(_supportContact, StringComparison.OrdinalIgnoreCase)) return true;
        if (context.Contains(email, StringComparison.OrdinalIgnoreCase)) return true;
        return IsAllowedHost(email[(email.IndexOf('@') + 1)..]);
    }

    private bool IsAllowedHost(string host) =>
        _allowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase)
                            || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));

    private static string Digits(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Filters a streamed answer as it arrives. Push() returns the text that is safe to send
/// now; the trailing unfinished word is held back until whitespace (or Flush) completes it.</summary>
public sealed class StreamingOutputFilter
{
    private readonly OutputFilter _filter;
    private readonly string _context;
    private readonly StringBuilder _pending = new();

    internal StreamingOutputFilter(OutputFilter filter, string context)
    {
        _filter = filter;
        _context = context;
    }

    public string Push(string delta)
    {
        _pending.Append(delta);
        var text = _pending.ToString();
        var cut = text.LastIndexOfAny(new[] { ' ', '\n', '\t' });
        if (cut < 0) return "";

        var ready = text[..(cut + 1)];
        _pending.Clear().Append(text[(cut + 1)..]);
        return _filter.FilterLinksAndEmails(ready, _context);
    }

    public string Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return _filter.FilterLinksAndEmails(rest, _context);
    }
}
