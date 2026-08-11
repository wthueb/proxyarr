FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Proxyarr.slnx Directory.Build.props Directory.Packages.props global.json ./
COPY src/Proxyarr/Proxyarr.csproj src/Proxyarr/
COPY tests/Proxyarr.Tests/Proxyarr.Tests.csproj tests/Proxyarr.Tests/
COPY tests/Proxyarr.IntegrationTests/Proxyarr.IntegrationTests.csproj tests/Proxyarr.IntegrationTests/
RUN dotnet restore

COPY src/ src/
COPY tests/ tests/
RUN dotnet build -c Release --no-restore

FROM build AS test
RUN dotnet test tests/Proxyarr.Tests -c Release --no-build

FROM build AS publish
RUN dotnet publish src/Proxyarr -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=publish /app .

# base image sets ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_HTTP_PORTS=""
ENV PROXYARR_CONFIG=/config/config.yml
EXPOSE 8484
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD ["wget", "--spider", "--quiet", "http://127.0.0.1:8484/healthz"]
USER $APP_UID

ENTRYPOINT ["dotnet", "Proxyarr.dll"]
