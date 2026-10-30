namespace ZoneLab;

internal sealed class SettingsDialog : Form
{
    private readonly Func<AppConfig, bool> _save;
    private readonly List<ZoneRow> _zoneRows = new();
    private readonly FlowLayoutPanel _zoneList;
    private readonly Button _captureKeyButton;
    private readonly Button _discardKeyButton;
    private readonly int _cropSize;
    private readonly bool _alwaysOnTop;
    private readonly bool _hideWhileCapturing;
    private Keys _captureKey;
    private Keys _discardKey;
    private Button? _capturingButton;
    private ZoneRow? _capturingZone;

    public SettingsDialog(AppConfig config, Func<AppConfig, bool> save)
    {
        _save = save;
        _captureKey = AppConfig.ParseKey(config.CaptureKey);
        _discardKey = AppConfig.ParseKey(config.DiscardKey);
        _cropSize = config.CropSize;
        _alwaysOnTop = config.AlwaysOnTop;
        _hideWhileCapturing = config.HideWhileCapturing;

        Text = "Label settings";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(660, 520);
        MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        var shortcutPage = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(18),
        };
        var zonesPage = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(18),
            Visible = false,
        };

        var shortcutLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0),
        };
        shortcutLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        shortcutLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        shortcutLayout.Controls.Add(CreateCaption("Capture"), 0, 0);
        _captureKeyButton = CreateKeyButton(_captureKey);
        shortcutLayout.Controls.Add(_captureKeyButton, 1, 0);
        shortcutLayout.Controls.Add(CreateCaption("Discard"), 0, 1);
        _discardKeyButton = CreateKeyButton(_discardKey);
        shortcutLayout.Controls.Add(_discardKeyButton, 1, 1);
        shortcutLayout.Controls.Add(new Label
        {
            Text = "Choose a shortcut, then press A–Z, 0–9, or F1–F12. Backspace clears it; Escape cancels.",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 16, 0, 0),
        }, 0, 2);
        shortcutLayout.SetColumnSpan(shortcutLayout.GetControlFromPosition(0, 2)!, 2);
        _captureKeyButton.Click += (_, _) => StartKeyCapture(_captureKeyButton);
        _discardKeyButton.Click += (_, _) => StartKeyCapture(_discardKeyButton);
        shortcutPage.Controls.Add(shortcutLayout);

        _zoneList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        var addZoneButton = MakeButton("＋  Add Label");
        addZoneButton.Accent = true;
        addZoneButton.Dock = DockStyle.Bottom;
        addZoneButton.Height = 36;
        addZoneButton.Margin = new Padding(0, 8, 0, 0);
        addZoneButton.Click += (_, _) => AddZone();
        zonesPage.Controls.Add(_zoneList);
        zonesPage.Controls.Add(addZoneButton);

        foreach (var zone in config.Zones)
            AddZoneRow(zone.Name, AppConfig.ParseKey(zone.Key));

        var tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.BgDeep,
            Padding = new Padding(10, 4, 0, 0),
        };
        var shortcutTab = MakeButton("Shortcuts");
        shortcutTab.Accent = true;
        shortcutTab.Margin = new Padding(0, 0, 6, 0);
        shortcutTab.Height = 30;
        var zonesTab = MakeButton("Zones");
        zonesTab.Margin = new Padding(0, 0, 6, 0);
        zonesTab.Height = 30;
        shortcutTab.Click += (_, _) => SelectPage(shortcutPage, zonesPage, shortcutTab, zonesTab);
        zonesTab.Click += (_, _) => SelectPage(zonesPage, shortcutPage, zonesTab, shortcutTab);
        tabBar.Controls.Add(shortcutTab);
        tabBar.Controls.Add(zonesTab);

        var pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        pageHost.Controls.Add(zonesPage);
        pageHost.Controls.Add(shortcutPage);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(1),
        };
        content.Controls.Add(pageHost);

        var saveButton = MakeButton("Save settings");
        saveButton.Accent = true;
        var cancelButton = MakeButton("Cancel");
        cancelButton.Quiet = true;
        saveButton.Click += (_, _) => SaveSettings();
        cancelButton.DialogResult = DialogResult.Cancel;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        actions.Controls.Add(cancelButton);
        actions.Controls.Add(saveButton);
        Controls.Add(content);
        Controls.Add(actions);
        Controls.Add(tabBar);
        Controls.Add(Theme.CreateDialogTitleBar(this, Text));
        AcceptButton = saveButton;
        CancelButton = cancelButton;

        KeyDown += OnKeyDown;
    }

    private static void SelectPage(
        Control activePage,
        Control inactivePage,
        RoundedButton activeTab,
        RoundedButton inactiveTab)
    {
        activePage.Visible = true;
        activePage.BringToFront();
        inactivePage.Visible = false;
        activeTab.Accent = true;
        inactiveTab.Accent = false;
    }

    private static Label CreateCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Text,
        Margin = new Padding(0, 10, 0, 10),
    };

    private static Button CreateKeyButton(Keys key)
    {
        var button = MakeButton(AppConfig.FormatKey(key));
        button.Width = 155;
        button.Height = 30;
        button.Margin = new Padding(0, 4, 0, 4);
        return button;
    }

    private static RoundedButton MakeButton(string text)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Margin = new Padding(6, 4, 0, 0),
            Padding = new Padding(8, 3, 8, 3),
        };
        return button;
    }

    private void StartKeyCapture(Button button)
    {
        RestoreCaptureText();
        _capturingButton = button;
        _capturingZone = _zoneRows.FirstOrDefault(row => row.KeyButton == button);
        button.Text = "Press a key...";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturingButton is null) return;
        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Escape)
        {
            RestoreCaptureText();
            return;
        }

        if (e.KeyCode == Keys.Back)
        {
            SetCapturedKey(Keys.None);
            return;
        }

        if (!AppConfig.IsSupportedKey(e.KeyCode))
        {
            _capturingButton.Text = "Use A-Z, 0-9, F1-F12";
            return;
        }

        SetCapturedKey(e.KeyCode);
    }

    private void SetCapturedKey(Keys key)
    {
        if (_capturingButton == _captureKeyButton) _captureKey = key;
        else if (_capturingButton == _discardKeyButton) _discardKey = key;
        else if (_capturingZone is not null) _capturingZone.Key = key;
        RestoreCaptureText();
    }

    private void RestoreCaptureText()
    {
        if (_capturingButton is not null)
        {
            Keys key = _capturingButton == _captureKeyButton
                ? _captureKey
                : _capturingButton == _discardKeyButton
                    ? _discardKey
                    : _capturingZone?.Key ?? Keys.None;
            _capturingButton.Text = AppConfig.FormatKey(key);
        }
        _capturingButton = null;
        _capturingZone = null;
    }

    private void AddZone()
    {
        if (_zoneRows.Count >= 20)
        {
            MessageBox.Show(this, "A maximum of 20 zones is supported.", "Zone limit",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new AddZoneDialog(FindAvailableKey());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_zoneRows.Any(row => string.Equals(row.NameBox.Text.Trim(), dialog.ZoneName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A zone with that name already exists.", "Duplicate zone",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AddZoneRow(dialog.ZoneName, dialog.ZoneKey);
    }

    private Keys FindAvailableKey()
    {
        var used = _zoneRows.Select(row => row.Key)
            .Append(_captureKey)
            .Append(_discardKey)
            .ToHashSet();
        foreach (Keys key in Enumerable.Range(0, 9).Select(index => (Keys)((int)Keys.D1 + index))
                     .Concat(Enumerable.Range(0, 9).Select(index => (Keys)((int)Keys.NumPad1 + index))))
        {
            if (!used.Contains(key)) return key;
        }
        return Keys.None;
    }

    private void AddZoneRow(string name, Keys key)
    {
        var row = new ZoneRow(name, key);
        row.KeyButton.Click += (_, _) => StartKeyCapture(row.KeyButton);
        row.RemoveButton.Click += (_, _) =>
        {
            if (_capturingButton == row.KeyButton) RestoreCaptureText();
            _zoneRows.Remove(row);
            _zoneList.Controls.Remove(row.Panel);
            row.Panel.Dispose();
        };
        _zoneRows.Add(row);
        _zoneList.Controls.Add(row.Panel);
    }

    private void SaveSettings()
    {
        if (_capturingButton is not null)
        {
            MessageBox.Show(this, "Finish or cancel shortcut capture first.", "Shortcut pending",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var updated = new AppConfig
        {
            CaptureKey = AppConfig.ToConfigKey(_captureKey),
            DiscardKey = AppConfig.ToConfigKey(_discardKey),
            CropSize = _cropSize,
            AlwaysOnTop = _alwaysOnTop,
            HideWhileCapturing = _hideWhileCapturing,
            Zones = _zoneRows.Select(row => new ZoneConfig
            {
                Name = row.NameBox.Text.Trim(),
                Key = AppConfig.ToConfigKey(row.Key),
            }).ToList(),
        };

        try
        {
            AppConfig.Validate(updated);
            if (!_save(updated)) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed class ZoneRow
    {
        public ZoneRow(string name, Keys key)
        {
            NameBox = CreateTextBox(name, 260);
            NameBox.Margin = new Padding(0, 3, 6, 0);
            Key = key;
            KeyButton = CreateKeyButton(key);
            KeyButton.Width = 105;
            KeyButton.Height = 27;
            RemoveButton = MakeButton("Remove");
            ((RoundedButton)RemoveButton).Quiet = true;
            RemoveButton.Width = 78;
            RemoveButton.Height = 27;
            Panel = new FlowLayoutPanel
            {
                Width = 560,
                Height = 42,
                WrapContents = false,
                Margin = new Padding(0, 3, 0, 3),
            };
            Panel.Controls.Add(NameBox);
            Panel.Controls.Add(KeyButton);
            Panel.Controls.Add(RemoveButton);
        }

        public FlowLayoutPanel Panel { get; }
        public TextBox NameBox { get; }
        public Button KeyButton { get; }
        public Button RemoveButton { get; }
        public Keys Key { get; set; }
    }

    private sealed class AddZoneDialog : Form
    {
        private readonly TextBox _name;
        private readonly Button _keyButton;
        private Keys _key;
        private bool _capturing;

        public string ZoneName => _name.Text.Trim();
        public Keys ZoneKey => _key;

        public AddZoneDialog(Keys initialKey)
        {
            _key = initialKey;
            Text = "Add Label";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(360, 178);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 3,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(CreateCaption("Name"), 0, 0);
            _name = CreateTextBox(string.Empty);
            _name.Dock = DockStyle.Fill;
            layout.Controls.Add(_name, 1, 0);
            layout.Controls.Add(CreateCaption("Hotkey"), 0, 1);
            _keyButton = CreateKeyButton(_key);
            _keyButton.Click += (_, _) => { _capturing = true; _keyButton.Text = "Press a key..."; };
            layout.Controls.Add(_keyButton, 1, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = MakeButton("Cancel");
            cancel.Quiet = true;
            cancel.DialogResult = DialogResult.Cancel;
            var add = MakeButton("Add");
            add.Accent = true;
            add.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text))
                {
                    MessageBox.Show(this, "Enter a zone name.", "Missing name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            actions.Controls.Add(cancel);
            actions.Controls.Add(add);
            layout.Controls.Add(actions, 0, 2);
            layout.SetColumnSpan(actions, 2);
            Controls.Add(layout);
            Controls.Add(Theme.CreateDialogTitleBar(this, Text));
            AcceptButton = add;
            CancelButton = cancel;

            KeyDown += (_, e) =>
            {
                if (!_capturing) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Escape)
                {
                    _capturing = false;
                    _keyButton.Text = AppConfig.FormatKey(_key);
                }
                else if (e.KeyCode == Keys.Back)
                {
                    _key = Keys.None;
                    _capturing = false;
                    _keyButton.Text = "None";
                }
                else if (AppConfig.IsSupportedKey(e.KeyCode))
                {
                    _key = e.KeyCode;
                    _capturing = false;
                    _keyButton.Text = AppConfig.FormatKey(_key);
                }
                else
                {
                    _keyButton.Text = "Use A-Z, 0-9, F1-F12";
                }
            };
        }
    }

    private static TextBox CreateTextBox(string text, int width = 0) => new()
    {
        Text = text,
        Width = width,
        MaxLength = 40,
        BackColor = Theme.BgDeep,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
    };
}
