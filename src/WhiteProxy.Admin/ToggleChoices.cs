namespace WhiteProxy.Admin;

internal sealed class ToggleChoices : FlowLayoutPanel
{
    internal string? SelectedId => Controls.OfType<RadioButton>().FirstOrDefault(b => b.Checked)?.Tag as string;
    internal event EventHandler? SelectionChanged;

    internal ToggleChoices(IEnumerable<Choice> choices, string? selected = null)
    {
        WrapContents = false; AutoScroll = true;
        foreach (var choice in choices)
        {
            var button = new RadioButton { Text = choice.Name, Tag = choice.Id, Name = choice.Id, Appearance = Appearance.Button,
                AutoSize = true, MinimumSize = new(100, 34), TextAlign = ContentAlignment.MiddleCenter,
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(28, 44, 70),
                Padding = new(10, 4, 10, 4), Margin = new(0, 0, 7, 0) };
            button.FlatAppearance.BorderColor = Color.FromArgb(175, 190, 209);
            button.FlatAppearance.CheckedBackColor = Color.FromArgb(36, 91, 157);
            button.CheckedChanged += (_, _) =>
            {
                button.BackColor = button.Checked ? Color.FromArgb(36, 91, 157) : Color.White;
                button.ForeColor = button.Checked ? Color.White : Color.FromArgb(28, 44, 70);
                if (button.Checked) SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            Controls.Add(button);
        }
        var buttons = Controls.OfType<RadioButton>().ToList();
        var initial = buttons.FirstOrDefault(b => (string?)b.Tag == selected) ?? buttons.FirstOrDefault();
        if (initial is not null) initial.Checked = true;
    }
}
