using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.App.Views;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        try
        {
            var settingsService = new AppSettingsService();
            var settings = await settingsService.LoadAsync().ConfigureAwait(true);
            if (!settings.IsConfigured)
            {
                var dataFolderWindow = new DataFolderWindow(settings.DataFolder);
                if (dataFolderWindow.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                settings = settings with { DataFolder = dataFolderWindow.SelectedFolder, IsConfigured = true };
                await settingsService.SaveAsync(settings).ConfigureAwait(true);
            }

            Directory.CreateDirectory(settings.DataFolder);
            var databasePath = Path.Combine(settings.DataFolder, "pncpking.db");
            var repository = new SqliteContractRepository(databasePath);
            await repository.InitializeAsync().ConfigureAwait(true);

            var socketsHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 2
            };
            var requestScheduler = new PncpRequestScheduler(maximumConcurrency: 2);
            var requestTelemetry = new PncpRequestTelemetry();
            var handler = new PncpSchedulingHandler(requestScheduler, requestTelemetry)
            {
                InnerHandler = socketsHandler
            };
            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(6)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PNCPKing/1.0 (+https://pncp.gov.br)");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            var client = new PncpClient(httpClient);
            var syncService = new SyncService(client, repository);
            var autoSyncCoordinator = new AutoSyncCoordinator(client, repository, syncService);
            var itemSearchService = new ItemSearchSessionService(
                client,
                repository,
                Path.Combine(settings.DataFolder, "pncpking-search-session.db"),
                requestTelemetry);
            var viewModel = new MainViewModel(
                repository,
                new PreflightService(client),
                syncService,
                autoSyncCoordinator,
                new ItemHydrationService(client, repository),
                itemSearchService,
                new BackupService(repository),
                requestTelemetry,
                settings.DataFolder);
            var mainWindow = new MainWindow(viewModel);
            MainWindow = mainWindow;
            mainWindow.Show();
            // During first-run setup the folder dialog is temporarily the only
            // window. Keep the application alive when it closes, then restore
            // normal shutdown behavior once the real main window is visible.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Não foi possível iniciar o PNCP King.\n\n{exception.Message}",
                "PNCP King",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
