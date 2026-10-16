# syntax=docker/dockerfile:1
# Build from the repository root:  docker build -f src/Base.Web/Dockerfile -t base-web .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props BannedSymbols.txt .editorconfig ./
COPY src/Base.Domain/Base.Domain.csproj src/Base.Domain/
COPY src/Base.Application/Base.Application.csproj src/Base.Application/
COPY src/Base.Infrastructure/Base.Infrastructure.csproj src/Base.Infrastructure/
COPY src/Base.Web/Base.Web.csproj src/Base.Web/
RUN dotnet restore src/Base.Web/Base.Web.csproj

COPY src/ src/
RUN dotnet publish src/Base.Web/Base.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# Chiseled: no shell, no package manager, non-root by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_GCHeapHardLimitPercent=75
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Base.Web.dll"]
