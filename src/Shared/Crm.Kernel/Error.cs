using System.Diagnostics.CodeAnalysis;

namespace Crm.Kernel;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    Unexpected,
}

[SuppressMessage("Design", "CA1716:Identifiers should not match keywords", Justification = "Type name required by specification.")]
public sealed record Error(
    ErrorKind Kind,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fields = null)
        => new(ErrorKind.Validation, code, message, fields);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);

    public static Error Unexpected(string code, string message) => new(ErrorKind.Unexpected, code, message);
}
