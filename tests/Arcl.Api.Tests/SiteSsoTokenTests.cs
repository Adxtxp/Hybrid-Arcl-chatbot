using System.Text;
using Arcl.Api.Services;

namespace Arcl.Api.Tests;

public class SiteSsoTokenTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret");
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_a_current_token() =>
        Assert.True(SiteSsoToken.IsValid(SiteSsoToken.Create(Secret, Now.AddHours(8)), Secret, Now));

    [Fact]
    public void Rejects_an_expired_token() =>
        Assert.False(SiteSsoToken.IsValid(SiteSsoToken.Create(Secret, Now.AddSeconds(-1)), Secret, Now));

    [Fact]
    public void Rejects_a_token_longer_than_the_max_lifetime() =>
        Assert.False(SiteSsoToken.IsValid(SiteSsoToken.Create(Secret, Now.AddDays(30)), Secret, Now));

    [Fact]
    public void Rejects_a_token_signed_with_another_secret() =>
        Assert.False(SiteSsoToken.IsValid(SiteSsoToken.Create(Encoding.UTF8.GetBytes("other"), Now.AddHours(1)), Secret, Now));

    [Fact]
    public void Rejects_a_token_whose_expiry_was_edited()
    {
        var parts = SiteSsoToken.Create(Secret, Now.AddHours(1)).Split('.');
        var tampered = $"{parts[0]}.{long.Parse(parts[1]) + 3600}.{parts[2]}";
        Assert.False(SiteSsoToken.IsValid(tampered, Secret, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.notanumber.sig")]
    [InlineData("v2.1.sig")]
    public void Rejects_malformed_tokens(string? token) =>
        Assert.False(SiteSsoToken.IsValid(token, Secret, Now));
}
