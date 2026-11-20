# skail subscription cycle (.NET 8)

[English](README.md)

Um app com uma function do skail que roda a vida inteira de uma assinatura. O cliente começa um teste grátis, e a function espera o fim do teste ou a assinatura do cliente, o que vier primeiro. Depois da assinatura, ela cobra o plano uma vez por mês: uma cobrança recusada ganha uma nova tentativa, e uma segunda recusa suspende a assinatura. Um cancelamento para as cobranças, e depois de 12 meses o ciclo termina. O teste dura 20 segundos e o mês 10 segundos aqui; uma assinatura real usa 30 dias e um mês, com o mesmo código. Você faz o papel do cliente numa página que o app serve em http://localhost:8080.

Ele mostra três recursos do skail:

- **cobrança recorrente**: uma assinatura é um laço que cobra e espera o próximo mês;
- a **espera com prazo**: o teste termina no prazo ou quando o cliente assina, o que vier primeiro;
- **hibernação e retomada**: entre as cobranças a execução não ocupa thread nem processo, então você pode fechar o app e os meses continuam contando.

## Requisitos

- Um destes: [Docker](https://docs.docker.com/get-docker/), [Podman](https://podman.io/docs/installation) ou o [SDK do .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) (ou mais novo). O script de execução usa o primeiro que encontrar.
- Um environment do skail com um workload registrado para o `SKAIL_WORKLOAD` do `.env` (`subscriptioncycle:v1.0.0` por padrão). O [Quickstart](https://docs.skail.dev/comece-aqui/quickstart) mostra como criar a conta, o environment e o workload.

O projeto usa o `Skail.Platform.Runtime` **2.3.0-rc.4**.

## Rodar

```bash
cp .env.example .env          # preencha SKAIL_SIDECAR, SKAIL_NAMESPACE e SKAIL_KEY (portal > Environments & keys > Connect)
scripts/run.sh                # depois abra http://localhost:8080
```

No Windows, no PowerShell: `copy .env.example .env` e depois `powershell -ExecutionPolicy Bypass -File scripts\run.ps1`, com os mesmos argumentos. O script funciona no Windows PowerShell que já vem no Windows; não precisa do PowerShell 7.

O script roda a demo com o que a máquina tiver: Docker, depois Podman, depois o SDK do .NET. Se não achar nenhum, ele diz o que instalar. Sem o script, os comandos são `dotnet run` (com os argumentos depois de `--`, como em `dotnet run -- <argumento>`).

![A página da demo](docs/page.png)

Você faz o papel do cliente. Escolha o cartão que ele tem (aprovado, recusado uma vez por mês ou sempre recusado) e clique em **Start trial**. A página mostra a contagem do teste grátis, liga **Subscribe Basic** e **Subscribe Pro** durante ele e **Cancel subscription** depois, e mostra os 12 meses conforme passam: pago, pago na nova tentativa, recusado. Cada botão envia o evento para a execução; cada passo também aparece em **What happened** e no console, e a página se atualiza sozinha a cada segundo.

## Os caminhos

| Cartão e o que você clica | O que acontece | Resultado |
|---|---|---|
| **Start trial**, depois nada | o teste termina no prazo e nada é cobrado | `trial ended` |
| **Card approved**, **Subscribe** | uma cobrança por mês, até o mês 12 | `cycle ended` |
| **Card approved**, **Subscribe**, depois **Cancel subscription** | as cobranças param, e os meses já pagos continuam pagos | `cancelled` |
| **Card declined once a month**, **Subscribe** | todo mês a primeira tentativa é recusada, o cliente é avisado e a nova tentativa 5 segundos depois é aprovada | o ciclo continua |
| **Card always declined**, **Subscribe** | o mês 1 é recusado duas vezes | `suspended` |

Com `declined-once`:

```text
[c914edc4] month 1: charge declined, card declined by the issuer
[c914edc4] customer warned: month 1 was declined (card declined by the issuer), new attempt in 5 s
[c914edc4] month 1: R$ 29.90 charged (attempt 2)
```

## Como funciona

O app faz três coisas no mesmo processo: serve a página, hospeda o worker que executa a function (`builder.UseSkail()`) e inicia o cliente com o cartão escolhido e transforma os botões nos eventos de assinatura e de cancelamento, pela API HTTP do skail. Os commands registram cada passo numa linha do tempo que a página mostra:

```http
POST {SKAIL_SIDECAR}/api/v1/trigger/subscriptioncycle/v1.0.0/ManageSubscription/{customerId}
["{customerId}", 20, 10]

POST {SKAIL_SIDECAR}/api/v1/fire/CUSTOMER_SUBSCRIBED/{customerId}
{"Plan": "pro", "At": "2026-11-18T14:42:05Z"}

POST {SKAIL_SIDECAR}/api/v1/fire/SUBSCRIPTION_CANCELLED/{customerId}
{"Reason": "cancelled in the demo", "At": "2026-11-18T14:42:38Z"}
```

A function, em [`SubscriptionFunctions.cs`](SubscriptionFunctions.cs):

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

- A espera por `SUBSCRIPTION_CANCELLED` é criada uma vez, antes do laço, e reaproveitada todo mês.
- A function decide pela task que o `WhenAny` devolveu, não pelo `IsCompleted`: numa retomada, o `IsCompleted` já enxerga um evento que chegou depois do prazo.
- Um cartão recusado é um resultado, não um erro: outra tentativa no mesmo segundo teria a mesma resposta, então a function espera 5 segundos e tenta mais uma vez.
- O `ChargeMonthlyFee` recebe o mês e a tentativa, e a rede de cartões simulada responde só pelo cartão e pela tentativa, então uma nova tentativa técnica do command não cobra o mês duas vezes.
- A function recebe só o id do cliente e as durações. O cartão fica no app, guardado por cliente como um gateway de pagamento guarda o cartão, e o command de cobrança lê dali, então ele nunca entra no histórico da execução nem no Monitor.

| Arquivo | O que tem |
|---|---|
| `SubscriptionFunctions.cs` | A function e os commands dela. |
| `Model.cs` | Os planos, os nomes dos eventos, os cartões simulados e os payloads dos eventos. |
| `Demo/SubscriptionDesk.cs` | Inicia o cliente e transforma cada botão num evento; guarda o cartão do cliente. |
| `Demo/Timeline.cs` | A linha do tempo dos passos, para o console e a página. |
| `Page/index.html` | A página, com o branding do skail. |
| `Skail/SkailApiClient.cs` | Trigger e fire pela API HTTP do skail. |
| `Program.cs` | Lê o `.env` e os argumentos, serve a página e a API dela e liga tudo. |
| `scripts/run.sh`, `scripts/run.ps1`, `Dockerfile` | Rodam a demo com Docker, Podman ou o .NET, o que houver na máquina. |

## Veja a durabilidade

1. Rode `scripts/run.sh`, abra a página, comece um teste, assine e espere o `month 1: ... charged`.
2. Aperte **Ctrl+C**. O app fecha; a assinatura continua no skail, esperando o próximo mês.
3. Espere alguns segundos e abra o app de novo com o id do cliente que aparece na página e no console, e o mesmo cartão: `scripts/run.sh <id do cliente> approved`. Com um id, o app não inicia outro cliente: só hospeda o worker de novo e acompanha esse cliente.
4. O mês que venceu com o app fechado é cobrado na hora, e os próximos mantêm o ritmo de 10 segundos. Clique em **Cancel subscription** para parar.

No portal, abra o [Monitor](https://docs.skail.dev/referencia/portal/monitor) do seu environment e clique na execução: cada mês aparece como `ChargeMonthlyFee` seguido de um `WhenAny` com o seu `Delay`. Compare **Duration** com **E2E Duration**: as esperas não ocupam o worker.

## Observações

- A execução tem um limite de 12 meses. Cancele as assinaturas que você iniciar, para elas não ficarem rodando no seu environment.
- Apps rodando o mesmo `SKAIL_WORKLOAD` no mesmo environment dividem as mesmas execuções: use um workload (ou environment) por pessoa.

## Aviso

**Esta é uma demonstração, não é código de produção.** Ela existe só para mostrar como o [skail](https://docs.skail.dev) funciona e é fornecida "como está", sem garantia de nenhum tipo. Os cartões e as cobranças são simulados: nenhum pagamento real é feito.

## Licença

[Apache-2.0](LICENSE)
