# syntax=docker/dockerfile:1
# Застосування EF Core міграцій OpenBP.Data (bundle) і вихід. Рядок підключення — ConnectionStrings__Default.
# Збірка: docker build -f docker/migrate.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/ .config/
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet tool restore \
    && RID="linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)" \
    && dotnet restore src/OpenBP.Data --runtime "$RID" \
    && dotnet ef migrations bundle --project src/OpenBP.Data --startup-project src/OpenBP.Data \
        --self-contained --target-runtime "$RID" --configuration Release --output /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /out/efbundle .
USER $APP_UID
# ConnectionStrings__Default має бути задано; bundle сам створює таблицю історії і застосовує лише нові міграції.
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$ConnectionStrings__Default\""]
