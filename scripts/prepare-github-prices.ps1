param(
    [Parameter(Mandatory = $true)][string]$BasePackage,
    [string]$CumulativePackage,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$MinimumAppVersion = '1.1.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($MinimumAppVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'MinimumAppVersion deve usar X.Y.Z.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)

function Read-ExportPackage([string]$Path) {
    $source = (Get-Item -LiteralPath $Path).FullName
    $zip = [IO.Compression.ZipFile]::OpenRead($source)
    try {
        if ($zip.Entries.Count -ne 2 -or @($zip.Entries | Where-Object FullName -eq 'manifest.json').Count -ne 1 -or
            @($zip.Entries | Where-Object FullName -eq 'updates.db').Count -ne 1) { throw 'Conteudo .pncpupdate invalido.' }
        $entry = $zip.GetEntry('manifest.json')
        if ($entry.Length -gt 65536) { throw 'Manifesto do pacote muito grande.' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.Format -ne 1 -or $metadata.Schema -lt 1 -or $metadata.Revision -lt 0 -or $metadata.Units -lt 0 -or
            $metadata.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $metadata.InitialBase -isnot [bool]) { throw 'Manifesto do pacote invalido.' }
        foreach ($identity in @($metadata.BaseId, $metadata.Origin, $metadata.PackageId)) { [void][Guid]::ParseExact($identity, 'N') }
        $data = $zip.GetEntry('updates.db')
        if ($data.Length -le 0) { throw 'Payload SQLite ausente.' }
        $stream = $data.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ne $metadata.Sha256) { throw 'SHA-256 interno divergente; pacote nao preparado.' }
        return [pscustomobject]@{ Source = $source; Manifest = $metadata; ExpandedSize = $data.Length }
    } finally { $zip.Dispose() }
}

function Write-PackageAssets($Package) {
    $size = (Get-Item -LiteralPath $Package.Source).Length
    $hash = (Get-FileHash -LiteralPath $Package.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $prefix = 'precos-' + $Package.Manifest.PackageId
    $parts = @()
    if ($size -lt 2GB) {
        $name = $prefix + '.pncpupdate'
        $target = Join-Path $output $name
        [IO.File]::Copy($Package.Source, ($target + '.partial'), $true)
        Move-Item -LiteralPath ($target + '.partial') -Destination $target -Force
        $parts += [ordered]@{ name = $name; size = $size; sha256 = $hash }
    } else {
        $inputStream = [IO.File]::OpenRead($Package.Source)
        try {
            $buffer = New-Object byte[] (1MB)
            $index = 0
            while ($inputStream.Position -lt $inputStream.Length) {
                $index++
                if ($index -gt 998) { throw 'O pacote excede o numero de anexos permitido por release.' }
                $name = $prefix + ('.pncpupdate.part{0:D4}' -f $index)
                $target = Join-Path $output $name
                $partStream = [IO.File]::Create($target + '.partial')
                try {
                    [long]$written = 0
                    while ($written -lt 1GB) {
                        $read = $inputStream.Read($buffer, 0, [int][Math]::Min($buffer.Length, (1GB - $written)))
                        if ($read -eq 0) { break }
                        $partStream.Write($buffer, 0, $read)
                        $written += $read
                    }
                } finally { $partStream.Dispose() }
                Move-Item -LiteralPath ($target + '.partial') -Destination $target -Force
                $parts += [ordered]@{ name = $name; size = $written;
                    sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
        } finally { $inputStream.Dispose() }
    }
    # Serialize the existing package identity, including the checksum of updates.db, separately from the transport checksum.
    $m = $Package.Manifest
    return [ordered]@{
        manifest = [ordered]@{ format = $m.Format; schema = $m.Schema; baseId = $m.BaseId; origin = $m.Origin;
            revision = $m.Revision; initialBase = $m.InitialBase; packageId = $m.PackageId; sha256 = $m.Sha256; units = $m.Units }
        expandedSize = $Package.ExpandedSize
        download = [ordered]@{ size = $size; sha256 = $hash; parts = @($parts) }
    }
}

$base = Read-ExportPackage $BasePackage
if (-not $base.Manifest.InitialBase) { throw 'BasePackage precisa ser a exportacao inicial completa.' }
$delta = $null
if ($CumulativePackage) {
    $delta = Read-ExportPackage $CumulativePackage
    if ($delta.Manifest.InitialBase -or $delta.Manifest.BaseId -ne $base.Manifest.BaseId -or
        $delta.Manifest.Origin -ne $base.Manifest.Origin -or $delta.Manifest.Schema -ne $base.Manifest.Schema -or
        $delta.Manifest.Revision -lt $base.Manifest.Revision -or $delta.Manifest.PackageId -eq $base.Manifest.PackageId) {
        throw 'O cumulativo nao pertence a esta base/origem/esquema.'
    }
}
$baseAssets = Write-PackageAssets $base
$deltaAssets = if ($delta) { Write-PackageAssets $delta } else { $null }
$deltaPartCount = if ($deltaAssets) { @($deltaAssets.download.parts).Count } else { 0 }
if (@($baseAssets.download.parts).Count + $deltaPartCount + 1 -gt 1000) {
    throw 'Os pacotes excedem o numero de anexos permitido por release.'
}
$manifest = [ordered]@{ format = 1; publishedAt = [DateTimeOffset]::UtcNow.ToString('o');
    minimumAppVersion = $MinimumAppVersion; schema = $base.Manifest.Schema; base = $baseAssets; cumulative = $deltaAssets }
$manifestPath = Join-Path $output 'prices-update.json'
[IO.File]::WriteAllText(($manifestPath + '.partial'), ($manifest | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath ($manifestPath + '.partial') -Destination $manifestPath -Force
Write-Host "Precos preparados em $output. Envie e confira todos os anexos antes de substituir prices-update.json na release precos (Latest=false)."
