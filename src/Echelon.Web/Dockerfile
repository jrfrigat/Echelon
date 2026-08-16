FROM mcr.microsoft.com/dotnet/aspnet:10.0.8 AS base
WORKDIR /app
EXPOSE 8080
# The aspnet image ships no HTTP client, and compose's healthcheck needs one.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
# Central Package Management: the versions live in Directory.Packages.props at the repo root, so the
# build context is the root (not src/), and these must be restored before any csproj - without them
# every PackageReference is version-less and restore fails. global.json pins the SDK to match.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ ./
RUN dotnet restore "Echelon.Web/Echelon.Web.csproj"
RUN dotnet build "Echelon.Web/Echelon.Web.csproj" -c Release -o /app/build --no-restore

FROM build AS publish
RUN dotnet publish "Echelon.Web/Echelon.Web.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Echelon.Web.dll"]
