param([Parameter(Mandatory = $true)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = $null
$parent = $null
$stagedPath = $null
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
    $stagedPath = $request.stagedPath
    if (-not (Test-Path -LiteralPath $request.targetPath -PathType Leaf)) { throw 'Executavel original ausente.' }
    $backup = $request.targetPath + '.' + $request.operationId + '.bak'
    $staged = Get-Item -LiteralPath $request.stagedPath
    if ($staged.Length -ne $request.size) { throw 'Tamanho do executavel divergente.' }
    $actualVersion = $staged.VersionInfo
    if (("{0}.{1}.{2}" -f $actualVersion.FileMajorPart, $actualVersion.FileMinorPart, $actualVersion.FileBuildPart) -ne $request.version) {
        throw 'Versao do executavel divergente.'
    }
    # Pin the process handle before signaling readiness. Slow shutdown is safe to wait for.
    $parent = [Diagnostics.Process]::GetProcessById($request.processId)
    if ($parent.StartTime.ToUniversalTime().Ticks -ne $request.processStartedUtcTicks) {
        throw 'A instancia que solicitou a atualizacao ja foi encerrada.'
    }
    [void]$parent.Handle
    [IO.File]::WriteAllText($RequestPath + '.ready', 'ready')
    $parent.WaitForExit()
    if (-not (Test-Path -LiteralPath ($RequestPath + '.commit'))) { throw 'A instalacao foi cancelada pelo programa.' }
    if ((Get-Item -LiteralPath $request.stagedPath).Length -ne $request.size -or
        (Get-Sha256Hex $request.stagedPath) -ne $request.sha256) {
        throw 'Executavel incompleto ou SHA-256 divergente antes da substituicao.'
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
    $cause = $_.Exception.GetBaseException()
    if ($replacementStarted -and -not $replacementFinished -and (($cause.HResult -band 65535) -in 32, 33)) {
        $message = 'O Windows manteve o executavel em uso. Feche a outra instancia que o esta usando e tente novamente. ' + $message
    }
    if ($replacementStarted -and -not $replacementFinished -and $backup -and (Test-Path -LiteralPath $backup)) {
        try {
            if (Test-Path -LiteralPath $request.targetPath) { [IO.File]::Replace($backup, $request.targetPath, $null) }
            else { [IO.File]::Move($backup, $request.targetPath) }
        } catch { $message += ' Recuperacao pendente: ' + $_.Exception.Message }
    }
    [IO.File]::WriteAllText($RequestPath + '.result', $message)
    # Before commitment the app displays the result. After commitment it may have exited.
    if (Test-Path -LiteralPath ($RequestPath + '.commit')) {
        Add-Type -AssemblyName PresentationFramework
        [void][System.Windows.MessageBox]::Show(
            "Nao foi possivel concluir a atualizacao.`n`n" + $message + "`n`nDiagnostico: " + $RequestPath + '.result',
            'Atualizacao do PNCP King')
    }
    exit 1
} finally {
    if ($parent) { $parent.Dispose() }
    if ($stagedPath -and (Test-Path -LiteralPath $stagedPath)) {
        Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue
    }
}
