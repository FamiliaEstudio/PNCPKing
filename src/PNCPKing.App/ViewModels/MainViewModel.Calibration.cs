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
    private ResourceUsageProfile _selectedResourceProfile;
    public ICommand EvaluatePcCommand { get; }
    public ICommand SelectResourceProfileCommand { get; }

    public bool IsAutomaticResourceProfile => _selectedResourceProfile == ResourceUsageProfile.Automatic;
    public bool IsRestrictedResourceProfile => _selectedResourceProfile == ResourceUsageProfile.Restricted;
    public bool IsMediumResourceProfile => _selectedResourceProfile == ResourceUsageProfile.Medium;
    public bool IsBroadResourceProfile => _selectedResourceProfile == ResourceUsageProfile.Broad;
    public string ResourceProfileSummary
    {
        get
        {
            var connections = _calibrationService.Connections;
            var active = connections.ProfileName == "Balanceado" ? "Médio" : connections.ProfileName;
            return $"Em uso: {active}" +
                (connections.SelectedResourceProfile == ResourceUsageProfile.Automatic ? " (automático)" : "") +
                (_selectedResourceProfile != connections.SelectedResourceProfile
                    ? $" · reinicie para aplicar {ResourceProfileName(_selectedResourceProfile)}" : "");
        }
    }

    private async Task SelectResourceProfileAsync(string? value)
    {
        if (!Enum.TryParse<ResourceUsageProfile>(value, out var profile) || !Enum.IsDefined(profile)) return;
        try
        {
            if (profile == _selectedResourceProfile) return;
            var settings = await _settingsService.UpdateAsync(settings => settings with
            {
                ResourceProfile = profile,
                SqliteCalibration = null
            }).ConfigureAwait(true);
            _selectedResourceProfile = settings.EffectiveResourceProfile;
            StatusText = $"Perfil {ResourceProfileName(profile)} salvo. Feche e reabra o PNCP King para aplicar.";
        }
        finally
        {
            OnPropertyChanged(nameof(IsAutomaticResourceProfile));
            OnPropertyChanged(nameof(IsRestrictedResourceProfile));
            OnPropertyChanged(nameof(IsMediumResourceProfile));
            OnPropertyChanged(nameof(IsBroadResourceProfile));
            OnPropertyChanged(nameof(ResourceProfileSummary));
        }
    }

    private static string ResourceProfileName(ResourceUsageProfile profile) => profile switch
    {
        ResourceUsageProfile.Restricted => "Restrito",
        ResourceUsageProfile.Medium => "Médio",
        ResourceUsageProfile.Broad => "Amplo",
        _ => "Automático"
    };

    private bool CanEvaluatePc => !IsInitializing && !IsFileBusy && !IsIndexBusy && !IsCatalogBusy &&
        !IsPriceBusy && !IsForegroundBusy && !IsDocumentBusy && !IsPriceCacheBusy &&
        !IsNationalPriceIndexBusy && !_automaticMaintenanceRunning && !IsAnyAggressivePncpMode &&
        !_isResultPageLoading && !_isLocalPricePageLoading && !_contractSearchCountPending && !_retentionRunning &&
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
        if (calibration is not null && _selectedResourceProfile != _calibrationService.Connections.SelectedResourceProfile)
            throw new InvalidOperationException("O perfil de recursos foi alterado. Reinicie o PNCP King antes de aplicar uma avaliação.");
        if (calibration is not null && (IsFileBusy || !File.Exists(path) ||
            !calibration.AppliesTo(path, File.GetCreationTimeUtc(path), SqliteContractRepository.CurrentSchemaVersion,
                new SystemResourceProbe().GetSnapshot(), _selectedResourceProfile)))
            throw new InvalidOperationException("O banco ou o ambiente mudou. Execute uma nova avaliação.");
        await _settingsService.UpdateAsync(settings => settings with { SqliteCalibration = calibration }).ConfigureAwait(true);
    }
}
