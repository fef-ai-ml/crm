using System.Text.Json;
using Crm.Segmentation.Domain.Segments;

namespace Crm.Segmentation.Application.Seeding;

/// <summary>
/// The six segments `make seed` needs (E2E-005, H22): names, descriptions, statuses and rule definitions, in the
/// same shape <c>POST /segments</c> accepts. Each <see cref="Entry.Definition"/> is the rule tree exactly as
/// `tests/fixtures/rules/valid/seed-*.json` stores it — <c>SeedSegmentsTests</c> proves the parsed trees hash
/// identically — and the segment name equals the fixture file name without its extension. `docs/test/fixtures.md`
/// documents what each rule matches and the exact member count against the seed dataset.
/// </summary>
public static class SeedSegments
{
    public sealed record Entry(string Name, string Description, SegmentStatus Status, string DefinitionJson)
    {
        public JsonElement Definition
        {
            get
            {
                using var document = JsonDocument.Parse(DefinitionJson);
                return document.RootElement.Clone();
            }
        }
    }

    public static readonly IReadOnlyList<Entry> All =
    [
        new("seed-pro-id", "Pro-plan holders in Indonesia or Singapore, or otherwise dormant.", SegmentStatus.Active, """
            {
              "op": "and",
              "children": [
                { "type": "attribute", "key": "plan", "cmp": "eq", "value": "pro" },
                { "op": "or", "children": [
                  { "type": "attribute", "key": "country", "cmp": "in", "value": ["ID", "SG"] },
                  { "type": "event", "name": "app_open", "cmp": "eq", "count": 0, "window": "7d" }
                ]}
              ]
            }
            """),
        new("seed-purchasers", "Purchased at least once in the last 30 days.", SegmentStatus.Active,
            """{ "type": "event", "name": "purchase", "cmp": "gte", "count": 1, "window": "30d" }"""),
        new("seed-dormant", "No app_open event in the last 7 days.", SegmentStatus.Active,
            """{ "type": "event", "name": "app_open", "cmp": "eq", "count": 0, "window": "7d" }"""),
        new("seed-no-plan", "No plan attribute set.", SegmentStatus.Active,
            """{ "type": "attribute", "key": "plan", "cmp": "not_exists" }"""),
        new("seed-not-pro", "Plan attribute absent or not pro.", SegmentStatus.Active,
            """{ "type": "attribute", "key": "plan", "cmp": "neq", "value": "pro" }"""),
        new("seed-high-ltv-sg", "High lifetime value profiles in Singapore.", SegmentStatus.Draft, """
            {
              "op": "and",
              "children": [
                { "type": "attribute", "key": "ltv", "cmp": "gte", "value": 150 },
                { "type": "attribute", "key": "country", "cmp": "eq", "value": "SG" }
              ]
            }
            """),
    ];
}
