using System.Text.Json;
using System.Text.Json.Nodes;

namespace Crm.Profiles.Domain;

public sealed record AttributeChange(string Json, IReadOnlyList<string> ChangedKeys);

/// <summary>The Attributes JSON document of a profile. Keys are top-level; a value is replaced whole.</summary>
public static class ProfileAttributes
{
    public const string Empty = "{}";

    /// <summary>FR-10: per key, last write wins. A JSON null removes the key. Keys are reported in the order the traits list them.</summary>
    public static AttributeChange ApplyTraits(string attributesJson, JsonElement traits)
    {
        if (traits.ValueKind != JsonValueKind.Object)
        {
            return new AttributeChange(attributesJson, []);
        }

        var attributes = Parse(attributesJson);
        var changed = new List<string>();
        foreach (var trait in traits.EnumerateObject())
        {
            var exists = attributes.TryGetPropertyValue(trait.Name, out var current);
            if (trait.Value.ValueKind == JsonValueKind.Null)
            {
                if (exists)
                {
                    attributes.Remove(trait.Name);
                    changed.Add(trait.Name);
                }

                continue;
            }

            var incoming = JsonNode.Parse(trait.Value.GetRawText());
            if (exists && JsonNode.DeepEquals(current, incoming))
            {
                continue;
            }

            attributes[trait.Name] = incoming;
            changed.Add(trait.Name);
        }

        return changed.Count == 0 ? new AttributeChange(attributesJson, []) : new AttributeChange(attributes.ToJsonString(), changed);
    }

    /// <summary>FR-09: the loser only contributes keys the winner does not have.</summary>
    public static AttributeChange MergeLoser(string winnerJson, string loserJson)
    {
        var winner = Parse(winnerJson);
        var gained = new List<string>();
        foreach (var (key, value) in Parse(loserJson))
        {
            if (winner.ContainsKey(key))
            {
                continue;
            }

            winner[key] = value?.DeepClone();
            gained.Add(key);
        }

        return gained.Count == 0 ? new AttributeChange(winnerJson, []) : new AttributeChange(winner.ToJsonString(), gained);
    }

    private static JsonObject Parse(string json)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? Empty : json) as JsonObject
           ?? throw new InvalidOperationException("Profile attributes must be a JSON object.");
}
