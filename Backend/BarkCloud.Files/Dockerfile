# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Backend/BarkCloud.Files/BarkCloud.Files.csproj", "Backend/BarkCloud.Files/"]
COPY ["Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj", "Backend/BarkCloud.GrpcServer/"]
COPY ["Shared/BarkCloud.Shared.Auth/BarkCloud.Shared.Auth.csproj", "Shared/BarkCloud.Shared.Auth/"]
COPY ["Shared/BarkCloud.Shared.Exceptions/BarkCloud.Shared.Exceptions.csproj", "Shared/BarkCloud.Shared.Exceptions/"]
COPY ["Shared/BarkCloud.Shared.Identity/BarkCloud.Shared.Identity.csproj", "Shared/BarkCloud.Shared.Identity/"]
COPY ["Shared/BarkCloud.Shared.Queue/BarkCloud.Shared.Queue.csproj", "Shared/BarkCloud.Shared.Queue/"]
COPY ["Shared/BarkCloud.Proto/BarkCloud.Proto.csproj", "Shared/BarkCloud.Proto/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Backend/BarkCloud.Files/BarkCloud.Files.csproj"
COPY . .
WORKDIR "/src/Backend/BarkCloud.Files"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "BarkCloud.Files.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN mkdir -p /app/publish/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
# Статические бинари ffmpeg/ffprobe для генерации превью видео и аудио-обложек.
COPY --from=mwader/static-ffmpeg:latest /ffmpeg /ffprobe /usr/local/bin/
COPY --chown=1654:1654 --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "BarkCloud.Files.dll"]
