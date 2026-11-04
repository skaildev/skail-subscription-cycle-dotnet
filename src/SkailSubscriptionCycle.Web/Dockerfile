# Build context: repository root.
#   podman build -f src/SkailSubscriptionCycle.Web/Dockerfile -t subscription-cycle-web .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailSubscriptionCycle.Contracts/SkailSubscriptionCycle.Contracts.csproj src/SkailSubscriptionCycle.Contracts/
COPY src/SkailSubscriptionCycle.Web/SkailSubscriptionCycle.Web.csproj src/SkailSubscriptionCycle.Web/
RUN dotnet restore src/SkailSubscriptionCycle.Web/SkailSubscriptionCycle.Web.csproj
COPY src/SkailSubscriptionCycle.Contracts src/SkailSubscriptionCycle.Contracts
COPY src/SkailSubscriptionCycle.Web src/SkailSubscriptionCycle.Web
RUN dotnet publish src/SkailSubscriptionCycle.Web/SkailSubscriptionCycle.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SkailSubscriptionCycle.Web.dll"]
