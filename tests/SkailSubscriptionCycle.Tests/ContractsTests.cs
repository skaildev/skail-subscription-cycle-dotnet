using System.Text.Json;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Tests;

public class ContractsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_names_are_shared_constants()
    {
        Assert.Equal("CUSTOMER_SUBSCRIBED", Events.CustomerSubscribed);
        Assert.Equal("SUBSCRIPTION_CANCELLED", Events.SubscriptionCancelled);
    }

    [Theory]
    [InlineData("basic", true)]
    [InlineData("pro", true)]
    [InlineData("gold", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Planos_validates_the_plan(string? plan, bool valid) => Assert.Equal(valid, Plans.IsValid(plan));

    [Fact]
    public void Planos_have_a_monthly_price()
    {
        Assert.Equal(29.90m, Plans.Price(Plans.Basic));
        Assert.Equal(79.90m, Plans.Price(Plans.Pro));
        Assert.Throws<ArgumentException>(() => Plans.Price("gold"));
    }

    [Theory]
    [InlineData("approved", true)]
    [InlineData("declined-once", true)]
    [InlineData("always-declined", true)]
    [InlineData("amex", false)]
    [InlineData(null, false)]
    public void Cartoes_validates_the_simulated_card(string? card, bool valid) => Assert.Equal(valid, Cards.IsValid(card));

    [Theory]
    [InlineData("approved", 1, true)]
    [InlineData("approved", 2, true)]
    [InlineData("declined-once", 1, false)]
    [InlineData("declined-once", 2, true)]
    [InlineData("always-declined", 1, false)]
    [InlineData("always-declined", 2, false)]
    public void Cartoes_decides_each_attempt_from_the_card(string card, int attempt, bool approved)
    {
        var charge = Cards.ChargeFor(card, Plans.Pro, attempt);

        Assert.Equal(approved, charge.Approved);
        Assert.Equal(Plans.Price(Plans.Pro), charge.Amount);
        Assert.Equal(approved, charge.Reason is null);
    }

    [Fact]
    public void Ended_result_has_no_charges()
    {
        var r = CycleResult.Ended("c1");

        Assert.Equal((CycleEnd.Ended, 0, 0m), (r.End, r.MonthsPaid, r.TotalCharged));
    }

    [Fact]
    public void Suspended_result_counts_the_months_before_the_failed_one()
    {
        var r = CycleResult.Suspended("c1", Plans.Basic, 3, Cards.DeclineReason);

        Assert.Equal(CycleEnd.Suspended, r.End);
        Assert.Equal(2, r.MonthsPaid);
        Assert.Equal(2 * 29.90m, r.TotalCharged);
        Assert.Equal(3, r.Month);
        Assert.Equal(Cards.DeclineReason, r.Reason);
    }

    [Fact]
    public void Cancelled_and_expired_results_count_the_paid_months()
    {
        var cancelled = CycleResult.Cancelled("c1", Plans.Pro, 2);
        var expired = CycleResult.Expired("c1", Plans.Pro, 12);

        Assert.Equal((CycleEnd.Cancelled, 2, 2 * 79.90m), (cancelled.End, cancelled.MonthsPaid, cancelled.TotalCharged));
        Assert.Equal((CycleEnd.Expired, 12, 12 * 79.90m), (expired.End, expired.MonthsPaid, expired.TotalCharged));
    }

    [Fact]
    public void Event_factories_set_the_stage()
    {
        Assert.Equal(CustomerStage.TrialStarted, CustomerEvent.TrialStarted("c1").Stage);
        Assert.Equal(CustomerStage.Subscribed, CustomerEvent.Subscribed("c1", Plans.Pro).Stage);
        Assert.Equal(CustomerStage.Retrying, CustomerEvent.Retrying("c1", 1, Cards.DeclineReason).Stage);
        var failed = CustomerEvent.Failed("c1", "boom");
        Assert.Equal((CustomerStage.Failed, "boom"), (failed.Stage, failed.Error));
        var ended = CustomerEvent.Final(CycleResult.Ended("c1"));
        Assert.Equal(CustomerStage.TrialEnded, ended.Stage);
        Assert.Equal(CustomerStage.Suspended, CustomerEvent.Final(CycleResult.Suspended("c1", Plans.Pro, 1, "x")).Stage);
        Assert.Equal(CustomerStage.Cancelled, CustomerEvent.Final(CycleResult.Cancelled("c1", Plans.Pro, 1)).Stage);
        Assert.Equal(CustomerStage.Expired, CustomerEvent.Final(CycleResult.Expired("c1", Plans.Pro, 12)).Stage);
    }

    [Fact]
    public void Subscription_and_cancellation_round_trip_as_json()
    {
        var subscription = new Subscription(Plans.Pro, T0);
        var cancellation = new Cancellation("too expensive", T0);

        Assert.Equal(subscription, JsonSerializer.Deserialize<Subscription>(JsonSerializer.Serialize(subscription, Json.Options), Json.Options));
        Assert.Equal(cancellation, JsonSerializer.Deserialize<Cancellation>(JsonSerializer.Serialize(cancellation, Json.Options), Json.Options));
    }

    [Fact]
    public void Customer_event_and_charge_round_trip_as_json()
    {
        var evt = CustomerEvent.Final(CycleResult.Suspended("c1", Plans.Basic, 2, Cards.DeclineReason));
        var charge = new Charge(false, Cards.DeclineReason, 29.90m);

        Assert.Equal(evt, JsonSerializer.Deserialize<CustomerEvent>(JsonSerializer.Serialize(evt, Json.Options), Json.Options));
        Assert.Equal(charge, JsonSerializer.Deserialize<Charge>(JsonSerializer.Serialize(charge, Json.Options), Json.Options));
    }
}
