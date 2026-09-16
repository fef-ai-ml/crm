namespace Crm.Worker;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Ok());
        await app.RunAsync();
    }
}
