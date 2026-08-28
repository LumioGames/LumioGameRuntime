#!/usr/bin/env bash
set -euo pipefail

# 本仓要求的是「携带 runtime 10.0.11 的 .NET 10 SDK 版本族」,不是字面 SDK 版本号 10.0.11:
# 10.0.11 是 Microsoft.NETCore.App 的 runtime 版本,微软没有发布同号 SDK(实测承载版本族为 10.0.1xx / 10.0.4xx)。
# 因此 global.json 只能锁到「.NET 10 SDK 版本族」,runtime 这一半必须由本脚本补齐——两个口径缺一不可。
# LUMIO_EXPECTED_* 只供负向 fixture 覆写,正式验证不得设置。

expected_runtime="${LUMIO_EXPECTED_RUNTIME:-10.0.11}"
expected_sdk_prefix="${LUMIO_EXPECTED_SDK_PREFIX:-10.0.}"

# 必须在仓库根解析,否则校验到的是调用方 cwd 的另一份 global.json。
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$repo_root"

if sdk_version="$(dotnet --version 2>/dev/null)"; then
  sdk_version="$(printf '%s' "$sdk_version" | tr -d '\r\n')"
else
  sdk_version="<unavailable>"
fi

if [[ "$sdk_version" != "$expected_sdk_prefix"* ]]; then
  printf 'SDK_MISMATCH expected=%s* actual=%s\n' "$expected_sdk_prefix" "$sdk_version" >&2
  exit 21
fi

runtimes="$(dotnet --list-runtimes 2>/dev/null || true)"
installed_runtimes="$(printf '%s\n' "$runtimes" | awk '$1=="Microsoft.NETCore.App"{print $2}' | paste -sd, - | tr -d '\r')"
[[ -z "$installed_runtimes" ]] && installed_runtimes="<unavailable>"

if ! printf '%s\n' "$runtimes" | awk '$1=="Microsoft.NETCore.App"{print $2}' | grep -Fxq "$expected_runtime"; then
  printf 'SDK_MISMATCH expected=runtime %s actual=%s sdk=%s\n' "$expected_runtime" "$installed_runtimes" "$sdk_version" >&2
  exit 21
fi

printf 'SDK_OK sdk=%s runtime=%s\n' "$sdk_version" "$expected_runtime"
