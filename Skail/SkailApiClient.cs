using System.Net;
using System.Text;
using System.Text.Json;

namespace SkailSubscriptionCycle.Skail;

public sealed class SkailApiException(string message) : Exception(message);

/// <summary>
/// Starts runs and fires events through the skail HTTP API:
/// POST {SKAIL_BASE_URL}/api/v1/trigger/{name}/{version}/ManageSubscription/{customerId} and
/// POST {SKAIL_BASE_URL}/api/v1/fire/{CUSTOMER_SUBSCRIBED|SUBSCRIPTION_CANCELLED}/{customerId}.
/// Both calls are outside skail's durability, so transient failures are retried here with the same id and body.
/// </summary>
public sealed class SkailApiClient(HttpClient http, SkailApiOptions options)
{
    private const int MaxAttempts = 5;
    private const string FunctionName = nameof(SubscriptionFunctions.ManageSubscription);

    public Task TriggerAsync(string customerId, int trialSeconds, int monthSeconds, CancellationToken ct = default)
    {
        var (name, version) = SplitWorkload(options.Workload);
        var route = $"api/v1/trigger/{Escape(name)}/{Escape(version)}/{FunctionName}/{Escape(customerId)}";
        // the arguments of ManageSubscription(customerId, trialSeconds, monthSeconds), in order
        var arguments = JsonSerializer.Serialize(new object[] { customerId, trialSeconds, monthSeconds });
        return PostAsync(route, arguments, ct);
    }

    /// <summary>Sends an event to the run waiting for it. The property names go as C# declares them.</summary>
    public Task FireAsync(string eventName, string customerId, object payload, CancellationToken ct = default) =>
        PostAsync($"api/v1/fire/{eventName}/{Escape(customerId)}", JsonSerializer.Serialize(payload), ct);

    private async Task PostAsync(string route, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new SkailApiException("SKAIL_BASE_URL is not set");

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
                request.Headers.Add("skail-key", options.Key);
                request.Headers.Add("Authorization", $"Bearer {options.Key}");
                request.Headers.Add("Skail-namespace", options.Namespace);

                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) return;

                failure = $"skail answered {(int)response.StatusCode}";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new SkailApiException($"{failure}: check SKAIL_KEY and SKAIL_NAMESPACE");
                if (!IsTransient(response.StatusCode))
                    throw new SkailApiException(failure);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                failure = e.Message; // network error or timeout
            }

            if (attempt < MaxAttempts) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 200 * Math.Pow(2, attempt - 1))), ct);
        }

        throw new SkailApiException($"giving up after {MaxAttempts} attempts: {failure}");
    }

    // SKAIL_WORKLOAD is name:version; both parts are path segments of the trigger route
    private static (string Name, string Version) SplitWorkload(string workload)
    {
        var parts = workload.Split(':');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new SkailApiException($"SKAIL_WORKLOAD must be name:version, got '{workload}'");
        return (parts[0], parts[1]);
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}
