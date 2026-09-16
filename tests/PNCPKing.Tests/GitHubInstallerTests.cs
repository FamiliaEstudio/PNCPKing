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
            parent = StartPowerShell(["-Command", "Start-Sleep -Seconds 30"]);
            var request = new AppInstallRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, staged,
                bytes.Length, hash, version, id);
            var path = Path.Combine(directory, id + ".install.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, GitHubUpdateValidation.Json));
            if (scenario == "locked") locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            installer = StartPowerShell(["-File", Path.Combine(AppContext.BaseDirectory, "Scripts", "InstallGitHubUpdate.ps1"), "-RequestPath", path]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!File.Exists(path + ".ready") && !File.Exists(path + ".result"))
                await Task.Delay(50, deadline.Token);
            if (scenario != "corrupt")
            {
                Assert.True(File.Exists(path + ".ready"));
                Assert.Equal(oldBytes, await File.ReadAllBytesAsync(target));
                Assert.False(File.Exists(path + ".result"));
                if (scenario == "changed-after-ready") await File.WriteAllBytesAsync(staged, [1, 2, 3]);
                if (scenario != "cancelled") await File.WriteAllTextAsync(path + ".commit", "ready");
                parent.Kill();
                await parent.WaitForExitAsync(deadline.Token);
                while (!File.Exists(path + ".result")) await Task.Delay(50, deadline.Token);
            }
            var result = await File.ReadAllTextAsync(path + ".result");
            if (scenario == "success")
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

    private static Process StartPowerShell(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments))
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
}
