using System.Security.Cryptography;
using System.Text;

namespace Crm.Segmentation.Domain.Rules;

/// <summary>SHA-256 of a rule tree's canonical JSON form (H20): identifies a `Definition` independent of how it
/// was originally serialized (key order, whitespace, number formatting).</summary>
public static class DefinitionHash
{
    public static byte[] Compute(RuleNode tree) => SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalRuleWriter.Write(tree)));

    public static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);
}
