FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Crm.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/Hosts/Crm.Api/Crm.Api.csproj
RUN dotnet publish src/Hosts/Crm.Api/Crm.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD curl -fsS http://localhost:8080/health/live || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Crm.Api.dll"]
