# skail subscription cycle (.NET 8)

[Português (Brasil)](README.pt-BR.md)

An app with a skail function that runs the whole life of a subscription. The customer starts a trial, and the function waits for the trial deadline or for the customer to subscribe, whichever comes first. After the subscription it charges the plan once a month: a declined charge gets one retry, and a second decline suspends the subscription. A cancellation stops the charges, and after 12 months the cycle ends. The trial lasts 20 seconds and the month 10 seconds here; a real subscription uses 30 days and one month, with the same code. You play the customer on a page the app serves at http://localhost:8080.

It shows three skail features:

- **recurring billing**: a subscription is a loop that charges and waits for the next month;
- the **wait with a deadline**: the trial ends at the deadline or when the customer subscribes, whichever comes first;
- **hibernation and resume**: between charges the run holds no thread or process, so you can close the app and the months keep counting.

## Requirements

- One of: [Docker](https://docs.docker.com/get-docker/), [Podman](https://podman.io/docs/installation) or the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer). The run script uses the first one it finds.
- A skail environment with a workload registered for the `SKAIL_WORKLOAD` in `.env` (`subscriptioncycle:v1.0.0` by default). The [Quickstart](https://docs.skail.dev/comece-aqui/quickstart) shows how to create the account, the environment and the workload.

The project uses `Skail.Platform.Runtime` **2.3.0-rc.4**.

## Run

```bash
cp .env.example .env          # fill in SKAIL_SIDECAR, SKAIL_NAMESPACE and SKAIL_KEY (portal > Environments & keys > Connect)
scripts/run.sh                # then open http://localhost:8080
```

On Windows, in PowerShell: `copy .env.example .env`, then `powershell -ExecutionPolicy Bypass -File scripts\run.ps1` with the same arguments. The script works on the Windows PowerShell built into Windows; PowerShell 7 is not needed.

The script runs the demo with what the machine has: Docker, then Podman, then the .NET SDK. If it finds none of them, it says what to install. Without the script, the commands are `dotnet run` (with the arguments after `--`, as in `dotnet run -- <argument>`).

![The demo page](docs/page.png)

You play the customer. Pick the card the customer has (approved, declined once a month, or always declined) and click **Start trial**. The page counts down the trial, turns **Subscribe Basic** and **Subscribe Pro** on during it and **Cancel subscription** after it, and shows the 12 months as they go: paid, paid on the retry, declined. Each button sends the event to the run; each step also shows up under **What happened** and in the console, and the page refreshes itself every second.

## The paths

| Card and what you click | What happens | Result |
|---|---|---|
| **Start trial**, then nothing | the trial ends at the deadline and nothing is charged | `trial ended` |
| **Card approved**, **Subscribe** | one charge per month, up to month 12 | `cycle ended` |
| **Card approved**, **Subscribe**, then **Cancel subscription** | the charges stop, and the months already paid stay paid | `cancelled` |
| **Card declined once a month**, **Subscribe** | every month the first attempt is declined, the customer is warned and the retry 5 seconds later is approved | the cycle goes on |
| **Card always declined**, **Subscribe** | month 1 is declined twice | `suspended` |

With `declined-once`:

```text
[c914edc4] month 1: charge declined, card declined by the issuer
[c914edc4] customer warned: month 1 was declined (card declined by the issuer), new attempt in 5 s
[c914edc4] month 1: R$ 29.90 charged (attempt 2)
```

## How it works

The app does three jobs in one process: it serves the page, it hosts the worker that runs the function (`builder.UseSkail()`), and it starts the customer with the chosen card and turns the buttons into the subscription and cancellation events, through the skail HTTP API. The commands report each step to a timeline the page shows:

```http
POST {SKAIL_SIDECAR}/api/v1/trigger/subscriptioncycle/v1.0.0/ManageSubscription/{customerId}
["{customerId}", 20, 10]

POST {SKAIL_SIDECAR}/api/v1/fire/CUSTOMER_SUBSCRIBED/{customerId}
{"Plan": "pro", "At": "2026-11-18T14:42:05Z"}

POST {SKAIL_SIDECAR}/api/v1/fire/SUBSCRIPTION_CANCELLED/{customerId}
{"Reason": "cancelled in the demo", "At": "2026-11-18T14:42:38Z"}
```

The function, in [`SubscriptionFunctions.cs`](SubscriptionFunctions.cs):

```csharp
[SkailFunction]
public async SkailTask<string> ManageSubscription(string customerId, int trialSeconds, int monthSeconds)
{
    await StartTrial(customerId, trialSeconds);

    var subscription = SkailTask.WaitForEvent<Subscription>(Events.CustomerSubscribed, customerId);
    if (await SkailTask.WhenAny(SkailTask.DelaySeconds((ulong)trialSeconds), subscription) != subscription)
        return await EndTrial(customerId);

    var plan = (await subscription).Plan;
    await StartSubscription(customerId, plan);

    var cancellation = SkailTask.WaitForEvent<Cancellation>(Events.SubscriptionCancelled, customerId);

    for (var month = 1; month <= MaxMonths; month++)
    {
        var charge = await ChargeMonthlyFee(customerId, plan, month, 1);
        if (!charge.Approved)
        {
            await NotifyPaymentDeclined(customerId, month, charge.Reason!);
            await SkailTask.DelaySeconds(RetrySeconds);
            charge = await ChargeMonthlyFee(customerId, plan, month, 2);
        }
        if (!charge.Approved)
            return await SuspendSubscription(customerId, month);

        if (await SkailTask.WhenAny(cancellation, SkailTask.DelaySeconds((ulong)monthSeconds)) == cancellation)
            return await CancelSubscription(customerId, month);
    }

    return await EndCycle(customerId, MaxMonths);
}
```

- The wait for `SUBSCRIPTION_CANCELLED` is created once, before the loop, and reused every month.
- The function decides by the task `WhenAny` returned, not by `IsCompleted`: on a resume, `IsCompleted` already sees an event that arrived after the deadline.
- A declined card is a result, not an error: another attempt in the same second would get the same answer, so the function waits 5 seconds and tries once more.
- `ChargeMonthlyFee` gets the month and the attempt, and the simulated card network answers only by the card and the attempt, so a technical retry of the command does not charge the month twice.
- The function gets only the customer id and the durations. The card stays in the app, which keeps it by customer like a payment gateway keeps the card on file, and the charge command reads it there, so it never enters the run history or the Monitor.

| File | What it has |
|---|---|
| `SubscriptionFunctions.cs` | The function and its commands. |
| `Model.cs` | The plans, the event names, the simulated cards and the event payloads. |
| `Demo/SubscriptionDesk.cs` | Starts the customer and turns each button into an event; keeps the card on file. |
| `Demo/Timeline.cs` | The timeline of the steps, for the console and the page. |
| `Page/index.html` | The page, with the skail branding. |
| `Skail/SkailApiClient.cs` | Trigger and fire through the skail HTTP API. |
| `Program.cs` | Reads `.env` and the arguments, serves the page and its API, and wires everything up. |
| `scripts/run.sh`, `scripts/run.ps1`, `Dockerfile` | Run the demo with Docker, Podman or .NET, whatever the machine has. |

## See the durability

1. Run `scripts/run.sh`, open the page, start a trial, subscribe and wait for `month 1: ... charged`.
2. Press **Ctrl+C**. The app closes; the subscription stays in skail, waiting for the next month.
3. Wait some seconds and start the app again with the customer id shown on the page and in the console, and the same card: `scripts/run.sh <customer id> approved`. With an id, the app starts no new customer: it only hosts the worker again and follows that customer.
4. The month that came due while the app was closed is charged at once, and the next ones keep the 10-second rhythm. Click **Cancel subscription** to stop.

In the portal, open the [Monitor](https://docs.skail.dev/referencia/portal/monitor) of your environment and click the run: each month shows up as `ChargeMonthlyFee` followed by a `WhenAny` with its `Delay`. Compare **Duration** with **E2E Duration**: the waits do not use the worker.

## Notes

- The run has a limit of 12 months. Cancel the subscriptions you start, so they do not keep running in your environment.
- Apps running the same `SKAIL_WORKLOAD` on the same environment share the same runs: use one workload (or environment) per person.

## Disclaimer

**This is a demo, not production code.** It exists only to show how [skail](https://docs.skail.dev) works and is provided "as is", without warranty of any kind. The cards and the charges are simulated: no real payment is made.

## License

[Apache-2.0](LICENSE)
