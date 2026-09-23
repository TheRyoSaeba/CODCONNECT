FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/CODConnect.Core/CODConnect.Core.csproj src/CODConnect.Core/
COPY src/CODConnect.Protocol/CODConnect.Protocol.csproj src/CODConnect.Protocol/
COPY server/CODConnect.Rendezvous/CODConnect.Rendezvous.csproj server/CODConnect.Rendezvous/
RUN dotnet restore server/CODConnect.Rendezvous/CODConnect.Rendezvous.csproj
COPY src/CODConnect.Core/ src/CODConnect.Core/
COPY src/CODConnect.Protocol/ src/CODConnect.Protocol/
COPY server/CODConnect.Rendezvous/ server/CODConnect.Rendezvous/
RUN dotnet publish server/CODConnect.Rendezvous/CODConnect.Rendezvous.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "CODConnect.Rendezvous.dll"]
