# syntax=docker/dockerfile:1
# OpenBP.Worker: виконання сценаріїв з черги jobs. Назовні — лише health-порт 8089 (/health/live, /health/ready).
# Збірка: docker build -f docker/worker.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/OpenBP.Worker/OpenBP.Worker.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl — лише для HEALTHCHECK.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Файлове сховище (вкладення, файли з MCP-tools) — том /data/files, власник — користувач застосунку.
RUN mkdir -p /data/files && chown -R $APP_UID /data
ENV OPENBP_WORKER_HEALTH_PORT=8089 \
    Storage__LocalPath=/data/files \
    DOTNET_NOLOGO=1 \
    Logging__LogLevel__Microsoft.AspNetCore=Warning \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME ["/data/files"]
EXPOSE 8089
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8089/health/ready || exit 1
ENTRYPOINT ["dotnet", "OpenBP.Worker.dll"]
