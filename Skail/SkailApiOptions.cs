namespace SkailSubscriptionCycle.Skail;

/// <summary>
/// What the HTTP API calls need. The base URL is SKAIL_BASE_URL or, when it is not set, the environment address
/// in SKAIL_SIDECAR, which serves the same API.
/// </summary>
public sealed record SkailApiOptions(string BaseUrl, string Namespace, string Key, string Workload)
{
    public static SkailApiOptions FromEnvironment() => new(
        BaseUrl: NotEmpty("SKAIL_BASE_URL") ?? NotEmpty("SKAIL_SIDECAR") ?? "",
        Namespace: Environment.GetEnvironmentVariable("SKAIL_NAMESPACE") ?? "",
        Key: Environment.GetEnvironmentVariable("SKAIL_KEY") ?? "",
        Workload: Environment.GetEnvironmentVariable("SKAIL_WORKLOAD") ?? "");

    private static string? NotEmpty(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
