using System.Text.Json;

namespace ZoneLab;

internal sealed class GalleryDialog : Form
{
    private enum CaptureSection
    {
        DoneData,
        Todo,
        SavedEditing,
    }

    private readonly List<string> _files;
    private readonly string _doneDirectory;
    private readonly string _dataDirectory;
    private readonly DatasetManifestStore _manifestStore;
    private readonly Dictionary<CaptureSection, List<string>> _sectionFiles = new();
    private readonly Dictionary<CaptureSection, ListBox> _captureLists = new();
    private readonly Dictionary<CaptureSection, Label> _emptyMessages = new();
    private readonly Dictionary<CaptureSection, RoundedButton> _sectionButtons = new();
    private readonly Dictionary<CaptureSection, Control> _sectionSurfaces = new();
    private readonly Panel _sectionHost;
    private readonly Label _heading;
    private readonly RoundedButton _openButton;
    private readonly RoundedButton _deleteButton;
    private readonly RoundedButton _doneButton;
    private CaptureSection _activeSection = CaptureSection.Todo;
    private ListBox _captures = null!;

    public string? SelectedCapturePath { get; private set; }

    public GalleryDialog(string doneDirectory, string dataDirectory, DatasetManifestStore manifestStore)
    {
        _doneDirectory = doneDirectory;
        _dataDirectory = dataDirectory;
        _manifestStore = manifestStore;
        _files = GetCaptureFiles();

        Text = "Capture library";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(640, 560);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = false;
        MaximizeBox = false;

        _heading = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 8.25f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };
        var tabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Bg,
        };
        _sectionHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
        };

        AddSection(CaptureSection.DoneData, "Done / data", tabs);
        AddSection(CaptureSection.Todo, "Todo", tabs);
        AddSection(CaptureSection.SavedEditing, "Saved / editing", tabs);

        _openButton = MakeButton("Open capture", accent: true);
        _deleteButton = MakeButton("Delete capture", quiet: true);
        _deleteButton.ForeColor = Theme.Warn;
        _doneButton = MakeButton("Mark done");
        _doneButton.ForeColor = Theme.Success;
        var cancelButton = MakeButton("Cancel", quiet: true);
        cancelButton.DialogResult = DialogResult.Cancel;

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        footer.Controls.Add(_openButton);
        footer.Controls.Add(cancelButton);
        footer.Controls.Add(_deleteButton);
        footer.Controls.Add(_doneButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_heading, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
        layout.Controls.Add(_sectionHost, 0, 2);

        var dialogSurface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.Border,
            Padding = new Padding(1),
            Margin = Padding.Empty,
        };
        dialogSurface.Controls.Add(layout);
        dialogSurface.Controls.Add(footer);
        dialogSurface.Controls.Add(Theme.CreateDialogTitleBar(this, Text));
        Controls.Add(dialogSurface);

        _openButton.Click += (_, _) =>
        {
            string? path = GetSelectedFile();
            if (path is null) return;
            SelectedCapturePath = path;
            DialogResult = DialogResult.OK;
            Close();
        };
        _deleteButton.Click += (_, _) => DeleteSelectedCapture();
        _doneButton.Click += (_, _) => MarkSelectedDone();
        cancelButton.Click += (_, _) => Close();
        foreach (ListBox list in _captureLists.Values)
        {
            list.SelectedIndexChanged += (_, _) => UpdateActionButtons();
            list.DoubleClick += (_, _) => _openButton.PerformClick();
        }

        AcceptButton = _openButton;
        CancelButton = cancelButton;
        var toolTips = new ToolTip();
        ApplyButtonTooltips(this, toolTips);
        Disposed += (_, _) => toolTips.Dispose();

        RefreshCaptureFiles(null);
    }

    private void AddSection(CaptureSection section, string title, FlowLayoutPanel tabs)
    {
        var button = new RoundedButton
        {
            Text = title,
            AutoSize = false,
            Width = section == CaptureSection.SavedEditing ? 150 : 120,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 0, 12, 0),
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
        };
        button.Click += (_, _) => SelectSection(section);
        tabs.Controls.Add(button);
        _sectionButtons.Add(section, button);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f),
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 42,
            Visible = false,
        };
        list.DrawItem += DrawCapture;
        var empty = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            BackColor = Theme.BgDeep,
            Visible = false,
        };
        var surface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.BgDeep,
            EdgeColor = Theme.Border,
            Padding = new Padding(4),
            Visible = false,
        };
        surface.Controls.Add(list);
        surface.Controls.Add(empty);
        _sectionHost.Controls.Add(surface);
        _sectionFiles.Add(section, new List<string>());
        _captureLists.Add(section, list);
        _emptyMessages.Add(section, empty);
        _sectionSurfaces.Add(section, surface);
    }

    private void SelectSection(CaptureSection section)
    {
        _activeSection = section;
        _captures = _captureLists[section];
        foreach (CaptureSection key in _sectionButtons.Keys)
        {
            bool selected = key == section;
            _sectionButtons[key].Selected = selected;
            Control surface = _sectionSurfaces[key];
            surface.Visible = selected;
            if (selected) surface.BringToFront();
        }
        UpdateActionButtons();
    }

    private void UpdateCaptureList()
    {
        int doneCount = _sectionFiles[CaptureSection.DoneData].Count;
        int todoCount = _sectionFiles[CaptureSection.Todo].Count;
        int savedCount = _sectionFiles[CaptureSection.SavedEditing].Count;
        _heading.Text = $"Capture library  ·  {doneCount} done / data  ·  {todoCount} todo  ·  {savedCount} saved / editing";

        _sectionButtons[CaptureSection.DoneData].Text = $"Done / data ({doneCount})";
        _sectionButtons[CaptureSection.Todo].Text = $"Todo ({todoCount})";
        _sectionButtons[CaptureSection.SavedEditing].Text = $"Saved / editing ({savedCount})";
        _emptyMessages[CaptureSection.DoneData].Text = "No completed captures.";
        _emptyMessages[CaptureSection.Todo].Text = "No captures waiting to be labeled.";
        _emptyMessages[CaptureSection.SavedEditing].Text = "No captures with saved pin progress.";
        foreach (CaptureSection section in _sectionFiles.Keys)
        {
            bool isEmpty = _sectionFiles[section].Count == 0;
            _emptyMessages[section].Visible = isEmpty;
            _captureLists[section].Visible = !isEmpty;
        }
    }

    private void UpdateActionButtons()
    {
        string? selectedPath = GetSelectedFile();
        bool hasSelection = selectedPath is not null;
        _openButton.Enabled = hasSelection;
        _deleteButton.Enabled = hasSelection;
        _doneButton.Enabled = hasSelection
            && !IsDataCrop(selectedPath!)
            && _manifestStore.GetStatus(selectedPath!) == "todo";
        _deleteButton.Enabled = hasSelection && !IsDataCrop(selectedPath!);
    }

    private static void ApplyButtonTooltips(Control parent, ToolTip toolTips)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button button)
            {
                string text = string.IsNullOrWhiteSpace(button.AccessibleName)
                    ? button.Text
                    : button.AccessibleName;
                toolTips.SetToolTip(button, string.IsNullOrWhiteSpace(text) ? "Button" : text);
            }
            ApplyButtonTooltips(child, toolTips);
        }
    }

    private static RoundedButton MakeButton(string text, bool accent = false, bool quiet = false) => new()
    {
        Text = text,
        AutoSize = false,
        BackColor = Theme.Panel,
        ForeColor = Theme.Text,
        Width = TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont).Width + 32,
        Height = 32,
        Padding = new Padding(12, 0, 12, 0),
        Margin = new Padding(0, 0, 8, 0),
        Accent = accent,
        Quiet = quiet,
    };

    private List<string> GetCaptureFiles() =>
        _manifestStore.Manifest.Captures
            .Select(entry => _manifestStore.ResolveFile(entry.File))
            .Concat(Directory.Exists(_dataDirectory)
                ? Directory.GetFiles(_dataDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(CaptureFileOperations.IsSupportedImageFile)
                : Enumerable.Empty<string>())
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(CaptureFileOperations.GetCaptureTimeUtc)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RefreshCaptureFiles(string? selectedPath)
    {
        foreach (List<string> files in _sectionFiles.Values)
            files.Clear();
        _files.Clear();
        _files.AddRange(GetCaptureFiles());
        foreach (string file in _files)
            _sectionFiles[GetSection(file)].Add(file);

        foreach (CaptureSection section in _sectionFiles.Keys)
        {
            ListBox list = _captureLists[section];
            list.BeginUpdate();
            list.Items.Clear();
            foreach (string file in _sectionFiles[section])
                list.Items.Add(file);
            list.EndUpdate();
        }

        UpdateCaptureList();
        if (selectedPath is not null && File.Exists(selectedPath))
        {
            CaptureSection section = GetSection(selectedPath);
            SelectSection(section);
            _captures.SelectedIndex = _sectionFiles[section].FindIndex(
                file => string.Equals(file, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            CaptureSection initialSection = _sectionFiles[CaptureSection.Todo].Count > 0
                ? CaptureSection.Todo
                : _sectionFiles[CaptureSection.SavedEditing].Count > 0
                    ? CaptureSection.SavedEditing
                    : CaptureSection.DoneData;
            SelectSection(initialSection);
            if (_captures.Items.Count > 0)
                _captures.SelectedIndex = 0;
        }
        UpdateActionButtons();
    }

    private CaptureSection GetSection(string imagePath)
    {
        if (IsDataCrop(imagePath))
            return CaptureSection.DoneData;
        string status = _manifestStore.GetStatus(imagePath);
        if (status is "done" or "skipped")
            return CaptureSection.DoneData;
        return _manifestStore.FindCapture(imagePath)?.Pins.Count > 0
            ? CaptureSection.SavedEditing
            : CaptureSection.Todo;
    }

    private bool IsDataCrop(string imagePath) =>
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(imagePath) ?? string.Empty),
            Path.GetFullPath(_dataDirectory),
            StringComparison.OrdinalIgnoreCase);

    private string? GetSelectedFile()
    {
        int index = _captures?.SelectedIndex ?? -1;
        return index >= 0 && index < _sectionFiles[_activeSection].Count
            ? _sectionFiles[_activeSection][index]
            : null;
    }

    private void MarkSelectedDone()
    {
        string? imagePath = GetSelectedFile();
        if (imagePath is null || _manifestStore.GetStatus(imagePath) != "todo")
            return;

        try
        {
            if (!CaptureFileOperations.IsReadyForCompletion(imagePath, _manifestStore, out string reason))
            {
                MessageBox.Show(this, reason, "Capture is not ready",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string completedPath = _manifestStore.MoveToDone(imagePath, _doneDirectory);
            RefreshCaptureFiles(completedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Log.Error($"marking capture done failed: {imagePath}", ex);
            MessageBox.Show(this, $"Could not move the capture to dataset\\done.\n{ex.Message}",
                "Could not mark capture done", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelectedCapture()
    {
        string? imagePath = GetSelectedFile();
        if (imagePath is null || IsDataCrop(imagePath)) return;

        string metadataPath = Path.ChangeExtension(imagePath, ".json");
        string message = $"Permanently delete {Path.GetFileName(imagePath)} and its saved pin data?\n\n"
            + "Exported crops and labels.csv entries will not be deleted.";
        if (MessageBox.Show(this, message, "Delete capture", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            File.Delete(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"deleting capture failed: {imagePath}", ex);
            MessageBox.Show(this, $"Could not delete the capture.\n{ex.Message}", "Delete failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? metadataWarning = null;
        try
        {
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"deleting capture metadata failed: {metadataPath}", ex);
            metadataWarning = $"The capture image was deleted, but its pin data could not be deleted:\n{ex.Message}";
        }

        try
        {
            _manifestStore.RemoveCapture(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error($"removing capture from manifest failed: {imagePath}", ex);
            metadataWarning = $"The files were deleted, but manifest.json could not be updated:\n{ex.Message}";
        }

        RefreshCaptureFiles(null);
        if (metadataWarning is not null)
            MessageBox.Show(this, metadataWarning, "Capture partially deleted",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void DrawCapture(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count)
            return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;
        using (var fill = new SolidBrush(selected ? Theme.PanelHi : Theme.BgDeep))
            e.Graphics.FillRectangle(fill, bounds);

        string path = (string)list.Items[e.Index]!;
        string filename = Path.GetFileNameWithoutExtension(path);
        DateTime created = CaptureFileOperations.GetCaptureTimeUtc(path).ToLocalTime();
        string state = IsDataCrop(path)
            ? "EXPORTED DATA CROP"
            : _manifestStore.GetStatus(path) == "done"
            ? "DONE / DATA"
            : _manifestStore.GetStatus(path) == "skipped"
                ? "SKIPPED"
            : GetSection(path) == CaptureSection.SavedEditing
                ? "SAVED / EDITING"
                : "TODO";
        string details = $"{state}   ·   {created:g}";
        using var titleFont = new Font("Segoe UI", 9f);
        using var detailFont = new Font("Segoe UI", 8.25f);
        TextRenderer.DrawText(e.Graphics, filename, titleFont,
            new Rectangle(bounds.X + 12, bounds.Y + 4, bounds.Width - 24, 18),
            selected ? Color.White : Theme.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, details, detailFont,
            new Rectangle(bounds.X + 12, bounds.Y + 22, bounds.Width - 24, 16),
            Theme.Muted, TextFormatFlags.EndEllipsis);
        if (selected)
        {
            using var marker = new SolidBrush(Theme.Muted);
            e.Graphics.FillRectangle(marker, bounds.X, bounds.Y + 5, 3, bounds.Height - 10);
        }
        e.DrawFocusRectangle();
    }
}
