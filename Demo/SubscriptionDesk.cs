using SkailSubscriptionCycle.Skail;

namespace SkailSubscriptionCycle.Demo;

/// <summary>
/// Plays the customer for the page: it starts the trial with the chosen card and turns the buttons into the
/// subscription and the cancellation events. It follows one customer at a time, the last one started (or the one given
/// on the command line).
/// </summary>
public sealed class SubscriptionDesk(SkailApiClient skail, Timeline timeline, CardsOnFile cards)
{
    /// <summary>The trial and the month last seconds in the demo; a real subscription uses 30 days and one month.</summary>
    public const int TrialSeconds = 20;
    public const int MonthSeconds = 10;

    private readonly object gate = new();
    private string? customerId;
    private DateTimeOffset? startedAt;
    private bool subscribed, cancelled;

    /// <summary>Follows a customer that already exists, to see it resume.</summary>
    public void Follow(string id, string card)
    {
        cards.Keep(id, card);
        lock (gate) { customerId = id; startedAt = null; subscribed = cancelled = false; }
    }

    public async Task<string?> StartAsync(string card, CancellationToken ct)
    {
        if (!Cards.All.Contains(card)) return null;
        var id = Guid.NewGuid().ToString();
        cards.Keep(id, card);
        await skail.TriggerAsync(id, TrialSeconds, MonthSeconds, ct);
        lock (gate) { customerId = id; startedAt = DateTimeOffset.UtcNow; subscribed = cancelled = false; }
        return id;
    }

    /// <summary>Subscribes during the trial. Only once.</summary>
    public async Task<bool> SubscribeAsync(string plan, CancellationToken ct)
    {
        string id;
        lock (gate)
        {
            if (customerId is null || subscribed || plan is not (Plans.Basic or Plans.Pro) || Ended(customerId)) return false;
            subscribed = true;
            id = customerId;
        }
        await skail.FireAsync(Events.CustomerSubscribed, id, new Subscription(plan, DateTimeOffset.UtcNow), ct);
        timeline.Add(id, $"subscribed to {plan}: {Events.CustomerSubscribed} sent", "sent:subscribe");
        return true;
    }

    /// <summary>Cancels after the subscription. Only once.</summary>
    public async Task<bool> CancelAsync(CancellationToken ct)
    {
        string id;
        lock (gate)
        {
            if (customerId is null || !subscribed || cancelled || Ended(customerId)) return false;
            cancelled = true;
            id = customerId;
        }
        await skail.FireAsync(Events.SubscriptionCancelled, id, new Cancellation("cancelled in the demo", DateTimeOffset.UtcNow), ct);
        timeline.Add(id, $"cancelled: {Events.SubscriptionCancelled} sent", "sent:cancel");
        return true;
    }

    /// <summary>What the page shows: the customer, what can be done now and the timeline of the run.</summary>
    public object View()
    {
        lock (gate)
        {
            if (customerId is null) return new { customer = (object?)null };

            var entries = timeline.For(customerId);
            var end = entries.LastOrDefault(e => e.Step?.StartsWith("end:") == true)?.Step?[4..];
            var plan = entries.LastOrDefault(e => e.Step?.StartsWith("subscribed:") == true)?.Step?[11..];
            var inTrial = end is null && plan is null && !subscribed;
            return new
            {
                customer = new
                {
                    id = customerId,
                    card = cards.For(customerId),
                    plan,
                    monthsPaid = entries.Count(e => e.Step?.StartsWith("charged:") == true),
                    trialEnds = inTrial ? startedAt?.AddSeconds(TrialSeconds) : null,
                    end,
                },
                canSubscribe = inTrial,
                canCancel = end is null && subscribed && !cancelled,
                timeline = entries.Select(e => new { at = e.At, text = e.Text }),
            };
        }
    }

    private bool Ended(string id) => timeline.For(id).Any(e => e.Step?.StartsWith("end:") == true);
}
