#!/usr/bin/env bash
set -euo pipefail
career_root="$(cd "$(dirname "$0")/.." && pwd)"
(cd "$career_root/apps/web" && npm ci --ignore-scripts --no-audit --no-fund && npm run build)
dotnet build "$career_root/Career.slnx" -c Release --nologo
exec dotnet "$career_root/src/Career.Host/bin/Release/net10.0/Career.Host.dll" \
  --worker "$career_root/src/Career.AgentWorker/bin/Release/net10.0/Career.AgentWorker.dll" \
  --workspace "${CAREER_WORKSPACE:-$career_root/artifacts/development-workspace}" \
  --port "${CAREER_PORT:-5198}"
