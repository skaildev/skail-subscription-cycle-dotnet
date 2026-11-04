using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Worker;

public class SubscriptionFunctions(SubscriptionCommands commands)
{
    /// <summary>
    /// The whole life of a subscription in one function: the trial, which ends at the deadline or when the customer
    /// subscribes, then one charge per month, with one retry of a declined charge, until a cancellation, a suspension
    /// or the month limit. The durations are arguments and the decisions come only from recorded events, so a replay
    /// takes the same path.
    /// </summary>
    [SkailFunction]
    public async SkailTask<CycleResult> ManageSubscription(string customerId, int trialSeconds, int monthSeconds)
    {
        try
        {
            await commands.StartTrial(customerId);

            // Both waits are created before the WhenAny, without await.
            var trialDeadline = SkailTask.DelaySeconds((ulong)trialSeconds);
            var subscription = SkailTask.WaitForEvent<Subscription>(Events.CustomerSubscribed, customerId);

            // WhenAny returns the task that finished first. Decide by it, not by subscription.IsCompleted: on a
            // resume, IsCompleted already sees a subscription that arrived after the deadline.
            if (await SkailTask.WhenAny(trialDeadline, subscription) != subscription)
                return await commands.EndTrial(customerId);

            var plan = (await subscription).Plan;
            await commands.StartSubscription(customerId, plan);

            // One wait for the cancellation, created before the loop and reused by every month.
            var cancellation = SkailTask.WaitForEvent<Cancellation>(Events.SubscriptionCancelled, customerId);

            for (var month = 1; month <= CycleLimits.MaxMonths; month++)
            {
                var charge = await commands.ChargeMonthlyFee(customerId, plan, month, 1);
                if (!charge.Approved)
                {
                    await commands.NotifyPaymentDeclined(customerId, month, charge.Reason ?? "");
                    await SkailTask.DelaySeconds(CycleLimits.RetrySeconds);
                    charge = await commands.ChargeMonthlyFee(customerId, plan, month, 2);
                }

                if (!charge.Approved)
                    return await commands.SuspendSubscription(customerId, plan, month, charge.Reason ?? "");

                if (await SkailTask.WhenAny(cancellation, SkailTask.DelaySeconds((ulong)monthSeconds)) == cancellation)
                    return await commands.CancelSubscription(customerId, plan, month);
            }

            return await commands.EndCycle(customerId, plan, CycleLimits.MaxMonths);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            await commands.ReportStatus(CustomerEvent.Failed(customerId, ex.Message));
            throw;
        }
    }
}
