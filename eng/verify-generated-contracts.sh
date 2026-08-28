#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
current_directory="$repo_root/src/Lumio.GameRuntime.GeneratedContracts/Generated"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/lumio-contract-verify.XXXXXX")"
trap 'rm -rf "$temporary_directory"' EXIT

set +e
output=$(LUMIO_GENERATED_OUTPUT="$temporary_directory/Generated" bash "$repo_root/eng/generate-contracts.sh" 2>&1)
status=$?
set -e
if [[ "$status" -ne 0 ]]; then
  printf '%s\n' "$output" >&2
  exit "$status"
fi
printf '%s\n' "$output"

if [[ ! -d "$current_directory" ]]; then
  printf 'GENERATED_CONTRACT_DRIFT Generated\n' >&2
  exit 32
fi
if diff_output=$(diff -rq "$current_directory" "$temporary_directory/Generated" 2>&1); then
  printf 'GENERATED_CONTRACTS_VERIFIED\n'
  exit 0
fi

printf 'GENERATED_CONTRACT_DRIFT\n' >&2
printf '%s\n' "$diff_output" | sed -E 's#^Files .* and .* differ$#generated file contents differ#; s#^Only in .*: #generated file inventory differs: #' >&2
exit 32
