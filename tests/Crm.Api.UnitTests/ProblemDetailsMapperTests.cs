using Crm.Api.Problems;
using Crm.Kernel;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Crm.Api.UnitTests;

public class ProblemDetailsMapperTests
{
    private const string TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    public static TheoryData<Error, int, string> Cases => new()
    {
        { Error.Validation("segment.invalid", "The segment is not valid.", new Dictionary<string, string[]> { ["rules[0].op"] = ["unknown operator"] }), 400, "validation" },
        { Error.NotFound("segment.not_found", "No such segment."), 404, "notfound" },
        { Error.Conflict("segment.duplicate", "Name already used."), 409, "conflict" },
        { Error.Forbidden("auth.forbidden", "Not allowed."), 403, "forbidden" },
        { Error.Unauthorized("auth.invalid", "Invalid credentials."), 401, "unauthorized" },
        { Error.Unexpected("boom", "Unexpected."), 500, "unexpected" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void UT056_error_kinds_map_to_status_type_and_trace_id(Error error, int status, string kind)
    {
        var problem = ResultProblemMapper.Map(error, TraceId, "/v1/segments");

        problem.Status.Should().Be(status);
        problem.Type.Should().Be($"https://crm.example/problems/{kind}");
        problem.Detail.Should().Be(error.Message);
        problem.Instance.Should().Be("/v1/segments");
        problem.Extensions["traceId"].Should().Be(TraceId);
        problem.Extensions["code"].Should().Be(error.Code);
    }

    [Fact]
    public void UT056_validation_carries_errors_keyed_by_field_path()
    {
        var error = Error.Validation("segment.invalid", "The segment is not valid.", new Dictionary<string, string[]> { ["rules[0].op"] = ["unknown operator"] });

        var problem = ResultProblemMapper.Map(error, TraceId);

        var errors = problem.Extensions["errors"].Should().BeAssignableTo<IReadOnlyDictionary<string, string[]>>().Subject;
        errors["rules[0].op"].Should().Equal("unknown operator");
        ResultProblemMapper.Map(Error.NotFound("x", "y"), TraceId).Extensions.Should().NotContainKey("errors");
    }

    [Fact]
    public async Task Filter_turns_an_Error_into_a_problem_result_and_passes_other_results_through()
    {
        var filter = new ErrorResultFilter();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        var converted = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Error.Conflict("c", "duplicate")));
        converted.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(409);

        var ok = TypedResults.Ok(1);
        var untouched = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(ok));
        untouched.Should().BeSameAs(ok);
    }
}
