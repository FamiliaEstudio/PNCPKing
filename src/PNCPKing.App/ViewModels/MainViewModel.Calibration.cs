using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using PNCPKing.App.Views;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SqliteCalibrationService _calibrationService;
    private CancellationTokenSource? _calibrationCancellation;
    private SqliteCalibrationWindow? _calibrationWindow;
    private Task<SqliteCalibrationResult>? _calibrationTask;
    public ICommand EvaluatePcCommand { get; }

    private bool CanEvaluatePc => !IsInitializing && !IsFileBusy && !IsIndexBusy && !IsCatalogBusy &&
        !IsPriceBusy && !IsForegroundBusy && !IsDocumentBusy && !IsPriceCacheBusy &&
        !IsNationalPriceIndexBusy && !_automaticMaintenanceRunning && !IsAnyAggressivePncpMode &&
        !_isResultPageLoading && !_contractSearchCountPending && !_retentionRunning &&
        _quotationAutomationCancellation is null;

    private async Task EvaluatePcAsync()
    {
        if (_calibrationWindow is { } existing) { existing.Activate(); return; }
        if (!CanEvaluatePc || !_calibrationService.Connections.WorkCoordinator.IsIdle)
        {
            StatusText = "Aguarde as operações atuais terminarem para avaliar este PC.";
            return;
        }
        await using var maintenanceLease = _maintenanceCoordinator.TryEnter();
        if (maintenanceLease is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
        _calibrationCancellation = cancellation;
        var window = new SqliteCalibrationWindow(() => _calibrationCancellation?.Cancel(), SaveCalibrationAsync)
        { Owner = Application.Current.MainWindow };
        _calibrationWindow = window;
        window.Closed += (_, _) => _calibrationWindow = null;
        window.Show();
        var progress = new Progress<string>(message => window.ReportProgress(message));
        try
        {
            _calibrationTask = Task.Run(() => _calibrationService.EvaluateAsync(progress,
                cancellation.Token), cancellation.Token);
            var result = await _calibrationTask.ConfigureAwait(true);
            window.ShowResult(result);
            try
            {
                var report = Path.Combine(_diagnosticLog.DirectoryPath, "sqlite-calibration-latest.json");
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result with { SavedRecommendation = null },
                    new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(true);
                _diagnosticLog.Info("calibration", result.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _diagnosticLog.Warning("calibration", $"Não foi possível salvar o diagnóstico: {exception.Message}"); }
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            window.ShowResult(new(_calibrationService.Connections.SearchTuning, null, [],
                exception is OperationCanceledException ? "Avaliação cancelada; perfil atual preservado."
                    : $"Avaliação inconclusiva: {exception.Message}. Perfil atual preservado."));
        }
        finally
        {
            _calibrationCancellation = null;
            _calibrationTask = null;
            if (!_disposed) ScheduleNextMaintenance(_maintenanceCoordinator.GetDecision().RetryDelay);
        }
    }

    private async Task SaveCalibrationAsync(SavedSqliteCalibration? calibration)
    {
        var path = _calibrationService.Connections.DatabasePath;
        if (calibration is not null && (IsFileBusy || !File.Exists(path) ||
            !calibration.AppliesTo(path, File.GetCreationTimeUtc(path), SqliteContractRepository.CurrentSchemaVersion,
                new SystemResourceProbe().GetSnapshot())))
            throw new InvalidOperationException("O banco ou o ambiente mudou. Execute uma nova avaliação.");
        await _settingsService.UpdateAsync(settings => settings with { SqliteCalibration = calibration }).ConfigureAwait(true);
    }
}
