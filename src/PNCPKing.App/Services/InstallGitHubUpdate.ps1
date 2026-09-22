param([Parameter(Mandatory = $true)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = $null
$replacementStarted = $false
$replacementFinished = $false
$backup = $null

function Get-Sha256Hex([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToUpperInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}
try {
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    if ($request.operationId -notmatch '^[a-f0-9]{32}$') { throw 'Identidade da atualizacao invalida.' }
    if ([IO.Path]::GetFileName($request.targetPath) -ne 'PNCPKing.exe' -or
        $request.stagedPath -ne ($request.targetPath + '.' + $request.operationId + '.new')) {
        throw 'Destino da atualizacao invalido.'
    }
    if (-not (Test-Path -LiteralPath $request.targetPath -PathType Leaf)) { throw 'Executavel original ausente.' }
    $backup = $request.targetPath + '.' + $request.operationId + '.bak'
    $staged = Get-Item -LiteralPath $request.stagedPath
    if ($staged.Length -ne $request.size -or (Get-Sha256Hex $staged.FullName) -ne $request.sha256) {
        throw 'Executavel incompleto ou SHA-256 divergente.'
    }
    $actualVersion = $staged.VersionInfo
    if (("{0}.{1}.{2}" -f $actualVersion.FileMajorPart, $actualVersion.FileMinorPart, $actualVersion.FileBuildPart) -ne $request.version) {
        throw 'Versao do executavel divergente.'
    }
    [IO.File]::WriteAllText($RequestPath + '.ready', 'ready')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $parent = Get-Process -Id $request.processId -ErrorAction SilentlyContinue
        $parentAlive = $null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $request.processStartedUtcTicks
        if (-not $parentAlive) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($parentAlive) { throw 'O PNCP King ainda esta aberto. Feche o processo e tente atualizar novamente.' }
    if (-not (Test-Path -LiteralPath ($RequestPath + '.commit'))) { throw 'A instalacao foi cancelada pelo programa.' }
    if ((Get-Item -LiteralPath $request.stagedPath).Length -ne $request.size -or
        (Get-Sha256Hex $request.stagedPath) -ne $request.sha256) {
        throw 'O executavel mudou durante o encerramento; instalacao interrompida.'
    }
    # Only replacement failures can restore this backup. Once the new process is launched it may migrate the database.
    $replacementStarted = $true
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        try {
            [IO.File]::Replace($request.stagedPath, $request.targetPath, $backup)
            $replacementFinished = $true
            break
        } catch [IO.IOException] {
            if ($attempt -eq 14) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    [IO.File]::WriteAllText($RequestPath + '.result', 'installed')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $request.targetPath
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($request.targetPath)
    $start.Arguments = '--complete-github-update ' + $request.operationId
    $start.UseShellExecute = $true
    [void][Diagnostics.Process]::Start($start)
    # File replacement was successful; no automatic rollback after launching the new version.
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
} catch {
    $message = $_.Exception.Message
    if ($replacementStarted -and -not $replacementFinished -and $backup -and (Test-Path -LiteralPath $backup)) {
        try {
            if (Test-Path -LiteralPath $request.targetPath) { [IO.File]::Replace($backup, $request.targetPath, $null) }
            else { [IO.File]::Move($backup, $request.targetPath) }
        } catch { $message += ' Recuperacao pendente: ' + $_.Exception.Message }
    }
    [IO.File]::WriteAllText($RequestPath + '.result', $message)
    # Persist the explanation as well as showing it after the parent window has closed.
    Add-Type -AssemblyName PresentationFramework
    [void][System.Windows.MessageBox]::Show(
        "Nao foi possivel concluir a atualizacao. Feche processos que bloqueiem o executavel e tente novamente.`n`n" + $message,
        'Atualizacao do PNCP King')
    exit 1
} finally {
    if ($request -and (Test-Path -LiteralPath $request.stagedPath)) {
        Remove-Item -LiteralPath $request.stagedPath -Force -ErrorAction SilentlyContinue
    }
}
