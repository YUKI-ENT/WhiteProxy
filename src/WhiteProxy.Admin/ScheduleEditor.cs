using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class ScheduleEditor
{
    internal static EditorDialog Create(RecurringSchedule? existing, Action<RecurringSchedule> accept)
    {
        var form = new EditorDialog("曜日・時間帯", 500, "決定");
        var week = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        var days = form.Add("曜日", new FlowLayoutPanel { WrapContents = false }, 48);
        var checks = week.Select(day => new CheckBox { Name = "Day" + day, Text = Editors.DayName(day), AutoSize = true,
            MinimumSize = new(45, 30), Checked = existing?.Days.Contains(day) ?? (day is not DayOfWeek.Saturday and not DayOfWeek.Sunday) }).ToArray();
        days.Controls.AddRange(checks);
        var presets = form.Add("曜日をまとめて選択", new FlowLayoutPanel { WrapContents = false }, 48);
        foreach (var preset in new[] { "毎日", "平日", "土日", "リセット" })
        {
            var button = new Button { Text = preset, AutoSize = true, MinimumSize = new(70, 32) };
            button.Click += (_, _) => { for (var i = 0; i < checks.Length; i++) checks[i].Checked = preset == "毎日" || preset == "平日" && i < 5 || preset == "土日" && i >= 5; };
            presets.Controls.Add(button);
        }
        var start = form.Add("開始", new DateTimePicker { Name = "ScheduleStart", Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.Add((existing?.Start ?? new(12, 0)).ToTimeSpan()) });
        var end = form.Add("終了", new DateTimePicker { Name = "ScheduleEnd", Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.Add((existing?.End ?? new(13, 0)).ToTimeSpan()) });
        var enabled = form.Check("この時間帯", existing?.Enabled ?? true);
        form.Add("時間の指定", new Label { AutoSize = true, Text = "同じ日の開始・終了を指定します。日をまたぐ指定はできません。\n24時間の許可はルールの「全日・24時間」を選択してください。" }, 85);
        form.ValidateInput = () =>
        {
            var a = new TimeOnly(start.Value.Hour, start.Value.Minute); var b = new TimeOnly(end.Value.Hour, end.Value.Minute);
            if (!checks.Any(c => c.Checked) || b <= a) return EditorDialog.Error("曜日を選び、終了を開始より後の時刻に指定してください（日またぎ不可）。");
            accept(new() { Days = checks.Select((c, i) => (c, i)).Where(x => x.c.Checked).Select(x => week[x.i]).ToList(), Start = a, End = b, Enabled = enabled.Checked }); return true;
        };
        return form;
    }
}
