#!/usr/bin/env bash
set -euo pipefail
career_app_directory="$(cd "$(dirname "$0")" && pwd)"
exec "$career_app_directory/Career.Host" "$@"
