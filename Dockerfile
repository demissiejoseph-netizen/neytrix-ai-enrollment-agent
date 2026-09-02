# syntax=docker/dockerfile:1
# ---------------------------------------------------------------------------
# Neytrix AI Enrollment Agent — Cloud Run container
#
# Multi-stage: restore/publish with the .NET 8 SDK, run on the trimmed
# aspnet:8.0 runtime as a non-root user. Cloud Run injects PORT at runtime,
# so the entrypoint binds ASPNETCORE_URLS to it rather than hardcoding 8080.
# ---------------------------------------------------------------------------

# ---------- Stage 1: restore ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS restore
WORKDIR /src

# Copy only the project graph first so `dotnet restore` is cached independently
# of source churn. Any .cs edit will not invalidate the NuGet layer.
COPY Directory.Build.props ./
COPY src/NeytrixAI.Domain/NeytrixAI.Domain.csproj                 src/NeytrixAI.Domain/
COPY src/NeytrixAI.Infrastructure/NeytrixAI.Infrastructure.csproj src/NeytrixAI.Infrastructure/
COPY src/NeytrixAI.Api/NeytrixAI.Api.csproj                       src/NeytrixAI.Api/

# Restore only the API project graph (Domain/Infrastructure are pulled in via
# ProjectReference). The container never needs the test project, and
# NeytrixAI.sln references tests/NeytrixAI.Tests/NeytrixAI.Tests.csproj, which
# is intentionally not copied into this build context.
RUN dotnet restore src/NeytrixAI.Api/NeytrixAI.Api.csproj

# ---------- Stage 2: publish ----------
FROM restore AS publish
WORKDIR /src

COPY src/ src/

# No --no-restore risk: the restore layer above is reused via the parent stage.
RUN dotnet publish src/NeytrixAI.Api/NeytrixAI.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS final

# curl is used by the container smoke test and by any local healthcheck.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root. The aspnet image ships an `app` user (uid 1654) on .NET 8.
WORKDIR /app
COPY --from=publish --chown=app:app /app/publish .

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    PORT=8080

USER app
EXPOSE 8080

# Cloud Run sets PORT; bind to it and to all interfaces. Using shell form so
# ${PORT} is expanded at container start, not at build time.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet NeytrixAI.Api.dll --urls http://0.0.0.0:${PORT}"]
