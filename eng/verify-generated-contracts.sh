#!/usr/bin/env bash
set -euo pipefail

# 本脚本跑两条**性质不同**的检查,不要合并:
#
#   1. 生成物完整性(硬 gate,决定退出码)
#      生成物 == 从 manifest 记录的 architectureSourceCommit 重新生成的结果。
#      证明「生成物未被手改、manifest 的 provenance 戳属实」。锚定已提交对象后这条是确定性的。
#
#   2. 上游同步度(纯报告,永不影响退出码)
#      manifest 记录的 commit 与架构源上游当前发布之间的差距。
#      不能 fail:上游每改一次生成器就打断下游是错的。
#      只做 git 事实比对,不跑第二次生成器——跑生成器会让结论取决于「什么时候跑」,
#      那本身就是移动靶,正是本次要消灭的东西。
#
# 设计依据见 .spec/decisions/0002-generated-contract-gate-anchors-committed-objects.md。

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
current_directory="$repo_root/src/Lumio.GameRuntime.GeneratedContracts/Generated"
manifest_path="$current_directory/generated-contract-manifest.json"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/lumio-contract-verify.XXXXXX")"
trap 'rm -rf "$temporary_directory"' EXIT

if [[ ! -d "$current_directory" ]]; then
  printf 'GENERATED_CONTRACT_DRIFT Generated\n' >&2
  exit 32
fi
if [[ ! -f "$manifest_path" ]]; then
  printf 'GENERATED_CONTRACT_MANIFEST_MISSING %s\n' "${manifest_path#"$repo_root"/}" >&2
  exit 32
fi

# 回放锚点取自**已提交的 manifest**,不是上游 HEAD,也不是调用方传入的值。
pinned_commit="$(sed -n 's/.*"architectureSourceCommit":"\([0-9a-f]\{40\}\)".*/\1/p' "$manifest_path")"
if [[ ! "$pinned_commit" =~ ^[0-9a-f]{40}$ ]]; then
  printf 'GENERATED_CONTRACT_MANIFEST_UNREADABLE architectureSourceCommit\n' >&2
  exit 32
fi

# ---- 检查 1:生成物完整性(硬 gate)----
set +e
output=$(
  LUMIO_GENERATED_OUTPUT="$temporary_directory/Generated" \
  LUMIO_ARCHITECTURE_COMMIT="$pinned_commit" \
  bash "$repo_root/eng/generate-contracts.sh" 2>&1
)
status=$?
set -e
if [[ "$status" -ne 0 ]]; then
  printf '%s\n' "$output" >&2
  exit "$status"
fi
printf '%s\n' "$output"

if ! diff_output=$(diff -rq "$current_directory" "$temporary_directory/Generated" 2>&1); then
  printf 'GENERATED_CONTRACT_DRIFT pinned=%s\n' "$pinned_commit" >&2
  # 诊断行一律折成仓库相对路径。`diff -rq` 对子目录里的多余文件输出的是
  # `Only in <dir>/Artifacts: x.cs`,所以两个 Only in 规则都必须匹配到子目录后缀,
  # 否则绝对临时路径会原样漏进报错里。
  printf '%s\n' "$diff_output" \
    | sed -E "s#^Files $current_directory/(.*) and .* differ\$#generated file contents differ: \1#; \
              s#^Only in $current_directory(/(.*))?: (.*)\$#generated file inventory differs, only in repository: \2/\3#; \
              s#^Only in $temporary_directory/Generated(/(.*))?: (.*)\$#generated file inventory differs, only in regenerated output: \2/\3#; \
              s#: /#: #" >&2
  exit 32
fi
printf 'GENERATED_CONTRACTS_VERIFIED pinned=%s\n' "$pinned_commit"

# ---- 检查 2:上游同步度(报告,永不 fail)----
# 从这里往下的任何失败都不得改变退出码:上游不可达、未 fetch、无 origin 都只是「测不了」。
report_upstream_drift() {
  # LUMIO_ARCHITECTURE_ROOT 到这里必然已设置且是个仓库——上面的硬 gate 调过 generate-contracts.sh,
  # 未设置会先以 ARCHITECTURE_ROOT_MISSING / exit 31 结束,所以这里不再重复判空。
  local architecture_root="${LUMIO_ARCHITECTURE_ROOT}"

  local upstream_ref="${LUMIO_ARCHITECTURE_REF:-origin/main}" upstream_commit
  upstream_commit="$(git -C "$architecture_root" rev-parse --verify --quiet "${upstream_ref}^{commit}" 2>/dev/null || true)"
  [[ "$upstream_commit" =~ ^[0-9a-f]{40}$ ]] || { printf 'GENERATED_CONTRACTS_UPSTREAM_UNKNOWN reason=ref-unresolvable ref=%s\n' "$upstream_ref"; return 0; }

  if [[ "$upstream_commit" == "$pinned_commit" ]]; then
    printf 'GENERATED_CONTRACTS_UPSTREAM_IN_SYNC ref=%s commit=%s\n' "$upstream_ref" "${upstream_commit:0:12}"
    return 0
  fi
  # merge-base --is-ancestor 用 1 表示「不是祖先」,用 128 等表示探测本身失败。
  # 混为一谈会把一次失败的探测报成「已分叉」这样的确定结论。
  local ancestry
  git -C "$architecture_root" merge-base --is-ancestor "$pinned_commit" "$upstream_commit" 2>/dev/null
  ancestry=$?
  if [[ "$ancestry" -eq 1 ]]; then
    printf 'GENERATED_CONTRACTS_UPSTREAM_DIVERGED ref=%s pinned=%s upstream=%s\n' \
      "$upstream_ref" "${pinned_commit:0:12}" "${upstream_commit:0:12}"
    return 0
  elif [[ "$ancestry" -ne 0 ]]; then
    printf 'GENERATED_CONTRACTS_UPSTREAM_UNKNOWN reason=ancestry-check-failed ref=%s status=%s\n' \
      "$upstream_ref" "$ancestry"
    return 0
  fi

  local behind contract_changes tool_changes
  behind="$(git -C "$architecture_root" rev-list --count "$pinned_commit..$upstream_commit" 2>/dev/null || echo '?')"
  # 契约面与工具面分开报:生成器变了但 Schema 没变,和 Schema 真的变了,严重性完全不同。
  contract_changes="$(git -C "$architecture_root" diff --name-only "$pinned_commit" "$upstream_commit" -- schemas ids fixtures 2>/dev/null | wc -l | tr -d ' ')"
  tool_changes="$(git -C "$architecture_root" diff --name-only "$pinned_commit" "$upstream_commit" -- tools 2>/dev/null | wc -l | tr -d ' ')"

  if [[ "$contract_changes" != "0" ]]; then
    printf 'GENERATED_CONTRACTS_UPSTREAM_CONTRACT_AHEAD ref=%s pinned=%s upstream=%s behind=%s contract_files=%s tool_files=%s\n' \
      "$upstream_ref" "${pinned_commit:0:12}" "${upstream_commit:0:12}" "$behind" "$contract_changes" "$tool_changes"
  elif [[ "$tool_changes" != "0" ]]; then
    printf 'GENERATED_CONTRACTS_UPSTREAM_GENERATOR_ONLY ref=%s pinned=%s upstream=%s behind=%s tool_files=%s\n' \
      "$upstream_ref" "${pinned_commit:0:12}" "${upstream_commit:0:12}" "$behind" "$tool_changes"
  else
    printf 'GENERATED_CONTRACTS_UPSTREAM_UNRELATED_ONLY ref=%s pinned=%s upstream=%s behind=%s\n' \
      "$upstream_ref" "${pinned_commit:0:12}" "${upstream_commit:0:12}" "$behind"
  fi
}
report_upstream_drift || true
exit 0
