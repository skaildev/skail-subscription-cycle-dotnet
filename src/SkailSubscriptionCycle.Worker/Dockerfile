# Build context: repository root.
#   podman build -f src/SkailSubscriptionCycle.Worker/Dockerfile -t subscription-cycle-worker .
# dotnet publish runs in the SDK image; the final image only needs the .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailSubscriptionCycle.Contracts/SkailSubscriptionCycle.Contracts.csproj src/SkailSubscriptionCycle.Contracts/
COPY src/SkailSubscriptionCycle.Worker/SkailSubscriptionCycle.Worker.csproj src/SkailSubscriptionCycle.Worker/
RUN dotnet restore src/SkailSubscriptionCycle.Worker/SkailSubscriptionCycle.Worker.csproj
COPY src/SkailSubscriptionCycle.Contracts src/SkailSubscriptionCycle.Contracts
COPY src/SkailSubscriptionCycle.Worker src/SkailSubscriptionCycle.Worker
RUN dotnet publish src/SkailSubscriptionCycle.Worker/SkailSubscriptionCycle.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SkailSubscriptionCycle.Worker.dll"]
