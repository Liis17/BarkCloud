# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM alpine:3.22 AS lognara-agent
ARG TARGETARCH
ARG LOGNARA_AGENT_VERSION=latest
WORKDIR /download
RUN apk add --no-cache curl jq
RUN set -eu; \
    case "$TARGETARCH" in amd64|arm64) ;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1;; esac; \
    if [ "$LOGNARA_AGENT_VERSION" = "latest" ]; then \
      page=1; tag=""; \
      while :; do \
        releases="$(curl -fSL --retry 3 "https://api.github.com/repos/Liis17/Lognara/releases?per_page=100&page=$page")"; \
        count="$(printf '%s' "$releases" | jq 'if type == "array" then length else 0 end')"; \
        [ "$count" -gt 0 ] || break; \
        tag="$(printf '%s' "$releases" | jq -r '[.[] | select(.draft == false and .prerelease == false) | select(.tag_name | test("^agent-[0-9]{8}-[0-9]{6}-[0-9a-f]{12}$"))][0].tag_name // empty')"; \
        [ -z "$tag" ] || break; page=$((page + 1)); \
      done; \
    else tag="$LOGNARA_AGENT_VERSION"; fi; \
    if [ -z "$tag" ]; then echo "No published Lognara agent release found" >&2; exit 1; fi; \
    echo "Using Lognara agent $tag"; \
    base="https://github.com/Liis17/Lognara/releases/download/$tag"; \
    archive="lognara-agent-linux-$TARGETARCH.tar.gz"; \
    curl -fSL --retry 3 "$base/$archive" -o "$archive"; \
    curl -fSL --retry 3 "$base/SHA256SUMS" -o SHA256SUMS; \
    grep -E "^[0-9a-fA-F]{64}  ${archive}$" SHA256SUMS > selected.sha256; \
    test "$(wc -l < selected.sha256)" -eq 1; \
    sha256sum -c selected.sha256; \
    tar -xzf "$archive"; test -f lognara-agent; chmod 755 lognara-agent; mkdir -p /spool

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Backend/BarkCloud.Notification/BarkCloud.Notification.csproj", "Backend/BarkCloud.Notification/"]
COPY ["Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj", "Backend/BarkCloud.GrpcServer/"]
COPY ["Shared/BarkCloud.Shared.Auth/BarkCloud.Shared.Auth.csproj", "Shared/BarkCloud.Shared.Auth/"]
COPY ["Shared/BarkCloud.Shared.Exceptions/BarkCloud.Shared.Exceptions.csproj", "Shared/BarkCloud.Shared.Exceptions/"]
COPY ["Shared/BarkCloud.Shared.Identity/BarkCloud.Shared.Identity.csproj", "Shared/BarkCloud.Shared.Identity/"]
COPY ["Shared/BarkCloud.Shared.Queue/BarkCloud.Shared.Queue.csproj", "Shared/BarkCloud.Shared.Queue/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Backend/BarkCloud.Notification/BarkCloud.Notification.csproj"
COPY . .
WORKDIR "/src/Backend/BarkCloud.Notification"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "BarkCloud.Notification.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN mkdir -p /app/publish/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=lognara-agent /download/lognara-agent /usr/local/bin/lognara-agent
COPY --chown=1654:1654 --from=lognara-agent /spool /var/lib/lognara-agent/spool
COPY --chown=1654:1654 --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "BarkCloud.Notification.dll"]
