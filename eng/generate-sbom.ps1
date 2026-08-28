[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]] $Project
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = Join-Path $Root 'artifacts/sbom'
New-Item -ItemType Directory -Force -Path $Output | Out-Null

function Get-RelativePath([string] $Base, [string] $Target) {
    $baseUri = [Uri] (($Base.TrimEnd('\') + '\'))
    $targetUri = [Uri] $Target
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Get-TextSha256([string] $Text) {
    $sha = New-Object Security.Cryptography.SHA256Managed
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-ProjectPaths {
    $requested = [System.Collections.Generic.List[string]]::new()
    if ($Project) { foreach ($path in $Project) { $requested.Add($path) } }
    if ($env:LUMIO_DEPENDENCY_PROJECTS) {
        foreach ($path in ($env:LUMIO_DEPENDENCY_PROJECTS -split [IO.Path]::PathSeparator | Where-Object { $_ })) { $requested.Add($path) }
    }
    if ($requested.Count -gt 0) {
        return @($requested | ForEach-Object {
            if ([IO.Path]::IsPathRooted($_)) { [IO.Path]::GetFullPath($_) } else { [IO.Path]::GetFullPath((Join-Path $Root $_)) }
        } | Sort-Object -Unique)
    }
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $_.Extension -in @('.csproj', '.fsproj', '.vbproj') -and $_.FullName -notmatch '[\\/](?:bin|obj|artifacts|\.git|node_modules)[\\/]' } |
        Sort-Object FullName | ForEach-Object FullName)
}

function Get-DotnetJson([string] $ProjectPath) {
    $dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $output = (& $dotnet list $ProjectPath package --include-transitive --format json 2> $errorFile | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "dotnet exited ${LASTEXITCODE}: $((Get-Content -Raw $errorFile).Trim())" }
        $start = $output.IndexOf('{'); if ($start -lt 0) { throw 'dotnet did not return JSON' }
        return $output.Substring($start) | ConvertFrom-Json
    } finally { Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue }
}

function Add-GraphPackages($Node, [System.Collections.Generic.List[object]] $Result) {
    if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsValueType) { return }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) { foreach ($value in $Node) { Add-GraphPackages $value $Result }; return }
    $id = $Node.PSObject.Properties['id']; $resolved = $Node.PSObject.Properties['resolvedVersion']; $version = $Node.PSObject.Properties['version']
    if ($id -and $id.Value -is [string] -and (($resolved -and $resolved.Value) -or ($version -and $version.Value))) {
        $resolvedVersion = if ($resolved -and $resolved.Value) { [string] $resolved.Value } else { [string] $version.Value }
        $Result.Add([pscustomobject]@{ id = [string] $id.Value; version = $resolvedVersion; contentHash = if ($Node.contentHash) { [string] $Node.contentHash } else { $null } })
    }
    foreach ($property in $Node.PSObject.Properties) { Add-GraphPackages $property.Value $Result }
}

function Get-PackageEvidence($Record) {
    $roots = @($env:NUGET_PACKAGES, $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.nuget/packages' }), $(if ($env:HOME) { Join-Path $env:HOME '.nuget/packages' })) | Where-Object { $_ }
    $nuspec = $null; $nupkg = $null
    foreach ($base in $roots) {
        $directory = Join-Path (Join-Path $base $Record.id.ToLowerInvariant()) $Record.version.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        $nuspec = Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec' | Select-Object -First 1
        $nupkg = Get-ChildItem -LiteralPath $directory -File -Filter '*.nupkg' | Select-Object -First 1
        break
    }
    $license = $null
    if ($nuspec) {
        try {
            $xml = [xml](Get-Content -Raw -LiteralPath $nuspec.FullName)
            $node = $xml.SelectSingleNode("//*[local-name()='license']")
            if ($node -and $node.GetAttribute('type') -eq 'expression') { $license = $node.InnerText.Trim() }
        } catch { $license = $null }
    }
    $hash = if ($nupkg) { (Get-FileHash -Algorithm SHA256 -LiteralPath $nupkg.FullName).Hash.ToLowerInvariant() } else { $null }
    return [pscustomobject]@{ id = $Record.id; version = $Record.version; contentHash = $Record.contentHash; license = $license; packageHash = $hash }
}

$toolVersion = if ($env:LUMIO_SBOM_TOOL_VERSION) { $env:LUMIO_SBOM_TOOL_VERSION } else { 'wrapper-1.0' }
try { if (-not $env:LUMIO_SBOM_TOOL_VERSION) { $toolVersion = (& (if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }) --version 2>$null | Out-String).Trim() } } catch { }
$components = @{}
$projectGraph = [System.Collections.Generic.List[object]]::new()
$Projects = @(Get-ProjectPaths)
foreach ($projectPath in $Projects) {
    $relative = (Get-RelativePath $Root $projectPath).Replace('\', '/')
    try { $graph = Get-DotnetJson $projectPath } catch { [Console]::Error.WriteLine("SBOM_GRAPH_UNAVAILABLE project=$relative message=$($_.Exception.Message)"); exit 32 }
    $records = [System.Collections.Generic.List[object]]::new(); Add-GraphPackages $graph $records
    $unique = @{}; foreach ($record in $records) { $unique["$($record.id.ToLowerInvariant())@$($record.version)"] = $record }
    $ids = [System.Collections.Generic.List[string]]::new()
    foreach ($record in $unique.Values) {
        $key = "$($record.id.ToLowerInvariant())@$($record.version)"; $ids.Add($record.id)
        if (-not $components.ContainsKey($key)) { $components[$key] = Get-PackageEvidence $record }
    }
    $projectGraph.Add([pscustomobject]@{ project = $relative; packages = @($ids | Sort-Object) })
}
$componentList = @($components.Values | Sort-Object @{ Expression = { "$($_.id)@$($_.version)" } } | ForEach-Object {
    $properties = @([ordered]@{ name = 'lumio:contentHash'; value = if ($_.contentHash) { $_.contentHash } else { '' } }, [ordered]@{ name = 'lumio:packageHash'; value = if ($_.packageHash) { $_.packageHash } else { '' } })
    $entry = [ordered]@{ type = 'library'; name = $_.id; version = $_.version; purl = "pkg:nuget/$([uri]::EscapeDataString($_.id))@$($_.version)"; properties = $properties }
    if ($_.license) { $entry.licenses = @([ordered]@{ license = [ordered]@{ id = $_.license } }) }
    [pscustomobject] $entry
})
$evidence = [ordered]@{ toolName = 'Lumio SBOM wrapper'; toolVersion = $toolVersion; packageHashes = [ordered]@{}; projectGraph = @($projectGraph) }
foreach ($component in $components.Values) { $evidence.packageHashes["$($component.id)@$($component.version)"] = $component.packageHash }
$evidenceJson = $evidence | ConvertTo-Json -Depth 20 -Compress
$digest = Get-TextSha256 $evidenceJson
$graphJson = if ($projectGraph.Count -eq 0) { '[]' } else { @($projectGraph) | ConvertTo-Json -Depth 20 -Compress }
$bom = [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = "urn:uuid:$($digest.Substring(0,8))-$($digest.Substring(8,4))-5$($digest.Substring(13,3))-8$($digest.Substring(17,3))-$($digest.Substring(20,12))"; version = 1; metadata = [ordered]@{ tools = @([ordered]@{ vendor = 'LumioGames'; name = $evidence.toolName; version = $toolVersion }); properties = @([ordered]@{ name = 'lumio:projectGraph'; value = $graphJson }) }; components = $componentList }
$bomJson = $bom | ConvertTo-Json -Depth 20
$bomJson | Set-Content -LiteralPath (Join-Path $Output 'bom.json') -Encoding utf8
$manifest = [ordered]@{ toolName = $evidence.toolName; toolVersion = $toolVersion; packageHashes = $evidence.packageHashes; projectGraph = @($projectGraph); generatedAtUtc = [DateTime]::UtcNow.ToString('o'); bomSha256 = Get-TextSha256 $bomJson }
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $Output 'sbom-manifest.json') -Encoding utf8
Write-Output "SBOM_OK output=artifacts/sbom projects=$($Projects.Count) packages=$($componentList.Count)"
