FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# TARGETARCH is set by buildx; OCR_MODELS=skip builds an image without the OCR models (Ocr__Mode must then stay off)
ARG TARGETARCH
ARG OCR_MODELS=download

WORKDIR /app

COPY . ./
RUN dotnet restore MailArchiver.csproj && dotnet publish MailArchiver.csproj -c Release -o out

# Fork: OCR models for the attachment text engine (pinned revisions, SHA-256 checked, about 13 MB).
RUN if [ "$OCR_MODELS" = "download" ]; then \
      command -v curl >/dev/null || (apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*); \
      bash tools/fetch-ocr-models.sh out/ocr-models; \
    fi

# Keep only the native libraries (ONNX Runtime, SkiaSharp) of the platform being built.
RUN case "$TARGETARCH" in arm64) keep=linux-arm64 ;; *) keep=linux-x64 ;; esac; \
    if [ -d out/runtimes ]; then find out/runtimes -mindepth 1 -maxdepth 1 ! -name "$keep" -exec rm -rf {} +; fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install Kerberos library required by MailKit (libgssapi_krb5.so.2) and curl for the
# authenticated health check below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/out ./

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000

ENV TZ=UTC

# Liveness only: it does not touch PostgreSQL, so a database outage does not make Docker
# restart a healthy process. Requires an admin API key; without one the API answers 401 and
# the container stays unhealthy. Set MAILARCHIVER_HEALTH_API_KEY in the environment.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD sh -c 'curl -fsS -H "Authorization: Bearer $MAILARCHIVER_HEALTH_API_KEY" http://localhost:5000/api/v1/health/live || exit 1'

ENTRYPOINT ["dotnet", "MailArchiver.dll"]
