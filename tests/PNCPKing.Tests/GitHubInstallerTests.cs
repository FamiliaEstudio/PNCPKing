using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PNCPKing.App.Services;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubInstallerTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("locked")]
    [InlineData("corrupt")]
    [InlineData("cancelled")]
    [InlineData("changed-after-ready")]
    [InlineData("slow-shutdown")]
    public async Task WindowsInstallerWaitsForExitAndOnlyReplacesValidatedFiles(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.InstallerTests", "pasta com espaços " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? parent = null;
        Process? installer = null;
        FileStream? locked = null;
        try
        {
            // A Windows utility is used as an inert executable fixture. No PNCP King or user database is opened.
            var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
            var bytes = await File.ReadAllBytesAsync(fixture);
            var oldBytes = bytes.Concat(new byte[] { 1, 2, 3 }).ToArray();
            var target = Path.Combine(directory, "PNCPKing.exe");
            await File.WriteAllBytesAsync(target, oldBytes);
            var id = Guid.NewGuid().ToString("N");
            var staged = target + "." + id + ".new";
            await File.WriteAllBytesAsync(staged, scenario == "corrupt" ? [1, 2, 3] : bytes);
            var versionInfo = FileVersionInfo.GetVersionInfo(fixture);
            var version = $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}";
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (scenario != "corrupt")
                await GitHubAppInstaller.ValidateBinaryAsync(staged,
                    new(1, version, "win-x64", SqliteContractRepository.CurrentSchemaVersion, new("PNCPKing.exe", bytes.Length, hash)), CancellationToken.None);
            parent = StartPowerShell(["-Command", "Start-Sleep -Seconds 150"]);
            var request = new AppInstallRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, staged,
                bytes.Length, hash, version, id);
            var path = Path.Combine(directory, id + ".install.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, GitHubUpdateValidation.Json));
            if (scenario == "locked") locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            installer = StartPowerShell(["-File", Path.Combine(AppContext.BaseDirectory, "Scripts", "InstallGitHubUpdate.ps1"), "-RequestPath", path]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(scenario == "slow-shutdown" ? 110 : 30));
            while (!File.Exists(path + ".ready") && !File.Exists(path + ".result"))
                await Task.Delay(50, deadline.Token);
            if (scenario != "corrupt")
            {
                Assert.True(File.Exists(path + ".ready"));
                Assert.Equal(oldBytes, await File.ReadAllBytesAsync(target));
                Assert.False(File.Exists(path + ".result"));
                if (scenario == "changed-after-ready")
                {
                    var changed = (byte[])bytes.Clone();
                    changed[^1] ^= 1;
                    await File.WriteAllBytesAsync(staged, changed);
                }
                if (scenario != "cancelled") await GitHubAppInstaller.ConfirmInstallerAsync(installer, path, deadline.Token);
                if (scenario == "slow-shutdown")
                {
                    await Task.Delay(TimeSpan.FromSeconds(63), deadline.Token);
                    Assert.False(File.Exists(path + ".result"));
                    Assert.Equal(oldBytes, await File.ReadAllBytesAsync(target));
                }
                parent.Kill();
                await parent.WaitForExitAsync(deadline.Token);
                while (!File.Exists(path + ".result")) await Task.Delay(50, deadline.Token);
            }
            // The file becomes visible before the helper closes its write handle.
            // Wait for readable content within the same deadline as the handoff.
            string result;
            while (true)
            {
                try { result = await File.ReadAllTextAsync(path + ".result", deadline.Token); break; }
                catch (IOException) { await Task.Delay(50, deadline.Token); }
            }
            if (scenario is "success" or "slow-shutdown")
            {
                Assert.Equal("installed", result);
                await installer.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, installer.ExitCode);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
                Assert.False(File.Exists(target + "." + id + ".bak"));
            }
            else
            {
                Assert.NotEqual("installed", result);
                Assert.Equal(oldBytes, await File.ReadAllBytesAsync(target));
                if (scenario == "changed-after-ready") Assert.Contains("SHA-256", result);
                if (scenario == "corrupt")
                {
                    var error = await Assert.ThrowsAsync<IOException>(() =>
                        GitHubAppInstaller.ConfirmInstallerAsync(installer, path, deadline.Token));
                    Assert.Contains("Tamanho do executavel divergente", error.Message);
                }
                // Dismiss this fixture helper's error dialog; it is deliberately interactive in production.
                if (!installer.HasExited) installer.Kill(entireProcessTree: true);
                await installer.WaitForExitAsync(deadline.Token);
            }
        }
        finally
        {
            locked?.Dispose();
            foreach (var process in new[] { parent, installer }.OfType<Process>())
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            // ShellExecute returns before the inert fixture process has finished using its image.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (Exception e) when (attempt < 30 && e is IOException or UnauthorizedAccessException)
                { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public async Task CancelDuringPreparationReleasesTheTemporaryBeforeCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("PNCPKing-Prep-");
        var staged = Path.Combine(directory.FullName, "PNCPKing.exe.test.new");
        var script = Path.Combine(directory.FullName, "hold.ps1");
        var request = Path.Combine(directory.FullName, "install.json");
        Process? helper = null;
        try
        {
            await File.WriteAllTextAsync(staged, "fixture");
            await File.WriteAllTextAsync(script, """
                param($staged)
                $stream = [IO.File]::OpenRead($staged)
                try {
                    [IO.File]::WriteAllText($staged + '.locked', 'locked')
                    Start-Sleep -Seconds 90
                } finally { $stream.Dispose() }
                """);
            helper = StartPowerShell(["-File", script, staged]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(staged + ".locked")) await Task.Delay(50, deadline.Token);
            Assert.Throws<IOException>(() => File.Delete(staged));
            using var cancel = new CancellationTokenSource();
            var handoff = GitHubAppInstaller.ConfirmInstallerAsync(helper, request, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handoff);
            Assert.True(helper.HasExited);
            Assert.False(File.Exists(request + ".commit"));
            File.Delete(staged);
        }
        finally
        {
            if (helper is not null)
            {
                if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); }
                helper.Dispose();
            }
            directory.Delete(true);
        }
    }

    private static Process StartPowerShell(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments))
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
}
