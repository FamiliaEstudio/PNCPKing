param([string]$Version, [string]$NotesPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'src\PNCPKing.App\PNCPKing.App.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'A versao deve usar X.Y.Z.'
}
if (-not $NotesPath) { $NotesPath = Join-Path $root "docs\releases\v$Version.md" }
if (-not (Test-Path -LiteralPath $NotesPath -PathType Leaf)) {
    throw "Falta o resumo da release: $NotesPath"
}
$notes = Get-Content -LiteralPath $NotesPath -Raw -Encoding UTF8
$summaryLines = @($notes -split '\r?\n' | Where-Object {
    $_ -match '^\s*-\s+\p{L}' -and $_ -notmatch '^\s*-\s*(Full Changelog|https?://)'
})
if ([string]::IsNullOrWhiteSpace($notes) -or $summaryLines.Count -eq 0) {
    throw 'A release exige um resumo em topicos; notas vazias ou somente Full Changelog nao sao aceitas.'
}
return $notes.Trim()
