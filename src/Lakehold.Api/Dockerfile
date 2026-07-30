# Production image for the Lakehold API.
#
# Build context is the repository root, not this directory: central package management means the
# restore needs Directory.Packages.props and Directory.Build.props, which live above it.
#
#   docker build -f src/Lakehold.Api/Dockerfile -t lakehold-api .

FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS duckdb-extensions
ARG TARGETARCH
# Keep this equal to SELECT version() from the native library shipped by
# Skuirrels.DuckDB.NET.Bindings.Full. ExtensionPackagingTests fails on drift.
ARG DUCKDB_EXTENSION_VERSION=1.5.5
# Keep this equal to the default LakehouseOptions extension set.
ARG DUCKDB_PRELOADED_EXTENSIONS="ducklake httpfs json parquet excel"

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gzip \
    && rm -rf /var/lib/apt/lists/*

# Download the signed, version- and platform-specific binaries at image-build time. The build stage
# runs on BUILDPLATFORM, but selects TARGETARCH artifacts, so the release workflow retains its
# emulation-free amd64/arm64 cross-build.
RUN set -eux; \
    platform="$(case "$TARGETARCH" in \
        amd64) echo linux_amd64 ;; \
        arm64) echo linux_arm64 ;; \
        *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac)"; \
    destination="/duckdb-extensions/v${DUCKDB_EXTENSION_VERSION}/${platform}"; \
    mkdir -p "$destination"; \
    for extension in $DUCKDB_PRELOADED_EXTENSIONS; do \
        archive="/tmp/${extension}.duckdb_extension.gz"; \
        curl --fail --silent --show-error --location \
            --output "$archive" \
            "https://extensions.duckdb.org/v${DUCKDB_EXTENSION_VERSION}/${platform}/${extension}.duckdb_extension.gz"; \
        gzip --decompress --stdout "$archive" > "$destination/${extension}.duckdb_extension"; \
        test -s "$destination/${extension}.duckdb_extension"; \
    done

# Development Compose keeps the SDK and source watcher, but starts from an image whose DuckDB cache
# is already populated. That makes a fresh `docker compose up` independent of extension downloads at
# application runtime while preserving bind-mounted source and `dotnet watch`.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development
WORKDIR /src
COPY --from=duckdb-extensions \
    /duckdb-extensions/ /root/.duckdb/extensions/

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Manifests first, so a source-only change reuses the restore layer. Copying the whole tree up front
# would invalidate the cache on every edit and re-restore every build.
COPY Directory.Packages.props Directory.Build.props ./
COPY src/Lakehold.Api/Lakehold.Api.csproj src/Lakehold.Api/
COPY src/Lakehold.ControlPlane/Lakehold.ControlPlane.csproj src/Lakehold.ControlPlane/
COPY src/Lakehold.Engine/Lakehold.Engine.csproj src/Lakehold.Engine/
COPY src/Lakehold.ServiceDefaults/Lakehold.ServiceDefaults.csproj src/Lakehold.ServiceDefaults/

# Publishing for a specific runtime identifier prunes the native assets to the one platform this
# image runs on. DuckDB.NET.Data.Full carries binaries for Windows, macOS, and Linux on both
# architectures, and a portable publish ships all of them — several hundred megabytes of libraries
# that can never load here. TARGETARCH comes from the builder, so this stays correct on arm64 hosts
# rather than hard-coding the architecture of whoever built it last.
RUN RID=$(case "$TARGETARCH" in amd64) echo linux-x64 ;; arm64) echo linux-arm64 ;; *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; esac) \
    && echo "$RID" > /rid \
    && dotnet restore src/Lakehold.Api/Lakehold.Api.csproj -r "$RID"

COPY src/ src/
RUN dotnet publish src/Lakehold.Api/Lakehold.Api.csproj \
    -c Release \
    -o /app \
    -r "$(cat /rid)" \
    --self-contained false \
    --no-restore

# Debian rather than Alpine on purpose: DuckDB.NET ships glibc-linked native libraries, and on musl
# the process starts and then fails at the first query with a loader error — the worst place to find
# out. The image is larger; the alternative does not work.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is the health check. Without it the check reports unhealthy on a working container, which
# takes dependent services down with it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# DuckDB's normal per-user extension cache. Preloading it means the first catalog session does not
# need runtime access to extensions.duckdb.org. The signed binaries are still verified by DuckDB
# when loaded.
COPY --from=duckdb-extensions --chown=app:app \
    /duckdb-extensions/ /home/app/.duckdb/extensions/

# The durable state and disposable import mountpoints are owned by the non-root runtime user.
# Fresh named volumes inherit this ownership from the image; pre-existing host directories do not.
RUN mkdir -p /var/lib/lakehold /var/lib/lakehold-imports \
    && chown -R app:app /var/lib/lakehold /var/lib/lakehold-imports /app /home/app/.duckdb
USER app

ENV ASPNETCORE_URLS=http://0.0.0.0:5200 \
    ASPNETCORE_ENVIRONMENT=Production \
    HOME=/home/app \
    Lakehouse__StateRoot=/var/lib/lakehold \
    DOTNET_gcServer=1

EXPOSE 5200
VOLUME ["/var/lib/lakehold"]

HEALTHCHECK --interval=15s --timeout=5s --start-period=45s --retries=5 \
    CMD curl -fsS http://localhost:5200/health || exit 1

ENTRYPOINT ["dotnet", "Lakehold.Api.dll"]
