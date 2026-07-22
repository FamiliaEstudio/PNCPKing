$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\win-x64'
$candidates = @()
if ($env:PNCPKING_DOTNET) {
    $candidates += $env:PNCPKING_DOTNET
}
if ($env:ProgramFiles) {
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
}
$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($pathDotnet) {
    $candidates += $pathDotnet.Source
}

$dotnet = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if ((Test-Path $candidate) -and @(& $candidate --list-sdks 2>$null).Count -gt 0) {
        $dotnet = $candidate
        break
    }
}
if (-not $dotnet) {
    throw 'Não foi encontrado um SDK .NET 8. Instale o SDK (o runtime isolado não é suficiente) ou defina PNCPKING_DOTNET.'
}

& $dotnet build (Join-Path $root 'PNCPKing.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'A compilação Release falhou; a publicação foi interrompida.' }

& $dotnet test (Join-Path $root 'tests\PNCPKing.Tests\PNCPKing.Tests.csproj') `
    --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Os testes falharam; a publicação foi interrompida.' }

& $dotnet publish (Join-Path $root 'src\PNCPKing.App\PNCPKing.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'A publicação Windows falhou.' }

Get-ChildItem $output -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
$executables = @(Get-ChildItem (Join-Path $root 'artifacts') -Filter 'PNCPKing.exe' -File -Recurse)
if ($executables.Count -ne 1 -or $executables[0].FullName -ne (Join-Path $output 'PNCPKing.exe')) {
    throw 'A validação exige exatamente um PNCPKing.exe no caminho canônico artifacts\win-x64.'
}

Write-Host "PNCP King publicado em $output"
