# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY UncoveringGreatnessCRM.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish UncoveringGreatnessCRM.csproj -c Release -o /app/publish --no-restore

# ---- run ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
# Database (App_Data/crm.db) and Data Protection keys (App_Data/keys) live here: mount a persistent disk on this folder.
RUN mkdir -p /app/App_Data
EXPOSE 10000
# Render supplies PORT (default 10000).
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} exec dotnet UncoveringGreatnessCRM.dll"]
