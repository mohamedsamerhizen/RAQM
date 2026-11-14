FROM mcr.microsoft.com/dotnet/sdk:10.0.103 AS build
WORKDIR /source
COPY global.json RAQM.slnx ./
COPY RAQM.Api/RAQM.Api.csproj RAQM.Api/
RUN dotnet restore RAQM.Api/RAQM.Api.csproj
COPY RAQM.Api/ RAQM.Api/
RUN dotnet publish RAQM.Api/RAQM.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=15s --retries=5 CMD curl --fail --silent http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "RAQM.Api.dll"]

