using System.Diagnostics;
using System.Text.Json;
using PNCPKing.App.Services;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubPublishingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedAssetsMatchExportedPackagesWithoutChangingTheSource(bool includeCumulative)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var source = await TestDatabase.CreateAsync();
        var service = new OfficialUpdateService(source.Repository.DatabasePath);
        var initial = Path.Combine(source.Directory, "base inicial.pncpupdate");
        var initialManifest = await service.ExportAsync(initial);
        var initialBytes = await File.ReadAllBytesAsync(initial);
        var cumulative = Path.Combine(source.Directory, "cumulativo.pncpupdate");
        if (includeCumulative) await service.ExportAsync(cumulative);
        var output = Path.Combine(source.Directory, "anexos com espaços");
        var arguments = new List<string> { "-BasePackage", initial, "-OutputDirectory", output };
        if (includeCumulative) arguments.AddRange(["-CumulativePackage", cumulative]);
        var result = await RunScriptAsync("prepare-github-prices.ps1", arguments);
        Assert.True(result.ExitCode == 0, result.Output);
        var manifest = JsonSerializer.Deserialize<PricesUpdateManifest>(await File.ReadAllTextAsync(Path.Combine(output, "prices-update.json")), GitHubUpdateValidation.Json)!;
        GitHubUpdateValidation.Validate(manifest);
        Assert.Equal(initialManifest, manifest.Base.Manifest);
        Assert.Equal(includeCumulative, manifest.Cumulative is not null);
        foreach (var package in new[] { manifest.Base, manifest.Cumulative }.OfType<PriceUpdatePackage>())
        {
            var asset = Assert.Single(package.Download.Parts);
            await GitHubUpdateService.ValidatePackageAsync(Path.Combine(output, asset.Name), package);
        }
        Assert.Equal(initialBytes, await File.ReadAllBytesAsync(initial));
        Assert.Empty(Directory.GetFiles(output, "*.partial"));
        Assert.Empty(Directory.GetFiles(output, "*.exe"));
    }

    [Fact]
    public async Task PublisherRejectsCumulativeFromAnotherBaseBeforeWritingManifest()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var source = await TestDatabase.CreateAsync();
        var service = new OfficialUpdateService(source.Repository.DatabasePath);
        var initial = Path.Combine(source.Directory, "base.pncpupdate");
        await service.ExportAsync(initial);
        await service.ExportAsync(Path.Combine(source.Directory, "other.pncpupdate"), newBase: true);
        var delta = Path.Combine(source.Directory, "delta.pncpupdate");
        await service.ExportAsync(delta);
        var output = Path.Combine(source.Directory, "assets");
        var result = await RunScriptAsync("prepare-github-prices.ps1", ["-BasePackage", initial,
            "-CumulativePackage", delta, "-OutputDirectory", output]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, "prices-update.json")));
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
        finally { File.SetAttributes(target, FileAttributes.Normal); Directory.Delete(directory, true); }
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string name, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(AppContext.BaseDirectory, "Scripts", name) }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
