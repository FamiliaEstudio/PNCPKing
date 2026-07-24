using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationImportWindow : Window
{
    private readonly string _defaultOutputName;
    private readonly DataGridColumnLayoutService _columnLayouts;

    public QuotationImportWindow(
        QuotationImportDocument document,
        IReadOnlyList<QuotationProject> projects,
        Guid? selectedProjectId,
        AdequacyWeights weights,
        string filterSummary,
        DataGridColumnLayoutService columnLayouts)
    {
        _columnLayouts = columnLayouts;
        InitializeComponent();
        columnLayouts.Register("quotation-import-preview", ImportItemsGrid);
        Closed += (_, _) => _columnLayouts.Unregister(ImportItemsGrid);
        Items = document.Items;
        Projects = projects;
        DataContext = this;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        var baseName = Path.GetFileNameWithoutExtension(document.SourcePath);
        NewProjectNameTextBox.Text = projects.Count == 0 ? baseName : string.Empty;
        _defaultOutputName = baseName + "-cotação.xlsx";
        var contracts = document.Items.Sum(item =>
            checked(item.BatchCount * ItemSearchDefaults.ContractsPerBatch));
        SummaryTextBlock.Text =
            $"{document.Items.Count:N0} item(ns), até {contracts:N0} contratações candidatas examinadas. " +
            $"Filtros: {filterSummary}. Pesos: {weights}. " +
            "Cada lote examina 50 contratações e revela todos os itens compatíveis encontrados.";
    }

    public IReadOnlyList<QuotationImportItem> Items { get; }
    public IReadOnlyList<QuotationProject> Projects { get; }
    public Guid? ExistingProjectId { get; private set; }
    public string NewProjectName { get; private set; } = string.Empty;
    public string OutputPath { get; private set; } = string.Empty;

    private void ChooseColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _columnLayouts.ShowChooser(button, ImportItemsGrid);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar cotação automática",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = _defaultOutputName
        };
        if (dialog.ShowDialog() == true)
        {
            OutputPathTextBox.Text = dialog.FileName;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        NewProjectName = NewProjectNameTextBox.Text.Trim();
        ExistingProjectId = NewProjectName.Length > 0
            ? null
            : (ProjectComboBox.SelectedItem as QuotationProject)?.Id;
        OutputPath = OutputPathTextBox.Text.Trim();
        if (ExistingProjectId is null && NewProjectName.Length == 0)
        {
            MessageBox.Show("Selecione uma cotação existente ou informe o nome de uma nova.", "Importar cotação");
            return;
        }

        if (OutputPath.Length == 0)
        {
            MessageBox.Show("Escolha o arquivo Excel de saída antes de iniciar.", "Importar cotação");
            return;
        }

        DialogResult = true;
    }
}
