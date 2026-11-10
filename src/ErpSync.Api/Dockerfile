# syntax=docker/dockerfile:1
# Build from the repository root:  docker build -f src/ErpSync.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only project files, so the layer is cached until dependencies change.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ErpSync.Domain/ErpSync.Domain.csproj src/ErpSync.Domain/
COPY src/ErpSync.Application/ErpSync.Application.csproj src/ErpSync.Application/
COPY src/ErpSync.Infrastructure/ErpSync.Infrastructure.csproj src/ErpSync.Infrastructure/
COPY src/ErpSync.Api/ErpSync.Api.csproj src/ErpSync.Api/
RUN dotnet restore src/ErpSync.Api/ErpSync.Api.csproj

COPY src/ src/
RUN dotnet publish src/ErpSync.Api/ErpSync.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SQLite database lives on a volume owned by the non-root app user.
RUN mkdir /data && chown "$APP_UID" /data
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__ErpSync="Data Source=/data/erpsync.db"
EXPOSE 8080
VOLUME /data
USER $APP_UID
ENTRYPOINT ["dotnet", "ErpSync.Api.dll"]
