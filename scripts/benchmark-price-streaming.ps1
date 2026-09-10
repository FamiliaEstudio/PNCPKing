param(
    [Parameter(Mandatory = $true)]
    [string]$DatabasePath,

    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [ValidateRange(1, 20)]
    [int]$Rounds = 6,

    [string]$Configuration = "Release",

    [string]$BaselinePath,

    [string]$ComparisonReportPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
    throw "A cópia de benchmark não existe: $DatabasePath"
}

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-sdk-8\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "SDK .NET local não encontrado em $dotnet"
}

$env:PNCPKING_PERFORMANCE_DATABASE_COPY = (Resolve-Path -LiteralPath $DatabasePath).Path
$env:PNCPKING_PERFORMANCE_REPORT = [IO.Path]::GetFullPath($ReportPath)
$env:PNCPKING_PERFORMANCE_ROUNDS = $Rounds.ToString([Globalization.CultureInfo]::InvariantCulture)
if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $env:PNCPKING_PERFORMANCE_BASELINE = [IO.Path]::GetFullPath($BaselinePath)
}
if (-not [string]::IsNullOrWhiteSpace($ComparisonReportPath)) {
    $env:PNCPKING_PERFORMANCE_COMPARISON_REPORT = [IO.Path]::GetFullPath($ComparisonReportPath)
}

$project = Join-Path $PSScriptRoot "..\tests\PNCPKing.Tests\PNCPKing.Tests.csproj"
$arguments = @(
    "test",
    $project,
    "-c", $Configuration,
    "--no-restore",
    "--filter", "FullyQualifiedName~RealDatabaseCopy_BenchmarksPriceStreamingScenario",
    "--logger", "console;verbosity=normal"
)
$process = Start-Process -FilePath $dotnet -ArgumentList $arguments -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "O benchmark terminou com código $($process.ExitCode)."
}

Write-Host "Relatórios: $($env:PNCPKING_PERFORMANCE_REPORT) e $([IO.Path]::ChangeExtension($env:PNCPKING_PERFORMANCE_REPORT, '.txt'))"
