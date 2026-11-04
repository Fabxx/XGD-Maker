namespace XGDMaker;

internal sealed class IsoPickerForm : Form
{
    private const int ColumnType = 2;
    private readonly CancellationTokenSource _probeCancel = new();
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true
    };
    private readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 10, 8, 0) };

    public IReadOnlyList<string> SelectedIsos =>
        _list.CheckedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToList();

    public IsoPickerForm(string root, IReadOnlyList<string> isos)
    {
        Text = $"ISOs found in {root}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 450);
        Size = new Size(950, 600);
        Font = new Font("Segoe UI", 9f);

        _list.Columns.Add("ISO", 300);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Type", 150);
        _list.Columns.Add("Folder", 380);
        _list.BeginUpdate();
        foreach (var iso in isos)
        {
            long size = 0;
            try { size = new FileInfo(iso).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var relativeFolder = Path.GetRelativePath(root, Path.GetDirectoryName(iso) ?? root);
            _list.Items.Add(new ListViewItem(new[]
            {
                Path.GetFileName(iso),
                $"{size / (1024.0 * 1024 * 1024):0.00} GB",
                "checking…",
                relativeFolder == "." ? "(selected folder)" : relativeFolder
            })
            {
                Tag = iso,
                Checked = true,
                ToolTipText = iso
            });
        }
        _list.EndUpdate();
        _list.ShowItemToolTips = true;
        _list.ItemChecked += (_, _) => UpdateSummary();

        var checkAll = new Button { Text = "Check all", AutoSize = true };
        var checkNone = new Button { Text = "Uncheck all", AutoSize = true };
        var extract = new Button { Text = "Extract checked", AutoSize = true, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Height = 32, DialogResult = DialogResult.Cancel };
        checkAll.Click += (_, _) => SetAll(true);
        checkNone.Click += (_, _) => SetAll(false);
        AcceptButton = extract;
        CancelButton = cancel;

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.AddRange(new Control[] { checkAll, checkNone, _summary });

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.AddRange(new Control[] { cancel, extract });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        Controls.Add(layout);

        UpdateSummary();

        Shown += (_, _) => _ = ProbeAllAsync(_probeCancel.Token);
        FormClosed += (_, _) => _probeCancel.Cancel();
    }

    private async Task ProbeAllAsync(CancellationToken token)
    {
        var items = _list.Items.Cast<ListViewItem>().ToList();
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            var path = (string)item.Tag!;
            string type;
            bool isXbox;
            try
            {
                var offset = await Task.Run(() => XdvdfsProbe.FindGamePartitionOffset(path), token);
                isXbox = offset is not null;
                type = offset switch
                {
                    null => "not an Xbox 360 ISO",
                    0 => "game partition",
                    XdvdfsProbe.Xgd2VideoOffset => "XGD2",
                    XdvdfsProbe.Xgd3VideoOffset => "XGD3",
                    _ => "unknown"
                };
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                isXbox = false;
                type = "unreadable";
            }
            if (token.IsCancellationRequested || IsDisposed) return;
            item.SubItems[ColumnType].Text = type;
            if (!isXbox)
            {
                item.Checked = false;
                item.ForeColor = Color.Gray;
            }
        }
    }

    private void SetAll(bool value)
    {
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
            item.Checked = value && item.SubItems[ColumnType].Text is not ("not an Xbox 360 ISO" or "unreadable");
        _list.EndUpdate();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _list.CheckedItems.Count;
        _summary.Text = $"{count} of {_list.Items.Count} checked";
        if (AcceptButton is Button extract) extract.Enabled = count > 0;
    }
}
