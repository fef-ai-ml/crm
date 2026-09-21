using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Crm.ArchTests;

/// <summary>AT-007: Segmentation reads `profiles.Profiles` / `profiles.Events` by raw SQL (ADR-0009) and writes
/// nothing in schema `profiles`. Scans source text rather than a live connection, so it catches a bad table name
/// or a stray write statement at build time, before any SQL runs.</summary>
[Trait("Category", "Architecture")]
public class CrossSchemaReadTests
{
    private static readonly Regex TableReference = new(@"profiles\.(\w+)", RegexOptions.Compiled);
    private static readonly Regex WriteStatement = new(
        @"(INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE(\s+INTO)?)\s+profiles\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] SanctionedTables = ["Profiles", "Events"];

    [Fact]
    public void AT007_segmentation_sql_touches_only_the_sanctioned_profile_tables()
    {
        var errors = new List<string>();

        foreach (var (path, text) in ScannedFiles())
        {
            foreach (Match match in TableReference.Matches(text))
            {
                var table = match.Groups[1].Value;
                if (!SanctionedTables.Contains(table, StringComparer.Ordinal))
                {
                    errors.Add($"{path}: references 'profiles.{table}', only {string.Join('/', SanctionedTables)} are sanctioned");
                }
            }

            if (WriteStatement.IsMatch(text))
            {
                errors.Add($"{path}: contains a write statement against schema 'profiles'");
            }
        }

        errors.Should().BeEmpty();
    }

    private static IEnumerable<(string Path, string Text)> ScannedFiles()
    {
        var root = RepoRoot();
        var segmentationDir = Path.Combine(root, "src", "Modules", "Segmentation");
        var goldenDir = Path.Combine(root, "tests", "fixtures", "golden");

        foreach (var path in Directory.EnumerateFiles(segmentationDir, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(root, path), File.ReadAllText(path));
        }

        if (Directory.Exists(goldenDir))
        {
            foreach (var path in Directory.EnumerateFiles(goldenDir, "*.sql", SearchOption.AllDirectories))
            {
                yield return (Path.GetRelativePath(root, path), File.ReadAllText(path));
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("Crm.sln not found above " + AppContext.BaseDirectory)
            : dir.FullName;
    }
}
