namespace PNCPKing.Guard;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\PNCPGuard.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var settingsService = new GuardSettingsService();
        var log = new GuardLog(settingsService.LogPath);
        if (args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var settings = await settingsService.LoadAsync().ConfigureAwait(false);
                await new GuardRunner(settingsService, log).RunAsync(settings).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                log.Write("Ciclo encerrado com erro: " + exception);
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new GuardForm(settingsService, log));
    }
}
