using System.Net;
using System.Text;
using System.Text.Json;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Web.Skail;

/// <summary>
/// Starts runs and sends the customer's choices through the skail HTTP API:
/// POST {SKAIL_BASE_URL}/api/v1/trigger/{workload}/{version}/{function}/{id} and
/// POST {SKAIL_BASE_URL}/api/v1/fire/{event}/{instanceId}.
/// Both calls are outside skail's durability, so transient failures are retried here with the same id and body.
/// </summary>
public sealed class SkailApiGateway(HttpClient http, SkailOptions options, Func<int, TimeSpan>? retryDelay = null)
    : ISkailGateway
{
    public const int MaxAttempts = 5;
    private const string FunctionName = "ManageSubscription";

    // the fire body uses the property names of the declared types (Subscription, Cancellation), not camelCase
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = null };

    public Task TriggerAsync(string customerId, int trialSeconds, int monthSeconds, CancellationToken ct = default)
    {
        var (name, version) = SplitWorkload(options.Workload);
        var route = $"api/v1/trigger/{Escape(name)}/{Escape(version)}/{FunctionName}/{Escape(customerId)}";
        // the body is the array of arguments, in the order of the function's signature
        var args = JsonSerializer.Serialize(new object[] { customerId, trialSeconds, monthSeconds });
        return PostAsync(route, args, ct);
    }

    public Task FireSubscriptionAsync(string customerId, Subscription subscription, CancellationToken ct = default) =>
        PostAsync($"api/v1/fire/{Events.CustomerSubscribed}/{Escape(customerId)}", JsonSerializer.Serialize(subscription, PayloadOptions), ct);

    public Task FireCancellationAsync(string customerId, Cancellation cancellation, CancellationToken ct = default) =>
        PostAsync($"api/v1/fire/{Events.SubscriptionCancelled}/{Escape(customerId)}", JsonSerializer.Serialize(cancellation, PayloadOptions), ct);

    private async Task PostAsync(string route, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new SkailGatewayException("SKAIL_BASE_URL is not set");

        var url = $"{options.BaseUrl.TrimEnd('/')}/{route}";
        var failure = "";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("Accept", "application/json");
                request.Headers.Add("skail-key", options.Key);
                request.Headers.Add("Authorization", $"Bearer {options.Key}");
                request.Headers.Add("Skail-namespace", options.Namespace);

                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) return;

                failure = $"skail answered {(int)response.StatusCode}";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new SkailGatewayException($"{failure}: check SKAIL_KEY and SKAIL_NAMESPACE for this environment");
                if (!IsTransient(response.StatusCode))
                    throw new SkailGatewayException(failure);
            }
            catch (HttpRequestException e)
            {
                failure = e.Message;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                failure = $"timeout: {e.Message}";
            }

            if (attempt < MaxAttempts) await Task.Delay((retryDelay ?? DefaultDelay)(attempt), ct);
        }

        throw new SkailGatewayException($"giving up after {MaxAttempts} attempts: {failure}");
    }

    // SKAIL_WORKLOAD is name:version; both parts are path segments of the trigger route
    private static (string Name, string Version) SplitWorkload(string workload)
    {
        var parts = workload.Split(':');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new SkailGatewayException($"SKAIL_WORKLOAD must be name:version, got '{workload}'");
        return (parts[0], parts[1]);
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan DefaultDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(2000, 200 * Math.Pow(2, attempt - 1)));
}
