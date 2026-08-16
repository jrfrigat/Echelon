@echo off
setlocal
cd /d "%~dp0"

rem Every secret in docker-compose.yml is required with no default, so a missing .env surfaces
rem as an interpolation error per variable. Say so once, here, instead.
if not exist ".env" (
    echo [ERROR] .env not found in %CD%.
    echo         Copy .env.example to .env and fill in the required values.
    exit /b 1
)

rem docker-compose.override.yml is optional and merged automatically. With it, the stack runs
rem against the SQL Server it names (typically the one on this host); without it, docker-compose.yml
rem starts a containerized SQL Server instead. Neither is an error - just say which one is happening.
if exist "docker-compose.override.yml" (
    echo [info] docker-compose.override.yml present - SQL Server comes from there, not a container.
) else (
    echo [info] No docker-compose.override.yml - starting the containerized SQL Server.
    echo        To run against your own SQL Server instead, copy docker-compose.override.example.yml
    echo        to docker-compose.override.yml and fill in the connection string.
)

echo.
rem docker compose V2. No --force-recreate: it tore down the datastores on every run, costing
rem minutes of healthcheck wait for containers whose config had not changed. --build picks up code
rem changes; pass --force-recreate by hand on the rare occasion you actually need it.
docker compose up -d --build
if errorlevel 1 (
    echo [ERROR] docker compose up failed.
    exit /b 1
)

echo.
docker compose ps
echo.
echo Core API + PWA:   http://localhost:8081
echo   readiness:      http://localhost:8081/health/ready
echo   metrics:        http://localhost:8081/metrics
echo Ingress webhooks: see the ingress port in the list above (8080 unless remapped in the override)
echo Logs:             docker compose logs -f core ingress
echo Stop:             docker compose down
echo.
pause
endlocal
