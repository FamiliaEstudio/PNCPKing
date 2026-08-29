using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public partial class GuardWindow : Window
{
    private readonly GuardMasterService _service;
    private bool _busy;

    public GuardWindow(GuardMasterService service)
    {
        _service = service;
        InitializeComponent();
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a raiz local sincronizada pelo Google Drive",
            InitialDirectory = Directory.Exists(RootTextBox.Text) ? RootTextBox.Text : null
        };
        if (dialog.ShowDialog() == true)
        {
            RootTextBox.Text = dialog.FolderName;
        }
    }

    private async void GenerateCampaign_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            var root = ValidateRoot();
            var workers = ParseWorkers();
            if (File.Exists(Path.Combine(root, "control.json")) && MessageBox.Show(
                    this,
                    "Uma campanha já existe nesta pasta. Substituí-la invalida os planos anteriores. Continuar?",
                    "Substituir campanha",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            SetBusy(true, "Gerando campanha a partir do índice local…");
            var result = await _service.CreateOrReplaceCampaignAsync(root, workers).ConfigureAwait(true);
            var distribution = string.Join(
                "; ",
                result.Workers.Select(worker => $"{worker.WorkerName}: {worker.ContractCount:N0}"));
            StatusTextBlock.Text =
                $"Campanha {result.CampaignId} criada com {result.ContractCount:N0} contratação(ões). " +
                $"Distribuição: {distribution}. Os planos estão em plans\\{result.CampaignId}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "PNCP Guard", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "A campanha não foi criada.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportPackages_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            var root = ValidateRoot();
            SetBusy(true, "Validando e importando pacotes…");
            var result = await _service.ImportPackagesAsync(root).ConfigureAwait(true);
            StatusTextBlock.Text =
                $"Arquivos: {result.PackageFiles:N0}; pacotes importados: {result.ImportedPackages:N0}; " +
                $"duplicados: {result.DuplicatePackages:N0}; rejeitados: {result.RejectedPackages:N0}. " +
                $"Contratações aplicadas: {result.ImportedContracts:N0}; ausentes: {result.MissingContracts:N0}; " +
                $"versão divergente: {result.DivergentContracts:N0}; mais antigas: {result.OlderContracts:N0}." +
                (result.Errors.Count == 0
                    ? string.Empty
                    : Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(5)));
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "PNCP Guard", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "A importação não foi concluída.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string ValidateRoot()
    {
        if (string.IsNullOrWhiteSpace(RootTextBox.Text))
        {
            throw new InvalidOperationException("Escolha a raiz local do Google Drive.");
        }

        var root = Path.GetFullPath(RootTextBox.Text.Trim());
        Directory.CreateDirectory(root);
        return root;
    }

    private IReadOnlyList<GuardWorkerInput> ParseWorkers()
    {
        var workers = new List<GuardWorkerInput>();
        foreach (var rawLine in WorkersTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) || weight <= 0)
            {
                throw new InvalidOperationException(
                    $"Linha inválida: “{rawLine}”. Use o formato Nome|Peso, com peso inteiro positivo.");
            }

            workers.Add(new GuardWorkerInput(parts[0], weight));
        }

        if (workers.Count == 0)
        {
            throw new InvalidOperationException("Informe ao menos um trabalhador.");
        }

        return workers;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        if (message is not null)
        {
            StatusTextBlock.Text = message;
        }
    }
}
