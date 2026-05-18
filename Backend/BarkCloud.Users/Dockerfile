# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Backend/BarkCloud.Users/BarkCloud.Users.csproj", "Backend/BarkCloud.Users/"]
COPY ["Backend/BarkCloud.GrpcServer/BarkCloud.GrpcServer.csproj", "Backend/BarkCloud.GrpcServer/"]
COPY ["Shared/BarkCloud.Shared.Auth/BarkCloud.Shared.Auth.csproj", "Shared/BarkCloud.Shared.Auth/"]
COPY ["Shared/BarkCloud.Shared.Exceptions/BarkCloud.Shared.Exceptions.csproj", "Shared/BarkCloud.Shared.Exceptions/"]
COPY ["Shared/BarkCloud.Shared.Identity/BarkCloud.Shared.Identity.csproj", "Shared/BarkCloud.Shared.Identity/"]
COPY ["Shared/BarkCloud.Shared.Queue/BarkCloud.Shared.Queue.csproj", "Shared/BarkCloud.Shared.Queue/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Backend/BarkCloud.Users/BarkCloud.Users.csproj"
COPY . .
WORKDIR "/src/Backend/BarkCloud.Users"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "BarkCloud.Users.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN mkdir -p /app/publish/logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --chown=1654:1654 --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "BarkCloud.Users.dll"]
