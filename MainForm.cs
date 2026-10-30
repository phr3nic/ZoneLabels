using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

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
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int CaptureHotkeyId = 1;
    private const int DiscardHotkeyId = 2;
    private const int ZoneHotkeyBase = 10;
    private const int PinConfirmationMs = 550;
    private const int CropSize = 96;
    private const int ExpandedSidebarWidth = 272;
    private const int CollapsedSidebarWidth = 56;
    private const int SidebarCollapseBreakpoint = 1280;
    private static readonly Font ZoneButtonFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    private static readonly Font ZoneCountFont = new("Consolas", 8.25f);

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private readonly string _imageDirectory = Path.Combine(Log.Directory, "data");
    private readonly string _todoDirectory = Path.Combine(Log.Directory, "todo");
    private readonly string _doneDirectory = Path.Combine(Log.Directory, "done");
    private readonly string _csvPath = Path.Combine(Log.Directory, "labels.csv");
    private readonly DatasetManifestStore _manifestStore = new(Log.Directory);
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZoneCountRow> _countLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoundedButton> _zoneButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _registeredHotkeys = new();
    private readonly HashSet<Control> _dropEnabledControls = new();
    private readonly List<ScreenPin> _pins = new();
    private readonly List<ScreenPin> _placedPins = new();
    private readonly Stack<ScreenPin> _redoPins = new();
    private readonly ScreenPreview _preview;
    private readonly CropPreview _cropPreview;
    private CropPreviewSurface? _cropPreviewSurface;
    private readonly HorizontalFlowLayoutPanel _zoneButtonsPanel;
    private readonly HorizontalFlowLayoutPanel _shortcutPanel;
    private readonly FlowLayoutPanel _countList;
    private readonly Label _statusLabel;
    private readonly Label _statusIcon;
    private readonly Label _hotkeyIndicator;
    private readonly RoundedButton _captureButton;
    private readonly RoundedButton _saveButton;
    private readonly RoundedButton _discardButton;
    private readonly RoundedButton _doneButton;
    private readonly RoundedButton _fullScreenButton;
    private readonly ToolTip _windowToolTip;
    private readonly ToggleRow _alwaysOnTopCheck;
    private readonly ToggleRow _hideWhileCapturingCheck;
    private GlassPanel? _sidebarSurface;
    private TableLayoutPanel? _workspaceLayout;
    private TableLayoutPanel? _sidebarLayout;
    private Label? _sidebarCaption;
    private GlassPanel? _countsFrame;
    private FlowLayoutPanel? _utilityActions;
    private FlowLayoutPanel? _mainActions;
    private FlowLayoutPanel? _toggles;
    private TableLayoutPanel? _sidebarFooter;
    private RoundedButton? _openFolderButton;
    private RoundedButton? _refreshCountsButton;
    private RoundedButton? _settingsButton;
    private RoundedButton? _galleryButton;
    private RoundedButton? _sidebarToggleButton;
    private readonly System.Windows.Forms.Timer _pinFlashTimer;
    private readonly System.Windows.Forms.Timer _discardClickTimer;
    private readonly System.Windows.Forms.Timer _boundsSaveTimer;
    private AppConfig _config;
    private string? _currentGalleryPath;
    private GalleryCapture _currentGallery = new();
    private bool _frameIsFrozen;
    private bool _currentFrameIsDataCrop;
    private bool _captureInProgress;
    private bool _editingSettings;
    private bool _discardArmed;
    private bool _precisionStatusActive;
    private string _statusMessage = string.Empty;
    private bool _timerResolutionAcquired;
    private bool _hotkeyRegistrationFailed;
    private bool _isFullScreen;
    private bool _sidebarCollapsed;
    private int _lastResponsiveWidth;
    private Rectangle _restoreBounds;

    public MainForm()
    {
        uint timerResult = timeBeginPeriod(1);
        _timerResolutionAcquired = timerResult == 0;
        if (!_timerResolutionAcquired)
            Log.Error($"timeBeginPeriod(1) failed with result {timerResult}.");

        Directory.CreateDirectory(Log.Directory);
        Directory.CreateDirectory(_imageDirectory);
        Directory.CreateDirectory(_todoDirectory);
        _manifestStore.LoadOrMigrate();
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

        if (!File.Exists(_configPath))
        {
            try
            {
                WriteConfig(_config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("creating config.json with defaults failed", ex);
                _configWarning = "Could not create config.json. See dataset/capture.log.";
            }
        }

        PurgeDoneOnStartup();
        EnsureFolders();
        RecountLabels();

        Text = "Zone Labeller";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1380, 900);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.Manual;
        RestoreWindowBounds();
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
        _preview.PinMoved += SaveMovedPin;
        _preview.PinZonePickerRequested += ShowPinZonePicker;
        _preview.PreviewPinChanged += UpdateCropPreview;
        _preview.PrecisionStateChanged += UpdatePrecisionStatus;
        _cropPreview = new CropPreview();

        _zoneButtonsPanel = new HorizontalFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0, 8, 0, 8),
            BackColor = Color.Transparent,
        };
        _shortcutPanel = new HorizontalFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = new Padding(16, 0, 16, 0),
            BackColor = Theme.Bg,
        };
        _countList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = Padding.Empty,
            BackColor = Theme.Panel,
        };
        _countList.SizeChanged += (_, _) => UpdateCountRowWidths();
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 8.25f),
            Margin = new Padding(8, 0, 0, 0),
        };
        _statusIcon = new Label
        {
            Text = "●",
            Anchor = AnchorStyles.None,
            Width = 16,
            Height = 16,
            ForeColor = Theme.Success,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0),
        };
        _hotkeyIndicator = new Label
        {
            Text = "●",
            Width = 16,
            Height = 16,
            Anchor = AnchorStyles.None,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8f),
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Hotkeys suppressed",
            Margin = new Padding(0),
        };
        _captureButton = new RoundedButton { Text = "Capture", Width = 160, Height = 30, Accent = true };
        _saveButton = new RoundedButton { Text = "Save", Width = 64, Height = 30 };
        _discardButton = new RoundedButton { Text = "Discard", Width = 88, Height = 30 };
        _doneButton = new RoundedButton
        {
            Text = "Done",
            Width = 72,
            Height = 30,
            Enabled = false,
        };
        _fullScreenButton = new RoundedButton { Text = "⛶", Width = 32, Height = 32, Quiet = true };
        _windowToolTip = new ToolTip { BackColor = Theme.Panel, ForeColor = Theme.Text, InitialDelay = 450 };
        _windowToolTip.SetToolTip(_fullScreenButton, "Enter full screen (F11)");
        _windowToolTip.SetToolTip(_doneButton, "Save labeled crops and move this capture to dataset\\done");
        _windowToolTip.SetToolTip(_saveButton, "Save this capture's current pins without exporting crops");
        _windowToolTip.SetToolTip(_discardButton, "Press twice within 2 seconds to discard; double-click to discard immediately");
        _alwaysOnTopCheck = new ToggleRow("Always on top");
        _hideWhileCapturingCheck = new ToggleRow("Hide while capturing");
        _pinFlashTimer = new System.Windows.Forms.Timer { Interval = PinConfirmationMs };
        _discardClickTimer = new System.Windows.Forms.Timer
        {
            Interval = SystemInformation.DoubleClickTime + 50,
        };
        _discardClickTimer.Tick += (_, _) =>
        {
            _discardClickTimer.Stop();
            _discardArmed = false;
            SetStatus("Discard confirmation expired.");
        };
        _boundsSaveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _boundsSaveTimer.Tick += (_, _) =>
        {
            _boundsSaveTimer.Stop();
            SaveWindowBounds();
        };
        _pinFlashTimer.Tick += (_, _) =>
        {
            _pinFlashTimer.Stop();
            foreach (var pin in _pins) pin.Confirming = false;
            _preview.Invalidate();
        };
        BuildUi();
        _sidebarCollapsed = _config.SidebarCollapsed ?? ClientSize.Width < SidebarCollapseBreakpoint;
        if (!_config.SidebarCollapsed.HasValue)
        {
            _config.SidebarCollapsed = _sidebarCollapsed;
            SaveCurrentConfig();
        }
        ApplySidebarCollapsed(_sidebarCollapsed);
        EnableFileDrop(this);
        AttachHotkeyFocusEvents(this);
        ApplyMainButtonTooltips(this);
        RefreshConfigUi();
        RefreshZoneUi();
        TopMost = _config.AlwaysOnTop;
        _alwaysOnTopCheck.Checked = _config.AlwaysOnTop;
        _hideWhileCapturingCheck.Checked = _config.HideWhileCapturing;
        SetStatus(_configWarning ?? GetCaptureReadyMessage());
        _lastResponsiveWidth = ClientSize.Width;
        Resize += (_, _) =>
        {
            int width = ClientSize.Width;
            UpdateSidebarCountCapacity();
            if (_lastResponsiveWidth >= SidebarCollapseBreakpoint
                && width < SidebarCollapseBreakpoint
                && !_sidebarCollapsed)
            {
                _sidebarCollapsed = true;
                _config.SidebarCollapsed = true;
                ApplySidebarCollapsed(true);
                SaveCurrentConfig();
            }
            _lastResponsiveWidth = width;
            UpdateWindowRegion();
        };
        Shown += (_, _) => UpdateWindowRegion();
        Activated += (_, _) => UpdateHotkeyIndicator();
        Deactivate += (_, _) => UpdateHotkeyIndicator();
        Move += (_, _) => ScheduleWindowBoundsSave();
        ResizeEnd += (_, _) => ScheduleWindowBoundsSave();
        KeyDown += OnFormKeyDown;
        KeyUp += (_, _) => _preview.RefreshPrecisionState();
        _captureButton.Click += async (_, _) => await CaptureRequested();
        _saveButton.Click += (_, _) => SaveCurrentProgress();
        _discardButton.Click += (_, _) =>
        {
            RequestDiscard();
        };
        _discardButton.DoubleClick += (_, _) =>
        {
            RequestDiscard(bypassConfirmation: true);
        };
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
        _sidebarToggleButton!.Click += (_, _) =>
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            _config.SidebarCollapsed = _sidebarCollapsed;
            ApplySidebarCollapsed(_sidebarCollapsed);
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
            Padding = Padding.Empty,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        root.Controls.Add(BuildTitleBar(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        root.Controls.Add(BuildBottomBand(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildBottomBand()
    {
        var band = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            BackColor = Theme.Bg,
        };
        band.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        band.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        band.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        band.Controls.Add(CreateDivider(), 0, 0);
        band.Controls.Add(_shortcutPanel, 0, 1);
        band.Controls.Add(BuildZoneBar(), 0, 2);
        RefreshShortcutRow();
        return band;
    }

    private Control BuildTitleBar()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(16, 0, 16, 0),
            Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Paint += (_, e) =>
        {
            using var border = new Pen(Theme.Border);
            e.Graphics.DrawLine(border, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        var titleGroup = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Theme.Bg,
        };
        var title = new Label
        {
            Text = "Zone Labeller",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0),
        };
        var version = new Label
        {
            Text = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.1",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.25f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0),
        };
        _sidebarToggleButton = new RoundedButton
        {
            Text = string.Empty,
            Width = 32,
            Height = 32,
            Quiet = true,
            Icon = RoundedButtonIcon.Sidebar,
            AccessibleName = "Toggle sidebar",
            Margin = new Padding(0),
        };
        _windowToolTip.SetToolTip(_sidebarToggleButton, "Collapse or restore the sidebar");
        titleGroup.Controls.Add(title);
        titleGroup.Controls.Add(version);
        titleGroup.Controls.Add(_sidebarToggleButton);
        title.MouseDown += DragWindow;
        title.DoubleClick += (_, _) => ToggleFullScreen();
        version.MouseDown += DragWindow;
        version.DoubleClick += (_, _) => ToggleFullScreen();
        header.Controls.Add(titleGroup, 0, 0);

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(8, 0, 16, 0),
            BackColor = Theme.Bg,
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        status.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        status.Height = 48;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.AutoSize = false;
        _statusLabel.Margin = Padding.Empty;
        _statusIcon.Anchor = AnchorStyles.None;
        _hotkeyIndicator.Anchor = AnchorStyles.None;
        status.Controls.Add(_statusIcon, 0, 0);
        status.Controls.Add(_statusLabel, 1, 0);
        status.Controls.Add(_hotkeyIndicator, 2, 0);
        header.Controls.Add(status, 1, 0);

        var rightControls = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
        };
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 16, 0),
            Padding = new Padding(0, 9, 0, 9),
            BackColor = Theme.Bg,
        };
        foreach (RoundedButton button in new[] { _captureButton, _discardButton, _saveButton, _doneButton })
        {
            button.Height = 30;
            button.Margin = new Padding(0, 0, button == _doneButton ? 0 : 8, 0);
            actions.Controls.Add(button);
        }

        var divider = CreateDivider();
        divider.Width = 1;
        divider.Height = 30;
        divider.Dock = DockStyle.None;
        divider.Margin = new Padding(0, 0, 16, 0);

        var windowControls = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 8),
            BackColor = Theme.Bg,
        };
        var close = new RoundedButton { Text = "×", Width = 32, Height = 32, Quiet = true };
        var minimize = new RoundedButton { Text = "−", Width = 32, Height = 32, Quiet = true };
        _fullScreenButton.Width = 32;
        _fullScreenButton.Height = 32;
        foreach (RoundedButton button in new[] { minimize, _fullScreenButton, close })
        {
            button.Margin = new Padding(0, 0, 8, 0);
            windowControls.Controls.Add(button);
        }
        windowControls.Controls[windowControls.Controls.Count - 1].Margin = Padding.Empty;

        _windowToolTip.SetToolTip(close, "Close Zone Labeller");
        _windowToolTip.SetToolTip(minimize, "Minimize");
        close.Click += (_, _) => Close();
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        rightControls.Controls.Add(actions);
        rightControls.Controls.Add(divider);
        rightControls.Controls.Add(windowControls);
        header.Controls.Add(rightControls, 2, 0);
        return header;
    }

    private Control BuildWorkspace()
    {
        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ExpandedSidebarWidth));
        _workspaceLayout = workspace;

        var previewFrame = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.BgDeep,
            EdgeColor = Theme.Border,
            Padding = new Padding(1),
            Margin = new Padding(0),
        };
        _preview.BackColor = Theme.BgDeep;
        previewFrame.Controls.Add(_preview);

        var cropSurface = _cropPreviewSurface = new CropPreviewSurface
        {
            Size = new Size(208, 232),
            BackColor = Theme.Panel,
            Padding = new Padding(8),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Margin = new Padding(0),
            Visible = false,
        };
        var cropLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Theme.Panel,
        };
        cropLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        cropLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));
        var cropCaption = new Label
        {
            Text = "CROP · 96 × 96",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.25f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        _cropPreview.Size = new Size(192, 192);
        _cropPreview.Margin = new Padding(0);
        cropLayout.Controls.Add(cropCaption, 0, 0);
        cropLayout.Controls.Add(_cropPreview, 0, 1);
        cropSurface.Controls.Add(cropLayout);
        _windowToolTip.SetToolTip(cropSurface,
            "Right-drag over the canvas to pan while zoomed. Scroll over the canvas to zoom.");
        _windowToolTip.SetToolTip(_cropPreview,
            "Right-drag over the canvas to pan while zoomed. Scroll over the canvas to zoom.");
        cropSurface.Location = new Point(previewFrame.ClientSize.Width - cropSurface.Width - 16,
            previewFrame.ClientSize.Height - cropSurface.Height - 16);
        previewFrame.Controls.Add(cropSurface);
        cropSurface.BringToFront();
        previewFrame.SizeChanged += (_, _) =>
            cropSurface.Location = new Point(previewFrame.ClientSize.Width - cropSurface.Width - 16,
                previewFrame.ClientSize.Height - cropSurface.Height - 16);

        workspace.Controls.Add(previewFrame, 0, 0);
        workspace.Controls.Add(BuildSidebar(), 1, 0);
        return workspace;
    }

    private Control BuildSidebar()
    {
        var sidebar = _sidebarSurface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.Border,
            Padding = new Padding(16),
            Margin = Padding.Empty,
        };
        var layout = _sidebarLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var caption = _sidebarCaption = new Label
        {
            Text = "LABEL COUNTS",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 7.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };
        var countsFrame = _countsFrame = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.Border,
            Padding = new Padding(0),
            Margin = Padding.Empty,
        };
        countsFrame.Controls.Add(_countList);
        var utilityActions = _utilityActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
        };
        var openFolder = _openFolderButton = new RoundedButton
        {
            Width = 32,
            Height = 32,
            Icon = RoundedButtonIcon.Folder,
            AccessibleName = "Open dataset folder",
        };
        var reset = _refreshCountsButton = new RoundedButton
        {
            Width = 32,
            Height = 32,
            Icon = RoundedButtonIcon.Refresh,
            AccessibleName = "Refresh counts",
        };
        _windowToolTip.SetToolTip(openFolder, "Open dataset folder");
        _windowToolTip.SetToolTip(reset, "Refresh counts");
        openFolder.Margin = new Padding(0, 0, 8, 0);
        reset.Margin = new Padding(0);
        openFolder.Click += (_, _) => OpenDatasetFolder();
        reset.Click += (_, _) => RefreshCounts();
        utilityActions.Controls.Add(reset);
        utilityActions.Controls.Add(openFolder);

        var mainActions = _mainActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
        };
        var settings = _settingsButton = new RoundedButton
        {
            Text = "Label settings",
            Width = ExpandedSidebarWidth - 34,
            Height = 30,
            AccessibleName = "Label settings",
            Margin = new Padding(0, 0, 0, 8),
        };
        var gallery = _galleryButton = new RoundedButton
        {
            Text = "Capture library",
            Width = ExpandedSidebarWidth - 34,
            Height = 30,
            AccessibleName = "Capture library",
            Margin = Padding.Empty,
        };
        _windowToolTip.SetToolTip(settings, "Edit label zones, targets, retention, and shortcuts.");
        _windowToolTip.SetToolTip(gallery, "Browse and reopen saved captures.");
        settings.Click += (_, _) => EditSettings();
        gallery.Click += (_, _) => OpenGallery();
        mainActions.Controls.Add(settings);
        mainActions.Controls.Add(gallery);

        var toggles = _toggles = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
        };
        _alwaysOnTopCheck.Margin = new Padding(0, 0, 0, 4);
        _hideWhileCapturingCheck.Margin = Padding.Empty;
        _windowToolTip.SetToolTip(_alwaysOnTopCheck, "Keep Zone Labeller above other windows.");
        _windowToolTip.SetToolTip(_hideWhileCapturingCheck, "Hide Zone Labeller while capturing the screen.");
        toggles.Controls.Add(_alwaysOnTopCheck);
        toggles.Controls.Add(_hideWhileCapturingCheck);

        var sidebarFooter = _sidebarFooter = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        sidebarFooter.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        sidebarFooter.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        var repositoryLink = new LinkLabel
        {
            Text = "github.com/phr3nic/ZoneLabels",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Text,
            VisitedLinkColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.25f),
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
            TabStop = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
        };
        repositoryLink.AccessibleName = "Open ZoneLabels GitHub repository";
        _windowToolTip.SetToolTip(repositoryLink, "Open github.com/phr3nic/ZoneLabels");
        repositoryLink.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/phr3nic/ZoneLabels",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Error("opening repository link failed", ex);
                SetStatus($"Could not open repository link. See {Log.FilePath}.", true);
            }
        };
        var sidebarVersion = new Label
        {
            Text = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.1",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.25f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };
        sidebarFooter.Controls.Add(repositoryLink, 0, 0);
        sidebarFooter.Controls.Add(sidebarVersion, 0, 1);

        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(countsFrame, 0, 1);
        layout.Controls.Add(CreateDivider(), 0, 2);
        layout.Controls.Add(utilityActions, 0, 3);
        layout.Controls.Add(mainActions, 0, 4);
        layout.Controls.Add(toggles, 0, 5);
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 6);
        layout.Controls.Add(sidebarFooter, 0, 7);
        sidebar.Controls.Add(layout);
        return sidebar;
    }

    private Control BuildZoneBar()
    {
        _zoneButtonsPanel.Dock = DockStyle.Fill;
        _zoneButtonsPanel.WrapContents = false;
        _zoneButtonsPanel.AutoScroll = true;
        _zoneButtonsPanel.Padding = new Padding(16, 0, 16, 0);
        _zoneButtonsPanel.Margin = Padding.Empty;
        _zoneButtonsPanel.VerticalScroll.Enabled = false;
        _zoneButtonsPanel.VerticalScroll.Visible = false;
        return _zoneButtonsPanel;
    }

    private void RefreshShortcutRow()
    {
        _shortcutPanel.SuspendLayout();
        DisposeChildren(_shortcutPanel);
        (string Key, string Label)[] shortcuts =
        [
            (FormatConfiguredKey(_config.CaptureKey), "Capture"),
            (FormatConfiguredKey(_config.DiscardKey), "Discard"),
            ("Ctrl+Z", "Undo"),
            ("Ctrl+Shift+Z", "Redo"),
            (_config.PrecisionKey, "Precision"),
            ("Wheel", "Zoom"),
            ("Right drag", "Pan"),
        ];
        foreach ((string key, string label) in shortcuts)
        {
            var hint = new ShortcutHint(key, label)
            {
                Margin = new Padding(0, 0, 14, 0),
            };
            _shortcutPanel.Controls.Add(hint);
        }
        _shortcutPanel.ResumeLayout(true);
    }

    private void ApplySidebarCollapsed(bool collapsed)
    {
        if (_sidebarSurface is null || _workspaceLayout is null || _sidebarLayout is null)
            return;

        _sidebarCollapsed = collapsed;
        int width = collapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        _workspaceLayout.ColumnStyles[1].Width = width;
        _sidebarSurface.Padding = new Padding(collapsed ? 8 : 16);
        _sidebarCaption!.Visible = !collapsed;
        _countsFrame!.Visible = !collapsed;
        _sidebarFooter!.Visible = !collapsed;

        _sidebarLayout.RowStyles[0].Height = collapsed ? 0 : 18;
        if (collapsed)
            _sidebarLayout.RowStyles[1].Height = 0;
        else
            UpdateSidebarCountCapacity();
        _sidebarLayout.RowStyles[2].Height = 1;
        _sidebarLayout.RowStyles[3].Height = collapsed ? 72 : 32;
        _sidebarLayout.RowStyles[4].Height = collapsed ? 72 : 68;
        _sidebarLayout.RowStyles[5].Height = 68;
        _sidebarLayout.RowStyles[7].Height = collapsed ? 0 : 38;

        _utilityActions!.FlowDirection = collapsed ? FlowDirection.TopDown : FlowDirection.RightToLeft;
        _utilityActions.Controls.SetChildIndex(
            collapsed ? _openFolderButton! : _refreshCountsButton!, 0);
        foreach (RoundedButton button in new[] { _openFolderButton!, _refreshCountsButton! })
        {
            button.Margin = collapsed ? new Padding(0, 0, 0, 8) : new Padding(0);
            button.Quiet = false;
        }

        int contentWidth = Math.Max(32, width - (collapsed ? 18 : 34));
        _mainActions!.FlowDirection = FlowDirection.TopDown;
        _settingsButton!.Width = collapsed ? 32 : contentWidth;
        _galleryButton!.Width = collapsed ? 32 : contentWidth;
        _settingsButton.Height = collapsed ? 32 : 30;
        _galleryButton.Height = collapsed ? 32 : 30;
        _settingsButton.Text = collapsed ? string.Empty : "Label settings";
        _galleryButton.Text = collapsed ? string.Empty : "Capture library";
        _settingsButton.Icon = collapsed ? RoundedButtonIcon.Settings : RoundedButtonIcon.None;
        _galleryButton.Icon = collapsed ? RoundedButtonIcon.Library : RoundedButtonIcon.None;
        _settingsButton.Margin = collapsed ? new Padding(0, 0, 0, 8) : new Padding(0, 0, 0, 8);
        _galleryButton.Margin = Padding.Empty;

        _alwaysOnTopCheck.ShowLabel = !collapsed;
        _hideWhileCapturingCheck.ShowLabel = !collapsed;
        _alwaysOnTopCheck.Width = contentWidth;
        _hideWhileCapturingCheck.Width = contentWidth;
        _sidebarToggleButton!.Icon = collapsed ? RoundedButtonIcon.SidebarExpand : RoundedButtonIcon.Sidebar;
        _windowToolTip.SetToolTip(_sidebarToggleButton,
            collapsed ? "Restore the sidebar" : "Collapse the sidebar");
        UpdateCountRowWidths();
        PerformLayout();
    }

    private void UpdateSidebarCountCapacity()
    {
        if (_sidebarLayout is null || _sidebarSurface is null || _sidebarCollapsed)
            return;

        int fixedHeight = 18 + 1 + 32 + 68 + 68 + 38;
        int availableHeight = Math.Max(42,
            _sidebarSurface.ClientSize.Height - _sidebarSurface.Padding.Vertical - fixedHeight);
        int contentHeight = Math.Max(42, _config.Zones.Count * 38 + 2);
        _sidebarLayout.RowStyles[1].Height = Math.Min(320, Math.Min(availableHeight, contentHeight));
    }

    private static Control CreateDivider() => new Panel
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Border,
        Margin = new Padding(0),
    };

    private void RefreshConfigUi()
    {
        string capture = FormatConfiguredKey(_config.CaptureKey);
        string discard = FormatConfiguredKey(_config.DiscardKey);
        _captureButton.Text = "Capture";
        _captureButton.TrailingHint = capture;
        _discardButton.Text = "Discard";
        FitButtonToText(_captureButton);
        FitButtonToText(_discardButton);
        FitButtonToText(_saveButton);
        FitButtonToText(_doneButton);
        RefreshShortcutRow();
        bool globalCapture = AppConfig.ParseHotkey(_config.CaptureKey).Modifiers.HasFlag(HotkeyModifiers.Alt);
        _windowToolTip.SetToolTip(_captureButton, $"Capture ({capture}). "
            + (globalCapture
                ? "Works globally while the pointer is outside this window."
                : "Works while the canvas has focus."));
        _windowToolTip.SetToolTip(_discardButton,
            $"Discard ({discard}). Press twice within 2 seconds; double-click to discard immediately.");
        _windowToolTip.SetToolTip(_saveButton, "Save current pin progress without exporting crops.");
        _windowToolTip.SetToolTip(_doneButton, "Export labeled crops and move this capture to dataset\\done.");
        _preview.CaptureShortcut = capture;
        _preview.PrecisionKey = AppConfig.ParsePrecisionKey(_config.PrecisionKey);
        _preview.PrecisionFactor = _config.PrecisionFactor;
        _preview.MinimumPinSpacing = _config.MinPinSpacing;
        _windowToolTip.SetToolTip(_preview,
            $"Capture: {capture}. Right-drag to pan while zoomed; scroll to zoom; pin shortcuts work when this canvas has focus.");
        UpdateHotkeyIndicator();
    }

    private static void FitButtonToText(RoundedButton button)
    {
        int width = TextRenderer.MeasureText(button.Text, button.Font).Width + 24;
        if (!string.IsNullOrEmpty(button.TrailingHint))
            width += TextRenderer.MeasureText(button.TrailingHint, button.Font).Width + 16;
        button.Width = Math.Max(32, width);
    }

    private void ApplyMainButtonTooltips(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button button && string.IsNullOrWhiteSpace(_windowToolTip.GetToolTip(button)))
            {
                string text = string.IsNullOrWhiteSpace(button.AccessibleName)
                    ? button.Text
                    : button.AccessibleName;
                _windowToolTip.SetToolTip(button, string.IsNullOrWhiteSpace(text) ? "Button" : text);
            }
            ApplyMainButtonTooltips(child);
        }
    }

    private string GetCaptureReadyMessage() =>
        AppConfig.ParseHotkey(_config.CaptureKey).Modifiers.HasFlag(HotkeyModifiers.Alt)
            ? $"Ready. Press {FormatConfiguredKey(_config.CaptureKey)} outside this window."
            : $"Ready. Press {FormatConfiguredKey(_config.CaptureKey)} while this window is focused.";

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

            string keyText = FormatConfiguredKey(zone.Key);
            int target = GetTargetCount(zone.Name);
            var button = new RoundedButton
            {
                Text = zone.Name,
                KeyChipText = keyText,
                SubLabel = $"{_counts[zone.Name]} / {target}",
                SubLabelColor = _counts[zone.Name] < target ? Theme.Warn : Theme.Muted,
                Width = 96,
                Height = 36,
                Margin = new Padding(0, 0, 6, 0),
                Font = ZoneButtonFont,
                Enabled = _frameIsFrozen,
                Selected = _pins.LastOrDefault()?.Zone is string activeZone
                    && string.Equals(activeZone, zone.Name, StringComparison.OrdinalIgnoreCase),
            };
            int labelWidth = Math.Max(
                TextRenderer.MeasureText(zone.Name, ZoneButtonFont).Width,
                TextRenderer.MeasureText(button.SubLabel, ZoneCountFont).Width);
            button.Width = Math.Max(96, labelWidth + 18 + 8 + 24);
            string zoneName = zone.Name;
            button.Click += (_, _) => AssignZone(zoneName);
            _windowToolTip.SetToolTip(button, $"Label {zone.Name} ({keyText})");
            _zoneButtonsPanel.Controls.Add(button);
            _zoneButtons[zone.Name] = button;

            var countRow = new ZoneCountRow(zone.Name, _counts[zone.Name], GetTargetCount(zone.Name))
            {
                Width = Math.Max(0, _countList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth),
                Height = 38,
                Margin = Padding.Empty,
            };
            _countLabels[zone.Name] = countRow;
            _countList.Controls.Add(countRow);
        }

        _zoneButtonsPanel.ResumeLayout(true);
        ApplyMainButtonTooltips(_zoneButtonsPanel);
        _countList.ResumeLayout(true);
        UpdateSidebarCountCapacity();
        UpdateCountRowWidths();
        RefreshConfigUi();
        UpdateHotkeys();
    }

    private void EditSettings()
    {
        if (_captureInProgress) return;
        var viewport = _preview.CaptureViewState();
        _editingSettings = true;
        UpdateHotkeyIndicator();
        try
        {
            using var dialog = new SettingsDialog(_config, SaveSettingsFromDialog, PurgeDoneFromSettings);
            dialog.ShowDialog(this);
        }
        finally
        {
            _editingSettings = false;
            UpdateHotkeys();
            UpdateHotkeyIndicator();
            _preview.RestoreViewState(viewport);
        }
    }

    private bool SaveSettingsFromDialog(AppConfig updated)
    {
        updated.AlwaysOnTop = _config.AlwaysOnTop;
        updated.HideWhileCapturing = _config.HideWhileCapturing;
        updated.SidebarCollapsed = _config.SidebarCollapsed;
        updated.MinPinSpacing = _config.MinPinSpacing;
        if (WindowState == FormWindowState.Normal && !_isFullScreen)
        {
            updated.WindowX = Left;
            updated.WindowY = Top;
            updated.WindowWidth = Width;
            updated.WindowHeight = Height;
        }
        else
        {
            updated.WindowX = _config.WindowX;
            updated.WindowY = _config.WindowY;
            updated.WindowWidth = _config.WindowWidth;
            updated.WindowHeight = _config.WindowHeight;
        }
        AppConfig.Validate(updated);
        WriteConfig(updated);
        _config = updated;
        RecountLabels();
        bool removedAssignedZone = false;
        foreach (ScreenPin pin in _pins)
        {
            if (pin.Zone is not null
                && !_config.Zones.Any(zone => string.Equals(zone.Name, pin.Zone, StringComparison.OrdinalIgnoreCase)))
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

    private void RestoreWindowBounds()
    {
        bool hasSavedBounds = _config.WindowX.HasValue
            && _config.WindowY.HasValue
            && _config.WindowWidth.HasValue
            && _config.WindowHeight.HasValue;
        Rectangle bounds = hasSavedBounds
            ? new Rectangle(_config.WindowX!.Value, _config.WindowY!.Value,
                _config.WindowWidth!.Value, _config.WindowHeight!.Value)
            : Bounds;
        Screen primary = Screen.PrimaryScreen ?? Screen.FromControl(this);
        Screen screen = hasSavedBounds
            ? Screen.AllScreens.FirstOrDefault(candidate => candidate.WorkingArea.IntersectsWith(bounds)) ?? primary
            : primary;
        Rectangle workingArea = screen.WorkingArea;
        MinimumSize = new Size(Math.Min(800, workingArea.Width), Math.Min(600, workingArea.Height));
        if (!hasSavedBounds)
            bounds.Location = new Point(
                workingArea.Left + (workingArea.Width - bounds.Width) / 2,
                workingArea.Top + (workingArea.Height - bounds.Height) / 2);
        int width = Math.Clamp(bounds.Width, MinimumSize.Width, workingArea.Width);
        int height = Math.Clamp(bounds.Height, MinimumSize.Height, workingArea.Height);
        int x = Math.Clamp(bounds.X, workingArea.Left, workingArea.Right - width);
        int y = Math.Clamp(bounds.Y, workingArea.Top, workingArea.Bottom - height);
        Bounds = new Rectangle(x, y, width, height);
    }

    private void ScheduleWindowBoundsSave()
    {
        if (WindowState != FormWindowState.Normal || _isFullScreen || !IsHandleCreated) return;
        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Start();
    }

    private void SaveWindowBounds()
    {
        if (WindowState != FormWindowState.Normal || _isFullScreen) return;
        _config.WindowX = Left;
        _config.WindowY = Top;
        _config.WindowWidth = Width;
        _config.WindowHeight = Height;
        SaveCurrentConfig();
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
        _hotkeyRegistrationFailed = false;

        HotkeyBinding captureHotkey = AppConfig.ParseHotkey(_config.CaptureKey);
        if (captureHotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            RegisterHotkey(CaptureHotkeyId, captureHotkey);
        HotkeyBinding discardHotkey = AppConfig.ParseHotkey(_config.DiscardKey);
        if (discardHotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            RegisterHotkey(DiscardHotkeyId, discardHotkey);
        for (int index = 0; index < _config.Zones.Count; index++)
        {
            HotkeyBinding zoneHotkey = AppConfig.ParseHotkey(_config.Zones[index].Key);
            if (zoneHotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
                RegisterHotkey(ZoneHotkeyBase + index, zoneHotkey);
        }

        if (_hotkeyRegistrationFailed)
            SetStatus("A global hotkey is in use by another app; click the window to use shortcuts.", true);
        UpdateHotkeyIndicator();
    }

    private void RegisterHotkey(int id, HotkeyBinding hotkey)
    {
        if (hotkey.Key == Keys.None || !hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) return;
        uint modifiers = ModNoRepeat;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= ModControl;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= ModAlt;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= ModShift;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= ModWindows;
        if (RegisterHotKey(Handle, id, modifiers, (uint)hotkey.Key))
        {
            _registeredHotkeys.Add(id);
            return;
        }

        _hotkeyRegistrationFailed = true;
        Log.Error($"RegisterHotKey({AppConfig.FormatHotkey(hotkey)}) failed: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
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
            if (IsTextInputFocused() || _captureInProgress) return;
            int id = message.WParam.ToInt32();
            if (id == CaptureHotkeyId)
                _ = CaptureRequested(requirePointerOutside: true);
            else if (id == DiscardHotkeyId)
                RequestDiscard();
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
        _preview.RefreshPrecisionState();
        if (_editingSettings || IsTextInputFocused()) return;
        if (AppConfig.ParseHotkey(_config.DiscardKey).Matches(e.KeyCode, e.Modifiers)
            && !_registeredHotkeys.Contains(DiscardHotkeyId))
        {
            RequestDiscard();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (!_preview.Focused) return;

        if (e.KeyCode == Keys.Escape
            && HotkeyBinding.ReadModifiers(e.Modifiers) == HotkeyModifiers.None
            && _preview.CancelPan())
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D6
            && HotkeyBinding.ReadModifiers(e.Modifiers) == HotkeyModifiers.Control)
        {
            int zoneIndex = (int)e.KeyCode - (int)Keys.D1;
            if (zoneIndex < _config.Zones.Count)
                AssignZone(_config.Zones[zoneIndex].Name);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Z
            && HotkeyBinding.ReadModifiers(e.Modifiers) == (HotkeyModifiers.Control | HotkeyModifiers.Shift))
        {
            RedoLastPin();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Z
            && HotkeyBinding.ReadModifiers(e.Modifiers) == HotkeyModifiers.Control)
        {
            UndoLastPin();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        bool f11IsAssigned = IsUnmodifiedF11(_config.CaptureKey)
            || IsUnmodifiedF11(_config.DiscardKey)
            || _config.Zones.Any(zone => IsUnmodifiedF11(zone.Key));
        bool escapeIsAssigned = IsUnmodifiedEscape(_config.CaptureKey)
            || IsUnmodifiedEscape(_config.DiscardKey)
            || _config.Zones.Any(zone => IsUnmodifiedEscape(zone.Key));
        if (e.KeyCode == Keys.F11
            && HotkeyBinding.ReadModifiers(e.Modifiers) == HotkeyModifiers.None
            && !f11IsAssigned)
        {
            ToggleFullScreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (_isFullScreen && e.KeyCode == Keys.Escape
            && HotkeyBinding.ReadModifiers(e.Modifiers) == HotkeyModifiers.None
            && !escapeIsAssigned)
        {
            ToggleFullScreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (AppConfig.ParseHotkey(_config.CaptureKey).Matches(e.KeyCode, e.Modifiers)
            && !_registeredHotkeys.Contains(CaptureHotkeyId))
        {
            HotkeyBinding captureHotkey = AppConfig.ParseHotkey(_config.CaptureKey);
            _ = CaptureRequested(requirePointerOutside: captureHotkey.Modifiers.HasFlag(HotkeyModifiers.Alt));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else
        {
            int zoneIndex = _config.Zones.FindIndex(zone =>
                AppConfig.ParseHotkey(zone.Key).Matches(e.KeyCode, e.Modifiers));
            if (zoneIndex >= 0 && !_registeredHotkeys.Contains(ZoneHotkeyBase + zoneIndex))
            {
                AssignZone(_config.Zones[zoneIndex].Name);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
    }

    private async Task CaptureRequested(bool requirePointerOutside = false)
    {
        if (_captureInProgress || _editingSettings) return;
        if (requirePointerOutside && Bounds.Contains(Cursor.Position))
        {
            SetStatus("Move the pointer outside ZoneLab, then press the capture shortcut.", true);
            return;
        }
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
            }

            if (_frameIsFrozen && _pins.Count > 0)
            {
                if (!PersistPinsOrReport())
                    return;
                _pins.Clear();
                _preview.RefreshPinSpacingWarnings();
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
                SetStatus($"Captured {_preview.Frame.Width}×{_preview.Frame.Height}. Click targets to add pins; {FormatConfiguredKey(_config.CaptureKey)} captures another frame.");
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
            _manifestStore.AddCapture(imagePath, "todo");
            return imagePath;
        }
        catch
        {
            if (File.Exists(temporaryImage)) File.Delete(temporaryImage);
            if (File.Exists(imagePath)) File.Delete(imagePath);
            string statePath = Path.ChangeExtension(imagePath, ".json");
            if (File.Exists(statePath)) File.Delete(statePath);
            throw;
        }
    }

    private void EnableFileDrop(Control control)
    {
        if (!_dropEnabledControls.Add(control)) return;

        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                && paths.Any(IsImportableDropPath))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        };
        control.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                ImportDroppedPaths(paths);
        };
        control.ControlAdded += (_, e) =>
        {
            if (e.Control is Control addedControl)
                EnableFileDrop(addedControl);
        };
        foreach (Control child in control.Controls)
            EnableFileDrop(child);
    }

    private static bool IsImportableDropPath(string path) =>
        Directory.Exists(path)
        || (File.Exists(path)
            && (CaptureFileOperations.IsSupportedImageFile(path)
                || string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)));

    private void ImportDroppedPaths(IEnumerable<string> paths)
    {
        var failures = new List<string>();
        int imported = 0;
        try
        {
            Directory.CreateDirectory(_todoDirectory);
            foreach (string path in paths)
                ImportDroppedPath(path, ref imported, failures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("preparing todo folder for dropped images failed", ex);
            SetStatus($"Could not import dropped images. See {Log.FilePath}.", true);
            return;
        }

        if (failures.Count > 0)
        {
            SetStatus(imported > 0
                ? $"Added {imported} image(s) to todo; some items could not be imported. See {Log.FilePath}."
                : $"Could not import dropped images. See {Log.FilePath}.", true);
        }
        else if (imported > 0)
        {
            SetStatus($"Added {imported} image(s) to todo.");
        }
    }

    private void ImportDroppedPath(string path, ref int imported, List<string> failures)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return;

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                    ImportDroppedPath(entry, ref imported, failures);
                return;
            }

            if (CaptureFileOperations.IsSupportedImageFile(path))
            {
                CopyImageToTodo(path);
                imported++;
            }
            else if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                imported += ImportImagesFromZip(path, failures);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Log.Error($"importing dropped item failed: {path}", ex);
            failures.Add(path);
        }
    }

    private void CopyImageToTodo(string sourcePath)
    {
        string destinationPath = GetUniqueTodoPath(Path.GetFileName(sourcePath));
        string temporaryPath = Path.Combine(_todoDirectory, $".{Guid.NewGuid():N}.importing");
        try
        {
            File.Copy(sourcePath, temporaryPath);
            File.Move(temporaryPath, destinationPath);
            try
            {
                _manifestStore.AddCapture(destinationPath, "todo");
            }
            catch
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                throw;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private int ImportImagesFromZip(string zipPath, List<string> failures)
    {
        int imported = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)
                || !CaptureFileOperations.IsSupportedImageFile(entry.FullName))
                continue;

            string entryName = Path.GetFileName(entry.FullName.Replace('/', '\\'));
            string destinationPath = GetUniqueTodoPath(entryName);
            string temporaryPath = Path.Combine(_todoDirectory, $".{Guid.NewGuid():N}.importing");
            try
            {
                using (Stream input = entry.Open())
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.Move(temporaryPath, destinationPath);
                try
                {
                    _manifestStore.AddCapture(destinationPath, "todo");
                }
                catch
                {
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    throw;
                }
                imported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or NotSupportedException)
            {
                Log.Error($"importing ZIP image failed: {zipPath}:{entry.FullName}", ex);
                failures.Add(zipPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        return imported;
    }

    private string GetUniqueTodoPath(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalidCharacter, '_');
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "image";

        string candidate = Path.Combine(_todoDirectory, baseName + extension);
        int suffix = 1;
        while (File.Exists(candidate)
            || File.Exists(Path.ChangeExtension(candidate, ".json"))
            || Directory.EnumerateFiles(_todoDirectory)
                .Any(path => CaptureFileOperations.IsSupportedImageFile(path)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(candidate),
                        StringComparison.OrdinalIgnoreCase)))
            candidate = Path.Combine(_todoDirectory, $"{baseName}_{suffix++}{extension}");
        return candidate;
    }

    private void OpenGallery()
    {
        if (_captureInProgress || _editingSettings) return;
        string? selectedCapturePath = null;
        var viewport = _preview.CaptureViewState();
        try
        {
            using var dialog = new GalleryDialog(
                _doneDirectory,
                _imageDirectory,
                _manifestStore);
            if (dialog.ShowDialog(this) == DialogResult.OK)
                selectedCapturePath = dialog.SelectedCapturePath;
        }
        finally
        {
            _preview.RestoreViewState(viewport);
        }

        if (selectedCapturePath is not null)
            TryOpenGalleryCapture(selectedCapturePath);
    }

    private bool TryOpenGalleryCapture(string imagePath, ScreenPreview.ViewState? viewport = null)
    {
        try
        {
            using var source = new Bitmap(imagePath);
            var frame = new Bitmap(source);
            GalleryCapture capture = LoadGalleryState(imagePath, frame.Size);
            SetCurrentGalleryCapture(imagePath, frame, capture, viewport);
            string collection = IsDataCrop(imagePath)
                ? "exported data"
                : _manifestStore.GetStatus(imagePath);
            SetStatus($"Opened {Path.GetFileName(imagePath)} from {collection}. Scroll to zoom; right-drag to pan.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Error($"opening gallery capture failed: {imagePath}", ex);
            SetStatus("Could not open that gallery capture. See dataset/capture.log.", true);
            return false;
        }
    }

    private string? FindNextTodoCapture()
    {
        return _manifestStore.Manifest.Captures
            .Where(entry => entry.Status == "todo")
            .Select(entry => _manifestStore.ResolveFile(entry.File))
            .Where(File.Exists)
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private GalleryCapture LoadGalleryState(string imagePath, Size frameSize)
    {
        string statePath = GetGalleryStatePath(imagePath);
        GalleryCapture state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<GalleryCapture>(File.ReadAllText(statePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? throw new InvalidDataException("The gallery pin data is empty or invalid.")
            : new GalleryCapture();
        state.Pins ??= new List<GalleryPin>();
        if (!IsDataCrop(imagePath))
        {
            DatasetCaptureEntry entry = _manifestStore.FindCapture(imagePath)
                ?? throw new InvalidDataException($"Capture is not present in manifest.json: {imagePath}");
            var exported = state.Pins.Where(pin => pin.Exported)
                .Select(pin => (pin.X, pin.Y, pin.Zone))
                .ToHashSet();
            state.Pins = entry.Pins.Select(pin => new GalleryPin
            {
                X = pin.X,
                Y = pin.Y,
                Zone = pin.Zone,
                Exported = exported.Contains((pin.X, pin.Y, pin.Zone)),
            }).ToList();
        }
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

    private void SetCurrentGalleryCapture(
        string path,
        Bitmap frame,
        GalleryCapture capture,
        ScreenPreview.ViewState? viewport = null)
    {
        _preview.Frame?.Dispose();
        _preview.Frame = frame;
        if (viewport is null)
            _preview.ResetView();
        _currentGalleryPath = path;
        _currentFrameIsDataCrop = IsDataCrop(path);
        _currentGallery = capture;
        _pins.Clear();
        _placedPins.Clear();
        _redoPins.Clear();
        UpdateDoneButtonState();
        _pins.AddRange(capture.Pins.Select(pin => new ScreenPin(pin.X, pin.Y)
        {
            Zone = pin.Zone,
            Exported = pin.Exported,
        }));
        _preview.RefreshPinSpacingWarnings();
        _frameIsFrozen = true;
        RefreshZoneButtonEnabledState();
        UpdateActiveZoneButton();
        UpdateDoneButtonState();
        _preview.Invalidate();
        _preview.SelectedPin = _pins.LastOrDefault();
        if (viewport is not null)
            _preview.RestoreViewState(viewport.Value);
        UpdateCropPreview(_preview.HoveredPin);
    }

    private void CompleteCurrentCapture()
    {
        if (_captureInProgress || _editingSettings || !_frameIsFrozen
            || _currentFrameIsDataCrop || _currentGalleryPath is null)
            return;
        if (_manifestStore.GetStatus(_currentGalleryPath) == "done")
        {
            AdvanceToNextTodo(Path.GetFileName(_currentGalleryPath));
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

        string completedPath;
        try
        {
            SavePins();
            if (!PersistPinsOrReport())
                return;

            string sourcePath = _currentGalleryPath;
            completedPath = _manifestStore.MoveToDone(sourcePath, _doneDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Log.Error("completing capture failed", ex);
            SetStatus($"Could not complete this capture. See {Log.FilePath}", true);
            return;
        }

        AdvanceToNextTodo(Path.GetFileName(completedPath));
    }

    private void AdvanceToNextTodo(string completedFileName)
    {
        ScreenPreview.ViewState viewport = _preview.CaptureViewState();
        string? nextCapture = FindNextTodoCapture();
        if (nextCapture is null)
        {
            ClearCurrentCapture();
            SetStatus($"Completed {completedFileName}. No more todo images; crops are in dataset/data.");
            return;
        }

        if (TryOpenGalleryCapture(nextCapture, viewport))
            SetStatus($"Completed {completedFileName}. Next image: {Path.GetFileName(nextCapture)}.");
        else
            SetStatus($"Completed {completedFileName}, but the next image could not be opened. See {Log.FilePath}.", true);
    }

    private void ClearCurrentCapture()
    {
        _preview.Frame?.Dispose();
        _preview.Frame = null;
        _preview.ResetView();
        _currentGalleryPath = null;
        _currentGallery = new GalleryCapture();
        _currentFrameIsDataCrop = false;
        _pins.Clear();
        _placedPins.Clear();
        _preview.SelectedPin = null;
        _preview.RefreshPinSpacingWarnings();
        _frameIsFrozen = false;
        RefreshZoneButtonEnabledState();
        UpdateDoneButtonState();
        UpdateActiveZoneButton();
        _preview.Invalidate();
        UpdateCropPreview(null);
    }

    private void UpdateDoneButtonState()
    {
        _doneButton.Enabled = _frameIsFrozen
            && !_currentFrameIsDataCrop
            && _currentGalleryPath is not null
            && (_manifestStore.GetStatus(_currentGalleryPath) == "done"
                || _pins.Count > 0 && _pins.All(pin => !string.IsNullOrWhiteSpace(pin.Zone)));
    }

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
        if (!_currentFrameIsDataCrop)
        {
            _manifestStore.UpdatePins(_currentGalleryPath, _pins.Select(pin => new DatasetManifestPin
            {
                X = pin.X,
                Y = pin.Y,
                Zone = pin.Zone,
            }));
        }
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
        _redoPins.Clear();
        var pin = new ScreenPin(location.X, location.Y);
        bool tooClose = _pins.Any(existing => ArePinsTooClose(pin, existing));
        _pins.Add(pin);
        _placedPins.Add(pin);
        UpdateDoneButtonState();
        _preview.RefreshPinSpacingWarnings();
        _preview.SelectedPin = pin;
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        UpdateCropPreview(pin);
        if (tooClose)
            SetStatus(saved
                ? $"Too close: pins must be at least {_config.MinPinSpacing}px apart."
                : $"Too close. Pin is active but could not be saved; see {Log.FilePath}.", true);
        else if (saved)
            SetStatus($"Pin {_pins.Count} added at ({location.X}, {location.Y}). Assign a zone with a hotkey or button.");
    }

    private void DeletePin(ScreenPin pin)
    {
        RemovePin(pin);
    }

    private void SaveMovedPin(ScreenPin pin)
    {
        _preview.RefreshPinSpacingWarnings();
        _preview.SelectedPin = pin;
        UpdateCropPreview(pin);
        PersistPinsOrReport();
    }

    private void ShowPinZonePicker(ScreenPin pin, Point location)
    {
        if (!_pins.Contains(pin)) return;
        var picker = new ContextMenuStrip
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
        };
        foreach (ZoneConfig zone in _config.Zones)
        {
            string zoneName = zone.Name;
            var item = new ToolStripMenuItem(zoneName)
            {
                Checked = string.Equals(pin.Zone, zoneName, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };
            item.Click += (_, _) =>
            {
                AssignZone(pin, zoneName);
                picker.Close();
            };
            picker.Items.Add(item);
        }
        picker.Closed += (_, _) => picker.Dispose();
        picker.Show(_preview, location);
    }

    private bool RemovePin(ScreenPin pin)
    {
        int index = _pins.IndexOf(pin);
        if (index < 0) return false;
        _pins.RemoveAt(index);
        _placedPins.Remove(pin);
        _preview.RefreshPinSpacingWarnings();
        if (ReferenceEquals(_preview.SelectedPin, pin))
            _preview.SelectedPin = _pins.LastOrDefault();
        _preview.ClearHoveredPin(pin);
        UpdateDoneButtonState();
        UpdateActiveZoneButton();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        UpdateCropPreview(_preview.HoveredPin);
        if (saved) SetStatus($"Pin {index + 1} removed.");
        return saved;
    }

    private void UndoLastPin()
    {
        while (_placedPins.Count > 0)
        {
            ScreenPin pin = _placedPins[^1];
            _placedPins.RemoveAt(_placedPins.Count - 1);
            if (!_pins.Contains(pin)) continue;

            bool saved = RemovePin(pin);
            if (!saved) return;
            _redoPins.Push(pin);
            if (_pins.Any(existing => _pins.Any(other => !ReferenceEquals(existing, other)
                    && ArePinsTooClose(existing, other))))
                SetStatus($"Too close: pins must be at least {_config.MinPinSpacing}px apart.", true);
            else
                SetStatus($"Undid the most recently placed pin. {_pins.Count} pin(s) remain.");
            return;
        }

        SetStatus("There are no newly placed pins to undo.");
    }

    private void RedoLastPin()
    {
        if (_redoPins.Count == 0)
        {
            SetStatus("There are no pins to redo.");
            return;
        }

        ScreenPin pin = _redoPins.Pop();
        _pins.Add(pin);
        _placedPins.Add(pin);
        _preview.RefreshPinSpacingWarnings();
        _preview.SelectedPin = pin;
        UpdateDoneButtonState();
        UpdateActiveZoneButton();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        UpdateCropPreview(pin);
        if (saved) SetStatus($"Redid pin {_pins.Count}.");
    }

    private bool ArePinsTooClose(ScreenPin first, ScreenPin second)
    {
        if (_config.MinPinSpacing <= 0) return false;
        long dx = first.X - second.X;
        long dy = first.Y - second.Y;
        long spacing = _config.MinPinSpacing;
        return dx * dx + dy * dy <= spacing * spacing;
    }

    private void AssignZone(string zoneName)
    {
        if (!_frameIsFrozen || _pins.Count == 0)
        {
            SetStatus("Add a pin to the frozen frame before assigning a zone.", true);
            return;
        }

        AssignZone(_pins[^1], zoneName);
    }

    private void AssignZone(ScreenPin pin, string zoneName)
    {
        if (!_frameIsFrozen || !_pins.Contains(pin)) return;
        foreach (ScreenPin existing in _pins)
            existing.Confirming = false;
        pin.Zone = zoneName;
        pin.Exported = false;
        pin.Confirming = true;
        foreach (var (name, button) in _zoneButtons)
            button.Selected = string.Equals(name, zoneName, StringComparison.OrdinalIgnoreCase);
        _pinFlashTimer.Stop();
        _pinFlashTimer.Start();
        UpdateDoneButtonState();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        if (saved) SetStatus($"Pin {_pins.Count} assigned to {zoneName}.");
    }

    private void SaveCurrentProgress()
    {
        if (!_frameIsFrozen || _currentGalleryPath is null)
        {
            SetStatus("There is no current capture to save.", true);
            return;
        }

        if (PersistPinsOrReport())
            SetStatus("Current capture and pin progress saved. Crops were not exported.");
    }

    private void RequestDiscard(bool bypassConfirmation = false)
    {
        if (_pins.Count == 0)
        {
            if (bypassConfirmation) return;
            _discardClickTimer.Stop();
            _discardArmed = false;
            SetStatus("There are no pins to discard.");
            return;
        }

        if (!bypassConfirmation && !_discardArmed)
        {
            _discardArmed = true;
            _discardClickTimer.Stop();
            _discardClickTimer.Interval = 2000;
            _discardClickTimer.Start();
            SetStatus("Press Discard again within 2 seconds to remove all pins.");
            return;
        }

        _discardClickTimer.Stop();
        _discardArmed = false;
        int removed = _pins.Count;
        _pins.Clear();
        _placedPins.Clear();
        _redoPins.Clear();
        _preview.RefreshPinSpacingWarnings();
        _preview.SelectedPin = null;
        UpdateActiveZoneButton();
        bool saved = PersistPinsOrReport();
        _preview.Invalidate();
        UpdateCropPreview(null);
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
                string filename = $"{timestamp}_{SafeFilenamePart(zone)}_{index + 1}.png";
                string outputPath = Path.Combine(_imageDirectory, filename);
                string temporaryPath = outputPath + ".tmp";

                using (var crop = CreateCropImage(frame, pin))
                    crop.Save(temporaryPath, ImageFormat.Png);
                temporaryFiles.Add(temporaryPath);
                File.Move(temporaryPath, outputPath);
                temporaryFiles.Remove(temporaryPath);
                pendingFiles.Add(outputPath);

                int screenX = _currentGallery.ScreenLeft + pin.X;
                int screenY = _currentGallery.ScreenTop + pin.Y;
                rows.Add(CsvRow(filename, zone, CropSize, CropSize, screenX, screenY));
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

        bool countsRefreshed = RecountLabels();
        foreach (ScreenPin pin in pendingPins)
            pin.Exported = true;
        SaveGalleryState();
        string savedMessage = _currentFrameIsDataCrop
            ? $"Saved {pendingPins.Length} labelled crop(s) to dataset/data. Exported crop pin progress was saved."
            : $"Saved {pendingPins.Length} labelled crop(s) to dataset/data. Capture and pin progress remain in dataset/todo.";
        SetStatus(countsRefreshed
            ? savedMessage
            : $"Saved {pendingPins.Length} labelled crop(s), but counts could not be refreshed. See {Log.FilePath}.",
            !countsRefreshed);
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
            SetStatus($"Pin is active but could not be saved. See {Log.FilePath}", true);
            return false;
        }
    }

    private static Bitmap CreateCropImage(Bitmap frame, ScreenPin pin)
    {
        const int halfSize = CropSize / 2;
        int left = pin.X - halfSize;
        int top = pin.Y - halfSize;
        var crop = new Bitmap(CropSize, CropSize, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(crop);
            graphics.Clear(Color.Black);
            Rectangle source = Rectangle.Intersect(
                new Rectangle(left, top, CropSize, CropSize),
                new Rectangle(Point.Empty, frame.Size));
            if (!source.IsEmpty)
            {
                var destination = new Rectangle(source.X - left, source.Y - top, source.Width, source.Height);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(frame, destination, source, GraphicsUnit.Pixel);
            }
            return crop;
        }
        catch
        {
            crop.Dispose();
            throw;
        }
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
        int target = GetTargetCount(zone);
        if (_countLabels.TryGetValue(zone, out ZoneCountRow? row))
            row.SetCount(count, target);
        if (_zoneButtons.TryGetValue(zone, out RoundedButton? button))
        {
            button.SubLabel = $"{count} / {target}";
            button.SubLabelColor = count < target ? Theme.Warn : Theme.Muted;
        }
    }

    private void UpdateCountRowWidths()
    {
        int contentHeight = _countList.Controls.Cast<Control>()
            .Sum(control => control.Height + control.Margin.Vertical);
        int scrollbarWidth = contentHeight > _countList.ClientSize.Height
            ? SystemInformation.VerticalScrollBarWidth
            : 0;
        int rowWidth = Math.Max(0, _countList.ClientSize.Width - scrollbarWidth);
        foreach (ZoneCountRow row in _countLabels.Values)
            row.Width = rowWidth;
    }

    private bool RecountLabels()
    {
        var recount = _config.Zones.ToDictionary(zone => zone.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (DatasetCaptureEntry entry in _manifestStore.Manifest.Captures)
        {
            if (entry.Status == "skipped") continue;
            foreach (DatasetManifestPin pin in entry.Pins)
            {
                if (!string.IsNullOrWhiteSpace(pin.Zone) && recount.ContainsKey(pin.Zone))
                    recount[pin.Zone]++;
            }
        }

        _counts.Clear();
        foreach (ZoneConfig zone in _config.Zones)
            SetCount(zone.Name, recount[zone.Name]);
        return true;
    }

    private int GetTargetCount(string zone)
    {
        foreach ((string name, int count) in _config.TargetCounts)
        {
            if (string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
                return count;
        }
        return 0;
    }

    private void UpdateCropPreview(ScreenPin? pin)
    {
        ScreenPin? target = pin ?? _preview.SelectedPin ?? _pins.LastOrDefault();
        _cropPreview.ShowCrop(_preview.Frame, target);
        if (_cropPreviewSurface is not null)
            _cropPreviewSurface.Visible = _preview.Frame is not null && target is not null;
    }

    private void PurgeDoneOnStartup()
    {
        try
        {
            int removed = _manifestStore.PurgeDone(_config.DoneRetentionDays);
            Log.Info($"done purge removed {removed} file(s).");
        }
        catch (Exception ex)
        {
            Log.Error("automatic done purge failed", ex);
        }
    }

    private string PurgeDoneFromSettings()
    {
        try
        {
            int removed = _manifestStore.PurgeDone(_config.DoneRetentionDays);
            Log.Info($"manual done purge removed {removed} file(s).");
            SetStatus($"Purged {removed} file(s) from dataset/done.");
            return $"Removed {removed} file(s).";
        }
        catch (Exception ex)
        {
            Log.Error("manual done purge failed", ex);
            SetStatus($"Could not purge dataset/done. See {Log.FilePath}.", true);
            return "Purge failed; see capture.log.";
        }
    }

    private static void DisposeChildren(Control parent)
    {
        foreach (Control child in parent.Controls.Cast<Control>().ToArray())
        {
            parent.Controls.Remove(child);
            child.Dispose();
        }
    }

    private void RefreshCounts()
    {
        if (RecountLabels())
            SetStatus("Counts refreshed from manifest.json.");
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

    private bool IsDataCrop(string imagePath) =>
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(imagePath) ?? string.Empty),
            Path.GetFullPath(_imageDirectory),
            StringComparison.OrdinalIgnoreCase);

    private void UpdateActiveZoneButton()
    {
        string? activeZone = _pins.LastOrDefault()?.Zone;
        foreach (var (name, button) in _zoneButtons)
            button.Selected = activeZone is not null
                && string.Equals(name, activeZone, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnmodifiedF11(string value)
    {
        HotkeyBinding binding = AppConfig.ParseHotkey(value);
        return binding.Key == Keys.F11 && binding.Modifiers == HotkeyModifiers.None;
    }

    private static bool IsUnmodifiedEscape(string value)
    {
        HotkeyBinding binding = AppConfig.ParseHotkey(value);
        return binding.Key == Keys.Escape && binding.Modifiers == HotkeyModifiers.None;
    }

    private static string FormatConfiguredKey(string value)
    {
        HotkeyBinding hotkey = AppConfig.ParseHotkey(value);
        return hotkey.Key == Keys.None ? "—" : AppConfig.FormatHotkey(hotkey);
    }

    private void SetStatus(string message, bool error = false)
    {
        _statusMessage = message;
        _statusLabel.Text = _precisionStatusActive ? $"PRECISE · {message}" : message;
        _statusLabel.ForeColor = error ? Theme.Warn : Theme.Muted;
        _statusIcon.Text = error ? "!" : "●";
        _statusIcon.ForeColor = error ? Theme.Warn : Theme.Success;
    }

    private void UpdatePrecisionStatus(bool active)
    {
        _precisionStatusActive = active;
        _statusLabel.Text = active ? $"PRECISE · {_statusMessage}" : _statusMessage;
    }

    private void AttachHotkeyFocusEvents(Control parent)
    {
        parent.GotFocus += (_, _) => UpdateHotkeyIndicator();
        parent.LostFocus += (_, _) => UpdateHotkeyIndicator();
        foreach (Control child in parent.Controls)
            AttachHotkeyFocusEvents(child);
    }

    private bool IsTextInputFocused()
    {
        Control focusRoot = Form.ActiveForm ?? this;
        Control? focused = FindFocusedControl(focusRoot);
        return focused is TextBoxBase or ComboBox or UpDownBase;
    }

    private static Control? FindFocusedControl(Control parent)
    {
        if (parent.Focused) return parent;
        foreach (Control child in parent.Controls)
        {
            if (child.ContainsFocus)
                return FindFocusedControl(child);
        }
        return null;
    }

    private void UpdateHotkeyIndicator()
    {
        if (_hotkeyIndicator is null || _preview is null) return;
        bool textInputFocused = IsTextInputFocused();
        bool hasGlobalBinding = _registeredHotkeys.Count > 0;
        bool hasCanvasBinding = AppConfig.ParseHotkey(_config.CaptureKey).Key != Keys.None
                && !_registeredHotkeys.Contains(CaptureHotkeyId)
            || AppConfig.ParseHotkey(_config.DiscardKey).Key != Keys.None
                && !_registeredHotkeys.Contains(DiscardHotkeyId)
            || _config.Zones.Select((zone, index) => (zone, index))
                .Any(item => AppConfig.ParseHotkey(item.zone.Key).Key != Keys.None
                    && !_registeredHotkeys.Contains(ZoneHotkeyBase + item.index));
        bool live = !textInputFocused
            && (hasGlobalBinding || _preview.Focused && hasCanvasBinding);
        _hotkeyIndicator.ForeColor = live ? Theme.Accent : Theme.Muted;
        _hotkeyIndicator.AccessibleName = live ? "Hotkeys live" : "Hotkeys suppressed";
        _windowToolTip.SetToolTip(_hotkeyIndicator, live
            ? "Hotkeys live: Alt bindings are global; other bindings work on the canvas."
            : "Hotkeys are suppressed while text input has focus or the canvas is unfocused.");
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
            SaveWindowBounds();
            _boundsSaveTimer.Stop();
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
        Region? previous = Region;
        Region = null;
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _boundsSaveTimer.Stop();
        SaveWindowBounds();
        foreach (int id in _registeredHotkeys)
            UnregisterHotKey(Handle, id);
        _registeredHotkeys.Clear();
        _pinFlashTimer.Stop();
        _discardClickTimer.Stop();
        _discardArmed = false;
        if (_timerResolutionAcquired)
            timeEndPeriod(1);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pinFlashTimer.Dispose();
            _discardClickTimer.Dispose();
            _boundsSaveTimer.Dispose();
            _windowToolTip.Dispose();
            _preview.Frame?.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class CropPreview : Control
    {
        private Bitmap? _crop;

        public CropPreview()
        {
            Size = new Size(194, 194);
            BackColor = Color.Black;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        public void ShowCrop(Bitmap? frame, ScreenPin? pin)
        {
            Bitmap? next = null;
            if (frame is not null && pin is not null)
            {
                try
                {
                    next = CreateCropImage(frame, pin);
                }
                catch (Exception ex)
                {
                    Log.Error("updating crop preview failed", ex);
                }
            }

            Bitmap? previous = _crop;
            _crop = next;
            previous?.Dispose();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            try
            {
                if (_crop is not null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    e.Graphics.DrawImage(_crop, new Rectangle(0, 0, CropSize * 2, CropSize * 2),
                        new Rectangle(0, 0, CropSize, CropSize), GraphicsUnit.Pixel);
                }
                using var border = new Pen(Theme.Border, 1);
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
            catch (Exception ex)
            {
                Log.Error("painting crop preview failed", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _crop?.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CropPreviewSurface : Panel
    {
        public CropPreviewSurface()
        {
            SetStyle(ControlStyles.UserPaint
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
            var shadowBounds = new Rectangle(4, 4, Width - 5, Height - 5);
            using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                e.Graphics.FillRectangle(shadow, shadowBounds);
            var bounds = new Rectangle(0, 0, Width - 5, Height - 5);
            using (var fill = new SolidBrush(Theme.Panel))
                e.Graphics.FillRectangle(fill, bounds);
            using var border = new Pen(Theme.Border, 1);
            e.Graphics.DrawRectangle(border, bounds);
        }
    }

    private sealed class HorizontalFlowLayoutPanel : FlowLayoutPanel
    {
        private const int WsVScroll = 0x00200000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.Style &= ~WsVScroll;
                return parameters;
            }
        }
    }

    private sealed class ShortcutHint : Control
    {
        private static readonly Font KeyFont = new("Consolas", 7.5f);
        private static readonly Font LabelFont = new("Segoe UI", 7.5f);
        private readonly string _key;
        private readonly string _label;
        private readonly int _keyWidth;

        public ShortcutHint(string key, string label)
        {
            _key = key;
            _label = label;
            _keyWidth = Math.Max(18, TextRenderer.MeasureText(key, KeyFont).Width + 12);
            Width = _keyWidth + 6 + TextRenderer.MeasureText(label, LabelFont).Width + 1;
            Height = 23;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.UserPaint
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var chipBounds = new Rectangle(0, 2, _keyWidth, 18);
            using (var chipPath = RoundedButton.CreateRoundedRectanglePath(chipBounds, 6))
            using (var chipFill = new SolidBrush(Theme.Border))
                e.Graphics.FillPath(chipFill, chipPath);
            TextRenderer.DrawText(e.Graphics, _key, KeyFont, chipBounds, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, _label, LabelFont,
                new Rectangle(chipBounds.Right + 6, 0, Width - chipBounds.Right - 6, Height),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private sealed class ZoneCountRow : Control
    {
        private readonly string _name;
        private readonly Font _labelFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
        private readonly Font _countFont = new("Consolas", 8.25f);
        private int _count;
        private int _target;

        public ZoneCountRow(string name, int count, int target)
        {
            _name = name;
            _count = count;
            _target = target;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            SetStyle(ControlStyles.UserPaint
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
        }

        public void SetCount(int count, int target)
        {
            if (_count == count && _target == target) return;
            _count = count;
            _target = target;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var labelBounds = new Rectangle(0, 0, Math.Max(0, Width - 100), 24);
            var countBounds = new Rectangle(Math.Max(0, Width - 100), 0, Math.Min(100, Width), 24);
            TextRenderer.DrawText(e.Graphics, _name, _labelFont, labelBounds, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Color countColor = _count < _target && _target > 0 ? Theme.Warn : Theme.Text;
            TextRenderer.DrawText(e.Graphics, $"{_count} / {_target}", _countFont, countBounds, countColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var track = new Rectangle(8, Height - 9, Math.Max(0, Width - 16), 6);
            using (var trackBrush = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                e.Graphics.FillRectangle(trackBrush, track);
            float ratio = _target > 0 ? Math.Clamp(_count / (float)_target, 0f, 1f) : 0f;
            int fillWidth = (int)Math.Round(track.Width * ratio);
            if (fillWidth > 0)
            {
                using var fill = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(fill, new Rectangle(track.X, track.Y, fillWidth, track.Height));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont.Dispose();
                _countFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ToggleRow : Control
    {
        private bool _hovered;
        private bool _checked;
        private bool _showLabel = true;

        public ToggleRow(string text)
        {
            Text = text;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.CheckButton;
            Width = ExpandedSidebarWidth - 34;
            Height = 32;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
        }

        public event EventHandler? CheckedChanged;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool ShowLabel
        {
            get => _showLabel;
            set
            {
                if (_showLabel == value) return;
                _showLabel = value;
                Invalidate();
            }
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
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                Checked = !Checked;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var backgroundPath = RoundedButton.CreateRectanglePath(bounds);
            using (var background = new SolidBrush(_hovered ? Theme.PanelHi : Theme.Panel))
                e.Graphics.FillPath(background, backgroundPath);
            using (var border = new Pen(Focused ? Theme.Accent : Theme.Border, 1))
                e.Graphics.DrawPath(border, backgroundPath);

            var switchBounds = new Rectangle(Math.Max(2, Width - 42), (Height - 18) / 2, 34, 18);
            using (var switchPath = RoundedButton.CreateRoundedRectanglePath(switchBounds, 9))
            using (var switchFill = new SolidBrush(Checked ? Theme.Accent : Theme.Border))
                e.Graphics.FillPath(switchFill, switchPath);
            var thumb = new Rectangle(switchBounds.X + (Checked ? 18 : 3),
                switchBounds.Y + 3, 12, 12);
            using (var thumbFill = new SolidBrush(Theme.Text))
                e.Graphics.FillEllipse(thumbFill, thumb);
            if (ShowLabel)
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(8, 0, switchBounds.X - 14, Height),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(3, 3, Width - 7, Height - 7),
                    Theme.Accent, Theme.Panel);
        }
    }

    private sealed class ScreenPreview : Control
    {
        public readonly record struct ViewState(
            float Zoom,
            PointF PanImageOffset,
            Point AimPoint,
            bool HasAimPoint,
            Point MousePosition,
            bool HasMousePosition,
            PointF AimMarkerPosition,
            bool PrecisionActive,
            ScreenPin? HoveredPin);

        private ScreenPin? _hoveredPin;
        private readonly HashSet<ScreenPin> _pinsTooClose = new();
        private readonly Dictionary<ScreenPin, Rectangle> _pinChipBounds = new();
        private RectangleF _imageRectangle;
        private Point _panStartMouse;
        private Point _mousePosition;
        private Point _aimPoint;
        private PointF _aimMarkerPosition;
        private PointF _panImageOffset;
        private PointF _panStartImageOffset;
        private MouseButtons _panButton;
        private ScreenPin? _draggingPin;
        private Point _pinDragStartMouse;
        private int _pinDragStartX;
        private int _pinDragStartY;
        private bool _pinDragMoved;
        private bool _zoneChipPressed;
        private float _zoom = 1f;
        private bool _isPanning;
        private bool _panMoved;
        private bool _suppressPanClick;
        private bool _hasAimPoint;
        private bool _hasMousePosition;
        private bool _precisionActive;
        private Keys _precisionKey = Keys.LShiftKey;
        private float _precisionFactor = 0.25f;
        private readonly Font _emptyLineFont = new("Segoe UI", 9f);
        private readonly Font _dimensionFont = new("Consolas", 8.25f);
        private readonly Font _zoomFont = new("Consolas", 8.25f);
        private readonly Font _pinLabelFont = new("Consolas", 8.25f);

        public Bitmap? Frame { get; set; }
        public string CaptureShortcut { get; set; } = "Q";
        public Keys PrecisionKey
        {
            get => _precisionKey;
            set => _precisionKey = value;
        }
        public float PrecisionFactor
        {
            get => _precisionFactor;
            set => _precisionFactor = value;
        }
        public List<ScreenPin> Pins { get; set; } = null!;
        private int _minimumPinSpacing;
        public int MinimumPinSpacing
        {
            get => _minimumPinSpacing;
            set
            {
                if (_minimumPinSpacing == value) return;
                _minimumPinSpacing = value;
                RefreshPinSpacingWarnings();
            }
        }
        public ScreenPin? SelectedPin { get; set; }
        public ScreenPin? HoveredPin => _hoveredPin;
        public bool IsPanning => _isPanning;
        public event Action<Point>? PinCreated;
        public event Action<ScreenPin>? PinDeleteRequested;
        public event Action<ScreenPin>? PinMoved;
        public event Action<ScreenPin, Point>? PinZonePickerRequested;
        public event Action<ScreenPin?>? PreviewPinChanged;
        public event Action<bool>? PrecisionStateChanged;

        public void ResetView()
        {
            _zoom = 1f;
            _aimPoint = Frame is null
                ? Point.Empty
                : new Point(Frame.Width / 2, Frame.Height / 2);
            _panImageOffset = PointF.Empty;
            _hasAimPoint = Frame is not null;
            _hasMousePosition = false;
            _aimMarkerPosition = Point.Empty;
            if (_precisionActive)
                PrecisionStateChanged?.Invoke(false);
            _precisionActive = false;
            _hoveredPin = null;
            _isPanning = false;
            _draggingPin = null;
        }

        public ViewState CaptureViewState() => new(
            _zoom,
            _panImageOffset,
            _aimPoint,
            _hasAimPoint,
            _mousePosition,
            _hasMousePosition,
            _aimMarkerPosition,
            _precisionActive,
            _hoveredPin);

        public void RestoreViewState(ViewState state)
        {
            _zoom = state.Zoom;
            _panImageOffset = state.PanImageOffset;
            _aimPoint = state.AimPoint;
            _hasAimPoint = state.HasAimPoint;
            _mousePosition = state.MousePosition;
            _hasMousePosition = state.HasMousePosition;
            _aimMarkerPosition = state.AimMarkerPosition;
            _precisionActive = state.PrecisionActive;
            _hoveredPin = state.HoveredPin is not null && Pins.Contains(state.HoveredPin)
                ? state.HoveredPin
                : null;
            _isPanning = false;
            _panMoved = false;
            _suppressPanClick = false;
            PrecisionStateChanged?.Invoke(_precisionActive);
            Invalidate();
        }

        public void RefreshPrecisionState()
        {
            bool active = ClientRectangle.Contains(_mousePosition) && IsPrecisionKeyDown(_precisionKey);
            if (_precisionActive == active) return;
            _precisionActive = active;
            _aimMarkerPosition = _mousePosition;
            PrecisionStateChanged?.Invoke(active);
            Invalidate();
        }

        private static bool IsPrecisionKeyDown(Keys key) =>
            key != Keys.None && (GetAsyncKeyState((int)key) & 0x8000) != 0;

        public void RefreshPinSpacingWarnings()
        {
            _pinsTooClose.Clear();
            if (MinimumPinSpacing <= 0 || Pins is null)
            {
                Invalidate();
                return;
            }

            var cells = new Dictionary<(int X, int Y), List<ScreenPin>>();
            long minimumDistanceSquared = (long)MinimumPinSpacing * MinimumPinSpacing;
            foreach (ScreenPin pin in Pins)
            {
                int cellX = pin.X / MinimumPinSpacing;
                int cellY = pin.Y / MinimumPinSpacing;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (!cells.TryGetValue((cellX + offsetX, cellY + offsetY), out List<ScreenPin>? candidates))
                        continue;
                    foreach (ScreenPin other in candidates)
                    {
                        long dx = (long)pin.X - other.X;
                        long dy = (long)pin.Y - other.Y;
                        if (dx * dx + dy * dy > minimumDistanceSquared)
                            continue;
                        _pinsTooClose.Add(pin);
                        _pinsTooClose.Add(other);
                    }
                }

                if (!cells.TryGetValue((cellX, cellY), out List<ScreenPin>? cell))
                    cells[(cellX, cellY)] = cell = new List<ScreenPin>();
                cell.Add(pin);
            }
            Invalidate();
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
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(Frame, _imageRectangle);
            using (var border = new Pen(Theme.Border, 1))
                graphics.DrawRectangle(border, _imageRectangle.X, _imageRectangle.Y, _imageRectangle.Width, _imageRectangle.Height);

            DrawPins(graphics, scale, _imageRectangle, ClientRectangle, cacheChipBounds: true);

            var dimensionText = $"{Frame.Width} × {Frame.Height}";
            TextRenderer.DrawText(graphics, dimensionText, _dimensionFont,
                new Point(12, 10), Color.FromArgb(230, 255, 255, 255));
            DrawMagnifier(graphics, scale);
            if (_zoom <= 1f)
                DrawAimMarker(graphics);
            DrawZoomBadge(graphics);
        }

        private void DrawEmptyState(Graphics graphics)
        {
            int cardWidth = Math.Min(380, ClientSize.Width - 48);
            const int cardHeight = 48;
            if (cardWidth < 220 || ClientSize.Height < cardHeight) return;
            var card = new Rectangle((ClientSize.Width - cardWidth) / 2,
                (ClientSize.Height - cardHeight) / 2, cardWidth, cardHeight);
            using var cardPath = RoundedButton.CreateRectanglePath(card);
            using var fill = new SolidBrush(Theme.Panel);
            using var border = new Pen(Theme.BorderSoft);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(fill, cardPath);
            graphics.DrawPath(border, cardPath);
            TextRenderer.DrawText(graphics, "No capture open. Capture an image to begin.",
                _emptyLineFont, new Rectangle(card.X + 16, card.Y, card.Width - 32, card.Height),
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawZoomBadge(Graphics graphics)
        {
            string text = $"{_zoom * 100:0}%";
            Size textSize = TextRenderer.MeasureText(text, _zoomFont);
            var bounds = new Rectangle(Width - textSize.Width - 38, 10, textSize.Width + 24, 24);
            using var path = RoundedButton.CreateRectanglePath(bounds);
            using var background = new SolidBrush(Color.FromArgb(225, 15, 20, 32));
            using var border = new Pen(Theme.Border);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);
            TextRenderer.DrawText(graphics, text, _zoomFont, bounds, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawAimMarker(Graphics graphics)
        {
            if (!_hasMousePosition || !_imageRectangle.Contains(_aimMarkerPosition))
                return;
            PointF marker = _aimMarkerPosition;

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = new Pen(Color.FromArgb(230, 0, 0, 0), 3f);
            using var line = new Pen(Color.White, 1.4f);
            graphics.DrawEllipse(outline, marker.X - 9, marker.Y - 9, 18, 18);
            graphics.DrawLine(outline, marker.X - 13, marker.Y, marker.X + 13, marker.Y);
            graphics.DrawLine(outline, marker.X, marker.Y - 13, marker.X, marker.Y + 13);
            graphics.DrawEllipse(line, marker.X - 9, marker.Y - 9, 18, 18);
            graphics.DrawLine(line, marker.X - 13, marker.Y, marker.X + 13, marker.Y);
            graphics.DrawLine(line, marker.X, marker.Y - 13, marker.X, marker.Y + 13);
            using var center = new SolidBrush(Theme.Bg);
            graphics.FillEllipse(center, marker.X - 2, marker.Y - 2, 4, 4);
        }

        private void DrawMagnifier(Graphics graphics, float fitScale)
        {
            if (_zoom <= 1f || Frame is null || !_hasMousePosition || !_imageRectangle.Contains(_mousePosition))
                return;

            const int diameter = 224;
            var lensBounds = new Rectangle(
                _mousePosition.X - diameter / 2,
                _mousePosition.Y - diameter / 2,
                diameter,
                diameter);
            float zoomScale = fitScale * _zoom;
            var zoomedImage = new RectangleF(
                _mousePosition.X - (_aimPoint.X + 0.5f) * zoomScale,
                _mousePosition.Y - (_aimPoint.Y + 0.5f) * zoomScale,
                Frame.Width * zoomScale,
                Frame.Height * zoomScale);
            using var lensPath = new GraphicsPath();
            lensPath.AddEllipse(lensBounds);
            GraphicsState state = graphics.Save();
            graphics.SetClip(lensPath, CombineMode.Intersect);
            graphics.FillEllipse(Brushes.Black, lensBounds);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(Frame, zoomedImage);
            DrawPins(graphics, zoomScale, zoomedImage, lensBounds, cacheChipBounds: false);
            graphics.Restore(state);

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shadow = new Pen(Color.FromArgb(190, 0, 0, 0), 5f))
                graphics.DrawEllipse(shadow, lensBounds);
            using (var border = new Pen(Theme.Text, 2f))
                graphics.DrawEllipse(border, lensBounds);
            using (var reticle = new Pen(Color.FromArgb(220, 255, 255, 255), 1f))
            {
                graphics.DrawLine(reticle, _mousePosition.X - 9, _mousePosition.Y,
                    _mousePosition.X + 9, _mousePosition.Y);
                graphics.DrawLine(reticle, _mousePosition.X, _mousePosition.Y - 9,
                    _mousePosition.X, _mousePosition.Y + 9);
            }
            using var center = new SolidBrush(Theme.Accent);
            graphics.FillEllipse(center, _mousePosition.X - 2, _mousePosition.Y - 2, 4, 4);
        }

        private void DrawPins(
            Graphics graphics,
            float scale,
            RectangleF imageRectangle,
            Rectangle clipBounds,
            bool cacheChipBounds)
        {
            var usedChipBounds = new List<Rectangle>();
            var chips = new Dictionary<ScreenPin, Rectangle>();
            foreach (ScreenPin pin in Pins
                         .OrderBy(pin => imageRectangle.Top + (pin.Y + 0.5f) * scale)
                         .ThenBy(pin => pin.X))
            {
                float x = imageRectangle.Left + (pin.X + 0.5f) * scale;
                float y = imageRectangle.Top + (pin.Y + 0.5f) * scale;
                Rectangle chip = GetPinLabelBounds(pin, x, y, scale, clipBounds);
                int originalY = chip.Y;
                while (usedChipBounds.Any(existing => existing.IntersectsWith(chip))
                    && chip.Bottom + 18 <= clipBounds.Bottom)
                    chip.Y += 18;

                if (usedChipBounds.Any(existing => existing.IntersectsWith(chip)))
                {
                    chip.Y = originalY;
                    while (usedChipBounds.Any(existing => existing.IntersectsWith(chip))
                        && chip.Y - 18 >= clipBounds.Top)
                        chip.Y -= 18;
                }

                usedChipBounds.Add(chip);
                chips[pin] = chip;
            }

            if (cacheChipBounds)
            {
                _pinChipBounds.Clear();
                foreach ((ScreenPin pin, Rectangle bounds) in chips)
                    _pinChipBounds[pin] = bounds;
            }

            foreach (ScreenPin pin in Pins)
            {
                float x = imageRectangle.Left + (pin.X + 0.5f) * scale;
                float y = imageRectangle.Top + (pin.Y + 0.5f) * scale;
                DrawPin(graphics, pin, scale, x, y, chips[pin]);
            }
        }

        private void DrawPin(Graphics graphics, ScreenPin pin, float scale, float x, float y, Rectangle label)
        {
            float radius = Math.Max(5f, Math.Min(10f, 8f / Math.Max(scale, 0.15f)));
            bool hovered = ReferenceEquals(pin, _hoveredPin);
            Color color = pin.Confirming ? Theme.Accent : Theme.Text;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var glow = new SolidBrush(Color.FromArgb(hovered ? 100 : 55, color)))
                graphics.FillEllipse(glow, x - radius * 2.2f, y - radius * 2.2f, radius * 4.4f, radius * 4.4f);
            if (_pinsTooClose.Contains(pin))
            {
                using var warningRing = new Pen(Theme.Warn, 1.5f) { DashStyle = DashStyle.Dash };
                float ringRadius = radius + 8;
                graphics.DrawEllipse(warningRing, x - ringRadius, y - ringRadius,
                    ringRadius * 2, ringRadius * 2);
            }
            using (var fill = new SolidBrush(color))
                graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
            using (var outline = new Pen(Color.FromArgb(230, 255, 255, 255), 1.2f))
                graphics.DrawEllipse(outline, x - radius, y - radius, radius * 2, radius * 2);
            if (hovered)
            {
                using var hoverRing = new Pen(Theme.Accent, 2f);
                graphics.DrawEllipse(hoverRing, x - radius - 4, y - radius - 4,
                    (radius + 4) * 2, (radius + 4) * 2);
            }

            using var labelBackground = new SolidBrush(Color.FromArgb(230, 18, 21, 28));
            using var labelBorder = new Pen(hovered ? Theme.Accent : Theme.Border, 1f);
            graphics.FillRectangle(labelBackground, label);
            graphics.DrawRectangle(labelBorder, label);
            TextRenderer.DrawText(graphics, pin.Zone ?? "unassigned", _pinLabelFont,
                label, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private Rectangle GetPinLabelBounds(
            ScreenPin pin,
            float x,
            float y,
            float scale,
            Rectangle clipBounds)
        {
            string label = pin.Zone ?? "unassigned";
            int width = Math.Clamp(TextRenderer.MeasureText(label, _pinLabelFont).Width + 12,
                Math.Min(48, clipBounds.Width), Math.Min(130, clipBounds.Width));
            float radius = Math.Max(5f, Math.Min(10f, 8f / Math.Max(scale, 0.15f)));
            int left = (int)Math.Round(x + 8f * scale);
            int top = (int)Math.Round(y - radius - 8f - 18f);
            left = Math.Clamp(left, clipBounds.Left, clipBounds.Right - width);
            top = Math.Clamp(top, clipBounds.Top, clipBounds.Bottom - 18);
            return new Rectangle(left, top, width, 18);
        }

        private bool IsOnZoneChip(ScreenPin pin, Point clientPoint)
        {
            if (Frame is null || _imageRectangle.Width <= 0) return false;
            return _pinChipBounds.TryGetValue(pin, out Rectangle bounds)
                && bounds.Contains(clientPoint);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Frame is null) return;
            if ((e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) && _zoom > 1f)
            {
                _isPanning = true;
                _panButton = e.Button;
                _panMoved = false;
                _suppressPanClick = false;
                _panStartMouse = e.Location;
                _panStartImageOffset = _panImageOffset;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Left && _imageRectangle.Contains(e.Location))
            {
                ScreenPin? pin = FindPin(e.Location);
                if (pin is not null)
                {
                    _draggingPin = pin;
                    _pinDragStartMouse = e.Location;
                    _pinDragStartX = pin.X;
                    _pinDragStartY = pin.Y;
                    _pinDragMoved = false;
                    _zoneChipPressed = IsOnZoneChip(pin, e.Location);
                    SelectedPin = pin;
                    Capture = true;
                    _hoveredPin = pin;
                    PreviewPinChanged?.Invoke(pin);
                    Invalidate();
                    return;
                }

                if (TryMapPoint(e.Location, out Point point))
                {
                    PinCreated?.Invoke(point);
                    _hoveredPin = FindPin(e.Location);
                    PreviewPinChanged?.Invoke(_hoveredPin);
                }
                return;
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _mousePosition = PointToClient(Cursor.Position);
            _aimMarkerPosition = _mousePosition;
            _hasMousePosition = true;
            if (_zoom > 1f)
                UpdateAimPointFromPointer(_mousePosition);
            RefreshPrecisionState();
            Focus();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point previousMousePosition = _mousePosition;
            bool hadPreviousMousePosition = _hasMousePosition;
            _mousePosition = e.Location;
            _hasMousePosition = true;
            bool precisionActive = ClientRectangle.Contains(e.Location) && IsPrecisionKeyDown(_precisionKey);
            if (precisionActive != _precisionActive)
            {
                _precisionActive = precisionActive;
                _aimMarkerPosition = e.Location;
                PrecisionStateChanged?.Invoke(precisionActive);
            }
            if (precisionActive)
            {
                if (hadPreviousMousePosition)
                {
                    _aimMarkerPosition.X += (e.X - previousMousePosition.X) * _precisionFactor;
                    _aimMarkerPosition.Y += (e.Y - previousMousePosition.Y) * _precisionFactor;
                }
            }
            else
            {
                _aimMarkerPosition = e.Location;
            }
            if (_draggingPin is not null)
            {
                if (Math.Abs(e.X - _pinDragStartMouse.X) > 2 || Math.Abs(e.Y - _pinDragStartMouse.Y) > 2)
                    _pinDragMoved = true;
                if (_pinDragMoved)
                {
                    UpdateAimPointFromPointer(e.Location);
                    _draggingPin.X = _aimPoint.X;
                    _draggingPin.Y = _aimPoint.Y;
                    RefreshPinSpacingWarnings();
                    PreviewPinChanged?.Invoke(_draggingPin);
                }
                Invalidate();
                return;
            }
            if (_isPanning)
            {
                if (Math.Abs(e.X - _panStartMouse.X) > 2 || Math.Abs(e.Y - _panStartMouse.Y) > 2)
                    _panMoved = true;
                float scale = GetFitScale() * _zoom;
                _panImageOffset = new PointF(
                    _panStartImageOffset.X - (e.X - _panStartMouse.X) / scale,
                    _panStartImageOffset.Y - (e.Y - _panStartMouse.Y) / scale);
                UpdateAimPointFromPointer(e.Location);
                ScreenPin? hoveredDuringPan = FindPin(e.Location);
                if (!ReferenceEquals(_hoveredPin, hoveredDuringPan))
                {
                    _hoveredPin = hoveredDuringPan;
                    PreviewPinChanged?.Invoke(hoveredDuringPan);
                }
                Invalidate();
                return;
            }

            if (Frame is not null && (_zoom > 1f || precisionActive)
                && _imageRectangle.Contains(_aimMarkerPosition))
                UpdateAimPointFromPointer(Point.Round(_aimMarkerPosition));
            ScreenPin? hovered = FindPin(e.Location);
            bool hoveredPinChanged = !ReferenceEquals(_hoveredPin, hovered);
            if (hoveredPinChanged)
            {
                _hoveredPin = hovered;
                PreviewPinChanged?.Invoke(hovered);
            }
            if (hoveredPinChanged || _zoom > 1f)
            {
                Invalidate();
            }
            else
            {
                if (hadPreviousMousePosition && _imageRectangle.Contains(previousMousePosition))
                    InvalidateAimMarker(previousMousePosition);
                if (_imageRectangle.Contains(e.Location))
                    InvalidateAimMarker(e.Location);
            }
            Cursor = Frame is not null && _imageRectangle.Contains(e.Location)
                ? hovered is not null ? Cursors.SizeAll : Cursors.Cross
                : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_precisionActive)
            {
                _precisionActive = false;
                PrecisionStateChanged?.Invoke(false);
            }
            Point previousMousePosition = _mousePosition;
            bool hadPreviousMousePosition = _hasMousePosition;
            bool hadHoveredPin = _hoveredPin is not null;
            if (hadHoveredPin)
            {
                _hoveredPin = null;
                PreviewPinChanged?.Invoke(null);
            }
            if (_isPanning) return;
            _hasMousePosition = false;
            if (_zoom > 1f || hadHoveredPin)
                Invalidate();
            else if (hadPreviousMousePosition && _imageRectangle.Contains(previousMousePosition))
                InvalidateAimMarker(previousMousePosition);
            Cursor = Cursors.Default;
        }

        public void ClearHoveredPin(ScreenPin pin)
        {
            if (!ReferenceEquals(_hoveredPin, pin)) return;
            _hoveredPin = null;
            PreviewPinChanged?.Invoke(null);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingPin is ScreenPin draggedPin && e.Button == MouseButtons.Left)
            {
                bool moved = _pinDragMoved;
                bool openZonePicker = _zoneChipPressed && !moved;
                _draggingPin = null;
                _pinDragMoved = false;
                _zoneChipPressed = false;
                Capture = false;
                if (moved)
                    PinMoved?.Invoke(draggedPin);
                else if (openZonePicker)
                    PinZonePickerRequested?.Invoke(draggedPin, e.Location);
                Invalidate();
                return;
            }
            bool endingPan = _isPanning && e.Button == _panButton;
            if (e.Button == MouseButtons.Right
                && !_suppressPanClick
                && (!endingPan || !_panMoved)
                && ClientRectangle.Contains(e.Location))
            {
                ScreenPin? pin = FindPin(e.Location);
                if (pin is not null) PinDeleteRequested?.Invoke(pin);
            }
            if (endingPan)
            {
                _isPanning = false;
                _panButton = MouseButtons.None;
                _suppressPanClick = false;
                Capture = false;
                Cursor = _imageRectangle.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
            }
            else if (e.Button == MouseButtons.Right)
                _suppressPanClick = false;
        }

        public bool CancelPan()
        {
            if (!_isPanning) return false;
            _isPanning = false;
            _suppressPanClick = true;
            _panButton = MouseButtons.None;
            if (Capture)
                Capture = false;
            Cursor = _imageRectangle.Contains(_mousePosition) ? Cursors.Cross : Cursors.Default;
            Invalidate();
            return true;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || !_isPanning) return;
            _isPanning = false;
            _suppressPanClick = true;
            _panButton = MouseButtons.None;
            Cursor = _imageRectangle.Contains(_mousePosition) ? Cursors.Cross : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Frame is null || !_imageRectangle.Contains(e.Location)) return;

            _aimMarkerPosition = e.Location;
            UpdateAimPointFromPointer(Point.Round(_aimMarkerPosition));
            _hasAimPoint = true;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), 1f, 16f);
            _mousePosition = e.Location;
            _hasMousePosition = true;
            _hoveredPin = FindPin(e.Location);
            PreviewPinChanged?.Invoke(_hoveredPin);
            Invalidate();
        }

        private bool TryMapPoint(Point clientPoint, out Point point)
        {
            point = Point.Empty;
            if (Frame is null || !_imageRectangle.Contains(clientPoint)) return false;
            if ((_zoom > 1f || _precisionActive) && _hasAimPoint)
            {
                point = _aimPoint;
                return true;
            }
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
            ScreenPin? chipPin = Pins.LastOrDefault(pin => IsOnZoneChip(pin, clientPoint));
            if (chipPin is not null) return chipPin;
            float scaleX = _imageRectangle.Width / Frame.Width;
            float scaleY = _imageRectangle.Height / Frame.Height;
            PointF marker = _zoom > 1f && _hasAimPoint
                ? new PointF(_imageRectangle.Left + (_aimPoint.X + 0.5f) * scaleX,
                    _imageRectangle.Top + (_aimPoint.Y + 0.5f) * scaleY)
                : clientPoint;
            ScreenPin? closest = null;
            float closestDistance = 18 * 18;
            foreach (ScreenPin pin in Pins)
            {
                float distance = DistanceSquared(Point.Round(marker),
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
            float scale = GetFitScale();
            float width = Frame.Width * scale;
            float height = Frame.Height * scale;
            return new RectangleF(
                (ClientSize.Width - width) / 2f,
                (ClientSize.Height - height) / 2f,
                width,
                height);
        }

        private void UpdateAimPointFromPointer(Point pointer)
        {
            if (Frame is null || _imageRectangle.Width <= 0 || _imageRectangle.Height <= 0) return;
            float scaleX = Frame.Width / _imageRectangle.Width;
            float scaleY = Frame.Height / _imageRectangle.Height;
            _aimPoint = new Point(
                Math.Clamp((int)Math.Round((pointer.X - _imageRectangle.Left) * scaleX + _panImageOffset.X), 0, Frame.Width - 1),
                Math.Clamp((int)Math.Round((pointer.Y - _imageRectangle.Top) * scaleY + _panImageOffset.Y), 0, Frame.Height - 1));
            _hasAimPoint = true;
        }

        private void InvalidateAimMarker(Point position)
        {
            var bounds = new Rectangle(position.X - 15, position.Y - 15, 30, 30);
            bounds.Intersect(ClientRectangle);
            if (!bounds.IsEmpty)
                Invalidate(bounds);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _imageRectangle = CalculateImageRectangle();
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
                _emptyLineFont.Dispose();
                _dimensionFont.Dispose();
                _zoomFont.Dispose();
                _pinLabelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

internal sealed class ScreenPin(int x, int y)
{
    public int X { get; set; } = x;
    public int Y { get; set; } = y;
    public string? Zone { get; set; }
    public bool Confirming { get; set; }
    public bool Exported { get; set; }
}

internal enum RoundedButtonIcon
{
    None,
    Folder,
    Refresh,
    Settings,
    Library,
    Sidebar,
    SidebarExpand,
}

internal sealed class RoundedButton : Button
{
    private static readonly Font SubLabelFont = new("Consolas", 8.25f);
    private bool _hovered;
    private bool _pressed;
    private bool _focused;
    private bool _accent;
    private bool _quiet;
    private bool _selected;
    private string? _keyChipText;
    private string? _subLabel;
    private Color _subLabelColor = Theme.Muted;
    private string? _trailingHint;
    private RoundedButtonIcon _icon;

    public bool Accent
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            Invalidate();
        }
    }

    public bool Quiet
    {
        get => _quiet;
        set
        {
            if (_quiet == value) return;
            _quiet = value;
            Invalidate();
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public string? KeyChipText
    {
        get => _keyChipText;
        set
        {
            if (_keyChipText == value) return;
            _keyChipText = value;
            Invalidate();
        }
    }

    public string? SubLabel
    {
        get => _subLabel;
        set
        {
            if (_subLabel == value) return;
            _subLabel = value;
            Invalidate();
        }
    }

    public Color SubLabelColor
    {
        get => _subLabelColor;
        set
        {
            if (_subLabelColor == value) return;
            _subLabelColor = value;
            Invalidate();
        }
    }

    public string? TrailingHint
    {
        get => _trailingHint;
        set
        {
            if (_trailingHint == value) return;
            _trailingHint = value;
            Invalidate();
        }
    }

    public RoundedButtonIcon Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            Invalidate();
        }
    }

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        Cursor = Cursors.Hand;
        TabStop = true;
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

    protected override void OnGotFocus(EventArgs e)
    {
        _focused = true;
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _focused = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
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
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        Color fill = !Enabled
            ? Theme.Panel
            : Accent
                ? _pressed ? ControlPaint.Dark(Theme.Accent, 0.12f)
                    : _hovered ? ControlPaint.Light(Theme.Accent, 0.12f)
                    : Theme.Accent
                : _pressed || _hovered ? Theme.PanelHi
                : Selected ? Theme.PanelHi
                : parentColor;
        bool drawBorder = !Quiet || Selected || _focused;
        Color edge = _focused || Accent || Selected ? Theme.Accent : Theme.Border;

        using (var path = CreateRectanglePath(bounds))
        {
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, path);
            if (drawBorder)
            {
                using var pen = new Pen(edge, 1);
                graphics.DrawPath(pen, path);
            }
        }

        Color textColor = !Enabled ? Theme.Muted : Accent ? Color.White : Quiet ? Theme.Muted : ForeColor;
        if (!string.IsNullOrEmpty(KeyChipText))
        {
            var chipBounds = new Rectangle(10, Math.Max(0, (Height - 18) / 2), 18, 18);
            using (var chipPath = CreateRoundedRectanglePath(chipBounds, 4))
            using (var chipFill = new SolidBrush(Theme.Border))
                graphics.FillPath(chipFill, chipPath);
            using var chipFont = new Font("Consolas", KeyChipText.Length > 3 ? 5.5f : 7f);
            TextRenderer.DrawText(graphics, KeyChipText, chipFont, chipBounds, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(SubLabel))
            {
                var nameBounds = new Rectangle(chipBounds.Right + 8, 2,
                    Math.Max(0, Width - chipBounds.Right - 18), 17);
                var subLabelBounds = new Rectangle(nameBounds.X, 19, nameBounds.Width, 14);
                TextRenderer.DrawText(graphics, Text, Font, nameBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(graphics, SubLabel, SubLabelFont, subLabelBounds,
                    Enabled ? SubLabelColor : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                var labelBounds = new Rectangle(chipBounds.Right + 8, 0,
                    Math.Max(0, Width - chipBounds.Right - 18), Height);
                TextRenderer.DrawText(graphics, Text, Font, labelBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }
        else if (!string.IsNullOrEmpty(TrailingHint))
        {
            var labelBounds = new Rectangle(12, 0, Math.Max(0, Width - 24), Height);
            using var hintFont = new Font("Segoe UI", 8.25f);
            int hintWidth = TextRenderer.MeasureText(TrailingHint, hintFont).Width;
            TextRenderer.DrawText(graphics, Text, Font, labelBounds, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, TrailingHint, hintFont,
                new Rectangle(Math.Max(12, Width - hintWidth - 12), 0, Math.Min(hintWidth, Width - 24), Height),
                Color.FromArgb(190, 255, 255, 255),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            if (Icon != RoundedButtonIcon.None)
            {
                DrawVectorIcon(graphics, Icon, new Rectangle((Width - 16) / 2, (Height - 16) / 2, 16, 16),
                    !Enabled ? Theme.Muted : _hovered || _focused ? Theme.Text : ForeColor);
            }
            var textBounds = new Rectangle(12, 0, Math.Max(0, Width - 24), Height);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        if (_focused)
        {
            using var focusPen = new Pen(edge, 1);
            using var focusPath = CreateRectanglePath(
                new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)));
            graphics.DrawPath(focusPen, focusPath);
        }
    }

    private static void DrawVectorIcon(Graphics graphics, RoundedButtonIcon icon, Rectangle bounds, Color color)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (icon == RoundedButtonIcon.Folder)
        {
            using var path = new GraphicsPath();
            path.AddLines(new[]
            {
                new Point(bounds.Left + 1, bounds.Top + 4),
                new Point(bounds.Left + 6, bounds.Top + 4),
                new Point(bounds.Left + 8, bounds.Top + 6),
                new Point(bounds.Right - 1, bounds.Top + 6),
                new Point(bounds.Right - 2, bounds.Bottom - 2),
                new Point(bounds.Left + 2, bounds.Bottom - 2),
                new Point(bounds.Left + 1, bounds.Top + 4),
            });
            graphics.DrawPath(pen, path);
        }
        else if (icon == RoundedButtonIcon.Refresh)
        {
            graphics.DrawArc(pen, bounds.Left + 2, bounds.Top + 2, 12, 12, 205, 275);
            graphics.DrawLines(pen, new[]
            {
                new Point(bounds.Left + 1, bounds.Top + 3),
                new Point(bounds.Left + 1, bounds.Top + 7),
                new Point(bounds.Left + 5, bounds.Top + 7),
            });
        }
        else if (icon == RoundedButtonIcon.Settings)
        {
            graphics.DrawEllipse(pen, bounds.Left + 4, bounds.Top + 4, 8, 8);
            graphics.DrawEllipse(pen, bounds.Left + 7, bounds.Top + 7, 2, 2);
            graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 1, bounds.Left + 8, bounds.Top + 4);
            graphics.DrawLine(pen, bounds.Left + 8, bounds.Bottom - 4, bounds.Left + 8, bounds.Bottom - 1);
            graphics.DrawLine(pen, bounds.Left + 1, bounds.Top + 8, bounds.Left + 4, bounds.Top + 8);
            graphics.DrawLine(pen, bounds.Right - 4, bounds.Top + 8, bounds.Right - 1, bounds.Top + 8);
        }
        else if (icon == RoundedButtonIcon.Library)
        {
            graphics.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 3, 5, 11);
            graphics.DrawRectangle(pen, bounds.Left + 9, bounds.Top + 3, 5, 11);
            graphics.DrawLine(pen, bounds.Left + 7, bounds.Top + 4, bounds.Left + 7, bounds.Bottom - 2);
            graphics.DrawLine(pen, bounds.Left + 9, bounds.Top + 4, bounds.Left + 9, bounds.Bottom - 2);
        }
        else if (icon is RoundedButtonIcon.Sidebar or RoundedButtonIcon.SidebarExpand)
        {
            graphics.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 2, 12, 12);
            graphics.DrawLine(pen, bounds.Left + 6, bounds.Top + 2, bounds.Left + 6, bounds.Bottom - 2);
            if (icon == RoundedButtonIcon.SidebarExpand)
            {
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 6, bounds.Left + 11, bounds.Top + 8);
                graphics.DrawLine(pen, bounds.Left + 11, bounds.Top + 8, bounds.Left + 8, bounds.Top + 10);
            }
        }
    }

    internal static GraphicsPath CreateRectanglePath(Rectangle bounds)
    {
        return CreateRoundedRectanglePath(bounds, 6);
    }

    internal static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
