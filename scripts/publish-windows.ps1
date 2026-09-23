param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\win-x64'
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'src\PNCPKing.App\PNCPKing.App.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'A versao deve usar X.Y.Z, sem pre-release.'
}
$candidates = @()
if ($env:PNCPKING_DOTNET) {
    $candidates += $env:PNCPKING_DOTNET
}
if ($env:ProgramFiles) {
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
}
if ($env:LOCALAPPDATA) {
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk-8\dotnet.exe')
    $candidates += (Join-Path $env:LOCALAPPDATA 'PNCPKing\dotnet\dotnet.exe')
}
$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($pathDotnet) {
    $candidates += $pathDotnet.Source
}

$dotnet = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if ((Test-Path $candidate) -and
        @(Get-ChildItem (Join-Path (Split-Path -Parent $candidate) 'sdk') -Directory -ErrorAction SilentlyContinue).Count -gt 0) {
        $dotnet = $candidate
        break
    }
}
if (-not $dotnet) {
    throw 'Não foi encontrado um SDK .NET 8. Instale o SDK (o runtime isolado não é suficiente) ou defina PNCPKING_DOTNET.'
}

function Invoke-Dotnet([string[]]$DotnetArguments) {
    # Wait explicitly, including when the publication script is launched through WSL.
    $quoted = @($DotnetArguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $dotnet -ArgumentList $quoted -NoNewWindow -PassThru
    # Start-Process -Wait also waits for persistent MSBuild child servers. Wait for the actual command instead.
    $null = $process.Handle
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "dotnet falhou com codigo $($process.ExitCode); publicacao interrompida." }
}

if (Get-Process -Name 'PNCPKing' -ErrorAction SilentlyContinue) {
    throw 'O PNCP King está aberto. Feche o aplicativo antes de publicar o executável canônico.'
}

Invoke-Dotnet @('build', (Join-Path $root 'PNCPKing.sln'), '--configuration', 'Release', "-p:Version=$Version")

Invoke-Dotnet @('test', (Join-Path $root 'tests\PNCPKing.Tests\PNCPKing.Tests.csproj'), '--configuration', 'Release', '--no-build',
    '--', 'xUnit.ParallelizeTestCollections=false')

Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests\PNCPKing.UiChecks\PNCPKing.UiChecks.csproj'),
    '--configuration', 'Release', '--', '--layout')

if (Test-Path $output) {
    $existing = @(Get-ChildItem $output -Force)
    if (@($existing | Where-Object { $_.PSIsContainer -or
        ($_.Name -notin @('PNCPKing.exe', 'app-update.json') -and $_.Extension -ne '.pdb') }).Count -gt 0) {
        throw 'A pasta canonica contem arquivos adicionais. Publicacao interrompida para preservar seu conteudo.'
    }
    if (Get-Process -Name 'PNCPKing' -ErrorAction SilentlyContinue) {
        throw 'O PNCP King esta aberto. Feche o aplicativo antes de substituir o executavel canonico.'
    }
    $existing | Remove-Item -Force
}

Invoke-Dotnet @('publish', (Join-Path $root 'src\PNCPKing.App\PNCPKing.App.csproj'), '--configuration', 'Release',
    '--runtime', 'win-x64', '--self-contained', 'true', '--output', $output, '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false')

Get-ChildItem $output -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
$executables = @(Get-ChildItem (Join-Path $root 'artifacts') -Filter '*.exe' -File -Recurse)
if ($executables.Count -ne 1 -or
    $executables[0].FullName -ne (Join-Path $output 'PNCPKing.exe')) {
    throw 'A distribuição canônica exige exatamente um executável: artifacts\win-x64\PNCPKing.exe.'
}

$schemaSource = Get-Content (Join-Path $root 'src\PNCPKing.Infrastructure\Data\SqliteContractRepository.cs') -Raw
if ($schemaSource -notmatch 'CurrentSchemaVersion\s*=\s*(\d+)') { throw 'Versao do esquema nao encontrada.' }
$schema = [int]$Matches[1]
$exe = Get-Item (Join-Path $output 'PNCPKing.exe')
$actualVersion = '{0}.{1}.{2}' -f $exe.VersionInfo.FileMajorPart, $exe.VersionInfo.FileMinorPart, $exe.VersionInfo.FileBuildPart
if ($actualVersion -ne $Version) { throw 'A versao publicada diverge da versao solicitada.' }
$manifest = [ordered]@{ format = 1; version = $Version; platform = 'win-x64'; schema = $schema;
    file = [ordered]@{ name = 'PNCPKing.exe'; size = $exe.Length;
        sha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }
[IO.File]::WriteAllText((Join-Path $output 'app-update.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
Write-Host "PNCP King publicado em $output\PNCPKing.exe"
