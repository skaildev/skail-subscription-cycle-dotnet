using System.Net.Http.Json;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Worker.Progress;

/// <summary>Reports progress to the web app: POST {web}/internal/customers/{id}/events.</summary>
public sealed class HttpProgressNotifier(HttpClient http) : IProgressNotifier
{
    public async Task NotifyAsync(CustomerEvent evt, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"internal/customers/{Uri.EscapeDataString(evt.CustomerId)}/events", evt, Json.Options, ct);
        // when a command fails, this message is what the skail Monitor shows
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Web app answered {(int)response.StatusCode} to the progress event", null, response.StatusCode);
    }
}

public sealed record ChargeRequest(string Plan, int Month, int Attempt);

/// <summary>Asks the web app (the card network) for the charge: POST {web}/internal/customers/{id}/charges.</summary>
public sealed class HttpChargeGateway(HttpClient http) : IChargeGateway
{
    public async Task<Charge> ChargeAsync(string customerId, string plan, int month, int attempt, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"internal/customers/{Uri.EscapeDataString(customerId)}/charges",
            new ChargeRequest(plan, month, attempt), Json.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Web app answered {(int)response.StatusCode} to the charge", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<Charge>(Json.Options, ct)
               ?? throw new InvalidOperationException("the card network answered with an empty body");
    }
}
