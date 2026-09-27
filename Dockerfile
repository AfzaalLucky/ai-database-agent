# AI Database Agent API — container image.
#   docker build -t ai-database-agent .
# Runs as a non-root user on a chiseled (distroless) image: no shell, no package manager.
# SQL Server and Ollama are external; see docs/deployment.md for configuration.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files only, so the layer is reused until a dependency changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/AiDatabaseAgent.Core/AiDatabaseAgent.Core.csproj src/AiDatabaseAgent.Core/
COPY src/AiDatabaseAgent.Infrastructure/AiDatabaseAgent.Infrastructure.csproj src/AiDatabaseAgent.Infrastructure/
COPY src/AiDatabaseAgent.Api/AiDatabaseAgent.Api.csproj src/AiDatabaseAgent.Api/
RUN dotnet restore src/AiDatabaseAgent.Api/AiDatabaseAgent.Api.csproj

COPY src/ src/
COPY knowledge/ knowledge/
RUN dotnet publish src/AiDatabaseAgent.Api/AiDatabaseAgent.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# "-extra" adds ICU and time zone data, which SqlClient needs for culture-aware collations and dates.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AiDatabaseAgent.Api.dll"]
