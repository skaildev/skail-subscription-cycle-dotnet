using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Web.Customers;

namespace SkailSubscriptionCycle.Tests;

public class CustomerStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static Subscription Sub(string plan = Plans.Basic) => new(plan, T0);
    private static Cancellation Cancel() => new("too expensive", T0);

    private static CustomerStore NewStore(string card = Cards.Approved)
    {
        var store = new CustomerStore();
        store.Create("c1", "Ana", Plans.Basic, card, 20, 10);
        return store;
    }

    /// <summary>Trial started and the customer subscribed (the worker confirmed it).</summary>
    private static CustomerStore Subscribed(string card = Cards.Approved)
    {
        var store = NewStore(card);
        store.Apply(CustomerEvent.TrialStarted("c1"));
        Assert.Equal(DecisionOutcome.Accepted, store.TrySubscribe("c1", Sub()));
        store.Apply(CustomerEvent.Subscribed("c1", Plans.Basic));
        return store;
    }

    [Fact]
    public void A_new_customer_is_starting_with_no_charges()
    {
        var c = NewStore().Get("c1")!;

        Assert.Equal((CustomerPhase.Starting, 0, 0m), (c.Phase, c.Month, c.TotalCharged));
        Assert.True(c.Active);
        Assert.Equal(("Ana", Plans.Basic, Cards.Approved, 20, 10), (c.Name, c.Plan, c.Card, c.TrialSeconds, c.MonthSeconds));
    }

    [Fact]
    public void TrialStarted_moves_to_trial_once_and_stamps_the_start()
    {
        var store = NewStore();

        Assert.True(store.Apply(CustomerEvent.TrialStarted("c1")));
        Assert.False(store.Apply(CustomerEvent.TrialStarted("c1")));

        var c = store.Get("c1")!;
        Assert.Equal(CustomerPhase.Trial, c.Phase);
        Assert.NotNull(c.TrialStartedAt);
    }

    [Fact]
    public void TrialEnded_ends_the_customer_and_closes_the_stream()
    {
        var store = NewStore();
        store.Apply(CustomerEvent.TrialStarted("c1"));
        using var sub = store.Watch("c1");

        Assert.True(store.Apply(CustomerEvent.Final(CycleResult.Ended("c1"))));

        var c = store.Get("c1")!;
        Assert.Equal(CustomerPhase.Ended, c.Phase);
        Assert.True(c.IsTerminal);
        Assert.Equal(CycleEnd.Ended, c.Result!.End);
        Assert.True(sub.Reader.TryRead(out var published));
        Assert.Equal(CustomerPhase.Ended, published!.Phase);
        Assert.True(sub.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void TrySubscribe_only_works_during_the_trial_and_only_once()
    {
        var store = NewStore();
        Assert.Equal(DecisionOutcome.NotAllowed, store.TrySubscribe("c1", Sub())); // still Starting

        store.Apply(CustomerEvent.TrialStarted("c1"));
        Assert.Equal(DecisionOutcome.Accepted, store.TrySubscribe("c1", Sub()));
        Assert.Equal(DecisionOutcome.Duplicate, store.TrySubscribe("c1", Sub()));
        Assert.Equal(DecisionOutcome.NotFound, store.TrySubscribe("nope", Sub()));
    }

    [Fact]
    public void TrySubscribe_after_the_trial_ended_is_not_allowed()
    {
        var store = NewStore();
        store.Apply(CustomerEvent.TrialStarted("c1"));
        store.Apply(CustomerEvent.Final(CycleResult.Ended("c1")));

        Assert.Equal(DecisionOutcome.NotAllowed, store.TrySubscribe("c1", Sub()));
    }

    [Fact]
    public void ReleaseSubscribe_lets_the_customer_try_again_after_a_failed_fire()
    {
        var store = NewStore();
        store.Apply(CustomerEvent.TrialStarted("c1"));
        store.TrySubscribe("c1", Sub());

        store.ReleaseSubscribe("c1");

        Assert.Equal(DecisionOutcome.Accepted, store.TrySubscribe("c1", Sub()));
        Assert.Single(store.Get("c1")!.Timeline, t => t.Kind == "SubscribeRequested");
    }

    [Fact]
    public void Subscribed_moves_the_trial_to_subscribed_with_the_chosen_plan()
    {
        var store = NewStore();
        store.Apply(CustomerEvent.TrialStarted("c1"));
        store.TrySubscribe("c1", Sub(Plans.Pro));

        Assert.True(store.Apply(CustomerEvent.Subscribed("c1", Plans.Pro)));
        Assert.False(store.Apply(CustomerEvent.Subscribed("c1", Plans.Pro)));

        var c = store.Get("c1")!;
        Assert.Equal((CustomerPhase.Subscribed, Plans.Pro), (c.Phase, c.Plan));
    }

    [Fact]
    public void An_approved_charge_adds_to_the_total_and_the_month()
    {
        var store = Subscribed();

        var charge = store.Charge("c1", Plans.Basic, 1, 1)!;

        Assert.True(charge.Approved);
        var c = store.Get("c1")!;
        Assert.Equal((CustomerPhase.Subscribed, 1, 29.90m), (c.Phase, c.Month, c.TotalCharged));
        Assert.Single(c.Timeline, t => t.Kind == "Charged");
    }

    [Fact]
    public void A_declined_charge_moves_to_declined_without_adding_to_the_total()
    {
        var store = Subscribed(Cards.DeclinedOnce);

        var charge = store.Charge("c1", Plans.Basic, 1, 1)!;

        Assert.False(charge.Approved);
        var c = store.Get("c1")!;
        Assert.Equal((CustomerPhase.Declined, 0m), (c.Phase, c.TotalCharged));
        Assert.Single(c.Timeline, t => t.Kind == "ChargeDeclined");
    }

    [Fact]
    public void Declined_once_approves_the_second_attempt_and_goes_back_to_subscribed()
    {
        var store = Subscribed(Cards.DeclinedOnce);
        store.Charge("c1", Plans.Basic, 1, 1);

        var second = store.Charge("c1", Plans.Basic, 1, 2)!;

        Assert.True(second.Approved);
        var c = store.Get("c1")!;
        Assert.Equal((CustomerPhase.Subscribed, 1, 29.90m), (c.Phase, c.Month, c.TotalCharged));
    }

    [Fact]
    public void The_same_attempt_is_answered_again_without_charging_twice()
    {
        var store = Subscribed();

        var first = store.Charge("c1", Plans.Basic, 1, 1);
        var again = store.Charge("c1", Plans.Basic, 1, 1);

        Assert.Equal(first, again);
        var c = store.Get("c1")!;
        Assert.Equal(29.90m, c.TotalCharged);
        Assert.Single(c.Charges);
    }

    [Fact]
    public void Charge_for_an_unknown_customer_is_null()
    {
        Assert.Null(NewStore().Charge("nope", Plans.Basic, 1, 1));
    }

    [Fact]
    public void Charge_is_not_recorded_once_the_customer_finished_but_is_still_answered()
    {
        var store = Subscribed();
        store.Apply(CustomerEvent.Final(CycleResult.Cancelled("c1", Plans.Basic, 0)));

        var charge = store.Charge("c1", Plans.Basic, 1, 1);

        Assert.NotNull(charge);
        Assert.Empty(store.Get("c1")!.Charges);
    }

    [Fact]
    public void Retrying_is_recorded_once_and_only_while_declined()
    {
        var store = Subscribed(Cards.AlwaysDeclined);
        Assert.False(store.Apply(CustomerEvent.Retrying("c1", 1, "x"))); // approved so far: nothing to retry

        store.Charge("c1", Plans.Basic, 1, 1);
        Assert.True(store.Apply(CustomerEvent.Retrying("c1", 1, "x")));
        Assert.False(store.Apply(CustomerEvent.Retrying("c1", 1, "x")));
    }

    [Fact]
    public void Always_declined_ends_suspended()
    {
        var store = Subscribed(Cards.AlwaysDeclined);
        store.Charge("c1", Plans.Basic, 1, 1);
        store.Apply(CustomerEvent.Retrying("c1", 1, "x"));
        store.Charge("c1", Plans.Basic, 1, 2);

        Assert.True(store.Apply(CustomerEvent.Final(CycleResult.Suspended("c1", Plans.Basic, 1, "x"))));

        var c = store.Get("c1")!;
        Assert.Equal(CustomerPhase.Suspended, c.Phase);
        Assert.Equal(0m, c.TotalCharged);
    }

    [Fact]
    public void TryCancel_needs_an_active_subscription_and_works_once()
    {
        var trial = NewStore();
        trial.Apply(CustomerEvent.TrialStarted("c1"));
        Assert.Equal(DecisionOutcome.NotAllowed, trial.TryCancel("c1", Cancel())); // nothing to cancel in a trial

        var store = Subscribed();
        Assert.Equal(DecisionOutcome.Accepted, store.TryCancel("c1", Cancel()));
        Assert.Equal(DecisionOutcome.Duplicate, store.TryCancel("c1", Cancel()));
        Assert.Equal(DecisionOutcome.NotFound, store.TryCancel("nope", Cancel()));
        Assert.True(store.Get("c1")!.CancelRequested);
    }

    [Fact]
    public void TryCancel_is_accepted_while_a_declined_charge_waits_for_its_retry()
    {
        var store = Subscribed(Cards.DeclinedOnce);
        store.Charge("c1", Plans.Basic, 1, 1);

        Assert.Equal(DecisionOutcome.Accepted, store.TryCancel("c1", Cancel()));
    }

    [Fact]
    public void ReleaseCancel_lets_the_customer_try_again()
    {
        var store = Subscribed();
        store.TryCancel("c1", Cancel());

        store.ReleaseCancel("c1");

        Assert.False(store.Get("c1")!.CancelRequested);
        Assert.Equal(DecisionOutcome.Accepted, store.TryCancel("c1", Cancel()));
    }

    [Fact]
    public void Cancelled_ends_the_customer_and_blocks_further_actions()
    {
        var store = Subscribed();
        store.Charge("c1", Plans.Basic, 1, 1);
        store.TryCancel("c1", Cancel());

        Assert.True(store.Apply(CustomerEvent.Final(CycleResult.Cancelled("c1", Plans.Basic, 1))));

        var c = store.Get("c1")!;
        Assert.Equal(CustomerPhase.Cancelled, c.Phase);
        Assert.Equal(1, c.Result!.MonthsPaid);
        Assert.Equal(DecisionOutcome.NotAllowed, store.TryCancel("c1", Cancel()));
    }

    [Fact]
    public void Expired_ends_after_the_month_limit()
    {
        var store = Subscribed();

        Assert.True(store.Apply(CustomerEvent.Final(CycleResult.Expired("c1", Plans.Basic, 12))));

        Assert.Equal(CustomerPhase.Expired, store.Get("c1")!.Phase);
    }

    [Fact]
    public void Failed_ends_any_active_customer_and_terminal_states_ignore_everything()
    {
        var store = NewStore();

        Assert.True(store.Apply(CustomerEvent.Failed("c1", "boom")));
        Assert.False(store.Apply(CustomerEvent.TrialStarted("c1")));
        Assert.False(store.Apply(CustomerEvent.Final(CycleResult.Ended("c1"))));

        var c = store.Get("c1")!;
        Assert.Equal((CustomerPhase.Failed, "boom"), (c.Phase, c.Error));
        Assert.False(c.Active);
    }

    [Fact]
    public void Events_for_unknown_customers_are_ignored()
    {
        Assert.False(NewStore().Apply(CustomerEvent.TrialStarted("nope")));
    }

    [Fact]
    public void Subscribers_receive_every_snapshot_in_order()
    {
        var store = NewStore();
        using var sub = store.Watch("c1");

        store.Apply(CustomerEvent.TrialStarted("c1"));
        store.TrySubscribe("c1", Sub());
        store.Apply(CustomerEvent.Subscribed("c1", Plans.Basic));

        var phases = new List<CustomerPhase>();
        while (sub.Reader.TryRead(out var s)) phases.Add(s.Phase);
        Assert.Equal([CustomerPhase.Trial, CustomerPhase.Trial, CustomerPhase.Subscribed], phases);
    }

    [Fact]
    public void Remove_forgets_the_customer_and_completes_the_stream()
    {
        var store = NewStore();
        using var sub = store.Watch("c1");

        store.Remove("c1");

        Assert.Null(store.Get("c1"));
        Assert.True(sub.Reader.Completion.IsCompleted);
    }
}
