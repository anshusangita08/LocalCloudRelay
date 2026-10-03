namespace LocalCloudRelay;

/// <summary>One entry in the Add dialog: a vendor and the ways it can authenticate.</summary>
internal sealed record ProviderChoice(string Label, bool HasAccount, bool HasApiKey, string Note)
{
    public override string ToString() => Label;
}

/// <summary>
/// The short Add flow: pick a vendor, pick account sign-in or an API key, done. Every
/// URL, client ID and executable is filled in from the choice. The full editor is still
/// one click away under "Advanced" for anything unusual.
/// </summary>
internal sealed class AddProviderForm : Form
{
    public const string ChatGpt = "ChatGPT / OpenAI";
    public const string Claude = "Claude";
    public const string Gemini = "Gemini";
    public const string Antigravity = "Antigravity";

    public static IReadOnlyList<ProviderChoice> Choices { get; } =
    [
        new(ChatGpt, true, true, "Sign in with your ChatGPT plan in the browser, or paste an OpenAI API key."),
        new(Claude, true, true, "Sign in with your Claude account through Claude Code, or paste an Anthropic API key."),
        new(Gemini, true, true, "Sign in with Google in the browser, or paste a Gemini API key. Google now serves personal Gemini sign-in through Antigravity, so this adds an Antigravity account."),
        new(Antigravity, true, false, "Sign in with Google in the browser."),
        .. ProviderPresets.All.Where(p => p.Label != "Custom")
            .Select(p => new ProviderChoice(p.Label, false, true, p.Note ?? "Paste the API key for this gateway."))
    ];

    private readonly ComboBox _choice = ProviderEditorForm.Combo();
    private readonly RadioButton _account = Radio("Sign in with my account");
    private readonly RadioButton _apiKey = Radio("Use an API key");
    private readonly TextBox _keyBox = ProviderEditorForm.Field();
    private readonly Label _note = new()
    {
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = Palette.TextMuted,
        BackColor = Color.Transparent,
        Padding = new Padding(0, 8, 0, 0)
    };
    private readonly Button _go = ProviderEditorForm.DarkButton("Sign in", primary: true);

    public ProviderSettings? Result { get; private set; }
    /// <summary>True when the caller should run account sign-in and fetch models next.</summary>
    public bool SignInRequested { get; private set; }
    /// <summary>True when the user asked for the full editor instead.</summary>
    public bool AdvancedRequested { get; private set; }

    public AddProviderForm()
    {
        Text = "Add provider";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Panel;
        ForeColor = Palette.TextPrimary;
        Font = new Font("Segoe UI", 9.5F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _keyBox.UseSystemPasswordChar = true;
        _keyBox.Width = 440;
        _keyBox.Dock = DockStyle.None;
        _choice.Dock = DockStyle.None;
        _choice.Width = 440;
        _choice.Items.AddRange(Choices.Cast<object>().ToArray());
        _choice.SelectedIndexChanged += (_, _) => Refresh(choiceChanged: true);
        _account.CheckedChanged += (_, _) => Refresh(choiceChanged: false);
        _apiKey.CheckedChanged += (_, _) => Refresh(choiceChanged: false);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(20),
            BackColor = Palette.Panel
        };
        layout.Controls.Add(ProviderEditorForm.Caption("Provider"));
        layout.Controls.Add(_choice);
        layout.Controls.Add(Spacer());
        layout.Controls.Add(ProviderEditorForm.Caption("How to connect"));
        layout.Controls.Add(_account);
        layout.Controls.Add(_apiKey);
        layout.Controls.Add(_keyBox);
        layout.Controls.Add(_note);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 16, 0, 0),
            BackColor = Color.Transparent
        };
        var advanced = ProviderEditorForm.DarkButton("Advanced...");
        advanced.Click += (_, _) => { AdvancedRequested = true; DialogResult = DialogResult.Retry; Close(); };
        var cancel = ProviderEditorForm.DarkButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _go.Click += (_, _) => Submit();
        actions.Controls.Add(_go);
        actions.Controls.Add(cancel);
        actions.Controls.Add(advanced);
        layout.Controls.Add(actions);
        Controls.Add(layout);
        AcceptButton = _go;
        CancelButton = cancel;

        _choice.SelectedIndex = 0;
    }

    private void Refresh(bool choiceChanged)
    {
        if (_choice.SelectedItem is not ProviderChoice choice) return;
        if (choiceChanged)
        {
            _account.Enabled = choice.HasAccount;
            _apiKey.Enabled = choice.HasApiKey;
            _account.Checked = choice.HasAccount;
            _apiKey.Checked = !choice.HasAccount;
        }
        _keyBox.Visible = _apiKey.Checked;
        _go.Text = _account.Checked ? "Sign in" : "Add";
        _note.Text = choice.Note;
    }

    private void Submit()
    {
        if (_choice.SelectedItem is not ProviderChoice choice) return;
        var key = _keyBox.Text.Trim();
        if (_apiKey.Checked && key.Length == 0 && !IsLocal(choice))
        {
            MessageBox.Show(this, "Paste the API key first.", "Add provider", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = Build(choice.Label, _account.Checked, key, Guid.NewGuid().ToString("N")[..8]);
        SignInRequested = _account.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsLocal(ProviderChoice choice) =>
        ProviderPresets.Find(choice.Label)?.Kind == ProviderKinds.Local;

    /// <summary>Maps a choice to a complete provider profile. Pure, so it is unit tested.</summary>
    internal static ProviderSettings Build(string label, bool account, string? apiKey, string id)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        return (label, account) switch
        {
            (ChatGpt, true) => new ProviderSettings(id, "ChatGPT", ProviderKinds.OpenAi, "https://api.openai.com/v1",
                null, true, 0, AuthMode: ProviderAuthMode.OAuth),
            (ChatGpt, false) => new ProviderSettings(id, "OpenAI", ProviderKinds.OpenAi, "https://api.openai.com/v1", key, true, 0),
            // Claude Code has no model-list command. Its aliases always mean the newest
            // model of each family; Fetch models resolves them to exact ids.
            (Claude, true) => new ProviderSettings(id, "Claude", ProviderKinds.ClaudeCode, "https://api.anthropic.com/v1",
                null, true, 0, AuthMode: ProviderAuthMode.CliAccount, CliExecutable: "claude",
                ImportedModels: ClaudeCodeGateway.ModelAliases),
            (Claude, false) => new ProviderSettings(id, "Anthropic", ProviderKinds.Anthropic, "https://api.anthropic.com/v1", key, true, 0),
            // Google retired Gemini CLI sign-in for personal accounts ("migrate to the
            // Antigravity suite"); a Google account reaches Gemini models through agy now.
            (Gemini, true) => new ProviderSettings(id, "Gemini (Google)", ProviderKinds.Antigravity, "https://cloudcode-pa.googleapis.com",
                null, true, 0, AuthMode: ProviderAuthMode.CliAccount, CliExecutable: "agy"),
            (Gemini, false) => new ProviderSettings(id, "Gemini", ProviderKinds.Gemini, "https://generativelanguage.googleapis.com", key, true, 0),
            (Antigravity, _) => new ProviderSettings(id, "Antigravity", ProviderKinds.Antigravity, "https://cloudcode-pa.googleapis.com",
                null, true, 0, AuthMode: ProviderAuthMode.CliAccount, CliExecutable: "agy"),
            _ when ProviderPresets.Find(label) is { } preset => new ProviderSettings(id, preset.Name, preset.Kind, preset.BaseUrl, key, true, 0),
            _ => throw new ArgumentException($"Unknown provider choice '{label}'.", nameof(label))
        };
    }

    private static RadioButton Radio(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Palette.TextPrimary,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 4, 0, 0)
    };

    private static Control Spacer() => new Panel { Height = 8, Width = 1, BackColor = Color.Transparent };
}
