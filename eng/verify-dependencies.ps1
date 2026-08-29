[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]] $Project
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PolicyPath = Join-Path $Root 'eng/dependency-policy.json'
$ReportDirectory = Join-Path $Root 'artifacts/dependencies'
$ReportPath = Join-Path $ReportDirectory 'dependency-report.json'
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$Policy = Get-Content -Raw -LiteralPath $PolicyPath | ConvertFrom-Json
$Issues = [System.Collections.Generic.List[object]]::new()
$ProjectRecords = [System.Collections.Generic.List[object]]::new()
$PackageRecords = @{}

function Get-RelativePath([string] $Base, [string] $Target) {
    $baseUri = [Uri] (($Base.TrimEnd('\') + '\'))
    $targetUri = [Uri] $Target
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Add-Issue([string] $Code, [string] $Message, [string] $ProjectPath = '') {
    $entry = [ordered]@{ code = $Code; message = $Message }
    if ($ProjectPath) { $entry.project = $ProjectPath }
    $Issues.Add([pscustomobject] $entry)
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

function Get-XmlProperty([System.Xml.XmlDocument] $Xml, [string] $Name) {
    $node = $Xml.SelectSingleNode("//*[local-name()='$Name']")
    if ($node) { return $node.InnerText.Trim() }
    return ''
}

function Get-CentralVersions {
    $xml = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Packages.props'))
    $versions = @{}
    foreach ($node in $xml.SelectNodes("//*[local-name()='PackageVersion']")) {
        $include = $node.GetAttribute('Include')
        $version = $node.GetAttribute('Version')
        if ($include -and $version) { $versions[$include.ToLowerInvariant()] = $version }
    }
    return $versions
}

function Test-Floating([string] $Version) {
    return [string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -match '[\[\]\(\),*?]|-[0-9A-Za-z]'
}

function Test-Scope([string] $Name, [object[]] $Scopes) {
    foreach ($scope in $Scopes) {
        $pattern = '^' + [regex]::Escape([string] $scope).Replace('\*', '.*').Replace('\?', '.') + '$'
        if ($Name -match $pattern) { return $true }
    }
    return $false
}

# packageScopes 是准入许可,不是禁令:没登记的包既不许可也不禁止。被裁决移除的包落在这个
# 空档里——删掉 packageScopes 条目后,把中央钉和引用一起加回去可以一路静默通过。
# forbiddenPackages 补的就是这一半:按 id 的 glob 匹配(NuGet id 大小写不敏感,-match 默认即是)。
# 与 verify-dependencies.sh 的 forbiddenReason 同义,两侧改动必须同步。
function Get-ForbiddenReason([string] $Id) {
    if (-not $Policy.PSObject.Properties['forbiddenPackages']) { return $null }
    foreach ($entry in $Policy.forbiddenPackages.PSObject.Properties) {
        $pattern = '^' + [regex]::Escape([string] $entry.Name).Replace('\*', '.*').Replace('\?', '.') + '$'
        if ($Id -match $pattern) { return [string] $entry.Value }
    }
    return $null
}

# 三个引入面各查一次,少一个就留一条绕道:① 中央钉(下面紧接着扫,不依赖 dotnet 解析图,
# 依赖图取不到时照样会响);② 项目里声明的 PackageReference;③ 解析图里出现的包,含传递依赖。
function Get-CentralPins {
    $xml = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Packages.props'))
    $pins = [System.Collections.Generic.List[object]]::new()
    foreach ($node in $xml.SelectNodes("//*[local-name()='PackageVersion']")) {
        $include = $node.GetAttribute('Include')
        if ($include) { $pins.Add([pscustomobject]@{ id = $include; version = $node.GetAttribute('Version') }) }
    }
    return $pins
}

function Get-DotnetJson([string] $ProjectPath, [string[]] $Extra) {
    $dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
    $arguments = @('list', $ProjectPath, 'package', '--include-transitive') + $Extra + @('--format', 'json')
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $output = (& $dotnet @arguments 2> $errorFile | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "dotnet exited ${LASTEXITCODE}: $((Get-Content -Raw $errorFile).Trim())" }
        $start = $output.IndexOf('{')
        if ($start -lt 0) { throw 'dotnet did not return JSON' }
        return $output.Substring($start) | ConvertFrom-Json
    } finally {
        Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    }
}

function Add-GraphPackages($Node, [System.Collections.Generic.List[object]] $Result) {
    if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsValueType) { return }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($value in $Node) { Add-GraphPackages $value $Result }
        return
    }
    $id = $Node.PSObject.Properties['id']
    $resolved = $Node.PSObject.Properties['resolvedVersion']
    $version = $Node.PSObject.Properties['version']
    if ($id -and $id.Value -is [string] -and (($resolved -and $resolved.Value) -or ($version -and $version.Value))) {
        $resolvedVersion = if ($resolved -and $resolved.Value) { [string] $resolved.Value } else { [string] $version.Value }
        $Result.Add([pscustomobject]@{
            id = [string] $id.Value
            version = $resolvedVersion
            requestedVersion = if ($Node.requestedVersion) { [string] $Node.requestedVersion } else { $null }
            contentHash = if ($Node.contentHash) { [string] $Node.contentHash } else { $null }
        })
    }
    foreach ($property in $Node.PSObject.Properties) { Add-GraphPackages $property.Value $Result }
}

function Get-PackageCache([string] $Id, [string] $Version) {
    $roots = @($env:NUGET_PACKAGES, $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.nuget/packages' }), $(if ($env:HOME) { Join-Path $env:HOME '.nuget/packages' })) | Where-Object { $_ }
    foreach ($base in $roots) {
        $directory = Join-Path (Join-Path $base $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        $nuspec = Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec' | Select-Object -First 1
        $nupkg = Get-ChildItem -LiteralPath $directory -File -Filter '*.nupkg' | Select-Object -First 1
        return [pscustomobject]@{ Nuspec = if ($nuspec) { $nuspec.FullName } else { $null }; Nupkg = if ($nupkg) { $nupkg.FullName } else { $null } }
    }
    return [pscustomobject]@{ Nuspec = $null; Nupkg = $null }
}

function Get-License([string] $NuspecPath) {
    if (-not $NuspecPath) { return $null }
    try {
        $xml = [xml](Get-Content -Raw -LiteralPath $NuspecPath)
        $node = $xml.SelectSingleNode("//*[local-name()='license']")
        if ($node -and $node.GetAttribute('type') -eq 'expression') { return $node.InnerText.Trim() }
    } catch { return $null }
    return $null
}

function Add-LicenseEvidence($Record) {
    $cache = Get-PackageCache $Record.id $Record.version
    $license = Get-License $cache.Nuspec
    $Record | Add-Member -NotePropertyName license -NotePropertyValue $license -Force
    $hash = if ($cache.Nupkg) { (Get-FileHash -Algorithm SHA256 -LiteralPath $cache.Nupkg).Hash.ToLowerInvariant() } else { $null }
    $Record | Add-Member -NotePropertyName packageHash -NotePropertyValue $hash -Force
    if (-not $license) {
        Add-Issue 'DEPENDENCY_LICENSE_UNKNOWN' "$($Record.id)@$($Record.version) has no SPDX license expression"
        return
    }
    foreach ($token in ($license -split '\s+(?:OR|AND|WITH)\s+' | ForEach-Object Trim | Where-Object { $_ })) {
        if (($Policy.requiresLegalReview | Where-Object { $_ -ieq $token }) -or ($Policy.forbiddenLicensePatterns | Where-Object { $token -like "*$($_)*" })) {
            Add-Issue 'DEPENDENCY_LICENSE_REVIEW_REQUIRED' "$($Record.id)@$($Record.version) license=$license"
        } elseif (-not ($Policy.allowedLicenses | Where-Object { $_ -ieq $token })) {
            Add-Issue 'DEPENDENCY_LICENSE_UNKNOWN' "$($Record.id)@$($Record.version) license=$license"
        }
    }
}

$Central = Get-CentralVersions
foreach ($pin in @(Get-CentralPins)) {
    $reason = Get-ForbiddenReason $pin.id
    if ($reason) {
        $suffix = if ($pin.version) { "@$($pin.version)" } else { '' }
        Add-Issue 'PACKAGE_FORBIDDEN' "$($pin.id)$suffix 中央钉仍在 Directory.Packages.props —— $reason"
    }
}
$Projects = @(Get-ProjectPaths)
foreach ($projectPath in $Projects) {
    if (-not (Test-Path -LiteralPath $projectPath)) { Add-Issue 'DEPENDENCY_PROJECT_MISSING' $projectPath; continue }
    $relative = (Get-RelativePath $Root $projectPath).Replace('\', '/')
    $xml = [xml](Get-Content -Raw -LiteralPath $projectPath)
    $projectName = Get-XmlProperty $xml 'AssemblyName'
    if (-not $projectName) { $projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath) }
    $references = [System.Collections.Generic.List[object]]::new()
    foreach ($node in $xml.SelectNodes("//*[local-name()='PackageReference']")) {
        $id = $node.GetAttribute('Include'); if (-not $id) { $id = $node.GetAttribute('Update') }
        if (-not $id) { continue }
        $version = $node.GetAttribute('Version'); if (-not $version) { $version = $node.GetAttribute('VersionOverride') }
        $explicit = $node.HasAttribute('Version') -or $node.HasAttribute('VersionOverride')
        $references.Add([pscustomobject]@{ id = $id; version = $version; explicit = $explicit })
        $centralVersion = $Central[$id.ToLowerInvariant()]
        if ($explicit) {
            if (Test-Floating $version) { Add-Issue 'FLOATING_VERSION_FORBIDDEN' "$id Version=`"$version`"" $relative }
            Add-Issue 'EXPLICIT_VERSION_FORBIDDEN' "$id must use Directory.Packages.props" $relative
            if ($centralVersion -and $version -cne $centralVersion) { Add-Issue 'CENTRAL_VERSION_MISMATCH' "$id expected $centralVersion actual $version" $relative }
        } elseif (-not $centralVersion) { Add-Issue 'PACKAGE_VERSION_NOT_CENTRALLY_PINNED' $id $relative }
        $scopes = $Policy.packageScopes.PSObject.Properties[$id]
        if ($scopes -and -not (Test-Scope $projectName $scopes.Value)) { Add-Issue 'PACKAGE_SCOPE_VIOLATION' "$id is not allowed in $projectName" $relative }
        $forbidden = Get-ForbiddenReason $id
        if ($forbidden) { Add-Issue 'PACKAGE_FORBIDDEN' "$id 被 $projectName 直接引用 —— $forbidden" $relative }
    }
    $lockPath = Get-XmlProperty $xml 'NuGetLockFilePath'
    if ($lockPath) { $lockPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($projectPath)) $lockPath)) }
    else { $lockPath = Join-Path ([IO.Path]::GetDirectoryName($projectPath)) 'packages.lock.json' }
    if ($Policy.requireLockFiles -and -not (Test-Path -LiteralPath $lockPath)) { Add-Issue 'DEPENDENCY_LOCK_FILE_MISSING' "${relative}: $(Get-RelativePath $Root $lockPath)" $relative }
    try { $graph = Get-DotnetJson $projectPath @() } catch {
        Add-Issue 'DEPENDENCY_GRAPH_UNAVAILABLE' "${relative}: $($_.Exception.Message)" $relative
        $ProjectRecords.Add([pscustomobject]@{ path = $relative; name = $projectName; packageReferences = @($references.id); packages = @() })
        continue
    }
    $graphPackages = [System.Collections.Generic.List[object]]::new(); Add-GraphPackages $graph $graphPackages
    $unique = @{}; foreach ($record in $graphPackages) { $unique["$($record.id.ToLowerInvariant())@$($record.version)"] = $record }
    foreach ($record in $unique.Values) {
        $scopes = $Policy.packageScopes.PSObject.Properties[$record.id]
        if ($scopes -and -not (Test-Scope $projectName $scopes.Value)) { Add-Issue 'PACKAGE_SCOPE_VIOLATION' "$($record.id) is not allowed in $projectName" $relative }
        $forbidden = Get-ForbiddenReason $record.id
        if ($forbidden) { Add-Issue 'PACKAGE_FORBIDDEN' "$($record.id)@$($record.version) 出现在 $projectName 的解析图(含传递依赖) —— $forbidden" $relative }
    }
    foreach ($record in $unique.Values) {
        $key = "$($record.id.ToLowerInvariant())@$($record.version)"
        if (-not $PackageRecords.ContainsKey($key)) { $PackageRecords[$key] = $record }
    }
    try {
        $audit = Get-DotnetJson $projectPath @('--vulnerable')
        $vulnerabilities = [System.Collections.Generic.List[object]]::new()
        function Find-Vulnerabilities($Node) {
            if ($null -eq $Node -or $Node -is [string] -or $Node.GetType().IsValueType) { return }
            if ($Node.vulnerabilities) { foreach ($item in $Node.vulnerabilities) { $vulnerabilities.Add($item) } }
            if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) { foreach ($item in $Node) { Find-Vulnerabilities $item } }
            else { foreach ($property in $Node.PSObject.Properties) { Find-Vulnerabilities $property.Value } }
        }
        Find-Vulnerabilities $audit
        foreach ($vulnerability in $vulnerabilities) { Add-Issue 'DEPENDENCY_VULNERABILITY' "${relative}: $($vulnerability | ConvertTo-Json -Compress)" $relative }
    } catch { Add-Issue 'DEPENDENCY_VULNERABILITY_AUDIT_UNAVAILABLE' "${relative}: $($_.Exception.Message)" $relative }
    $ProjectRecords.Add([pscustomobject]@{ path = $relative; name = $projectName; packageReferences = @($references.id); packages = @($unique.Values) })
}

foreach ($record in $PackageRecords.Values) { Add-LicenseEvidence $record }
$report = [ordered]@{
    policy = (Get-RelativePath $Root $PolicyPath).Replace('\', '/')
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    projects = @($ProjectRecords)
    packages = @($PackageRecords.Values)
    issues = @($Issues)
    status = if ($Issues.Count) { 'failed' } else { 'ok' }
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding utf8
if ($Issues.Count) {
    foreach ($entry in $Issues) { [Console]::Error.WriteLine("$($entry.code) $($entry.message)$(if ($entry.project) { " project=$($entry.project)" })") }
    [Console]::Error.WriteLine("DEPENDENCY_POLICY_FAILED issues=$($Issues.Count)")
    exit 31
}
Write-Output "DEPENDENCY_POLICY_OK projects=$($Projects.Count)"
