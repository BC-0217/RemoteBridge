[CmdletBinding()]
param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ProjectRoot)
$project = Join-Path $root 'src/PeerDesk.Windows'
$versionPath = Join-Path $project 'Version.props'
if (!(Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw 'Version.props is missing.' }
# Serialize parallel product builds. No user configuration or credentials are read.
$lockDir = Join-Path $root 'artifacts/version'
[IO.Directory]::CreateDirectory($lockDir) | Out-Null
$lockPath = Join-Path $lockDir 'version.lock'
$watch = [Diagnostics.Stopwatch]::StartNew()
$lock = $null
while ($null -eq $lock) {
    try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] {
        if ($watch.ElapsedMilliseconds -ge 5000) { throw 'Version update lock timed out.' }
        Start-Sleep -Milliseconds 25
    }
}
try {
    [xml]$xml = [IO.File]::ReadAllText($versionPath)
    $group = $xml.Project.PropertyGroup
    $version = [string]$group.Version
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-preview\.(\d+))?$') { throw 'Unsupported version format.' }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    $preview = if ($Matches[4]) { [int]$Matches[4] } else { -1 }
    # Only shipped product sources/resources/configuration; never local settings,
    # tests, logs, build outputs, private material or git state.
    $paths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($dir in @($project, (Join-Path $root 'src/PeerDesk.ReleaseSecurity'))) {
        if (Test-Path -LiteralPath $dir) {
            foreach ($f in Get-ChildItem -LiteralPath $dir -File -Recurse) {
                $rel = $f.FullName.Substring($root.Length).Replace('\','/')
                if ($rel -match '/(bin|obj)/' -or $f.FullName -eq $versionPath) { continue }
                if ($f.Extension -in @('.cs','.csproj','.props','.targets','.json','.resx')) { $paths.Add($f.FullName) }
            }
        }
    }
    $paths.Add($PSCommandPath)
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = New-Object IO.MemoryStream
    try {
        foreach ($path in ($paths | Sort-Object -CaseSensitive)) {
            $rel = if ($path -eq $PSCommandPath) { '/tools/Update-Version.ps1' } else { $path.Substring($root.Length).Replace('\','/') }
            $name = [Text.Encoding]::UTF8.GetBytes($rel + [char]0)
            $stream.Write($name, 0, $name.Length)
            $data = [IO.File]::ReadAllBytes($path)
            $length = [BitConverter]::GetBytes([long]$data.Length)
            $stream.Write($length, 0, $length.Length)
            $stream.Write($data, 0, $data.Length)
        }
        $stream.Position = 0
        $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()
    } finally { $stream.Dispose(); $sha.Dispose() }
    $sourceChanged = [string]$group.SourceFingerprint -ne $fingerprint
    $revision = if ($preview -ge 0) { $preview } else { 0 }
    $metadataMatches = [string]$group.FileVersion -eq "$major.$minor.$patch.$revision" -and [string]$group.AssemblyVersion -eq "$major.$minor.0.0" -and [string]$group.InformationalVersion -eq $version
    if (!$sourceChanged -and $metadataMatches) {
        Write-Output ('VERSION_UNCHANGED ' + $version)
        exit 0
    }
    if ($CheckOnly) { Write-Output ('VERSION_SOURCE_CHANGED ' + $version); exit 3 }
    if ($sourceChanged -and ![string]::IsNullOrEmpty([string]$group.SourceFingerprint)) {
        if ($preview -ge 0) { $preview++; $version = "$major.$minor.$patch-preview.$preview" }
        else { $patch++; $version = "$major.$minor.$patch" }
    }
    $revision = if ($preview -ge 0) { $preview } else { 0 }
    if ($major -gt 65534 -or $minor -gt 65534 -or $patch -gt 65534 -or $revision -gt 65534) { throw 'Version component exceeds the Windows file-version limit.' }
    $group.Version = $version
    $group.AssemblyVersion = "$major.$minor.0.0"
    $group.FileVersion = "$major.$minor.$patch.$revision"
    $group.InformationalVersion = $version
    $group.SourceFingerprint = $fingerprint
    $temp = $versionPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true
    $settings.NewLineChars = [Environment]::NewLine
    $writer = [Xml.XmlWriter]::Create($temp,$settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
    [IO.File]::Replace($temp,$versionPath,(Join-Path $lockDir 'Version.props.previous'))
    Write-Output ('VERSION_UPDATED ' + $version)
} finally { if ($null -ne $lock) { $lock.Dispose() } }
