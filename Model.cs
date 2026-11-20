namespace SkailSubscriptionCycle;

/// <summary>The plans, as plain strings (they travel in the payload of an event), each with a monthly price.</summary>
public static class Plans
{
    public const string Basic = "basic";
    public const string Pro = "pro";

    public static decimal Price(string plan) => plan == Pro ? 79.90m : 29.90m;
}

/// <summary>External event names, shared by the function (WaitForEvent) and the console (fire).</summary>
public static class Events
{
    public const string CustomerSubscribed = "CUSTOMER_SUBSCRIBED";
    public const string SubscriptionCancelled = "SUBSCRIPTION_CANCELLED";
}

/// <summary>
/// The simulated cards. The answer of the card network depends only on the card and the attempt, so a technical retry
/// of the command gets the same answer.
/// </summary>
public static class Cards
{
    public const string Approved = "approved";
    public const string DeclinedOnce = "declined-once";
    public const string AlwaysDeclined = "always-declined";

    public static readonly string[] All = [Approved, DeclinedOnce, AlwaysDeclined];

    public static bool IsApproved(string card, int attempt) => card switch
    {
        DeclinedOnce => attempt > 1,
        AlwaysDeclined => false,
        _ => true,
    };
}

/// <summary>The customer subscribed: payload of CUSTOMER_SUBSCRIBED. At is stamped by whoever fires the event.</summary>
public sealed record Subscription(string Plan, DateTimeOffset At);

/// <summary>The customer cancelled: payload of SUBSCRIPTION_CANCELLED.</summary>
public sealed record Cancellation(string? Reason, DateTimeOffset At);

/// <summary>Result of one charge attempt: a decline is a result, not an error.</summary>
public sealed record Charge(bool Approved, string? Reason, decimal Amount);
