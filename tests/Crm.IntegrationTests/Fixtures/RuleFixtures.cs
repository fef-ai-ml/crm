using System.Text.Json;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>C10: the one copy of a helper every Segments test class used to duplicate privately — loads
/// `tests/fixtures/rules/valid/{fixtureFileName}`'s `"definition"` property, found by walking up from the test
/// binary to the directory containing Crm.sln (mirrors Segmentation.UnitTests' own Fixtures.cs).</summary>
public static class RuleFixtures
{
    public static JsonElement LoadDefinition(string fixtureFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory);
        }

        var path = Path.Combine(dir.FullName, "tests", "fixtures", "rules", "valid", fixtureFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone().GetProperty("definition");
    }
}
