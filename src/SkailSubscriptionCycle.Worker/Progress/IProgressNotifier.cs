using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Worker.Progress;

public interface IProgressNotifier
{
    Task NotifyAsync(CustomerEvent evt, CancellationToken ct = default);
}

/// <summary>The card network that answers each monthly charge. A decline is a result, not an error.</summary>
public interface IChargeGateway
{
    Task<Charge> ChargeAsync(string customerId, string plan, int month, int attempt, CancellationToken ct = default);
}
