# syntax=docker/dockerfile:1
# Multi-stage build for the NeytrixAI enrollment API (.NET 8).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (better layer caching) using only project files.
COPY NeytrixAI.sln ./
COPY src/NeytrixAI.Domain/NeytrixAI.Domain.csproj src/NeytrixAI.Domain/
COPY src/NeytrixAI.Infrastructure/NeytrixAI.Infrastructure.csproj src/NeytrixAI.Infrastructure/
COPY src/NeytrixAI.Api/NeytrixAI.Api.csproj src/NeytrixAI.Api/
COPY tests/NeytrixAI.Tests/NeytrixAI.Tests.csproj tests/NeytrixAI.Tests/
RUN dotnet restore src/NeytrixAI.Api/NeytrixAI.Api.csproj

COPY . .
RUN dotnet publish src/NeytrixAI.Api/NeytrixAI.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Run as the non-root user shipped in the base image.
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish .

# The container orchestrator should hit /health for readiness/liveness.
ENTRYPOINT ["dotnet", "NeytrixAI.Api.dll"]
