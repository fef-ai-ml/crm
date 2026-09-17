using Crm.Tenancy.Application;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class AuthOptionsValidatorTests
{
    [Fact]
    public void Short_signing_key_fails_with_a_message_naming_the_actual_reason()
    {
        var options = new AuthOptions { SigningKey = "too-short" };

        var result = new Crm.Tenancy.Infrastructure.Auth.AuthOptionsValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("32");
    }

    [Fact]
    public void Valid_options_pass()
    {
        var options = new AuthOptions { SigningKey = "unit-test-signing-key-0123456789abcdef0123456789" };

        var result = new Crm.Tenancy.Infrastructure.Auth.AuthOptionsValidator().Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }
}
