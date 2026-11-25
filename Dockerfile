# Build outside the production server (for example, in GitHub Actions).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/InfinityUrban.Channex/InfinityUrban.Channex.csproj src/InfinityUrban.Channex/
COPY src/InfinityUrban.Web/InfinityUrban.Web.csproj src/InfinityUrban.Web/
RUN dotnet restore src/InfinityUrban.Web/InfinityUrban.Web.csproj
COPY src/InfinityUrban.Channex/ src/InfinityUrban.Channex/
COPY src/InfinityUrban.Web/ src/InfinityUrban.Web/
RUN dotnet publish src/InfinityUrban.Web/InfinityUrban.Web.csproj \
    -c Release --no-restore --self-contained false -o /out \
    -p:UseAppHost=false -p:StaticWebAssetsEnabled=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VCS_REF=local
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Channex__SourceDirectory=/app/channex \
    Hosting__Revision=$VCS_REF
COPY --from=build /out/ ./
COPY --from=build /source/src/InfinityUrban.Channex/Assets/operations.js /app/channex/Assets/operations.js
RUN mkdir -p /app/.local && chown "$APP_UID" /app/.local && chmod 700 /app/.local
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 4 \
    -H 'Host: infinityboard.urbaninfinity.com.br' http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "InfinityUrban.Web.dll"]
