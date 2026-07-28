# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ARG MINVERVERSIONOVERRIDE
ENV MINVERVERSIONOVERRIDE=$MINVERVERSIONOVERRIDE
WORKDIR /src
# Copy MSBuild/CPM manifests before project files so `dotnet restore` can resolve versions
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY src/SignRelay.Contracts/SignRelay.Contracts.csproj src/SignRelay.Contracts/
COPY src/SignRelay.Server/SignRelay.Server.csproj src/SignRelay.Server/
RUN dotnet restore src/SignRelay.Server/SignRelay.Server.csproj
COPY src/SignRelay.Contracts/ src/SignRelay.Contracts/
COPY src/SignRelay.Server/ src/SignRelay.Server/
RUN dotnet publish src/SignRelay.Server/SignRelay.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
ARG MINVERVERSIONOVERRIDE
LABEL org.opencontainers.image.version=$MINVERVERSIONOVERRIDE
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
VOLUME ["/data"]

# Run as a non-root user (useradd works on both Debian and Ubuntu Chiseled base images)
RUN useradd -u 1001 -M -s /sbin/nologin appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data
USER appuser

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SignRelay.Server.dll"]
