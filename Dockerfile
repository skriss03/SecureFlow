# SecureFlow server image (API + web UI). Every setting comes from environment variables at run time
# (see deploy/openshift/configmap.yaml), so the same image runs on a laptop and on OCP.
#
#   docker build -t secureflow:local .
#   docker run -p 5180:8080 -e ANTHROPIC_API_KEY=... -v secureflow-data:/data secureflow:local
#
# Base images are build arguments so they can come from an internal registry / Artifactory mirror.
ARG NODE_IMAGE=node:22-alpine
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${NODE_IMAGE} AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# vite.config.ts writes into ../src/SecureFlow.Api/wwwroot
RUN mkdir -p /src/src/SecureFlow.Api && npm run build

FROM ${SDK_IMAGE} AS api
WORKDIR /src
COPY Directory.Build.props SecureFlow.slnx ./
COPY src/ src/
COPY --from=web /src/src/SecureFlow.Api/wwwroot src/SecureFlow.Api/wwwroot
RUN dotnet publish src/SecureFlow.Api/SecureFlow.Api.csproj -c Release -o /app --nologo

FROM ${RUNTIME_IMAGE}
# git is needed for scans started from the UI with a repository URL; CLI uploads don't use it.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api /app ./
COPY samples/diagram.png samples/payments-platform.png /app/samples/
# OpenShift runs containers with an arbitrary UID in group 0: make writable paths group-owned and writable.
RUN mkdir -p /data && chgrp -R 0 /data /app && chmod -R g=u /data /app
ENV Urls=http://+:8080 \
    Storage__DataDir=/data \
    Storage__SamplesDir=/app/samples \
    Ai__CacheDir=/data/cache \
    DOTNET_RUNNING_IN_CONTAINER=true
VOLUME /data
EXPOSE 8080
USER 1001
ENTRYPOINT ["dotnet", "SecureFlow.Api.dll"]
