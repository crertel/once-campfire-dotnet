# syntax = docker/dockerfile:1
#
# Production image for the ASP.NET port. A drop-in for the Rails image:
# uid 1000, working directory /rails, storage at /rails/storage/{db,files,backups},
# the same environment variables, ports 80 and 443, and the ONCE hooks.
# Thruster 0.1.23 (the binary the Rails image runs) listens on HTTP_PORT and
# starts this server with PORT set to TARGET_PORT.
#
#   docker build -t campfire .
#   docker run --publish 80:80 --env DISABLE_SSL=true --volume campfire:/rails/storage campfire

ARG DOTNET_SDK=11.0.100-rc.1
ARG DOTNET_RUNTIME=11.0.0-rc.1
ARG THRUSTER_VERSION=0.1.23


FROM debian:trixie-slim AS thrust
ARG THRUSTER_VERSION
ARG TARGETARCH
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*
RUN case "$TARGETARCH" in \
      amd64) platform=x86_64-linux ;; \
      arm64) platform=aarch64-linux ;; \
      *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    curl -fsSL -o /tmp/thrust.gem "https://rubygems.org/downloads/thruster-${THRUSTER_VERSION}-${platform}.gem" && \
    mkdir -p /tmp/gem && \
    tar -xOf /tmp/thrust.gem data.tar.gz | tar -xz -C /tmp/gem && \
    install -D -m 755 "/tmp/gem/exe/${platform}/thrust" /out/thrust


FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src
COPY global.json ./
COPY src/Campfire.Core/Campfire.Core.csproj src/Campfire.Core/
COPY src/Campfire.Web/Campfire.Web.csproj src/Campfire.Web/
RUN dotnet restore src/Campfire.Web/Campfire.Web.csproj
COPY src/ src/
RUN dotnet publish src/Campfire.Web/Campfire.Web.csproj -c Release -o /out --no-restore -p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME}

ARG OCI_DESCRIPTION
LABEL org.opencontainers.image.description="${OCI_DESCRIPTION}"
ARG OCI_SOURCE
LABEL org.opencontainers.image.source="${OCI_SOURCE}"
LABEL org.opencontainers.image.licenses="MIT"

# The ASP.NET base image already has group 1000. The Rails image and the
# restore command both address that uid as rails:rails.
RUN set -eu; \
    group="$(getent group 1000 | cut -d: -f1 || true)"; \
    if [ -z "$group" ]; then groupadd --system --gid 1000 rails; \
    elif [ "$group" != "rails" ]; then groupmod -n rails "$group"; fi; \
    user="$(getent passwd 1000 | cut -d: -f1 || true)"; \
    if [ -z "$user" ]; then useradd rails --uid 1000 --gid rails --create-home --shell /bin/bash; \
    elif [ "$user" != "rails" ]; then usermod -l rails -d /home/rails -m "$user"; fi

WORKDIR /rails

COPY --from=build --chown=1000:1000 /out/ ./
COPY --from=thrust /out/thrust /usr/local/bin/thrust
COPY --chown=1000:1000 app/assets/ app/assets/
COPY --chown=1000:1000 public/502.html public/502.html
COPY --chmod=755 hooks/ /hooks/

COPY --chmod=755 <<'EOF' /rails/bin/boot
#!/bin/sh
set -eu
cd /rails
exec /usr/local/bin/thrust dotnet Campfire.Web.dll
EOF

COPY --chmod=755 <<'EOF' /rails/script/admin/generate-secrets
#!/bin/sh
set -eu
cd /rails
exec dotnet Campfire.Web.dll secrets
EOF

COPY --chmod=755 <<'EOF' /rails/script/admin/prepare-backup
#!/bin/sh
set -eu
cd /rails
exec dotnet Campfire.Web.dll backup
EOF

RUN mkdir -p /rails/storage/db /rails/storage/files /rails/storage/backups /rails/storage/logs /rails/storage/thruster && \
    chown -R 1000:1000 /rails

USER 1000:1000

ENV RAILS_ENV="production" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    HTTP_IDLE_TIMEOUT=60 \
    HTTP_READ_TIMEOUT=300 \
    HTTP_WRITE_TIMEOUT=300

ARG APP_VERSION
ENV APP_VERSION=$APP_VERSION
ARG GIT_REVISION
ENV GIT_REVISION=$GIT_REVISION

EXPOSE 80 443

CMD ["bin/boot"]
