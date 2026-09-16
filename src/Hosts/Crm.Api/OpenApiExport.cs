using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api;

/// <summary>
/// CLI mode: `dotnet run --project src/Hosts/Crm.Api -- --export-openapi docs/api/openapi.json`
/// builds the app, writes the OpenAPI 3.x document, and exits without listening. CI diffs the result (CT-001).
/// </summary>
public static class OpenApiExport
{
    public const string Flag = "--export-openapi";

    public static (string? Path, string[] RemainingArgs) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, Flag);
        if (index < 0)
        {
            return (null, args);
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{Flag} requires a file path argument.", nameof(args));
        }

        var rest = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return (args[index + 1], rest);
    }

    public static async Task WriteAsync(WebApplication app, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await using var textWriter = new StreamWriter(stream);
        var writer = new OpenApiJsonWriter(textWriter);
        document.SerializeAsV3(writer);
        await textWriter.FlushAsync(cancellationToken);
        await textWriter.WriteAsync('\n');
    }
}
