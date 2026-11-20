using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailSubscriptionCycle;
using SkailSubscriptionCycle.Demo;
using SkailSubscriptionCycle.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run [-- <customer id> [card]]
//   then open http://localhost:8080; a customer id follows a customer that already exists (to see it resume),
//   with its card: approved (default), declined-once or always-declined
var positional = args.Where(a => !a.StartsWith('-')).ToList();
var customer = positional.ElementAtOrDefault(0);
var card = positional.ElementAtOrDefault(1) ?? Cards.Approved;
if ((customer is not null && !Guid.TryParse(customer, out _)) || !Cards.All.Contains(card) || positional.Count > 2)
{
    Console.Error.WriteLine($"Unknown arguments. Pass a customer id and its card ({string.Join(", ", Cards.All)}), or nothing.");
    return 1;
}

DotEnv.Load();
string[] required = ["SKAIL_WORKLOAD", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_SIDECAR"];
var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Missing {string.Join(", ", missing)}. Copy .env.example to .env and fill it in.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

// keep the console for the demo: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// the page: localhost only, unless DEMO_LISTEN says otherwise (the container listens on all interfaces)
var listen = Environment.GetEnvironmentVariable("DEMO_LISTEN") ?? "http://localhost:8080";
builder.WebHost.UseUrls(listen);

builder.Services.AddSingleton<Timeline>();
builder.Services.AddSingleton<CardsOnFile>();
builder.Services.AddSingleton<SubscriptionDesk>();
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));

// hosts the worker that executes SubscriptionFunctions; reads the SKAIL_* variables
builder.UseSkail();

var app = builder.Build();
var desk = app.Services.GetRequiredService<SubscriptionDesk>();
if (customer is not null) desk.Follow(Guid.Parse(customer).ToString(), card);

var page = Path.Combine(AppContext.BaseDirectory, "Page", "index.html");
app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));
app.MapGet("/api/state", () => Results.Json(desk.View()));
app.MapPost("/api/customers/{card}", async (string card, CancellationToken ct) =>
    await desk.StartAsync(card, ct) is { } id ? Results.Json(new { id }) : Results.BadRequest());
app.MapPost("/api/subscribe/{plan}", async (string plan, CancellationToken ct) =>
    await desk.SubscribeAsync(plan, ct) ? Results.Ok() : Results.Conflict());
app.MapPost("/api/cancel", async (CancellationToken ct) => await desk.CancelAsync(ct) ? Results.Ok() : Results.Conflict());

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Open http://localhost:8080 to start a customer and play the customer. Ctrl+C exits."));

await app.RunAsync();
return 0;
