FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj", "ReleaseOrchestrator.Web/"]
COPY ["ReleaseOrchestrator.Application/ReleaseOrchestrator.Application.csproj", "ReleaseOrchestrator.Application/"]
COPY ["ReleaseOrchestrator.Core/ReleaseOrchestrator.Core.csproj", "ReleaseOrchestrator.Core/"]
COPY ["ReleaseOrchestrator.Infrastructure/ReleaseOrchestrator.Infrastructure.csproj", "ReleaseOrchestrator.Infrastructure/"]
COPY ["ReleaseOrchestrator.Migrations.MsSql/ReleaseOrchestrator.Migrations.MsSql.csproj", "ReleaseOrchestrator.Migrations.MsSql/"]
COPY ["ReleaseOrchestrator.Pwa/ReleaseOrchestrator.Pwa.csproj", "ReleaseOrchestrator.Pwa/"]
RUN dotnet restore "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj"
COPY . .
RUN dotnet build "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ReleaseOrchestrator.Web/ReleaseOrchestrator.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ReleaseOrchestrator.Web.dll"]
