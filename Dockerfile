# syntax=docker/dockerfile:1.7
#
# CourseHub — self-hosted video course player.
#   docker build -t coursehub .
#   docker run -p 8080:8080 -v /path/to/courses:/courses:ro -v ./config:/config coursehub

ARG WHISPER_VERSION=v1.9.5

# ---------- whisper.cpp (speech-to-text for transcripts) ----------
FROM ubuntu:24.04 AS whisper
ARG WHISPER_VERSION
ARG TARGETARCH
RUN apt-get update \
 && apt-get install -y --no-install-recommends build-essential cmake git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch "${WHISPER_VERSION}" https://github.com/ggml-org/whisper.cpp /src
# x86: build every CPU variant and pick the best one at runtime, so the same image runs on
# old NAS Celerons (no AVX) and modern desktops (AVX-512). ARM64: baseline armv8 + NEON.
RUN set -eux; \
    if [ "$TARGETARCH" = "amd64" ]; then \
      CPU_FLAGS="-DBUILD_SHARED_LIBS=ON -DGGML_BACKEND_DL=ON -DGGML_CPU_ALL_VARIANTS=ON -DGGML_NATIVE=OFF"; \
    else \
      CPU_FLAGS="-DBUILD_SHARED_LIBS=OFF -DGGML_NATIVE=OFF"; \
    fi; \
    cmake -S /src -B /build -DCMAKE_BUILD_TYPE=Release \
      -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=OFF -DWHISPER_SDL2=OFF -DWHISPER_CURL=OFF $CPU_FLAGS; \
    cmake --build /build --config Release -j"$(nproc)"; \
    mkdir -p /out; \
    cp /build/bin/whisper-cli /out/; \
    find /build -name '*.so*' -exec cp -P {} /out/ \; ; \
    /out/whisper-cli --help > /dev/null 2>&1 || LD_LIBRARY_PATH=/out /out/whisper-cli --help > /dev/null

# ---------- app ----------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first for layer caching. Publish restores again once the .razor files are present:
# the SDK only adds Blazor's framework scripts (_framework/blazor.web.js) when it sees components.
COPY CourseHub.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish CourseHub.csproj -c Release -o /app /p:UseAppHost=false \
 && test -n "$(grep -l '_framework/blazor.web' /app/CourseHub.staticwebassets.endpoints.json)"

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

LABEL org.opencontainers.image.title="CourseHub" \
      org.opencontainers.image.description="Self-hosted, Udemy-style player for your own video courses"

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ffmpeg libgomp1 tzdata curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=whisper /out /opt/whisper
WORKDIR /app
COPY --from=build /app .

ENV PATH="/opt/whisper:${PATH}" \
    LD_LIBRARY_PATH="/opt/whisper" \
    ASPNETCORE_HTTP_PORTS=8080 \
    CourseHub__CoursesRoot=/courses \
    CourseHub__KeysFolder=/config/keys \
    CourseHub__RescanIntervalMinutes=30 \
    ConnectionStrings__Progress="Data Source=/config/coursehub.db" \
    MediaTools__DataFolder=/config/storage \
    TZ=Etc/UTC

RUN mkdir -p /config /courses && chown app:app /config
VOLUME ["/config"]
EXPOSE 8080
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/health > /dev/null || exit 1

ENTRYPOINT ["dotnet", "CourseHub.dll"]
