namespace XGDMaker;

internal sealed class MainForm : Form
{
    private const int ColumnGame = 0, ColumnType = 1, ColumnOutput = 2, ColumnStatus = 3;

    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputFolder = new() { Dock = DockStyle.Fill, PlaceholderText = "Empty = next to each game folder" };
    private readonly TextBox _abgx = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _randomFill = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Fill unused sectors with random data, like retail discs (writes the full image; slower)"
    };
    private readonly CheckBox _runPatch = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "After each successful build, run ABGX360 AutoFix (runs in the background, output goes to the log)"
    };
    private readonly CheckBox _embedSs = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Embed the verified SS in the ISO (full disc layout, needed by Xenon)"
    };
    private readonly CheckBox _overwrite = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Overwrite existing ISOs (and their .iso.ss / .dvd files)"
    };
    private readonly CheckBox _deleteLoose = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Delete the loose .dvd file (the .iso.ss is always kept: Xenon reads the SS from it)"
    };
    private readonly CheckBox _deleteSource = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Permanently delete the source game folder once the ISO is verified identical to it"
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
    private readonly Button _scanButton = new() { Text = "Scan", AutoSize = true };
    private readonly Button _checkAll = new() { Text = "Check all", AutoSize = true };
    private readonly Button _checkNone = new() { Text = "Uncheck all", AutoSize = true };
    private readonly Button _extractButton = new() { Text = "Extract ISOs…", AutoSize = true };
    private readonly Button _extractFolderButton = new() { Text = "Extract all ISOs in folder…", AutoSize = true };
    private readonly Button _buildButton = new() { Text = "Build checked", AutoSize = true, Height = 36 };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Height = 36, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Value = 0 };
    private readonly Label _progressText = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 10, 8, 0) };
    private CancellationTokenSource? _cancel;

    public MainForm()
    {
        Text = "XGD Maker";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 680);
        Size = new Size(1100, 820);
        Font = new Font("Segoe UI", 9f);
        _abgx.Text = FindAbgx();

        _games.Columns.Add("Game", 260);
        _games.Columns.Add("Type", 60);
        _games.Columns.Add("Output ISO", 420);
        _games.Columns.Add("Status", 300);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Find extracted Xbox 360 games and build an ABGX360-ready ISO for each one",
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8)
        }, 0, 0);

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 9, Padding = new Padding(0, 0, 0, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(fields, 0, "Games folder", _root, "Browse…", BrowseRoot);
        AddPathRow(fields, 1, "Output folder", _outputFolder, "Browse…", BrowseOutputFolder);
        AddPathRow(fields, 2, "ABGX360", _abgx, "Browse…", () => BrowseExecutable(_abgx, "abgx360.exe"));
        fields.Controls.Add(MakeLabel("Padding"), 0, 3);
        fields.Controls.Add(_randomFill, 1, 3);
        fields.SetColumnSpan(_randomFill, 2);
        fields.Controls.Add(MakeLabel("ABGX patch"), 0, 4);
        fields.Controls.Add(_runPatch, 1, 4);
        fields.SetColumnSpan(_runPatch, 2);
        fields.Controls.Add(MakeLabel("SS"), 0, 5);
        fields.Controls.Add(_embedSs, 1, 5);
        fields.SetColumnSpan(_embedSs, 2);
        fields.Controls.Add(MakeLabel("Existing ISOs"), 0, 6);
        fields.Controls.Add(_overwrite, 1, 6);
        fields.SetColumnSpan(_overwrite, 2);
        fields.Controls.Add(MakeLabel("Cleanup"), 0, 7);
        fields.Controls.Add(_deleteLoose, 1, 7);
        fields.SetColumnSpan(_deleteLoose, 2);
        fields.Controls.Add(_deleteSource, 1, 8);
        fields.SetColumnSpan(_deleteSource, 2);
        root.Controls.Add(fields, 0, 1);

        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        listButtons.Controls.AddRange(new Control[] { _scanButton, _checkAll, _checkNone, _extractButton, _extractFolderButton });
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

        _scanButton.Click += async (_, _) => await ScanAsync();
        _checkAll.Click += (_, _) => SetAllChecked(true);
        _checkNone.Click += (_, _) => SetAllChecked(false);
        _extractButton.Click += async (_, _) => await PickAndExtractIsosAsync();
        _extractFolderButton.Click += async (_, _) => await ExtractFolderAsync();
        _buildButton.Click += async (_, _) => await BuildCheckedAsync();
        _cancelButton.Click += (_, _) =>
        {
            _cancel?.Cancel();
            _cancelButton.Enabled = false;
            AppendLog("Cancelling: the current operation stops now (an ISO being written is deleted) and nothing else is started.");
        };
        _outputFolder.TextChanged += (_, _) => RefreshOutputColumn();
        _games.ItemCheck += (_, e) =>
        {
            if (e.NewValue == CheckState.Checked && _games.Items[e.Index].Tag is FoundGame { Detection.DiscType: null })
                e.NewValue = CheckState.Unchecked;
        };

        Shown += async (_, _) =>
        {
            try
            {
                await AbgxDatabase.CheckForUpdatesAsync(AppendLog, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppendLog($"Database update check failed: {ex.Message}");
            }
        };

        AppendLog("Choose a folder: every folder below it that directly contains default.xex is listed as a game.");
        AppendLog("Source folders are never modified. Existing ISOs are skipped unless \"Overwrite existing ISOs\" is ticked.");
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
            Description = "Select a game folder, or a folder that contains many extracted games",
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
            Description = "Folder for the ISOs (leave empty to put each ISO next to its game folder)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_outputFolder.Text)) dialog.InitialDirectory = _outputFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputFolder.Text = dialog.SelectedPath;
    }

    private void BrowseExecutable(TextBox target, string expectedName)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Locate {expectedName}",
            Filter = $"{expectedName}|{expectedName}|Executables (*.exe)|*.exe",
            CheckFileExists = true,
            FileName = expectedName
        };
        if (File.Exists(target.Text)) dialog.InitialDirectory = Path.GetDirectoryName(target.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
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
            var games = await Task.Run(() => GameScanner.Scan(rootFolder, AppendLog, token), token);

            _games.BeginUpdate();
            foreach (var game in games)
            {
                var item = new ListViewItem(new[]
                {
                    game.DisplayName,
                    game.Detection.DiscType ?? "?",
                    GameScanner.OutputPathFor(game, _outputFolder.Text),
                    game.Detection.DiscType is null ? game.Detection.Description : "Ready"
                })
                {
                    Tag = game,
                    ToolTipText = game.Folder
                };
                if (game.Detection.DiscType is null) item.ForeColor = Color.Firebrick;
                _games.Items.Add(item);
                item.Checked = game.Detection.DiscType is not null;
            }
            _games.EndUpdate();
            _games.ShowItemToolTips = true;
            if (games.Count == 0) AppendLog("No folder containing default.xex was found.");
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

    private void RefreshOutputColumn()
    {
        foreach (ListViewItem item in _games.Items)
        {
            if (item.Tag is FoundGame game) item.SubItems[ColumnOutput].Text = GameScanner.OutputPathFor(game, _outputFolder.Text);
        }
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in _games.Items) item.Checked = value;
    }

    private async Task BuildCheckedAsync()
    {
        var queue = _games.CheckedItems.Cast<ListViewItem>().ToList();
        if (queue.Count == 0)
        {
            MessageBox.Show(this, "Check at least one game in the list.", "Build", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var abgx = _abgx.Text.Trim();
        var abgxAvailable = File.Exists(abgx);
        var randomFill = _randomFill.Checked;
        var runPatch = _runPatch.Checked && abgxAvailable;
        var embedSs = _embedSs.Checked;
        var overwrite = _overwrite.Checked;
        var deleteLoose = _deleteLoose.Checked;
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
                var game = (FoundGame)item.Tag!;
                var output = GameScanner.OutputPathFor(game, outputFolder);
                _progressText.Text = $"{i + 1} / {queue.Count}";

                if (token.IsCancellationRequested)
                {
                    SetStatus(item, "Not started (cancelled)", Color.DimGray);
                    skipped++;
                    continue;
                }

                AppendLog("");
                AppendLog($"===== [{i + 1}/{queue.Count}] {game.DisplayName} =====");
                if (File.Exists(output) && !overwrite)
                {
                    SetStatus(item, "Skipped: ISO already exists", Color.DimGray);
                    AppendLog($"Skipped: {output} already exists.");
                    skipped++;
                    _progress.Value = i + 1;
                    continue;
                }

                SetStatus(item, "Building…", SystemColors.ControlText);
                _games.EnsureVisible(item.Index);
                try
                {
                    var result = await IsoBuilder.BuildAsync(
                        new BuildRequest(game.Folder, output, abgx, randomFill, embedSs, overwrite), AppendLog, token);
                    var ssEmbedded = result.SsEmbedded;
                    var ssIsV2 = result.SsIsV2;

                    if (abgxAvailable && !result.AbgxRecognizedDefaultXex)
                    {
                        SetStatus(item, "Built, but ABGX360 check FAILED", Color.Firebrick);
                        failed++;
                        _progress.Value = i + 1;
                        continue;
                    }
                    if (runPatch)
                    {
                        SetStatus(item, "PASS – AutoFix running…", Color.ForestGreen);
                        var patch = await AbgxPatcher.RunAsync(abgx, result, AppendLog, token);
                        ssEmbedded = patch.SsEmbedded;
                        ssIsV2 = patch.SsIsV2;
                    }

                    if (deleteLoose && File.Exists(IsoBuilder.DvdFileFor(output)))
                    {
                        File.Delete(IsoBuilder.DvdFileFor(output));
                        AppendLog($"Deleted loose file {Path.GetFileName(IsoBuilder.DvdFileFor(output))}.");
                    }

                    var sourceNote = "";
                    if (deleteSource)
                        sourceNote = await DeleteSourceIfIdenticalAsync(game.Folder, output, token);

                    var prefix = (abgxAvailable ? "PASS" : "Built (not verified: ABGX360 not found)") + sourceNote;
                    if (!ssEmbedded)
                        SetStatus(item, $"{prefix} – no SS (no verified SS for this disc)", Color.DarkOrange);
                    else if (!ssIsV2)
                        SetStatus(item, $"{prefix} – SS embedded, but v1 (Xenon needs v2)", Color.DarkOrange);
                    else
                        SetStatus(item, $"{prefix} – SS v2 embedded, ready for Xenon", Color.ForestGreen);
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

    private async Task PickAndExtractIsosAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the Xbox 360 ISOs to extract",
            Filter = "Xbox 360 ISO (*.iso)|*.iso|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames.Length == 0) return;
        await ExtractIsosAsync(dialog.FileNames);
    }

    private async Task ExtractFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder: the Xbox 360 ISOs inside it (including subfolders) are listed so you can pick which to extract",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (Directory.Exists(_root.Text)) dialog.InitialDirectory = _root.Text;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var folder = dialog.SelectedPath;
        AppendLog($"Searching {folder} for Xbox 360 ISOs...");
        SetBusy(true, scanning: true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        List<string> isos;
        try
        {
            isos = await Task.Run(() => FindIsoFiles(folder, token), token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Search cancelled.");
            return;
        }
        finally
        {
            SetBusy(false, scanning: true);
        }
        AppendLog($"Found {isos.Count} ISO file(s); their type is checked in the list.");
        if (isos.Count == 0)
        {
            MessageBox.Show(this, "No ISO file was found in that folder.", "Extract ISOs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        IReadOnlyList<string> selected;
        using (var picker = new IsoPickerForm(folder, isos))
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            selected = picker.SelectedIsos;
        }
        if (selected.Count == 0) return;
        AppendLog($"{selected.Count} of {isos.Count} ISO(s) selected for extraction.");
        await ExtractIsosAsync(selected.ToArray());
    }

    private List<string> FindIsoFiles(string root, CancellationToken token)
    {
        var result = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push((DirectoryInfo)entry);
                    }
                    else if (entry.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"Skipped {folder.FullName}: {ex.Message}");
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private async Task ExtractIsosAsync(string[] isos)
    {
        var outputFolder = _outputFolder.Text.Trim();
        var overwrite = _overwrite.Checked;
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
                AppendLog($"===== Extract [{i + 1}/{isos.Length}] {Path.GetFileName(iso)} =====");
                var parent = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(iso)! : outputFolder;
                var name = Path.GetFileNameWithoutExtension(iso);
                var destination = Path.Combine(parent, name);
                if (!overwrite && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    destination = Path.Combine(parent, name + " (extracted)");
                try
                {
                    await XdvdfsExtractor.ExtractAsync(iso, destination, overwrite, AppendLog, token);
                    done++;
                }
                catch (OperationCanceledException)
                {
                    AppendLog("Cancelled. Files already extracted for this ISO are left in place.");
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

        var summary = $"Extraction finished: {done} extracted, {failed} failed.";
        AppendLog(summary);
        MessageBox.Show(this, summary, "Extract ISOs", MessageBoxButtons.OK,
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
        _scanButton.Enabled = !busy;
        _buildButton.Enabled = !busy;
        _checkAll.Enabled = !busy;
        _checkNone.Enabled = !busy;
        _extractButton.Enabled = !busy;
        _extractFolderButton.Enabled = !busy;
        _randomFill.Enabled = !busy;
        _runPatch.Enabled = !busy;
        _embedSs.Enabled = !busy;
        _overwrite.Enabled = !busy;
        _deleteLoose.Enabled = !busy;
        _deleteSource.Enabled = !busy;
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

    private static string FindAbgx()
    {
        var candidates = new[]
        {
            @"C:\Windows\SysWOW64\abgx360.exe",
            @"C:\Windows\System32\abgx360.exe",
            @"C:\Program Files (x86)\abgx360\abgx360.exe"
        };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
