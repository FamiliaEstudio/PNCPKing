using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Services;

public sealed record DownloadedPriceUpdate(string Path, PriceUpdatePackage Package);
public sealed record PendingGitHubUpdate(string DatabasePath, string DatabaseOrigin, string? PreviousBaseId,
    string AppVersion, bool ReplaceBase, IReadOnlyList<DownloadedPriceUpdate> Packages);
public sealed record AppInstallRequest(int ProcessId, long ProcessStartedUtcTicks, string TargetPath,
    string StagedPath, long Size, string Sha256, string Version, string OperationId);

public static class GitHubAppInstaller
{
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PNCP King", "updates");

    public static string OperationPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identidade de atualização inválida.");
        return Path.Combine(CacheDirectory, id + ".json");
    }

    public static async Task ValidateBinaryAsync(string path, AppUpdateManifest manifest, CancellationToken ct)
    {
        if (!await GitHubUpdateService.VerifyAsync(path, manifest.File.Size, manifest.File.Sha256, ct).ConfigureAwait(false))
            throw new InvalidDataException("Executável incompleto ou SHA-256 divergente.");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart != GitHubUpdateValidation.ParseVersion(manifest.Version).Major ||
            info.FileMinorPart != GitHubUpdateValidation.ParseVersion(manifest.Version).Minor ||
            info.FileBuildPart != GitHubUpdateValidation.ParseVersion(manifest.Version).Build)
            throw new InvalidDataException("A versão do executável diverge da release.");
        // The native host must be PE32+ for AMD64; a DLL or other architecture cannot replace the app.
        using var input = new BinaryReader(File.OpenRead(path));
        if (input.ReadUInt16() != 0x5a4d || input.BaseStream.Length < 64) throw new InvalidDataException("Executável Windows inválido.");
        input.BaseStream.Position = 0x3c;
        var header = input.ReadInt32();
        if (header < 64 || header > input.BaseStream.Length - 26) throw new InvalidDataException("Cabeçalho Windows inválido.");
        input.BaseStream.Position = header;
        if (input.ReadUInt32() != 0x00004550 || input.ReadUInt16() != 0x8664)
            throw new InvalidDataException("O executável precisa ser Windows x64.");
        input.BaseStream.Position = header + 22;
        if ((input.ReadUInt16() & 0x2000) != 0 || input.ReadUInt16() != 0x20b)
            throw new InvalidDataException("O arquivo não é um aplicativo Windows x64.");
    }

    public static void CheckDestination(string? executable = null)
    {
        executable ??= Environment.ProcessPath ?? throw new IOException("Caminho do executável indisponível.");
        if (!string.Equals(Path.GetFileName(executable), "PNCPKing.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A instalação automática exige abrir o PNCPKing.exe publicado.");
        if ((File.GetAttributes(executable) & FileAttributes.ReadOnly) != 0)
            throw new IOException("O executável está marcado como somente leitura; ajuste a permissão antes de atualizar.");
        var probe = executable + "." + Guid.NewGuid().ToString("N") + ".write-test";
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (UnauthorizedAccessException e) { throw new IOException("Sem permissão para atualizar esta pasta do programa.", e); }
        finally { File.Delete(probe); }
    }

    public static async Task PrepareAsync(string downloaded, AppUpdateManifest manifest, PendingGitHubUpdate pending,
        CancellationToken ct)
    {
        CheckDestination();
        await ValidateBinaryAsync(downloaded, manifest, ct).ConfigureAwait(false);
        var target = Environment.ProcessPath!;
        GitHubUpdateService.EnsureSpace(Path.GetDirectoryName(target)!, manifest.File.Size);
        var id = Guid.NewGuid().ToString("N");
        var operation = OperationPath(id);
        var staged = target + "." + id + ".new";
        var script = Path.Combine(CacheDirectory, id + ".ps1");
        var requestPath = Path.Combine(CacheDirectory, id + ".install.json");
        Directory.CreateDirectory(CacheDirectory);
        var handedOff = false;
        try
        {
            await using (var input = File.OpenRead(downloaded))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PNCPKing.App.InstallGitHubUpdate.ps1")
                ?? throw new IOException("Instalador incorporado ausente."))
            await using (var output = File.Create(script))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(operation, JsonSerializer.Serialize(pending, GitHubUpdateValidation.Json), ct).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var request = new AppInstallRequest(process.Id, process.StartTime.ToUniversalTime().Ticks, target, staged,
                manifest.File.Size, manifest.File.Sha256, manifest.Version, id);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, GitHubUpdateValidation.Json), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestPath", requestPath })
                info.ArgumentList.Add(arg);
            using var installer = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o instalador.");
            // Do not close the app unless the helper has read and validated its local request.
            for (var attempt = 0; attempt < 100 && !File.Exists(requestPath + ".ready"); attempt++)
            {
                if (installer.HasExited) throw new IOException("O instalador foi interrompido antes do encerramento do programa.");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            if (!File.Exists(requestPath + ".ready")) throw new IOException("O instalador não respondeu; o programa continuará aberto.");
            ct.ThrowIfCancellationRequested();
            // The helper must observe this handoff as well as process exit before replacing anything.
            await File.WriteAllTextAsync(requestPath + ".commit", "ready", ct).ConfigureAwait(false);
            handedOff = true;
        }
        finally
        {
            if (!handedOff)
            {
                File.Delete(staged);
                File.Delete(operation);
                File.Delete(requestPath + ".commit");
            }
        }
    }

    public static async Task<PendingGitHubUpdate> ReadPendingAsync(string id, CancellationToken ct)
    {
        var path = OperationPath(id);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Operação pendente inválida.");
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PendingGitHubUpdate>(input, GitHubUpdateValidation.Json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Operação pendente ausente.");
    }

    public static void Complete(string id)
    {
        File.Delete(OperationPath(id));
        foreach (var suffix in new[] { ".ps1", ".install.json", ".install.json.ready", ".install.json.commit", ".install.json.result" })
            File.Delete(Path.Combine(CacheDirectory, id + suffix));
    }
}
