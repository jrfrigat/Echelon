FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["ReleaseOrchestrator.Ingress.Webhooks/ReleaseOrchestrator.Ingress.Webhooks.csproj", "ReleaseOrchestrator.Ingress.Webhooks/"]
COPY ["ReleaseOrchestrator.Application/ReleaseOrchestrator.Application.csproj", "ReleaseOrchestrator.Application/"]
COPY ["ReleaseOrchestrator.Core/ReleaseOrchestrator.Core.csproj", "ReleaseOrchestrator.Core/"]
RUN dotnet restore "ReleaseOrchestrator.Ingress.Webhooks/ReleaseOrchestrator.Ingress.Webhooks.csproj"
COPY . .
RUN dotnet build "ReleaseOrchestrator.Ingress.Webhooks/ReleaseOrchestrator.Ingress.Webhooks.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ReleaseOrchestrator.Ingress.Webhooks/ReleaseOrchestrator.Ingress.Webhooks.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ReleaseOrchestrator.Ingress.Webhooks.dll"]
