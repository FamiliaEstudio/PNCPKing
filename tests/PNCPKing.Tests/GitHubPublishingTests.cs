using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubPublishingTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("**Full Changelog**: https://github.com/example/compare/v1...v2", false)]
    [InlineData("- Full Changelog: https://github.com/example/compare/v1...v2", false)]
    [InlineData("- [Histórico completo](https://github.com/example/compare/v1...v2)", false)]
    [InlineData("Novidades:\n\n- Preços em linhas compactas e leitura do descritivo completo.", true)]
    public async Task ReleaseNotesRequireAWrittenSummary(string notes, bool valid)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"pncpking-release-notes-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, notes);
            var result = await RunScriptAsync("read-release-notes.ps1", ["-Version", "1.2.12", "-NotesPath", path]);
            Assert.True((result.ExitCode == 0) == valid, result.Output);
            File.Delete(path);
            result = await RunScriptAsync("read-release-notes.ps1", ["-Version", "1.2.12", "-NotesPath", path]);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PublisherPreservesTheFullPackageAndBuildsAnImportableLegacyWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var source = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(OfficialUpdateService.WindowDays - 1));
        await source.Repository.EnsureCoverageWindowAsync(start, today, [6]);
        await source.Repository.SetCoverageStatusAsync(start, today, 6, "ALL", CoverageStatus.Complete, 0);
        var update = Path.Combine(source.Directory, "janela móvel.pncpupdate");
        var exported = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(update);
        var original = await File.ReadAllBytesAsync(update);
        var output = Path.Combine(source.Directory, "anexos com espaços");

        var result = await RunScriptAsync("prepare-github-prices.ps1",
            ["-UpdatePackage", update, "-OutputDirectory", output]);
        Assert.True(result.ExitCode == 0, result.Output);
        var manifest = JsonSerializer.Deserialize<PricesUpdateManifest>(
            await File.ReadAllTextAsync(Path.Combine(output, "prices-update-v2.json")), GitHubUpdateValidation.Json)!;
        GitHubUpdateValidation.Validate(manifest);
        Assert.Equal(exported.PackageId, manifest.Update.Manifest.PackageId);
        Assert.Equal("1.2.24", manifest.MinimumAppVersion);
        Assert.Equal(20, manifest.Update.Manifest.Chunks.Count);
        var incompatible = await RunScriptAsync("prepare-github-prices.ps1",
            ["-UpdatePackage", update, "-OutputDirectory", output, "-MinimumAppVersion", "1.2.23"]);
        Assert.NotEqual(0, incompatible.ExitCode);
        var asset = Assert.Single(manifest.Update.Download.Parts);
        await GitHubUpdateService.ValidatePackageAsync(Path.Combine(output, asset.Name), manifest.Update);
        var legacy = JsonSerializer.Deserialize<PricesUpdateManifest>(
            await File.ReadAllTextAsync(Path.Combine(output, "prices-update.json")), GitHubUpdateValidation.Json)!;
        GitHubUpdateValidation.Validate(legacy);
        Assert.Equal("1.2.0", legacy.MinimumAppVersion);
        Assert.NotEqual(exported.PackageId, legacy.Update.Manifest.PackageId);
        Assert.Equal(today.AddDays(-9), legacy.Update.Manifest.StartDate);
        Assert.Equal(today, legacy.Update.Manifest.EndDate);
        Assert.Equal(10, legacy.Update.Manifest.Chunks.Count);
        Assert.Equal(exported.GeneratedAt, legacy.Update.Manifest.GeneratedAt);
        Assert.Equal(exported.IntegrityValidatedAt, legacy.Update.Manifest.IntegrityValidatedAt);
        var legacyAsset = Assert.Single(legacy.Update.Download.Parts);
        var legacyPath = Path.Combine(output, legacyAsset.Name);
        await GitHubUpdateService.ValidatePackageAsync(legacyPath, legacy.Update);
        Assert.True(await GitHubUpdateService.VerifyAsync(legacyPath, legacyAsset.Size, legacyAsset.Sha256));
        using (var originalZip = ZipFile.OpenRead(update))
        using (var legacyZip = ZipFile.OpenRead(legacyPath))
        {
            foreach (var chunk in legacy.Update.Manifest.Chunks)
            {
                Assert.Contains(chunk, exported.Chunks);
                using var originalStream = originalZip.GetEntry(chunk.Entry)!.Open();
                using var legacyStream = legacyZip.GetEntry(chunk.Entry)!.Open();
                using var originalBytes = new MemoryStream();
                using var legacyBytes = new MemoryStream();
                await originalStream.CopyToAsync(originalBytes);
                await legacyStream.CopyToAsync(legacyBytes);
                Assert.Equal(originalBytes.ToArray(), legacyBytes.ToArray());
            }
        }
        await using var destination = await TestDatabase.CreateAsync();
        var importer = new OfficialUpdateService(destination.Repository.DatabasePath);
        await importer.ImportAsync(legacyPath);
        var state = await importer.GetTransferStatusAsync();
        Assert.True(state.Imports[legacy.Update.Manifest.PackageId].Completed);
        await importer.ImportAsync(Path.Combine(output, asset.Name));
        state = await importer.GetTransferStatusAsync();
        Assert.True(state.Imports[manifest.Update.Manifest.PackageId].Completed);
        Assert.Equal(original, await File.ReadAllBytesAsync(update));
        Assert.Equal(4, Directory.GetFiles(output).Length);
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
