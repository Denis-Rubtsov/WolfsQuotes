# Сборка: docker/podman build -t ghcr.io/denis-rubtsov/wolfsquotes .
# Поддерживает buildx --platform (кросс-сборка без эмуляции).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Vlk/Vlk.csproj Vlk/
RUN dotnet restore Vlk/Vlk.csproj -a $TARGETARCH
COPY Vlk/ Vlk/
RUN dotnet publish Vlk/Vlk.csproj -c Release -a $TARGETARCH -o /out --no-restore

# aspnet, а не runtime: у всех трёх ботов один базовый слой (в нём есть и обычный .NET runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.source=https://github.com/Denis-Rubtsov/WolfsQuotes
WORKDIR /app
COPY --from=build /out .
# Несекретные значения по умолчанию; всё можно перекрыть переменными окружения.
# Снаружи нужны: Token, AiApikey, ADMIN_IDS, BASIC_URL (необязательно StarsPrice) — см. deploy/.
ENV TZ=Europe/Moscow \
    QuotesFile=/data/quotes.json \
    SystemPromptFile=/data/systemprompt.txt
VOLUME /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Vlk.dll"]
