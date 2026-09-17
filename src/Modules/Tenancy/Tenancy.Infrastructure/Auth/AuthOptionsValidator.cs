using Crm.Tenancy.Application;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>Reports the actual reason <see cref="AuthOptions"/> is invalid, rather than a static message
/// listing every possible cause.</summary>
internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsValid(out var reason) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(reason);
    }
}
