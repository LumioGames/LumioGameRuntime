#!/usr/bin/env bash
set -euo pipefail

expected="10.0.11"
if actual="$(dotnet --version 2>/dev/null)"; then
  actual="$(printf '%s' "$actual" | tr -d '\r\n')"
else
  actual="<unavailable>"
fi

if [[ "$actual" != "$expected" ]]; then
  printf 'SDK_MISMATCH expected=%s actual=%s\n' "$expected" "$actual" >&2
  exit 21
fi

printf 'SDK_OK version=%s\n' "$actual"
