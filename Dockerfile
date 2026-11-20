# Builds the console app and runs it with the repository folder mounted on /data (scripts/run.sh does it):
# the app reads .env from there, so the key never goes into the image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
COPY --from=build /app /app
# the page listens on all interfaces inside the container; scripts/run.sh publishes it on localhost:8080
ENV DEMO_LISTEN=http://0.0.0.0:8080 ASPNETCORE_HTTP_PORTS=
EXPOSE 8080
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/SkailSubscriptionCycle.dll"]
