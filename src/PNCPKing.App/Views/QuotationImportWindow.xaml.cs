using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationImportWindow : Window
{
    private readonly string _defaultOutputName;

    public QuotationImportWindow(
        QuotationImportDocument document,
        IReadOnlyList<QuotationProject> projects,
        Guid? selectedProjectId,
        AdequacyWeights weights,
        string filterSummary)
    {
        InitializeComponent();
        Items = document.Items;
        Projects = projects;
        DataContext = this;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        var baseName = Path.GetFileNameWithoutExtension(document.SourcePath);
        NewProjectNameTextBox.Text = projects.Count == 0 ? baseName : string.Empty;
        _defaultOutputName = baseName + "-cotação.xlsx";
        var calls = document.Items.Sum(item => checked(item.BatchCount * 50));
        SummaryTextBlock.Text =
            $"{document.Items.Count:N0} item(ns), até {calls:N0} consultas de preços. " +
            $"Filtros: {filterSummary}. Pesos: {weights}. " +
            "A execução é sequencial e pode demorar conforme os disparos e contratos examinados.";
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
            DataGridColumnChooser.Show(button, ImportItemsGrid);
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
