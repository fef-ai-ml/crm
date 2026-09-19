FROM mcr.microsoft.com/dotnet/sdk:10.0 AS projfiles
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Crm.sln ./
COPY src/ src/
RUN find src -type f ! \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Opt-in for hosts whose IPv6 route is broken (DNS answers with AAAA records, connections time out):
# `dotnet restore` then hangs on nuget.org with NU1301. 1 = .NET uses IPv4 sockets only. Build stage only.
ARG DOTNET_SYSTEM_NET_DISABLEIPV6=0
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=${DOTNET_SYSTEM_NET_DISABLEIPV6}
WORKDIR /src
COPY --from=projfiles /src/ ./
# Keep this Dockerfile identical to Crm.Worker/Dockerfile up to and including this restore line: a differing
# restore command strands one image's restore layer under a duplicate parent-layer copy, forcing a network
# restore instead of sharing the cache the other image already built.
RUN dotnet restore src/Hosts/Crm.Api/Crm.Api.csproj && dotnet restore src/Hosts/Crm.Worker/Crm.Worker.csproj
COPY src/ src/
RUN dotnet publish src/Hosts/Crm.Api/Crm.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD curl -fsS http://localhost:8080/health/live || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Crm.Api.dll"]
