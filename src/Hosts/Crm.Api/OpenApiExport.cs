using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api;

/// <summary>
/// CLI mode: `dotnet run --project src/Hosts/Crm.Api -- --export-openapi docs/api/openapi.json`
/// builds the app, writes the OpenAPI 3.x document, and exits. CI diffs the result (CT-001).
/// The ASP.NET Core endpoint metadata that the OpenAPI generator reads (via <see cref="Microsoft.AspNetCore.Mvc.ApiExplorer.IApiDescriptionGroupCollectionProvider"/>)
/// is only wired up once the host actually starts (routing finalizes its endpoint data source then), so this briefly
/// starts Kestrel bound to an OS-assigned loopback port (never a fixed/external one) purely to trigger that wiring,
/// then stops it immediately; no request is ever served and no database/Redis/Kafka connection is required.
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
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync(cancellationToken);
        OpenApiDocument document;
        try
        {
            var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
            document = await provider.GetOpenApiDocumentAsync(cancellationToken);
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }

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
