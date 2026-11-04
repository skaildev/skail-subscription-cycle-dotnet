using SkailSubscriptionCycle.Contracts;

namespace SkailSubscriptionCycle.Web.Customers;

public enum CustomerPhase
{
    Starting,
    Trial,
    Subscribed,

    /// <summary>A charge was declined and the customer waits for its one retry.</summary>
    Declined,
    Ended,
    Cancelled,
    Suspended,
    Expired,
    Failed,
}

public enum DecisionOutcome
{
    Accepted,
    Duplicate,

    /// <summary>The action does not apply in the current phase (trial over, nothing to cancel, finished).</summary>
    NotAllowed,
    NotFound,
}

public sealed record ChargeRecord(int Month, int Attempt, Charge Charge);

public sealed record TimelineEntry(string Kind, string Text, DateTimeOffset At);

public sealed record CustomerState(
    string CustomerId,
    string Name,
    string Plan,
    string Card,
    int TrialSeconds,
    int MonthSeconds,
    CustomerPhase Phase,
    int Month,
    decimal TotalCharged,
    bool SubscribeRequested,
    bool CancelRequested,
    DateTimeOffset? TrialStartedAt,
    IReadOnlyList<ChargeRecord> Charges,
    IReadOnlyList<TimelineEntry> Timeline,
    CycleResult? Result,
    string? Error)
{
    public bool IsTerminal => Phase is CustomerPhase.Ended or CustomerPhase.Cancelled or CustomerPhase.Suspended
        or CustomerPhase.Expired or CustomerPhase.Failed;

    public bool Active => !IsTerminal;
}
