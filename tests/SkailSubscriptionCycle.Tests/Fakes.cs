using System.Net;

namespace SkailSubscriptionCycle.Tests;

/// <summary>HttpMessageHandler that returns scripted responses and records the requests.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        return respond(request, Calls.Count);
    }

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(status) { Content = new StringContent(json) });

    public static StubHandler Status(HttpStatusCode status) =>
        new((_, _) => new HttpResponseMessage(status));
}
