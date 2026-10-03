namespace LocalCloudRelay;

/// <summary>
/// Add or edit a router: a name, a strategy, and the models allowed to answer it.
///
/// The model list is the point of this dialog. A router picks from what you selected here
/// and nothing else, so the models on offer are listed with their provider and price to
/// make the choice an informed one.
/// </summary>
public sealed class RouterEditorForm : Form
{
    private readonly TextBox _name = Field();
    private readonly ComboBox _strategy = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary
    };
    private readonly CheckedListBox _pool = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        IntegralHeight = false
    };
    private readonly Label _summary = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top,
        ForeColor = Palette.TextMuted,
        BackColor = Color.Transparent,
        Padding = new Padding(0, 4, 0, 0)
    };
    private readonly Label _explain = new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Palette.TextPrimary,
        BackColor = Palette.Input,
        Padding = new Padding(10, 8, 10, 8),
        Margin = new Padding(0, 4, 0, 4)
    };
    private readonly CheckBox _enabled = new()
    {
        Text = "Enabled",
        AutoSize = true,
        ForeColor = Palette.TextSecondary,
        BackColor = Color.Transparent
    };

    private readonly TextBox _budget = new()
    {
        Width = 90,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "no limit"
    };

    private readonly IReadOnlyList<RouterPoolEntry> _available;

    public RouterRule? Result { get; private set; }

    /// <summary>One selectable model: what it is, who serves it, and what it costs.</summary>
    public sealed record RouterPoolEntry(string Model, string ProviderName, string PriceLabel, string Tip)
    {
        public override string ToString() => $"{Model}   ·   {ProviderName}   ·   {PriceLabel}";
    }

    public RouterEditorForm(RouterRule? existing, IReadOnlyList<RouterPoolEntry> available)
    {
        _available = available;
        Text = existing is null ? "Add router" : "Edit router";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Panel;
        ForeColor = Palette.TextPrimary;
        Font = new Font("Segoe UI", 9F);
        // Tall enough for its own rows, the strategy explanation, a usable model list and
        // the button bar.
        ClientSize = new Size(680, 760);

        _strategy.Items.AddRange(RouterStrategies.All.Select(s => (object)new StrategyItem(s)).ToArray());
        _strategy.SelectedIndexChanged += (_, _) => UpdateSummary();
        // ItemCheck fires once per row while the list is populated below, which happens
        // during construction - before the dialog has a window handle, and BeginInvoke
        // throws there. That is why Edit crashed and Add did not: only an existing rule
        // starts with rows already ticked. Deferring is still wanted for a real click,
        // because ItemCheck runs before the check state is committed and the count would
        // otherwise be one behind.
        _pool.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(UpdateSummary);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Palette.Panel,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowCount = 8;

        var intro = new Label
        {
            Text = "A router is a model name your agent asks for, such as \"coding\". The relay answers it " +
                   "with one of the models you tick below, chosen by the strategy. Put the router name " +
                   "in your agent's model setting.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Palette.TextSecondary,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 10)
        };
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(Caption("Name"), 0, 1);
        layout.Controls.Add(_name, 1, 1);
        layout.Controls.Add(Caption("Strategy"), 0, 2);
        layout.Controls.Add(_strategy, 1, 2);
        layout.SetColumnSpan(_explain, 2);
        layout.Controls.Add(_explain, 0, 3);
        layout.SetColumnSpan(_summary, 2);
        layout.Controls.Add(_summary, 0, 4);
        layout.SetColumnSpan(_pool, 2);
        layout.Controls.Add(_pool, 0, 5);
        // Enabled and the optional daily budget share a row.
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        options.Controls.Add(_enabled);
        options.Controls.Add(new Label
        {
            Text = "Daily budget  $",
            AutoSize = true,
            ForeColor = Palette.TextSecondary,
            BackColor = Color.Transparent,
            Padding = new Padding(24, 4, 0, 0)
        });
        options.Controls.Add(_budget);
        layout.SetColumnSpan(options, 2);
        layout.Controls.Add(options, 0, 6);
        var hint = new Label
        {
            Text = "Only the models ticked above can answer this name. Unticking is the only way to remove one.",
            AutoSize = true,
            ForeColor = Palette.TextMuted,
            BackColor = Color.Transparent
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 7);
        Controls.Add(layout);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Palette.Panel,
            Padding = new Padding(12)
        };
        var cancel = DarkButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var save = DarkButton("Save", primary: true);
        save.Click += (_, _) => Save();
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        Controls.Add(actions);
        AcceptButton = save;

        FillPool(existing);
    }

    /// <summary>
    /// What the model list should show, and which rows start ticked.
    ///
    /// Pure and separate from the control so it can be tested without driving a dialog.
    /// A model the rule names but the catalog no longer serves is still listed - otherwise
    /// editing an existing rule would silently drop that entry the moment it saved.
    /// </summary>
    public static IReadOnlyList<(RouterPoolEntry Entry, bool Ticked)> BuildPool(
        RouterRule? existing, IReadOnlyList<RouterPoolEntry> available)
    {
        var pool = existing?.Pool ?? [];
        var rows = new List<(RouterPoolEntry, bool)>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in available)
        {
            known.Add(entry.Model);
            rows.Add((entry, pool.Contains(entry.Model, StringComparer.OrdinalIgnoreCase)));
        }
        foreach (var missing in pool.Where(m => !known.Contains(m)))
            rows.Add((new RouterPoolEntry(missing, "not served now", "no price",
                "Not in the current catalog."), true));

        return rows;
    }

    /// <summary>
    /// Lists every served model, ticking the ones already in the rule. A model that is no
    /// longer served still appears if the rule names it, so an existing rule can be edited
    /// rather than silently losing an entry.
    /// </summary>
    private void FillPool(RouterRule? existing)
    {
        foreach (var (entry, ticked) in BuildPool(existing, _available))
            _pool.Items.Add(entry, ticked);

        if (_pool.Items.Count == 0)
            _summary.Text = "No models are being served yet. Add a provider and press Refresh first.";

        if (existing is not null)
        {
            _name.Text = existing.Name;
            // Canonical, so a rule saved under an old name ("cheapest-first") keeps its
            // strategy instead of silently becoming Sticky when edited.
            _strategy.SelectedIndex = Math.Max(0, RouterStrategies.All.ToList().IndexOf(RouterStrategies.Canonical(existing.Strategy)));
            _enabled.Checked = existing.Enabled;
            _budget.Text = existing.DailyBudgetUsd?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        else
        {
            _strategy.SelectedIndex = 0;
            _enabled.Checked = true;
        }
        UpdateSummary();
    }

    private IEnumerable<string> Ticked() =>
        _pool.CheckedItems.Cast<RouterPoolEntry>().Select(e => e.Model).ToArray();

    private string Selected() => (_strategy.SelectedItem as StrategyItem)?.Value ?? RouterStrategies.All[0];

    private sealed record StrategyItem(string Value)
    {
        public override string ToString() => RouterStrategies.Title(Value);
    }

    private void UpdateSummary()
    {
        var count = Ticked().Count();
        _summary.Text = $"{count} of {_pool.Items.Count} models ticked. A failed model rests from " +
                        $"{RouterEngine.Cooldown.TotalSeconds:0} seconds, doubling if it keeps failing, while the next one answers.";
        _explain.Text = RouterStrategies.Explain(Selected());
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Warn("Give the router a name.");
            return;
        }
        if (name.Length > RouterRule.MaxNameLength)
        {
            Warn($"Keep the name under {RouterRule.MaxNameLength} characters.");
            return;
        }
        // A space inside a model id is ambiguous: some agents put it in JSON, some in a
        // path. Not worth supporting by accident.
        if (name.Any(char.IsWhiteSpace))
        {
            Warn("A router name cannot contain spaces - it is sent as a model id.");
            return;
        }

        var models = Ticked().ToArray();
        if (models.Length == 0)
        {
            Warn("Tick at least one model. A router only ever picks from its own list.");
            return;
        }

        if (!TryParseBudget(_budget.Text, out var budget))
        {
            Warn("Enter the daily budget as a dollar amount, such as 5 or 2.50, or leave it empty for no limit.");
            return;
        }

        Result = new RouterRule(name, Selected(), models, _enabled.Checked, budget);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Empty means no limit; otherwise a positive dollar amount, with either decimal mark.</summary>
    internal static bool TryParseBudget(string? text, out decimal? budget)
    {
        budget = null;
        var trimmed = (text ?? string.Empty).Trim().TrimStart('$').Replace(',', '.');
        if (trimmed.Length == 0) return true;
        if (!decimal.TryParse(trimmed, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0m) return false;
        budget = value;
        return true;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        ForeColor = Palette.TextSecondary,
        BackColor = Color.Transparent,
        Padding = new Padding(0, 6, 0, 0)
    };

    private static TextBox Field() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Palette.Input,
        ForeColor = Palette.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button DarkButton(string text, bool primary = false) => new()
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
