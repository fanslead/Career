#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
P0_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd -P)"

usage() {
    printf 'Usage: %s <results-directory> [image-tag] [--platform linux/arm64|linux/amd64] [--rid linux-arm64|linux-x64] [--image-tag tag]\n' "$0" >&2
    printf 'Defaults: --platform linux/arm64 --rid linux-arm64\n' >&2
}

if [[ $# -eq 1 && ( "$1" == -h || "$1" == --help ) ]]; then
    usage
    exit 0
fi

if [[ $# -lt 1 ]]; then
    usage
    exit 2
fi

RESULT_DIR_INPUT="$1"
shift
IMAGE_TAG="${P0_IMAGE_TAG:-}"
if [[ $# -gt 0 && "$1" != --* ]]; then
    IMAGE_TAG="$1"
    shift
fi
PLATFORM="${P0_TARGET_PLATFORM:-linux/arm64}"
PUBLISH_RID="${P0_TARGET_RID:-linux-arm64}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --platform)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            PLATFORM="$2"
            shift 2
            ;;
        --rid)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            PUBLISH_RID="$2"
            shift 2
            ;;
        --image-tag)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            IMAGE_TAG="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown argument: %s\n' "$1" >&2
            usage
            exit 2
            ;;
    esac
done

case "${PLATFORM}:${PUBLISH_RID}" in
    linux/arm64:linux-arm64|linux/amd64:linux-x64) ;;
    *)
        printf 'Unsupported platform/RID pair: %s / %s\n' "$PLATFORM" "$PUBLISH_RID" >&2
        exit 2
        ;;
esac

if [[ -z "$IMAGE_TAG" ]]; then
    IMAGE_TAG="career-p0-${PUBLISH_RID}:10.0.400"
fi

mkdir -p -- "$RESULT_DIR_INPUT"
RESULT_DIR="$(cd -- "$RESULT_DIR_INPUT" && pwd -P)"
if [[ -n "$(find "$RESULT_DIR" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    printf 'Results directory is not empty; refusing to overwrite existing evidence: %s\n' "$RESULT_DIR" >&2
    exit 2
fi

RUN_ID="$(date -u '+%Y%m%dT%H%M%SZ')-$$-${RANDOM}"
RESOURCE_PREFIX="career-p0-${PUBLISH_RID}-${RUN_ID}"
VOLUME_NAME="${RESOURCE_PREFIX}-results"
PREP_CONTAINER_NAME="${RESOURCE_PREFIX}-volume-init"
TEST_CONTAINER_NAME="${RESOURCE_PREFIX}-validation"
RESULTS_UID="$(id -u)"
RESULTS_GID="$(id -g)"
CLEANUP_LOG="${RESULT_DIR}/resource-cleanup.log"
CONTAINER_CREATED=0
PREP_CONTAINER_CREATED=0
TEST_STARTED=0
VOLUME_CREATED=0
EXPORT_COMPLETED=0
CONTAINER_CREATE_ATTEMPTED=0
PREP_CONTAINER_CREATE_ATTEMPTED=0
VOLUME_CREATE_ATTEMPTED=0
: > "$CLEANUP_LOG"

INSPECT_VALUE=""
inspect_owned_resource() {
    local resource_kind="$1"
    local resource_name="$2"
    local format="$3"
    local inspection_output
    local inspect_status=0

    inspection_output="$(docker "$resource_kind" inspect --format "$format" "$resource_name" 2>&1)" || inspect_status=$?
    if (( inspect_status == 0 )); then
        INSPECT_VALUE="$inspection_output"
        return 0
    fi
    if [[ "$inspection_output" =~ [Nn]o\ such\ (container|volume|object) ]]; then
        return 1
    fi

    printf 'could not inspect %s %s (docker exit %s): %s\n' \
        "$resource_kind" "$resource_name" "$inspect_status" "${inspection_output:-<no diagnostic>}" >> "$CLEANUP_LOG"
    return 2
}

cleanup() {
    local exit_status=$?
    local cleanup_failed=0
    trap - EXIT INT TERM

    if (( PREP_CONTAINER_CREATED || PREP_CONTAINER_CREATE_ATTEMPTED )); then
        local prep_inspect_status
        if inspect_owned_resource container "$PREP_CONTAINER_NAME" '{{ index .Config.Labels "com.example.career-p0.run-id" }}'; then
            prep_inspect_status=0
        else
            prep_inspect_status=$?
        fi
        if (( prep_inspect_status == 0 )) && [[ "$INSPECT_VALUE" == "$RUN_ID" ]]; then
            if docker rm -f "$PREP_CONTAINER_NAME" >> "$CLEANUP_LOG" 2>&1; then
                printf 'removed prep container %s\n' "$PREP_CONTAINER_NAME" >> "$CLEANUP_LOG"
            else
                printf 'failed to remove prep container %s\n' "$PREP_CONTAINER_NAME" >> "$CLEANUP_LOG"
                cleanup_failed=1
            fi
        elif (( prep_inspect_status == 0 )); then
            printf 'refused to remove prep container %s because its ownership label did not match\n' "$PREP_CONTAINER_NAME" >> "$CLEANUP_LOG"
            cleanup_failed=1
        elif (( prep_inspect_status == 2 )); then
            cleanup_failed=1
        fi
    fi

    if (( CONTAINER_CREATED || CONTAINER_CREATE_ATTEMPTED )); then
        local test_inspect_status
        if inspect_owned_resource container "$TEST_CONTAINER_NAME" '{{ index .Config.Labels "com.example.career-p0.run-id" }}'; then
            test_inspect_status=0
        else
            test_inspect_status=$?
        fi
        if (( test_inspect_status == 0 )) && [[ "$INSPECT_VALUE" == "$RUN_ID" ]]; then
            if docker rm -f "$TEST_CONTAINER_NAME" >> "$CLEANUP_LOG" 2>&1; then
                printf 'removed validation container %s\n' "$TEST_CONTAINER_NAME" >> "$CLEANUP_LOG"
            else
                printf 'failed to remove validation container %s\n' "$TEST_CONTAINER_NAME" >> "$CLEANUP_LOG"
                cleanup_failed=1
            fi
        elif (( test_inspect_status == 0 )); then
            printf 'refused to remove validation container %s because its ownership label did not match\n' "$TEST_CONTAINER_NAME" >> "$CLEANUP_LOG"
            cleanup_failed=1
        elif (( test_inspect_status == 2 )); then
            cleanup_failed=1
        fi
    fi

    if (( VOLUME_CREATED || VOLUME_CREATE_ATTEMPTED )); then
        local volume_inspect_status
        if inspect_owned_resource volume "$VOLUME_NAME" '{{ index .Labels "com.example.career-p0.run-id" }}'; then
            volume_inspect_status=0
        else
            volume_inspect_status=$?
        fi
        if (( volume_inspect_status == 0 )) && [[ "$INSPECT_VALUE" == "$RUN_ID" ]]; then
            if (( EXPORT_COMPLETED || ! TEST_STARTED )); then
                if docker volume rm "$VOLUME_NAME" >> "$CLEANUP_LOG" 2>&1; then
                    printf 'removed results volume %s\n' "$VOLUME_NAME" >> "$CLEANUP_LOG"
                else
                    printf 'failed to remove results volume %s\n' "$VOLUME_NAME" >> "$CLEANUP_LOG"
                    cleanup_failed=1
                fi
            else
                printf 'preserved results volume %s because export did not complete\n' "$VOLUME_NAME" >> "$CLEANUP_LOG"
            fi
        elif (( volume_inspect_status == 0 )); then
            printf 'refused to remove results volume %s because its ownership label did not match\n' "$VOLUME_NAME" >> "$CLEANUP_LOG"
            cleanup_failed=1
        elif (( volume_inspect_status == 2 )); then
            cleanup_failed=1
        fi
    fi

    if (( cleanup_failed )) && (( exit_status == 0 )); then
        exit_status=1
    fi
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

{
    printf 'captured_utc=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    printf 'docker_server=%s\n' "$(docker version --format '{{.Server.Version}}')"
    printf 'target_platform=%s\n' "$PLATFORM"
    printf 'target_rid=%s\n' "$PUBLISH_RID"
    printf 'node_base=node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d\n'
    printf 'dotnet_base=mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510\n'
} > "${RESULT_DIR}/run-metadata.txt"

printf 'Building image %s for %s / %s\n' "$IMAGE_TAG" "$PLATFORM" "$PUBLISH_RID"
docker build \
    --platform "$PLATFORM" \
    --build-arg "P0_TARGET_RID=${PUBLISH_RID}" \
    --progress plain \
    --file "${P0_DIR}/Dockerfile" \
    --tag "$IMAGE_TAG" \
    "$P0_DIR" 2>&1 | tee "${RESULT_DIR}/docker-build.log"

docker image inspect --format '{"Id":{{json .Id}},"Os":{{json .Os}},"Architecture":{{json .Architecture}},"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}}}' "$IMAGE_TAG" > "${RESULT_DIR}/image-inspect.json"
printf 'image_id=%s\n' "$(docker image inspect --format '{{.Id}}' "$IMAGE_TAG")" >> "${RESULT_DIR}/run-metadata.txt"
{
    printf 'target_platform=%s\n' "$PLATFORM"
    docker run --rm \
        --platform "$PLATFORM" \
        --network none \
        --init \
        --entrypoint /bin/sh \
        "$IMAGE_TAG" \
        -c 'dotnet --info; python3 --version; clang --version; dpkg-query --show build-essential clang python3 zlib1g-dev'
} > "${RESULT_DIR}/environment.txt" 2>&1

if docker container inspect "$PREP_CONTAINER_NAME" >/dev/null 2>&1 || docker container inspect "$TEST_CONTAINER_NAME" >/dev/null 2>&1; then
    printf 'Generated container name collision; refusing to touch existing containers\n' >&2
    exit 2
fi
if docker volume inspect "$VOLUME_NAME" >/dev/null 2>&1; then
    printf 'Generated volume name collision; refusing to touch existing volume\n' >&2
    exit 2
fi

VOLUME_CREATE_ATTEMPTED=1
docker volume create \
    --name "$VOLUME_NAME" \
    --label com.example.career-p0.owner=validate-linux-container.sh \
    --label "com.example.career-p0.run-id=${RUN_ID}" \
    > "${RESULT_DIR}/validation-volume-create.txt"
VOLUME_CREATED=1
VOLUME_LABEL_RUN_ID="$(docker volume inspect --format '{{ index .Labels "com.example.career-p0.run-id" }}' "$VOLUME_NAME")"
if [[ "$VOLUME_LABEL_RUN_ID" != "$RUN_ID" ]]; then
    printf 'Generated volume name was claimed by a resource with a different ownership label\n' >&2
    exit 2
fi
VOLUME_CREATE_ATTEMPTED=0
printf '%s\n' "$VOLUME_NAME" > "${RESULT_DIR}/validation-volume-name.txt"

PREP_CONTAINER_CREATE_ATTEMPTED=1
PREP_CONTAINER_ID="$(docker create \
    --name "$PREP_CONTAINER_NAME" \
    --label com.example.career-p0.owner=validate-linux-container.sh \
    --label "com.example.career-p0.run-id=${RUN_ID}" \
    --platform "$PLATFORM" \
    --network none \
    --init \
    --user 0:0 \
    --mount "type=volume,source=${VOLUME_NAME},target=/results" \
    --entrypoint /bin/sh \
    "$IMAGE_TAG" \
    -c "chown ${RESULTS_UID}:${RESULTS_GID} /results")"
PREP_CONTAINER_CREATED=1
PREP_CONTAINER_RUN_ID="$(docker container inspect --format '{{ index .Config.Labels "com.example.career-p0.run-id" }}' "$PREP_CONTAINER_NAME")"
if [[ "$PREP_CONTAINER_RUN_ID" != "$RUN_ID" ]]; then
    printf 'Generated volume-init container name was claimed by a resource with a different ownership label\n' >&2
    exit 2
fi
PREP_CONTAINER_CREATE_ATTEMPTED=0
printf '%s\n' "$PREP_CONTAINER_NAME" > "${RESULT_DIR}/validation-volume-init-container-name.txt"
printf '%s\n' "$PREP_CONTAINER_ID" > "${RESULT_DIR}/validation-volume-init-container-id.txt"
docker start "$PREP_CONTAINER_NAME" > /dev/null
PREP_EXIT_CODE="$(docker wait "$PREP_CONTAINER_NAME")"
printf '%s\n' "$PREP_EXIT_CODE" > "${RESULT_DIR}/validation-volume-init-exit-code.txt"
if [[ "$PREP_EXIT_CODE" != 0 ]]; then
    printf 'Could not initialize validation volume ownership (exit %s)\n' "$PREP_EXIT_CODE" >&2
    exit "$PREP_EXIT_CODE"
fi
docker rm "$PREP_CONTAINER_NAME" > /dev/null
PREP_CONTAINER_CREATED=0

if docker container inspect "$TEST_CONTAINER_NAME" >/dev/null 2>&1; then
    printf 'Generated validation container name collision; refusing to touch existing container\n' >&2
    exit 2
fi
CONTAINER_CREATE_ATTEMPTED=1
TEST_CONTAINER_ID="$(docker create \
    --name "$TEST_CONTAINER_NAME" \
    --label com.example.career-p0.owner=validate-linux-container.sh \
    --label "com.example.career-p0.run-id=${RUN_ID}" \
    --platform "$PLATFORM" \
    --network none \
    --init \
    --user "${RESULTS_UID}:${RESULTS_GID}" \
    --mount "type=volume,source=${VOLUME_NAME},target=/results" \
    --env DOTNET_CLI_HOME=/tmp/dotnet \
    --env DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    --env DOTNET_NOLOGO=1 \
    --entrypoint /bin/sh \
    "$IMAGE_TAG" \
    -c 'set -eu; mkdir -p /results/lockfiles; cp /out/locks/*.json /out/locks/SHA256SUMS /out/locks/target-diff.txt /results/lockfiles/; cd /results/lockfiles; sha256sum -c SHA256SUMS; exec python3 /src/scripts/validate.py --host /out/host/P0.Host --worker /out/worker/P0.Worker.dll --dotnet dotnet --output /results/validation')"
CONTAINER_CREATED=1
TEST_CONTAINER_RUN_ID="$(docker container inspect --format '{{ index .Config.Labels "com.example.career-p0.run-id" }}' "$TEST_CONTAINER_NAME")"
if [[ "$TEST_CONTAINER_RUN_ID" != "$RUN_ID" ]]; then
    printf 'Generated validation container name was claimed by a resource with a different ownership label\n' >&2
    exit 2
fi
CONTAINER_CREATE_ATTEMPTED=0
printf '%s\n' "$TEST_CONTAINER_NAME" > "${RESULT_DIR}/validation-container-name.txt"
printf '%s\n' "$TEST_CONTAINER_ID" > "${RESULT_DIR}/validation-container-id.txt"
TEST_STARTED=1
docker start "$TEST_CONTAINER_NAME" > /dev/null
docker wait "$TEST_CONTAINER_NAME" > "${RESULT_DIR}/validation-container-exit-code.txt" &
WAIT_PID=$!
set +e
docker logs --follow "$TEST_CONTAINER_NAME" 2>&1 | tee "${RESULT_DIR}/validation-console.log"
LOG_STATUS=${PIPESTATUS[0]}
set -e
wait "$WAIT_PID"
RUN_STATUS="$(<"${RESULT_DIR}/validation-container-exit-code.txt")"

if docker cp "${TEST_CONTAINER_NAME}:/results/." "${RESULT_DIR}/" > "${RESULT_DIR}/docker-cp.log" 2>&1; then
    :
else
    COPY_STATUS=$?
    printf 'docker cp failed with exit code %s; preserving owned volume for recovery\n' "$COPY_STATUS" >&2
    exit "$COPY_STATUS"
fi
if [[ ! -s "${RESULT_DIR}/lockfiles/SHA256SUMS" || ! -s "${RESULT_DIR}/validation/summary.json" ]]; then
    printf 'Exported results are incomplete; preserving owned volume for recovery\n' >&2
    exit 1
fi
if command -v sha256sum >/dev/null 2>&1; then
    (cd "${RESULT_DIR}/lockfiles" && sha256sum -c SHA256SUMS) > "${RESULT_DIR}/exported-lock-check.txt" 2>&1
else
    (cd "${RESULT_DIR}/lockfiles" && shasum -a 256 -c SHA256SUMS) > "${RESULT_DIR}/exported-lock-check.txt" 2>&1
fi

if ! python3 - "${RESULT_DIR}/validation/summary.json" <<'PY'
import json
import sys

path = sys.argv[1]
try:
    with open(path, encoding="utf-8") as stream:
        summary = json.load(stream)
except (OSError, json.JSONDecodeError) as error:
    print(f"cannot read validation summary: {error}", file=sys.stderr)
    raise SystemExit(1)

if not isinstance(summary, dict):
    raise SystemExit("validation summary must be a JSON object")
names = summary.get("caseNames")
cases = summary.get("cases")
if not isinstance(names, list) or not names or any(not isinstance(name, str) or not name.strip() for name in names):
    raise SystemExit("validation summary caseNames must be a nonempty list of names")
if len(set(names)) != len(names):
    raise SystemExit("validation summary caseNames contains duplicates")
if type(summary.get("caseCount")) is not int or summary["caseCount"] != len(names):
    raise SystemExit("validation summary caseCount does not match caseNames")
if not isinstance(cases, list) or len(cases) != len(names):
    raise SystemExit("validation summary cases count does not match caseNames")
actual_names = [case.get("name") if isinstance(case, dict) else None for case in cases]
if actual_names != names:
    raise SystemExit("validation summary case names do not match caseNames order")
if any(not isinstance(case, dict) or type(case.get("ok")) is not bool for case in cases):
    raise SystemExit("every validation case must contain a boolean ok value")
passed = sum(case["ok"] is True for case in cases)
failed = sum(case["ok"] is False for case in cases)
if type(summary.get("passed")) is not int or summary["passed"] != passed:
    raise SystemExit("validation summary passed count is inconsistent")
if type(summary.get("failed")) is not int or summary["failed"] != failed:
    raise SystemExit("validation summary failed count is inconsistent")
if summary.get("suiteError") or failed:
    raise SystemExit(f"validation suite failed: suiteError={summary.get('suiteError')!r}, failed={failed}")
PY
then
    printf 'Exported validation summary is invalid or contains failed cases; preserving owned volume for recovery\n' >&2
    exit 1
fi
EXPORT_COMPLETED=1

if [[ "$RUN_STATUS" != 0 ]]; then
    exit "$RUN_STATUS"
fi
if [[ "$LOG_STATUS" != 0 ]]; then
    exit "$LOG_STATUS"
fi
