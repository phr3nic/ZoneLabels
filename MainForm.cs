using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ZoneLab;

/// <summary>
/// Full-screen zone labeller. BitBlt captures the primary monitor's desktop
/// compositor, so games must be running borderless-windowed rather than exclusive fullscreen.
/// </summary>
public sealed class MainForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height,
        IntPtr sourceDc, int sourceX, int sourceY, uint rasterOperation);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private const int WmHotkey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeGrip = 8;
    private const uint ModNoRepeat = 0x4000;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int CaptureHotkeyId = 1;
    private const int DiscardHotkeyId = 2;
    private const int ZoneHotkeyBase = 10;
    private const int PinConfirmationMs = 550;

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private readonly string _imageDirectory = Path.Combine(Log.Directory, "data");
    private readonly string _todoDirectory = Path.Combine(Log.Directory, "todo");
    private readonly string _doneDirectory = Path.Combine(Log.Directory, "done");
    private readonly string _legacyGalleryDirectory = Path.Combine(Log.Directory, "gallery");
    private readonly string _csvPath = Path.Combine(Log.Directory, "labels.csv");
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _countLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoundedButton> _zoneButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _registeredHotkeys = new();
    private readonly List<ScreenPin> _pins = new();
    private readonly ScreenPreview _preview;
    private readonly FlowLayoutPanel _zoneButtonsPanel;
    private readonly FlowLayoutPanel _countList;
    private readonly Label _statusLabel;
    private readonly Label _shortcutHint;
    private readonly RoundedButton _captureButton;
    private readonly RoundedButton _discardButton;
    private readonly RoundedButton _doneButton;
    private readonly RoundedButton _fullScreenButton;
    private readonly ToolTip _windowToolTip;
    private readonly CheckBox _alwaysOnTopCheck;
    private readonly CheckBox _hideWhileCapturingCheck;
    private readonly System.Windows.Forms.Timer _pinFlashTimer;
    private AppConfig _config;
    private string? _currentGalleryPath;
    private GalleryCapture _currentGallery = new();
    private bool _frameIsFrozen;
    private bool _captureInProgress;
    private bool _editingSettings;
    private bool _timerResolutionAcquired;
    private bool _hotkeyRegistrationFailed;
    private bool _isFullScreen;
    private Rectangle _restoreBounds;

    public MainForm()
    {
        uint timerResult = timeBeginPeriod(1);
        _timerResolutionAcquired = timerResult == 0;
        if (!_timerResolutionAcquired)
            Log.Error($"timeBeginPeriod(1) failed with result {timerResult}.");

        EnsureFolders();
        try
        {
            _config = AppConfig.Load(_configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Error("loading config.json failed; using defaults", ex);
            _config = AppConfig.CreateDefault();
            _configWarning = "Could not load config.json; defaults are active. See dataset/capture.log.";
        }

        foreach (var zone in _config.Zones)
            _counts[zone.Name] = 0;

        Text = "Zone Labeller";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1380, 900);
        MinimumSize = new Size(1040, 700);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;

        _preview = new ScreenPreview
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Pins = _pins,
        };
        _preview.PinCreated += AddPin;
        _preview.PinDeleteRequested += DeletePin;

        _zoneButtonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(2, 7, 2, 0),
            BackColor = Color.Transparent,
        };
        _countList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Margin = new Padding(12, 0, 0, 0),
        };
        _shortcutHint = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 8, 12, 0),
        };
        _captureButton = new RoundedButton { Text = string.Empty, Width = 132, Height = 36, Accent = true };
        _discardButton = new RoundedButton { Text = string.Empty, Width = 118, Height = 36 };
        _doneButton = new RoundedButton
        {
            Text = "✓  Done",
            Width = 92,
            Height = 36,
            ForeColor = Theme.Success,
            Enabled = false,
        };
        _fullScreenButton = new RoundedButton { Text = "⛶", Width = 34, Height = 32, Quiet = true };
        _windowToolTip = new ToolTip { BackColor = Theme.Panel, ForeColor = Theme.Text, InitialDelay = 450 };
        _windowToolTip.SetToolTip(_fullScreenButton, "Enter full screen (F11)");
        _windowToolTip.SetToolTip(_doneButton, "Save labeled crops and move this capture to dataset\\done");
        _alwaysOnTopCheck = new CheckBox { Text = "Always on top", AutoSize = true, ForeColor = Theme.Text };
        _hideWhileCapturingCheck = new CheckBox { Text = "Hide while capturing", AutoSize = true, ForeColor = Theme.Text };
        _pinFlashTimer = new System.Windows.Forms.Timer { Interval = PinConfirmationMs };
        _pinFlashTimer.Tick += (_, _) =>
        {
            _pinFlashTimer.Stop();
            foreach (var pin in _pins) pin.Confirming = false;
            _preview.Invalidate();
        };
        BuildUi();
        RefreshConfigUi();
        RefreshZoneUi();
        TopMost = _config.AlwaysOnTop;
        _alwaysOnTopCheck.Checked = _config.AlwaysOnTop;
        _hideWhileCapturingCheck.Checked = _config.HideWhileCapturing;
        SetStatus(_configWarning ?? $"Ready. Press {FormatConfiguredKey(_config.CaptureKey)} over the target.");
        Resize += (_, _) => UpdateWindowRegion();
        Shown += (_, _) => UpdateWindowRegion();
        KeyDown += OnFormKeyDown;
        _captureButton.Click += async (_, _) => await CaptureRequested();
        _discardButton.Click += (_, _) => DiscardPins();
        _doneButton.Click += (_, _) => CompleteCurrentCapture();
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();
        _alwaysOnTopCheck.CheckedChanged += (_, _) =>
        {
            TopMost = _alwaysOnTopCheck.Checked;
            _config.AlwaysOnTop = TopMost;
            SaveCurrentConfig();
        };
        _hideWhileCapturingCheck.CheckedChanged += (_, _) =>
        {
            _config.HideWhileCapturing = _hideWhileCapturingCheck.Checked;
            SaveCurrentConfig();
        };
    }

    private string? _configWarning;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 12, 18, 12),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        root.Controls.Add(BuildTitleBar(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        root.Controls.Add(BuildZoneBar(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    private Control BuildTitleBar()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(2, 0, 2, 4),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        var title = new Label
        {
            Text = "ZONE LABELLER",
            AutoSize = true,
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 0, 0, 0),
        };
        var glowText = new Label
        {
            Text = "FULL-FRAME DATA COLLECTION  ·  BORDERLESS CAPTURE",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Consolas", 8f),
            TextAlign = ContentAlignment.TopLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(5, 1, 0, 0),
        };
        brand.Controls.Add(title, 0, 0);
        brand.Controls.Add(glowText, 0, 1);
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        var close = new RoundedButton { Text = "×", Width = 34, Height = 32, Quiet = true };
        var minimize = new RoundedButton { Text = "−", Width = 34, Height = 32, Quiet = true };
        _windowToolTip.SetToolTip(close, "Close Zone Labeller");
        _windowToolTip.SetToolTip(minimize, "Minimize");
        close.Click += (_, _) => Close();
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _captureButton.Margin = new Padding(6, 0, 0, 0);
        _discardButton.Margin = new Padding(6, 0, 0, 0);
        _doneButton.Margin = new Padding(6, 0, 0, 0);
        controls.Controls.Add(close);
        controls.Controls.Add(minimize);
        controls.Controls.Add(_fullScreenButton);
        controls.Controls.Add(_doneButton);
        controls.Controls.Add(_discardButton);
        controls.Controls.Add(_captureButton);
        controls.Controls.Add(_shortcutHint);
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(controls, 1, 0);
        header.MouseDown += DragWindow;
        brand.MouseDown += DragWindow;
        title.MouseDown += DragWindow;
        title.DoubleClick += (_, _) => ToggleFullScreen();
        glowText.MouseDown += DragWindow;
        return header;
    }

    private Control BuildWorkspace()
    {
        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 8),
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 252));

        var previewFrame = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.BgDeep,
            EdgeColor = Theme.Border,
            CornerRadius = 13,
            Padding = new Padding(1),
            Margin = new Padding(0, 0, 14, 0),
        };
        _preview.BackColor = Theme.BgDeep;
        previewFrame.Controls.Add(_preview);
        workspace.Controls.Add(previewFrame, 0, 0);
        workspace.Controls.Add(BuildSidebar(), 1, 0);
        return workspace;
    }

    private Control BuildSidebar()
    {
        var sidebar = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.BorderSoft,
            CornerRadius = 13,
            Padding = new Padding(15, 14, 15, 14),
            Margin = new Padding(0),
        };
        var sidebarLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
        };
        sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebarLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebarLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label
        {
            Text = "LABELLED ZONES",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            Margin = new Padding(1, 0, 0, 10),
        };
        var countsFrame = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.BgDeep,
            EdgeColor = Theme.BorderSoft,
            CornerRadius = 9,
            Padding = new Padding(9, 8, 5, 8),
            Margin = new Padding(0),
        };
        countsFrame.Controls.Add(_countList);
        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = Color.Transparent,
        };
        var settings = new RoundedButton { Text = "⚙   Label settings", Width = 210, Height = 38 };
        var gallery = new RoundedButton { Text = "▤   Capture library", Width = 210, Height = 38 };
        var openFolder = new RoundedButton { Text = "↗   Open dataset folder", Width = 210, Height = 38 };
        var reset = new RoundedButton { Text = "↺   Reset counts", Width = 210, Height = 38, Quiet = true };
        settings.Click += (_, _) => EditSettings();
        gallery.Click += (_, _) => OpenGallery();
        openFolder.Click += (_, _) => OpenDatasetFolder();
        reset.Click += (_, _) => ResetCounts();
        options.Controls.Add(settings);
        options.Controls.Add(gallery);
        options.Controls.Add(openFolder);
        options.Controls.Add(reset);
        var checkboxes = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 13, 0, 0),
            BackColor = Color.Transparent,
        };
        _alwaysOnTopCheck.BackColor = Color.Transparent;
        _hideWhileCapturingCheck.BackColor = Color.Transparent;
        checkboxes.Controls.Add(_alwaysOnTopCheck);
        checkboxes.Controls.Add(_hideWhileCapturingCheck);
        sidebarLayout.Controls.Add(caption, 0, 0);
        sidebarLayout.Controls.Add(countsFrame, 0, 1);
        sidebarLayout.Controls.Add(options, 0, 2);
        sidebarLayout.Controls.Add(checkboxes, 0, 3);
        sidebar.Controls.Add(sidebarLayout);
        return sidebar;
    }

    private Control BuildZoneBar()
    {
        var surface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.BorderSoft,
            CornerRadius = 11,
            Padding = new Padding(11, 5, 11, 0),
            Margin = new Padding(0, 2, 0, 0),
        };
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label
        {
            Text = "ASSIGN ZONE TO LAST PIN",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Margin = new Padding(2, 2, 0, 0),
        };
        container.Controls.Add(caption, 0, 0);
        container.Controls.Add(_zoneButtonsPanel, 0, 1);
        surface.Controls.Add(container);
        return surface;
    }

    private Control BuildFooter()
    {
        var footer = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.BorderSoft,
            CornerRadius = 9,
            Padding = new Padding(4, 0, 8, 0),
            Margin = new Padding(0, 3, 0, 0),
        };
        footer.Controls.Add(_statusLabel);
        return footer;
    }

    private void RefreshConfigUi()
    {
        string capture = FormatConfiguredKey(_config.CaptureKey);
        string discard = FormatConfiguredKey(_config.DiscardKey);
        _captureButton.Text = $"Capture  [{capture}]";
        _discardButton.Text = $"Discard  [{discard}]";
        _shortcutHint.Text = $"{capture} CAPTURE   {discard} DISCARD   F11 FULL SCREEN";
        _preview.CaptureShortcut = capture;
    }

    private void RefreshZoneUi()
    {
        _zoneButtonsPanel.SuspendLayout();
        DisposeChildren(_zoneButtonsPanel);
        _zoneButtons.Clear();
        _countList.SuspendLayout();
        DisposeChildren(_countList);
        _countLabels.Clear();

        foreach (ZoneConfig zone in _config.Zones)
        {
            if (!_counts.ContainsKey(zone.Name))
                _counts[zone.Name] = 0;

            var button = new RoundedButton
            {
                Text = $"{FormatConfiguredKey(zone.Key)}   {zone.Name}",
                Width = Math.Max(112, Math.Min(190, 96 + zone.Name.Length * 7)),
                Height = 48,
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                Margin = new Padding(0, 0, 8, 8),
                Enabled = _frameIsFrozen,
                Accent = _pins.LastOrDefault()?.Zone is string activeZone
                    && string.Equals(activeZone, zone.Name, StringComparison.OrdinalIgnoreCase),
            };
            string zoneName = zone.Name;
            button.Click += (_, _) => AssignZone(zoneName);
            _zoneButtonsPanel.Controls.Add(button);
            _zoneButtons[zone.Name] = button;

            var row = new Panel
            {
                Width = 180,
                Height = 34,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 1),
            };
            var nameLabel = new Label
            {
                Text = zone.Name,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3, 0, 0, 0),
            };
            var countLabel = new Label
            {
                Text = _counts[zone.Name].ToString(),
                Dock = DockStyle.Right,
                Width = 56,
                ForeColor = Theme.Accent,
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 7, 0),
            };
            row.Controls.Add(nameLabel);
            row.Controls.Add(countLabel);
            _countLabels[zone.Name] = countLabel;
            _countList.Controls.Add(row);
        }

        _zoneButtonsPanel.ResumeLayout(true);
        _countList.ResumeLayout(true);
        RefreshConfigUi();
        UpdateHotkeys();
    }

    private void EditSettings()
    {
        if (_captureInProgress) return;
        _editingSettings = true;
        UnregisterHotkeys();
        try
        {
            using var dialog = new SettingsDialog(_config, SaveSettingsFromDialog);
            dialog.ShowDialog(this);
        }
        finally
        {
            _editingSettings = false;
            UpdateHotkeys();
        }
    }

    private bool SaveSettingsFromDialog(AppConfig updated)
    {
        updated.CropSize = _config.CropSize;
        updated.AlwaysOnTop = _config.AlwaysOnTop;
        updated.HideWhileCapturing = _config.HideWhileCapturing;
        AppConfig.Validate(updated);
        WriteConfig(updated);
        _config = updated;
        var oldCounts = new Dictionary<string, int>(_counts, StringComparer.OrdinalIgnoreCase);
        _counts.Clear();
        foreach (ZoneConfig zone in _config.Zones)
            _counts[zone.Name] = oldCounts.GetValueOrDefault(zone.Name);
        bool removedAssignedZone = false;
        foreach (ScreenPin pin in _pins)
        {
            if (pin.Zone is not null && !_counts.ContainsKey(pin.Zone))
            {
                pin.Zone = null;
                pin.Exported = false;
                pin.Confirming = false;
                removedAssignedZone = true;
            }
        }
        TopMost = _config.AlwaysOnTop;
        _alwaysOnTopCheck.Checked = _config.AlwaysOnTop;
        _hideWhileCapturingCheck.Checked = _config.HideWhileCapturing;
        RefreshZoneUi();
        _preview.Invalidate();
        bool galleryStateSaved = PersistPinsOrReport();
        if (!galleryStateSaved)
        {
            SetStatus("Settings saved, but gallery pin progress could not be saved.", true);
            return true;
        }
        SetStatus(_hotkeyRegistrationFailed
            ? "Settings saved, but a global hotkey is in use by another app."
            : removedAssignedZone
                ? "Settings saved. Pins assigned to removed zones need a new label."
                : "Settings saved.",
            _hotkeyRegistrationFailed || !galleryStateSaved);
        return true;
    }

    private void SaveCurrentConfig()
    {
        try
        {
            AppConfig.Validate(_config);
            WriteConfig(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("saving config.json failed", ex);
            SetStatus($"Could not save config.json. See {Log.FilePath}", true);
        }
    }

    private void WriteConfig(AppConfig config)
    {
        string temporaryPath = _configPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
        File.Move(temporaryPath, _configPath, overwrite: true);
    }

    private void RegisterHotkeys()
    {
        UnregisterHotkeys();
        if (_editingSettings) return;
        _hotkeyRegistrationFailed = false;

        RegisterHotkey(CaptureHotkeyId, AppConfig.ParseKey(_config.CaptureKey));
        RegisterHotkey(DiscardHotkeyId, AppConfig.ParseKey(_config.DiscardKey));
        for (int index = 0; index < _config.Zones.Count; index++)
            RegisterHotkey(ZoneHotkeyBase + index, AppConfig.ParseKey(_config.Zones[index].Key));

        if (_hotkeyRegistrationFailed)
            SetStatus("A global hotkey is in use by another app; click the window to use shortcuts.", true);
    }

    private void RegisterHotkey(int id, Keys key)
    {
        if (key == Keys.None) return;
        if (RegisterHotKey(Handle, id, ModNoRepeat, (uint)key))
        {
            _registeredHotkeys.Add(id);
            return;
        }

        _hotkeyRegistrationFailed = true;
        Log.Error($"RegisterHotKey({AppConfig.FormatKey(key)}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    private void UpdateHotkeys()
    {
        if (IsHandleCreated)
            RegisterHotkeys();
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated) return;
        foreach (int id in _registeredHotkeys)
            UnregisterHotKey(Handle, id);
        _registeredHotkeys.Clear();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
        {
            if (_isFullScreen)
            {
                base.WndProc(ref message);
                return;
            }
            Point screenPoint = new(unchecked((short)(long)message.LParam),
                unchecked((short)((long)message.LParam >> 16)));
            int hit = GetResizeHitTest(screenPoint);
            if (hit != 0)
            {
                message.Result = (IntPtr)hit;
                return;
            }
        }

        if (message.Msg == WmHotkey)
        {
            if (_editingSettings || _captureInProgress) return;
            int id = message.WParam.ToInt32();
            if (id == CaptureHotkeyId)
                _ = CaptureRequested();
            else if (id == DiscardHotkeyId)
                DiscardPins();
            else if (id >= ZoneHotkeyBase && id < ZoneHotkeyBase + _config.Zones.Count)
                AssignZone(_config.Zones[id - ZoneHotkeyBase].Name);
            return;
        }
        base.WndProc(ref message);
    }

    private int GetResizeHitTest(Point screenPoint)
    {
        Rectangle bounds = Bounds;
        bool left = screenPoint.X < bounds.Left + ResizeGrip;
        bool right = screenPoint.X >= bounds.Right - ResizeGrip;
        bool top = screenPoint.Y < bounds.Top + ResizeGrip;
        bool bottom = screenPoint.Y >= bounds.Bottom - ResizeGrip;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 0;
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editingSettings) return;
        bool f11IsAssigned = AppConfig.ParseKey(_config.CaptureKey) == Keys.F11
            || AppConfig.ParseKey(_config.DiscardKey) == Keys.F11
            || _config.Zones.Any(zone => AppConfig.ParseKey(zone.Key) == Keys.F11);
        if (e.KeyCode == Keys.F11 && !f11IsAssigned)
        {
            ToggleFullScreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (_isFullScreen && e.KeyCode == Keys.Escape)
        {
            ToggleFullScreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == AppConfig.ParseKey(_config.CaptureKey)
            && !_registeredHotkeys.Contains(CaptureHotkeyId))
        {
            _ = CaptureRequested();
            e.Handled = true;
        }
        else if (e.KeyCode == AppConfig.ParseKey(_config.DiscardKey)
            && !_registeredHotkeys.Contains(DiscardHotkeyId))
        {
            DiscardPins();
            e.Handled = true;
        }
        else
        {
            int zoneIndex = _config.Zones.FindIndex(zone => AppConfig.ParseKey(zone.Key) == e.KeyCode);
            if (zoneIndex >= 0 && !_registeredHotkeys.Contains(ZoneHotkeyBase + zoneIndex))
            {
                AssignZone(_config.Zones[zoneIndex].Name);
                e.Handled = true;
            }
        }
    }

    private async Task CaptureRequested()
    {
        if (_captureInProgress || _editingSettings) return;
        _captureInProgress = true;
        try
        {
            if (_frameIsFrozen && _pins.Any(pin => !pin.Exported && string.IsNullOrWhiteSpace(pin.Zone)))
            {
                SetStatus("Assign a zone to every pin before capturing the next frame.", true);
                return;
            }

            if (_frameIsFrozen && _pins.Any(pin => !pin.Exported))
            {
                SavePins();
                _pins.Clear();
                _preview.Invalidate();
            }

            Bitmap? captured = null;
            if (_config.HideWhileCapturing)
            {
                Hide();
                await Task.Delay(150);
            }

            try
            {
                captured = CapturePrimaryMonitor(out Rectangle monitorBounds);
                string galleryPath = SaveNewGalleryCapture(captured, monitorBounds);
                SetCurrentGalleryCapture(galleryPath, captured, new GalleryCapture
                {
                    ScreenLeft = monitorBounds.Left,
                    ScreenTop = monitorBounds.Top,
                });
                captured = null;
            }
            finally
            {
                if (_config.HideWhileCapturing)
                    Show();
                captured?.Dispose();
            }

            if (_preview.Frame is not null)
                SetStatus($"Captured {_preview.Frame.Width}×{_preview.Frame.Height}. Click targets to add pins; {FormatConfiguredKey(_config.CaptureKey)} saves and advances.");
        }
        catch (Exception ex)
        {
            Log.Error("screen capture or crop save failed", ex);
            SetStatus($"Capture failed. See stderr / {Log.FilePath}", true);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private static Bitmap CapturePrimaryMonitor(out Rectangle bounds)
    {
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary monitor is available.");
        bounds = screen.Bounds;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC returned null.");
        }

        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr targetDc = graphics.GetHdc();
            try
            {
                if (!BitBlt(targetDc, 0, 0, bounds.Width, bounds.Height, screenDc,
                        bounds.Left, bounds.Top, SrcCopy | CaptureBlt))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            }
            finally
            {
                graphics.ReleaseHdc(targetDc);
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        return bitmap;
    }

    private string SaveNewGalleryCapture(Bitmap frame, Rectangle monitorBounds)
    {
        EnsureFolders();
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string imagePath = Path.Combine(_todoDirectory, $"{timestamp}.png");
        int suffix = 1;
        while (File.Exists(imagePath))
            imagePath = Path.Combine(_todoDirectory, $"{timestamp}_{suffix++}.png");

        string temporaryImage = imagePath + ".tmp";
        try
        {
            frame.Save(temporaryImage, ImageFormat.Png);
            File.Move(temporaryImage, imagePath);
            SaveGalleryState(imagePath, new GalleryCapture
            {
                ScreenLeft = monitorBounds.Left,
                ScreenTop = monitorBounds.Top,
            });
            return imagePath;
        }
        catch
        {
            if (File.Exists(temporaryImage)) File.Delete(temporaryImage);
            if (File.Exists(imagePath)) File.Delete(imagePath);
            throw;
        }
    }

    private void OpenGallery()
    {
        if (_captureInProgress || _editingSettings) return;
        using var dialog = new GalleryDialog(
            new[] { _todoDirectory, _legacyGalleryDirectory, _doneDirectory },
            _doneDirectory);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedCapturePath is null)
            return;

        try
        {
            using var source = new Bitmap(dialog.SelectedCapturePath);
            var frame = new Bitmap(source);
            GalleryCapture capture = LoadGalleryState(dialog.SelectedCapturePath, frame.Size);
            SetCurrentGalleryCapture(dialog.SelectedCapturePath, frame, capture);
            string collection = CaptureFileOperations.IsCompleted(dialog.SelectedCapturePath, _doneDirectory)
                ? "done"
                : "todo";
            SetStatus($"Opened {Path.GetFileName(dialog.SelectedCapturePath)} from {collection}. Scroll to zoom; middle-drag to pan.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Error($"opening gallery capture failed: {dialog.SelectedCapturePath}", ex);
            SetStatus("Could not open that gallery capture. See dataset/capture.log.", true);
        }
    }

    private GalleryCapture LoadGalleryState(string imagePath, Size frameSize)
    {
        string statePath = GetGalleryStatePath(imagePath);
        if (!File.Exists(statePath))
            return new GalleryCapture();

        var state = JsonSerializer.Deserialize<GalleryCapture>(File.ReadAllText(statePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The gallery pin data is empty or invalid.");
        state.Pins ??= new List<GalleryPin>();
        foreach (GalleryPin pin in state.Pins)
        {
            if (pin.X < 0 || pin.Y < 0 || pin.X >= frameSize.Width || pin.Y >= frameSize.Height)
                throw new InvalidDataException("A saved gallery pin lies outside its image.");
            if (!string.IsNullOrWhiteSpace(pin.Zone)
                && !_config.Zones.Any(zone => string.Equals(zone.Name, pin.Zone, StringComparison.OrdinalIgnoreCase)))
            {
                pin.Zone = null;
                pin.Exported = false;
            }
        }
        return state;
    }

    private void SetCurrentGalleryCapture(string path, Bitmap frame, GalleryCapture capture)
    {
        _preview.Frame?.Dispose();
        _preview.Frame = frame;
        _preview.ResetView();
        _currentGalleryPath = path;
        _currentGallery = capture;
        _pins.Clear();
        _pins.AddRange(capture.Pins.Select(pin => new ScreenPin(pin.X, pin.Y)
        {
            Zone = pin.Zone,
            Exported = pin.Exported,
        }));
        _frameIsFrozen = true;
        RefreshZoneButtonEnabledState();
        UpdateActiveZoneButton();
        UpdateDoneButtonState();
        _preview.Invalidate();
    }

    private void CompleteCurrentCapture()
    {
        if (_captureInProgress || _editingSettings || !_frameIsFrozen || _currentGalleryPath is null)
            return;
        if (CaptureFileOperations.IsCompleted(_currentGalleryPath, _doneDirectory))
        {
            SetStatus("This capture is already in dataset/done.");
            UpdateDoneButtonState();
            return;
        }
        if (_pins.Count == 0)
        {
            SetStatus("Add at least one labeled pin before marking this capture done.", true);
            return;
        }
        if (_pins.Any(pin => string.IsNullOrWhiteSpace(pin.Zone)))
        {
            SetStatus("Assign a zone to every pin before marking this capture done.", true);
            return;
        }

        try
        {
            SavePins();
            if (!PersistPinsOrReport())
                return;

            _currentGalleryPath = CaptureFileOperations.MoveToDone(_currentGalleryPath, _doneDirectory);
            UpdateDoneButtonState();
            SetStatus("Capture completed and moved to dataset/done. Labeled crops are in dataset/data.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Log.Error("completing capture failed", ex);
            SetStatus($"Could not complete this capture. See {Log.FilePath}", true);
        }
    }

    private void UpdateDoneButtonState() =>
        _doneButton.Enabled = _frameIsFrozen
            && _currentGalleryPath is not null
            && !CaptureFileOperations.IsCompleted(_currentGalleryPath, _doneDirectory);

    private static string GetGalleryStatePath(string imagePath) =>
        Path.ChangeExtension(imagePath, ".json");

    private void SaveGalleryState()
    {
        if (_currentGalleryPath is null) return;

        _currentGallery.Pins = _pins.Select(pin => new GalleryPin
        {
            X = pin.X,
            Y = pin.Y,
            Zone = pin.Zone,
            Exported = pin.Exported,
        }).ToList();
        SaveGalleryState(_currentGalleryPath, _currentGallery);
    }

    private static void SaveGalleryState(string imagePath, GalleryCapture capture)
    {
        string statePath = GetGalleryStatePath(imagePath);
        string temporaryPath = statePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(capture, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private void AddPin(Point location)
    {
        if (!_frameIsFrozen || _preview.Frame is null) return;
        _pins.Add(new ScreenPin(location.X, location.Y));
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        if (saved)
            SetStatus($"Pin {_pins.Count} added at ({location.X}, {location.Y}). Assign a zone with a hotkey or button.");
    }

    private void DeletePin(ScreenPin pin)
    {
        int index = _pins.IndexOf(pin);
        if (index < 0) return;
        _pins.RemoveAt(index);
        UpdateActiveZoneButton();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        if (saved) SetStatus($"Pin {index + 1} removed.");
    }

    private void AssignZone(string zoneName)
    {
        if (!_frameIsFrozen || _pins.Count == 0)
        {
            SetStatus("Add a pin to the frozen frame before assigning a zone.", true);
            return;
        }

        ScreenPin pin = _pins[^1];
        foreach (ScreenPin existing in _pins)
            existing.Confirming = false;
        pin.Zone = zoneName;
        pin.Exported = false;
        pin.Confirming = true;
        foreach (var (name, button) in _zoneButtons)
            button.Accent = string.Equals(name, zoneName, StringComparison.OrdinalIgnoreCase);
        _pinFlashTimer.Stop();
        _pinFlashTimer.Start();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        if (saved) SetStatus($"Pin {_pins.Count} assigned to {zoneName}.");
    }

    private void DiscardPins()
    {
        if (_pins.Count == 0)
        {
            SetStatus("There are no pins to discard.");
            return;
        }

        int removed = _pins.Count;
        _pins.Clear();
        UpdateActiveZoneButton();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        if (saved) SetStatus($"Discarded {removed} pin(s). The frozen frame is unchanged.");
    }

    private void SavePins()
    {
        if (_preview.Frame is null || _pins.Count == 0 || _currentGalleryPath is null) return;
        EnsureFolders();

        var frame = _preview.Frame;
        ScreenPin[] pendingPins = _pins.Where(pin => !pin.Exported).ToArray();
        if (pendingPins.Length == 0) return;
        string timestamp = CreateUniqueTimestamp(pendingPins);
        var pendingFiles = new List<string>();
        var temporaryFiles = new List<string>();
        var rows = new List<string>();

        try
        {
            for (int index = 0; index < pendingPins.Length; index++)
            {
                ScreenPin pin = pendingPins[index];
                string zone = pin.Zone ?? throw new InvalidOperationException("A pin has no zone label.");
                Rectangle cropBounds = GetCropBounds(pin, frame.Size, _config.CropSize);
                string filename = $"{timestamp}_{SafeFilenamePart(zone)}_{index + 1}.png";
                string outputPath = Path.Combine(_imageDirectory, filename);
                string temporaryPath = outputPath + ".tmp";

                using (var crop = frame.Clone(cropBounds, PixelFormat.Format32bppArgb))
                    crop.Save(temporaryPath, ImageFormat.Png);
                temporaryFiles.Add(temporaryPath);
                File.Move(temporaryPath, outputPath);
                temporaryFiles.Remove(temporaryPath);
                pendingFiles.Add(outputPath);

                int screenX = _currentGallery.ScreenLeft + pin.X;
                int screenY = _currentGallery.ScreenTop + pin.Y;
                rows.Add(CsvRow(filename, zone, cropBounds.Width, cropBounds.Height, screenX, screenY));
            }

            AppendCsvRows(rows);
        }
        catch
        {
            foreach (string file in temporaryFiles.Concat(pendingFiles))
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception cleanupException)
                {
                    Log.Error($"cleanup failed for {file}", cleanupException);
                }
            }
            throw;
        }

        foreach (ScreenPin pin in pendingPins)
        {
            pin.Exported = true;
            SetCount(pin.Zone!, _counts.GetValueOrDefault(pin.Zone!) + 1);
        }
        SaveGalleryState();
        SetStatus($"Saved {pendingPins.Length} labelled crop(s) to dataset/data. Capture and pin progress remain in dataset/todo.");
    }

    private string CreateUniqueTimestamp(IReadOnlyList<ScreenPin> pendingPins)
    {
        DateTime candidate = DateTime.Now;
        while (pendingPins.Select((pin, index) => Path.Combine(_imageDirectory,
                   $"{candidate:yyyyMMdd_HHmmss_fff}_{SafeFilenamePart(pin.Zone!)}_{index + 1}.png"))
               .Any(File.Exists))
            candidate = candidate.AddMilliseconds(1);
        return candidate.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
    }

    private bool PersistPinsOrReport()
    {
        try
        {
            SaveGalleryState();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("saving gallery pin state failed", ex);
            SetStatus($"Pin is active but could not be saved to todo capture. See {Log.FilePath}", true);
            return false;
        }
    }

    private static Rectangle GetCropBounds(ScreenPin pin, Size frameSize, int requestedSize)
    {
        int width = Math.Min(requestedSize, frameSize.Width);
        int height = Math.Min(requestedSize, frameSize.Height);
        int x = Math.Clamp(pin.X - width / 2, 0, frameSize.Width - width);
        int y = Math.Clamp(pin.Y - height / 2, 0, frameSize.Height - height);
        return new Rectangle(x, y, width, height);
    }

    private void AppendCsvRows(IReadOnlyCollection<string> rows)
    {
        bool needsHeader = !File.Exists(_csvPath) || new FileInfo(_csvPath).Length == 0;
        long originalLength = File.Exists(_csvPath) ? new FileInfo(_csvPath).Length : 0;
        try
        {
            using var stream = new FileStream(_csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (needsHeader) writer.WriteLine("filename,zone,width,height,x,y");
            foreach (string row in rows) writer.WriteLine(row);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (File.Exists(_csvPath))
            {
                try
                {
                    using var rollback = new FileStream(_csvPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    rollback.SetLength(originalLength);
                }
                catch (Exception rollbackException)
                {
                    Log.Error("labels.csv rollback failed", rollbackException);
                }
            }
            throw;
        }
    }

    private static string CsvRow(string filename, string zone, int width, int height, int x, int y) =>
        $"{EscapeCsv(filename)},{EscapeCsv(zone)},{width},{height},{x},{y}";

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string SafeFilenamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new(value.Where(character => !invalid.Contains(character)).ToArray());
        return safe.Replace(' ', '_');
    }

    private void SetCount(string zone, int count)
    {
        _counts[zone] = count;
        if (_countLabels.TryGetValue(zone, out Label? label))
            label.Text = count.ToString();
    }

    private static void DisposeChildren(Control parent)
    {
        foreach (Control child in parent.Controls.Cast<Control>().ToArray())
        {
            parent.Controls.Remove(child);
            child.Dispose();
        }
    }

    private void ResetCounts()
    {
        if (MessageBox.Show(this, "Reset the displayed counts? Existing dataset images and CSV rows will not be changed.",
                "Reset counts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        foreach (ZoneConfig zone in _config.Zones)
            SetCount(zone.Name, 0);
        SetStatus("Counts reset. Existing files were left untouched.");
    }

    private void OpenDatasetFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { Log.Directory },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("opening dataset folder failed", ex);
            SetStatus("Could not open dataset folder. See capture.log.", true);
        }
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(Log.Directory);
        Directory.CreateDirectory(_imageDirectory);
        Directory.CreateDirectory(_todoDirectory);
        Directory.CreateDirectory(_doneDirectory);
    }

    private void RefreshZoneButtonEnabledState()
    {
        foreach (RoundedButton button in _zoneButtonsPanel.Controls)
            button.Enabled = _frameIsFrozen;
    }

    private void UpdateActiveZoneButton()
    {
        string? activeZone = _pins.LastOrDefault()?.Zone;
        foreach (var (name, button) in _zoneButtons)
            button.Accent = activeZone is not null
                && string.Equals(name, activeZone, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatConfiguredKey(string value)
    {
        Keys key = AppConfig.ParseKey(value);
        return key == Keys.None ? "—" : AppConfig.FormatKey(key);
    }

    private void SetStatus(string message, bool error = false)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = error ? Theme.Warn : Theme.Muted;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _restoreBounds = Bounds;
            Screen screen = Screen.FromControl(this);
            _isFullScreen = true;
            Region?.Dispose();
            Region = null;
            Bounds = screen.Bounds;
            _fullScreenButton.Text = "⛶";
            _windowToolTip.SetToolTip(_fullScreenButton, "Exit full screen (F11 or Esc)");
            return;
        }

        _isFullScreen = false;
        Rectangle restore = _restoreBounds;
        if (restore.Width < MinimumSize.Width || restore.Height < MinimumSize.Height)
            restore = new Rectangle(Left + 24, Top + 24, 1380, 900);

        Rectangle workingArea = Screen.FromRectangle(restore).WorkingArea;
        if (!workingArea.IntersectsWith(restore))
            restore.Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - restore.Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - restore.Height) / 2));
        restore.Width = Math.Min(restore.Width, workingArea.Width);
        restore.Height = Math.Min(restore.Height, workingArea.Height);
        Bounds = restore;
        _fullScreenButton.Text = "⛶";
        _windowToolTip.SetToolTip(_fullScreenButton, "Enter full screen (F11)");
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (_isFullScreen)
        {
            Region?.Dispose();
            Region = null;
            return;
        }
        if (Width <= 0 || Height <= 0) return;
        using var path = RoundedButton.CreateRoundRectangle(new Rectangle(0, 0, Width, Height), 16);
        Region? previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Border, 1);
        using var path = RoundedButton.CreateRoundRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (int id in _registeredHotkeys)
            UnregisterHotKey(Handle, id);
        _registeredHotkeys.Clear();
        _pinFlashTimer.Stop();
        if (_timerResolutionAcquired)
            timeEndPeriod(1);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pinFlashTimer.Dispose();
            _windowToolTip.Dispose();
            _preview.Frame?.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ScreenPreview : Control
    {
        private ScreenPin? _hoveredPin;
        private RectangleF _imageRectangle;
        private Point _panStartMouse;
        private PointF _panStartOffset;
        private PointF _panOffset;
        private float _zoom = 1f;
        private bool _isPanning;
        private readonly Font _emptyTitleFont = new("Segoe UI Semibold", 13f, FontStyle.Bold);
        private readonly Font _emptyBodyFont = new("Segoe UI", 9.5f);
        private readonly Font _emptyHintFont = new("Consolas", 8f, FontStyle.Bold);
        private readonly Font _dimensionFont = new("Consolas", 9f, FontStyle.Bold);
        private readonly Font _instructionFont = new("Consolas", 8f, FontStyle.Bold);
        private readonly Font _zoomFont = new("Consolas", 8f, FontStyle.Bold);
        private readonly Font _pinLabelFont = new("Consolas", 8f, FontStyle.Bold);

        public Bitmap? Frame { get; set; }
        public string CaptureShortcut { get; set; } = "Q";
        public List<ScreenPin> Pins { get; set; } = null!;
        public event Action<Point>? PinCreated;
        public event Action<ScreenPin>? PinDeleteRequested;

        public void ResetView()
        {
            _zoom = 1f;
            _panOffset = PointF.Empty;
            _hoveredPin = null;
            _isPanning = false;
        }

        public ScreenPreview()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.Selectable
                     | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            BackColor = Theme.BgDeep;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(Theme.BgDeep);
            if (Frame is null)
            {
                DrawEmptyState(graphics);
                return;
            }

            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            _imageRectangle = CalculateImageRectangle();
            float scale = _imageRectangle.Width / Frame.Width;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(Frame, _imageRectangle);
            using (var border = new Pen(Theme.Border, 1))
                graphics.DrawRectangle(border, _imageRectangle.X, _imageRectangle.Y, _imageRectangle.Width, _imageRectangle.Height);

            foreach (ScreenPin pin in Pins)
                DrawPin(graphics, pin, scale);

            var dimensionText = $"{Frame.Width} × {Frame.Height}";
            TextRenderer.DrawText(graphics, dimensionText, _dimensionFont,
                new Point(12, 10), Color.FromArgb(230, 255, 255, 255));
            var instruction = "LEFT CLICK  PIN     RIGHT CLICK  DELETE     WHEEL  ZOOM     MIDDLE-DRAG  PAN";
            TextRenderer.DrawText(graphics, instruction, _instructionFont,
                new Rectangle(12, Height - 28, Width - 24, 18), Color.FromArgb(210, 255, 255, 255),
                TextFormatFlags.Bottom | TextFormatFlags.Left);
            DrawZoomBadge(graphics);
        }

        private void DrawEmptyState(Graphics graphics)
        {
            int cardWidth = Math.Min(420, ClientSize.Width - 48);
            int cardHeight = 178;
            if (cardWidth < 220 || ClientSize.Height < 100) return;
            var card = new Rectangle((ClientSize.Width - cardWidth) / 2,
                (ClientSize.Height - cardHeight) / 2, cardWidth, cardHeight);
            using var cardPath = RoundedButton.CreateRoundRectangle(card, 14);
            using var fill = new LinearGradientBrush(card, Theme.PanelTop, Theme.Panel, 90f);
            using var border = new Pen(Theme.BorderSoft);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(fill, cardPath);
            graphics.DrawPath(border, cardPath);

            var iconBounds = new Rectangle(card.X + 26, card.Y + 27, 38, 38);
            using (var glow = new SolidBrush(Theme.AccentSoft))
                graphics.FillEllipse(glow, iconBounds);
            using (var dot = new SolidBrush(Theme.Accent))
                graphics.FillEllipse(dot, iconBounds.X + 12, iconBounds.Y + 12, 14, 14);

            TextRenderer.DrawText(graphics, "READY TO LABEL", _emptyTitleFont,
                new Rectangle(card.X + 76, card.Y + 29, card.Width - 100, 26),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, "Capture the primary display to start pinning.",
                _emptyBodyFont, new Rectangle(card.X + 28, card.Y + 88, card.Width - 56, 22),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, $"{CaptureShortcut} CAPTURE     ·     WHEEL ZOOM     ·     MIDDLE-DRAG PAN",
                _emptyHintFont, new Rectangle(card.X + 28, card.Y + 126, card.Width - 56, 20),
                Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawZoomBadge(Graphics graphics)
        {
            string text = $"{_zoom * 100:0}%";
            Size textSize = TextRenderer.MeasureText(text, _zoomFont);
            var bounds = new Rectangle(Width - textSize.Width - 38, 10, textSize.Width + 24, 24);
            using var path = RoundedButton.CreateRoundRectangle(bounds, 7);
            using var background = new SolidBrush(Color.FromArgb(225, 15, 20, 32));
            using var border = new Pen(Theme.Border);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);
            TextRenderer.DrawText(graphics, text, _zoomFont, bounds, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawPin(Graphics graphics, ScreenPin pin, float scale)
        {
            float x = _imageRectangle.Left + (pin.X + 0.5f) * scale;
            float y = _imageRectangle.Top + (pin.Y + 0.5f) * scale;
            float radius = Math.Max(5f, Math.Min(10f, 8f / Math.Max(scale, 0.15f)));
            bool hovered = ReferenceEquals(pin, _hoveredPin);
            Color color = pin.Confirming ? Theme.Success : Theme.Accent;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var glow = new SolidBrush(Color.FromArgb(hovered ? 100 : 55, color)))
                graphics.FillEllipse(glow, x - radius * 2.2f, y - radius * 2.2f, radius * 4.4f, radius * 4.4f);
            using (var fill = new SolidBrush(hovered ? Color.FromArgb(155, 150, 255) : color))
                graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
            using (var outline = new Pen(Color.FromArgb(230, 255, 255, 255), 1.2f))
                graphics.DrawEllipse(outline, x - radius, y - radius, radius * 2, radius * 2);

            if (!string.IsNullOrWhiteSpace(pin.Zone))
            {
                var label = new Rectangle((int)x + 10, (int)y - 11, 130, 19);
                using var background = new SolidBrush(Color.FromArgb(210, 11, 14, 20));
                graphics.FillRectangle(background, label);
                TextRenderer.DrawText(graphics, pin.Zone, _pinLabelFont,
                    label, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Frame is null) return;
            if (e.Button == MouseButtons.Middle && _zoom > 1f)
            {
                _isPanning = true;
                _panStartMouse = e.Location;
                _panStartOffset = _panOffset;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Left && TryMapPoint(e.Location, out Point point))
            {
                PinCreated?.Invoke(point);
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                ScreenPin? pin = FindPin(e.Location);
                if (pin is not null) PinDeleteRequested?.Invoke(pin);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isPanning)
            {
                _panOffset = new PointF(
                    _panStartOffset.X + e.X - _panStartMouse.X,
                    _panStartOffset.Y + e.Y - _panStartMouse.Y);
                Invalidate();
                return;
            }
            ScreenPin? hovered = FindPin(e.Location);
            if (!ReferenceEquals(_hoveredPin, hovered))
            {
                _hoveredPin = hovered;
                Invalidate();
            }
            Cursor = Frame is not null && _imageRectangle.Contains(e.Location)
                ? hovered is not null ? Cursors.Hand : Cursors.Cross
                : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = false;
                Capture = false;
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Frame is null || !_imageRectangle.Contains(e.Location)) return;

            float oldScale = _imageRectangle.Width / Frame.Width;
            float imageX = (e.X - _imageRectangle.Left) / oldScale;
            float imageY = (e.Y - _imageRectangle.Top) / oldScale;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), 1f, 16f);
            if (_zoom <= 1f)
            {
                _panOffset = PointF.Empty;
            }
            else
            {
                float newScale = GetFitScale() * _zoom;
                _panOffset = new PointF(
                    e.X - (ClientSize.Width - Frame.Width * newScale) / 2f - imageX * newScale,
                    e.Y - (ClientSize.Height - Frame.Height * newScale) / 2f - imageY * newScale);
            }
            _imageRectangle = CalculateImageRectangle();
            Invalidate();
        }

        private bool TryMapPoint(Point clientPoint, out Point point)
        {
            point = Point.Empty;
            if (Frame is null || !_imageRectangle.Contains(clientPoint)) return false;
            float xScale = Frame.Width / _imageRectangle.Width;
            float yScale = Frame.Height / _imageRectangle.Height;
            point = new Point(
                Math.Clamp((int)((clientPoint.X - _imageRectangle.Left) * xScale), 0, Frame.Width - 1),
                Math.Clamp((int)((clientPoint.Y - _imageRectangle.Top) * yScale), 0, Frame.Height - 1));
            return true;
        }

        private ScreenPin? FindPin(Point clientPoint)
        {
            if (Frame is null || _imageRectangle.Width <= 0) return null;
            float scaleX = _imageRectangle.Width / Frame.Width;
            float scaleY = _imageRectangle.Height / Frame.Height;
            ScreenPin? closest = null;
            float closestDistance = 18 * 18;
            foreach (ScreenPin pin in Pins)
            {
                float distance = DistanceSquared(clientPoint,
                    new PointF(_imageRectangle.Left + (pin.X + 0.5f) * scaleX,
                        _imageRectangle.Top + (pin.Y + 0.5f) * scaleY));
                if (distance > closestDistance) continue;
                closest = pin;
                closestDistance = distance;
            }
            return closest;
        }

        private float GetFitScale()
        {
            if (Frame is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return 1f;
            return Math.Min(ClientSize.Width / (float)Frame.Width, ClientSize.Height / (float)Frame.Height);
        }

        private RectangleF CalculateImageRectangle()
        {
            if (Frame is null) return RectangleF.Empty;
            float scale = GetFitScale() * _zoom;
            float width = Frame.Width * scale;
            float height = Frame.Height * scale;
            return new RectangleF(
                (ClientSize.Width - width) / 2f + _panOffset.X,
                (ClientSize.Height - height) / 2f + _panOffset.Y,
                width,
                height);
        }

        private static float DistanceSquared(Point point, PointF target)
        {
            float dx = point.X - target.X;
            float dy = point.Y - target.Y;
            return dx * dx + dy * dy;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _emptyTitleFont.Dispose();
                _emptyBodyFont.Dispose();
                _emptyHintFont.Dispose();
                _dimensionFont.Dispose();
                _instructionFont.Dispose();
                _zoomFont.Dispose();
                _pinLabelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

internal sealed class ScreenPin(int x, int y)
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public string? Zone { get; set; }
    public bool Confirming { get; set; }
    public bool Exported { get; set; }
}

internal sealed class RoundedButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public bool Accent { get; set; }
    public bool Quiet { get; set; }

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color parentColor = Theme.Bg;
        for (Control? parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.BackColor.A == 255)
            {
                parentColor = parent.BackColor;
                break;
            }
        }
        graphics.Clear(parentColor);
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        Color fill = !Enabled
            ? Theme.BgDeep
            : _pressed ? Theme.PanelHi
            : Accent ? Theme.AccentSoft
            : _hovered ? Theme.PanelHi : Quiet ? Theme.BgDeep : Theme.Panel;
        Color edge = Accent ? Theme.Accent : _hovered ? Theme.Accent : Quiet ? Theme.BorderSoft : Theme.Border;

        if (Enabled && (_hovered || Accent))
        {
            using var glow = new Pen(Color.FromArgb(Accent ? 42 : 62, Theme.Accent), 3);
            using var glowPath = CreateRoundRectangle(bounds, 9);
            graphics.DrawPath(glow, glowPath);
        }
        using (var path = CreateRoundRectangle(bounds, 9))
        using (var brush = new LinearGradientBrush(bounds, fill, Color.FromArgb(
                   Math.Min(255, fill.R + 8), Math.Min(255, fill.G + 8), Math.Min(255, fill.B + 12)), 90f))
        using (var pen = new Pen(edge, 1))
        {
            graphics.FillPath(brush, path);
            graphics.DrawPath(pen, path);
        }

        Color textColor = !Enabled ? Theme.Muted : Accent ? Color.White : ForeColor;
        TextRenderer.DrawText(graphics, Text, Font, bounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    internal static GraphicsPath CreateRoundRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
