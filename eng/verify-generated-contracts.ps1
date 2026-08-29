[CmdletBinding()]
param()

# 本脚本跑两条**性质不同**的检查,不要合并(口径与 verify-generated-contracts.sh 一致):
#   1. 生成物完整性(硬 gate,决定退出码):生成物 == 从 manifest 记录的 commit 重新生成的结果。
#   2. 上游同步度(纯报告,永不影响退出码):只做 git 事实比对,不跑第二次生成器。

$ErrorActionPreference = 'Stop'
# 见 generate-contracts.ps1 同处注释:PS 7.4+ 默认让原生命令非零退出抛终止性错误,
# 会绕过下面所有基于 $LASTEXITCODE 的判断。
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$currentDirectory = Join-Path $repoRoot 'src/Lumio.GameRuntime.GeneratedContracts/Generated'
$manifestPath = Join-Path $currentDirectory 'generated-contract-manifest.json'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('lumio-contract-verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $currentDirectory -PathType Container)) {
        [Console]::Error.WriteLine('GENERATED_CONTRACT_DRIFT Generated')
        exit 32
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        [Console]::Error.WriteLine('GENERATED_CONTRACT_MANIFEST_MISSING src/Lumio.GameRuntime.GeneratedContracts/Generated/generated-contract-manifest.json')
        exit 32
    }

    # 回放锚点取自**已提交的 manifest**,不是上游 HEAD。
    $pinnedCommit = ''
    try { $pinnedCommit = [string]((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).architectureSourceCommit) } catch { $pinnedCommit = '' }
    if ($pinnedCommit -notmatch '^[0-9a-f]{40}$') {
        [Console]::Error.WriteLine('GENERATED_CONTRACT_MANIFEST_UNREADABLE architectureSourceCommit')
        exit 32
    }

    # ---- 检查 1:生成物完整性(硬 gate)----
    $previousCommit = $env:LUMIO_ARCHITECTURE_COMMIT
    $env:LUMIO_ARCHITECTURE_COMMIT = $pinnedCommit
    try {
        & (Join-Path $PSScriptRoot 'generate-contracts.ps1') -OutputDirectory (Join-Path $temporaryDirectory 'Generated')
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally {
        $env:LUMIO_ARCHITECTURE_COMMIT = $previousCommit
    }

    $currentFiles = @(Get-ChildItem -LiteralPath $currentDirectory -Recurse -File | ForEach-Object { $_.FullName.Substring($currentDirectory.Length + 1).Replace('\', '/') } | Sort-Object)
    $generatedDirectory = Join-Path $temporaryDirectory 'Generated'
    $generatedFiles = @(Get-ChildItem -LiteralPath $generatedDirectory -Recurse -File | ForEach-Object { $_.FullName.Substring($generatedDirectory.Length + 1).Replace('\', '/') } | Sort-Object)
    $differences = @()
    foreach ($path in @($currentFiles + $generatedFiles | Sort-Object -Unique)) {
        $currentPath = Join-Path $currentDirectory $path
        $generatedPath = Join-Path $generatedDirectory $path
        if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf) -or -not (Test-Path -LiteralPath $generatedPath -PathType Leaf)) {
            $differences += "generated file inventory differs: $path"
            continue
        }
        if ((Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash) {
            $differences += "generated file contents differ: $path"
        }
    }
    if ($differences.Count -gt 0) {
        [Console]::Error.WriteLine("GENERATED_CONTRACT_DRIFT pinned=$pinnedCommit")
        $differences | ForEach-Object { [Console]::Error.WriteLine($_) }
        exit 32
    }
    Write-Output "GENERATED_CONTRACTS_VERIFIED pinned=$pinnedCommit"

    # ---- 检查 2:上游同步度(报告,永不 fail)----
    try {
        # LUMIO_ARCHITECTURE_ROOT 到这里必然已设置——硬 gate 调过 generate-contracts.ps1,
        # 未设置会先以 ARCHITECTURE_ROOT_MISSING / exit 31 结束。
        $architectureRoot = $env:LUMIO_ARCHITECTURE_ROOT
        $upstreamRef = $env:LUMIO_ARCHITECTURE_REF
        if ([string]::IsNullOrWhiteSpace($upstreamRef)) { $upstreamRef = 'origin/main' }
        $upstreamCommit = (& git -C $architectureRoot rev-parse --verify --quiet "$upstreamRef^{commit}" 2>$null | Out-String).Trim()
        if ($upstreamCommit -notmatch '^[0-9a-f]{40}$') {
            Write-Output "GENERATED_CONTRACTS_UPSTREAM_UNKNOWN reason=ref-unresolvable ref=$upstreamRef"
        } elseif ($upstreamCommit -eq $pinnedCommit) {
            Write-Output "GENERATED_CONTRACTS_UPSTREAM_IN_SYNC ref=$upstreamRef commit=$($upstreamCommit.Substring(0,12))"
        } else {
            # 1 = 不是祖先(已分叉);其它非零 = 探测本身失败,不能当成确定结论。口径与 .sh 一致。
            & git -C $architectureRoot merge-base --is-ancestor $pinnedCommit $upstreamCommit 2>$null
            $ancestry = $LASTEXITCODE
            if ($ancestry -eq 1) {
                Write-Output "GENERATED_CONTRACTS_UPSTREAM_DIVERGED ref=$upstreamRef pinned=$($pinnedCommit.Substring(0,12)) upstream=$($upstreamCommit.Substring(0,12))"
            } elseif ($ancestry -ne 0) {
                Write-Output "GENERATED_CONTRACTS_UPSTREAM_UNKNOWN reason=ancestry-check-failed ref=$upstreamRef status=$ancestry"
            } else {
                $behind = (& git -C $architectureRoot rev-list --count "$pinnedCommit..$upstreamCommit" 2>$null | Out-String).Trim()
                # 契约面与工具面分开报:生成器变了但 Schema 没变,和 Schema 真的变了,严重性完全不同。
                $contractChanges = @(& git -C $architectureRoot diff --name-only $pinnedCommit $upstreamCommit -- schemas ids fixtures 2>$null).Count
                $toolChanges = @(& git -C $architectureRoot diff --name-only $pinnedCommit $upstreamCommit -- tools 2>$null).Count
                $short = "ref=$upstreamRef pinned=$($pinnedCommit.Substring(0,12)) upstream=$($upstreamCommit.Substring(0,12)) behind=$behind"
                if ($contractChanges -ne 0) {
                    Write-Output "GENERATED_CONTRACTS_UPSTREAM_CONTRACT_AHEAD $short contract_files=$contractChanges tool_files=$toolChanges"
                } elseif ($toolChanges -ne 0) {
                    Write-Output "GENERATED_CONTRACTS_UPSTREAM_GENERATOR_ONLY $short tool_files=$toolChanges"
                } else {
                    Write-Output "GENERATED_CONTRACTS_UPSTREAM_UNRELATED_ONLY $short"
                }
            }
        }
    } catch {
        Write-Output 'GENERATED_CONTRACTS_UPSTREAM_UNKNOWN reason=report-failed'
    }
    exit 0
} finally {
    if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
