# ─── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore dependencies (layer-cached until .csproj files change)
COPY CoupleSync.sln ./
COPY src/CoupleSync.Api/CoupleSync.Api.csproj               src/CoupleSync.Api/
COPY src/CoupleSync.Application/CoupleSync.Application.csproj src/CoupleSync.Application/
COPY src/CoupleSync.Domain/CoupleSync.Domain.csproj           src/CoupleSync.Domain/
COPY src/CoupleSync.Infrastructure/CoupleSync.Infrastructure.csproj src/CoupleSync.Infrastructure/
RUN dotnet restore src/CoupleSync.Api/CoupleSync.Api.csproj

# Copy remaining source and publish
COPY src/ src/
RUN dotnet publish src/CoupleSync.Api/CoupleSync.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ─── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Culture and time-zone data. The alpine image ships without ICU and turns globalization-invariant mode on;
# in that mode named cultures do not exist and Unicode normalization does nothing. The API is written not to
# depend on either (see CoupleSync.InvariantGlobalizationTests), and the image carries the real data anyway:
# icu-libs + icu-data-full for cultures, tzdata for America/Sao_Paulo.
RUN apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Create non-root user for security
RUN addgroup -S appgroup && adduser -S appuser -G appgroup

# Ensure uploads directory exists and is writable by appuser
RUN mkdir -p /app/uploads && chown appuser:appgroup /app/uploads

USER appuser

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "CoupleSync.Api.dll"]
