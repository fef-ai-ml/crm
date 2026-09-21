using System.Text.Json;
using Xunit;

namespace Crm.Segmentation.UnitTests;

/// <summary>Loads `tests/fixtures/rules/{valid,invalid}/*.json` (docs/test/fixtures.md "Rule Fixtures"), found by
/// walking up from the test binary to the directory containing `Crm.sln` (CT-002).</summary>
public static class Fixtures
{
    public static readonly IReadOnlyList<string> ValidFileNames =
    [
        "attr-eq", "attr-neq", "attr-in", "attr-nin", "attr-gt", "attr-gte", "attr-lt", "attr-lte",
        "attr-contains", "attr-exists", "attr-not-exists", "attr-bool",
        "event-gte", "event-eq-zero", "event-eq-n", "event-lte",
        "group-nested", "group-not", "depth-5", "leaves-20",
        "seed-pro-id", "seed-purchasers", "seed-dormant", "seed-no-plan", "seed-not-pro", "seed-high-ltv-sg",
    ];

    public static readonly IReadOnlyList<string> InvalidFileNames =
    [
        "depth-6", "leaves-21", "not-two-children", "not-no-children",
        "window-366d", "window-1w", "window-0d", "window-bare",
        "key-dot", "key-quote", "key-65",
        "cmp-unknown", "event-count-negative", "type-unknown",
    ];

    public static TheoryData<string> Valid() => new(ValidFileNames);

    public static TheoryData<string> Invalid() => new(InvalidFileNames);

    public static (JsonElement Definition, string? ExpectedErrorPath) Load(string name)
    {
        var kind = ValidFileNames.Contains(name) ? "valid" : "invalid";
        var path = Path.Combine(RulesRoot(), kind, $"{name}.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.Clone();
        var expectedErrorPath = root.TryGetProperty("expectedErrorPath", out var pathProperty) ? pathProperty.GetString() : null;

        return (root.GetProperty("definition"), expectedErrorPath);
    }

    public static string RulesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "tests", "fixtures", "rules");
    }

    /// <summary>`tests/fixtures/golden/`, a sibling of <see cref="RulesRoot"/> (docs/test/fixtures.md "Golden SQL Files").</summary>
    public static string GoldenRoot() => Path.Combine(Directory.GetParent(RulesRoot())!.FullName, "golden");
}
