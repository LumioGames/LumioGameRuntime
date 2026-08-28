$ErrorActionPreference = 'Stop'

$expected = '10.0.11'
try {
    $actual = (& dotnet --version 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($actual)) {
        $actual = '<unavailable>'
    }
} catch {
    $actual = '<unavailable>'
}

if ($actual -ne $expected) {
    [Console]::Error.WriteLine("SDK_MISMATCH expected=$expected actual=$actual")
    exit 21
}

Write-Output "SDK_OK version=$actual"
