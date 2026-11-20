# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS build
WORKDIR /src
COPY lib lib
COPY src src
RUN dotnet publish src/Panda/Panda.csproj -c Release -o /app -p:DebugType=none -nologo

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine

ARG TARGETARCH

RUN apk add --no-cache \
    ffmpeg \
    curl \
    unzip \
    ca-certificates \
    tzdata

RUN case "$TARGETARCH" in \
        arm64) YTDLP=yt-dlp_musllinux_aarch64; DENO=deno-aarch64-unknown-linux-gnu.zip ;; \
        *) YTDLP=yt-dlp_musllinux; DENO=deno-x86_64-unknown-linux-gnu.zip ;; \
    esac \
    && curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/$YTDLP" \
        -o /usr/local/bin/yt-dlp \
    && curl -fsSL "https://github.com/denoland/deno/releases/latest/download/$DENO" \
        -o /tmp/deno.zip \
    && unzip -q /tmp/deno.zip -d /usr/local/bin \
    && rm /tmp/deno.zip \
    && chmod 755 /usr/local/bin/yt-dlp /usr/local/bin/deno

RUN addgroup -S -g 8080 panda \
    && adduser -S -D -H -u 8080 -G panda -h /home/panda panda \
    && mkdir -p /data \
    && chown 8080:8080 /data

COPY --from=build /app /app
USER panda
ENV PANDA_DATA=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/panda.dll"]
