using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class ModuleBoundaryTests
{
    private static readonly Dictionary<string, Assembly[]> Modules = new()
    {
        ["Ingestion"] = [typeof(Ingestion.Application.AssemblyMarker).Assembly, typeof(Ingestion.Infrastructure.AssemblyMarker).Assembly],
        ["Profiles"] = [typeof(Profiles.Domain.AssemblyMarker).Assembly, typeof(Profiles.Application.AssemblyMarker).Assembly, typeof(Profiles.Infrastructure.AssemblyMarker).Assembly],
        ["Segmentation"] = [typeof(Segmentation.Domain.AssemblyMarker).Assembly, typeof(Segmentation.Application.AssemblyMarker).Assembly, typeof(Segmentation.Infrastructure.AssemblyMarker).Assembly],
        ["Tenancy"] = [typeof(Tenancy.Domain.AssemblyMarker).Assembly, typeof(Tenancy.Application.AssemblyMarker).Assembly, typeof(Tenancy.Infrastructure.AssemblyMarker).Assembly],
    };

    public static TheoryData<string> ModuleNames => new(Modules.Keys);

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void AT001_module_depends_only_on_kernel_and_contracts(string module)
    {
        var otherModuleNamespaces = Modules.Keys.Where(m => m != module).Select(m => $"Crm.{m}").ToArray();

        foreach (var assembly in Modules[module])
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(otherModuleNamespaces).GetResult();
            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name} must not depend on other modules: {Names(result)}");
        }
    }

    [Fact]
    public void AT002_segmentation_never_references_profiles_assemblies()
    {
        foreach (var assembly in Modules["Segmentation"])
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name)
                .Should().NotContain(n => n!.StartsWith("Profiles.", StringComparison.Ordinal), $"{assembly.GetName().Name} references Profiles.*");
        }
    }

    [Fact]
    public void AT003_ingestion_has_no_database_dependency()
    {
        foreach (var assembly in Modules["Ingestion"])
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .Should().NotContain(n =>
                    n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || n.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
                    || n.StartsWith("StackExchange.Redis", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AT004_no_module_references_a_host_or_hosting()
    {
        foreach (var assembly in Modules.Values.SelectMany(a => a))
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .Should().NotContain(n => n == "Crm.Api" || n == "Crm.Worker" || n == "Crm.Hosting", $"{assembly.GetName().Name} references a host");
        }
    }

    private static string Names(TestResult result)
        => result.FailingTypes is null ? "" : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
