namespace SkailSubscriptionCycle.Demo;

/// <summary>One thing that happened in a run, as the commands report it.</summary>
/// <param name="Step">A short machine-readable tag the page uses (for example "charged:2" or "end:cancelled").</param>
public sealed record TimelineEntry(int Seq, string RunId, DateTimeOffset At, string Text, string? Step);

/// <summary>
/// What the commands did, in order: printed to the console and kept in memory for the page. In your system this would
/// be your own logs or database; skail keeps the real history of every run in the Monitor.
/// </summary>
public sealed class Timeline
{
    private readonly object gate = new();
    private readonly List<TimelineEntry> entries = [];

    public void Add(string runId, string text, string? step = null)
    {
        lock (gate)
        {
            entries.Add(new TimelineEntry(entries.Count + 1, runId, DateTimeOffset.UtcNow, text, step));
        }
        Console.WriteLine($"[{runId[..8]}] {text}");
    }

    public IReadOnlyList<TimelineEntry> For(string runId)
    {
        lock (gate) return entries.Where(e => e.RunId == runId).ToList();
    }
}
