using System.Net;
using System.Text.Json;
using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Web.Skail;

namespace SkailSubscriptionCycle.Tests;

public class GatewayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SkailOptions Opts = new()
    {
        BaseUrl = "https://skail.example.test/",
        Namespace = "tenant-1/env-1",
        Key = "env-key",
        Workload = "subscription-cycle:v0.0.1",
    };

    private static readonly Subscription Sub = new(Plans.Pro, T0);
    private static readonly Cancellation Cancel = new("too expensive", T0);

    private static SkailApiGateway Gateway(StubHandler handler, SkailOptions? options = null) =>
        new(new HttpClient(handler), options ?? Opts, retryDelay: _ => TimeSpan.Zero);

    [Fact]
    public async Task Trigger_posts_the_arguments_to_the_function_route_of_the_workload()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync("c-1", 20, 10);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://skail.example.test/api/v1/trigger/subscription-cycle/v0.0.1/ManageSubscription/c-1", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("Bearer env-key", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.Equal("""["c-1",20,10]""", body);
    }

    [Fact]
    public async Task FireSubscription_goes_to_CUSTOMER_SUBSCRIBED_with_the_customer_id_as_instance_id()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).FireSubscriptionAsync("c-1", Sub);

        var (request, _) = Assert.Single(handler.Calls);
        Assert.Equal("https://skail.example.test/api/v1/fire/CUSTOMER_SUBSCRIBED/c-1", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
    }

    [Fact]
    public async Task FireCancellation_goes_to_SUBSCRIPTION_CANCELLED_with_the_customer_id_as_instance_id()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).FireCancellationAsync("c-1", Cancel);

        Assert.Equal("https://skail.example.test/api/v1/fire/SUBSCRIPTION_CANCELLED/c-1", Assert.Single(handler.Calls).Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Fire_bodies_use_the_property_names_of_the_declared_types()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).FireSubscriptionAsync("c-1", Sub);
        await Gateway(handler).FireCancellationAsync("c-1", Cancel);

        var sub = JsonDocument.Parse(handler.Calls[0].Body).RootElement;
        Assert.Equal("pro", sub.GetProperty("Plan").GetString());
        Assert.True(sub.TryGetProperty("At", out _));
        var cancel = JsonDocument.Parse(handler.Calls[1].Body).RootElement;
        Assert.Equal("too expensive", cancel.GetProperty("Reason").GetString());
        Assert.True(cancel.TryGetProperty("At", out _));
    }

    [Fact]
    public void Options_read_the_api_base_url_from_SKAIL_BASE_URL()
    {
        var names = new[] { "SKAIL_BASE_URL", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_WORKLOAD" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("SKAIL_BASE_URL", "https://env.example.test");
            Environment.SetEnvironmentVariable("SKAIL_NAMESPACE", "t/e");
            Environment.SetEnvironmentVariable("SKAIL_KEY", "k");
            Environment.SetEnvironmentVariable("SKAIL_WORKLOAD", "w:v1");

            var options = SkailOptions.FromEnvironment();

            Assert.Equal("https://env.example.test", options.BaseUrl);
            Assert.Equal(("t/e", "k", "w:v1"), (options.Namespace, options.Key, options.Workload));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task Customer_id_is_escaped_in_the_routes()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync("a/b c", 1, 1);
        await Gateway(handler).FireSubscriptionAsync("a/b c", Sub);
        await Gateway(handler).FireCancellationAsync("a/b c", Cancel);

        Assert.EndsWith("/ManageSubscription/a%2Fb%20c", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        Assert.EndsWith("/CUSTOMER_SUBSCRIBED/a%2Fb%20c", handler.Calls[1].Request.RequestUri!.AbsoluteUri);
        Assert.EndsWith("/SUBSCRIPTION_CANCELLED/a%2Fb%20c", handler.Calls[2].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_id_and_body()
    {
        var handler = new StubHandler((_, n) => new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        await Gateway(handler).FireSubscriptionAsync("c-1", Sub);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Single(handler.Calls.Select(c => c.Request.RequestUri!.ToString()).Distinct());
        Assert.Single(handler.Calls.Select(c => c.Body).Distinct());
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        var handler = StubHandler.Status(HttpStatusCode.TooManyRequests);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync("c", 1, 1));

        Assert.Equal(SkailApiGateway.MaxAttempts, handler.Calls.Count);
        Assert.Contains("giving up", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthorized_is_not_retried_and_points_at_the_key_and_namespace(HttpStatusCode status)
    {
        var handler = StubHandler.Status(status);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).FireCancellationAsync("c", Cancel));

        Assert.Single(handler.Calls);
        Assert.Contains("SKAIL_KEY", e.Message);
    }

    [Fact]
    public async Task Missing_base_url_or_a_malformed_workload_is_reported()
    {
        var noUrl = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { Workload = "w:v1" }).TriggerAsync("c", 1, 1));
        Assert.Contains("SKAIL_BASE_URL", noUrl.Message);

        var badWorkload = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { BaseUrl = "https://x", Workload = "oops" }).TriggerAsync("c", 1, 1));
        Assert.Contains("SKAIL_WORKLOAD", badWorkload.Message);
    }
}
