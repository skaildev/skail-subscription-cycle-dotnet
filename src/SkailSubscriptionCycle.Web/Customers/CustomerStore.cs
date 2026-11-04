using System.Globalization;
using System.Threading.Channels;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Web.Customers;

/// <summary>
/// In-memory state of the customers. The durable state lives in skail; this is the UI view, the "one subscription
/// and one cancellation" guard, and the simulated card network. Clicks are registered here before the fire, and the
/// final result comes from the worker.
/// </summary>
public sealed class CustomerStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, CustomerState> _customers = [];
    private readonly Dictionary<string, List<Channel<CustomerState>>> _subscribers = [];

    public CustomerState Create(string customerId, string name, string plan, string card, int trialSeconds, int monthSeconds)
    {
        lock (_gate)
        {
            var customer = new CustomerState(customerId, name, plan, card, trialSeconds, monthSeconds,
                CustomerPhase.Starting, 0, 0m, false, false, null, [], [], null, null);
            _customers[customerId] = customer;
            return customer;
        }
    }

    public CustomerState? Get(string customerId)
    {
        lock (_gate) return _customers.GetValueOrDefault(customerId);
    }

    public void Remove(string customerId)
    {
        lock (_gate)
        {
            _customers.Remove(customerId);
            Complete(customerId);
        }
    }

    /// <summary>The first subscribe wins; a second one, or one outside the trial, is refused so no second event is fired.</summary>
    public DecisionOutcome TrySubscribe(string customerId, Subscription subscription)
    {
        lock (_gate)
        {
            if (!_customers.TryGetValue(customerId, out var c)) return DecisionOutcome.NotFound;
            if (c.Phase != CustomerPhase.Trial) return DecisionOutcome.NotAllowed;
            if (c.SubscribeRequested) return DecisionOutcome.Duplicate;

            Publish(Add(c with { SubscribeRequested = true }, "SubscribeRequested", $"Subscription requested: plan {subscription.Plan}"));
            return DecisionOutcome.Accepted;
        }
    }

    /// <summary>Undoes <see cref="TrySubscribe"/> when the fire failed, so the customer can try again.</summary>
    public void ReleaseSubscribe(string customerId) => Release(customerId, "SubscribeRequested", c => c.SubscribeRequested && c.Phase == CustomerPhase.Trial,
        c => c with { SubscribeRequested = false });

    /// <summary>The first cancel wins; it needs an active subscription (including a declined charge awaiting its retry).</summary>
    public DecisionOutcome TryCancel(string customerId, Cancellation cancellation)
    {
        lock (_gate)
        {
            if (!_customers.TryGetValue(customerId, out var c)) return DecisionOutcome.NotFound;
            if (c.Phase is not (CustomerPhase.Subscribed or CustomerPhase.Declined)) return DecisionOutcome.NotAllowed;
            if (c.CancelRequested) return DecisionOutcome.Duplicate;

            var reason = string.IsNullOrWhiteSpace(cancellation.Reason) ? "" : $": {cancellation.Reason}";
            Publish(Add(c with { CancelRequested = true }, "CancelRequested", $"Cancellation requested{reason}"));
            return DecisionOutcome.Accepted;
        }
    }

    /// <summary>Undoes <see cref="TryCancel"/> when the fire failed, so the customer can try again.</summary>
    public void ReleaseCancel(string customerId) => Release(customerId, "CancelRequested", c => c.CancelRequested && c.Active,
        c => c with { CancelRequested = false });

    /// <summary>
    /// The simulated card network. Each month and attempt is charged once: a technical retry of the command gets
    /// the recorded Charge and is not counted twice. Returns null for an unknown customer.
    /// </summary>
    public Charge? Charge(string customerId, string plan, int month, int attempt)
    {
        lock (_gate)
        {
            if (!_customers.TryGetValue(customerId, out var c)) return null;

            var known = c.Charges.FirstOrDefault(r => r.Month == month && r.Attempt == attempt);
            if (known is not null) return known.Charge;

            var charge = Cards.ChargeFor(c.Card, plan, attempt);
            if (c.Phase is not (CustomerPhase.Subscribed or CustomerPhase.Declined)) return charge;

            var amount = Money(charge.Amount);
            var retry = attempt > 1 ? $" (attempt {attempt})" : "";
            var charged = c with { Month = month, Charges = [.. c.Charges, new ChargeRecord(month, attempt, charge)] };
            Publish(charge.Approved
                ? Add(charged with { Phase = CustomerPhase.Subscribed, TotalCharged = c.TotalCharged + charge.Amount },
                    "Charged", $"Month {month}: charged R$ {amount}{retry}")
                : Add(charged with { Phase = CustomerPhase.Declined },
                    "ChargeDeclined", $"Month {month}: payment declined{retry}: {charge.Reason}"));
            return charge;
        }
    }

    /// <returns>true when the event changed the customer; false when ignored (unknown customer, terminal state, repeated stage).</returns>
    public bool Apply(CustomerEvent evt)
    {
        lock (_gate)
        {
            if (!_customers.TryGetValue(evt.CustomerId, out var c) || c.IsTerminal) return false;

            CustomerState? next = evt.Stage switch
            {
                CustomerStage.TrialStarted when c.Phase == CustomerPhase.Starting =>
                    Add(c with { Phase = CustomerPhase.Trial, TrialStartedAt = _clock.GetUtcNow() }, "TrialStarted",
                        $"Trial started ({c.TrialSeconds}s)"),
                CustomerStage.TrialEnded when c.Phase == CustomerPhase.Trial && evt.Result is { } r =>
                    Add(c with { Phase = CustomerPhase.Ended, Result = r }, "TrialEnded", "Trial ended without a subscription"),
                CustomerStage.Subscribed when c.Phase == CustomerPhase.Trial =>
                    Add(c with { Phase = CustomerPhase.Subscribed, Plan = evt.Plan ?? c.Plan }, "Subscribed", $"Subscribed to plan {evt.Plan ?? c.Plan} (R$ {Money(Plans.Price(evt.Plan ?? c.Plan))}/month)"),
                CustomerStage.Retrying when c.Phase == CustomerPhase.Declined && c.Timeline.LastOrDefault()?.Kind != "Retrying" =>
                    Add(c, "Retrying", $"Customer warned about the declined payment, retrying in {CycleLimits.RetrySeconds}s"),
                CustomerStage.Suspended when IsCharging(c) && evt.Result is { } r =>
                    Add(c with { Phase = CustomerPhase.Suspended, Result = r }, "Suspended", $"Subscription suspended at month {r.Month}: {r.Reason}"),
                CustomerStage.Cancelled when IsCharging(c) && evt.Result is { } r =>
                    Add(c with { Phase = CustomerPhase.Cancelled, Result = r }, "Cancelled", $"Subscription cancelled after month {r.Month}"),
                CustomerStage.Expired when c.Phase == CustomerPhase.Subscribed && evt.Result is { } r =>
                    Add(c with { Phase = CustomerPhase.Expired, Result = r }, "Expired", $"Cycle ended after {r.MonthsPaid} months"),
                CustomerStage.Failed => Add(c with { Phase = CustomerPhase.Failed, Error = evt.Error ?? "unknown error" }, "Failed", evt.Error ?? "unknown error"),
                _ => null,
            };
            if (next is null) return false;

            Publish(next);
            return true;
        }
    }

    /// <summary>Follows the state of a customer; dispose to stop. The reader completes when the customer ends.</summary>
    public StateFeed Watch(string customerId)
    {
        var channel = Channel.CreateUnbounded<CustomerState>();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(customerId, out var list)) _subscribers[customerId] = list = [];
            list.Add(channel);
        }
        return new StateFeed(channel.Reader, () =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(customerId, out var list)) list.Remove(channel);
            }
        });
    }

    private static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    private static bool IsCharging(CustomerState c) => c.Phase is CustomerPhase.Subscribed or CustomerPhase.Declined;

    private void Release(string customerId, string kind, Func<CustomerState, bool> applies, Func<CustomerState, CustomerState> change)
    {
        lock (_gate)
        {
            if (!_customers.TryGetValue(customerId, out var c) || !applies(c)) return;

            var timeline = c.Timeline.ToList();
            var last = timeline.FindLastIndex(t => t.Kind == kind);
            if (last >= 0) timeline.RemoveAt(last);
            Publish(change(c with { Timeline = timeline }));
        }
    }

    private CustomerState Add(CustomerState c, string kind, string text) =>
        c with { Timeline = [.. c.Timeline, new TimelineEntry(kind, text, _clock.GetUtcNow())] };

    // must be called with _gate held
    private void Publish(CustomerState state)
    {
        _customers[state.CustomerId] = state;
        if (_subscribers.TryGetValue(state.CustomerId, out var list))
            foreach (var channel in list) channel.Writer.TryWrite(state);
        if (state.IsTerminal) Complete(state.CustomerId);
    }

    private void Complete(string customerId)
    {
        if (!_subscribers.TryGetValue(customerId, out var list)) return;
        foreach (var channel in list) channel.Writer.TryComplete();
    }

    public sealed class StateFeed(ChannelReader<CustomerState> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<CustomerState> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
