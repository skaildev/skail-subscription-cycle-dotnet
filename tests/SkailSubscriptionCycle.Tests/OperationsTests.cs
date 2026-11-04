using Microsoft.Extensions.Logging.Abstractions;
using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Worker;
using SkailSubscriptionCycle.Worker.Progress;

namespace SkailSubscriptionCycle.Tests;

public sealed class RecordingNotifier : IProgressNotifier
{
    public List<CustomerEvent> Events { get; } = [];
    public Exception? FailWith { get; set; }

    public Task NotifyAsync(CustomerEvent evt, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>Fake of the web app's simulated card network.</summary>
public sealed class FakeCharges(Func<string, string, int, int, Charge>? respond = null) : IChargeGateway
{
    public List<(string CustomerId, string Plan, int Month, int Attempt)> Requests { get; } = [];
    public Exception? FailWith { get; set; }

    public Task<Charge> ChargeAsync(string customerId, string plan, int month, int attempt, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Requests.Add((customerId, plan, month, attempt));
        return Task.FromResult((respond ?? ((_, p, _, _) => new Charge(true, null, Plans.Price(p)))) (customerId, plan, month, attempt));
    }
}

/// <summary>Commands need the skail runtime; the logic they delegate to is tested here.</summary>
public class OperationsTests
{
    private static (SubscriptionOperations Ops, RecordingNotifier Notifier, FakeCharges Charges) Create(FakeCharges? charges = null)
    {
        var notifier = new RecordingNotifier();
        charges ??= new FakeCharges();
        return (new SubscriptionOperations(notifier, charges, NullLogger<SubscriptionOperations>.Instance), notifier, charges);
    }

    [Fact]
    public async Task StartTrial_notifies_the_start()
    {
        var (ops, notifier, _) = Create();

        await ops.StartTrialAsync("c1");

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((CustomerStage.TrialStarted, "c1"), (evt.Stage, evt.CustomerId));
    }

    [Fact]
    public async Task EndTrial_returns_the_ended_result_and_notifies_it()
    {
        var (ops, notifier, _) = Create();

        var result = await ops.EndTrialAsync("c1");

        Assert.Equal(CycleEnd.Ended, result.End);
        var evt = Assert.Single(notifier.Events);
        Assert.Equal((CustomerStage.TrialEnded, result), (evt.Stage, evt.Result));
    }

    [Fact]
    public async Task StartSubscription_notifies_the_plan()
    {
        var (ops, notifier, _) = Create();

        await ops.StartSubscriptionAsync("c1", Plans.Pro);

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((CustomerStage.Subscribed, Plans.Pro), (evt.Stage, evt.Plan));
    }

    [Fact]
    public async Task StartSubscription_rejects_an_unknown_plan_without_notifying()
    {
        var (ops, notifier, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => ops.StartSubscriptionAsync("c1", "gold"));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task ChargeMonthlyFee_asks_the_card_network_and_returns_its_answer()
    {
        var declined = new Charge(false, Cards.DeclineReason, 29.90m);
        var (ops, _, charges) = Create(new FakeCharges((_, _, _, _) => declined));

        var charge = await ops.ChargeMonthlyFeeAsync("c1", Plans.Basic, 3, 2);

        Assert.Equal(declined, charge);
        Assert.Equal(("c1", Plans.Basic, 3, 2), Assert.Single(charges.Requests));
    }

    [Fact]
    public async Task ChargeMonthlyFee_throws_when_the_card_network_fails_so_skail_retries()
    {
        var (ops, _, charges) = Create();
        charges.FailWith = new HttpRequestException("down");

        await Assert.ThrowsAsync<HttpRequestException>(() => ops.ChargeMonthlyFeeAsync("c1", Plans.Basic, 1, 1));
    }

    [Fact]
    public async Task ChargeMonthlyFee_rejects_an_invalid_month_or_attempt()
    {
        var (ops, _, charges) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ops.ChargeMonthlyFeeAsync("c1", Plans.Basic, 0, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ops.ChargeMonthlyFeeAsync("c1", Plans.Basic, 1, 0));
        Assert.Empty(charges.Requests);
    }

    [Fact]
    public async Task NotifyPaymentDeclined_notifies_the_retry_with_the_month_and_the_reason()
    {
        var (ops, notifier, _) = Create();

        await ops.NotifyPaymentDeclinedAsync("c1", 3, Cards.DeclineReason);

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((CustomerStage.Retrying, 3, Cards.DeclineReason), (evt.Stage, evt.Month, evt.Reason));
    }

    [Fact]
    public async Task SuspendSubscription_returns_the_suspended_result_and_notifies_it()
    {
        var (ops, notifier, _) = Create();

        var result = await ops.SuspendSubscriptionAsync("c1", Plans.Basic, 2, Cards.DeclineReason);

        Assert.Equal((CycleEnd.Suspended, 2, Cards.DeclineReason), (result.End, result.Month, result.Reason));
        Assert.Equal((CustomerStage.Suspended, result), (notifier.Events[0].Stage, notifier.Events[0].Result));
    }

    [Fact]
    public async Task CancelSubscription_and_EndCycle_return_and_notify_their_results()
    {
        var (ops, notifier, _) = Create();

        var cancelled = await ops.CancelSubscriptionAsync("c1", Plans.Pro, 2);
        var expired = await ops.EndCycleAsync("c2", Plans.Pro, 12);

        Assert.Equal((CycleEnd.Cancelled, 2), (cancelled.End, cancelled.MonthsPaid));
        Assert.Equal((CycleEnd.Expired, 12), (expired.End, expired.MonthsPaid));
        Assert.Equal([CustomerStage.Cancelled, CustomerStage.Expired], notifier.Events.Select(e => e.Stage));
    }

    [Fact]
    public async Task Final_commands_are_idempotent_the_result_derives_from_the_arguments()
    {
        var (ops, _, _) = Create();

        Assert.Equal(await ops.CancelSubscriptionAsync("c1", Plans.Pro, 2), await ops.CancelSubscriptionAsync("c1", Plans.Pro, 2));
        Assert.Equal(await ops.EndTrialAsync("c1"), await ops.EndTrialAsync("c1"));
    }

    [Fact]
    public async Task ReportStatus_forwards_the_event_and_propagates_failures()
    {
        var (ops, notifier, _) = Create();
        var evt = CustomerEvent.Failed("c1", "boom");

        await ops.ReportStatusAsync(evt);
        Assert.Equal(evt, Assert.Single(notifier.Events));

        notifier.FailWith = new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => ops.ReportStatusAsync(evt));
    }
}
