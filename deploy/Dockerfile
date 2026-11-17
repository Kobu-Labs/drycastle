# Dedicated server image. Build context: out/shooter/linux-server (from `make shooter-export`), which already contains the
# binary, .pck, self-contained .NET runtime, assets/ and the full map/. See docs/release.md.
FROM debian:bookworm-slim

# The export is self-contained (own .NET runtime); it only needs glibc and libstdc++, and ffmpeg for the rozhlas live feed (the server runs it as a subprocess). ICU is not installed, so use invariant globalization.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libstdc++6 ffmpeg \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --create-home --uid 10001 shooter \
 && mkdir /navcache && chown shooter:shooter /navcache

# DOTNET_GCgen0size (hex bytes, 1 MB): the young generation the .NET collector fills before it stops the game to collect. The default let it grow to
# several megabytes, and a collection then stopped a tick for 13 to 30 ms; with a small one it collects about once a second for about 6 ms
# (the same time in all, in smaller pieces: measured with 64 bots, the slowest tick of ten seconds went from 36 to 30 ms).
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_GCgen0size=100000 \
    DRYCASTLE_ASSETS=/app/assets

WORKDIR /app
COPY --chown=shooter:shooter . /app
RUN chmod +x /app/drycastle-server.x86_64

USER shooter
EXPOSE 27015/udp

# Database comes from DRYCASTLE_DB (a postgres:// URL, or a SQLite file path); the map is /app/map next to the binary.
# The bots' navigation tiles are kept in /navcache, a volume in docker-compose.yml: baked once per map version (40 s on the VPS, during which
# the server answers nobody), not again after every new image.
ENTRYPOINT ["/app/drycastle-server.x86_64", "--headless", "--", "--role", "server", "--port", "27015", "--nav-cache", "/navcache"]
