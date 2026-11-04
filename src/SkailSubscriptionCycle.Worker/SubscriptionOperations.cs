using Microsoft.Extensions.Logging;
using SkailSubscriptionCycle.Contracts;
using SkailSubscriptionCycle.Worker.Progress;

namespace SkailSubscriptionCycle.Worker;

/// <summary>
/// The I/O behind the commands, as plain tasks. Commands only run inside the skail runtime, so keeping the
/// logic here lets it be unit tested without skail. The web app plays the card network and the page: if a call
/// cannot be delivered the method throws and skail retries the command.
/// </summary>
public class SubscriptionOperations(IProgressNotifier notifier, IChargeGateway charges, ILogger<SubscriptionOperations> log)
{
    public async Task StartTrialAsync(string customerId)
    {
        log.LogInformation("Trial started for customer {CustomerId}", customerId);
        await notifier.NotifyAsync(CustomerEvent.TrialStarted(customerId));
    }

    /// <summary>Idempotent: the result derives from the arguments, and the web app ignores a repeated stage.</summary>
    public Task<CycleResult> EndTrialAsync(string customerId) =>
        FinishAsync(CycleResult.Ended(customerId), "Trial of customer {CustomerId} ended without a subscription");

    public async Task StartSubscriptionAsync(string customerId, string plan)
    {
        if (!Plans.IsValid(plan))
            throw new ArgumentException($"unknown plan '{plan}'", nameof(plan));

        log.LogInformation("Customer {CustomerId} subscribed to plan {Plan}", customerId, plan);
        await notifier.NotifyAsync(CustomerEvent.Subscribed(customerId, plan));
    }

    /// <summary>
    /// A decline is a result, not an error. The card network records each month and attempt once, so a technical
    /// retry gets the same answer and does not charge twice.
    /// </summary>
    public async Task<Charge> ChargeMonthlyFeeAsync(string customerId, string plan, int month, int attempt)
    {
        if (month < 1) throw new ArgumentOutOfRangeException(nameof(month), "the first month is 1");
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "the first attempt is 1");

        var charge = await charges.ChargeAsync(customerId, plan, month, attempt);
        log.LogInformation("Month {Month}, attempt {Attempt} of customer {CustomerId}: {Outcome}",
            month, attempt, customerId, charge.Approved ? "approved" : $"declined ({charge.Reason})");
        return charge;
    }

    public async Task NotifyPaymentDeclinedAsync(string customerId, int month, string reason)
    {
        log.LogInformation("Customer {CustomerId} warned about the declined payment of month {Month}: {Reason}", customerId, month, reason);
        await notifier.NotifyAsync(CustomerEvent.Retrying(customerId, month, reason));
    }

    public Task<CycleResult> SuspendSubscriptionAsync(string customerId, string plan, int month, string reason) =>
        FinishAsync(CycleResult.Suspended(customerId, plan, month, reason), "Subscription of customer {CustomerId} suspended");

    public Task<CycleResult> CancelSubscriptionAsync(string customerId, string plan, int lastPaidMonth) =>
        FinishAsync(CycleResult.Cancelled(customerId, plan, lastPaidMonth), "Subscription of customer {CustomerId} cancelled");

    public Task<CycleResult> EndCycleAsync(string customerId, string plan, int months) =>
        FinishAsync(CycleResult.Expired(customerId, plan, months), "Cycle of customer {CustomerId} reached the month limit");

    public Task ReportStatusAsync(CustomerEvent evt) => notifier.NotifyAsync(evt);

    // Idempotent: the result derives from the arguments, and the web app ignores a repeated final stage.
    private async Task<CycleResult> FinishAsync(CycleResult result, string message)
    {
        log.LogInformation(message, result.CustomerId);
        await notifier.NotifyAsync(CustomerEvent.Final(result));
        return result;
    }
}
