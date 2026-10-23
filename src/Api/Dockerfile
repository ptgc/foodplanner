# Build context is the repository root: docker build -f src/Api/Dockerfile .
# SDK pinned to the 10.0 band that global.json (10.0.100, latestFeature) resolves to.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Foodplanner.slnx ./
COPY src/ src/
RUN dotnet publish src/Api/Foodplanner.Api.csproj -c Release -o /out

# Chiseled-extra (adds ICU: Microsoft.Data.SqlClient does not work in invariant globalization mode).
# No shell, no package manager, runs as non-root user "app" (uid 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Foodplanner.Api.dll"]
