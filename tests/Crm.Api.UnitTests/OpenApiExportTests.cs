using FluentAssertions;
using Xunit;

namespace Crm.Api.UnitTests;

[Trait("Category", "Unit")]
public class OpenApiExportTests
{
    [Fact]
    public void Parses_export_path_and_strips_the_flag_from_host_args()
    {
        var (path, rest) = OpenApiExport.Parse(["--export-openapi", "out/openapi.json", "--urls", "http://x"]);

        path.Should().Be("out/openapi.json");
        rest.Should().Equal("--urls", "http://x");
    }

    [Fact]
    public void Returns_null_path_when_flag_is_absent()
    {
        var (path, rest) = OpenApiExport.Parse(["--urls", "http://x"]);

        path.Should().BeNull();
        rest.Should().Equal("--urls", "http://x");
    }

    [Fact]
    public void Flag_without_value_throws()
    {
        var act = () => OpenApiExport.Parse(["--export-openapi"]);

        act.Should().Throw<ArgumentException>().WithMessage("*--export-openapi requires a file path*");
    }
}
