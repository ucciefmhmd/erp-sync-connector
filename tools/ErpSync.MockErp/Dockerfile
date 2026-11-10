# syntax=docker/dockerfile:1
# Build from the repository root:  docker build -f tools/ErpSync.MockErp/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY tools/ErpSync.MockErp/ErpSync.MockErp.csproj tools/ErpSync.MockErp/
RUN dotnet restore tools/ErpSync.MockErp/ErpSync.MockErp.csproj
COPY tools/ErpSync.MockErp/ tools/ErpSync.MockErp/
RUN dotnet publish tools/ErpSync.MockErp/ErpSync.MockErp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ErpSync.MockErp.dll"]
