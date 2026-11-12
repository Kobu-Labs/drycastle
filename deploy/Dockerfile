# Dedicated server image. Build context: out/shooter/linux-server (from `make shooter-export`), which already contains the
# binary, .pck, self-contained .NET runtime, assets/ and the full map/. See docs/release.md.
FROM debian:bookworm-slim

# The export is self-contained (own .NET runtime); it only needs glibc and libstdc++. ICU is not installed, so use invariant globalization.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libstdc++6 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --create-home --uid 10001 shooter

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DRYCASTLE_ASSETS=/app/assets

WORKDIR /app
COPY --chown=shooter:shooter . /app
RUN chmod +x /app/drycastle-server.x86_64

USER shooter
EXPOSE 27015/udp

# Database comes from DRYCASTLE_DB (a postgres:// URL, or a SQLite file path); the map is /app/map next to the binary.
ENTRYPOINT ["/app/drycastle-server.x86_64", "--headless", "--", "--role", "server", "--port", "27015"]
