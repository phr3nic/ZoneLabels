namespace ZoneLab;

internal sealed class GalleryDialog : Form
{
    private readonly ListBox _captures;
    private readonly List<string> _files;
    private readonly string[] _captureDirectories;
    private readonly string _doneDirectory;
    private readonly Label _emptyMessage;
    private readonly Label _heading;

    public string? SelectedCapturePath { get; private set; }

    public GalleryDialog(IEnumerable<string> captureDirectories, string doneDirectory)
    {
        _captureDirectories = captureDirectories.ToArray();
        _doneDirectory = doneDirectory;
        _files = GetCaptureFiles();

        Text = "Capture library";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 500);
        MinimumSize = new Size(480, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = false;
        MaximizeBox = false;

        _captures = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 42,
        };
        foreach (string file in _files)
            _captures.Items.Add(file);
        if (_captures.Items.Count > 0) _captures.SelectedIndex = 0;
        _captures.DrawItem += DrawCapture;

        _emptyMessage = new Label
        {
            Text = "No captures yet. Use Capture to save a screen to dataset\\todo.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Muted,
            Visible = _files.Count == 0,
        };
        var open = new RoundedButton
        {
            Text = "Open capture",
            AutoSize = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Padding = new Padding(10, 4, 10, 4),
            Accent = true,
        };
        var cancel = new RoundedButton
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Padding = new Padding(10, 4, 10, 4),
            Quiet = true,
        };
        var delete = new RoundedButton
        {
            Text = "Delete capture",
            AutoSize = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Warn,
            Padding = new Padding(10, 4, 10, 4),
            Quiet = true,
            Enabled = _files.Count > 0,
        };
        var done = new RoundedButton
        {
            Text = "Mark done",
            AutoSize = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Success,
            Padding = new Padding(10, 4, 10, 4),
            Accent = true,
            Enabled = _files.Count > 0,
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        footer.Controls.Add(open);
        footer.Controls.Add(cancel);
        footer.Controls.Add(delete);
        footer.Controls.Add(done);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _heading = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        UpdateCaptureList();
        var listSurface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.BgDeep,
            EdgeColor = Theme.BorderSoft,
            CornerRadius = 9,
            Padding = new Padding(4),
        };
        listSurface.Controls.Add(_captures);
        listSurface.Controls.Add(_emptyMessage);
        layout.Controls.Add(_heading, 0, 0);
        layout.Controls.Add(listSurface, 0, 1);
        Controls.Add(layout);
        Controls.Add(footer);
        Controls.Add(Theme.CreateDialogTitleBar(this, Text));
        AcceptButton = open;
        CancelButton = cancel;

        open.Click += (_, _) =>
        {
            if (_captures.SelectedIndex < 0)
            {
                if (_files.Count == 0) DialogResult = DialogResult.Cancel;
                else MessageBox.Show(this, "Select a capture first.", "Capture library",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectedCapturePath = _files[_captures.SelectedIndex];
            DialogResult = DialogResult.OK;
            Close();
        };
        _captures.DoubleClick += (_, _) => open.PerformClick();
        _captures.SelectedIndexChanged += (_, _) =>
        {
            bool hasSelection = _captures.SelectedIndex >= 0;
            delete.Enabled = hasSelection;
            done.Enabled = hasSelection && !CaptureFileOperations.IsCompleted(
                _files[_captures.SelectedIndex], _doneDirectory);
        };
        bool hasInitialSelection = _captures.SelectedIndex >= 0;
        delete.Enabled = hasInitialSelection;
        done.Enabled = hasInitialSelection && !CaptureFileOperations.IsCompleted(
            _files[_captures.SelectedIndex], _doneDirectory);
        delete.Click += (_, _) => DeleteSelectedCapture();
        done.Click += (_, _) => MarkSelectedDone();
    }

    private void UpdateCaptureList()
    {
        int doneCount = _files.Count(file => CaptureFileOperations.IsCompleted(file, _doneDirectory));
        int todoCount = _files.Count - doneCount;
        _heading.Text = $"CAPTURE LIBRARY     ·     {todoCount} TODO  /  {doneCount} DONE";
        _emptyMessage.Visible = _files.Count == 0;
    }

    private List<string> GetCaptureFiles() =>
        _captureDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

    private void MarkSelectedDone()
    {
        int selectedIndex = _captures.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _files.Count)
            return;

        string imagePath = _files[selectedIndex];
        if (CaptureFileOperations.IsCompleted(imagePath, _doneDirectory))
            return;

        try
        {
            if (!CaptureFileOperations.IsReadyForCompletion(imagePath, out string reason))
            {
                MessageBox.Show(this, reason, "Capture is not ready",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string completedPath = CaptureFileOperations.MoveToDone(imagePath, _doneDirectory);
            RefreshCaptureFiles(completedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log.Error($"marking capture done failed: {imagePath}", ex);
            MessageBox.Show(this, $"Could not move the capture to dataset\\done.\n{ex.Message}",
                "Could not mark capture done", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshCaptureFiles(string? selectedPath)
    {
        _files.Clear();
        _files.AddRange(GetCaptureFiles());
        _captures.BeginUpdate();
        _captures.Items.Clear();
        foreach (string file in _files)
            _captures.Items.Add(file);
        _captures.SelectedIndex = selectedPath is null
            ? -1
            : _files.FindIndex(file => string.Equals(file, selectedPath, StringComparison.OrdinalIgnoreCase));
        _captures.EndUpdate();
        UpdateCaptureList();
    }

    private void DeleteSelectedCapture()
    {
        int selectedIndex = _captures.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _files.Count)
            return;

        string imagePath = _files[selectedIndex];
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

        _files.RemoveAt(selectedIndex);
        _captures.Items.RemoveAt(selectedIndex);
        if (_captures.Items.Count > 0)
            _captures.SelectedIndex = Math.Min(selectedIndex, _captures.Items.Count - 1);
        UpdateCaptureList();

        if (metadataWarning is not null)
            MessageBox.Show(this, metadataWarning, "Capture partially deleted",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void DrawCapture(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _files.Count) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;
        Color background = selected ? Theme.AccentSoft : Theme.BgDeep;
        using (var fill = new SolidBrush(background))
            e.Graphics.FillRectangle(fill, bounds);

        string filename = Path.GetFileNameWithoutExtension(_files[e.Index]);
        DateTime created = File.GetLastWriteTime(_files[e.Index]);
        string source = Path.GetFileName(Path.GetDirectoryName(_files[e.Index]) ?? string.Empty);
        string state = CaptureFileOperations.IsCompleted(_files[e.Index], _doneDirectory)
            ? "DONE"
            : string.Equals(source, "gallery", StringComparison.OrdinalIgnoreCase)
                ? "OLDER GALLERY"
                : "TODO";
        string details = $"{state}   ·   {created:g}";
        using var titleFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 8f);
        TextRenderer.DrawText(e.Graphics, filename, titleFont,
            new Rectangle(bounds.X + 12, bounds.Y + 4, bounds.Width - 24, 18),
            selected ? Color.White : Theme.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, details, detailFont,
            new Rectangle(bounds.X + 12, bounds.Y + 22, bounds.Width - 24, 16),
            Theme.Muted, TextFormatFlags.EndEllipsis);
        if (selected)
        {
            using var marker = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(marker, bounds.X, bounds.Y + 5, 3, bounds.Height - 10);
        }
        e.DrawFocusRectangle();
    }
}
