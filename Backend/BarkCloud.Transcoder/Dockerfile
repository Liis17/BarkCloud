FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Backend/BarkCloud.Transcoder/BarkCloud.Transcoder.csproj", "Backend/BarkCloud.Transcoder/"]
RUN dotnet restore "Backend/BarkCloud.Transcoder/BarkCloud.Transcoder.csproj"
COPY . .
WORKDIR /src/Backend/BarkCloud.Transcoder
RUN dotnet publish "BarkCloud.Transcoder.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=mwader/static-ffmpeg:latest /ffmpeg /usr/local/bin/ffmpeg
COPY --from=mwader/static-ffmpeg:latest /ffprobe /usr/local/bin/ffprobe
COPY --chown=1654:1654 --from=build /app/publish/ ./
RUN mkdir -p /mnt/transcode && chown 1654:1654 /mnt/transcode
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "BarkCloud.Transcoder.dll"]
