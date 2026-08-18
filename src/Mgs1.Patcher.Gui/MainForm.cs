using Mgs1.Patcher.Core;
using Mgs1.Patcher.Gui.Logic;

namespace Mgs1.Patcher.Gui;

internal sealed class MainForm : Form
{
    private readonly string[] startupArguments;
    private readonly TextBox disc1Cue = NewPathBox();
    private readonly TextBox disc2Cue = NewPathBox();
    private readonly TextBox destination = NewPathBox();
    private readonly Label disc1State = NewStateLabel();
    private readonly Label disc2State = NewStateLabel();
    private readonly Label status = new()
    {
        AutoEllipsis = true,
        BorderStyle = BorderStyle.Fixed3D,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    private readonly Button apply = new() { Text = "Aplicar tradução", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancelar", AutoSize = true, Enabled = false };
    private readonly ToolStripMenuItem selectDisc1 = new("Selecionar CUE do &Disco 1...");
    private readonly ToolStripMenuItem selectDisc2 = new("Selecionar CUE do &Disco 2...");
    private readonly ToolStripMenuItem selectDestination = new("Escolher pasta de &destino...");
    private readonly List<Button> selectorButtons = [];
    private PatchWorkflowController? controller;
    private CancellationTokenSource? workCancellation;
    private bool busy;
    private bool closeRequested;

    public MainForm(string[] startupArguments)
    {
        this.startupArguments = startupArguments;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Control;
        ClientSize = new Size(612, 392);
        Font = new Font("Microsoft Sans Serif", 8.25F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MGS1 PT-BR — Aplicador de tradução";

        Controls.Add(BuildMenu());
        Controls.Add(BuildDiscGroup("disc1", "Disco 1", 42, disc1Cue, disc1State, () => SelectCueAsync("disc1")));
        Controls.Add(BuildDiscGroup("disc2", "Disco 2", 128, disc2Cue, disc2State, () => SelectCueAsync("disc2")));
        Controls.Add(BuildDestinationGroup());
        Controls.Add(BuildStatusArea());

        apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => RequestCancellation();
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnFormClosing;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Renderer = new ToolStripSystemRenderer() };
        var file = new ToolStripMenuItem("&Arquivo");
        selectDisc1.Click += async (_, _) => await SelectCueAsync("disc1");
        selectDisc2.Click += async (_, _) => await SelectCueAsync("disc2");
        selectDestination.Click += (_, _) => ChooseDestination();
        var exit = new ToolStripMenuItem("&Sair");
        exit.Click += (_, _) => Close();
        file.DropDownItems.AddRange([selectDisc1, selectDisc2, selectDestination, new ToolStripSeparator(), exit]);

        var help = new ToolStripMenuItem("A&juda");
        var howTo = new ToolStripMenuItem("&Como usar");
        howTo.Click += (_, _) => ShowHowTo();
        var about = new ToolStripMenuItem("&Sobre");
        about.Click += (_, _) => ShowAbout();
        help.DropDownItems.AddRange([howTo, about]);
        menu.Items.AddRange([file, help]);
        return menu;
    }

    private GroupBox BuildDiscGroup(
        string discId,
        string title,
        int top,
        TextBox cueBox,
        Label stateLabel,
        Func<Task> selectionAction)
    {
        var group = new GroupBox { Text = title, Left = 12, Top = top, Width = 588, Height = 78 };
        var cueLabel = new Label { Text = "Arquivo CUE:", Left = 12, Top = 23, Width = 76, TextAlign = ContentAlignment.MiddleLeft };
        cueBox.Left = 90;
        cueBox.Top = 20;
        cueBox.Width = 385;
        var choose = new Button { Text = "Procurar...", Left = 482, Top = 18, Width = 92 };
        choose.Click += async (_, _) => await selectionAction();
        selectorButtons.Add(choose);
        stateLabel.Left = 90;
        stateLabel.Top = 47;
        stateLabel.Width = 484;
        group.Controls.AddRange([cueLabel, cueBox, choose, stateLabel]);
        ConfigureCueDropTarget(group, discId);
        return group;
    }

    private void ConfigureCueDropTarget(Control target, string discId)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, eventArgs) =>
        {
            eventArgs.Effect = CanAcceptCueDrop(eventArgs.Data)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        target.DragDrop += async (_, eventArgs) => await HandleCueDropAsync(discId, eventArgs.Data);
        foreach (Control child in target.Controls)
        {
            ConfigureCueDropTarget(child, discId);
        }
    }

    private GroupBox BuildDestinationGroup()
    {
        var group = new GroupBox { Text = "Pasta de destino", Left = 12, Top = 214, Width = 588, Height = 58 };
        var label = new Label { Text = "Destino:", Left = 12, Top = 24, Width = 76, TextAlign = ContentAlignment.MiddleLeft };
        destination.Left = 90;
        destination.Top = 20;
        destination.Width = 385;
        var choose = new Button { Text = "Procurar...", Left = 482, Top = 18, Width = 92 };
        choose.Click += (_, _) => ChooseDestination();
        selectorButtons.Add(choose);
        group.Controls.AddRange([label, destination, choose]);
        return group;
    }

    private Control BuildStatusArea()
    {
        var panel = new Panel { Left = 12, Top = 284, Width = 588, Height = 96 };
        status.Left = 0;
        status.Top = 0;
        status.Width = 588;
        status.Height = 32;
        progress.Left = 0;
        progress.Top = 42;
        progress.Width = 390;
        progress.Height = 18;
        apply.Left = 400;
        apply.Top = 39;
        cancel.Left = 496;
        cancel.Top = 39;
        panel.Controls.AddRange([status, progress, apply, cancel]);
        return panel;
    }

    private async Task InitializeAsync()
    {
        BeginBusy(cancellable: false);
        try
        {
            ApplicationDataRoot root = ApplicationDataRoot.Resolve(startupArguments);
            controller = await PatchWorkflowController.CreateAsync(root);
            controller.StateChanged += OnControllerStateChanged;
            status.Text = "Selecione ou arraste os CUEs limpos dos Discos 1 e 2.";
        }
        catch (Exception exception) when (exception is ApplicationDataException or PatcherException or IOException or UnauthorizedAccessException)
        {
            PatchUserMessage message = PatchUserMessages.ForStartupFailure(exception);
            status.Text = message.Text;
            MessageBox.Show(this, message.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task SelectCueAsync(string discId)
    {
        if (controller is null || busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            AutoUpgradeEnabled = false,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "Arquivos CUE (*.cue)|*.cue|Todos os arquivos (*.*)|*.*",
            FilterIndex = 1,
            Multiselect = false,
            RestoreDirectory = true,
            Title = discId == "disc1" ? "Selecionar CUE do Disco 1" : "Selecionar CUE do Disco 2",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await CheckCueAsync(discId, dialog.FileName);
    }

    private bool CanAcceptCueDrop(IDataObject? data) =>
        controller is not null
        && !busy
        && CueDropPolicy.Evaluate(data?.GetData(DataFormats.FileDrop) as string[]).Kind == CueDropCandidateKind.Accepted;

    private async Task HandleCueDropAsync(string discId, IDataObject? data)
    {
        if (controller is null || busy)
        {
            return;
        }

        CueDropCandidate candidate = CueDropPolicy.Evaluate(data?.GetData(DataFormats.FileDrop) as string[]);
        if (candidate.Kind != CueDropCandidateKind.Accepted || candidate.CuePath is null)
        {
            return;
        }

        await CheckCueAsync(discId, candidate.CuePath);
    }

    private async Task CheckCueAsync(string discId, string cuePath)
    {
        if (controller is null || busy)
        {
            return;
        }

        BeginBusy(cancellable: true);
        try
        {
            status.Text = discId == "disc1" ? "Conferindo Disco 1 por hash..." : "Conferindo Disco 2 por hash...";
            DiscSelectionState selection = await controller.SelectCueAsync(discId, cuePath, workCancellation!.Token);
            ApplySelectionToView(selection);
            PatchUserMessage message = PatchUserMessages.ForSelection(selection);
            status.Text = message.Text;
            if (selection.Kind is not DiscSelectionKind.Ready and not DiscSelectionKind.Checking)
            {
                MessageBox.Show(this, message.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            status.Text = "A conferência foi cancelada. Os originais não foram alterados e nenhuma saída foi criada.";
        }
        finally
        {
            EndBusy();
        }
    }

    private void ChooseDestination()
    {
        if (busy)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            AutoUpgradeEnabled = false,
            Description = "Escolha a pasta para as imagens PT-BR verificadas.",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            destination.Text = dialog.SelectedPath;
            RefreshButtons();
        }
    }

    private async Task ApplyAsync()
    {
        if (controller is null || busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(destination.Text))
        {
            MessageBox.Show(this, "Escolha a pasta de destino antes de aplicar a tradução.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BeginBusy(cancellable: true);
        try
        {
            var uiProgress = new Progress<PatchProgress>(value =>
            {
                status.Text = FormatProgress(value);
                UpdateProgress(value);
            });
            PatchWorkflowResult result = await controller.ApplyAsync(
                destination.Text,
                uiProgress,
                workCancellation!.Token);
            status.Text = result.Message.Text;
            PrepareApplyResultModal(result.Succeeded);
            MessageBoxIcon icon = ResultIcon(result);
            MessageBox.Show(this, result.Message.Text, Text, MessageBoxButtons.OK, icon);
        }
        finally
        {
            EndBusy();
        }
    }

    private void OnControllerStateChanged(object? sender, PatchWorkflowState state)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnControllerStateChanged(sender, state)));
            return;
        }

        ApplySelectionToView(state.Disc1);
        ApplySelectionToView(state.Disc2);
        if (state.Progress is not null)
        {
            status.Text = FormatProgress(state.Progress);
            UpdateProgress(state.Progress);
        }
    }

    private void ApplySelectionToView(DiscSelectionState selection)
    {
        TextBox cue = selection.RequestedDiscId == "disc1" ? disc1Cue : disc2Cue;
        Label state = selection.RequestedDiscId == "disc1" ? disc1State : disc2State;
        cue.Text = selection.CuePath ?? string.Empty;
        state.Text = selection.Kind == DiscSelectionKind.Unselected
            ? "Aguardando seleção — arraste um CUE aqui."
            : PatchUserMessages.ForSelection(selection).Text;
    }

    private void BeginBusy(bool cancellable)
    {
        busy = true;
        workCancellation = new CancellationTokenSource();
        selectDisc1.Enabled = false;
        selectDisc2.Enabled = false;
        selectDestination.Enabled = false;
        foreach (Button button in selectorButtons)
        {
            button.Enabled = false;
        }
        apply.Enabled = false;
        cancel.Enabled = cancellable;
        progress.Style = cancellable ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!cancellable)
        {
            progress.Value = 0;
        }
    }

    private void EndBusy()
    {
        workCancellation?.Dispose();
        workCancellation = null;
        busy = false;
        progress.Style = ProgressBarStyle.Continuous;
        RefreshButtons();
        if (closeRequested)
        {
            BeginInvoke(new Action(Close));
        }
    }

    private void RefreshButtons()
    {
        bool ready = controller is not null
            && controller.State.Disc1.Kind == DiscSelectionKind.Ready
            && controller.State.Disc2.Kind == DiscSelectionKind.Ready
            && !string.IsNullOrWhiteSpace(destination.Text);
        selectDisc1.Enabled = !busy && controller is not null;
        selectDisc2.Enabled = !busy && controller is not null;
        selectDestination.Enabled = !busy && controller is not null;
        foreach (Button button in selectorButtons)
        {
            button.Enabled = !busy && controller is not null;
        }
        apply.Enabled = !busy && ready;
        cancel.Enabled = busy && workCancellation is not null;
    }

    private void RequestCancellation()
    {
        if (workCancellation is { IsCancellationRequested: false })
        {
            cancel.Enabled = false;
            status.Text = "Cancelamento solicitado; aguardando a limpeza segura.";
            workCancellation.Cancel();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!busy)
        {
            return;
        }

        eventArgs.Cancel = true;
        closeRequested = true;
        RequestCancellation();
    }

    private void UpdateProgress(PatchProgress patchProgress)
    {
        if (patchProgress.TotalBytes <= 0)
        {
            progress.Style = ProgressBarStyle.Marquee;
            return;
        }

        progress.Style = ProgressBarStyle.Continuous;
        long scaled = Math.Clamp(patchProgress.CompletedBytes * 1000L / patchProgress.TotalBytes, 0, 1000);
        progress.Value = (int)scaled;
    }

    private void PrepareApplyResultModal(bool succeeded)
    {
        cancel.Enabled = false;
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = succeeded ? progress.Maximum : progress.Minimum;
    }

    private static MessageBoxIcon ResultIcon(PatchWorkflowResult result) => result.Succeeded
        ? MessageBoxIcon.Information
        : result.Message.Category is PatchUserErrorCategory.ApplicationPayload or PatchUserErrorCategory.UnexpectedIo
            ? MessageBoxIcon.Error
            : MessageBoxIcon.Warning;

    private static string FormatProgress(PatchProgress value) => value.Phase switch
    {
        PatchProgressPhase.ValidatingInputs => $"Conferindo a imagem: {value.Item}",
        PatchProgressPhase.ValidatingPatches => "Conferindo os dados da aplicação...",
        PatchProgressPhase.Preflight => "Verificando espaço livre e segurança do destino...",
        PatchProgressPhase.ApplyingBin => "Aplicando a tradução à BIN...",
        PatchProgressPhase.ApplyingCue => "Aplicando a tradução ao CUE...",
        PatchProgressPhase.ReverifyingInputs => "Confirmando que os originais foram preservados...",
        PatchProgressPhase.Publishing => "Publicando os arquivos verificados...",
        PatchProgressPhase.Completed => "Verificação concluída.",
        _ => "Processando...",
    };

    private void ShowHowTo() => MessageBox.Show(
        this,
        "1. Selecione o CUE limpo de cada disco com Procurar ou arraste-o para o painel correspondente. A BIN indicada no CUE é localizada na mesma pasta e conferida por hash.\n\n" +
        "2. Escolha uma pasta de destino vazia. Cada par usará o nome do CUE original com o sufixo (PT-BR).\n\n" +
        "3. Clique em Aplicar tradução. Não há substituição, continuação nem aceitação de outras revisões.",
        "Como usar",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);

    private void ShowAbout() => MessageBox.Show(
        this,
        "MGS1 PT-BR — aplicador de tradução\n\n" +
        "Interface Windows clássica sobre um núcleo que valida hashes, espaço, payloads e saídas antes de publicar.\n\n" +
        "Aceitação limitada aos checkpoints exatos definidos no manifest; não é uma declaração de tradução globalmente final.",
        "Sobre",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);

    private static TextBox NewPathBox() => new()
    {
        BorderStyle = BorderStyle.Fixed3D,
        ReadOnly = true,
        TabStop = false,
    };

    private static Label NewStateLabel() => new()
    {
        AutoEllipsis = true,
        ForeColor = SystemColors.ControlText,
        Text = "Aguardando seleção — arraste um CUE aqui.",
        TextAlign = ContentAlignment.MiddleLeft,
    };
}
