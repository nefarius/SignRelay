# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SignRelay.Contracts/SignRelay.Contracts.csproj src/SignRelay.Contracts/
COPY src/SignRelay.Server/SignRelay.Server.csproj src/SignRelay.Server/
RUN dotnet restore src/SignRelay.Server/SignRelay.Server.csproj
COPY src/SignRelay.Contracts/ src/SignRelay.Contracts/
COPY src/SignRelay.Server/ src/SignRelay.Server/
RUN dotnet publish src/SignRelay.Server/SignRelay.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "SignRelay.Server.dll"]
