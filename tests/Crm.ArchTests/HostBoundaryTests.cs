using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class HostBoundaryTests
{
    public static TheoryData<string, Assembly> Hosts => new()
    {
        { "Crm.Api", typeof(Crm.Api.Program).Assembly },
        { "Crm.Worker", typeof(Crm.Worker.Program).Assembly },
    };

    [Theory(Skip = "Hosts register modules in Tasks 7–8")]
    [MemberData(nameof(Hosts))]
    public void AT005_hosts_reference_only_module_infrastructure_projects(string name, Assembly host)
    {
        var moduleRefs = host.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Ingestion.", StringComparison.Ordinal)
                        || n.StartsWith("Profiles.", StringComparison.Ordinal)
                        || n.StartsWith("Segmentation.", StringComparison.Ordinal)
                        || n.StartsWith("Tenancy.", StringComparison.Ordinal))
            .ToList();

        moduleRefs.Should().NotBeEmpty($"{name} registers modules");
        moduleRefs.Should().OnlyContain(n => n.EndsWith(".Infrastructure", StringComparison.Ordinal), $"{name} may reference module Infrastructure projects only");
    }
}
