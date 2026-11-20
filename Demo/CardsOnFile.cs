using System.Collections.Concurrent;

namespace SkailSubscriptionCycle.Demo;

/// <summary>
/// The card each customer chose, kept by the app like a payment gateway keeps the card on file. The charge command
/// reads it by customer id, so the card never goes into the run history or the Monitor.
/// </summary>
public sealed class CardsOnFile
{
    private readonly ConcurrentDictionary<string, string> cards = new();

    public void Keep(string customerId, string card) => cards[customerId] = card;

    public string For(string customerId) => cards.GetValueOrDefault(customerId, Cards.Approved);
}
