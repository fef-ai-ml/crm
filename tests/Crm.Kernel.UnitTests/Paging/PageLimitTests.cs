using Crm.Kernel.Paging;
using FluentAssertions;
using Xunit;

namespace Crm.Kernel.UnitTests.Paging;

public sealed class PageLimitTests
{
    [Fact]
    public void Absent_limit_is_the_default() => PageLimit.Resolve(null).Value.Should().Be(50);

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void Limits_inside_the_range_pass_through(int limit) => PageLimit.Resolve(limit).Value.Should().Be(limit);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void Limits_outside_the_range_are_a_validation_error(int limit)
    {
        var result = PageLimit.Resolve(limit);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("pagination.invalid_limit");
        result.Error.Fields.Should().ContainKey("limit");
    }
}
