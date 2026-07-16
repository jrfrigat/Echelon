FROM mcr.microsoft.com/dotnet/aspnet:10.0.8 AS base
WORKDIR /app
EXPOSE 8080
# The aspnet image ships no HTTP client, and compose's healthcheck needs one.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
COPY ["ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj", "ReleaseOrchestrator.Web/"]
COPY ["ReleaseOrchestrator.Application/ReleaseOrchestrator.Application.csproj", "ReleaseOrchestrator.Application/"]
COPY ["ReleaseOrchestrator.Core/ReleaseOrchestrator.Core.csproj", "ReleaseOrchestrator.Core/"]
COPY ["ReleaseOrchestrator.Infrastructure/ReleaseOrchestrator.Infrastructure.csproj", "ReleaseOrchestrator.Infrastructure/"]
COPY ["ReleaseOrchestrator.Migrations.MsSql/ReleaseOrchestrator.Migrations.MsSql.csproj", "ReleaseOrchestrator.Migrations.MsSql/"]
COPY ["ReleaseOrchestrator.Pwa/ReleaseOrchestrator.Pwa.csproj", "ReleaseOrchestrator.Pwa/"]
RUN dotnet restore "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj"
# src/.dockerignore keeps host bin/obj out of this copy; without it the restore above is
# overwritten by Windows-path artifacts and the build fails to resolve packages.
COPY . .
RUN dotnet build "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ReleaseOrchestrator.Web.dll"]
