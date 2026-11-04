using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Web.Skail;

namespace SkailSubscriptionCycle.Tests;

public sealed class FakeGateway : ISkailGateway
{
    public List<(string CustomerId, int Trial, int Month)> Triggers { get; } = [];
    public List<(string CustomerId, Subscription Subscription)> Subscribes { get; } = [];
    public List<(string CustomerId, Cancellation Cancellation)> Cancels { get; } = [];
    public Exception? FailWith { get; set; }
    public Exception? FailFireWith { get; set; }

    public Task TriggerAsync(string customerId, int trialSeconds, int monthSeconds, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Triggers.Add((customerId, trialSeconds, monthSeconds));
        return Task.CompletedTask;
    }

    public Task FireSubscriptionAsync(string customerId, Subscription subscription, CancellationToken ct = default)
    {
        if (FailFireWith is not null) throw FailFireWith;
        Subscribes.Add((customerId, subscription));
        return Task.CompletedTask;
    }

    public Task FireCancellationAsync(string customerId, Cancellation cancellation, CancellationToken ct = default)
    {
        if (FailFireWith is not null) throw FailFireWith;
        Cancels.Add((customerId, cancellation));
        return Task.CompletedTask;
    }
}

public sealed class WebFactory : WebApplicationFactory<Program>
{
    public FakeGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ISkailGateway>();
            s.AddSingleton<ISkailGateway>(Gateway);
        });
    }
}

public class WebApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;
    private readonly HttpClient _http;

    public WebApiTests(WebFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    private async Task<JsonElement> StartAsync(object? body = null)
    {
        var res = await _http.PostAsJsonAsync("/api/customers", body ?? new { });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> StartIdAsync(object? body = null) => (await StartAsync(body)).GetProperty("customerId").GetString()!;

    private Task<HttpResponseMessage> PostEventAsync(string id, CustomerEvent evt) =>
        _http.PostAsJsonAsync($"/internal/customers/{id}/events", evt, Json.Options);

    private Task<HttpResponseMessage> ChargeAsync(string id, string plan = Plans.Basic, int month = 1, int attempt = 1) =>
        _http.PostAsJsonAsync($"/internal/customers/{id}/charges", new { plan, month, attempt });

    private Task<HttpResponseMessage> SubscribeAsync(string id, object? body = null) =>
        _http.PostAsJsonAsync($"/api/customers/{id}/subscribe", body ?? new { });

    private Task<HttpResponseMessage> CancelAsync(string id, object? body = null) =>
        _http.PostAsJsonAsync($"/api/customers/{id}/cancel", body ?? new { });

    private async Task<JsonElement> GetAsync(string id) => await _http.GetFromJsonAsync<JsonElement>($"/api/customers/{id}");

    /// <summary>A customer in trial, as the worker would leave it after StartTrial.</summary>
    private async Task<string> InTrialAsync(object? body = null)
    {
        var id = await StartIdAsync(body);
        await PostEventAsync(id, CustomerEvent.TrialStarted(id));
        return id;
    }

    /// <summary>A subscribed customer, as the worker would leave it after StartSubscription.</summary>
    private async Task<string> SubscribedAsync(object? body = null)
    {
        var id = await InTrialAsync(body);
        Assert.Equal(HttpStatusCode.OK, (await SubscribeAsync(id)).StatusCode);
        await PostEventAsync(id, CustomerEvent.Subscribed(id, Plans.Basic));
        return id;
    }

    [Fact]
    public async Task Health_endpoint_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Index_page_is_served_with_the_form_and_the_actions()
    {
        var html = await _http.GetStringAsync("/");

        foreach (var id in new[] { "name", "plan", "trial", "month", "card", "start", "subscribe", "cancel" })
            Assert.Contains($"id=\"{id}\"", html);
    }

    [Fact]
    public async Task Config_has_the_form_defaults()
    {
        var config = await _http.GetFromJsonAsync<JsonElement>("/api/config");

        Assert.Equal(CycleLimits.DefaultName, config.GetProperty("defaultName").GetString());
        Assert.Equal(CycleLimits.DefaultTrialSeconds, config.GetProperty("defaultTrialSeconds").GetInt32());
        Assert.Equal(CycleLimits.DefaultMonthSeconds, config.GetProperty("defaultMonthSeconds").GetInt32());
    }

    [Fact]
    public async Task Start_without_a_body_uses_the_defaults_and_triggers_skail()
    {
        var c = await StartAsync();

        var id = c.GetProperty("customerId").GetString();
        Assert.Equal(CycleLimits.DefaultName, c.GetProperty("name").GetString());
        Assert.Equal("Starting", c.GetProperty("phase").GetString());
        Assert.True(c.GetProperty("active").GetBoolean());
        Assert.Equal("basic", c.GetProperty("plan").GetString());
        Assert.Contains(_factory.Gateway.Triggers, t => t.CustomerId == id
            && (t.Trial, t.Month) == (CycleLimits.DefaultTrialSeconds, CycleLimits.DefaultMonthSeconds));
    }

    [Fact]
    public async Task Start_uses_the_informed_values()
    {
        var id = await StartIdAsync(new { name = "  Beto  ", plan = "pro", trialSeconds = 5, monthSeconds = 3, card = "declined-once" });

        Assert.Contains(_factory.Gateway.Triggers, t => t.CustomerId == id && (t.Trial, t.Month) == (5, 3));
        var c = await GetAsync(id);
        Assert.Equal(("Beto", "pro", "declined-once"), (c.GetProperty("name").GetString(), c.GetProperty("plan").GetString(), c.GetProperty("card").GetString()));
    }

    [Theory]
    [InlineData("""{"plan":"gold"}""")]
    [InlineData("""{"card":"amex"}""")]
    [InlineData("""{"trialSeconds":0}""")]
    [InlineData("""{"monthSeconds":-1}""")]
    [InlineData("""{"trialSeconds":99999999999}""")]
    public async Task Start_rejects_invalid_values(string json)
    {
        var res = await _http.PostAsync("/api/customers", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Start_rejects_a_name_that_is_too_long()
    {
        var res = await _http.PostAsJsonAsync("/api/customers", new { name = new string('x', CycleLimits.MaxNameLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Start_failure_returns_502_and_forgets_the_customer()
    {
        _factory.Gateway.FailWith = new SkailGatewayException("skail answered 401");
        try
        {
            var res = await _http.PostAsJsonAsync("/api/customers", new { });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Contains("401", await res.Content.ReadAsStringAsync());
        }
        finally { _factory.Gateway.FailWith = null; }
    }

    [Fact]
    public async Task Get_unknown_customer_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/customers/nope")).StatusCode);
    }

    [Fact]
    public async Task Subscribe_fires_CUSTOMER_SUBSCRIBED_with_the_plan_and_marks_the_request()
    {
        var id = await InTrialAsync(new { plan = "pro" });

        var res = await SubscribeAsync(id);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (firedId, subscription) = Assert.Single(_factory.Gateway.Subscribes, f => f.CustomerId == id);
        Assert.Equal((id, Plans.Pro), (firedId, subscription.Plan));
        Assert.NotEqual(default, subscription.At);
        Assert.True((await GetAsync(id)).GetProperty("subscribeRequested").GetBoolean());
    }

    [Fact]
    public async Task Subscribe_can_choose_another_plan_and_invalid_plan_is_400()
    {
        var id = await InTrialAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SubscribeAsync(id, new { plan = "gold" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SubscribeAsync(id, new { plan = "pro" })).StatusCode);

        Assert.Equal(Plans.Pro, Assert.Single(_factory.Gateway.Subscribes, f => f.CustomerId == id).Subscription.Plan);
    }

    [Fact]
    public async Task A_second_subscribe_is_409_and_does_not_fire_again()
    {
        var id = await InTrialAsync();
        await SubscribeAsync(id);

        var second = await SubscribeAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(_factory.Gateway.Subscribes, f => f.CustomerId == id);
    }

    [Fact]
    public async Task Subscribing_after_the_trial_ended_is_409_without_firing()
    {
        var id = await InTrialAsync();
        await PostEventAsync(id, CustomerEvent.Final(CycleResult.Ended(id)));

        Assert.Equal(HttpStatusCode.Conflict, (await SubscribeAsync(id)).StatusCode);
        Assert.DoesNotContain(_factory.Gateway.Subscribes, f => f.CustomerId == id);
    }

    [Fact]
    public async Task Subscribe_for_an_unknown_customer_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SubscribeAsync("nope")).StatusCode);
    }

    [Fact]
    public async Task A_failed_subscribe_fire_is_502_and_the_customer_can_try_again()
    {
        var id = await InTrialAsync();
        _factory.Gateway.FailFireWith = new SkailGatewayException("giving up after 5 attempts");
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await SubscribeAsync(id)).StatusCode);
        }
        finally { _factory.Gateway.FailFireWith = null; }

        Assert.False((await GetAsync(id)).GetProperty("subscribeRequested").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SubscribeAsync(id)).StatusCode);
    }

    [Fact]
    public async Task Cancel_fires_SUBSCRIPTION_CANCELLED_with_the_reason()
    {
        var id = await SubscribedAsync();

        var res = await CancelAsync(id, new { reason = "too expensive" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (_, cancellation) = Assert.Single(_factory.Gateway.Cancels, f => f.CustomerId == id);
        Assert.Equal("too expensive", cancellation.Reason);
        Assert.True((await GetAsync(id)).GetProperty("cancelRequested").GetBoolean());
    }

    [Fact]
    public async Task Cancel_without_a_subscription_or_a_second_time_is_409_without_firing()
    {
        var trial = await InTrialAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(trial)).StatusCode);

        var id = await SubscribedAsync();
        await CancelAsync(id);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(id)).StatusCode);

        Assert.DoesNotContain(_factory.Gateway.Cancels, f => f.CustomerId == trial);
        Assert.Single(_factory.Gateway.Cancels, f => f.CustomerId == id);
    }

    [Fact]
    public async Task Cancel_for_an_unknown_customer_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync("nope")).StatusCode);
    }

    [Fact]
    public async Task A_failed_cancel_fire_is_502_and_the_customer_can_try_again()
    {
        var id = await SubscribedAsync();
        _factory.Gateway.FailFireWith = new SkailGatewayException("giving up");
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await CancelAsync(id)).StatusCode);
        }
        finally { _factory.Gateway.FailFireWith = null; }

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(id)).StatusCode);
    }

    [Fact]
    public async Task Charge_follows_the_simulated_card_and_updates_the_page_state()
    {
        var id = await SubscribedAsync(new { card = "declined-once" });

        var first = await (await ChargeAsync(id, month: 1, attempt: 1)).Content.ReadFromJsonAsync<Charge>(Json.Options);
        var again = await (await ChargeAsync(id, month: 1, attempt: 1)).Content.ReadFromJsonAsync<Charge>(Json.Options);
        var second = await (await ChargeAsync(id, month: 1, attempt: 2)).Content.ReadFromJsonAsync<Charge>(Json.Options);

        Assert.False(first!.Approved);
        Assert.Equal(first, again);
        Assert.True(second!.Approved);
        var state = await GetAsync(id);
        Assert.Equal(29.90m, state.GetProperty("totalCharged").GetDecimal());
        Assert.Equal(1, state.GetProperty("month").GetInt32());
    }

    [Fact]
    public async Task Charge_rejects_invalid_requests_and_unknown_customers()
    {
        var id = await SubscribedAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await ChargeAsync("nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChargeAsync(id, plan: "gold")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChargeAsync(id, month: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChargeAsync(id, attempt: 0)).StatusCode);
    }

    [Fact]
    public async Task Internal_events_update_the_customer_and_unknown_ones_are_ignored()
    {
        var id = await StartIdAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostEventAsync(id, CustomerEvent.TrialStarted(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostEventAsync("nope", CustomerEvent.TrialStarted("nope"))).StatusCode);

        Assert.Equal("Trial", (await GetAsync(id)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Stream_sends_the_current_state_then_each_update_and_closes_when_finished()
    {
        var id = await InTrialAsync();

        var reading = Task.Run(async () =>
        {
            using var response = await _http.GetAsync($"/api/customers/{id}/stream", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var phases = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
                if (line.StartsWith("data: "))
                    phases.Add(JsonDocument.Parse(line[6..]).RootElement.GetProperty("phase").GetString()!);
            return phases;
        });

        await Task.Delay(300);
        await PostEventAsync(id, CustomerEvent.Final(CycleResult.Ended(id)));

        var phases = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["Trial", "Ended"], phases);
    }

    [Fact]
    public async Task Stream_of_an_unknown_customer_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/customers/nope/stream")).StatusCode);
    }
}
