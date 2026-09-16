#pragma warning disable IDE0005 // Using directive is unnecessary
using Crm.Kernel;
using FluentAssertions;
using Xunit;
#pragma warning restore IDE0005

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class ResultTests
{
    [Fact]
    public void Ok_carries_value_and_no_error()
    {
        Result<int> result = Result.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_carries_error_and_value_access_throws()
    {
        var error = Error.NotFound("segment.missing", "Segment not found");
        Result<int> result = Result.Fail<int>(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Match_selects_branch()
    {
        Result<string>.Ok("a").Match(v => v + "!", e => e.Code).Should().Be("a!");
        Result<string>.Fail(Error.Conflict("dup", "dup")).Match(v => v, e => e.Code).Should().Be("dup");
    }

    [Fact]
    public void Implicit_conversions_from_value_and_error()
    {
        Result<int> ok = 7;
        Result<int> fail = Error.Validation("bad", "bad", new Dictionary<string, string[]> { ["x"] = ["required"] });

        ok.IsSuccess.Should().BeTrue();
        fail.Error!.Kind.Should().Be(ErrorKind.Validation);
        fail.Error.Fields!["x"].Should().ContainSingle("required");
    }
}
