namespace Arcl.Api.Services;

/// <summary>
/// IConfiguration's indexer returns "" (not null) for a key that exists but is blank — which is
/// exactly what every secret looks like in the committed appsettings.json (ConnectionString,
/// GeminiApiKey ship as "" there on purpose, meant to be overridden by user-secrets or an env var
/// at runtime). A plain `config["X"] ?? Environment.GetEnvironmentVariable(...)` fallback chain
/// never reaches the environment variable in that case, because "" isn't null — it just silently
/// hands SqlConnection/GeminiClient an empty string instead of failing loudly or falling back.
/// GetNonEmpty treats blank/whitespace-only the same as missing so the fallback chain actually
/// works.
/// </summary>
public static class ConfigExtensions
{
    public static string? GetNonEmpty(this IConfiguration config, string key)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}