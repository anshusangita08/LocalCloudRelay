namespace LocalCloudRelay;

/// <summary>Adds or edits an API-key or account-backed provider profile.</summary>
internal sealed class ProviderEditorForm : Form
{
    private readonly TextBox _nameTextBox = Field();
    private readonly ComboBox _presetComboBox = Combo();
    private readonly ComboBox _kindComboBox = Combo();
    private readonly ComboBox _authModeComboBox = Combo();
    private readonly TextBox _baseUrlTextBox = Field();
    private readonly TextBox _apiKeyTextBox = Field();
    private readonly TextBox _oauthClientIdTextBox = Field();
    private readonly TextBox _oauthClientSecretTextBox = Field();
    private readonly TextBox _cloudProjectIdTextBox = Field();
    private readonly TextBox _cliExecutableTextBox = Field();
    private readonly TextBox _manualModelsTextBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _noteLabel = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        MaximumSize = new Size(580, 0),
        ForeColor = Palette.TextMuted,
        BackColor = Color.Transparent,
        Padding = new Padding(0, 6, 0, 0)
    };
    private readonly CheckBox _enabledCheckBox = new()
    {
        Text = "Enabled",
        AutoSize = true,
        ForeColor = Palette.TextSecondary,
        BackColor = Color.Transparent
    };
    private readonly ProviderSettings? _existing;

    public ProviderSettings? Result { get; private set; }

    public ProviderEditorForm(ProviderSettings? existing)
    {
        _existing = existing;
        Text = existing is null ? "Add provider" : "Edit provider";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Panel;
        ForeColor = Palette.TextPrimary;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(650, 720);
        MinimumSize = new Size(600, 620);

        _kindComboBox.Items.AddRange([
            new ProviderKindItem("OpenAI", ProviderKinds.OpenAi),
            new ProviderKindItem("Anthropic", ProviderKinds.Anthropic),
            new ProviderKindItem("Gemini", ProviderKinds.Gemini),
            new ProviderKindItem("Claude Code", ProviderKinds.ClaudeCode),
            new ProviderKindItem("Antigravity", ProviderKinds.Antigravity),
            new ProviderKindItem("Gemini CLI", ProviderKinds.GeminiCli),
            new ProviderKindItem("Local (free)", ProviderKinds.Local)
        ]);
        _presetComboBox.Items.AddRange(ProviderPresets.All.Select(p => (object)p.Label).ToArray());
        _presetComboBox.SelectedIndexChanged += (_, _) => ApplyPreset();
        _presetComboBox.SelectedIndex = ProviderPresets.All.Count - 1;
        _presetComboBox.Enabled = existing is null;
        _kindComboBox.SelectedIndexChanged += (_, _) => ConfigureAuthModes(preserveSelection: true);
        _authModeComboBox.SelectedIndexChanged += (_, _) => UpdateFieldAvailability();
        _kindComboBox.SelectedIndexChanged += (_, _) => ApplyProviderDefaults();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Palette.Panel,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "Preset", _presetComboBox, 38);
        AddRow(layout, 1, "Name", _nameTextBox, 38);
        AddRow(layout, 2, "Provider", _kindComboBox, 38);
        AddRow(layout, 3, "Authentication", _authModeComboBox, 38);
        AddRow(layout, 4, "Base URL", _baseUrlTextBox, 38);
        AddRow(layout, 5, "API key", _apiKeyTextBox, 38);
        AddRow(layout, 6, "OAuth client ID", _oauthClientIdTextBox, 38);
        AddRow(layout, 7, "OAuth client secret", _oauthClientSecretTextBox, 38);
        AddRow(layout, 8, "Cloud project ID", _cloudProjectIdTextBox, 38);
        AddRow(layout, 9, "CLI executable", _cliExecutableTextBox, 38);
        AddRow(layout, 10, "Manual model IDs", _manualModelsTextBox, 112);
        AddRow(layout, 11, string.Empty, _enabledCheckBox, 36);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.SetColumnSpan(_noteLabel, 2);
        layout.Controls.Add(_noteLabel, 0, 12);

        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.Panel };
        scrollPanel.Controls.Add(layout);
        Controls.Add(scrollPanel);

        var actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Palette.Panel,
            Padding = new Padding(12)
        };
        var cancelButton = DarkButton("Cancel");
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var saveButton = DarkButton("Save", primary: true);
        saveButton.Click += (_, _) => Save();
        actionsPanel.Controls.Add(cancelButton);
        actionsPanel.Controls.Add(saveButton);
        Controls.Add(actionsPanel);
        AcceptButton = saveButton;

        _oauthClientSecretTextBox.UseSystemPasswordChar = true;
        if (existing is null)
        {
            _kindComboBox.SelectedIndex = 0;
            _nameTextBox.Text = "OpenAI";
            _enabledCheckBox.Checked = true;
        }
        else
        {
            _nameTextBox.Text = existing.Name;
            _baseUrlTextBox.Text = existing.BaseUrl;
            _apiKeyTextBox.Text = existing.ApiKey ?? string.Empty;
            _oauthClientIdTextBox.Text = existing.OAuthClientId ?? string.Empty;
            _oauthClientSecretTextBox.Text = existing.OAuthClientSecret ?? string.Empty;
            _cloudProjectIdTextBox.Text = existing.ProjectId ?? string.Empty;
            _cliExecutableTextBox.Text = existing.CliExecutable ?? string.Empty;
            _manualModelsTextBox.Text = string.Join(Environment.NewLine, existing.ImportedModels ?? []);
            _enabledCheckBox.Checked = existing.Enabled;
            var index = _kindComboBox.Items.Cast<ProviderKindItem>()
                .Select((item, i) => (item, i))
                .FirstOrDefault(x => x.item.Value.Equals(existing.Kind, StringComparison.OrdinalIgnoreCase));
            _kindComboBox.SelectedIndex = index.i >= 0 ? index.i : 0;
            ConfigureAuthModes(preserveSelection: false);
            _authModeComboBox.SelectedItem = existing.AuthMode;
        }
        ConfigureAuthModes(preserveSelection: true);
        UpdateFieldAvailability();
        UpdateNote();
    }

    private void ConfigureAuthModes(bool preserveSelection)
    {
        var oldMode = preserveSelection ? SelectedAuthMode : (ProviderAuthMode?)null;
        _authModeComboBox.Items.Clear();
        foreach (var mode in ModesFor(SelectedKind)) _authModeComboBox.Items.Add(mode);
        _authModeComboBox.SelectedItem = oldMode is not null && _authModeComboBox.Items.Contains(oldMode.Value)
            ? oldMode.Value
            : _authModeComboBox.Items.Count > 0 ? _authModeComboBox.Items[0] : null;
        UpdateFieldAvailability();
        UpdateNote();
    }

    private static IReadOnlyList<ProviderAuthMode> ModesFor(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderKinds.OpenAi => [ProviderAuthMode.ApiKey, ProviderAuthMode.OAuth],
        ProviderKinds.Gemini => [ProviderAuthMode.ApiKey, ProviderAuthMode.OAuth],
        ProviderKinds.ClaudeCode or ProviderKinds.Antigravity or ProviderKinds.GeminiCli => [ProviderAuthMode.CliAccount],
        _ => [ProviderAuthMode.ApiKey]
    };

    private string SelectedKind => (_kindComboBox.SelectedItem as ProviderKindItem)?.Value ?? ProviderKinds.OpenAi;
    private ProviderAuthMode SelectedAuthMode => _authModeComboBox.SelectedItem is ProviderAuthMode mode
        ? mode : ProviderAuthMode.ApiKey;

    private void ApplyProviderDefaults()
    {
        if (string.IsNullOrWhiteSpace(_baseUrlTextBox.Text))
            _baseUrlTextBox.Text = DefaultBaseUrl(SelectedKind);
        if (string.IsNullOrWhiteSpace(_nameTextBox.Text) || _nameTextBox.Text is "OpenAI" or "Anthropic" or "Gemini" or "Claude Code" or "Antigravity")
            _nameTextBox.Text = DisplayKind(SelectedKind);
    }

    private static string DefaultBaseUrl(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderKinds.OpenAi => "https://api.openai.com/v1",
        ProviderKinds.Anthropic or ProviderKinds.ClaudeCode => "https://api.anthropic.com/v1",
        ProviderKinds.Gemini or ProviderKinds.GeminiCli => "https://generativelanguage.googleapis.com",
        ProviderKinds.Antigravity => "https://cloudcode-pa.googleapis.com",
        _ => "http://127.0.0.1:11434/v1"
    };

    private static string DisplayKind(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderKinds.OpenAi => "OpenAI",
        ProviderKinds.Anthropic => "Anthropic",
        ProviderKinds.Gemini => "Gemini",
        ProviderKinds.ClaudeCode => "Claude Code",
        ProviderKinds.Antigravity => "Antigravity",
        ProviderKinds.GeminiCli => "Gemini CLI",
        _ => "Local"
    };

    private void UpdateFieldAvailability()
    {
        var kind = SelectedKind;
        var mode = SelectedAuthMode;
        var cli = mode == ProviderAuthMode.CliAccount;
        var oauth = mode == ProviderAuthMode.OAuth;
        _apiKeyTextBox.Enabled = mode == ProviderAuthMode.ApiKey;
        _oauthClientIdTextBox.Enabled = oauth;
        _oauthClientSecretTextBox.Enabled = oauth && kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase);
        _cloudProjectIdTextBox.Enabled = oauth && kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase);
        _cliExecutableTextBox.Enabled = cli;
        _baseUrlTextBox.Enabled = !cli;
    }

    private void UpdateNote()
    {
        _noteLabel.Text = SelectedKind switch
        {
            ProviderKinds.Gemini when SelectedAuthMode == ProviderAuthMode.OAuth =>
                "Gemini OAuth needs a Google desktop OAuth client ID, client secret, and Cloud project ID. Enable the Generative Language API for that project.",
            ProviderKinds.OpenAi when SelectedAuthMode == ProviderAuthMode.OAuth =>
                "OpenAI OAuth signs in with a ChatGPT plan. First sign-in registers this local app; subsequent sign-ins use the saved account registration.",
            ProviderKinds.ClaudeCode =>
                "Claude Code uses its official CLI account. It has no supported account model catalog; enter model IDs manually.",
            ProviderKinds.GeminiCli =>
                "Gemini CLI uses its own Google login. It has no model-list command; edit the model IDs below.",
            ProviderKinds.Antigravity =>
                "Antigravity uses its official CLI account and model catalog. CLI tool actions follow that CLI user's permissions; sandbox/temp working directory reduce scope but do not guarantee tools are disabled.",
            _ => "Model IDs can be separated by commas or new lines. Manual IDs are used by account-backed profiles."
        };
    }

    private void ApplyPreset()
    {
        if (_presetComboBox.SelectedItem is not string selected) return;
        var preset = ProviderPresets.Find(selected);
        if (preset is null) return;
        _nameTextBox.Text = preset.Name;
        _baseUrlTextBox.Text = preset.BaseUrl;
        if (preset.Label != "Custom")
        {
            var index = _kindComboBox.Items.Cast<ProviderKindItem>().Select((item, i) => (item, i))
                .FirstOrDefault(x => x.item.Value.Equals(preset.Kind, StringComparison.OrdinalIgnoreCase));
            if (index.i >= 0) _kindComboBox.SelectedIndex = index.i;
        }
        _noteLabel.Text = preset.Note ?? string.Empty;
    }

    private sealed record ProviderKindItem(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private void Save()
    {
        var name = _nameTextBox.Text.Trim();
        var kind = SelectedKind;
        var authMode = SelectedAuthMode;
        var cli = authMode == ProviderAuthMode.CliAccount;
        var url = _baseUrlTextBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give the provider a name.", "Provider settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!cli && (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) || parsedUri.Scheme is not ("http" or "https")))
        {
            MessageBox.Show(this, "Enter a valid http(s) base URL.", "Provider settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (authMode == ProviderAuthMode.OAuth && kind == ProviderKinds.Gemini &&
            (string.IsNullOrWhiteSpace(_oauthClientIdTextBox.Text) || string.IsNullOrWhiteSpace(_oauthClientSecretTextBox.Text) ||
             string.IsNullOrWhiteSpace(_cloudProjectIdTextBox.Text)))
        {
            MessageBox.Show(this, "Gemini OAuth needs the desktop client ID, client secret, and Cloud project ID.", "Provider settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var id = _existing?.Id ?? Guid.NewGuid().ToString("N")[..8];
        var original = _existing;
        var apiKey = _apiKeyTextBox.Text.Trim();
        var clientId = _oauthClientIdTextBox.Text.Trim();
        var clientSecret = _oauthClientSecretTextBox.Text.Trim();
        var projectId = _cloudProjectIdTextBox.Text.Trim();
        var cliExecutable = _cliExecutableTextBox.Text.Trim();
        var models = ParseModelIds(_manualModelsTextBox.Text);
        var parsedBaseUrl = cli ? DefaultBaseUrl(kind) : NormalizeBaseUrl(new Uri(url, UriKind.Absolute));
        Result = new ProviderSettings(id, name, kind, parsedBaseUrl,
            authMode == ProviderAuthMode.ApiKey && apiKey.Length > 0 ? apiKey : null,
            _enabledCheckBox.Checked, original?.Priority ?? 0,
            DisabledModels: original?.DisabledModels,
            AuthMode: authMode,
            AccountId: original?.AccountId,
            ProjectId: projectId.Length > 0 ? projectId : null,
            CliExecutable: cli ? (cliExecutable.Length > 0 ? cliExecutable : DefaultCliExecutable(kind)) : null,
            ImportedModels: models,
            ModelsFetchedAt: original?.ModelsFetchedAt,
            OAuthTokens: original?.OAuthTokens,
            OAuthClientId: clientId.Length > 0 ? clientId : null,
            OAuthClientSecret: clientSecret.Length > 0 ? clientSecret : null,
            OAuthEmail: original?.OAuthEmail,
            OAuthIssuer: original?.OAuthIssuer);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string DefaultCliExecutable(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderKinds.ClaudeCode => "claude",
        ProviderKinds.GeminiCli => "gemini",
        _ => "agy"
    };

    internal static IReadOnlyList<string> ParseModelIds(string text) => text
        .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(model => model.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal static string NormalizeBaseUrl(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return text.Length == 0 ? uri.GetLeftPart(UriPartial.Authority) : text;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string caption, Control control, int height)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        if (caption.Length > 0) layout.Controls.Add(Caption(caption), 0, row);
        layout.Controls.Add(control, 1, row);
    }

    internal static ComboBox Combo() => new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary
    };

    internal static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        ForeColor = Palette.TextSecondary,
        BackColor = Color.Transparent,
        Padding = new Padding(0, 6, 0, 0)
    };

    internal static TextBox Field() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle
    };

    internal static Button DarkButton(string text, bool primary = false) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(14, 6, 14, 6),
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Palette.LinkUp : Palette.Hairline,
        ForeColor = primary ? Palette.Input : Palette.TextPrimary,
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand
    };
}
