using Crm.Api.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Crm.Api.UnitTests;

public class ProblemDefaultsTests
{
    [Theory]
    [InlineData(400, "validation", "request.invalid")]
    [InlineData(401, "unauthorized", "auth.unauthorized")]
    [InlineData(403, "forbidden", "auth.forbidden")]
    [InlineData(404, "notfound", "resource.not_found")]
    [InlineData(409, "conflict", "request.conflict")]
    [InlineData(429, "ratelimited", "rate_limit.exceeded")]
    [InlineData(503, "unavailable", "service.unavailable")]
    [InlineData(500, "unexpected", "server.unexpected")]
    public void Status_code_problems_get_a_crm_type_trace_id_instance_and_code(int status, string slug, string code)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/v1/api-keys";
        var context = new ProblemDetailsContext { HttpContext = http, ProblemDetails = new ProblemDetails { Status = status, Type = "https://tools.ietf.org/html/rfc9110" } };

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Type.Should().Be($"https://crm.example/problems/{slug}");
        context.ProblemDetails.Instance.Should().Be("/v1/api-keys");
        context.ProblemDetails.Extensions["traceId"].Should().NotBeNull();
        context.ProblemDetails.Extensions["code"].Should().Be(code);
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

    [Fact]
    public void A_code_set_by_the_mapper_is_not_overwritten()
    {
        var context = new ProblemDetailsContext
        {
            HttpContext = new DefaultHttpContext(),
            ProblemDetails = new ProblemDetails { Status = 409, Type = ResultProblemMapper.TypeFor("conflict") },
        };
        context.ProblemDetails.Extensions["code"] = "tenancy.slug_taken";

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Extensions["code"].Should().Be("tenancy.slug_taken");
    }
}
