param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version' }

& (Join-Path $projectRoot 'build.ps1') -OutputName 'WhaleCompanion.release.exe'
$releaseExe = Join-Path $projectRoot 'WhaleCompanion.release.exe'
$test = Start-Process -FilePath $releaseExe -ArgumentList '--self-test' -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -Wait
$testResult = (Get-Content -LiteralPath (Join-Path $projectRoot 'selftest-result.txt') -Raw).Trim()
if ($test.ExitCode -ne 0 -or $testResult -notmatch '^PASS \d+ checks$') { throw ('Self-test failed: ' + $testResult) }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$outputDir = Join-Path $projectRoot 'dist'
[IO.Directory]::CreateDirectory($outputDir) | Out-Null

function Write-ReleaseArchive {
    param([string]$Destination, [string]$Prefix, [System.Collections.IDictionary]$Entries)
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in ($Entries.Keys | Sort-Object)) {
                $entryName = $Prefix + '/' + $name.Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $Entries[$name], $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

# Explicit allowlists protect local preferences, diagnostics, and conversation data.
$portable = [ordered]@{}
$portable['WhaleCompanion.exe'] = $releaseExe
foreach ($name in @('WhaleCompanion.exe.config', '启动鲸鱼娘.cmd', '使用说明.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'VERSION', 'whale.ico')) {
    $portable[$name] = Join-Path $projectRoot $name
}
$assetFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets') -File -Filter '*.png')
if ($assetFiles.Count -ne 92) { throw 'Expected the 92 licensed character assets' }
foreach ($file in $assetFiles) { $portable['assets/' + $file.Name] = $file.FullName }
foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File -Filter '*.txt')) { $portable['licenses/' + $file.Name] = $file.FullName }

$source = [ordered]@{}
foreach ($name in @('.gitignore', '.gitattributes', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'VERSION', '使用说明.md', '启动鲸鱼娘.cmd', 'build.ps1', 'release.ps1', 'app.manifest', 'WhaleCompanion.exe.config', 'whale.ico')) {
    $source[$name] = Join-Path $projectRoot $name
}
foreach ($name in $portable.Keys) {
    if ($name.StartsWith('assets/') -or $name.StartsWith('licenses/')) { $source[$name] = $portable[$name] }
}
foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -File -Filter '*.cs')) { $source['src/' + $file.Name] = $file.FullName }
foreach ($name in @('demo.png', 'demo.gif', 'mini.png')) { $source['docs/images/' + $name] = Join-Path $projectRoot ('docs/images/' + $name) }

foreach ($file in @($portable.Values) + @($source.Values)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Release input is missing: ' + $file) }
}
$portablePath = Join-Path $outputDir ('WhaleCompanion-v' + $Version + '-windows-x64.zip')
$sourcePath = Join-Path $outputDir ('codex-whale-companion-v' + $Version + '-source.zip')
Write-ReleaseArchive -Destination $portablePath -Prefix 'WhaleCompanion' -Entries $portable
Write-ReleaseArchive -Destination $sourcePath -Prefix 'codex-whale-companion' -Entries $source

# Validate archive membership rather than trusting ignore patterns.
foreach ($path in @($portablePath, $sourcePath)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(?i)(settings\.json|errors\.log|probe-result|selftest-result|ui-check-result|运行状态|\.jsonl|\.codex/|\.git/|\.env(?:\.|$))') {
                throw ('Private or runtime file found in archive: ' + $entry.FullName)
            }
        }
    } finally { $archive.Dispose() }
}
$hashes = foreach ($path in @($portablePath, $sourcePath)) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($path)
}
[IO.File]::WriteAllLines((Join-Path $outputDir 'SHA256SUMS.txt'), [string[]]$hashes, (New-Object Text.UTF8Encoding($false)))
Write-Output $testResult
Get-Item -LiteralPath $portablePath, $sourcePath | Select-Object Name, Length
Write-Output 'Archive allowlist validation passed; checksums saved in dist/SHA256SUMS.txt'
