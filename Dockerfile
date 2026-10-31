# Сборка: podman build -t localhost/vlk .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Vlk/Vlk.csproj Vlk/
RUN dotnet restore Vlk/Vlk.csproj
COPY Vlk/ Vlk/
RUN dotnet publish Vlk/Vlk.csproj -c Release -o /out --no-restore

# aspnet, а не runtime: у всех трёх ботов один базовый слой (в нём есть и обычный .NET runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Настоящий appsettings.json (с токенами) монтируется поверх /app/appsettings.json, состояние лежит в /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Vlk.dll"]
