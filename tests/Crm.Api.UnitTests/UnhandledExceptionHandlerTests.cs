using Crm.Api.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Crm.Api.UnitTests;

public class UnhandledExceptionHandlerTests
{
    [Fact]
    public async Task A_client_abort_is_not_handled_or_logged()
    {
        var problems = Substitute.For<IProblemDetailsService>();
        var logger = Substitute.For<ILogger<UnhandledExceptionHandler>>();
        var handler = new UnhandledExceptionHandler(problems, logger);
        var http = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };

        var handled = await handler.TryHandleAsync(http, new OperationCanceledException(), CancellationToken.None);

        handled.Should().BeFalse();
        await problems.DidNotReceive().TryWriteAsync(Arg.Any<ProblemDetailsContext>());
        logger.ReceivedCalls().Should().BeEmpty("a cancelled client request must not be logged as an unhandled error");
    }

    [Fact]
    public async Task An_unrelated_exception_is_still_written_as_a_500_problem()
    {
        var problems = Substitute.For<IProblemDetailsService>();
        problems.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
        var logger = Substitute.For<ILogger<UnhandledExceptionHandler>>();
        var handler = new UnhandledExceptionHandler(problems, logger);
        var http = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(http, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        await problems.Received(1).TryWriteAsync(Arg.Any<ProblemDetailsContext>());
    }

    [Fact]
    public async Task A_bad_request_exception_is_a_400_problem_not_a_500()
    {
        var problems = Substitute.For<IProblemDetailsService>();
        problems.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
        var logger = Substitute.For<ILogger<UnhandledExceptionHandler>>();
        var handler = new UnhandledExceptionHandler(problems, logger);
        var http = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(
            http, new BadHttpRequestException("Failed to read parameter", StatusCodes.Status400BadRequest), CancellationToken.None);

        handled.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await problems.Received(1).TryWriteAsync(Arg.Is<ProblemDetailsContext>(c =>
            c.ProblemDetails.Status == StatusCodes.Status400BadRequest
            && c.ProblemDetails.Type == "https://crm.example/problems/validation"));
        logger.ReceivedCalls()
            .Any(call => call.GetArguments().Length > 0 && Equals(call.GetArguments()[0], LogLevel.Error))
            .Should().BeFalse("a bad request must not be logged as an unhandled error");
    }
}
