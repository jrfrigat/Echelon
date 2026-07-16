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

rem `docker compose` (V2). The old `docker-compose` V1 binary has been EOL since July 2023.
rem No --force-recreate: it tore down mssql, rabbitmq and redis on every run, costing minutes of
rem healthcheck wait for containers whose config had not changed. Compose recreates what the
rem config actually changed; pass --force-recreate by hand on the rare occasion you need it.
docker compose up -d --build
if errorlevel 1 (
    echo [ERROR] docker compose up failed.
    exit /b 1
)

docker compose ps
endlocal
