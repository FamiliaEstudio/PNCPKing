using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.App.Views;
using PNCPKing.Infrastructure.Api;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using PNCPKing.Core.Quotations;
using PNCPKing.Core.Models;

namespace PNCPKing.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppDiagnosticLog? _diagnosticLog;
    private AppPerformanceTelemetry? _performanceTelemetry;
    private DispatcherResponsivenessMonitor? _responsivenessMonitor;
    private AdaptiveMaintenanceCoordinator? _maintenanceCoordinator;
    private PreProcessInputEventHandler? _inputHandler;
    private int _uiErrorDialogOpen;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var resourceProbe = new SystemResourceProbe();
        _diagnosticLog = new AppDiagnosticLog(resourceProbe);
        _performanceTelemetry = new AppPerformanceTelemetry(resourceProbe);
        using var startupSpan = _performanceTelemetry.Begin("startup", "application");
        _diagnosticLog.WriteStartupHeader();
        AsyncCommandRuntime.Configure(
            HandleRecoverableCommandException,
            () => _performanceTelemetry.Record(
                "ui",
                "interaction-suppressed",
                TimeSpan.Zero));
        DispatcherUnhandledException += (_, args) =>
            _diagnosticLog.Error("dispatcher", "Exceção não tratada na interface.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _diagnosticLog.Error("appdomain", "Exceção fatal não tratada.", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            _diagnosticLog.Error("task", "Exceção não observada em tarefa de fundo.", args.Exception);

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        try
        {
            if (HasAnotherPncpKingProcess())
            {
                _diagnosticLog.Warning(
                    "startup",
                    "Outra instância PNCPKing.exe foi encontrada; a nova abertura foi interrompida.");
                MessageBox.Show(
                    "Já existe outro PNCP King em execução. Feche versões antigas pelo Gerenciador de Tarefas " +
                    "antes de abrir este executável.",
                    "PNCP King já está aberto",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            _instanceMutex = new Mutex(
                initiallyOwned: true,
                name: "Local\\PNCPKing.SingleInstance",
                createdNew: out var createdNew);
            if (!createdNew)
            {
                _diagnosticLog.Warning("startup", "O bloqueio de instância única já estava ocupado.");
                MessageBox.Show(
                    "O PNCP King já está em execução neste usuário.",
                    "PNCP King já está aberto",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            var staleImports = BackupService.CleanupStaleTemporaryDirectories(TimeSpan.FromMinutes(5));
            if (staleImports > 0)
            {
                _diagnosticLog.Info(
                    "startup",
                    $"{staleImports} pasta(s) temporária(s) de importações antigas foram removidas.");
            }

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

            var desktopShortcutService = new DesktopShortcutService();
            Exception? desktopShortcutStartupException = null;
            try
            {
                desktopShortcutService.Apply(settings.EffectiveDesktopShortcutEnabled);
            }
            catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
            {
                desktopShortcutStartupException = exception;
                _diagnosticLog.Error(
                    "desktop-shortcut",
                    "Não foi possível reconciliar o atalho da área de trabalho durante a abertura.",
                    exception);
            }

            var columnLayouts = new DataGridColumnLayoutService(
                settingsService,
                settings,
                _performanceTelemetry);
            Directory.CreateDirectory(settings.DataFolder);
            var databasePath = Path.Combine(settings.DataFolder, "pncpking.db");
            _performanceTelemetry.SetDatabasePath(databasePath);
            _diagnosticLog.Info(
                "database",
                $"Banco selecionado. pasta_dados={settings.DataFolder}; banco={databasePath}; " +
                $"tamanho={(File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0)} bytes.");
            var sqliteConnections = new SqliteConnectionFactory(
                databasePath,
                resourceProbe: resourceProbe);
            _performanceTelemetry.SetSqliteProfile(sqliteConnections.ProfileName);
            var repository = new SqliteContractRepository(sqliteConnections, _performanceTelemetry);
            var quotationRepository = new SqliteQuotationRepository(sqliteConnections);
            var internetEvidenceStore = new InternetEvidenceStore(settings.DataFolder);

            var socketsHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 48
            };
            var requestScheduler = new PncpRequestScheduler(maximumConcurrency: 48);
            var requestTelemetry = new PncpRequestTelemetry();
            var handler = new PncpSchedulingHandler(requestScheduler, requestTelemetry, _performanceTelemetry)
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
            var documentService = new ContractDocumentService(client, settings.DataFolder);
            var rasterizer = new PdfPageRasterizer();
            var ocrService = new EmbeddedTesseractOcrService();
            var textIndexService = new PdfTextIndexService(rasterizer, ocrService);
            var relevantPageService = new ContractRelevantPageService(
                documentService,
                textIndexService);
            var evidenceService = new QuotationEvidenceExportService(
                documentService,
                textIndexService,
                rasterizer,
                quotationRepository,
                internetEvidenceStore);
            var syncService = new SyncService(client, repository, _performanceTelemetry);
            var autoSyncCoordinator = new AutoSyncCoordinator(client, repository, syncService);
            var priceCacheRepository = new SqlitePriceCacheRepository(sqliteConnections, _performanceTelemetry);
            var priceCacheService = new PriceCacheService(
                client,
                repository,
                repository,
                priceCacheRepository,
                performance: _performanceTelemetry);
            var itemSearchService = new ItemSearchSessionService(
                client,
                repository,
                Path.Combine(settings.DataFolder, "pncpking-general-search.db"),
                requestTelemetry,
                persistentSession: true,
                requestScheduler: requestScheduler);
            var transientItemSearchService = new ItemSearchSessionService(
                client,
                repository,
                Path.Combine(settings.DataFolder, "pncpking-search-session.db"),
                requestTelemetry,
                requestScheduler: requestScheduler);
            var quotationItemSearchService = new QuotationItemSearchService(
                repository,
                quotationRepository,
                transientItemSearchService);
            var quotationService = new QuotationService(
                quotationRepository,
                new QuotationAnalyzer(),
                _performanceTelemetry);
            var catalogRepository = new SqliteCatalogRepository(sqliteConnections, _performanceTelemetry);
            var catalogHttpClient = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 2
            })
            {
                BaseAddress = new Uri("https://dadosabertos.compras.gov.br/"),
                Timeout = TimeSpan.FromMinutes(6)
            };
            catalogHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PNCPKing/1.0");
            catalogHttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            var catalogSyncService = new CatalogSyncService(
                new ComprasCatalogClient(catalogHttpClient),
                catalogRepository);
            var catalogSearchService = new CatalogSearchService(catalogRepository);
            var sweetCodeRepository = new SqliteSweetCodeRepository(sqliteConnections);
            var internetPriceService = new InternetPriceService(
                quotationRepository,
                quotationService,
                internetEvidenceStore);
            var windowCaptureService = new WindowsForegroundWindowCaptureService();
            var aiHttpClient = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 2
            })
            {
                Timeout = TimeSpan.FromMinutes(30)
            };
            aiHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PNCPKing/1.0");
            var aiDraftCache = new AiDraftCache(settings.DataFolder);
            var aiProvider = new OpenAiCompatibleQuotationProvider(aiHttpClient);
            var aiDraftService = new AiQuotationDraftService(
                textIndexService,
                new PdfToMarkdownConverter(),
                aiProvider,
                aiDraftCache,
                settings.DataFolder);
            var aiPromptRefinementService = new AiPromptRefinementService(aiProvider);
            var aiCostEstimator = new AiCostEstimator(
                new BcbExchangeRateClient(aiHttpClient, settings.DataFolder));
            var timedAutomation = new TimedQuotationAutomationService(
                repository,
                transientItemSearchService,
                quotationService);
            var maintenanceCoordinator = new AdaptiveMaintenanceCoordinator(resourceProbe);
            _maintenanceCoordinator = maintenanceCoordinator;
            _inputHandler = (_, args) =>
            {
                if (args.StagingItem.Input is MouseButtonEventArgs or MouseWheelEventArgs or
                    KeyEventArgs or TouchEventArgs)
                {
                    maintenanceCoordinator.NotifyVisibleActivity();
                }
            };
            InputManager.Current.PreProcessInput += _inputHandler;
            var viewModel = new MainViewModel(
                repository,
                new PreflightService(client),
                syncService,
                autoSyncCoordinator,
                requestScheduler,
                priceCacheRepository,
                priceCacheService,
                new ItemHydrationService(client, repository),
                itemSearchService,
                transientItemSearchService,
                quotationItemSearchService,
                new BackupService(repository, _performanceTelemetry),
                quotationService,
                new QuotationWorkbookService(),
                new QuotationWorkbookImportService(),
                new QuotationPackageService(databasePath, settings.DataFolder),
                catalogRepository,
                catalogSyncService,
                catalogSearchService,
                requestTelemetry,
                sweetCodeRepository,
                documentService,
                relevantPageService,
                evidenceService,
                internetPriceService,
                internetEvidenceStore,
                windowCaptureService,
                rasterizer,
                ocrService,
                columnLayouts,
                aiDraftService,
                aiCostEstimator,
                new WindowsCredentialStore(),
                aiDraftCache,
                aiPromptRefinementService,
                timedAutomation,
                settingsService,
                desktopShortcutService,
                settings.EffectiveDesktopShortcutEnabled,
                settings.EffectiveCatalogRefreshIntervalDays,
                settings.DataFolder,
                _diagnosticLog,
                _performanceTelemetry,
                maintenanceCoordinator);
            var mainWindow = new MainWindow(viewModel, columnLayouts);
            MainWindow = mainWindow;
            mainWindow.Show();
            if (desktopShortcutStartupException is not null)
            {
                MessageBox.Show(
                    mainWindow,
                    "O PNCP King continuará funcionando, mas o Windows não permitiu atualizar " +
                    "o atalho da área de trabalho. A preferência foi mantida e uma nova tentativa " +
                    "será feita na próxima abertura.\n\n" + desktopShortcutStartupException.Message,
                    "Atalho da área de trabalho",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            _responsivenessMonitor = new DispatcherResponsivenessMonitor(Dispatcher, _performanceTelemetry);
            // During first-run setup the folder dialog is temporarily the only
            // window. Keep the application alive when it closes, then restore
            // normal shutdown behavior once the real main window is visible.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            viewModel.SetStartupPhase("Preparando e migrando o banco de dados…");
            var initializationProgress = new Progress<DatabaseInitializationProgress>(
                viewModel.SetStartupProgress);
            DatabaseInitializationResult initialization;
            using (var databaseSpan = _performanceTelemetry.Begin("startup", "database-initialize"))
            {
                initialization = await Task.Run(
                        () => repository.InitializeAsync(
                            viewModel.StartupCancellationToken,
                            initializationProgress),
                        viewModel.StartupCancellationToken)
                    .ConfigureAwait(true);
                databaseSpan.Complete();
            }

            _performanceTelemetry.SetDatabaseSchemaVersion(initialization.CurrentVersion);

            _diagnosticLog.Info(
                "database",
                $"Banco inicializado. esquema_anterior={initialization.PreviousVersion}; " +
                $"esquema_atual={initialization.CurrentVersion}; " +
                $"migracoes={string.Join(',', initialization.AppliedMigrations)}; " +
                $"duracao_ms={initialization.Duration.TotalMilliseconds:N1}; " +
                $"perfil_sqlite={sqliteConnections.ProfileName}.");
            viewModel.SetStartupPhase("Carregando a pesquisa essencial…");
            await viewModel.InitializeAsync(viewModel.StartupCancellationToken).ConfigureAwait(true);
            viewModel.CompleteStartup();
            startupSpan.Complete();
            _diagnosticLog.Info("startup", "Janela principal inicializada com sucesso.");
        }
        catch (OperationCanceledException)
        {
            Shutdown();
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("startup", "Não foi possível iniciar o PNCP King.", exception);
            MessageBox.Show(
                $"Não foi possível iniciar o PNCP King.\n\n{exception.Message}\n\n" +
                $"Log para diagnóstico:\n{_diagnosticLog.FilePath}",
                "PNCP King",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_inputHandler is not null)
        {
            InputManager.Current.PreProcessInput -= _inputHandler;
            _inputHandler = null;
        }

        _maintenanceCoordinator?.CancelActiveSlice();
        _maintenanceCoordinator = null;
        _responsivenessMonitor?.Dispose();
        _responsivenessMonitor = null;
        _diagnosticLog?.Info("shutdown", $"Aplicativo encerrado com código {e.ApplicationExitCode}.");
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // A instância não chegou a assumir o bloqueio.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    private void HandleRecoverableCommandException(Exception exception)
    {
        _diagnosticLog?.Error("ui-command", "Falha recuperável em uma ação da interface.", exception);
        _performanceTelemetry?.Record(
            "ui",
            "command-error",
            TimeSpan.Zero,
            succeeded: false,
            errorKind: exception.GetType().Name);
        if (Interlocked.Exchange(ref _uiErrorDialogOpen, 1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                MessageBox.Show(
                    $"A ação não pôde ser concluída, mas o PNCP King continuará aberto.\n\n{exception.Message}",
                    "PNCP King",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _uiErrorDialogOpen, 0);
            }
        });
    }

    private static bool HasAnotherPncpKingProcess()
    {
        try
        {
            return Process.GetProcessesByName("PNCPKing")
                .Any(process =>
                {
                    using (process)
                    {
                        return process.Id != Environment.ProcessId;
                    }
                });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
