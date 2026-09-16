using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class HostBoundaryTests
{
    [Fact]
    public void AT005_crm_api_references_only_module_infrastructure_projects()
    {
        AssertReferencesOnlyModuleInfrastructure("Crm.Api", typeof(Crm.Api.Program).Assembly);
    }

    [Fact(Skip = "Worker registers modules in Task 8")]
    public void AT005_crm_worker_references_only_module_infrastructure_projects()
    {
        AssertReferencesOnlyModuleInfrastructure("Crm.Worker", typeof(Crm.Worker.Program).Assembly);
    }

    private static void AssertReferencesOnlyModuleInfrastructure(string name, Assembly host)
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
