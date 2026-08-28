$ErrorActionPreference = 'Stop'

# 与 eng/verify-sdk.sh 同一双口径(语义说明见该脚本注释):
# ① global.json 必须解析出 .NET 10 SDK 版本族;② 该 SDK 必须随附 runtime 10.0.11。
# LUMIO_EXPECTED_* 只供负向 fixture 覆写,正式验证不得设置。

$expectedRuntime = if ($env:LUMIO_EXPECTED_RUNTIME) { $env:LUMIO_EXPECTED_RUNTIME } else { '10.0.11' }
$expectedSdkPrefix = if ($env:LUMIO_EXPECTED_SDK_PREFIX) { $env:LUMIO_EXPECTED_SDK_PREFIX } else { '10.0.' }

# 必须在仓库根解析,否则校验到的是调用方 cwd 的另一份 global.json。
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    try {
        $sdkVersion = (& dotnet --version 2>$null | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($sdkVersion)) { $sdkVersion = '<unavailable>' }
    } catch {
        $sdkVersion = '<unavailable>'
    }

    if (-not $sdkVersion.StartsWith($expectedSdkPrefix)) {
        [Console]::Error.WriteLine("SDK_MISMATCH expected=$expectedSdkPrefix* actual=$sdkVersion")
        exit 21
    }

    try {
        $runtimeLines = @(& dotnet --list-runtimes 2>$null)
    } catch {
        $runtimeLines = @()
    }

    $netCoreVersions = @(
        $runtimeLines | ForEach-Object {
            $parts = $_.Trim() -split '\s+'
            if ($parts.Length -ge 2 -and $parts[0] -eq 'Microsoft.NETCore.App') { $parts[1] }
        }
    )

    $installedRuntimes = if ($netCoreVersions.Count -gt 0) { $netCoreVersions -join ',' } else { '<unavailable>' }

    if ($netCoreVersions -notcontains $expectedRuntime) {
        [Console]::Error.WriteLine("SDK_MISMATCH expected=runtime $expectedRuntime actual=$installedRuntimes sdk=$sdkVersion")
        exit 21
    }

    Write-Output "SDK_OK sdk=$sdkVersion runtime=$expectedRuntime"
}
finally {
    Pop-Location
}
