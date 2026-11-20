using System.Globalization;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSubscriptionCycle.Demo;

namespace SkailSubscriptionCycle;

public class SubscriptionFunctions(Timeline timeline, CardsOnFile cards)
{
    private const int MaxMonths = 12;
    private const int RetrySeconds = 5;

    /// <summary>
    /// The whole life of a subscription: the trial, which ends at the deadline or when the customer subscribes, then
    /// one charge per month, with one retry of a declined charge, until a cancellation, a suspension or 12 months. The
    /// durations are arguments, so the same code serves the demo (seconds) and a real subscription (30 days, a month).
    /// The name and the parameters are the trigger contract: ["{customerId}", {trialSeconds}, {monthSeconds}].
    /// </summary>
    [SkailFunction]
    public async SkailTask<string> ManageSubscription(string customerId, int trialSeconds, int monthSeconds)
    {
        await StartTrial(customerId, trialSeconds);

        // decide by the task WhenAny returns, not by subscription.IsCompleted: on a resume, IsCompleted already sees a
        // subscription that arrived after the deadline
        var subscription = SkailTask.WaitForEvent<Subscription>(Events.CustomerSubscribed, customerId);
        if (await SkailTask.WhenAny(SkailTask.DelaySeconds((ulong)trialSeconds), subscription) != subscription)
            return await EndTrial(customerId);

        var plan = (await subscription).Plan;
        await StartSubscription(customerId, plan);

        // one wait for the cancellation, created before the loop and reused every month
        var cancellation = SkailTask.WaitForEvent<Cancellation>(Events.SubscriptionCancelled, customerId);

        for (var month = 1; month <= MaxMonths; month++)
        {
            var charge = await ChargeMonthlyFee(customerId, plan, month, 1);
            if (!charge.Approved)
            {
                await NotifyPaymentDeclined(customerId, month, charge.Reason!);
                await SkailTask.DelaySeconds(RetrySeconds);
                charge = await ChargeMonthlyFee(customerId, plan, month, 2);
            }
            if (!charge.Approved)
                return await SuspendSubscription(customerId, month);

            if (await SkailTask.WhenAny(cancellation, SkailTask.DelaySeconds((ulong)monthSeconds)) == cancellation)
                return await CancelSubscription(customerId, month);
        }

        return await EndCycle(customerId, MaxMonths);
    }

    // The commands are the I/O: the charge asks the simulated card network, the others report what would happen, to the
    // console and to the page.
    // A completed command never runs again, so a replay does not charge twice nor print the same line twice.

    [SkailCommand(retryCount: 3)]
    public async SkailTask StartTrial(string customerId, int trialSeconds) =>
        await Report(customerId, $"trial started: {trialSeconds} s to subscribe", "trial:started");

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> EndTrial(string customerId)
    {
        await Report(customerId, "trial ended without a subscription: nothing was charged", "end:trial-ended");
        return "trial ended";
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask StartSubscription(string customerId, string plan) =>
        await Report(customerId, $"subscribed to the {plan} plan, {Money(Plans.Price(plan))} a month", $"subscribed:{plan}");

    /// <summary>
    /// The card network answers by the card and the attempt, so a retry of the command gets the same answer and the
    /// month is not charged twice. A decline is a result: another attempt in the same second would get the same answer.
    /// </summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<Charge> ChargeMonthlyFee(string customerId, string plan, int month, int attempt)
    {
        // the card on file is read here, by customer id: it never enters the run history
        var charge = Cards.IsApproved(cards.For(customerId), attempt)
            ? new Charge(true, null, Plans.Price(plan))
            : new Charge(false, "card declined by the issuer", Plans.Price(plan));
        var retry = attempt > 1 ? $" (attempt {attempt})" : "";
        await Report(customerId, charge.Approved
            ? $"month {month}: {Money(charge.Amount)} charged{retry}"
            : $"month {month}: charge declined{retry}, {charge.Reason}",
            charge.Approved ? $"charged:{month}" : $"declined:{month}");
        return charge;
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask NotifyPaymentDeclined(string customerId, int month, string reason) =>
        await Report(customerId, $"customer warned: month {month} was declined ({reason}), new attempt in {RetrySeconds} s", $"warned:{month}");

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> SuspendSubscription(string customerId, int month)
    {
        await Report(customerId, $"suspended: month {month} was declined twice", "end:suspended");
        return "suspended";
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> CancelSubscription(string customerId, int lastPaidMonth)
    {
        await Report(customerId, $"cancelled: no more charges; months 1 to {lastPaidMonth} stay paid", "end:cancelled");
        return "cancelled";
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> EndCycle(string customerId, int months)
    {
        await Report(customerId, $"cycle ended: {months} months paid", "end:cycle-ended");
        return "cycle ended";
    }

    private Task Report(string customerId, string message, string step)
    {
        timeline.Add(customerId, message, step);
        return Task.CompletedTask;
    }

    private static string Money(decimal value) => "R$ " + value.ToString("N2", CultureInfo.InvariantCulture);
}
