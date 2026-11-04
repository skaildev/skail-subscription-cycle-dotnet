using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Worker;

/// <summary>
/// The I/O of the function, one operation per command. A retry runs the whole command again, so every command is
/// idempotent. The commands delegate to <see cref="SubscriptionOperations"/>, so their logic is tested without skail.
/// </summary>
public class SubscriptionCommands(SubscriptionOperations operations)
{
    [SkailCommand(retryCount: 3)]
    public async SkailTask StartTrial(string customerId)
        => await operations.StartTrialAsync(customerId);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<CycleResult> EndTrial(string customerId)
        => await operations.EndTrialAsync(customerId);

    [SkailCommand(retryCount: 3)]
    public async SkailTask StartSubscription(string customerId, string plan)
        => await operations.StartSubscriptionAsync(customerId, plan);

    /// <summary>Idempotent by customer, month and attempt: a technical retry gets the same answer from the card network.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<Charge> ChargeMonthlyFee(string customerId, string plan, int month, int attempt)
        => await operations.ChargeMonthlyFeeAsync(customerId, plan, month, attempt);

    [SkailCommand(retryCount: 3)]
    public async SkailTask NotifyPaymentDeclined(string customerId, int month, string reason)
        => await operations.NotifyPaymentDeclinedAsync(customerId, month, reason);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<CycleResult> SuspendSubscription(string customerId, string plan, int month, string reason)
        => await operations.SuspendSubscriptionAsync(customerId, plan, month, reason);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<CycleResult> CancelSubscription(string customerId, string plan, int lastPaidMonth)
        => await operations.CancelSubscriptionAsync(customerId, plan, lastPaidMonth);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<CycleResult> EndCycle(string customerId, string plan, int months)
        => await operations.EndCycleAsync(customerId, plan, months);

    /// <summary>Idempotent: the web app ignores a repeated event (same customer and stage).</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask ReportStatus(CustomerEvent evt)
        => await operations.ReportStatusAsync(evt);
}
