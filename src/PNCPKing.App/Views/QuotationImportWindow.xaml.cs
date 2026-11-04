using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.App.Views;

public partial class QuotationImportWindow : Window
{
    private readonly string _defaultOutputName;
    private readonly DataGridColumnLayoutService _columnLayouts;
    private readonly bool _externalAi;

    public QuotationImportWindow(
        QuotationImportDocument document,
        IReadOnlyList<QuotationProject> projects,
        Guid? selectedProjectId,
        AdequacyWeights weights,
        string filterSummary,
        DataGridColumnLayoutService columnLayouts)
    {
        _columnLayouts = columnLayouts;
        _externalAi = document.IsExternalAi;
        InitializeComponent();
        MonitorAwareWindowBehavior.Attach(this);
        columnLayouts.Register("quotation-import-preview", ImportItemsGrid);
        Closed += (_, _) => _columnLayouts.Unregister(ImportItemsGrid);
        Items = document.Items;
        Projects = projects;
        DataContext = this;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        var baseName = Path.GetFileNameWithoutExtension(document.SourcePath);
        NewProjectNameTextBox.Text = projects.Count == 0 ? baseName : string.Empty;
        _defaultOutputName = baseName + (_externalAi ? "-resultado-IA.zip" : "-cotação.xlsx");
        FullDescriptionColumn.Visibility = _externalAi ? Visibility.Visible : Visibility.Collapsed;
        if (_externalAi)
        {
            Title = "Importar itens para coleta com IA externa";
            OutputLabel.Text = "Resultado para a IA";
            var criteria = document.Items.Sum(item => SearchText.ParseMainCriteria(item.SearchText).Count);
            var occurrences = document.Items.Sum(item =>
                (long)item.BatchCount * ItemSearchDefaults.ContractsPerBatch * SearchText.ParseMainCriteria(item.SearchText).Count);
            SummaryTextBlock.Text =
                $"{document.Items.Count:N0} item(ns), {criteria:N0} pesquisas alternativas; até {occurrences:N0} ocorrências antes de remover repetidos. " +
                $"Filtros: {filterSummary}. Pesos: {weights}. " +
                "O + separa pesquisas, como na busca normal. A coluna G define páginas por expressão. " +
                "O Excel cria os itens da cotação e a coleta gera um resultado para a IA, sem baixar documentos. " +
                "A faixa de preço será conferida na seleção, após eventuais conversões de unidade.";
        }
        else
        {
            var localPrices = document.Items.Sum(item => checked(item.BatchCount * ItemSearchDefaults.ContractsPerBatch));
            SummaryTextBlock.Text =
                $"{document.Items.Count:N0} item(ns), até {localPrices:N0} preços locais exibidos. " +
                $"Filtros: {filterSummary}. Pesos: {weights}. " +
                "A coluna G define quantas páginas de até 50 preços salvos serão lidas por item.";
        }
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
            _columnLayouts.ShowChooser(this, ImportItemsGrid);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = _externalAi ? "Salvar resultado da coleta para IA" : "Salvar cotação automática",
            Filter = _externalAi ? "Resultado para IA (*.zip)|*.zip" : "Planilha do Excel (*.xlsx)|*.xlsx",
            DefaultExt = _externalAi ? ".zip" : ".xlsx",
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
            MessageBox.Show(_externalAi ? "Escolha o arquivo de resultado antes de iniciar." :
                "Escolha o arquivo Excel de saída antes de iniciar.", "Importar cotação");
            return;
        }

        DialogResult = true;
    }
}
