using System.Diagnostics;
using PNCPKing.Core.Models;

namespace PNCPKing.Guard;

internal sealed class GuardForm : Form
{
    private readonly GuardSettingsService _settingsService;
    private readonly GuardLog _log;
    private readonly TextBox _plan = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly Label _worker = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "Nenhum plano configurado" };
    private readonly CheckBox _schedule = new() { Text = "Ativar tarefa automática (10 min após logon; repetir a cada 30 min)", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "Pronto." };
    private readonly Button _save = new() { Text = "Salvar configuração", AutoSize = true };
    private readonly Button _run = new() { Text = "Executar agora", AutoSize = true };
    private GuardSettings _settings = new();
    private GuardWorkerPlan? _selectedPlan;

    public GuardForm(GuardSettingsService settingsService, GuardLog log)
    {
        _settingsService = settingsService;
        _log = log;
        Text = "PNCP Guard";
        Width = 820;
        Height = 360;
        MinimumSize = new Size(680, 330);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        Shown += async (_, _) => await LoadSettingsAsync().ConfigureAwait(true);
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 7,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "PNCP Guard — coleta distribuída",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 3);
        layout.Controls.Add(new Label { Text = "Plano do trabalhador", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_plan, 1, 1);
        var browsePlan = new Button { Text = "Selecionar…", AutoSize = true };
        browsePlan.Click += SelectPlan_Click;
        layout.Controls.Add(browsePlan, 2, 1);
        layout.Controls.Add(new Label { Text = "Trabalhador", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_worker, 1, 2);
        layout.SetColumnSpan(_worker, 2);
        layout.Controls.Add(new Label { Text = "Raiz local do Google Drive", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_root, 1, 3);
        var browseRoot = new Button { Text = "Selecionar…", AutoSize = true };
        browseRoot.Click += SelectRoot_Click;
        layout.Controls.Add(browseRoot, 2, 3);
        layout.Controls.Add(_schedule, 1, 4);
        layout.SetColumnSpan(_schedule, 2);
        layout.Controls.Add(_status, 0, 5);
        layout.SetColumnSpan(_status, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };
        _run.Click += Run_Click;
        _save.Click += Save_Click;
        var logs = new Button { Text = "Abrir logs", AutoSize = true };
        logs.Click += (_, _) => OpenLogs();
        buttons.Controls.Add(_run);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(logs);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsService.LoadAsync().ConfigureAwait(true);
        _plan.Text = _settings.PlanPath ?? string.Empty;
        _root.Text = _settings.DriveRoot ?? string.Empty;
        _schedule.Checked = _settings.ScheduleEnabled;
        _worker.Text = string.IsNullOrWhiteSpace(_settings.WorkerName)
            ? "Nenhum plano configurado"
            : $"{_settings.WorkerName} ({_settings.WorkerId})";
        if (!string.IsNullOrWhiteSpace(_settings.PlanPath) && File.Exists(_settings.PlanPath))
        {
            try
            {
                _selectedPlan = await GuardFileCodec.ReadJsonAsync<GuardWorkerPlan>(_settings.PlanPath)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _status.Text = "O plano salvo não pôde ser lido: " + exception.Message;
            }
        }
    }

    private async void SelectPlan_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecione o plano do trabalhador",
            Filter = "Plano do PNCP Guard (*.pncpguardplan)|*.pncpguardplan",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var plan = await GuardFileCodec.ReadJsonAsync<GuardWorkerPlan>(dialog.FileName).ConfigureAwait(true);
            if (plan.Kind != GuardFormat.PlanKind || plan.Version != GuardFormat.Version)
            {
                throw new InvalidDataException("O arquivo não é um plano PNCP Guard v1.");
            }

            if (!string.IsNullOrWhiteSpace(_settings.WorkerId) && _settings.WorkerId != plan.Worker.Id)
            {
                throw new InvalidOperationException(
                    "Este PNCP Guard já adotou outro trabalhador. Para evitar sobreposição, o identificador não pode ser trocado.");
            }

            _selectedPlan = plan;
            _plan.Text = dialog.FileName;
            _worker.Text = $"{plan.Worker.Name} ({plan.Worker.Id}) — {plan.Contracts.Count:N0} contratação(ões)";
            if (string.IsNullOrWhiteSpace(_root.Text))
            {
                var campaignFolder = Directory.GetParent(dialog.FileName);
                var plansFolder = campaignFolder?.Parent;
                if (plansFolder?.Name.Equals("plans", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _root.Text = plansFolder.Parent?.FullName ?? string.Empty;
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Plano inválido", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectRoot_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecione a raiz local sincronizada pelo Google Drive",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_root.Text) ? _root.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _root.Text = dialog.SelectedPath;
        }
    }

    private async void Save_Click(object? sender, EventArgs e)
    {
        try
        {
            await SaveCoreAsync().ConfigureAwait(true);
            _status.Text = "Configuração salva e tarefa do Windows reconciliada.";
        }
        catch (Exception exception)
        {
            _log.Write("Falha ao salvar configuração: " + exception);
            MessageBox.Show(this, exception.Message, "PNCP Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void Run_Click(object? sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            await SaveCoreAsync().ConfigureAwait(true);
            var progress = new Progress<string>(message => _status.Text = message);
            var result = await new GuardRunner(_settingsService, _log)
                .RunAsync(_settings, progress)
                .ConfigureAwait(true);
            _status.Text = result.Message;
        }
        catch (Exception exception)
        {
            _log.Write("Execução manual encerrada com erro: " + exception);
            MessageBox.Show(this, exception.Message, "PNCP Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Execução encerrada com erro; consulte os logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveCoreAsync()
    {
        if (_selectedPlan is null || string.IsNullOrWhiteSpace(_plan.Text) || !File.Exists(_plan.Text))
        {
            throw new InvalidOperationException("Selecione um plano de trabalhador válido.");
        }

        if (string.IsNullOrWhiteSpace(_root.Text))
        {
            throw new InvalidOperationException("Selecione a raiz local do Google Drive.");
        }

        var root = Path.GetFullPath(_root.Text.Trim());
        Directory.CreateDirectory(root);
        if (!File.Exists(Path.Combine(root, "control.json")))
        {
            throw new InvalidOperationException("A raiz escolhida não contém control.json.");
        }

        _settings = new GuardSettings
        {
            WorkerId = _settings.WorkerId ?? _selectedPlan.Worker.Id,
            WorkerName = _settings.WorkerName ?? _selectedPlan.Worker.Name,
            PlanPath = Path.GetFullPath(_plan.Text),
            DriveRoot = root,
            ScheduleEnabled = _schedule.Checked
        };
        if (_settings.WorkerId != _selectedPlan.Worker.Id)
        {
            throw new InvalidOperationException("O plano selecionado não pertence ao trabalhador adotado por este computador.");
        }

        await new GuardScheduledTask().SetEnabledAsync(_settings.ScheduleEnabled).ConfigureAwait(true);
        await _settingsService.SaveAsync(_settings).ConfigureAwait(true);
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsService.LogPath)!);
        if (!File.Exists(_settingsService.LogPath))
        {
            File.WriteAllText(_settingsService.LogPath, string.Empty);
        }

        Process.Start(new ProcessStartInfo(_settingsService.LogPath) { UseShellExecute = true });
    }

    private void SetBusy(bool busy)
    {
        _run.Enabled = !busy;
        _save.Enabled = !busy;
        UseWaitCursor = busy;
    }
}
