using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Arcl.Api.Services;

/// <summary>
/// Sign-in from the ARCL website. Its admin-only Pages/Secure/ChatbotDashboard.aspx signs a
/// short-lived token with a secret both sides share (Arcl:SiteSsoSecret here, ArclChatbotSsoSecret
/// in the site's web.config), so a site admin opens the dashboard without typing the admin key.
/// Format: <c>v1.{expiry unix seconds}.{base64url HMAC-SHA256 of "v1.{expiry}"}</c>. The site
/// side must produce exactly this — see README "Integration with the ARCL website".
/// </summary>
public static class SiteSsoToken
{
    /// <summary>Longest lifetime accepted, whatever the token claims — caps the damage of a leaked secret's misuse.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    public static string Create(byte[] secret, DateTimeOffset expires)
    {
        var payload = "v1." + expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return payload + "." + Sign(secret, payload);
    }

    public static bool IsValid(string? token, byte[] secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)) return false;

        var payload = parts[0] + "." + parts[1];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]), Encoding.ASCII.GetBytes(Sign(secret, payload))))
            return false;

        var expires = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(exp, 0, 253402300799));
        return expires > now && expires <= now + MaxLifetime;
    }

    private static string Sign(byte[] secret, string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
