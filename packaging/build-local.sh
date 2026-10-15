#!/usr/bin/env bash
set -euo pipefail
career_root="$(cd "$(dirname "$0")/.." && pwd)"
career_rid="${1:-}"
if [[ -z "$career_rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) career_rid=osx-arm64 ;;
    Darwin-x86_64) career_rid=osx-x64 ;;
    Linux-aarch64) career_rid=linux-arm64 ;;
    Linux-x86_64) career_rid=linux-x64 ;;
    *) echo "Please pass a supported .NET runtime identifier." >&2; exit 2 ;;
  esac
fi
case "$career_rid" in osx-arm64|osx-x64|linux-arm64|linux-x64|win-x64|win-arm64) ;; *) echo "Unsupported runtime identifier." >&2; exit 2 ;; esac
career_output="$career_root/artifacts/release/$career_rid"
mkdir -p "$career_output"
(cd "$career_root/apps/web" && npm ci --ignore-scripts --no-audit --no-fund && npm run build)
dotnet publish "$career_root/src/Career.AgentWorker/Career.AgentWorker.csproj" -c Release -r "$career_rid" --self-contained true -o "$career_output/worker" --nologo
dotnet publish "$career_root/src/Career.Host/Career.Host.csproj" -c Release -r "$career_rid" --self-contained true -p:PublishAot=true -p:CareerWorkerPublishDir="$career_output/worker" -o "$career_output/app" --nologo
if [[ "$career_rid" == osx-* ]]; then
  cp "$career_root/packaging/Career.command" "$career_output/app/Career.command"
  chmod +x "$career_output/app/Career.command"
fi
printf 'Built Career at %s\n' "$career_output/app"
