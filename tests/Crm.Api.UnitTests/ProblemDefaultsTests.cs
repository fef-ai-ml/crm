using Crm.Api.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Crm.Api.UnitTests;

public class ProblemDefaultsTests
{
    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "notfound")]
    [InlineData(429, "ratelimited")]
    [InlineData(500, "unexpected")]
    public void Status_code_problems_get_a_crm_type_trace_id_and_instance(int status, string slug)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/v1/api-keys";
        var context = new ProblemDetailsContext { HttpContext = http, ProblemDetails = new ProblemDetails { Status = status, Type = "https://tools.ietf.org/html/rfc9110" } };

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Type.Should().Be($"https://crm.example/problems/{slug}");
        context.ProblemDetails.Instance.Should().Be("/v1/api-keys");
        context.ProblemDetails.Extensions["traceId"].Should().NotBeNull();
    }

    [Fact]
    public void A_type_set_by_the_mapper_is_kept()
    {
        var context = new ProblemDetailsContext
        {
            HttpContext = new DefaultHttpContext(),
            ProblemDetails = new ProblemDetails { Status = 409, Type = ResultProblemMapper.TypeFor("conflict") },
        };

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Type.Should().Be("https://crm.example/problems/conflict");
    }
}
