using System.Diagnostics;
using System.Text.Json;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubPublishingTests
{
    [Fact]
    public async Task PublisherProducesOneV2AssetWithoutChangingSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var source = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await source.Repository.EnsureCoverageWindowAsync(today.AddDays(-9), today, [6]);
        await source.Repository.SetCoverageStatusAsync(today.AddDays(-9), today, 6, "ALL", CoverageStatus.Complete, 0);
        var update = Path.Combine(source.Directory, "janela móvel.pncpupdate");
        var exported = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(update);
        var original = await File.ReadAllBytesAsync(update);
        var output = Path.Combine(source.Directory, "anexos com espaços");

        var result = await RunScriptAsync("prepare-github-prices.ps1",
            ["-UpdatePackage", update, "-OutputDirectory", output]);
        Assert.True(result.ExitCode == 0, result.Output);
        var manifest = JsonSerializer.Deserialize<PricesUpdateManifest>(
            await File.ReadAllTextAsync(Path.Combine(output, "prices-update.json")), GitHubUpdateValidation.Json)!;
        GitHubUpdateValidation.Validate(manifest);
        Assert.Equal(exported.PackageId, manifest.Update.Manifest.PackageId);
        var asset = Assert.Single(manifest.Update.Download.Parts);
        await GitHubUpdateService.ValidatePackageAsync(Path.Combine(output, asset.Name), manifest.Update);
        Assert.Equal(original, await File.ReadAllBytesAsync(update));
        Assert.Equal(2, Directory.GetFiles(output).Length);
        Assert.Empty(Directory.GetFiles(output, "*.partial"));
        Assert.Empty(Directory.GetFiles(output, "*.exe"));
    }

    [Theory]
    [InlineData("../operation")]
    [InlineData("not-an-id")]
    public void ResumeArgumentCannotSelectArbitraryFiles(string id) =>
        Assert.Throws<InvalidDataException>(() => GitHubAppInstaller.OperationPath(id));

    [Fact]
    public void ReadOnlyExecutableIsRejectedBeforeShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "PNCPKing.exe");
        try
        {
            File.WriteAllText(target, "fixture");
            GitHubAppInstaller.CheckDestination(target);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            Assert.Contains("somente leitura", Assert.Throws<IOException>(() => GitHubAppInstaller.CheckDestination(target)).Message);
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string name,
        IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(AppContext.BaseDirectory, "Scripts", name)
                 }.Concat(arguments))
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
