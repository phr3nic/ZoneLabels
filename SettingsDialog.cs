namespace ZoneLab;

internal sealed class SettingsDialog : Form
{
    private readonly Func<AppConfig, bool> _save;
    private readonly Func<string> _purgeDone;
    private readonly List<ZoneRow> _zoneRows = new();
    private readonly FlowLayoutPanel _zoneList;
    private readonly Button _captureKeyButton;
    private readonly Button _discardKeyButton;
    private readonly ComboBox _precisionKeyInput;
    private readonly NumericUpDown _precisionFactorInput;
    private readonly NumericUpDown _doneRetentionInput;
    private readonly ToolTip _toolTip = new();
    private readonly bool _alwaysOnTop;
    private readonly bool _hideWhileCapturing;
    private HotkeyBinding _captureKey;
    private HotkeyBinding _discardKey;
    private Button? _capturingButton;
    private ZoneRow? _capturingZone;

    public SettingsDialog(AppConfig config, Func<AppConfig, bool> save, Func<string> purgeDone)
    {
        _save = save;
        _purgeDone = purgeDone;
        _captureKey = AppConfig.ParseHotkey(config.CaptureKey);
        _discardKey = AppConfig.ParseHotkey(config.DiscardKey);
        _alwaysOnTop = config.AlwaysOnTop;
        _hideWhileCapturing = config.HideWhileCapturing;

        Text = "Label settings";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        var shortcutPage = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(16),
        };
        var zonesPage = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(16),
            Visible = false,
        };

        var shortcutLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 6,
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
        shortcutLayout.Controls.Add(CreateCaption("Precision key"), 0, 2);
        _precisionKeyInput = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 155,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 8, 0, 8),
        };
        _precisionKeyInput.Items.AddRange(new object[]
        {
            "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt",
        });
        _precisionKeyInput.SelectedItem = config.PrecisionKey;
        if (_precisionKeyInput.SelectedIndex < 0)
            _precisionKeyInput.SelectedItem = "LeftShift";
        shortcutLayout.Controls.Add(_precisionKeyInput, 1, 2);
        shortcutLayout.Controls.Add(CreateCaption("Precision factor"), 0, 3);
        _precisionFactorInput = new NumericUpDown
        {
            Minimum = 0.05m,
            Maximum = 1m,
            Increment = 0.05m,
            DecimalPlaces = 2,
            Value = (decimal)config.PrecisionFactor,
            Width = 155,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 8, 0, 8),
        };
        StyleInput(_precisionFactorInput);
        shortcutLayout.Controls.Add(_precisionFactorInput, 1, 3);
        shortcutLayout.Controls.Add(CreateCaption("Done retention (days)"), 0, 4);
        _doneRetentionInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3650,
            Value = config.DoneRetentionDays,
            Width = 155,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 8, 0, 8),
        };
        StyleInput(_doneRetentionInput);
        shortcutLayout.Controls.Add(_doneRetentionInput, 1, 4);
        shortcutLayout.Controls.Add(new Label
        {
            Text = "Capture keys must be A-Z or number-row 0-9. Without Alt, shortcuts work only when the canvas has focus. Alt shortcuts are global except during settings or text entry; capture requires the pointer outside the window. Precision applies only while the pointer is over the canvas. Ctrl+Z undoes a pin; Ctrl+Shift+Z redoes it; Ctrl+1 to Ctrl+6 labels it.",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 16, 0, 0),
        }, 0, 5);
        shortcutLayout.SetColumnSpan(shortcutLayout.GetControlFromPosition(0, 5)!, 2);
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
        var zoneHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 8),
        };
        zoneHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        zoneHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        zoneHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        zoneHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        zoneHeader.Controls.Add(CreateCaption("Label"), 0, 0);
        zoneHeader.Controls.Add(CreateCaption("Hotkey"), 1, 0);
        zoneHeader.Controls.Add(CreateCaption("Goal"), 2, 0);
        var addZoneButton = MakeButton("＋  Add values");
        addZoneButton.Dock = DockStyle.Bottom;
        addZoneButton.Height = 32;
        addZoneButton.Margin = new Padding(0, 8, 0, 0);
        addZoneButton.Click += (_, _) => AddZone();
        zonesPage.Controls.Add(_zoneList);
        zonesPage.Controls.Add(zoneHeader);
        zonesPage.Controls.Add(addZoneButton);

        foreach (var zone in config.Zones)
            AddZoneRow(zone.Name, AppConfig.ParseHotkey(zone.Key),
                config.TargetCounts.GetValueOrDefault(zone.Name, 500));

        var tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.BgDeep,
            Padding = new Padding(8, 0, 0, 0),
        };
        var shortcutTab = MakeButton("Shortcuts");
        shortcutTab.Selected = true;
        shortcutTab.Margin = new Padding(0, 0, 8, 0);
        shortcutTab.Height = 32;
        var zonesTab = MakeButton("Zones");
        zonesTab.Margin = new Padding(0, 0, 8, 0);
        zonesTab.Height = 32;
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
        var purgeButton = MakeButton("Purge done now");
        var purgeStatus = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = new Padding(8, 8, 0, 0),
        };
        purgeButton.Click += (_, _) => purgeStatus.Text = _purgeDone();
        saveButton.Click += (_, _) => SaveSettings();
        cancelButton.DialogResult = DialogResult.Cancel;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 8),
        };
        actions.Controls.Add(purgeStatus);
        actions.Controls.Add(purgeButton);
        actions.Controls.Add(cancelButton);
        actions.Controls.Add(saveButton);
        var dialogSurface = new GlassPanel
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Panel,
            EdgeColor = Theme.Border,
            Padding = new Padding(1),
            Margin = Padding.Empty,
        };
        dialogSurface.Controls.Add(content);
        dialogSurface.Controls.Add(actions);
        dialogSurface.Controls.Add(tabBar);
        dialogSurface.Controls.Add(Theme.CreateDialogTitleBar(this, Text));
        Controls.Add(dialogSurface);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ApplyButtonTooltips(this);
        Disposed += (_, _) => _toolTip.Dispose();

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
        activeTab.Selected = true;
        inactiveTab.Selected = false;
    }

    private static Label CreateCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Text,
        Margin = new Padding(0, 8, 0, 8),
    };

    private static Button CreateKeyButton(HotkeyBinding key)
    {
        var button = MakeButton(AppConfig.FormatHotkey(key));
        button.Width = 155;
        button.Height = 32;
        button.Margin = Padding.Empty;
        button.AccessibleName = $"Shortcut {AppConfig.FormatHotkey(key)}";
        return button;
    }

    private void ApplyButtonTooltips(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button button)
            {
                string text = string.IsNullOrWhiteSpace(button.AccessibleName)
                    ? button.Text
                    : button.AccessibleName;
                _toolTip.SetToolTip(button, string.IsNullOrWhiteSpace(text) ? "Button" : text);
            }
            ApplyButtonTooltips(child);
        }
    }

    private static RoundedButton MakeButton(string text)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = false,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Width = TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont).Width + 32,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 0, 12, 0),
        };
        return button;
    }

    private void StartKeyCapture(Button button)
    {
        RestoreCaptureText();
        _capturingButton = button;
        _capturingZone = _zoneRows.FirstOrDefault(row => row.KeyButton == button);
        button.Text = "Press a key...";
        _toolTip.SetToolTip(button, "Press a supported key combination, or Escape to cancel.");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturingButton is null) return;
        e.Handled = true;
        e.SuppressKeyPress = true;

        HotkeyModifiers modifiers = HotkeyBinding.ReadModifiers(e.Modifiers);
        if (e.KeyCode == Keys.Escape && modifiers == HotkeyModifiers.None)
        {
            RestoreCaptureText();
            return;
        }

        if (e.KeyCode == Keys.Back && modifiers == HotkeyModifiers.None)
        {
            SetCapturedKey(new HotkeyBinding(Keys.None, HotkeyModifiers.None));
            return;
        }

        if (!AppConfig.IsSupportedKey(e.KeyCode))
        {
            _capturingButton.Text = "Use a non-modifier key";
            _toolTip.SetToolTip(_capturingButton, "Use a supported non-modifier key.");
            return;
        }

        if (_capturingButton == _captureKeyButton && !AppConfig.IsSupportedCaptureKey(e.KeyCode))
        {
            _capturingButton.Text = "Use A-Z or number-row 0-9";
            _toolTip.SetToolTip(_capturingButton, "Capture is limited to A-Z and number-row 0-9.");
            return;
        }
        SetCapturedKey(new HotkeyBinding(e.KeyCode, modifiers));
    }

    private void SetCapturedKey(HotkeyBinding key)
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
            HotkeyBinding key = _capturingButton == _captureKeyButton
                ? _captureKey
                : _capturingButton == _discardKeyButton
                    ? _discardKey
                    : _capturingZone?.Key ?? new HotkeyBinding(Keys.None, HotkeyModifiers.None);
            _capturingButton.Text = AppConfig.FormatHotkey(key);
            _capturingButton.AccessibleName = $"Shortcut {AppConfig.FormatHotkey(key)}";
            _toolTip.SetToolTip(_capturingButton, _capturingButton.AccessibleName);
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

        AddZoneRow(dialog.ZoneName, dialog.ZoneKey, dialog.TargetCount);
    }

    private HotkeyBinding FindAvailableKey()
    {
        var used = _zoneRows.Select(row => row.Key.Key)
            .Append(_captureKey.Key)
            .Append(_discardKey.Key)
            .ToHashSet();
        foreach (Keys key in Enumerable.Range(0, 9).Select(index => (Keys)((int)Keys.D1 + index))
                     .Concat(Enumerable.Range(0, 9).Select(index => (Keys)((int)Keys.NumPad1 + index))))
        {
            if (!used.Contains(key)) return new HotkeyBinding(key, HotkeyModifiers.None);
        }
        return new HotkeyBinding(Keys.None, HotkeyModifiers.None);
    }

    private void AddZoneRow(string name, HotkeyBinding key, int targetCount)
    {
        var row = new ZoneRow(name, key, targetCount);
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
        ApplyButtonTooltips(row.Panel);
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
            PrecisionKey = (string)_precisionKeyInput.SelectedItem!,
            PrecisionFactor = (float)_precisionFactorInput.Value,
            AlwaysOnTop = _alwaysOnTop,
            HideWhileCapturing = _hideWhileCapturing,
            DoneRetentionDays = (int)_doneRetentionInput.Value,
            TargetCounts = _zoneRows.ToDictionary(
                row => row.ZoneName,
                row => (int)row.TargetInput.Value,
                StringComparer.OrdinalIgnoreCase),
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
        public ZoneRow(string name, HotkeyBinding key, int targetCount)
        {
            NameBox = CreateTextBox(name, 205);
            NameBox.Margin = new Padding(0, 0, 8, 0);
            Key = key;
            KeyButton = CreateKeyButton(key);
            KeyButton.Width = 140;
            KeyButton.Height = 32;
            KeyButton.Margin = new Padding(0, 0, 8, 0);
            TargetInput = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 10000000,
                Value = targetCount,
                Width = 78,
                Height = 32,
                BackColor = Theme.BgDeep,
                ForeColor = Theme.Text,
                Margin = new Padding(0, 0, 8, 0),
            };
            StyleInput(TargetInput);
            RemoveButton = MakeButton("Remove");
            ((RoundedButton)RemoveButton).Quiet = true;
            RemoveButton.Width = 72;
            RemoveButton.Height = 32;
            RemoveButton.Margin = Padding.Empty;
            Panel = new FlowLayoutPanel
            {
                Width = 670,
                Height = 48,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 8),
            };
            Panel.Controls.Add(NameBox);
            Panel.Controls.Add(KeyButton);
            Panel.Controls.Add(TargetInput);
            Panel.Controls.Add(RemoveButton);
        }

        public FlowLayoutPanel Panel { get; }
        public TextBox NameBox { get; }
        public Button KeyButton { get; }
        public NumericUpDown TargetInput { get; }
        public Button RemoveButton { get; }
        public HotkeyBinding Key { get; set; }
        public string ZoneName => NameBox.Text.Trim();
    }

    private sealed class AddZoneDialog : Form
    {
        private readonly TextBox _name;
        private readonly Button _keyButton;
        private readonly NumericUpDown _targetInput;
        private HotkeyBinding _key;
        private bool _capturing;

        public string ZoneName => _name.Text.Trim();
        public HotkeyBinding ZoneKey => _key;
        public int TargetCount => (int)_targetInput.Value;

        public AddZoneDialog(HotkeyBinding initialKey)
        {
            _key = initialKey;
            Text = "Add Label";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(420, 230);
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
                RowCount = 4,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(CreateCaption("Name"), 0, 0);
            _name = CreateTextBox(string.Empty);
            _name.Dock = DockStyle.Fill;
            layout.Controls.Add(_name, 1, 0);
            layout.Controls.Add(CreateCaption("Hotkey"), 0, 1);
            _keyButton = CreateKeyButton(_key);
            var toolTips = new ToolTip();
            toolTips.SetToolTip(_keyButton, $"Shortcut {AppConfig.FormatHotkey(_key)}");
            _keyButton.Click += (_, _) =>
            {
                _capturing = true;
                _keyButton.Text = "Press a key...";
                toolTips.SetToolTip(_keyButton, "Press a supported key combination, or Escape to cancel.");
            };
            layout.Controls.Add(_keyButton, 1, 1);
            layout.Controls.Add(CreateCaption("Goal"), 0, 2);
            _targetInput = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 10000000,
                Value = 500,
                Width = 155,
                BackColor = Theme.BgDeep,
                ForeColor = Theme.Text,
                Margin = new Padding(0, 8, 0, 8),
            };
            StyleInput(_targetInput);
            layout.Controls.Add(_targetInput, 1, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = MakeButton("Cancel");
            cancel.Quiet = true;
            cancel.DialogResult = DialogResult.Cancel;
            var add = MakeButton("Add");
            add.Accent = true;
            toolTips.SetToolTip(cancel, "Cancel adding this label.");
            toolTips.SetToolTip(add, "Add this label with its selected shortcut and goal.");
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
            layout.Controls.Add(actions, 0, 3);
            layout.SetColumnSpan(actions, 2);
            var dialogSurface = new GlassPanel
            {
                Dock = DockStyle.Fill,
                SurfaceColor = Theme.Panel,
                EdgeColor = Theme.Border,
                Padding = new Padding(1),
                Margin = Padding.Empty,
            };
            dialogSurface.Controls.Add(layout);
            dialogSurface.Controls.Add(Theme.CreateDialogTitleBar(this, Text));
            Controls.Add(dialogSurface);
            ApplyTooltips(this, toolTips);
            Disposed += (_, _) => toolTips.Dispose();
            AcceptButton = add;
            CancelButton = cancel;

            KeyDown += (_, e) =>
            {
                if (!_capturing) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                HotkeyModifiers modifiers = HotkeyBinding.ReadModifiers(e.Modifiers);
                if (e.KeyCode == Keys.Escape && modifiers == HotkeyModifiers.None)
                {
                    _capturing = false;
                    _keyButton.Text = AppConfig.FormatHotkey(_key);
                    toolTips.SetToolTip(_keyButton, $"Shortcut {AppConfig.FormatHotkey(_key)}");
                }
                else if (e.KeyCode == Keys.Back && modifiers == HotkeyModifiers.None)
                {
                    _key = new HotkeyBinding(Keys.None, HotkeyModifiers.None);
                    _capturing = false;
                    _keyButton.Text = AppConfig.FormatHotkey(_key);
                    toolTips.SetToolTip(_keyButton, "No shortcut assigned.");
                }
                else if (AppConfig.IsSupportedKey(e.KeyCode))
                {
                    _key = new HotkeyBinding(e.KeyCode, modifiers);
                    _capturing = false;
                    _keyButton.Text = AppConfig.FormatHotkey(_key);
                    toolTips.SetToolTip(_keyButton, $"Shortcut {AppConfig.FormatHotkey(_key)}");
                }
                else
                {
                    _keyButton.Text = "Use a non-modifier key";
                    toolTips.SetToolTip(_keyButton, "Use a supported non-modifier key.");
                }
            };
        }

        private static void ApplyTooltips(Control parent, ToolTip toolTips)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is Button button && string.IsNullOrWhiteSpace(toolTips.GetToolTip(button)))
                {
                    string text = string.IsNullOrWhiteSpace(button.AccessibleName)
                        ? button.Text
                        : button.AccessibleName;
                    toolTips.SetToolTip(button, string.IsNullOrWhiteSpace(text) ? "Button" : text);
                }
                ApplyTooltips(child, toolTips);
            }
        }
    }

    private static TextBox CreateTextBox(string text, int width = 0)
    {
        var textBox = new TextBox
        {
            Text = text,
            Width = width,
            MaxLength = 40,
            BackColor = Theme.BgDeep,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        StyleInput(textBox);
        return textBox;
    }

    private static void StyleInput(Control input)
    {
        bool hovered = false;
        input.BackColor = Theme.BgDeep;
        input.MouseEnter += (_, _) =>
        {
            hovered = true;
            input.BackColor = Theme.PanelHi;
        };
        input.MouseLeave += (_, _) =>
        {
            hovered = false;
            if (!input.Focused) input.BackColor = Theme.BgDeep;
        };
        input.GotFocus += (_, _) => input.BackColor = Theme.PanelHi;
        input.LostFocus += (_, _) => input.BackColor = hovered ? Theme.PanelHi : Theme.BgDeep;
    }
}
