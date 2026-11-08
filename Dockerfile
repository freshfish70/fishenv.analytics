# syntax=docker/dockerfile:1
#
# One Dockerfile, two images:
#   docker build --target ingest -t analytics-ingest .
#   docker build --target api    -t analytics-api    .
#
# The build stage always runs on the build host's architecture and cross-publishes for TARGETARCH,
# so the .NET build needs no emulation (only the small apt step in the runtime stage does).

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

# Restore first (only project files), so source changes don't invalidate the package cache layer.
COPY Directory.Build.props ./
COPY src/Analytics.Core/Analytics.Core.csproj         src/Analytics.Core/
COPY src/Analytics.Postgres/Analytics.Postgres.csproj src/Analytics.Postgres/
COPY src/Analytics.Ingest/Analytics.Ingest.csproj     src/Analytics.Ingest/
COPY src/Analytics.Api/Analytics.Api.csproj           src/Analytics.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
 && dotnet restore src/Analytics.Ingest/Analytics.Ingest.csproj -a "$arch" \
 && dotnet restore src/Analytics.Api/Analytics.Api.csproj -a "$arch"

COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
 && dotnet publish src/Analytics.Ingest/Analytics.Ingest.csproj -c Release -a "$arch" --no-restore \
        -p:UseAppHost=false -o /out/ingest \
 && dotnet publish src/Analytics.Api/Analytics.Api.csproj -c Release -a "$arch" --no-restore \
        -p:UseAppHost=false -o /out/api \
 && mkdir -p /out/data/geo

# ---------------------------------------------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
# Npgsql probes for GSSAPI (Kerberos) on connect and logs an error when the library is missing.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM runtime AS ingest
COPY --from=build /out/ingest ./
# RIR delegation files (~25 MB) are downloaded here; mount a volume to keep them across restarts.
COPY --from=build --chown=$APP_UID:$APP_UID /out/data /app/data
ENV Urls=http://0.0.0.0:8080 \
    Ingest__DataDir=/app/data
VOLUME /app/data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Analytics.Ingest.dll"]

FROM runtime AS api
COPY --from=build /out/api ./
# appsettings.json binds to 127.0.0.1, which is unreachable from outside a container.
ENV Urls=http://0.0.0.0:8081
EXPOSE 8081
USER $APP_UID
# `docker run --rm <image> create-key "name" admin` prints a new API key.
ENTRYPOINT ["dotnet", "Analytics.Api.dll"]
