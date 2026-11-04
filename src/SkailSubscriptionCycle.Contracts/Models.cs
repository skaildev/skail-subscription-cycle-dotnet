namespace SkailSubscriptionCycle.Contracts;

/// <summary>
/// The plans, as plain strings (<see cref="Subscription"/> is the payload of an external event), each with a
/// simulated monthly price.
/// </summary>
public static class Plans
{
    public const string Basic = "basic";
    public const string Pro = "pro";

    public static bool IsValid(string? plan) => plan is Basic or Pro;

    public static decimal Price(string plan) => plan switch
    {
        Basic => 29.90m,
        Pro => 79.90m,
        _ => throw new ArgumentException($"unknown plan '{plan}'", nameof(plan)),
    };
}

/// <summary>External event names, shared by the function (WaitForEvent) and the web app (fire).</summary>
public static class Events
{
    public const string CustomerSubscribed = "CUSTOMER_SUBSCRIBED";
    public const string SubscriptionCancelled = "SUBSCRIPTION_CANCELLED";
}

public static class CycleLimits
{
    public const string DefaultName = "Demo Customer";
    public const int MaxNameLength = 80;

    /// <summary>Defaults of the form on the page; a real subscription would use 30 days and a month.</summary>
    public const int DefaultTrialSeconds = 20;
    public const int DefaultMonthSeconds = 10;
    public const int MaxSeconds = 60 * 60 * 24 * 366;

    /// <summary>The demo does not charge forever: after this month without a cancellation the cycle ends as Expired.</summary>
    public const int MaxMonths = 12;

    /// <summary>Wait before the single retry of a declined charge.</summary>
    public const int RetrySeconds = 5;
}

/// <summary>Simulated credit cards: they decide, deterministically, whether a charge is approved.</summary>
public static class Cards
{
    public const string Approved = "approved";
    public const string DeclinedOnce = "declined-once";
    public const string AlwaysDeclined = "always-declined";

    public const string DeclineReason = "card declined by the issuer";

    public static bool IsValid(string? card) => card is Approved or DeclinedOnce or AlwaysDeclined;

    /// <summary>The simulated card network: the answer depends only on the card and the attempt, so a technical retry gets the same answer.</summary>
    public static Charge ChargeFor(string card, string plan, int attempt)
    {
        var approved = card switch
        {
            Approved => true,
            DeclinedOnce => attempt > 1,
            AlwaysDeclined => false,
            _ => throw new ArgumentException($"unknown card '{card}'", nameof(card)),
        };
        return new Charge(approved, approved ? null : DeclineReason, Plans.Price(plan));
    }
}

/// <summary>
/// The customer subscribed: payload of the <c>CUSTOMER_SUBSCRIBED</c> event. The event goes into the run history, so it
/// carries only the chosen plan and the time, stamped by the web app because the function cannot read the clock.
/// </summary>
public sealed record Subscription(string Plan, DateTimeOffset At);

/// <summary>The customer cancelled: payload of the <c>SUBSCRIPTION_CANCELLED</c> event.</summary>
public sealed record Cancellation(string? Reason, DateTimeOffset At);

/// <summary>Business result of one charge attempt: a decline is a result, not an exception.</summary>
public sealed record Charge(bool Approved, string? Reason, decimal Amount);

public enum CycleEnd
{
    /// <summary>The trial ended without a subscription.</summary>
    Ended,
    Cancelled,
    Suspended,
    Expired,
}

public sealed record CycleResult(
    string CustomerId,
    CycleEnd End,
    string? Plan,
    int MonthsPaid,
    decimal TotalCharged,
    /// <summary>Last paid month (Cancelled), failing month (Suspended) or last month (Expired).</summary>
    int? Month,
    string? Reason)
{
    public static CycleResult Ended(string customerId) => new(customerId, CycleEnd.Ended, null, 0, 0m, null, null);

    /// <summary>The charge of <paramref name="month"/> failed twice: the months before it were paid.</summary>
    public static CycleResult Suspended(string customerId, string plan, int month, string reason) =>
        new(customerId, CycleEnd.Suspended, plan, month - 1, (month - 1) * Plans.Price(plan), month, reason);

    public static CycleResult Cancelled(string customerId, string plan, int lastPaidMonth) =>
        new(customerId, CycleEnd.Cancelled, plan, lastPaidMonth, lastPaidMonth * Plans.Price(plan), lastPaidMonth, null);

    public static CycleResult Expired(string customerId, string plan, int months) =>
        new(customerId, CycleEnd.Expired, plan, months, months * Plans.Price(plan), months, null);
}

public enum CustomerStage
{
    TrialStarted,
    TrialEnded,
    Subscribed,
    Retrying,
    Suspended,
    Cancelled,
    Expired,
    Failed,
}

/// <summary>Progress reported by the worker to the web app. Subscribing and cancelling go straight to skail.</summary>
public sealed record CustomerEvent(
    string CustomerId,
    CustomerStage Stage,
    string? Plan = null,
    int? Month = null,
    string? Reason = null,
    CycleResult? Result = null,
    string? Error = null)
{
    public static CustomerEvent TrialStarted(string customerId) => new(customerId, CustomerStage.TrialStarted);

    public static CustomerEvent Subscribed(string customerId, string plan) =>
        new(customerId, CustomerStage.Subscribed, Plan: plan);

    public static CustomerEvent Retrying(string customerId, int month, string reason) =>
        new(customerId, CustomerStage.Retrying, Month: month, Reason: reason);

    public static CustomerEvent Failed(string customerId, string error) =>
        new(customerId, CustomerStage.Failed, Error: error);

    /// <summary>The event that ends the cycle, matching the result.</summary>
    public static CustomerEvent Final(CycleResult result) =>
        new(result.CustomerId, result.End switch
        {
            CycleEnd.Ended => CustomerStage.TrialEnded,
            CycleEnd.Cancelled => CustomerStage.Cancelled,
            CycleEnd.Suspended => CustomerStage.Suspended,
            _ => CustomerStage.Expired,
        }, Result: result);
}
