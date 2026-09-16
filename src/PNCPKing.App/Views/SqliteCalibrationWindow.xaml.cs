using System.Text;
using System.Windows;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public partial class SqliteCalibrationWindow : Window
{
    private readonly Action _cancel;
    private readonly Func<SavedSqliteCalibration?, Task> _save;
    private SavedSqliteCalibration? _recommendation;
    private bool _running = true;

    public SqliteCalibrationWindow(Action cancel, Func<SavedSqliteCalibration?, Task> save)
    {
        InitializeComponent();
        _cancel = cancel;
        _save = save;
        Closed += (_, _) => _cancel();
    }

    public void ReportProgress(string message)
    {
        if (_running) Status.Text = message;
    }

    public void ShowResult(SqliteCalibrationResult result)
    {
        _running = false;
        _recommendation = result.SavedRecommendation;
        Activity.IsIndeterminate = false;
        Status.Text = result.Message;
        Apply.IsEnabled = _recommendation is not null;
        Restore.IsEnabled = true;
        Cancel.Content = "Fechar";
        Cancel.IsEnabled = true;
        var text = new StringBuilder();
        text.AppendLine($"Atual nesta execução: {result.Current.Description}");
        if (result.Recommended is { } choice)
        {
            text.AppendLine($"\n{choice.Name} recomendada — {choice.Description}");
            var baseline = result.Measurements.Where(value => value.Tuning == result.Current).ToArray();
            var proposed = result.Measurements.Where(value => value.Tuning == choice).ToArray();
            foreach (var scenario in baseline.Select(value => value.Scenario).Distinct())
            {
                var before = baseline.Where(value => value.Scenario == scenario).ToArray();
                var after = proposed.Where(value => value.Scenario == scenario).ToArray();
                text.AppendLine($"\n{scenario}");
                text.AppendLine($"Primeiro preço: {Seconds(Median(before.Select(value => value.FirstRowMs ?? 0)))} → {Seconds(Median(after.Select(value => value.FirstRowMs ?? 0)))}");
                text.AppendLine($"Dez primeiros: {Seconds(Median(before.Select(value => value.TenRowsMs ?? 0)))} → {Seconds(Median(after.Select(value => value.TenRowsMs ?? 0)))}");
                text.AppendLine($"Página completa: {Seconds(Median(before.Select(value => value.PageMs)))} → {Seconds(Median(after.Select(value => value.PageMs)))}");
            }
            var difference = (proposed.Max(value => value.PeakProcessBytes) - baseline.Max(value => value.PeakProcessBytes)) / 1048576d;
            text.AppendLine($"\nDiferença entre picos de RAM do processo: {difference:+0.0;-0.0;0} MiB");
            text.AppendLine($"Menor RAM livre observada: {proposed.Min(value => value.MinimumFreeBytes) / 1048576d:N0} MiB");
            text.AppendLine("Sem pressão de memória durante as comparações aprovadas.");
        }
        text.AppendLine("\nMedições da entrega pelo banco; não incluem renderização da grade. A diferença de picos não representa memória exclusiva do cache.");
        text.AppendLine("Primeiro acesso e repetições podem ter estados de cache diferentes; não houve limpeza do cache do Windows.\n");
        foreach (var value in result.Measurements)
        {
            text.AppendLine($"{value.Tuning.Description} | {value.Scenario} | rodada {value.Round + 1}" +
                (value.FirstAccess ? " | primeiro acesso da avaliação" : " | acesso subsequente"));
            text.AppendLine($"  Primeiro: {(value.FirstRowMs is { } first ? Seconds(first) : "—")}; dez: {(value.TenRowsMs is { } ten ? Seconds(ten) : "—")}; página: {Seconds(value.PageMs)}; próxima: {Seconds(value.NextPageMs)}");
            text.AppendLine($"  Fila: {Seconds(value.QueueMs)}; preparação medida: {Seconds(value.PreparationMs)}; pico: {value.PeakProcessBytes / 1048576d:N0} MiB; RAM livre mínima: {value.MinimumFreeBytes / 1048576d:N0} MiB; {value.Outcome}.\n");
        }
        Details.Text = text.ToString();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) => await SaveAsync(_recommendation);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await SaveAsync(null);
    private async Task SaveAsync(SavedSqliteCalibration? calibration)
    {
        Apply.IsEnabled = false;
        Restore.IsEnabled = false;
        try
        {
            await _save(calibration);
            Status.Text = calibration is null
                ? "Padrão restaurado. A configuração será usada na próxima abertura."
                : "Recomendação salva para este PC. Reinicie o PNCP King para aplicá-la.";
        }
        catch (Exception exception)
        {
            Status.Text = $"Não foi possível salvar: {exception.Message}";
        }
        finally
        {
            Apply.IsEnabled = _recommendation is not null;
            Restore.IsEnabled = true;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { _cancel(); Cancel.IsEnabled = false; }
        else Close();
    }
    private static string Seconds(double milliseconds) => $"{milliseconds / 1000:N1} s";
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
}
