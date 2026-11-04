namespace SkailSubscriptionCycle.Web.Skail;

public sealed class SkailOptions
{
    /// <summary>SKAIL_BASE_URL: base URL of the skail HTTP API for the environment, without /api/v1.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>SKAIL_NAMESPACE (tenant/environment), sent in the Skail-namespace header.</summary>
    public string Namespace { get; init; } = "";

    /// <summary>SKAIL_KEY, sent in the skail-key and Authorization headers. Secret.</summary>
    public string Key { get; init; } = "";

    /// <summary>SKAIL_WORKLOAD as name:version, exactly as registered in the portal.</summary>
    public string Workload { get; init; } = "";

    public static SkailOptions FromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("SKAIL_BASE_URL") ?? "",
        Namespace = Environment.GetEnvironmentVariable("SKAIL_NAMESPACE") ?? "",
        Key = Environment.GetEnvironmentVariable("SKAIL_KEY") ?? "",
        Workload = Environment.GetEnvironmentVariable("SKAIL_WORKLOAD") ?? "",
    };
}
