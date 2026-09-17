using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public partial class ApiKeyTests
{
    [GeneratedRegex("^crm_live_[0-9A-Za-z]{32}$")]
    private static partial Regex KeyFormat();

    [Fact]
    public void UT050_generated_keys_match_the_format_and_prefixes_are_distinct()
    {
        var generated = Enumerable.Range(0, 1000).Select(_ => ApiKey.Generate("sdk", "user-1")).ToList();

        generated.Should().OnlyContain(g => KeyFormat().IsMatch(g.Plaintext));
        generated.Should().OnlyContain(g => g.Entity.Prefix == g.Plaintext.Substring(ApiKey.KeyPrefix.Length, ApiKey.PrefixLength));
        generated.Select(g => g.Entity.Prefix).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void UT051_entity_keeps_only_the_sha256_hash()
    {
        var generated = ApiKey.Generate("sdk", "user-1");

        generated.Entity.KeyHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(generated.Plaintext)));
        typeof(ApiKey).GetProperties().Select(p => p.Name).Should().NotContain(["Plaintext", "Key", "Secret"]);
        generated.Entity.Verify(generated.Plaintext).Should().Be(ApiKeyVerification.Valid);
    }

    [Fact]
    public void UT051_verify_rejects_a_key_with_one_character_changed()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        var last = generated.Plaintext[^1];
        var tampered = generated.Plaintext[..^1] + (last == 'a' ? 'b' : 'a');

        generated.Entity.Verify(tampered).Should().Be(ApiKeyVerification.Invalid);
        generated.Entity.Verify("not-a-key").Should().Be(ApiKeyVerification.Invalid);
    }

    [Fact]
    public void UT051_verify_uses_fixed_time_comparison()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src/Modules/Tenancy/Tenancy.Domain/ApiKey.cs"));
        source.Should().Contain("CryptographicOperations.FixedTimeEquals");
    }

    [Fact]
    public void UT052_revoked_key_is_reported_revoked_even_with_the_right_plaintext()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        generated.Entity.Revoke(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));

        generated.Entity.Verify(generated.Plaintext).Should().Be(ApiKeyVerification.Revoked);
        generated.Entity.RevokedAt.Should().Be(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("crm_live_ABCDEFGH012345678901234567890123", true, "ABCDEFGH")]
    [InlineData("crm_live_short", false, "")]
    [InlineData("crm_test_ABCDEFGH012345678901234567890123", false, "")]
    [InlineData("crm_live_ABCDEFGH01234567890123456789012!", false, "")]
    [InlineData(null, false, "")]
    public void TryParsePrefix_accepts_only_well_formed_keys(string? plaintext, bool expected, string prefix)
    {
        ApiKey.TryParsePrefix(plaintext, out var actual).Should().Be(expected);
        actual.Should().Be(prefix);
    }

    [Fact]
    public void Touch_records_last_use()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        var now = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

        generated.Entity.Touch(now);

        generated.Entity.LastUsedAt.Should().Be(now);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Crm.sln not found above the test directory.");
    }
}
