using Zladovce.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddZladovceApi();

var app = builder.Build();
app.UseZladovceApi();

await app.MigrateDatabaseIfRequestedAsync();
await app.RunAsync();

/// <summary>Entry point type, public so the integration tests can host the application.</summary>
public partial class Program;
