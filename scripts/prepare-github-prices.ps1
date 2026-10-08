param(
    [Parameter(Mandatory = $true)][string]$UpdatePackage,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$MinimumAppVersion = '1.2.24'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256Hex([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Write-PriceManifest([string]$PackagePath, $Metadata, [string]$ManifestName, [string]$MinimumVersion) {
    $size = (Get-Item -LiteralPath $PackagePath).Length
    if ($size -le 0 -or $size -ge 2GB) { throw 'O pacote publicado precisa ser menor que 2 GiB.' }
    $hash = Get-Sha256Hex $PackagePath
    $file = [ordered]@{ name = [IO.Path]::GetFileName($PackagePath); size = $size; sha256 = $hash }
    [long]$expanded = 0
    foreach ($chunk in $Metadata.Chunks) { $expanded += [long]$chunk.ExpandedSize }
    $manifest = [ordered]@{
        format = 2
        publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
        minimumAppVersion = $MinimumVersion
        schema = 29
        update = [ordered]@{
            manifest = $Metadata
            expandedSize = $expanded
            download = [ordered]@{ size = $size; sha256 = $hash; parts = @($file) }
        }
    }
    $path = Join-Path $output $ManifestName
    [IO.File]::WriteAllText(($path + '.partial'), ($manifest | ConvertTo-Json -Depth 16), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath ($path + '.partial') -Destination $path -Force
}

if ($MinimumAppVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'MinimumAppVersion deve usar X.Y.Z.'
}
if ([Version]$MinimumAppVersion -lt [Version]'1.2.0') {
    throw 'Pacotes v2 exigem MinimumAppVersion 1.2.0 ou posterior.'
}

$source = (Get-Item -LiteralPath $UpdatePackage).FullName
$sourceSize = (Get-Item -LiteralPath $source).Length
if ($sourceSize -le 0 -or $sourceSize -ge 2GB) {
    throw 'O .pncpupdate precisa ser menor que 2 GiB.'
}

$zip = [IO.Compression.ZipFile]::OpenRead($source)
try {
    $manifestEntries = @($zip.Entries | Where-Object FullName -eq 'manifest.json')
    if ($manifestEntries.Count -ne 1 -or $manifestEntries[0].Length -le 0 -or $manifestEntries[0].Length -gt 1MB) {
        throw 'Manifesto interno ausente ou invalido.'
    }
    $reader = New-Object IO.StreamReader($manifestEntries[0].Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }

    if ($metadata.Format -ne 2 -or $metadata.Schema -ne 29) { throw 'Somente .pncpupdate v2 com esquema 29 pode ser publicado.' }
    [void][Guid]::ParseExact($metadata.PackageId, 'N')
    $start = [DateTime]::ParseExact([string]$metadata.StartDate, 'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
    $end = [DateTime]::ParseExact([string]$metadata.EndDate, 'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None)
    $windowDays = ($end - $start).Days + 1
    if ($windowDays -notin @(10, 20)) { throw 'A janela precisa conter exatamente 10 ou 20 dias.' }
    if ($windowDays -eq 20 -and [Version]$MinimumAppVersion -lt [Version]'1.2.24') {
        throw 'Pacotes de 20 dias exigem MinimumAppVersion 1.2.24 ou posterior.'
    }
    if (-not $metadata.GeneratedAt -or -not $metadata.IntegrityValidatedAt -or
        [DateTimeOffset]$metadata.IntegrityValidatedAt -lt [DateTimeOffset]$metadata.GeneratedAt) {
        throw 'O exportador nao registrou a validacao de integridade.'
    }

    $chunks = @($metadata.Chunks)
    $daily = @($chunks | Where-Object Kind -eq 'publication-day')
    $late = @($chunks | Where-Object Kind -eq 'late-changes')
    if ($daily.Count -ne $windowDays -or $late.Count -gt 1 -or
        $chunks.Count -ne ($daily.Count + $late.Count)) {
        throw 'O pacote nao possui os blocos diarios esperados.'
    }
    $expectedDates = @{}
    for ($i = 0; $i -lt $windowDays; $i++) { $expectedDates[$start.AddDays($i).ToString('yyyy-MM-dd')] = $true }
    foreach ($chunk in $daily) {
        if (-not $expectedDates.ContainsKey([string]$chunk.Date)) { throw 'Data diaria duplicada ou fora da janela.' }
        $expectedDates.Remove([string]$chunk.Date)
    }
    if ($expectedDates.Count -ne 0) { throw 'A janela diaria esta incompleta.' }

    $entryNames = @{}
    [long]$expandedSize = 0
    foreach ($chunk in $chunks) {
        if (-not $chunk.Key -or -not $chunk.Entry -or $entryNames.ContainsKey([string]$chunk.Entry) -or
            $chunk.ExpandedSize -le 0 -or $chunk.ExpandedSize -ge 2GB -or
            $chunk.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $chunk.ContentDigest -notmatch '^[a-fA-F0-9]{64}$') {
            throw 'Descritor de bloco invalido.'
        }
        foreach ($count in @($chunk.Contracts, $chunk.ItemSnapshots, $chunk.Items, $chunk.ResultSnapshots,
                $chunk.Results, $chunk.CoverageCells)) {
            if ([long]$count -lt 0) { throw 'Contagem de bloco invalida.' }
        }
        $entryNames[[string]$chunk.Entry] = $true
        $entry = $zip.GetEntry([string]$chunk.Entry)
        if ($null -eq $entry -or $entry.Length -ne [long]$chunk.ExpandedSize) { throw "Bloco ausente ou truncado: $($chunk.Key)." }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ne ([string]$chunk.Sha256).ToLowerInvariant()) { throw "SHA-256 interno divergente: $($chunk.Key)." }
        $expandedSize += [long]$chunk.ExpandedSize
    }
    if ($zip.Entries.Count -ne $chunks.Count + 1) { throw 'O pacote contem entradas nao declaradas.' }
} finally {
    $zip.Dispose()
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$name = 'precos-' + $metadata.PackageId + '.pncpupdate'
$target = Join-Path $output $name
[IO.File]::Copy($source, ($target + '.partial'), $true)
Move-Item -LiteralPath ($target + '.partial') -Destination $target -Force
if ($windowDays -eq 20) {
    Write-PriceManifest $target $metadata 'prices-update-v2.json' $MinimumAppVersion

    # Repackage validated, unchanged chunks; never open or revalidate a SQLite database.
    # The public legacy manifest must describe a real ten-day archive, including its own identity/hash.
    $legacy = $metadata | ConvertTo-Json -Depth 16 | ConvertFrom-Json
    $legacy.StartDate = $end.AddDays(-9).ToString('yyyy-MM-dd')
    $legacy.PackageId = [Guid]::NewGuid().ToString('N')
    $legacy.Chunks = @($chunks | Where-Object {
        $_.Kind -eq 'late-changes' -or
        ($_.Kind -eq 'publication-day' -and [string]$_.Date -ge [string]$legacy.StartDate)
    })
    $legacyTarget = Join-Path $output ('precos-' + $legacy.PackageId + '.pncpupdate')
    $partial = $legacyTarget + '.partial'
    try {
        $inputZip = [IO.Compression.ZipFile]::OpenRead($target)
        try {
            $outputZip = [IO.Compression.ZipFile]::Open($partial, [IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($chunk in $legacy.Chunks) {
                    $entry = $outputZip.CreateEntry([string]$chunk.Entry, [IO.Compression.CompressionLevel]::Optimal)
                    $inputStream = $inputZip.GetEntry([string]$chunk.Entry).Open()
                    try {
                        $outputStream = $entry.Open()
                        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                    } finally { $inputStream.Dispose() }
                }
                $entry = $outputZip.CreateEntry('manifest.json')
                $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write(($legacy | ConvertTo-Json -Depth 16)) } finally { $writer.Dispose() }
            } finally { $outputZip.Dispose() }
        } finally { $inputZip.Dispose() }
        Move-Item -LiteralPath $partial -Destination $legacyTarget -Force
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    Write-PriceManifest $legacyTarget $legacy 'prices-update.json' '1.2.0'
} else {
    Write-PriceManifest $target $metadata 'prices-update.json' $MinimumAppVersion
    # Also refresh the current manifest so republishing a ten-day source cannot leave it stale.
    Write-PriceManifest $target $metadata 'prices-update-v2.json' $MinimumAppVersion
}
Write-Host "Pacotes preparados em $output. Publique os .pncpupdate e os dois prices-update*.json na release precos."
