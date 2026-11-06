namespace XGDMaker;

internal sealed class MainForm : Form
{
    private const int ColumnGame = 0, ColumnType = 1, ColumnOutput = 2, ColumnStatus = 3;

    private static readonly Color DarkBg = Color.FromArgb(30, 30, 36);
    private static readonly Color DarkInput = Color.FromArgb(45, 45, 52);
    private static readonly Color DarkFg = Color.FromArgb(230, 230, 235);
    private static readonly Color DarkBorder = Color.FromArgb(70, 70, 80);
    private static readonly Color DarkLogBg = Color.FromArgb(20, 22, 27);

    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputFolder = new() { Dock = DockStyle.Fill, PlaceholderText = "Empty = next to each game folder / ISO" };
    // Padding and SS/DMI/PFI embedding are always on for retail-style discs (hidden from the UI).
    private readonly CheckBox _randomFill = new() { AutoSize = true, Checked = true, Text = "Padding" };
    private readonly CheckBox _embedSs = new() { AutoSize = true, Checked = true, Text = "SS" };
    private readonly CheckBox _overwrite = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Overwrite existing files"
    };
    private readonly CheckBox _extractIso = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Extract ISO (list .iso files to extract instead of extracted games to build)"
    };
    private readonly CheckBox _darkTheme = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Dark theme"
    };
    private readonly CheckBox _deleteSource = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Delete the extracted game folder once the ISO is verified identical to it"
    };
    private readonly ListView _games = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
    };
    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(20, 22, 27),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9.5f),
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Button _checkAll = new() { Text = "Check all", AutoSize = true };
    private readonly Button _checkNone = new() { Text = "Uncheck all", AutoSize = true };
    private readonly Button _extractSsButton = new() { Text = "Extract Disc Sectors (SS, PFI, DMI, .DVD)", AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _buildButton = new() { Text = "Build checked", AutoSize = true, Height = 36 };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Height = 36, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Value = 0 };
    private readonly Label _progressText = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 10, 8, 0) };
    private readonly Label _headerLabel = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        Padding = new Padding(0, 0, 0, 8)
    };
    private readonly Label _cleanupLabel = new();
    private List<ScanEntry> _entries = new();
    private CancellationTokenSource? _cancel;

    public MainForm()
    {
        Text = "XGDM - Xbox Game Disc Maker";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 680);
        Size = new Size(1100, 820);
        Font = new Font("Segoe UI", 9f);
        Icon = XGDM.Properties.Resources.XGDM;

        _games.Columns.Add("Game", 260);
        _games.Columns.Add("Type", 110);
        _games.Columns.Add("Output", 370);
        _games.Columns.Add("Status", 300);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _headerLabel.Text = "Find extracted Xbox 360 games and .iso files: build retail-style ISOs or extract them";
        root.Controls.Add(_headerLabel, 0, 0);

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 7, Padding = new Padding(0, 0, 0, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(fields, 0, "Games", _root, "Browse…", BrowseRoot);
        AddPathRow(fields, 1, "Output folder", _outputFolder, "Browse…", BrowseOutputFolder);
        fields.Controls.Add(MakeLabel("Mode"), 0, 2);
        fields.Controls.Add(_extractIso, 1, 2);
        fields.SetColumnSpan(_extractIso, 2);
        fields.Controls.Add(MakeLabel("Theme"), 0, 3);
        fields.Controls.Add(_darkTheme, 1, 3);
        fields.SetColumnSpan(_darkTheme, 2);
        fields.Controls.Add(MakeLabel("Overwrite"), 0, 4);
        fields.Controls.Add(_overwrite, 1, 4);
        fields.SetColumnSpan(_overwrite, 2);
        _cleanupLabel.Text = "Cleanup";
        _cleanupLabel.AutoSize = true;
        _cleanupLabel.Anchor = AnchorStyles.Left;
        _cleanupLabel.Margin = new Padding(0, 7, 8, 7);
        fields.Controls.Add(_cleanupLabel, 0, 5);
        fields.Controls.Add(_deleteSource, 1, 5);
        fields.SetColumnSpan(_deleteSource, 2);
        var note = new Label
        {
            AutoSize = true,
            Text = "SS, DMI and PFI are embedded in the ISO; padding is always on. No separate .ss/.dvd files are written.",
            ForeColor = Color.SteelBlue
        };
        fields.Controls.Add(note, 1, 6);
        fields.SetColumnSpan(note, 2);
        root.Controls.Add(fields, 0, 1);

        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        listButtons.Controls.AddRange(new Control[]
        {
            _checkAll, _checkNone, _refreshButton, _extractSsButton
        });
        listPanel.Controls.Add(listButtons, 0, 0);
        listPanel.Controls.Add(_games, 0, 1);
        root.Controls.Add(listPanel, 0, 2);

        root.Controls.Add(_log, 0, 3);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Padding = new Padding(0, 10, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_buildButton, 0, 0);
        actions.Controls.Add(_cancelButton, 1, 0);
        actions.Controls.Add(_progress, 2, 0);
        actions.Controls.Add(_progressText, 3, 0);
        var close = new Button { Text = "Close", AutoSize = true, Height = 36 };
        close.Click += (_, _) => Close();
        actions.Controls.Add(close, 4, 0);
        root.Controls.Add(actions, 0, 4);

        Controls.Add(root);

        _root.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) _ = ScanAsync();
        };
        _checkAll.Click += (_, _) => SetAllChecked(true);
        _checkNone.Click += (_, _) => SetAllChecked(false);
        _refreshButton.Click += async (_, _) => await ScanAsync();
        _extractSsButton.Click += async (_, _) => await ExtractSsFromXisosAsync();
        _buildButton.Click += async (_, _) => await ProcessCheckedAsync();
        _cancelButton.Click += (_, _) =>
        {
            _cancel?.Cancel();
            _cancelButton.Enabled = false;
            AppendLog("Cancelling: the current operation stops now (an ISO being written is deleted) and nothing else is started.");
        };
        _extractIso.CheckedChanged += (_, _) =>
        {
            RefreshList();
            _buildButton.Text = _extractIso.Checked ? "Extract checked" : "Build checked";
            _cleanupLabel.Visible = !_extractIso.Checked;
            _deleteSource.Visible = !_extractIso.Checked;
        };
        _darkTheme.CheckedChanged += (_, _) => ApplyTheme(_darkTheme.Checked);
        _outputFolder.TextChanged += (_, _) => RefreshOutputColumn();
        _games.ItemCheck += (_, e) =>
        {
            if (e.NewValue == CheckState.Checked && _games.Items[e.Index].Tag is ScanEntry { IsIso: false, Type: "?" })
                e.NewValue = CheckState.Unchecked;
        };

        ApplyTheme(_darkTheme.Checked);

        AppendLog("Choose a folder: extracted games (folders with default.xex) and .iso files below it are listed.");
        AppendLog("Tick \"Extract ISO\" to extract the listed .iso files; leave it unticked to build the listed games into ISOs.");
        AppendLog("Source files are never modified. Existing outputs are skipped unless \"Overwrite existing files\" is ticked.");
        AppendLog("SS, DMI and PFI are generated and embedded automatically (no abgx360 required).");
    }

    private void ApplyTheme(bool dark)
    {
        if (dark) ApplyDarkTheme(this);
        else ApplyLightTheme(this);
    }

    private void ApplyDarkTheme(Control control)
    {
        switch (control)
        {
            case Form or TableLayoutPanel or FlowLayoutPanel or Panel:
                control.BackColor = DarkBg;
                control.ForeColor = DarkFg;
                break;
            case Label or CheckBox:
                control.BackColor = DarkBg;
                control.ForeColor = DarkFg;
                break;
            case ListView:
                control.BackColor = DarkInput;
                control.ForeColor = DarkFg;
                break;
            case TextBox:
                control.BackColor = DarkInput;
                control.ForeColor = DarkFg;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = DarkBorder;
                button.BackColor = DarkInput;
                button.ForeColor = DarkFg;
                button.UseVisualStyleBackColor = false;
                break;
            case RichTextBox rich:
                rich.BackColor = DarkLogBg;
                rich.ForeColor = Color.Gainsboro;
                break;
            case ProgressBar:
                control.BackColor = DarkInput;
                break;
        }
        foreach (Control child in control.Controls) ApplyDarkTheme(child);
    }

    private void ApplyLightTheme(Control control)
    {
        switch (control)
        {
            case Form or TableLayoutPanel or FlowLayoutPanel or Panel:
                control.BackColor = SystemColors.Control;
                control.ForeColor = SystemColors.ControlText;
                break;
            case Label or CheckBox:
                control.BackColor = SystemColors.Control;
                control.ForeColor = SystemColors.ControlText;
                break;
            case ListView:
                control.BackColor = SystemColors.Window;
                control.ForeColor = SystemColors.WindowText;
                break;
            case TextBox:
                control.BackColor = SystemColors.Window;
                control.ForeColor = SystemColors.WindowText;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Standard;
                button.BackColor = SystemColors.Control;
                button.ForeColor = SystemColors.ControlText;
                button.UseVisualStyleBackColor = true;
                break;
            case RichTextBox rich:
                rich.BackColor = SystemColors.Window;
                rich.ForeColor = SystemColors.WindowText;
                break;
            case ProgressBar:
                control.BackColor = SystemColors.Control;
                break;
        }
        foreach (Control child in control.Controls) ApplyLightTheme(child);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 8, 7)
    };

    private static void AddPathRow(TableLayoutPanel panel, int row, string label, TextBox box, string buttonText, Action click)
    {
        panel.Controls.Add(MakeLabel(label), 0, row);
        panel.Controls.Add(box, 1, row);
        var button = new Button { Text = buttonText, AutoSize = true };
        button.Click += (_, _) => click();
        panel.Controls.Add(button, 2, row);
    }

    private async void BrowseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder that contains extracted games and/or .iso files",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (Directory.Exists(_root.Text)) dialog.InitialDirectory = _root.Text;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _root.Text = dialog.SelectedPath;
        await ScanAsync();
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Folder for the output (leave empty to put each output next to its source)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_outputFolder.Text)) dialog.InitialDirectory = _outputFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputFolder.Text = dialog.SelectedPath;
    }

    private async Task ScanAsync()
    {
        var rootFolder = _root.Text.Trim();
        if (!Directory.Exists(rootFolder))
        {
            MessageBox.Show(this, "Choose an existing folder first.", "Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, scanning: true);
        _games.Items.Clear();
        _cancel = new CancellationTokenSource();
        try
        {
            AppendLog($"Scanning {rootFolder} ...");
            var token = _cancel.Token;
            _entries = await Task.Run(() => GameScanner.Scan(rootFolder, AppendLog, token), token);
            RefreshList();
            _games.ShowItemToolTips = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Scan cancelled.");
        }
        finally
        {
            SetBusy(false, scanning: true);
        }
    }

    private void RefreshList()
    {
        var showIsos = _extractIso.Checked;
        _games.BeginUpdate();
        _games.Items.Clear();
        foreach (var entry in _entries)
        {
            if (entry.IsIso != showIsos) continue;
            var item = new ListViewItem(new[]
            {
                entry.DisplayName,
                entry.Type,
                GameScanner.OutputPathFor(entry, _outputFolder.Text),
                entry.Status
            })
            {
                Tag = entry,
                ToolTipText = entry.Path
            };
            if (!entry.IsIso && entry.Type == "?") item.ForeColor = Color.Firebrick;
            _games.Items.Add(item);
            item.Checked = entry.IsIso || entry.Type != "?";
        }
        _games.EndUpdate();
        if (_games.Items.Count == 0)
            AppendLog(showIsos ? "No .iso file was found under the selected folder." : "No folder containing default.xex was found.");
    }

    private void RefreshOutputColumn()
    {
        foreach (ListViewItem item in _games.Items)
        {
            if (item.Tag is ScanEntry entry) item.SubItems[ColumnOutput].Text = GameScanner.OutputPathFor(entry, _outputFolder.Text);
        }
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in _games.Items) item.Checked = value;
    }

    private async Task ProcessCheckedAsync()
    {
        var queue = _games.CheckedItems.Cast<ListViewItem>().ToList();
        if (queue.Count == 0)
        {
            MessageBox.Show(this, "Check at least one item in the list.",
                _extractIso.Checked ? "Extract" : "Build", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_extractIso.Checked)
            await ExtractCheckedAsync(queue);
        else
            await BuildCheckedAsync(queue);
    }

    private async Task BuildCheckedAsync(List<ListViewItem> queue)
    {
        var randomFill = _randomFill.Checked;
        var embedSs = _embedSs.Checked;
        var overwrite = _overwrite.Checked;
        var deleteSource = _deleteSource.Checked;
        if (deleteSource && MessageBox.Show(this,
                "After each ISO is built, the program will compare it byte for byte with its game folder and, only if they " +
                "are identical, PERMANENTLY delete the game folder (it does not go to the Recycle Bin).\n\nContinue?",
                "Delete source folders", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var outputFolder = _outputFolder.Text.Trim();

        SetBusy(true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _progress.Maximum = queue.Count;
        _progress.Value = 0;
        int passed = 0, failed = 0, skipped = 0;

        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                var item = queue[i];
                var entry = (ScanEntry)item.Tag!;
                var output = GameScanner.OutputPathFor(entry, outputFolder);
                _progressText.Text = $"{i + 1} / {queue.Count}";

                if (token.IsCancellationRequested)
                {
                    SetStatus(item, "Not started (cancelled)", Color.DimGray);
                    skipped++;
                    continue;
                }

                AppendLog("");
                AppendLog($"===== [{i + 1}/{queue.Count}] {entry.DisplayName} =====");
                if (File.Exists(output) && !overwrite)
                {
                    SetStatus(item, "Skipped: ISO already exists", Color.DimGray);
                    AppendLog($"Skipped: {output} already exists.");
                    skipped++;
                    _progress.Value = i + 1;
                    continue;
                }

                SetStatus(item, entry.GodXblaKind is not null ? "Extracting…" : "Building…", SystemColors.ControlText);
                _games.EnsureVisible(item.Index);
                string? tempSource = null;
                try
                {
                    var sourceFolder = entry.Path;
                    if (entry.GodXblaKind is not null)
                    {
                        var pkg = new GodXblaPackage(entry.Path, entry.DisplayName, entry.GodXblaKind, entry.Status);
                        tempSource = Path.Combine(Path.GetTempPath(), "XGDM", entry.DisplayName + "_" + Guid.NewGuid().ToString("N")[..8]);
                        await Task.Run(() => GodXblaExtractor.Extract(pkg, tempSource, AppendLog, token), token);
                        sourceFolder = tempSource;
                    }

                    var result = await IsoBuilder.BuildAsync(
                        new BuildRequest(sourceFolder, output, randomFill, embedSs, overwrite), AppendLog, token);
                    var ssEmbedded = result.SsEmbedded;
                    var ssIsV2 = result.SsIsV2;

                    if (!result.Verified)
                    {
                        SetStatus(item, "ERROR - verification after rebuild FAILED", Color.Firebrick);
                        failed++;
                        _progress.Value = i + 1;
                        continue;
                    }

                    var sourceNote = "";
                    if (deleteSource && entry.GodXblaKind is null)
                        sourceNote = await DeleteSourceIfIdenticalAsync(entry.Path, output, token);

                    if (!ssEmbedded)
                        SetStatus(item, $"ERROR – could not read the Media ID{sourceNote}", Color.DarkOrange);
                    else
                        SetStatus(item, $"XISO OK – SS v{(ssIsV2 ? 2 : 1)} | DMI | PFI | .DVD restored{sourceNote}", Color.ForestGreen);
                    passed++;
                }
                catch (OperationCanceledException)
                {
                    SetStatus(item, "Cancelled", Color.DimGray);
                    AppendLog("Cancelled.");
                    skipped++;
                }
                catch (Exception ex)
                {
                    SetStatus(item, "Error: " + ex.Message, Color.Firebrick);
                    AppendLog($"ERROR: {ex.Message}");
                    failed++;
                }
                finally
                {
                    if (tempSource is not null)
                    {
                        try { Directory.Delete(tempSource, recursive: true); } catch { /* best effort */ }
                    }
                }
                _progress.Value = i + 1;
            }
        }
        finally
        {
            SetBusy(false);
            _progressText.Text = "";
        }

        var summary = $"Finished: {passed} built, {failed} failed, {skipped} skipped.";
        AppendLog(summary);
        MessageBox.Show(this, summary, "Batch complete", MessageBoxButtons.OK,
            failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private async Task ExtractCheckedAsync(List<ListViewItem> queue)
    {
        var outputFolder = _outputFolder.Text.Trim();
        var overwrite = _overwrite.Checked;
        SetBusy(true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _progress.Maximum = queue.Count;
        _progress.Value = 0;
        int done = 0, failed = 0;
        try
        {
            for (var i = 0; i < queue.Count && !token.IsCancellationRequested; i++)
            {
                var item = queue[i];
                var iso = ((ScanEntry)item.Tag!).Path;
                _progressText.Text = $"{i + 1} / {queue.Count}";
                AppendLog("");
                AppendLog($"===== Extract [{i + 1}/{queue.Count}] {Path.GetFileName(iso)} =====");
                var parent = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(iso)! : outputFolder;
                var name = Path.GetFileNameWithoutExtension(iso);
                var destination = Path.Combine(parent, name);
                if (!overwrite && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    destination = Path.Combine(parent, name + " (extracted)");
                SetStatus(item, "Extracting…", SystemColors.ControlText);
                _games.EnsureVisible(item.Index);
                try
                {
                    await XdvdfsExtractor.ExtractAsync(iso, destination, overwrite, AppendLog, token);
                    SetStatus(item, "Extracted", Color.ForestGreen);
                    done++;
                }
                catch (OperationCanceledException)
                {
                    AppendLog("Cancelled. Files already extracted for this ISO are left in place.");
                }
                catch (Exception ex)
                {
                    SetStatus(item, "Error: " + ex.Message, Color.Firebrick);
                    AppendLog($"ERROR: {ex.Message}");
                    failed++;
                }
                _progress.Value = i + 1;
            }
        }
        finally
        {
            SetBusy(false);
            _progressText.Text = "";
        }

        var summary = $"Extraction finished: {done} extracted, {failed} failed.";
        AppendLog(summary);
        MessageBox.Show(this, summary, "Extract ISOs", MessageBoxButtons.OK,
            failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private async Task ExtractSsFromXisosAsync()
    {
        var root = _root.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Choose the Games folder first.", "Extract SS + .DVD", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppendLog($"Searching {root} for Xbox 360 ISOs...");
        List<string> isos = await FindIsoFilesAsync(root);
        if (isos.Count == 0)
        {
            MessageBox.Show(this, "No ISO file was found in that folder.", "Extract SS + .DVD", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppendLog(isos.Count == 1
            ? "Found 1 ISO file; extracting the SS from it..."
            : $"Found {isos.Count} ISO files; extracting the SS from each...");
        await ExtractStealthFromIsosAsync(isos.ToArray());
    }

    private async Task<List<string>> FindIsoFilesAsync(string root)
    {
        SetBusy(true, scanning: true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        try
        {
            return await Task.Run(() => GameScanner.Scan(root, _ => { }, token).Where(e => e.IsIso).Select(e => e.Path).ToList(), token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Search cancelled.");
            return new List<string>();
        }
        finally
        {
            SetBusy(false, scanning: true);
        }
    }

    private async Task ExtractStealthFromIsosAsync(string[] isos)
    {
        SetBusy(true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _progress.Maximum = isos.Length;
        _progress.Value = 0;
        int done = 0, failed = 0;
        try
        {
            for (var i = 0; i < isos.Length && !token.IsCancellationRequested; i++)
            {
                var iso = isos[i];
                _progressText.Text = $"{i + 1} / {isos.Length}";
                AppendLog("");
                AppendLog($"===== Extract SS [{i + 1}/{isos.Length}] {Path.GetFileName(iso)} =====");
                try
                {
                    await XdvdfsExtractor.ExtractStealthAsync(iso, AppendLog, token);
                    done++;
                }
                catch (OperationCanceledException)
                {
                    AppendLog("Cancelled.");
                }
                catch (Exception ex)
                {
                    AppendLog($"ERROR: {ex.Message}");
                    failed++;
                }
                _progress.Value = i + 1;
            }
        }
        finally
        {
            SetBusy(false);
            _progressText.Text = "";
        }

        var summary = $"SS extraction finished: {done} extracted, {failed} failed.";
        AppendLog(summary);
        MessageBox.Show(this, summary, "Extract SS + .dvd", MessageBoxButtons.OK,
            failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private async Task<string> DeleteSourceIfIdenticalAsync(string folder, string iso, CancellationToken token)
    {
        var fullFolder = Path.GetFullPath(folder).TrimEnd('\\');
        if (Path.GetPathRoot(fullFolder)?.TrimEnd('\\') == fullFolder)
        {
            AppendLog("Source folder kept: it is a drive root.");
            return ", source kept";
        }
        if (!File.Exists(Path.Combine(fullFolder, "default.xex")))
        {
            AppendLog("Source folder kept: it no longer looks like a game folder.");
            return ", source kept";
        }

        AppendLog($"Comparing {Path.GetFileName(iso)} with {fullFolder} before deleting it...");
        var difference = await XdvdfsExtractor.CompareWithFolderAsync(iso, fullFolder, AppendLog, token);
        if (difference is not null)
        {
            AppendLog($"Source folder kept: {difference}.");
            return ", source kept (not identical)";
        }
        try
        {
            foreach (var entry in new DirectoryInfo(fullFolder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(fullFolder, recursive: true);
            AppendLog($"Source folder permanently deleted: {fullFolder}");
            return ", source deleted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Source folder not (fully) deleted: {ex.Message}");
            return ", source delete failed (see log)";
        }
    }

    private void SetStatus(ListViewItem item, string text, Color color)
    {
        item.SubItems[ColumnStatus].Text = text;
        item.ForeColor = color;
    }

    private void SetBusy(bool busy, bool scanning = false)
    {
        _buildButton.Enabled = !busy;
        _checkAll.Enabled = !busy;
        _checkNone.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _extractSsButton.Enabled = !busy;
        _overwrite.Enabled = !busy;
        _deleteSource.Enabled = !busy;
        _extractIso.Enabled = !busy;
        _root.ReadOnly = busy;
        _outputFolder.ReadOnly = busy;
        _games.Enabled = !busy || !scanning;
        _cancelButton.Enabled = busy;
        if (scanning) _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        UseWaitCursor = busy && scanning;
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(text)));
            return;
        }
        _log.AppendText(text.Length == 0 ? Environment.NewLine : $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }
}
