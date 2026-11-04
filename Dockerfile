# Image: routego/booking. Runs on the routego-core EC2 instance via docker compose (5005:8080).
# Build from the repo root:  docker build -t routego/booking .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Root build files first: the coding-standard analyzers and .editorconfig run during the build,
# and BannedSymbols.txt is a copy kept at the repo root, because the pipeline never checks submodules out.
COPY Directory.Build.props .editorconfig BannedSymbols.txt ./
COPY docs/coding-standards/enforcement/ docs/coding-standards/enforcement/

# Restore on the csproj alone so the package layer is cached until dependencies change.
COPY RouteGo.Booking.Api/RouteGo.Booking.Api.csproj RouteGo.Booking.Api/
RUN dotnet restore RouteGo.Booking.Api/RouteGo.Booking.Api.csproj -r linux-x64

COPY RouteGo.Booking.Api/ RouteGo.Booking.Api/
# -r linux-x64 matches the restore; framework-dependent, so the aspnet image supplies the runtime.
RUN dotnet publish RouteGo.Booking.Api/RouteGo.Booking.Api.csproj -c Release -r linux-x64 --self-contained false \
    --no-restore -o /app/publish

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet image already listens on 8080 and runs as the non-root 'app' user.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RouteGo.Booking.Api.dll"]
