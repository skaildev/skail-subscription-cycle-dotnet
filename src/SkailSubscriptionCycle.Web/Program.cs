using System.Text.Json;
using System.Text.Json.Serialization;
using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Web.Customers;
using SkailSubscriptionCycle.Web.Skail;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<CustomerStore>();
builder.Services.AddSingleton(SkailOptions.FromEnvironment());
builder.Services.AddHttpClient<ISkailGateway, SkailApiGateway>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/api/config", () => Results.Ok(new
{
    defaultName = CycleLimits.DefaultName,
    defaultTrialSeconds = IntFromEnvironment("DEFAULT_TRIAL_SECONDS", CycleLimits.DefaultTrialSeconds),
    defaultMonthSeconds = IntFromEnvironment("DEFAULT_MONTH_SECONDS", CycleLimits.DefaultMonthSeconds),
    monitorUrl = Environment.GetEnvironmentVariable("MONITOR_URL") is { Length: > 0 } url ? url : null,
}));

app.MapPost("/api/customers", async (StartCustomerRequest? request, CustomerStore store, ISkailGateway skail, CancellationToken ct) =>
{
    var name = string.IsNullOrWhiteSpace(request?.Name) ? CycleLimits.DefaultName : request.Name.Trim();
    var plan = string.IsNullOrWhiteSpace(request?.Plan) ? Plans.Basic : request.Plan;
    var card = string.IsNullOrWhiteSpace(request?.Card) ? Cards.Approved : request.Card;
    var trial = request?.TrialSeconds ?? IntFromEnvironment("DEFAULT_TRIAL_SECONDS", CycleLimits.DefaultTrialSeconds);
    var month = request?.MonthSeconds ?? IntFromEnvironment("DEFAULT_MONTH_SECONDS", CycleLimits.DefaultMonthSeconds);

    if (name.Length > CycleLimits.MaxNameLength)
        return Results.BadRequest(new { error = $"The name can have at most {CycleLimits.MaxNameLength} characters" });
    if (!Plans.IsValid(plan))
        return Results.BadRequest(new { error = $"Unknown plan. Use '{Plans.Basic}' or '{Plans.Pro}'" });
    if (!Cards.IsValid(card))
        return Results.BadRequest(new { error = $"Unknown card. Use '{Cards.Approved}', '{Cards.DeclinedOnce}' or '{Cards.AlwaysDeclined}'" });
    if (trial is < 1 or > CycleLimits.MaxSeconds || month is < 1 or > CycleLimits.MaxSeconds)
        return Results.BadRequest(new { error = $"Trial and month must be between 1 and {CycleLimits.MaxSeconds} seconds" });

    var customerId = Guid.NewGuid().ToString();
    var customer = store.Create(customerId, name, plan, card, trial, month);
    try
    {
        await skail.TriggerAsync(customerId, trial, month, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Remove(customerId);
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(customer);
});

app.MapGet("/api/customers/{id}", (string id, CustomerStore store) =>
    store.Get(id) is { } customer ? Results.Ok(customer) : Results.NotFound());

// Subscribe during the trial: registered here (first one wins) and fired to the function's WaitForEvent.
app.MapPost("/api/customers/{id}/subscribe", async (string id, SubscribeRequest? request, CustomerStore store, ISkailGateway skail, CancellationToken ct) =>
{
    var current = store.Get(id);
    if (current is null) return Results.NotFound();

    var plan = string.IsNullOrWhiteSpace(request?.Plan) ? current.Plan : request.Plan;
    if (!Plans.IsValid(plan))
        return Results.BadRequest(new { error = $"Unknown plan. Use '{Plans.Basic}' or '{Plans.Pro}'" });

    var subscription = new Subscription(plan, DateTimeOffset.UtcNow);
    switch (store.TrySubscribe(id, subscription))
    {
        case DecisionOutcome.NotFound: return Results.NotFound();
        case DecisionOutcome.Duplicate: return Results.Conflict(new { error = "The customer already subscribed" });
        case DecisionOutcome.NotAllowed: return Results.Conflict(new { error = "The customer is not in the trial" });
    }

    try
    {
        await skail.FireSubscriptionAsync(id, subscription, ct);
    }
    catch (SkailGatewayException e)
    {
        store.ReleaseSubscribe(id); // not delivered: the customer can try again
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(store.Get(id));
});

// Cancel an active subscription: registered here (first one wins) and fired to the function's WaitForEvent.
app.MapPost("/api/customers/{id}/cancel", async (string id, CancelRequest? request, CustomerStore store, ISkailGateway skail, CancellationToken ct) =>
{
    var cancellation = new Cancellation(
        string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim(),
        DateTimeOffset.UtcNow);

    switch (store.TryCancel(id, cancellation))
    {
        case DecisionOutcome.NotFound: return Results.NotFound();
        case DecisionOutcome.Duplicate: return Results.Conflict(new { error = "The cancellation was already requested" });
        case DecisionOutcome.NotAllowed: return Results.Conflict(new { error = "There is no active subscription to cancel" });
    }

    try
    {
        await skail.FireCancellationAsync(id, cancellation, ct);
    }
    catch (SkailGatewayException e)
    {
        store.ReleaseCancel(id);
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(store.Get(id));
});

// called by ChargeMonthlyFee: the simulated card network decides by the customer's card
app.MapPost("/internal/customers/{id}/charges", (string id, ChargeRequest? request, CustomerStore store) =>
{
    if (request is null || !Plans.IsValid(request.Plan) || request.Month < 1 || request.Attempt < 1)
        return Results.BadRequest(new { error = "plan, month and attempt are required (month and attempt start at 1)" });

    return store.Charge(id, request.Plan, request.Month, request.Attempt) is { } charge
        ? Results.Ok(charge)
        : Results.NotFound();
});

// called by the worker's commands (StartTrial, StartSubscription, SuspendSubscription, ..., ReportStatus)
app.MapPost("/internal/customers/{id}/events", (string id, CustomerEvent evt, CustomerStore store) =>
{
    store.Apply(evt with { CustomerId = id });
    return Results.NoContent();
});

app.MapGet("/api/customers/{id}/stream", async (string id, HttpContext http, CustomerStore store, CancellationToken ct) =>
{
    using var feed = store.Watch(id); // watch before reading the snapshot: no update is lost
    if (store.Get(id) is not { } current) return Results.NotFound();

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(CustomerState state)
    {
        var json = JsonSerializer.Serialize(state, Json.Options);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await Send(current);
        if (current.IsTerminal) return Results.Empty;

        await foreach (var state in feed.Reader.ReadAllAsync(ct))
        {
            await Send(state);
            if (state.IsTerminal) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    return Results.Empty;
});

app.Run();

static int IntFromEnvironment(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

public sealed record StartCustomerRequest(string? Name, string? Plan, int? TrialSeconds, int? MonthSeconds, string? Card);

public sealed record SubscribeRequest(string? Plan);

public sealed record CancelRequest(string? Reason);

public sealed record ChargeRequest(string Plan, int Month, int Attempt);

public partial class Program;
