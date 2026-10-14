# syntax=docker/dockerfile:1.7
# Build from this directory with matching --platform and P0_TARGET_RID values.
# Both bases are pinned to multi-architecture index digests; the platform flag
# selects the intended Linux architecture from those indexes.
ARG NODE_IMAGE=node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510
ARG P0_TARGET_RID=linux-arm64
FROM ${NODE_IMAGE} AS web-build

WORKDIR /src/Web
COPY Web/package.json Web/package-lock.json ./
RUN node --version && npm --version && npm ci --no-audit --no-fund
COPY Web/ ./
RUN npm run build

FROM ${DOTNET_SDK_IMAGE} AS toolchain

ARG EXPECTED_DOTNET_SDK=10.0.400
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        build-essential clang python3 zlib1g-dev \
    && rm -rf /var/lib/apt/lists/* \
    && test "$(dotnet --version)" = "${EXPECTED_DOTNET_SDK}" \
    && python3 --version \
    && clang --version \
    && dpkg-query --show build-essential clang python3 zlib1g-dev

FROM toolchain AS publish
ARG P0_TARGET_RID
ARG TARGETARCH

WORKDIR /src
COPY global.json ./
COPY P0.Host/ ./P0.Host/
COPY P0.Worker/ ./P0.Worker/
COPY scripts/validate.py ./scripts/validate.py
COPY --from=web-build /src/Web/dist ./Web/dist

RUN case "${TARGETARCH}:${P0_TARGET_RID}" in arm64:linux-arm64|amd64:linux-x64) ;; *) echo "Mismatched or unsupported TARGETARCH/P0_TARGET_RID: ${TARGETARCH}/${P0_TARGET_RID}" >&2; exit 2 ;; esac \
    && mkdir -p /out/locks \
    && cp P0.Host/packages.lock.json /out/locks/P0.Host.packages.lock.osx-arm64.json \
    && dotnet restore P0.Host/P0.Host.csproj --runtime "${P0_TARGET_RID}" --force-evaluate \
    && cp P0.Host/packages.lock.json "/out/locks/P0.Host.packages.lock.${P0_TARGET_RID}.json" \
    && dotnet restore P0.Host/P0.Host.csproj --runtime "${P0_TARGET_RID}" --locked-mode \
    && dotnet restore P0.Worker/P0.Worker.csproj --locked-mode \
    && cp P0.Worker/packages.lock.json /out/locks/P0.Worker.packages.lock.json \
    && (cd /out/locks && sha256sum P0.Host.packages.lock.osx-arm64.json "P0.Host.packages.lock.${P0_TARGET_RID}.json" P0.Worker.packages.lock.json > SHA256SUMS) \
    && python3 -c 'import json, os; from pathlib import Path; root=Path("/out/locks"); source=json.loads((root/"P0.Host.packages.lock.osx-arm64.json").read_text()); rid=os.environ["P0_TARGET_RID"]; linux=json.loads((root/("P0.Host.packages.lock."+rid+".json")).read_text()); worker=json.loads((root/"P0.Worker.packages.lock.json").read_text()); print("sourceHostTargets="+",".join(source["dependencies"])); print("linuxHostTargets="+",".join(linux["dependencies"])); print("workerTargets="+",".join(worker["dependencies"]))' > /out/locks/target-diff.txt

RUN dotnet publish P0.Host/P0.Host.csproj \
        --configuration Release \
        --runtime "${P0_TARGET_RID}" \
        --self-contained true \
        --no-restore \
        --output /out/host \
        -p:PublishAot=true \
    && dotnet publish P0.Worker/P0.Worker.csproj \
        --configuration Release \
        --no-restore \
        --output /out/worker \
        -p:UseAppHost=false

FROM publish AS validation

ENTRYPOINT ["python3", "/src/scripts/validate.py"]
CMD ["--host", "/out/host/P0.Host", "--worker", "/out/worker/P0.Worker.dll", "--dotnet", "dotnet", "--output", "/results"]
