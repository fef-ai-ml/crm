using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class BootstrapTests(CrmStackFixture stack)
{
    private static object Body(string slug, bool withViewer = false) => new
    {
        name = "Demo " + slug,
        slug,
        adminEmail = $"admin@{slug}.test",
        adminPassword = TenancySeed.Password,
        viewerEmail = withViewer ? $"viewer@{slug}.test" : null,
        viewerPassword = withViewer ? TenancySeed.Password : null,
    };

    [Fact]
    public async Task IT038_development_bootstraps_a_tenant_whose_admin_can_log_in()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Development" };
        using var client = factory.CreateApiClient();
        var slug = "boot-" + Guid.CreateVersion7().ToString("N")[..12];

        using var created = await client.PostAsJsonAsync("/v1/tenants", Body(slug, withViewer: true));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("slug").GetString().Should().Be(slug);
        body.RootElement.GetProperty("tenantId").GetGuid().Should().NotBe(Guid.Empty);

        using var adminLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = $"admin@{slug}.test", password = TenancySeed.Password });
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        adminLogin.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().NotContain("secure", "Development relaxes Secure for plain-HTTP localhost");
        using var viewerLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = $"viewer@{slug}.test", password = TenancySeed.Password });
        viewerLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        using var duplicate = await client.PostAsJsonAsync("/v1/tenants", Body(slug));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task IT038_bootstrap_validates_input_and_writes_nothing_on_failure()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Development" };
        using var client = factory.CreateApiClient();
        var slug = "weak-" + Guid.CreateVersion7().ToString("N")[..12];

        using var badSlug = await client.PostAsJsonAsync("/v1/tenants", new { name = "X", slug = "Bad Slug", adminEmail = "a@b.test", adminPassword = TenancySeed.Password });
        badSlug.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var weakPassword = await client.PostAsJsonAsync("/v1/tenants", new { name = "X", slug, adminEmail = $"admin@{slug}.test", adminPassword = "short" });
        weakPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await weakPassword.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();
        (await TenantExistsAsync(factory, slug)).Should().BeFalse("the tenant row is rolled back when the admin cannot be created");
    }

    [Fact]
    public async Task IT038_production_does_not_map_the_endpoint()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Production" };
        using var client = factory.CreateApiClient();
        var slug = "prod-" + Guid.CreateVersion7().ToString("N")[..12];

        using var response = await client.PostAsJsonAsync("/v1/tenants", Body(slug));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TenantExistsAsync(factory, slug)).Should().BeFalse();
    }

    private static async Task<bool> TenantExistsAsync(CrmApiFactory factory, string slug)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Tenants.AnyAsync(t => t.Slug == slug);
    }
}
