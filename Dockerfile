﻿# syntax=docker/dockerfile:1
ARG DOTNET=8.0

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime:$DOTNET AS base
WORKDIR /publish
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true
ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium
ENV XDG_CONFIG_HOME=/tmp/.chromium
ENV XDG_CACHE_HOME=/tmp/.chromium
RUN apt-get update \
  && apt-get install -y libx11-6 libx11-xcb1 libatk1.0-0 libgtk-3-0 libcups2 libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 libcairo2 libasound2 libxshmfence1 libnss3 chromium \
  && apt-get autopurge -y \
  && apt-get autoclean -y

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:$DOTNET AS build-env
WORKDIR /src

COPY Tranga.sln /src
COPY CLI/CLI.csproj /src/CLI/CLI.csproj
COPY Logging/Logging.csproj /src/Logging/Logging.csproj
COPY Tranga/Tranga.csproj /src/Tranga/Tranga.csproj
RUN dotnet restore /src/Tranga.sln

COPY . /src/
RUN dotnet publish -c Release --property:OutputPath=/publish -maxcpucount:1

FROM --platform=$TARGETPLATFORM base AS runtime
EXPOSE 6531
ARG UNAME=tranga
ARG UID=1000
ARG GID=1000
RUN groupadd -g $GID -o $UNAME \
  && useradd -m -u $UID -g $GID -o -s /bin/bash $UNAME \
  && mkdir /usr/share/tranga-api \
  && mkdir /Manga \
  && chown $UID:$GID /usr/share/tranga-api \
  && chown $UID:$GID /Manga
USER $UNAME

WORKDIR /publish
COPY --chown=$UID:$GID --from=build-env /publish .

ENTRYPOINT ["dotnet", "/publish/Tranga.dll"]
CMD ["-f", "-c", "-l", "/usr/share/tranga-api/logs"]

