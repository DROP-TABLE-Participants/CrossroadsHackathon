FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ErpGenerator.Runtime.sln ./
COPY src/ErpGenerator.Runtime.GeneratedPrimitives.Generator/ErpGenerator.Runtime.GeneratedPrimitives.Generator.csproj src/ErpGenerator.Runtime.GeneratedPrimitives.Generator/
COPY src/ErpGenerator.Runtime.GeneratedPrimitives/ErpGenerator.Runtime.GeneratedPrimitives.csproj src/ErpGenerator.Runtime.GeneratedPrimitives/
COPY src/ErpGenerator.Runtime.Shared/ErpGenerator.Runtime.Shared.csproj src/ErpGenerator.Runtime.Shared/
COPY src/ErpGenerator.Runtime.Data/ErpGenerator.Runtime.Data.csproj src/ErpGenerator.Runtime.Data/
COPY src/ErpGenerator.Runtime.Services/ErpGenerator.Runtime.Services.csproj src/ErpGenerator.Runtime.Services/
COPY src/ErpGenerator.Runtime.WebHost/ErpGenerator.Runtime.WebHost.csproj src/ErpGenerator.Runtime.WebHost/
COPY src src

RUN dotnet restore src/ErpGenerator.Runtime.WebHost/ErpGenerator.Runtime.WebHost.csproj

RUN dotnet publish src/ErpGenerator.Runtime.WebHost/ErpGenerator.Runtime.WebHost.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app


RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && for command in pg_dump pg_restore createdb dropdb; do command -v "$command" >/dev/null; done

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

COPY --from=build /app/publish ./

USER 1654

ENTRYPOINT ["dotnet", "ErpGenerator.Runtime.WebHost.dll"]
