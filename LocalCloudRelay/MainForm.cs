using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

namespace LocalCloudRelay;

/// <summary>
/// One page, no tabs, no timers. The two things a person actually opens this for are
/// handing someone the endpoint and checking whether anything is broken, so those lead.
/// Nothing streams: every number is read when Refresh is pressed.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly RelaySettingsStore _settingsStore = new();
    private readonly CatalogCache _catalogCache = new();
    private readonly RelayTelemetryStore _telemetry = new();
    // Every request, kept per day across restarts, and one line per failed or retried one.
    private readonly UsageHistory _history = new(RelayPaths.File("usage"));
    private readonly DiagnosticLog _diagnostics = new(RelayPaths.File("diagnostics.log"));
    private IReadOnlyList<UsagePeriod> _historySummary = [];
    private readonly DataGridView _historyGrid = Grid();
    private readonly RelayServer _server;
    private readonly NotifyIcon _tray;
    private Icon _trayIcon;

    private RelayConfig? _config;
    private CatalogRefresh? _refresh;
    // Every model each provider has offered, remembered across launches, so a model that
    // appears later is flagged as new and starts switched off.
    private readonly KnownModelsStore _knownStore = new(RelayPaths.File("known-models.json"));
    private KnownModels _known = KnownModels.Empty;
    private ModelChangeTracker.Result? _lastModelChange;
    private readonly DailyNotices _notices = new();
    private readonly System.Windows.Forms.Timer _autoRefresh = new();
    private bool _busy;
    private bool _userStopped;

    private readonly Panel _rail = new() { Dock = DockStyle.Left, Width = 3, BackColor = Palette.Hairline };
    private readonly Label _dot = new() { AutoSize = true, Text = "\u25CF", Font = new Font("Segoe UI", 13, FontStyle.Bold), BackColor = Color.Transparent };
    private readonly Label _status = new() { AutoSize = true, BackColor = Color.Transparent };
    private readonly Label _modelCount = new() { AutoSize = true, BackColor = Color.Transparent };
    private readonly Label _clients = new() { AutoSize = true, BackColor = Color.Transparent };
    private readonly Label _baseUrl = ValueLabel();
    private readonly Label _apiKey = ValueLabel();

    private readonly DataGridView _providerGrid = Grid();
    private readonly DataGridView _ledger = Grid();
    private readonly DataGridView _usageGrid = Grid();
    private readonly DataGridView _allowanceGrid = Grid();
    private readonly DataGridView _routerGrid = Grid();
    private readonly RouterEngine _routerEngine = new();
    private readonly DarkTabControl _modelTabs = new()
    {
        Dock = DockStyle.Fill,
        StripColor = Palette.Panel,
        Appearance = TabAppearance.Normal,
        SizeMode = TabSizeMode.FillToRight,
        Padding = new Point(14, 4)
    };
    private readonly DarkTabControl _mainTabs = new()
    {
        Dock = DockStyle.Fill,
        StripColor = Palette.Rack,
        Appearance = TabAppearance.Normal,
        SizeMode = TabSizeMode.FillToRight,
        Padding = new Point(16, 6)
    };

    public MainForm()
    {
        _server = new RelayServer(_telemetry, routerMemory: new RouterMemoryStore(RelayPaths.File("routing.json")),
            routerSpend: new RouterSpend(RelayPaths.File("spend.json")));
        // One icon object shared by the taskbar and the tray, so the two can never
        // disagree about the state. The real state arrives from StartupAsync.
        _trayIcon = RelayStatusIcon.Create(RelayStatusIcon.ColorIdle);

        Text = "Local Cloud Relay";
        Width = 1180;
        Height = 800;
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Rack;
        ForeColor = Palette.TextPrimary;
        Font = new Font("Segoe UI", 9F);
        Icon = _trayIcon;

        _tray = new NotifyIcon
        {
            Text = "Local Cloud Relay",
            Icon = _trayIcon,
            ContextMenuStrip = BuildTrayMenu(),
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        _telemetry.Recorded += record =>
        {
            _history.Append(record);
            _diagnostics.Write(record);
        };
        // An all-day session would otherwise only see new models and price changes on the
        // next launch. Skipped, not queued, when another action is running; it never opens
        // a sign-in window.
        _autoRefresh.Interval = (int)AutoRefreshInterval.TotalMilliseconds;
        _autoRefresh.Tick += (_, _) => { if (_config is not null && !_userStopped) _ = RunGuardedAsync(RefreshAllAsync); };
        _autoRefresh.Start();
        // Request threads raise these; the balloon has to be shown from the UI thread.
        _server.Notice += notice =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() => ShowNotice(notice));
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        Shown += async (_, _) => await StartupAsync();

        // A header, then tabs. This was one long scrolling page, which squeezed the
        // provider grid and the model grid into fixed slices and clipped whatever did
        // not fit - the model table lost its lower rows and its rightmost columns.
        // Each section now owns a tab, so it gets the whole window.
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Palette.Rack,
            Padding = new Padding(20, 16, 20, 18)
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(Header(), 0, 0);

        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.TabPages.Add(Page("Connection", ConnectionBlock()));
        _mainTabs.TabPages.Add(Page("Providers", Section("PROVIDERS",
            "Requests are routed to the provider that serves the model you pick.", _providerGrid, ProviderActions(), stretch: true,
            actionsBelow: true)));
        _mainTabs.TabPages.Add(Page("Models", Section("MODELS",
            "One tab per provider. Uncheck a model to stop serving it downstream.", _modelTabs, ModelActions(), stretch: true)));
        _mainTabs.TabPages.Add(Page("Routers", Section("ROUTERS",
            "Named models your agent can ask for. The relay decides what they point at.", _routerGrid, RouterActions(), stretch: true)));
        _mainTabs.TabPages.Add(Page("Session", SessionBlock()));
        shell.Controls.Add(_mainTabs, 0, 1);

        Controls.Add(shell);
        Controls.Add(_rail);

        FormClosing += (_, _) =>
        {
            _autoRefresh.Stop();
            _autoRefresh.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            // Icon and _trayIcon are the same object, so it is disposed once.
            _trayIcon.Dispose();
            Icon = null;
            _server.Dispose();
        };
    }

    // ---------------------------------------------------------------- layout

    /// <summary>
    /// Wraps a section in a tab page with its own scrollbar. The scroll is per tab
    /// rather than for the whole window, so a tall model table cannot push the
    /// provider list off the bottom of the screen.
    /// </summary>
    private static TabPage Page(string title, Control content)
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Palette.Rack,
            Padding = new Padding(0, 12, 0, 0)
        };
        scroll.Controls.Add(content);
        var page = new TabPage(title)
        {
            BackColor = Palette.Rack,
            Padding = new Padding(0),
            UseVisualStyleBackColor = false
        };
        page.Controls.Add(scroll);
        return page;
    }

    private Control ConnectionBlock()
    {
        var group = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Palette.Rack, Padding = new Padding(0, 0, 0, 18) };
        group.Controls.Add(Heading("ENDPOINT", "One base URL and one key. Every client uses these two values.", null, null), 0, 0);

        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Palette.Panel, Padding = new Padding(14, 8, 14, 8) };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // AutoSize rows, so the buttons define the row height instead of being
        // clipped by a fixed one.
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var url = new Label { Text = "Base URL", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Palette.TextSecondary, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        rows.Controls.Add(url, 0, 0);
        rows.Controls.Add(_baseUrl, 1, 0);
        rows.Controls.Add(SmallButton("Copy", () => CopyToClipboard(_baseUrl.Text, "base URL")), 2, 0);

        var key = new Label { Text = "API key", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Palette.TextSecondary, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        rows.Controls.Add(key, 0, 1);
        rows.Controls.Add(_apiKey, 1, 1);
        rows.Controls.Add(SmallButton("Copy", () => CopyToClipboard(_config?.LocalApiKey ?? string.Empty, "API key")), 2, 1);

        var hint = new Label
        {
            Text = "Use this as the base URL for OpenAI, Anthropic and Gemini shaped clients alike - " +
                   "no version segment, and it works with a /v1 one too. " +
                   "Routing is by model name, in the background. " +
                   "Antigravity as a client is the exception: Google does not support an API key against a custom endpoint.",
            AutoSize = true,
            // MaximumSize is what makes a WinForms label wrap instead of running off
            // the right edge of its container.
            MaximumSize = new Size(1080, 0),
            Dock = DockStyle.Top,
            ForeColor = Palette.TextMuted,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 10, 0, 0)
        };
        group.Controls.Add(rows, 0, 1);
        group.Controls.Add(hint, 0, 2);

        _clients.ForeColor = Palette.TextTertiary;
        _clients.Text = "No clients connected";
        _clients.Dock = DockStyle.Top;
        _clients.Padding = new Padding(0, 8, 0, 0);
        group.Controls.Add(_clients, 0, 3);
        return group;
    }

    private Control Header()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Palette.Rack, Padding = new Padding(0, 0, 0, 14) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Local Cloud Relay",
            AutoSize = true,
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            ForeColor = Palette.TextPrimary,
            BackColor = Color.Transparent
        };
        _dot.ForeColor = Palette.TextMuted;
        _status.ForeColor = Palette.TextMuted;
        _status.Text = "Not configured";

        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        left.Controls.Add(_dot);
        left.Controls.Add(title);
        left.Controls.Add(_status);
        panel.Controls.Add(left, 0, 0);

        _modelCount.ForeColor = Palette.TextTertiary;
        _modelCount.Text = "No models yet";
        _modelCount.Dock = DockStyle.Top;
        _modelCount.TextAlign = ContentAlignment.MiddleRight;
        panel.Controls.Add(_modelCount, 1, 0);
        // One refresh control for the whole app: models for every provider, published
        // prices, and the status and ledger. It also runs on launch, so there is no
        // per-provider fetch or price button to remember.
        panel.Controls.Add(SmallButton("Refresh all", () => _ = RunGuardedAsync(RefreshAllAsync)), 2, 0);
        return panel;
    }

    /// <summary>
    /// A titled section. <paramref name="stretch"/> makes the section fill its tab and
    /// hands the content the remaining height, which is what a grid wants: it then
    /// scrolls internally instead of being clipped by an arbitrary fixed height.
    /// </summary>
    /// <summary>
    /// The Session tab: totals for the whole relay, then the same numbers per model.
    /// This is where measured spend lives - it is not mixed into the model catalogue,
    /// where it competed with the published price for the reader's attention.
    /// </summary>
    private Control SessionBlock()
    {
        var group = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            BackColor = Palette.Rack
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        // The ledger is one row tall by nature; the per-model grid takes the rest.
        _ledger.Dock = DockStyle.Fill;
        _usageGrid.Dock = DockStyle.Fill;
        _ledger.MinimumSize = new Size(0, 92);

        group.Controls.Add(Heading("THIS SESSION", "Since the relay started. Press Refresh to re-read.", null, null), 0, 0);
        group.Controls.Add(_ledger, 0, 1);
        group.Controls.Add(Heading("PER MODEL", "Measured from your own traffic, not from a price table.", null, null), 0, 2);
        group.Controls.Add(_usageGrid, 0, 3);
        _allowanceGrid.Dock = DockStyle.Fill;
        group.Controls.Add(Heading("PLAN ALLOWANCES", "Requests used in the current 5-hour window, per model with a published allowance. Routing steps aside at 90%.", null, null), 0, 4);
        group.Controls.Add(_allowanceGrid, 0, 5);
        _historyGrid.Dock = DockStyle.Fill;
        group.Controls.Add(Heading("HISTORY", "Kept across restarts for 30 days. Updated on launch and on Refresh all.", null, null), 0, 6);
        group.Controls.Add(_historyGrid, 0, 7);
        return group;
    }

    private Control Section(string title, string subtitle, Control content, Control? actions, bool stretch = false,
        bool actionsBelow = false)
    {
        var group = new TableLayoutPanel
        {
            // A stretching section has to fill the tab page, not sit at the top of it.
            // Docked to the top it kept its default height, which is what clipped the
            // model table to its first rows.
            Dock = stretch ? DockStyle.Fill : DockStyle.Top,
            AutoSize = !stretch,
            ColumnCount = 1,
            BackColor = Palette.Rack
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(Heading(title, subtitle, null, actions, actionsBelow), 0, 0);

        if (stretch)
        {
            group.RowCount = 2;
            group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Dock = DockStyle.Fill;
        }
        else
        {
            content.Dock = DockStyle.Top;
            // An AutoSize panel already knows its height; forcing one would fight it.
            if (!content.AutoSize) content.Height = content.PreferredSize.Height;
        }
        group.Controls.Add(content, 0, 1);
        return group;
    }

    private Control ModelActions()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        panel.Controls.Add(SmallButton("Serve all", () => SetAllServed(true)));
        panel.Controls.Add(SmallButton("Serve none", () => SetAllServed(false)));
        panel.Controls.Add(SmallButton("Free models only", () => ServeFreeModels()));
        return panel;
    }

    /// <summary>Switches serving on or off for every model the provider offers.</summary>
    private async void SetAllServed(bool serve)
    {
        if (_modelTabs.SelectedTab?.Tag is not string providerId || _config is null) return;
        var provider = _config.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;
        var models = ModelsFor(provider);
        if (models.Count == 0) return;

        var updated = models.Aggregate(provider, (p, m) => p.WithModel(m, serve));
        if (!ConfirmBulkChange(provider, updated, serve ? "Serving" : "Stopping")) return;
        AcknowledgeModels(providerId, models);

        UpdateConfig(current => current with
        {
            Providers = [.. current.Providers.Select(p => p.Id == providerId
                ? models.Aggregate(p, (latest, model) => latest.WithModel(model, serve))
                : p)]
        });
        await ApplyAsync(restart: false, refreshCatalog: false);
        if (_server.IsRunning) SetStatus($"{(serve ? "Now serving" : "Stopped serving")} every model from {provider.Name}", RelayStatusIcon.ColorRunning);
    }

    /// <summary>Serves only the models published as free, on the selected provider.</summary>
    private async void ServeFreeModels()
    {
        if (_modelTabs.SelectedTab?.Tag is not string providerId || _config is null) return;
        var provider = _config.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;
        var models = ModelsFor(provider);
        if (models.Count == 0) return;

        var updated = models.Aggregate(provider,
            (p, m) => p.WithModel(m, ModelPricingTable.For(p, m)?.IsFree == true));
        if (!ConfirmBulkChange(provider, updated, "Serving")) return;
        AcknowledgeModels(providerId, models);

        UpdateConfig(current => current with
        {
            Providers = [.. current.Providers.Select(p => p.Id == providerId
                ? models.Aggregate(p, (latest, model) => latest.WithModel(model, ModelPricingTable.For(latest, model)?.IsFree == true))
                : p)]
        });
        await ApplyAsync(restart: false, refreshCatalog: false);
        if (_server.IsRunning) SetStatus($"Serving only free models from {provider.Name}", RelayStatusIcon.ColorRunning);
    }

    /// <summary>
    /// Confirms a change that switches models off, naming how many.
    ///
    /// These buttons rewrite every model on a provider at once, and the count is easy to
    /// underestimate - "Free models only" on a provider with two free models out of thirty
    /// switches off twenty-eight. There is no undo beyond pressing a button again, so it
    /// says what it is about to do first.
    /// </summary>
    private bool ConfirmBulkChange(ProviderSettings provider, ProviderSettings updated, string verb)
    {
        var changing = ModelsFor(provider)
            .Where(m => provider.ServesModel(m) != updated.ServesModel(m))
            .ToArray();
        if (changing.Length == 0) return true;

        var turningOff = changing.Count(m => !updated.ServesModel(m) && provider.ServesModel(m));
        if (turningOff == 0) return true;   // switching models on needs no confirmation

        var preview = string.Join(", ", changing.Where(m => !updated.ServesModel(m)).Take(6));
        if (turningOff > 6) preview += ", ...";
        return MessageBox.Show(this,
            $"{verb} only part of {provider.Name}: this stops serving {turningOff} of its models.\n\n{preview}",
            "Change models", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private Control Heading(string title, string subtitle, Control? middle, Control? actions, bool actionsBelow = false)
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Palette.Rack, Padding = new Padding(0, 0, 0, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var labels = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        var head = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
            ForeColor = Palette.TextTertiary,
            BackColor = Color.Transparent
        };
        var note = new Label
        {
            Text = subtitle,
            AutoSize = true,
            ForeColor = Palette.TextMuted,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 0, 0, 0)
        };
        labels.Controls.Add(head);
        labels.Controls.Add(note);
        bar.Controls.Add(labels, 0, 0);
        if (middle is not null) bar.Controls.Add(middle, 1, 0);
        if (actions is not null && actionsBelow)
        {
            // A long button row cannot share a line with the subtitle at the minimum
            // window width; wrapping it inside an AutoSize cell clipped all but two.
            actions.Margin = new Padding(0, 8, 0, 0);
            bar.Controls.Add(actions, 0, 1);
            bar.SetColumnSpan(actions, 3);
        }
        else if (actions is not null) bar.Controls.Add(actions, 2, 0);
        return bar;
    }

    private Control RouterActions()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        panel.Controls.Add(SmallButton("+ Add", AddRouterAsync));
        panel.Controls.Add(SmallButton("Edit", () => EditRouterAsync()));
        panel.Controls.Add(SmallButton("Remove", () => RemoveRouterAsync()));
        return panel;
    }

    /// <summary>
    /// The router table. "Resolves to" is the point of the whole feature: it shows what a
    /// name means *right now*, which is what changes when prices or providers change.
    /// </summary>
    private void BuildRouterGrid()
    {
        if (_routerGrid.Columns.Count == 0)
        {
            _routerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "On", FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
            _routerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 110, SortMode = DataGridViewColumnSortMode.Automatic });
            _routerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Strategy", FillWeight = 180, SortMode = DataGridViewColumnSortMode.Automatic });
            _routerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "In rotation", FillWeight = 70, SortMode = DataGridViewColumnSortMode.Automatic });
            _routerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Would use now", FillWeight = 260, SortMode = DataGridViewColumnSortMode.Automatic });
        }

        _routerGrid.ReadOnly = true;
        _routerGrid.Rows.Clear();
        foreach (var rule in _config?.RouterRules ?? [])
        {
            var resolved = _refresh?.Router.ResolveRule(rule.Name, _routerEngine, "preview");
            var target = !rule.Enabled
                ? "switched off"
                : rule.Pool.Count == 0
                    ? "no models selected"
                    : resolved is null
                        ? "nothing in its list is available"
                        : $"{resolved.Model}  ·  {resolved.Provider.Name}";

            // Read-only: why ticked models are out of rotation right now, from the live
            // relay's own check, so the preview cannot disagree with real routing.
            var resting = (_refresh?.Router.RoutesForPicker ?? [])
                .Where(route => rule.Pool.Contains(route.Model, StringComparer.OrdinalIgnoreCase))
                .Select(route => _server.RouterEngine.Unavailable(route.Provider, route.Model))
                .OfType<string>()
                .Distinct()
                .ToArray();
            if (rule.Enabled && resting.Length > 0)
                target += $"  ·  {resting.Length} resting";
            // Today's spend against the budget, and a plain statement once it is used up.
            var overBudget = _server.Spend.OverBudget(rule) is not null;
            if (rule.DailyBudgetUsd is { } budget)
                target += overBudget
                    ? $"  ·  budget used (${_server.Spend.SpentToday(rule.Key):0.00} of ${budget:0.00}), refusing until midnight"
                    : $"  ·  ${_server.Spend.SpentToday(rule.Key):0.00} of ${budget:0.00} today";

            _routerGrid.Rows.Add(
                rule.Enabled ? "Yes" : "No",
                rule.Name,
                RouterStrategies.Describe(rule.Strategy),
                rule.Pool.Count.ToString(),
                target);
            var row = _routerGrid.Rows[^1];
            if (resting.Length > 0) row.Cells[4].ToolTipText = string.Join(Environment.NewLine, resting);
            row.Cells[4].Style.ForeColor = !rule.Enabled || rule.Pool.Count == 0 ? Palette.TextMuted
                : resolved is null || overBudget ? Palette.LinkDown
                : Palette.LinkUp;
        }
    }

    /// <summary>
    /// Replaces a rule, matched on its name. Not record equality: two rules could be
    /// identical in every field the operator can see and still be two rows, and matching
    /// on the name is the thing that is actually unique.
    /// </summary>
    internal static IReadOnlyList<RouterRule> WithRule(
        IReadOnlyList<RouterRule> rules, string originalName, RouterRule replacement) =>
        [.. rules.Select(r => r.Key.Equals(originalName, StringComparison.OrdinalIgnoreCase) ? replacement : r)];

    /// <summary>Removes exactly one rule, matched on its name.</summary>
    internal static IReadOnlyList<RouterRule> WithoutRule(IReadOnlyList<RouterRule> rules, string name) =>
        [.. rules.Where(r => !r.Key.Equals(name, StringComparison.OrdinalIgnoreCase))];

    /// <summary>True when the name is already taken by a different rule.</summary>
    internal static bool RuleNameTaken(IReadOnlyList<RouterRule> rules, string name, string? exceptName = null) =>
        rules.Any(r => r.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                       (exceptName is null || !r.Key.Equals(exceptName, StringComparison.OrdinalIgnoreCase)));

    private async void AddRouterAsync()
    {
        var existing = _config?.RouterRules ?? [];
        using var dialog = new RouterEditorForm(null, PoolEntries());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null || _config is null) return;

        if (RuleNameTaken(existing, dialog.Result.Name))
        {
            MessageBox.Show(this, $"A router called '{dialog.Result.Name}' already exists.", "Add router",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UpdateConfig(current => current with { RouterRules = [.. (current.RouterRules ?? []), dialog.Result] });
        await ApplyAsync(restart: false, refreshCatalog: false);
        SetStatus($"Router '{dialog.Result.Name}' added", RelayStatusIcon.ColorRunning);
    }

    private async void EditRouterAsync()
    {
        if (_config is null || !SelectedRouter(out var existing)) return;

        using var dialog = new RouterEditorForm(existing, PoolEntries());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        if (RuleNameTaken(_config.RouterRules ?? [], dialog.Result.Name, exceptName: existing.Name))
        {
            MessageBox.Show(this, $"A router called '{dialog.Result.Name}' already exists.", "Edit router",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UpdateConfig(current => current with
        {
            RouterRules = WithRule(current.RouterRules ?? [], existing.Name, dialog.Result)
        });
        await ApplyAsync(restart: false, refreshCatalog: false);
        SetStatus($"Router '{dialog.Result.Name}' updated", RelayStatusIcon.ColorRunning);
    }

    /// <summary>
    /// Every served model, once, with the provider that would answer for it and its price.
    /// A model offered by two providers appears once, because the pool is a list of model
    /// ids. Round robin can use every provider serving a selected id; other strategies use
    /// the highest-priority provider, as a plain model request does.
    /// </summary>
    private IReadOnlyList<RouterEditorForm.RouterPoolEntry> PoolEntries()
    {
        var entries = new List<RouterEditorForm.RouterPoolEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, provider) in _refresh?.Router.RoutesForPicker ?? [])
        {
            if (!seen.Add(model)) continue;
            var price = ModelPricingTable.For(provider, model);
            var label = price is null ? "no price"
                : price.IsFree ? "free"
                : $"${price.InputPerMillion:0.####} in / ${price.OutputPerMillion:0.####} out";
            var matchingProviders = (_refresh?.Router.RoutesForPicker ?? [])
                .Where(candidate => candidate.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Provider.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entries.Add(new RouterEditorForm.RouterPoolEntry(model, provider.Name, label,
                matchingProviders.Length > 1
                    ? $"{model} is served by {string.Join(", ", matchingProviders)}; round robin rotates across them"
                    : $"{model} on {provider.Name}"));
        }
        return entries;
    }

    private async void RemoveRouterAsync()
    {
        if (_config is null || !SelectedRouter(out var existing)) return;
        if (MessageBox.Show(this, $"Remove the router '{existing.Name}'?\n\nAny agent asking for it will get a 400.",
                "Remove router", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        UpdateConfig(current => current with { RouterRules = WithoutRule(current.RouterRules ?? [], existing.Name) });
        await ApplyAsync(restart: false, refreshCatalog: false);
        SetStatus($"Router '{existing.Name}' removed", RelayStatusIcon.ColorIdle);
    }

    private bool SelectedRouter(out RouterRule rule)
    {
        rule = null!;
        if (_routerGrid.CurrentRow?.Cells[1].Value is not string name) return false;
        var found = (_config?.RouterRules ?? []).FirstOrDefault(r => r.Name == name);
        if (found is null) return false;
        rule = found;
        return true;
    }

    private Control ProviderActions()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        // Per provider: add, edit and remove only. Models and prices for every provider
        // come from the one Refresh all button at the top, which also runs on launch.
        panel.Controls.Add(SmallButton("+ Add", AddProviderAsync));
        panel.Controls.Add(SmallButton("Edit", () => EditProviderAsync()));
        panel.Controls.Add(SmallButton("Remove", () => RemoveProviderAsync()));
        return panel;
    }

    /// <summary>
    /// The one refresh: published prices, then every enabled provider's model list -
    /// account catalogs (ChatGPT, Gemini OAuth, Claude Code, Antigravity) as well as the
    /// API-key probes - then the status and ledger. Runs on launch and on Refresh all.
    ///
    /// Prices need a live source because the built-in table is a snapshot and snapshots
    /// drift: it had deepseek-v4-pro at 1.65/3.96 on Go against a published 0.66/1.98,
    /// and grok-4.7 at 5/30 against 2/6. A price printed beside a spend figure has to be
    /// current or it is worse than showing nothing.
    ///
    /// No sign-in window is ever opened from here: a signed-out account is reported in
    /// the status, and Edit on that provider signs it in again.
    /// </summary>
    private async Task RefreshAllAsync()
    {
        if (_config is null) return;
        SetStatus("Refreshing models and prices...", RelayStatusIcon.ColorWarning);
        // A refresh is the operator saying "try everything again", including providers
        // that refused a key or sign-in earlier.
        _server.RouterEngine.ClearUnusable();
        _historySummary = await Task.Run(() =>
        {
            _history.Prune();
            return _history.Summaries();
        });

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var pricing = await LivePricing.RefreshAsync(RelayPaths.File("pricing.json"), client);

        var failed = new List<string>();
        foreach (var provider in _config.Providers.Where(p => p.Enabled && p.AuthMode != ProviderAuthMode.ApiKey).ToArray())
        {
            try
            {
                if (await FetchAccountModelsAsync(provider) is { Count: > 0 } models)
                    SaveImportedModels(provider.Id, models);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(provider.Name);
            }
        }
        _config = _settingsStore.Load();
        if (_config is null) return;

        // Force the probe too: the catalog has a 10-minute TTL, and a refresh means the
        // model lists, not only the prices.
        _refresh = await CatalogRefreshRunner.RunAsync(_config, _catalogCache, force: true, client);
        var change = TrackNewModels();
        _lastModelChange = change;
        RebindRefreshToLatestConfig();
        if (_config is not null && _refresh is not null)
            _server.Apply(_config.LocalApiKey, _refresh.Router, _settingsStore.FindProvider);
        var refreshedModelCount = _refresh?.Router.ModelCount ?? 0;

        Render();

        var message = pricing is null
            ? $"Could not reach {LivePricing.Host}. Prices left as they were: {LivePricing.Current?.ModelCount ?? 0} models."
            : $"Prices updated: {pricing.ModelCount} models from {pricing.ProviderCount} published providers"
              + (pricing.GoRequestAllowanceCount > 0
                    ? $", {pricing.GoRequestAllowanceCount} Go request allowances"
                    : string.Empty)
              + ".";
        message += $" {refreshedModelCount} models served.";
        if (change is { AddedCount: > 0 })
        {
            var names = change.Added.Select(e => $"{_config?.Providers.FirstOrDefault(p => p.Id == e.Key)?.Name ?? e.Key} {e.Value.Count}");
            message += $" {change.AddedCount} new model{(change.AddedCount == 1 ? "" : "s")} ({string.Join(", ", names)}), switched off until you turn them on.";
        }
        else message += " No new models.";
        if (failed.Count > 0)
            message += $" Could not list models for {string.Join(", ", failed)}: select it and press Edit to sign in again.";
        SetStatus(message, pricing is null || failed.Count > 0 ? RelayStatusIcon.ColorWarning : RelayStatusIcon.ColorRunning);
    }

    /// <summary>
    /// Creates providers from endpoints these tools are already pointed at. Reads their
    /// config files only - never writes to them, never contacts a network.
    /// </summary>
    private async Task DiscoverAsync()
    {
        var result = ClientDiscovery.Scan();
        if (!result.AnythingFound)
        {
            // Finding nothing is the normal case, not an error. A modal dialog here read
            // as a failure, so it becomes a status line and nothing else.
            SetStatus("Nothing to import - add a provider with + Add", RelayStatusIcon.ColorIdle);
            return;
        }

        var added = new List<string>();
        var skipped = new List<string>();
        UpdateConfig(current =>
        {
            var existing = current.Providers.Select(p => p.BaseUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var providers = current.Providers.ToList();
            foreach (var endpoint in result.Found)
            {
                if (!existing.Add(endpoint.BaseUrl)) { skipped.Add(endpoint.Name); continue; }
                providers.Add(new ProviderSettings(
                    Guid.NewGuid().ToString("N")[..8], endpoint.Name, endpoint.Kind, endpoint.BaseUrl,
                    endpoint.ApiKey, true, providers.Count));
                added.Add(endpoint.Name);
            }
            return current with { Providers = providers };
        });

        if (added.Count == 0)
        {
            // Also not an error - everything found is already there.
            SetStatus($"{skipped.Count} endpoint(s) already configured", RelayStatusIcon.ColorIdle);
            return;
        }

        await ApplyAsync(restart: false);

        var message = $"Imported from your existing configuration:\n  {string.Join("\n  ", added)}";
        if (skipped.Count > 0) message += $"\n\nAlready configured, skipped:\n  {string.Join("\n  ", skipped)}";
        message += "\n\nKeys were copied into this app's encrypted store.";
        MessageBox.Show(this, message, "Discover endpoints", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------- data

    private static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Top,
            BackgroundColor = Palette.Panel,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            GridColor = Palette.Hairline,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = true,
            BackColor = Palette.Panel,
            ForeColor = Palette.TextPrimary
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Palette.Bezel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextTertiary;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Palette.Bezel;
        grid.ColumnHeadersHeight = 26;
        grid.DefaultCellStyle.BackColor = Palette.Panel;
        grid.DefaultCellStyle.ForeColor = Palette.TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Palette.Hairline;
        grid.DefaultCellStyle.SelectionForeColor = Palette.TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);
        return grid;
    }

    private void BuildProviderGrid()
    {
        var selectedProviderId = _providerGrid.CurrentRow?.Tag as string;
        _providerGrid.Columns.Clear();
        _providerGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", FillWeight = 40 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 150 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", FillWeight = 100 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Models", FillWeight = 70 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 200 });
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Base URL", FillWeight = 260 });
        // Models this provider started offering that are still waiting for a decision.
        _providerGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "New", FillWeight = 70,
            DefaultCellStyle = { ForeColor = Palette.LinkPending, Font = new Font("Segoe UI", 8.25F, FontStyle.Bold) }
        });

        var enabled = _config?.EnabledProviders.ToDictionary(p => p.Id) ?? [];
        _providerGrid.Rows.Clear();
        if (_config is null) return;

        foreach (var provider in _config.Providers.OrderBy(p => p.Priority).ThenBy(p => p.Name))
        {
            var probe = _refresh?.Probes.FirstOrDefault(p => p.Provider.Id == provider.Id);
            var models = ModelsFor(provider).Count;
            var account = provider.RequiresExactModelId;
            var status = !provider.Enabled ? "disabled"
                : account ? (models > 0 ? $"account · {models} models" : "account · press Edit to sign in")
                : probe is null ? $"{(models > 0 ? $"{models} models" : "not probed")}"
                : probe.Healthy ? $"ok \u00b7 {models} models \u00b7 {probe.ElapsedMs} ms"
                : probe.Error ?? "failed";

            var target = provider.AuthMode == ProviderAuthMode.CliAccount
                ? $"{provider.CliExecutable ?? provider.Kind} CLI sign-in"
                : provider.BaseUrl;
            var listed = ModelsFor(provider);
            var fresh = _known.NewFor(provider.Id).Count(m => listed.Contains(m, StringComparer.OrdinalIgnoreCase));
            var rowIndex = _providerGrid.Rows.Add(provider.Enabled, provider.Name, provider.Kind, models, status, target,
                fresh > 0 ? $"{fresh} new" : string.Empty);
            var row = _providerGrid.Rows[rowIndex];
            row.Tag = provider.Id;
            row.Cells[4].Style.ForeColor = !provider.Enabled ? Palette.TextMuted
                : account ? (models > 0 ? Palette.LinkUp : Palette.LinkPending)
                : probe is null ? Palette.TextTertiary
                : probe.Healthy ? Palette.LinkUp
                : Palette.LinkDown;
            if (models > 0) row.Cells[3].Style.ForeColor = Palette.LinkUp;
        }

        if (selectedProviderId is not null)
        {
            var selectedRow = _providerGrid.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(row => row.Tag as string == selectedProviderId);
            if (selectedRow is not null)
            {
                selectedRow.Selected = true;
                _providerGrid.CurrentCell = selectedRow.Cells[1];
            }
        }
    }

    /// <summary>
    /// One tab per provider, each a sortable grid of the catalogue: what the provider
    /// offers and what that provider's plan charges for it. What you actually spent is
    /// on the Session tab instead.
    /// </summary>
    private void BuildModelTabs()
    {
        var pages = _modelTabs.TabPages;
        // Rebuilding after a checkbox click must not throw the user back to the first tab.
        var selected = _modelTabs.SelectedTab?.Tag as string;
        // Nor may it throw away how the reader had sorted and scrolled each table: sorting
        // by price and then picking models from the sorted list is the whole workflow.
        var view = CaptureGridView();
        foreach (var p in pages.Cast<TabPage>().ToArray()) p.Dispose();
        pages.Clear();
        if (_config is null) return;
        foreach (var provider in _config.Providers.OrderBy(p => p.Priority))
        {
            var models = ModelsFor(provider);
            if (models.Count == 0) continue;
            pages.Add(BuildModelPage(provider, models, view.GetValueOrDefault(provider.Id)));
        }

        if (pages.Count == 0) return;
        _modelTabs.SelectedIndex = selected is null ? 0
            : Math.Max(0, IndexOfPage(pages, selected));
    }

    /// <summary>How each provider's table was sorted and scrolled, keyed by provider id.</summary>
    private Dictionary<string, (int Column, SortOrder Order, int FirstRow)> CaptureGridView()
    {
        var captured = new Dictionary<string, (int, SortOrder, int)>(StringComparer.Ordinal);
        foreach (TabPage page in _modelTabs.TabPages)
        {
            if (page.Tag as string is not { } providerId) continue;
            if (FindModelGrid(page) is not { } grid) continue;

            var column = grid.SortedColumn?.Index ?? -1;
            var order = grid.SortOrder;
            var firstRow = grid.Rows.Count > 0 ? Math.Max(0, grid.FirstDisplayedScrollingRowIndex) : 0;
            captured[providerId] = (column, order, firstRow);
        }
        return captured;
    }

    private static DataGridView? FindModelGrid(Control parent) =>
        parent.Controls.OfType<DataGridView>().FirstOrDefault()
        ?? parent.Controls.OfType<Control>().SelectMany(c => c.Controls.OfType<DataGridView>()).FirstOrDefault();

    /// <summary>Re-applies a remembered sort and scroll position once the rows are in.</summary>
    private static void RestoreGridView(DataGridView grid, (int Column, SortOrder Order, int FirstRow)? view)
    {
        if (view is not { } state) return;
        try
        {
            if (state.Column >= 0 && state.Column < grid.Columns.Count && state.Order != SortOrder.None)
                grid.Sort(grid.Columns[state.Column], state.Order == SortOrder.Descending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending);
            if (state.FirstRow > 0 && state.FirstRow < grid.Rows.Count)
                grid.FirstDisplayedScrollingRowIndex = state.FirstRow;
        }
        catch (InvalidOperationException)
        {
            // A sort that can no longer be applied is not worth failing a refresh over.
        }
    }

    private static int IndexOfPage(TabControl.TabPageCollection pages, string providerId)
    {
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Tag as string == providerId) return i;
        return 0;
    }

    /// <summary>One provider's tab: the catalogue, with a checkbox per model and its prices.</summary>
    private TabPage BuildModelPage(ProviderSettings provider, IReadOnlyList<string> models,
        (int Column, SortOrder Order, int FirstRow)? view = null)
    {
        var page = new TabPage(ModelTabLabel(provider, models))
        {
            BackColor = Palette.Panel,
            Padding = new Padding(4),
            Tag = provider.Id
        };
        // Added to the page before it is populated, so the page owns it for its whole life.
        var grid = SortableGrid();
        AddModelColumns(grid);
        page.Controls.Add(grid);
        // Fill, so the table uses the whole tab and scrolls itself. Docked to the top it
        // kept the DataGridView default height and clipped everything past the first rows.
        grid.Dock = DockStyle.Fill;

        PopulateModelRows(provider, models, grid, _known.NewFor(provider.Id));
        RestoreGridView(grid, view);
        return page;
    }

    private static void PopulateModelRows(ProviderSettings provider, IReadOnlyList<string> models, DataGridView grid,
        IReadOnlyList<string> newModels)
    {
        foreach (var model in models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            var price = ModelPricingTable.For(provider, model);
            var isServed = provider.ServesModel(model);
            var allowance = ModelPricingTable.FiveHourRequests(provider, model);

            // Null is how a DataGridView cell is left empty, and the Add overload
            // documents it - but its parameter is declared non-nullable, so the array is
            // built as object? and asserted. Do not "fix" this to DBNull: a DBNull in a
            // decimal column throws "Object must be of type Decimal" on sort.
            object?[] cells =
            [
                isServed, model,
                Cell(price?.InputPerMillion), Cell(price?.OutputPerMillion),
                Cell(price?.CachedReadPerMillion), Cell(price?.CachedWritePerMillion),
                allowance is null ? null
                    : allowance.Unlimited ? UnlimitedRequests
                    : allowance.Requests,
                newModels.Contains(model, StringComparer.OrdinalIgnoreCase) ? NewModelMark : null
            ];
            grid.Rows.Add(cells!);
            var index = grid.Rows.Count - 1;
            grid.Rows[index].Tag = (provider.Id, model, isServed);

            grid.Rows[index].Cells[1].Style.ForeColor = isServed ? Palette.TextPrimary : Palette.TextMuted;
            // An unpublished price is blank, never zero - zero would be a claim.
            for (var column = 2; column <= 6; column++)
                if (grid.Rows[index].Cells[column].Value is null)
                    grid.Rows[index].Cells[column].Style.ForeColor = Palette.TextMuted;
        }
    }

    /// <summary>
    /// Blank cells are <c>null</c>, never <c>DBNull.Value</c>. DataGridView sorts a
    /// mixed column with <see cref="Comparer{T}"/> over boxed values, which calls
    /// <c>Decimal.CompareTo(object)</c> - and that throws "Object must be of type
    /// Decimal" on DBNull. The comparer short-circuits null, so null sorts and
    /// displays blank. Swapping it back breaks every sortable price column.
    /// </summary>
    internal static object? Cell(decimal? value) => value;

    /// <summary>Text in the model grid's New column for a model first seen since the operator last decided on it.</summary>
    internal const string NewModelMark = "NEW";
    private const int NewModelColumn = 7;
    internal static object Cell(long value) => value;

    /// <summary>
    /// Stands in for a published "Unlimited" so the requests column stays one type and
    /// still sorts, sorted above every real count. <see cref="OnModelCellFormatting"/>
    /// draws it as text; it must never be written as a string into the same column.
    /// </summary>
    internal const long UnlimitedRequests = long.MaxValue;

    private static void OnModelCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.Value is long value && value == UnlimitedRequests)
        {
            e.Value = "unlimited";
            e.FormattingApplied = true;
        }
    }

    private static void AddModelColumns(DataGridView grid)
    {
        // Two states, not three. A single model is either served or not; the mixed case
        // is a property of the provider, and the tab label already carries that count.
        // A three-state cell would make every click cycle through Indeterminate, which
        // the handler cannot read as a decision.
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Serve",
            FillWeight = 50,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model", FillWeight = 210, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "In $/1M", FillWeight = 70, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Format = "$0.####", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Out $/1M", FillWeight = 78, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Format = "$0.####", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Cache in $/1M", FillWeight = 88, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Format = "$0.####", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Cache wr $/1M", FillWeight = 88, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Format = "$0.####", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "5h requests", FillWeight = 92, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Format = "n0", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        // Last, so no existing column index moves. "NEW" until the model is switched on or
        // off; sorting on it brings the new ones to the top.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "New", FillWeight = 50, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { ForeColor = Palette.LinkPending, Font = new Font("Segoe UI", 8.25F, FontStyle.Bold) }
        });
        // A published "unlimited" is drawn from the sentinel rather than stored as text.
        // A column holding both a number and a string throws in the sort comparer, the same
        // way a DBNull in a decimal column did.
        grid.CellFormatting += OnModelCellFormatting;
        // Measured usage and spend are deliberately not here. This grid is the catalogue:
        // what is available and what it costs. What you actually used is a different
        // question, answered on the Session tab, and mixing the two made a wide table
        // that was hard to read and easy to misread.
    }

    /// <summary>
    /// Used vs. allowed for every served model with a published request allowance (OpenCode
    /// Go's 5-hour table), most used first, so it is visible which models are close to
    /// stepping aside before a request is refused.
    /// </summary>
    private void BuildAllowanceGrid()
    {
        if (_allowanceGrid.Columns.Count == 0)
        {
            _allowanceGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provider", FillWeight = 120 });
            _allowanceGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model", FillWeight = 200 });
            foreach (var header in new[] { "Used", "Allowed" })
                _allowanceGrid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = header, FillWeight = 80,
                    DefaultCellStyle = { Format = "n0", Alignment = DataGridViewContentAlignment.MiddleRight }
                });
            _allowanceGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Left", FillWeight = 70,
                DefaultCellStyle = { Format = "0%", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
        }

        _allowanceGrid.Rows.Clear();
        if (_config is null) return;
        var engine = _server.RouterEngine;
        var rows = new List<(string Provider, string Model, int Used, long Allowed, double Left)>();
        foreach (var provider in _config.EnabledProviders)
            foreach (var model in ModelsFor(provider).Where(provider.ServesModel))
            {
                // The engine's own allowance, so this shows exactly what routing enforces.
                if (engine.AllowanceFor(provider, model) is not { } allowed || allowed <= 0)
                    continue;
                var used = engine.RequestsInWindow(provider, model, engine.Now);
                rows.Add((provider.Name, model, used, allowed, engine.Headroom(provider, model)));
            }

        foreach (var row in rows.OrderBy(r => r.Left).ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
        {
            _allowanceGrid.Rows.Add(row.Provider, row.Model, row.Used, row.Allowed, row.Left);
            _allowanceGrid.Rows[^1].Cells[4].Style.ForeColor =
                row.Left <= (double)RouterEngine.AllowanceReserve ? Palette.LinkDown
                : row.Left < 0.5 ? Palette.LinkPending
                : Palette.LinkUp;
        }
    }

    /// <summary>
    /// Per-model usage for the Session tab: what each model actually cost you, which is
    /// the counterpart to the published prices on the Models tab.
    /// </summary>
    private void BuildUsageGrid()
    {
        if (_usageGrid.Columns.Count == 0)
        {
            _usageGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model", FillWeight = 220, SortMode = DataGridViewColumnSortMode.Automatic });
            _usageGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Requests", FillWeight = 84, SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Format = "n0", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            foreach (var (header, weight) in new[] { ("In", 96), ("Out", 96), ("Cache read", 96) })
                _usageGrid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic,
                    DefaultCellStyle = { Format = "n0", Alignment = DataGridViewContentAlignment.MiddleRight }
                });
            _usageGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Cache hit", FillWeight = 72, SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Format = "0%", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            // Plan-account traffic (subscriptions) at API rates: what it would have cost,
            // kept apart from Cost so a flat subscription never reads as spend.
            _usageGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "API equiv.", FillWeight = 90, SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Format = "$0.0000", Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = Palette.TextTertiary }
            });
            _usageGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Cost", FillWeight = 96, SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = { Format = "$0.0000", Alignment = DataGridViewContentAlignment.MiddleRight, Font = new Font("Consolas", 9.25F) }
            });
        }

        _usageGrid.Rows.Clear();
        foreach (var group in _telemetry.GetReport().Models.OrderByDescending(m => m.ProviderCostUsd + m.EstimatedCostUsd))
        {
            var cost = group.ProviderCostUsd + group.EstimatedCostUsd;
            // A boxed double keeps the column sortable; DBNull shows blank before any prompt.
            _usageGrid.Rows.Add(group.Key, group.Requests, group.InputTokens, group.OutputTokens, group.CacheReadInputTokens,
                group.CacheHitRate is { } hit ? hit : DBNull.Value, group.ApiEquivalentUsd, cost);
            var row = _usageGrid.Rows[^1];
            row.Cells[7].Style.ForeColor = cost > 0 ? Palette.LinkUp : Palette.TextMuted;
        }
    }

    private DataGridView SortableGrid()
    {
        var grid = Grid();
        grid.ReadOnly = false;              // the Serve checkbox has to be clickable
        grid.AllowUserToOrderColumns = true;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.EnableHeadersVisualStyles = false;
        // A checkbox in a DataGridView does not commit on click; it commits when the
        // cell loses focus. Without this the value only lands after the click elsewhere.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += OnServeCellChanged;
        return grid;
    }

    /// <summary>
    /// Applies a Serve checkbox. The decision is compared against <see cref="_config"/>,
    /// never against a flag set around the rebuild, because DataGridView can deliver
    /// this event after that scope has closed - which previously wrote a switch-off for
    /// every model on every launch.
    /// </summary>
    private void OnServeCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0 || _config is null) return;
        if (sender is not DataGridView grid) return;
        if (grid.Rows[e.RowIndex].Tag is not ValueTuple<string, string, bool> tag) return;
        if (grid.Rows[e.RowIndex].Cells[0].Value is not bool serve) return;

        var (providerId, model, _) = tag;
        var latestProvider = _settingsStore.FindProvider(providerId);
        if (latestProvider is null) return;

        // The persisted config is the truth. A rebuild, sort, second event, or an
        // independent token update must not make this callback write a stale snapshot.
        var latest = UpdateConfig(current =>
        {
            var provider = current.Providers.FirstOrDefault(p => p.Id == providerId);
            if (provider is null || provider.ServesModel(model) == serve) return current;
            var updated = provider.WithModel(model, serve);
            return current with { Providers = [.. current.Providers.Select(p => p.Id == providerId ? updated : p)] };
        });
        var providerAfterUpdate = latest.Providers.FirstOrDefault(p => p.Id == providerId);
        if (providerAfterUpdate is null || providerAfterUpdate.ServesModel(model) != serve) return;

        // Updated in place, deliberately. Rebuilding the table here is what reset the sort
        // order and the scroll position on every tick, which made sorting the list and then
        // picking models from it impossible. Only this row changed, so only it is redrawn.
        grid.Rows[e.RowIndex].Tag = (providerId, model, serve);
        grid.Rows[e.RowIndex].Cells[1].Style.ForeColor = serve ? Palette.TextPrimary : Palette.TextMuted;
        // Switching a model on or off is the decision the new flag was asking for.
        AcknowledgeModels(providerId, [model]);
        grid.Rows[e.RowIndex].Cells[NewModelColumn].Value = null;
        UpdateModelTabLabel(providerId);
        BuildProviderGrid();

        // The server still needs the new config, so the router stops serving the model.
        _ = RunGuardedAsync(() => ApplyAsync(restart: false, refreshCatalog: false, rebuildModelGrid: false));
    }

    /// <summary>Refreshes one model tab's "served/total" without touching the grid inside it.</summary>
    private void UpdateModelTabLabel(string providerId)
    {
        var page = _modelTabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Tag as string == providerId);
        var provider = _config?.Providers.FirstOrDefault(p => p.Id == providerId);
        if (page is null || provider is null) return;

        page.Text = ModelTabLabel(provider, ModelsFor(provider));
    }

    /// <summary>"Name  (served/total)", plus how many are new when any are.</summary>
    private string ModelTabLabel(ProviderSettings provider, IReadOnlyList<string> models)
    {
        var label = $"{provider.Name}  ({models.Count(provider.ServesModel)}/{models.Count})";
        var fresh = _known.NewFor(provider.Id).Count(m => models.Contains(m, StringComparer.OrdinalIgnoreCase));
        return fresh > 0 ? $"{label} · {fresh} new" : label;
    }

    private IReadOnlyList<string> ModelsFor(ProviderSettings provider)
    {
        if (provider.RequiresExactModelId) return provider.ImportedModels ?? [];
        return _refresh?.Snapshot.ModelsByProviderId.TryGetValue(provider.Id, out var models) == true ? models : [];
    }

    /// <summary>Persists a targeted edit against the latest config, not a UI snapshot.</summary>
    private RelayConfig UpdateConfig(Func<RelayConfig, RelayConfig> update)
    {
        _config = _settingsStore.Update(current => update(current ?? _config ??
            new RelayConfig(RelayConfig.CurrentSchemaVersion, RelayProtocol.CreateLocalKey(), [])));
        return _config;
    }

    /// <summary>
    /// Catalog refresh is asynchronous; rebuild its routing view using the latest saved
    /// profiles after it completes. The request-time resolver separately supplies fresh
    /// credentials without requiring another catalog refresh.
    /// </summary>
    /// <summary>
    /// Compares every enabled provider's current models with the ones remembered from
    /// earlier refreshes and launches. New ones are flagged and switched off before the
    /// router is rebuilt, so they never serve a request until the operator turns them on.
    /// </summary>
    private ModelChangeTracker.Result? TrackNewModels()
    {
        var config = _settingsStore.Load();
        if (config is null || _refresh is null) return null;
        var current = config.Providers.Where(p => p.Enabled)
            .ToDictionary(p => p.Id, ModelsFor, StringComparer.Ordinal);
        var result = ModelChangeTracker.Track(
            ModelChangeTracker.Retain(_known, config.Providers.Select(p => p.Id)), current, DateTimeOffset.UtcNow);
        if (result.AddedCount > 0)
        {
            UpdateConfig(latest => latest with
            {
                Providers = [.. latest.Providers.Select(p => result.Added.TryGetValue(p.Id, out var added)
                    ? added.Aggregate(p, (profile, model) => profile.WithModel(model, serve: false))
                    : p)]
            });
        }
        if (!ReferenceEquals(result.Known, _known))
        {
            _known = result.Known;
            _knownStore.Save(_known);
        }
        if (result.AddedCount > 0)
        {
            var names = result.Added.Select(e => $"{config.Providers.FirstOrDefault(p => p.Id == e.Key)?.Name ?? e.Key} {e.Value.Count}");
            // Keyed on the models themselves, so a later refresh that finds more shows again.
            var key = "new|" + string.Join(",", result.Added.SelectMany(e => e.Value.Select(m => e.Key + "/" + m)).Order(StringComparer.OrdinalIgnoreCase));
            ShowNotice(new RelayNotice(key, $"{result.AddedCount} new model{(result.AddedCount == 1 ? "" : "s")}",
                $"{string.Join(", ", names)}. Switched off until you turn them on in the Models tab."));
        }
        return result;
    }

    /// <summary>Today, the last 7 days and the last 30 days, from the per-day usage files.</summary>
    private void BuildHistoryGrid()
    {
        if (_historyGrid.Columns.Count == 0)
        {
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Period", FillWeight = 110 });
            foreach (var header in new[] { "Requests", "Failed", "In", "Out", "Cache read" })
                _historyGrid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = header, FillWeight = 80,
                    DefaultCellStyle = { Format = "n0", Alignment = DataGridViewContentAlignment.MiddleRight }
                });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Cache hit", FillWeight = 70,
                DefaultCellStyle = { Format = "0%", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            foreach (var header in new[] { "API equiv.", "Cost" })
                _historyGrid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = header, FillWeight = 90,
                    DefaultCellStyle = { Format = "$0.0000", Alignment = DataGridViewContentAlignment.MiddleRight }
                });
        }

        _historyGrid.Rows.Clear();
        foreach (var period in _historySummary)
            _historyGrid.Rows.Add(period.Label, period.Requests, period.Failed, period.InputTokens, period.OutputTokens,
                period.CacheReadTokens, period.CacheHitRate is { } hit ? hit : DBNull.Value, period.ApiEquivalentUsd, period.CostUsd);
    }

    /// <summary>Opens the diagnostic log in the default text editor, or says there is nothing yet.</summary>
    private void OpenDiagnosticLog()
    {
        if (!File.Exists(_diagnostics.FilePath))
        {
            SetStatus("No failed or retried requests logged yet.", RelayStatusIcon.ColorRunning);
            return;
        }
        Process.Start(new ProcessStartInfo(_diagnostics.FilePath) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>How often Refresh all runs on its own while the relay is up.</summary>
    internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// "  ·  prices as of 14:05" today, "... as of 2 Oct 14:05" on an earlier day, nothing
    /// when prices were never fetched. A price is only worth showing with its age.
    /// </summary>
    internal static string PricesAsOf(DateTimeOffset? fetchedAt, DateTimeOffset now)
    {
        if (fetchedAt is not { } at) return string.Empty;
        var local = at.ToLocalTime();
        var stamp = local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        return $"  ·  prices as of {stamp}";
    }

    /// <summary>
    /// A Windows notification from the tray, at most once per event per day. The relay
    /// runs hidden all day, so this is how something that needs the operator gets seen
    /// before it shows up as a failed turn.
    /// </summary>
    private void ShowNotice(RelayNotice notice)
    {
        if (!_notices.ShouldShow(notice.Key)) return;
        _tray.ShowBalloonTip(10_000, notice.Title, notice.Message, ToolTipIcon.Warning);
    }

    /// <summary>Clears the new flag on models the operator just switched on or off.</summary>
    private void AcknowledgeModels(string providerId, IEnumerable<string> models)
    {
        var updated = ModelChangeTracker.Acknowledge(_known, providerId, models);
        if (ReferenceEquals(updated, _known)) return;
        _known = updated;
        _knownStore.Save(_known);
    }

    private void RebindRefreshToLatestConfig()
    {
        if (_refresh is null) return;
        var latest = _settingsStore.Load();
        if (latest is null)
        {
            _config = null;
            return;
        }
        _config = latest;
        var unhealthy = _refresh.Probes.Where(probe => !probe.Healthy).Select(probe => probe.Provider.Id);
        var router = new ProviderRouter(latest.EnabledProviders, _refresh.Snapshot, latest.RouterRules, unhealthy);
        _refresh = _refresh with { Router = router };
    }


    private void BuildLedger()
    {
        if (_ledger.Columns.Count == 0)
        {
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Requests" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Failed" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Token in" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Token out" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cache read" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cost" });
            _ledger.Columns[5].DefaultCellStyle.ForeColor = Palette.LinkUp;
            _ledger.Columns[5].DefaultCellStyle.Font = new Font("Consolas", 10F, FontStyle.Bold);
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cache hit" });
            _ledger.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Plan API equiv." });
        }

        var report = _telemetry.GetReport();
        _ledger.Rows.Clear();
        _ledger.Rows.Add(
            report.RequestCount.ToString("n0"),
            report.FailedRequestCount.ToString("n0"),
            report.InputTokens.ToString("n0"),
            report.OutputTokens.ToString("n0"),
            report.CacheReadInputTokens.ToString("n0"),
            $"${report.TotalConsumedCostUsd:0.000000}",
            report.CacheHitRate is { } hit ? hit.ToString("0%", System.Globalization.CultureInfo.CurrentCulture) : "—",
            $"${report.ApiEquivalentUsd:0.0000}");
    }

    // ---------------------------------------------------------------- actions

    private async Task StartupAsync()
    {
        // Prices first, before any network call: the cached projection is what lets the
        // grid show a real price on an offline start instead of a column of blanks.
        LivePricing.LoadCached(RelayPaths.File("pricing.json"));
        _known = _knownStore.Load();

        _config = _settingsStore.Load();

        // Show warning if settings could not be read but were preserved
        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadError))
        {
            MessageBox.Show(
                _settingsStore.LastLoadError + "\n\nA fresh configuration has been created. Your previous settings are preserved.",
                "Settings Load Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // The local key is half of the endpoint contract, and a client cannot be
        // configured without it. Mint it on first run so the ENDPOINT block is never
        // an empty field waiting on a provider, and so the key is stable from the
        // first launch rather than changing when a provider is finally added.
        _config = UpdateConfig(current =>
        {
            if (string.IsNullOrWhiteSpace(current.LocalApiKey))
                return current with { LocalApiKey = RelayProtocol.CreateLocalKey() };
            return current;
        });

        if (_config.Providers.Count == 0)
        {
            SetStatus("No provider yet - use + Add below, or Discover in the tray menu", RelayStatusIcon.ColorIdle);
            Render();
            return;
        }

        SetStatus("Starting relay...", RelayStatusIcon.ColorWarning);
        // Start serving from the cached catalog straight away; the refresh below then
        // brings every provider's models and the published prices up to date.
        await ApplyAsync(restart: true, refreshCatalog: false);
        if (_server.IsRunning) Hide();
        await RunGuardedAsync(RefreshAllAsync);
    }

    private async Task ApplyAsync(bool restart, bool refreshCatalog = true, bool rebuildModelGrid = true)
    {
        if (_config is null) return;
        if (restart) _userStopped = false;
        var startingConfig = _config;
        // Toggling one model must not re-probe every provider; the catalog is unchanged,
        // only which of its models are served.
        _refresh = await CatalogRefreshRunner.RunAsync(startingConfig, _catalogCache, force: refreshCatalog);
        // Every catalog rebuild is checked, so no path can serve a new model unflagged.
        if (TrackNewModels() is { AddedCount: > 0 } change) _lastModelChange = change;
        RebindRefreshToLatestConfig();
        if (_config is null || _refresh is null) return;

        _server.Apply(_config.LocalApiKey, _refresh.Router, _settingsStore.FindProvider);

        if (_refresh.Router.Providers.Count == 0)
        {
            SetStatus("No enabled provider - add one below", RelayStatusIcon.ColorWarning);
            Render(rebuildModelGrid);
            return;
        }

        if (!_userStopped && (restart || !_server.IsRunning))
        {
            try { await _server.StartAsync(); }
            catch (Exception ex)
            {
                SetStatus($"Could not start relay: {ex.Message}", RelayStatusIcon.ColorError);
                Render(rebuildModelGrid);
                return;
            }
        }

        var failed = _refresh.FailedProviders;
        var serving = _refresh.Router.ServingProviderCount;
        SetStatus(
            $"Running - {serving} of {_refresh.Router.Providers.Count} providers responding, {_refresh.Router.ModelCount} models" +
            (failed > 0 ? $", {failed} not responding" : string.Empty),
            failed > 0 ? RelayStatusIcon.ColorWarning : RelayStatusIcon.ColorRunning);
        Render(rebuildModelGrid);
    }

    private void Render(bool rebuildModelGrid = true)
    {
        // The bare host. The relay adds the version segment to paths that arrive
        // without one and collapses the ones that arrive doubled, so this single
        // string serves OpenAI-, Anthropic- and Gemini-shaped clients alike.
        _baseUrl.Text = $"http://{HostName()}:{RelayProtocol.Port}";
        _apiKey.Text = _config?.LocalApiKey ?? "not configured yet";

        BuildProviderGrid();
        // Rebuilding the model table is what threw away the reader's sort order and
        // scroll position every time they ticked a model, so the interactive path skips it
        // and updates the one row it changed instead.
        if (rebuildModelGrid) BuildModelTabs();
        BuildLedger();
        BuildUsageGrid();
        BuildAllowanceGrid();
        BuildHistoryGrid();
        BuildRouterGrid();

        var models = _refresh?.Router.ModelCount ?? 0;
        var serving = _refresh?.Router.ServingProviderCount ?? 0;
        _modelCount.Text = (models > 0
            ? $"{models} models from {serving} provider{(serving == 1 ? string.Empty : "s")}"
            : "No models yet") + PricesAsOf(LivePricing.Current?.FetchedAt, DateTimeOffset.Now);
        var clients = RelayStatus.RecentClientCount(_telemetry);
        _clients.Text = clients switch
        {
            0 => "No clients connected in the last 5 minutes",
            1 => "1 client connected in the last 5 minutes",
            _ => $"{clients} clients connected in the last 5 minutes"
        };
        _clients.ForeColor = clients > 0 ? Palette.LinkUp : Palette.TextTertiary;
    }

    private async Task AddProviderAsync()
    {
        ProviderSettings? added;
        var signIn = false;
        using (var quick = new AddProviderForm())
        {
            var answer = quick.ShowDialog(this);
            if (answer == DialogResult.Retry && quick.AdvancedRequested)
            {
                using var editor = new ProviderEditorForm(null);
                if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is null) return;
                added = editor.Result;
            }
            else if (answer == DialogResult.OK && quick.Result is not null)
            {
                added = quick.Result;
                signIn = quick.SignInRequested;
            }
            else return;
        }

        UpdateConfig(current => current with
        {
            Providers = [.. current.Providers, added with { Priority = current.Providers.Count }]
        });
        await ApplyAsync(restart: false);
        // One click from "Sign in" to served models: connect the account, then import
        // its catalog, so the models are ready to tick into a router.
        if (signIn) await RunGuardedAsync(() => SignInAndFetchAsync(added.Id));
    }

    private async Task SignInAndFetchAsync(string id)
    {
        if (!await ConnectProviderAsync(id, waitForCliLogin: true)) return;
        await FetchProviderModelsAsync(id);
    }

    private async Task EditProviderAsync()
    {
        if (_config is null || !SelectedProviderId(out var id)) return;
        var existing = _config.Providers.First(p => p.Id == id);
        using var editor = new ProviderEditorForm(existing);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is null) return;
        UpdateConfig(current =>
        {
            var latest = current.Providers.FirstOrDefault(provider => provider.Id == id);
            if (latest is null) return current;
            var edited = editor.Result with
            {
                Id = id,
                Priority = latest.Priority
            };
            edited = MergeLatestProfileState(existing, edited, latest);
            return current with { Providers = [.. current.Providers.Select(provider => provider.Id == id ? edited : provider)] };
        });
        await ApplyAsync(restart: false);
        _server.RouterEngine.ClearUnusable(id);
        // Edit is also how a signed-out account signs in again: check the sign-in (a
        // browser opens only when it is needed), then re-import the account's models.
        var saved = _settingsStore.FindProvider(id);
        if (saved is { Enabled: true } && saved.AuthMode != ProviderAuthMode.ApiKey)
            await RunGuardedAsync(() => SignInAndFetchAsync(id));
    }

    private async Task RemoveProviderAsync()
    {
        if (_config is null || !SelectedProviderId(out var id)) return;
        var provider = _config.Providers.First(p => p.Id == id);
        if (MessageBox.Show(this,
                $"Remove '{provider.Name}'?\n\nModels served only by this provider will disappear from the catalog.",
                "Remove provider", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        UpdateConfig(current => current with { Providers = [.. current.Providers.Where(p => p.Id != id)] });
        await ApplyAsync(restart: false);
    }

    /// <summary>Signs the provider's account in. Returns true when it is ready for requests.</summary>
    private async Task<bool> ConnectProviderAsync(string id, bool waitForCliLogin)
    {
        var provider = _settingsStore.FindProvider(id);
        if (provider is null) return false;
        if (provider.AuthMode == ProviderAuthMode.OAuth &&
            provider.Kind.Equals(ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            await SignInOpenAiAsync(provider);
            _config = _settingsStore.Load();
            await ApplyAsync(restart: false, refreshCatalog: false);
            SetStatus($"Signed in to OpenAI account {provider.Name}", RelayStatusIcon.ColorRunning);
            return true;
        }

        if (provider.AuthMode == ProviderAuthMode.OAuth &&
            provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            await SignInGeminiAsync(provider);
            _config = _settingsStore.Load();
            await ApplyAsync(restart: false, refreshCatalog: false);
            SetStatus($"Signed in to Gemini for {provider.Name}", RelayStatusIcon.ColorRunning);
            return true;
        }

        if (provider.Kind.Equals(ProviderKinds.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            using var gateway = new ClaudeCodeGateway(new ProviderCliRunner());
            var executable = CliExecutable(provider, "claude");
            return await CliSignInAsync(provider.Name, "Claude",
                () => gateway.CheckAuthenticationAsync(executable),
                () => StartHiddenLogin(executable, "auth", "login", "--claudeai"),
                "Finish signing in to Claude in the browser that opened.", waitForCliLogin);
        }

        if (provider.Kind.Equals(ProviderKinds.GeminiCli, StringComparison.OrdinalIgnoreCase))
        {
            var gateway = new GeminiCliGateway(new ProviderCliRunner());
            return await CliSignInAsync(provider.Name, "Gemini",
                () => Task.FromResult(gateway.IsSignedIn()),
                () => LaunchCliLogin(CliExecutable(provider, "gemini")),
                "In the Gemini window, choose \"Login with Google\" and finish in the browser. Close that window once you are signed in.",
                waitForCliLogin);
        }

        if (provider.Kind.Equals(ProviderKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            // agy has no status or login command. A tiny hidden request either succeeds,
            // or makes agy start Google sign-in in the browser and then succeeds.
            using var gateway = new AntigravityGateway(new ProviderCliRunner());
            SetStatus($"Signing in to {provider.Name}. If a Google sign-in page opens in your browser, finish it there.", RelayStatusIcon.ColorWarning);
            var signedIn = await gateway.SignInAsync(CliExecutable(provider, "agy"),
                uri => BeginInvoke(() => OpenAuthorizationPage(uri)));
            SetStatus(signedIn ? $"{provider.Name} is signed in"
                    : $"{provider.Name} sign-in did not finish. Select it and press Edit to try again.",
                signedIn ? RelayStatusIcon.ColorRunning : RelayStatusIcon.ColorWarning);
            return signedIn;
        }

        await ApplyAsync(restart: false, refreshCatalog: true);
        var result = _refresh?.Probes.FirstOrDefault(probe => probe.Provider.Id == id);
        SetStatus(result is null ? $"No status is available for {provider.Name}" : result.Healthy
            ? $"{provider.Name} is responding"
            : result.Error ?? $"{provider.Name} did not respond", result?.Healthy == true ? RelayStatusIcon.ColorRunning : RelayStatusIcon.ColorError);
        return result?.Healthy == true;
    }

    /// <summary>
    /// CLI accounts sign in inside the vendor's own CLI. Open its login, then poll its
    /// status until it reports signed in, so the user never has to come back and click.
    /// </summary>
    private async Task<bool> CliSignInAsync(string name, string product, Func<Task<bool>> isSignedIn,
        Action launchLogin, string instructions, bool wait)
    {
        SetStatus($"Checking {product} sign-in...", RelayStatusIcon.ColorWarning);
        if (await isSignedIn())
        {
            SetStatus($"{name} is signed in", RelayStatusIcon.ColorRunning);
            return true;
        }
        launchLogin();
        SetStatus(instructions, RelayStatusIcon.ColorWarning);
        if (!wait) return false;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!await isSignedIn()) continue;
            SetStatus($"{name} is signed in", RelayStatusIcon.ColorRunning);
            return true;
        }
        SetStatus($"{product} sign-in was not finished. Select the provider and press Edit to try again.", RelayStatusIcon.ColorWarning);
        return false;
    }

    private async Task SignInOpenAiAsync(ProviderSettings provider)
    {
        using var httpClient = NewAccountHttpClient();
        var tokenClient = new OAuthTokenClient(httpClient, _settingsStore);
        var hostId = _settingsStore.GetOrCreateOAuthHostId();
        await using var attempt = OpenAiChatGptOAuth.CreateAuthorizationAttempt(
            provider.OAuthClientId, hostId,
            provider.OAuthTokens?.IdToken, provider.OAuthEmail);
        OpenAuthorizationPage(attempt.AuthorizationUri);
        SetStatus("Waiting for ChatGPT sign-in in your browser...", RelayStatusIcon.ColorWarning);
        var callback = await attempt.WaitForCallbackAsync();
        var latest = _settingsStore.FindProvider(provider.Id);
        if (latest is null || latest.AuthMode != ProviderAuthMode.OAuth ||
            !latest.Kind.Equals(ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase) ||
            latest.OAuthClientId != provider.OAuthClientId || latest.AccountId != provider.AccountId)
            throw new InvalidOperationException("The OpenAI provider changed during sign-in. Start sign-in again.");
        await OpenAiChatGptSignIn.CompleteAsync(provider.Id, hostId, latest,
            attempt, callback, tokenClient, _settingsStore, new OpenAiIdTokenValidator(httpClient));
    }

    private async Task SignInGeminiAsync(ProviderSettings provider)
    {
        if (string.IsNullOrWhiteSpace(provider.OAuthClientId) || string.IsNullOrWhiteSpace(provider.OAuthClientSecret) ||
            string.IsNullOrWhiteSpace(provider.ProjectId))
            throw new InvalidOperationException("Edit the selected Gemini provider and enter its desktop OAuth client ID, client secret, and Cloud project ID first.");

        using var httpClient = NewAccountHttpClient();
        var tokenClient = new OAuthTokenClient(httpClient, _settingsStore);
        await using var attempt = GeminiOAuth.CreateAuthorizationAttempt(provider.OAuthClientId);
        OpenAuthorizationPage(attempt.AuthorizationUri);
        SetStatus("Waiting for Google sign-in in your browser...", RelayStatusIcon.ColorWarning);
        var callback = await attempt.WaitForCallbackAsync();
        var latest = _settingsStore.FindProvider(provider.Id);
        if (latest is null || latest.AuthMode != ProviderAuthMode.OAuth ||
            !latest.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) ||
            latest.OAuthClientId != provider.OAuthClientId || latest.OAuthClientSecret != provider.OAuthClientSecret ||
            latest.ProjectId != provider.ProjectId)
            throw new InvalidOperationException("The Gemini provider changed during sign-in. Start sign-in again.");
        await GeminiOAuthSignIn.CompleteAsync(provider.Id, latest, provider.OAuthClientId,
            provider.OAuthClientSecret, provider.ProjectId, attempt, callback, tokenClient, _settingsStore);
    }

    /// <summary>Imports one provider's models right after it is added or edited.</summary>
    private async Task FetchProviderModelsAsync(string id)
    {
        var provider = _settingsStore.FindProvider(id);
        if (provider is null) return;

        if (provider.AuthMode == ProviderAuthMode.ApiKey)
        {
            await ApplyAsync(restart: false, refreshCatalog: true);
            var count = _refresh?.Snapshot.ModelsByProviderId.TryGetValue(id, out var models) == true ? models.Count : 0;
            SetStatus(count > 0 ? $"Fetched {count} models for {provider.Name}" : $"No models were returned for {provider.Name}",
                count > 0 ? RelayStatusIcon.ColorRunning : RelayStatusIcon.ColorWarning);
            return;
        }

        SetStatus($"Finding models for {provider.Name}...", RelayStatusIcon.ColorWarning);
        var fetched = await FetchAccountModelsAsync(provider);
        if (fetched is null)
        {
            // Gemini CLI cannot list models, so the profile keeps its starting list.
            var count = provider.ImportedModels?.Count ?? 0;
            SetStatus(count > 0
                    ? $"{provider.Name} is ready with {count} models. Its CLI cannot list models; use Edit to change the list."
                    : $"{provider.Name} has no models. Its CLI cannot list them; use Edit to enter model IDs.",
                count > 0 ? RelayStatusIcon.ColorRunning : RelayStatusIcon.ColorWarning);
            return;
        }
        if (fetched.Count == 0)
        {
            SetStatus($"{provider.Name} did not report any models. Press Edit to sign in again, then Refresh all.", RelayStatusIcon.ColorWarning);
            return;
        }

        SaveImportedModels(id, fetched);
        _config = _settingsStore.Load();
        await ApplyAsync(restart: false, refreshCatalog: false);
        var note = provider.Kind.Equals(ProviderKinds.Antigravity, StringComparison.OrdinalIgnoreCase)
            ? " Antigravity CLI tool actions follow the CLI user's permissions; sandbox and temporary working directory reduce scope but do not guarantee tools are disabled."
            : string.Empty;
        SetStatus($"Fetched {fetched.Count} models for {provider.Name}.{note}", RelayStatusIcon.ColorRunning);
    }

    /// <summary>
    /// An account provider's live model list, or null when its CLI cannot list models
    /// (Gemini CLI). Never opens a sign-in window; a signed-out account throws.
    /// </summary>
    private async Task<IReadOnlyList<string>?> FetchAccountModelsAsync(ProviderSettings provider)
    {
        if (provider.Kind.Equals(ProviderKinds.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            using var resolver = new ClaudeCodeGateway(new ProviderCliRunner());
            var aliases = await resolver.ResolveModelsAsync(CliExecutable(provider, "claude"));
            // The aliases only name the newest model per family. models.dev lists every
            // Claude model, which Claude Code accepts by exact id.
            if (LivePricing.Current is null)
            {
                using var pricingClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                await LivePricing.RefreshAsync(RelayPaths.File("pricing.json"), pricingClient);
            }
            return ClaudeCodeGateway.MergeCatalog(aliases, LivePricing.Current?.VendorModels("anthropic") ?? []);
        }
        if (provider.Kind.Equals(ProviderKinds.GeminiCli, StringComparison.OrdinalIgnoreCase))
            return null;
        if (provider.AuthMode == ProviderAuthMode.OAuth && provider.Kind.Equals(ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            using var httpClient = NewAccountHttpClient();
            return await new OpenAiChatGptModelClient(httpClient, new OAuthTokenClient(httpClient, _settingsStore)).FetchModelsAsync(provider);
        }
        if (provider.AuthMode == ProviderAuthMode.OAuth && provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            using var httpClient = NewAccountHttpClient();
            return await new GeminiOAuthModelClient(httpClient, new OAuthTokenClient(httpClient, _settingsStore)).FetchModelsAsync(provider);
        }
        if (provider.AuthMode == ProviderAuthMode.CliAccount && provider.Kind.Equals(ProviderKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            using var gateway = new AntigravityGateway(new ProviderCliRunner());
            return await gateway.FetchModelsAsync(CliExecutable(provider, "agy"));
        }
        throw new InvalidOperationException($"Model fetching is not available for {provider.Name} with {provider.AuthMode} authentication.");
    }

    private void SaveImportedModels(string providerId, IEnumerable<string> models)
    {
        var distinctModels = models.Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        _config = _settingsStore.Update(current =>
        {
            if (current is null) throw new InvalidOperationException("Provider profile settings are unavailable.");
            var found = false;
            var providers = current.Providers.Select(profile =>
            {
                if (profile.Id != providerId) return profile;
                found = true;
                return profile with { ImportedModels = distinctModels, ModelsFetchedAt = DateTimeOffset.UtcNow };
            }).ToArray();
            if (!found) throw new InvalidOperationException("The selected provider profile no longer exists.");
            return current with { Providers = providers };
        });
    }

    private static HttpClient NewAccountHttpClient() => new() { Timeout = TimeSpan.FromSeconds(45) };

    private static void OpenAuthorizationPage(Uri authorizationUri)
    {
        using var browser = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
        if (browser is null) throw new InvalidOperationException("Could not open the default browser for sign-in.");
    }

    private static string CliExecutable(ProviderSettings provider, string fallback) =>
        string.IsNullOrWhiteSpace(provider.CliExecutable) ? fallback : provider.CliExecutable;

    /// <summary>
    /// Runs a CLI login with no console window; the CLI opens the browser itself. The
    /// process is stopped after five minutes so an abandoned login cannot linger.
    /// </summary>
    private static void StartHiddenLogin(string executable, params string[] arguments) =>
        _ = RunHiddenLoginAsync(executable, arguments);

    private static async Task RunHiddenLoginAsync(string executable, string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await new ProviderCliRunner().RunAsync(executable, arguments, null, null, timeout.Token); }
        catch (OperationCanceledException) { }
    }

    private static void LaunchCliLogin(string executable, params string[] arguments)
    {
        if (executable.Contains('"'))
            throw new InvalidOperationException("The CLI executable path cannot contain a quote character.");
        var command = '"' + executable + '"' + (arguments.Length == 0 ? string.Empty : " " + string.Join(' ', arguments));
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /k \"" + command + "\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false
        };
        if (Process.Start(startInfo) is not { } process)
            throw new InvalidOperationException($"Could not launch {Path.GetFileName(executable)} login.");
        process.Dispose();
    }

    private static ProviderSettings MergeLatestProfileState(
        ProviderSettings original, ProviderSettings edited, ProviderSettings latest)
    {
        static IReadOnlyList<string>? KeepLatestWhenUnchanged(
            IReadOnlyList<string>? oldValue, IReadOnlyList<string>? editedValue, IReadOnlyList<string>? latestValue) =>
            (oldValue ?? []).SequenceEqual(editedValue ?? [], StringComparer.Ordinal)
                ? latestValue
                : editedValue;

        return edited with
        {
            AuthMode = edited.AuthMode == original.AuthMode ? latest.AuthMode : edited.AuthMode,
            AccountId = edited.AccountId == original.AccountId ? latest.AccountId : edited.AccountId,
            ProjectId = edited.ProjectId == original.ProjectId ? latest.ProjectId : edited.ProjectId,
            CliExecutable = edited.CliExecutable == original.CliExecutable ? latest.CliExecutable : edited.CliExecutable,
            ImportedModels = KeepLatestWhenUnchanged(original.ImportedModels, edited.ImportedModels, latest.ImportedModels),
            ModelsFetchedAt = edited.ModelsFetchedAt == original.ModelsFetchedAt ? latest.ModelsFetchedAt : edited.ModelsFetchedAt,
            OAuthTokens = Equals(edited.OAuthTokens, original.OAuthTokens) ? latest.OAuthTokens : edited.OAuthTokens,
            OAuthClientId = edited.OAuthClientId == original.OAuthClientId ? latest.OAuthClientId : edited.OAuthClientId,
            OAuthClientSecret = edited.OAuthClientSecret == original.OAuthClientSecret ? latest.OAuthClientSecret : edited.OAuthClientSecret
        };
    }

    private bool SelectedProviderId(out string id)
    {
        id = string.Empty;
        if (_providerGrid.CurrentRow?.Tag is not string providerId) return false;
        var provider = _config?.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return false;
        id = provider.Id;
        return true;
    }

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            SetStatus($"Copied {what} to the clipboard", RelayStatusIcon.ColorRunning);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy {what}: {ex.Message}", RelayStatusIcon.ColorError);
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
        _dot.ForeColor = color;
        _rail.BackColor = color;
        _rail.Width = _status.ForeColor == Palette.TextMuted ? 2 : 3;
        _tray.Text = text.Length <= 63 ? text : text[..63];
        var next = RelayStatusIcon.Create(color);
        var previous = _trayIcon;
        _trayIcon = next;
        // The taskbar shows this form's icon, so it has to track the same state as
        // the tray. Assigning the same object to both keeps one source of truth.
        Icon = next;
        _tray.Icon = next;
        previous.Dispose();
    }

    private async Task RunGuardedAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, RelayStatusIcon.ColorError);
        }
        finally
        {
            _busy = false;
        }
    }

    internal void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    // ---------------------------------------------------------------- helpers

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open relay", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh all", null, (_, _) => _ = RunGuardedAsync(RefreshAllAsync));
        menu.Items.Add("Discover endpoints", null, (_, _) => _ = RunGuardedAsync(DiscoverAsync));
        menu.Items.Add("Open diagnostic log", null, (_, _) => OpenDiagnosticLog());
        menu.Items.Add("Stop relay", null, async (_, _) =>
        {
            _userStopped = true;
            await _server.StopAsync();
            SetStatus("Stopped", RelayStatusIcon.ColorWarning);
        });
        menu.Items.Add("Firewall command", null, (_, _) => CopyToClipboard(FirewallManager.ManualCommand, "firewall command"));
        menu.Items.Add("Forget configuration", null, async (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Forget the saved providers and generate a new local client key?\n\nAny client still using the current key will stop working.",
                    "Forget configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await _server.StopAsync();
            _settingsStore.Delete();
            // The cached catalog is keyed to the providers that just went away, so
            // leaving it would keep offering their models as available.
            _catalogCache.Delete();
            _config = null;
            _refresh = null;
            SetStatus("Configuration forgotten", RelayStatusIcon.ColorIdle);
            Render();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        return menu;
    }

    private static string HostName() => $"{Environment.MachineName}.local";

    // AutoSize must be off: an auto-sizing label inside a Percent column claims its
    // preferred width and pushes the Copy button off the right edge.
    private static Label ValueLabel() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Palette.TextPrimary,
        BackColor = Color.Transparent,
        Font = new Font("Consolas", 9.5F)
    };

    private static Button SmallButton(string text, Action onClick)
    {
        var button = MakeButton(text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button SmallButton(string text, Func<Task> onClick)
    {
        var button = MakeButton(text);
        // async void handler; the exception surface is the global ThreadException hook.
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(12, 4, 12, 4),
        FlatStyle = FlatStyle.Flat,
        BackColor = Palette.Hairline,
        ForeColor = Palette.TextPrimary,
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand
    };
}
