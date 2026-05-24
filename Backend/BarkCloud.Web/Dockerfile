# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Backend/BarkCloud.Web/BarkCloud.Web.csproj", "Backend/BarkCloud.Web/"]
COPY ["Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj", "Backend/BarkCloud.GrpcServer/"]
COPY ["Shared/BarkCloud.Shared.Auth/BarkCloud.Shared.Auth.csproj", "Shared/BarkCloud.Shared.Auth/"]
COPY ["Shared/BarkCloud.Shared.Exceptions/BarkCloud.Shared.Exceptions.csproj", "Shared/BarkCloud.Shared.Exceptions/"]
COPY ["Shared/BarkCloud.Shared.Identity/BarkCloud.Shared.Identity.csproj", "Shared/BarkCloud.Shared.Identity/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Backend/BarkCloud.Web/BarkCloud.Web.csproj"
COPY . .
WORKDIR "/src/Backend/BarkCloud.Web"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "BarkCloud.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Alpine + docker-cli/compose: веб управляет обновлением бэкенда через смонтированный docker.sock
# (см. Infrastructure/DockerService.cs). chiseled-образ не подошёл бы — в нём нет shell и пакетов.
# Контейнер запускается под root (нужен доступ к docker.sock) — это задаётся в docker-compose.yml.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
RUN apk add --no-cache docker-cli docker-cli-compose icu-libs tzdata
# icu-libs + отключённый invariant — чтобы русская локализация в Format.cs работала на alpine/musl
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BarkCloud.Web.dll"]
