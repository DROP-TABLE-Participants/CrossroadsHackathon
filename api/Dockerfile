FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ErpGenerator.Api.sln ./
COPY src/ErpGenerator.Api.Shared/ErpGenerator.Api.Shared.csproj src/ErpGenerator.Api.Shared/
COPY src/ErpGenerator.Api.Data/ErpGenerator.Api.Data.csproj src/ErpGenerator.Api.Data/
COPY src/ErpGenerator.Api.Services/ErpGenerator.Api.Services.csproj src/ErpGenerator.Api.Services/
COPY src/ErpGenerator.Api.WebHost/ErpGenerator.Api.WebHost.csproj src/ErpGenerator.Api.WebHost/
COPY src/ErpGenerator.Api.MigrationService/ErpGenerator.Api.MigrationService.csproj src/ErpGenerator.Api.MigrationService/
RUN dotnet restore src/ErpGenerator.Api.WebHost/ErpGenerator.Api.WebHost.csproj \
    && dotnet restore src/ErpGenerator.Api.MigrationService/ErpGenerator.Api.MigrationService.csproj

COPY src src
RUN dotnet publish src/ErpGenerator.Api.WebHost/ErpGenerator.Api.WebHost.csproj \
    --configuration Release \
    --output /app/web \
    --no-restore \
    /p:UseAppHost=false \
    && dotnet publish src/ErpGenerator.Api.MigrationService/ErpGenerator.Api.MigrationService.csproj \
    --configuration Release \
    --output /app/migration \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app/web
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
COPY --from=build /app/web ./
COPY --from=build /app/migration /app/migration
USER 1654
ENTRYPOINT ["dotnet", "ErpGenerator.Api.WebHost.dll"]
