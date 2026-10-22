FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY StbcServer/StbcServer.csproj StbcServer/
RUN dotnet restore StbcServer/StbcServer.csproj
COPY StbcServer/ StbcServer/
RUN dotnet publish StbcServer/StbcServer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV PORT=4530 \
    LOG_LEVEL=info
EXPOSE 4530/tcp 8080/tcp
USER app
ENTRYPOINT ["dotnet", "StbcServer.dll"]
