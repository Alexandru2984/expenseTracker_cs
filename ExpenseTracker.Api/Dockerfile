# ── Stage 1: Restore & publish ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ExpenseTracker.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 2: Build EF migration bundle (self-contained, Alpine/musl) ─────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS bundle
WORKDIR /src

COPY ExpenseTracker.Api.csproj ./
RUN dotnet restore

RUN dotnet tool install --global dotnet-ef --version 8.0.11
ENV PATH="$PATH:/root/.dotnet/tools"

COPY . ./
RUN dotnet ef migrations bundle \
    --self-contained -r linux-musl-x64 \
    -o /efbundle

# ── Stage 3: Runtime (Alpine, non-root) ───────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S appgroup && adduser -S appuser -G appgroup
USER appuser

COPY --from=build --chown=appuser:appgroup /app/publish .
COPY --from=bundle --chown=appuser:appgroup /efbundle ./efbundle

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "ExpenseTracker.Api.dll"]

