using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Web.Skail;

public sealed class SkailGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISkailGateway
{
    /// <summary>
    /// Starts the ManageSubscription function; <paramref name="customerId"/> is the execution id. The durations are
    /// arguments of the function (it cannot read configuration).
    /// </summary>
    Task TriggerAsync(string customerId, int trialSeconds, int monthSeconds, CancellationToken ct = default);

    /// <summary>Fires CUSTOMER_SUBSCRIBED, which the trial waits on; the instance id is the customer id.</summary>
    Task FireSubscriptionAsync(string customerId, Subscription subscription, CancellationToken ct = default);

    /// <summary>Fires SUBSCRIPTION_CANCELLED, which the monthly loop waits on; the instance id is the customer id.</summary>
    Task FireCancellationAsync(string customerId, Cancellation cancellation, CancellationToken ct = default);
}
