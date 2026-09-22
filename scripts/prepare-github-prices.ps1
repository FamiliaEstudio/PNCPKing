param(
    [Parameter(Mandatory = $true)][string]$UpdatePackage,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$MinimumAppVersion = '1.2.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

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
    if (($end - $start).Days -ne 9) { throw 'A janela precisa conter hoje e os nove dias anteriores.' }
    if (-not $metadata.GeneratedAt -or -not $metadata.IntegrityValidatedAt -or
        [DateTimeOffset]$metadata.IntegrityValidatedAt -lt [DateTimeOffset]$metadata.GeneratedAt) {
        throw 'O exportador nao registrou a validacao de integridade.'
    }

    $chunks = @($metadata.Chunks)
    $daily = @($chunks | Where-Object Kind -eq 'publication-day')
    $late = @($chunks | Where-Object Kind -eq 'late-changes')
    if ($daily.Count -ne 10 -or $late.Count -gt 1 -or $chunks.Count -lt 10 -or $chunks.Count -gt 11) {
        throw 'O pacote nao possui os dez blocos diarios esperados.'
    }
    $expectedDates = @{}
    for ($i = 0; $i -lt 10; $i++) { $expectedDates[$start.AddDays($i).ToString('yyyy-MM-dd')] = $true }
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
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$file = [ordered]@{ name = $name; size = $sourceSize; sha256 = $hash }
$package = [ordered]@{
    manifest = $metadata
    expandedSize = $expandedSize
    download = [ordered]@{ size = $sourceSize; sha256 = $hash; parts = @($file) }
}
$manifest = [ordered]@{
    format = 2
    publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    minimumAppVersion = $MinimumAppVersion
    schema = 29
    update = $package
}
$manifestPath = Join-Path $output 'prices-update.json'
[IO.File]::WriteAllText(($manifestPath + '.partial'), ($manifest | ConvertTo-Json -Depth 16), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath ($manifestPath + '.partial') -Destination $manifestPath -Force
Write-Host "Pacote movel v2 preparado em $output. Publique o .pncpupdate e prices-update.json na release precos."
