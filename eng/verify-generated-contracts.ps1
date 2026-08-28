[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$currentDirectory = Join-Path $repoRoot 'src/Lumio.GameRuntime.GeneratedContracts/Generated'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('lumio-contract-verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    & (Join-Path $PSScriptRoot 'generate-contracts.ps1') -OutputDirectory (Join-Path $temporaryDirectory 'Generated')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path -LiteralPath $currentDirectory -PathType Container)) {
        [Console]::Error.WriteLine('GENERATED_CONTRACT_DRIFT Generated')
        exit 32
    }

    $currentFiles = @(Get-ChildItem -LiteralPath $currentDirectory -Recurse -File | ForEach-Object { $_.FullName.Substring($currentDirectory.Length + 1).Replace('\', '/') } | Sort-Object)
    $generatedDirectory = Join-Path $temporaryDirectory 'Generated'
    $generatedFiles = @(Get-ChildItem -LiteralPath $generatedDirectory -Recurse -File | ForEach-Object { $_.FullName.Substring($generatedDirectory.Length + 1).Replace('\', '/') } | Sort-Object)
    $differences = @()
    foreach ($path in @($currentFiles + $generatedFiles | Sort-Object -Unique)) {
        $currentPath = Join-Path $currentDirectory ($path.Replace('/', '\'))
        $generatedPath = Join-Path $generatedDirectory ($path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf) -or -not (Test-Path -LiteralPath $generatedPath -PathType Leaf)) {
            $differences += $path
            continue
        }
        if ((Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash) {
            $differences += $path
        }
    }
    if ($differences.Count -gt 0) {
        [Console]::Error.WriteLine('GENERATED_CONTRACT_DRIFT')
        $differences | ForEach-Object { [Console]::Error.WriteLine($_) }
        exit 32
    }
    Write-Output 'GENERATED_CONTRACTS_VERIFIED'
} finally {
    if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
