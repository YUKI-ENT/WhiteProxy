using System.Reflection;
using WhiteProxy.Admin;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class DashboardRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T03:30:00Z");
    private static Configuration Config() => new()
    {
        Devices = [new() { Id = "pc", Name = "受付端末", Address = "192.168.10.21" }],
        Services = [new() { Id = "web", Name = "業務サービス", Hosts = ["example.com"] }],
        Policies = [new() { DeviceId = "pc", ServiceId = "web" }]
    };

    [Fact]
    public void TemporaryOnlyRuleStaysClosedUntilExplicitGrantAndClosesAtExpiry()
    {
        var c = Config(); var rule = c.Policies[0];
        Assert.False(DashboardRules.Evaluate(c, rule, Now).Allowed);
        c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(30) });
        Assert.True(DashboardRules.Evaluate(c, rule, Now).Allowed);
        Assert.False(DashboardRules.Evaluate(c, rule, Now.AddMinutes(30)).Allowed);
        rule.Deny = true;
        Assert.False(DashboardRules.Evaluate(c, rule, Now).Allowed);
        rule.Deny = false;
        Assert.True(DashboardRules.Evaluate(c, rule, Now).Allowed);
        Assert.False(DashboardRules.Evaluate(c, rule, Now.AddMinutes(30)).Allowed);
    }

    [Fact]
    public void GrantOnlyPairsRemainVisibleWithoutCreatingPermanentPermission()
    {
        var c = Config(); c.Policies.Clear();
        c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now.AddMinutes(10), ExpiresAtUtc = Now.AddHours(1) });
        var rule = Assert.Single(DashboardRules.List(c, Now));
        Assert.False(rule.AlwaysAllow); Assert.Empty(rule.Schedules);
        Assert.False(DashboardRules.Evaluate(c, rule, Now).Allowed);
        Assert.Equal("10/28 12:40", DashboardRules.NextStart(c, rule, Now));
        Assert.True(DashboardRules.Evaluate(c, rule, Now.AddMinutes(10)).Allowed);
        Assert.Empty(DashboardRules.List(c, Now.AddHours(1)));
        Assert.Empty(c.Policies);
    }

    [Fact]
    public void ResumeDoesNotAllowOutsideScheduleAndGlobalBlockStillWins()
    {
        var c = Config(); var p = c.Policies[0];
        p.Schedules.Add(new() { Days = [DayOfWeek.Wednesday], Start = new(12, 0), End = new(13, 0) });
        Assert.True(DashboardRules.Evaluate(c, p, Now).Allowed);
        p.Deny = true; Assert.False(DashboardRules.Evaluate(c, p, Now).Allowed);
        Assert.Equal("遮断解除が必要", DashboardRules.NextStart(c, p, Now));
        p.Deny = false; Assert.False(DashboardRules.Evaluate(c, p, Now.AddHours(1)).Allowed);
        c.Proxy.GlobalBlock = true; Assert.False(DashboardRules.Evaluate(c, p, Now).Allowed);
        c.Proxy.GlobalBlock = false; c.Services[0].Hosts.Clear();
        Assert.False(DashboardRules.Evaluate(c, p, Now).Allowed);
    }

    [Fact]
    public async Task DashboardContainsAllRuleActionsAndNoSeparateDeviceOrScheduleTabs()
    {
        await Sta(async () =>
        {
            using var form = new MainForm();
            var controls = Descendants(form).ToList();
            var tabs = Assert.Single(controls.OfType<TabControl>());
            Assert.Equal("ダッシュボード", tabs.TabPages[0].Text);
            Assert.DoesNotContain(tabs.TabPages.Cast<TabPage>(), t => t.Text is "端末管理" or "スケジュール・許可" or "許可ルール");
            var dashboardButtons = Descendants(tabs.TabPages[0]).OfType<Button>().Select(b => b.Text).ToList();
            Assert.Contains("＋ 許可ルールを追加", dashboardButtons);
            Assert.DoesNotContain("一時許可・延長", dashboardButtons);
            var c = Config(); c.Policies[0].AlwaysAllow = true;
            typeof(MainForm).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, c);
            typeof(MainForm).GetMethod("RefreshTables", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            var grid = Assert.Single(Descendants(tabs.TabPages[0]).OfType<DataGridView>());
            Assert.Equal("■ 状態未確認", grid.Rows[0].Cells["状態"].Value);
            Assert.Equal(5, grid.Columns.OfType<DataGridViewTextBoxColumn>().Count(c => c.Name != "drag"));
            Assert.Equal(new[] { "edit", "grant", "block", "delete", "clone" }, grid.Columns.OfType<DataGridViewButtonColumn>().Select(c => c.Name));
            Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column => Assert.Equal(DataGridViewColumnSortMode.NotSortable, column.SortMode));
            Assert.Equal("drag", grid.Columns[grid.ColumnCount - 1].Name);
            Assert.All(grid.Columns.OfType<DataGridViewButtonColumn>(), column => Assert.Equal("", column.HeaderText));
            Assert.Equal(DataGridViewCellBorderStyle.SingleHorizontal, grid.CellBorderStyle);
            Assert.All(grid.Rows[0].Cells.OfType<RuleActionCell>(), cell => Assert.False(cell.ActionEnabled));
            typeof(MainForm).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
            typeof(MainForm).GetMethod("RefreshTables", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Assert.Equal("サービス更新が必要", grid.Rows[0].Cells["現在の理由"].Value);
            Assert.All(grid.Rows[0].Cells.OfType<RuleActionCell>(), cell => Assert.False(cell.ActionEnabled));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task NewRuleSavesDeviceAndClosedRuleTogetherAndCancelHasNoSideEffects()
    {
        await Sta(async () =>
        {
            var c = Config(); c.Devices.Clear(); c.Policies.Clear(); Configuration? saved = null;
            using var form = RuleEditor.Create(c, null, candidate => { saved = candidate; return Task.FromResult(true); });
            var controls = Descendants(form).ToList();
            Assert.DoesNotContain(controls.OfType<CheckBox>(), b => b.Text.Contains("明示拒否"));
            var mode = Assert.Single(controls.OfType<ComboBox>(), b => b.Name == "RuleMode");
            Assert.Equal("temporary", Assert.IsType<Choice>(mode.SelectedItem).Id);
            Assert.Equal(-1, Assert.Single(controls.OfType<ComboBox>(), b => b.Name == "RuleService").SelectedIndex);
            Assert.Single(controls.OfType<TextBox>(), b => b.Name == "RuleDeviceName").Text = "新端末";
            Assert.Single(controls.OfType<TextBox>(), b => b.Name == "RuleAddress").Text = "192.168.10.22";
            Assert.Single(controls.OfType<ComboBox>(), b => b.Name == "RuleService").SelectedIndex = 0;
            Assert.True(form.ValidateInput!());
            Assert.Empty(c.Devices); Assert.Empty(c.Policies); Assert.Null(saved);
            Assert.True(await form.SaveAsync!());
            var device = Assert.Single(saved!.Devices); var rule = Assert.Single(saved.Policies);
            Assert.Equal(device.Id, rule.DeviceId); Assert.False(rule.AlwaysAllow); Assert.False(rule.Deny); Assert.Empty(rule.Schedules);
            Assert.False(DashboardRules.Evaluate(saved, rule, Now).Allowed);
        });
    }

    [Fact]
    public async Task EditingBlockedRulePreservesBlockAndTemporaryGrantHistory()
    {
        await Sta(async () =>
        {
            var c = Config(); c.Policies[0].Deny = true;
            c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now, ExpiresAtUtc = Now.AddHours(1) });
            Configuration? saved = null;
            using var form = RuleEditor.Create(c, c.Policies[0], candidate => { saved = candidate; return Task.FromResult(true); });
            Assert.True(form.ValidateInput!()); Assert.True(await form.SaveAsync!());
            Assert.True(saved!.Policies[0].Deny); Assert.Single(saved.TemporaryGrants);
            Assert.False(DashboardRules.Evaluate(saved, saved.Policies[0], Now).Allowed);
        });
    }

    [Fact]
    public void InactiveUpperSubnetDoesNotShadowHostAllowance()
    {
        var c = Config(); var host = c.Policies[0]; host.AlwaysAllow = true;
        c.Devices.Add(new() { Id = "subnet", Address = "192.168.10.0/24" });
        var upper = new AccessPolicy { DeviceId = "subnet", ServiceId = "web" };
        c.Policies.Insert(0, upper);
        Assert.True(DashboardRules.Evaluate(c, host, Now).Allowed);
        Assert.DoesNotContain("上位", DashboardRules.Reason(c, host, Now));
        upper.Deny = true;
        Assert.False(DashboardRules.Evaluate(c, host, Now).Allowed);
        c.Devices[1].Enabled = false;
        Assert.True(DashboardRules.Evaluate(c, host, Now).Allowed);
    }

    [Fact]
    public void MovingHostExceptionUpdatesOrderAndShadowedState()
    {
        var c = Config(); c.Policies[0].AlwaysAllow = true;
        c.Devices.Add(new() { Id = "subnet", Address = "192.168.10.0/24" });
        c.Policies.Insert(0, new() { DeviceId = "subnet", ServiceId = "web", Deny = true });
        var host = c.Policies[1];
        Assert.False(DashboardRules.Evaluate(c, host, Now).Allowed);
        Assert.Contains("上位 1 行目", DashboardRules.Reason(c, host, Now));
        Assert.True(DashboardRules.Move(c, DashboardRules.Key(host), -1));
        Assert.True(DashboardRules.Evaluate(c, host, Now).Allowed);
        Assert.False(DashboardRules.Move(c, DashboardRules.Key(host), -1));
        Assert.True(DashboardRules.Move(c, DashboardRules.Key(host), 1));
        Assert.False(DashboardRules.Evaluate(c, host, Now).Allowed);
    }

    [Fact]
    public async Task EditorAllowsOverlappingAndIdenticalSourceAddressesAndKeepsRulePosition()
    {
        await Sta(async () =>
        {
            var c = Config(); c.Devices[0].Address = "192.168.10.0/24";
            Configuration? saved = null;
            foreach (var ip in new[] { "192.168.10.21/32", "192.168.10.21" })
            {
                using var form = RuleEditor.Create(c, null, candidate => { saved = candidate; return Task.FromResult(true); });
                var controls = Descendants(form).ToList();
                Assert.DoesNotContain(controls.OfType<Button>(), b => b.Text == "端末情報を編集");
                controls.OfType<TextBox>().Single(b => b.Name == "RuleDeviceName").Text = "例外端末";
                controls.OfType<TextBox>().Single(b => b.Name == "RuleAddress").Text = ip;
                controls.OfType<ComboBox>().Single(b => b.Name == "RuleService").SelectedIndex = 0;
                Assert.True(form.ValidateInput!()); Assert.True(await form.SaveAsync!()); c = saved!;
            }
            Assert.Equal(3, c.Policies.Count); Assert.Empty(ConfigurationValidator.Validate(c));
            var first = DashboardRules.Key(c.Policies[0]); var second = DashboardRules.Key(c.Policies[1]);
            using var edit = RuleEditor.Create(c, c.Policies[1], candidate => { saved = candidate; return Task.FromResult(true); });
            Assert.True(edit.ValidateInput!()); Assert.True(await edit.SaveAsync!());
            Assert.Equal(first, DashboardRules.Key(saved!.Policies[0])); Assert.Equal(second, DashboardRules.Key(saved.Policies[1]));
        });
    }

    [Fact]
    public async Task WeekdaysAreHorizontalMondayToSundayAndResetClearsAll()
    {
        await Sta(async () =>
        {
            using var form = ScheduleEditor.Create(null, _ => { });
            var controls = Descendants(form).ToList();
            var days = controls.OfType<CheckBox>().Where(c => c.Name.StartsWith("Day")).ToList();
            Assert.Equal(new[] { "月", "火", "水", "木", "金", "土", "日" }, days.Select(c => c.Text));
            Assert.False(Assert.IsType<FlowLayoutPanel>(days[0].Parent).WrapContents);
            var reset = controls.OfType<Button>().Single(b => b.Text == "リセット");
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reset, [EventArgs.Empty]);
            Assert.All(days, day => Assert.False(day.Checked));
            Assert.DoesNotContain(controls.OfType<Label>(), label => label.Text == "日跨ぎ");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task GrantFormHasNoReasonAndHandlesCreateExtendAndEnd()
    {
        await Sta(async () =>
        {
            var c = Config(); var sent = new List<IpcRequest>();
            Task<bool> Send(IpcRequest request) { sent.Add(request); return Task.FromResult(true); }
            using (var create = GrantEditor.Create(c, c.Policies[0], Now, Send, _ => true))
            {
                var controls = Descendants(create).ToList();
                Assert.DoesNotContain(controls.OfType<TextBox>(), b => b.Text.Contains("理由"));
                Assert.DoesNotContain(controls.OfType<Label>(), b => b.Text.Contains("理由"));
                Assert.True(create.ValidateInput!()); Assert.True(await create.SaveAsync!());
                Assert.Equal("grant", sent[0].Operation); Assert.Equal("", sent[0].Reason);
            }
            c.TemporaryGrants.Add(new() { Id = "active", DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(30) });
            using var manage = GrantEditor.Create(c, c.Policies[0], Now, Send, _ => true);
            var action = Descendants(manage).OfType<ToggleChoices>().Single(b => b.Name == "GrantAction");
            Assert.Equal(new[] { "grant", "end" }, action.Controls.OfType<RadioButton>().Select(c => c.Tag as string));
            action.Controls.OfType<RadioButton>().Last().Checked = true;
            Assert.False(Descendants(manage).OfType<DateTimePicker>().Single(b => b.Name == "GrantEnd").Enabled);
            Assert.True(manage.ValidateInput!()); Assert.True(await manage.SaveAsync!());
            Assert.Equal("revoke", sent[^1].Operation); Assert.Equal("active", sent[^1].GrantId);
        });
    }

    [Theory]
    [InlineData(0, 3, "b,c,a")]
    [InlineData(2, 0, "c,a,b")]
    [InlineData(0, 2, "b,a,c")]
    [InlineData(2, 1, "a,c,b")]
    public void DragInsertionUsesVisibleGapAndPersistsExactOrder(int source, int gap, string expected)
    {
        var c = Config();
        c.Policies = new[] { "a", "b", "c" }.Select(id => new AccessPolicy { DeviceId = id, ServiceId = "web" }).ToList();
        var key = DashboardRules.Key(c.Policies[source]);
        Assert.True(DashboardRules.MoveTo(c, key, gap));
        Assert.Equal(expected, string.Join(',', JsonDefaults.Clone(c).Policies.Select(p => p.DeviceId)));
    }

    [Fact]
    public void DraggingWithinOwnRowOrOutsideListDoesNotChangeOrder()
    {
        var c = Config(); var key = DashboardRules.Key(c.Policies[0]);
        foreach (var gap in new[] { -1, 0, 1, 2 }) Assert.False(DashboardRules.MoveTo(c, key, gap));
        Assert.Equal(key, DashboardRules.Key(Assert.Single(c.Policies)));
    }

    [Fact]
    public void CloneInsertsImmediatelyBelowAndSeparatesTargetAndSchedulesWithoutCopyingGrants()
    {
        var c = Config(); var original = c.Policies[0]; original.Deny = true;
        original.Schedules.Add(new() { Days = [DayOfWeek.Wednesday], Start = new(12, 0), End = new(13, 0) });
        c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now, ExpiresAtUtc = Now.AddHours(1) });
        var clone = DashboardRules.CloneAfter(c, DashboardRules.Key(original));
        Assert.NotNull(clone); Assert.Same(clone, c.Policies[1]); Assert.Same(original, c.Policies[0]);
        Assert.NotEqual(original.DeviceId, clone.DeviceId); Assert.Equal(original.ServiceId, clone.ServiceId); Assert.True(clone.Deny);
        var target = c.Devices.Single(d => d.Id == clone.DeviceId);
        Assert.Equal(c.Devices[0].Address, target.Address); Assert.Contains("コピー", target.Name);
        target.Address = "192.168.10.22"; clone.Schedules[0].Days.Clear();
        Assert.Equal("192.168.10.21", c.Devices[0].Address); Assert.Single(original.Schedules[0].Days);
        Assert.DoesNotContain(c.TemporaryGrants, g => g.DeviceId == clone.DeviceId);
        Assert.Single(c.TemporaryGrants);
    }

    [Fact]
    public async Task BlockOperationUsesExclusiveHorizontalTogglesAndOnlySavesOnConfirm()
    {
        await Sta(async () =>
        {
            var c = Config(); c.Policies[0].Deny = true; Configuration? saved = null;
            using var form = BlockEditor.Create(c, c.Policies[0], candidate => { saved = candidate; return Task.FromResult(true); }, _ => true);
            var controls = Descendants(form).ToList();
            Assert.Empty(controls.OfType<ComboBox>());
            var action = controls.OfType<ToggleChoices>().Single(); Assert.False(action.WrapContents);
            Assert.Equal("block", action.SelectedId);
            var buttons = action.Controls.OfType<RadioButton>().ToList();
            Assert.All(buttons, button => Assert.Equal(Appearance.Button, button.Appearance));
            buttons.Single(button => button.Name == "normal").Checked = true;
            Assert.Equal("normal", action.SelectedId); Assert.Single(buttons, button => button.Checked);
            Assert.Null(saved); Assert.True(c.Policies[0].Deny);
            Assert.True(await form.SaveAsync!()); Assert.False(saved!.Policies[0].Deny); Assert.True(c.Policies[0].Deny);
        });
    }

    [Fact]
    public async Task RuleDragAcceptsOnlyItsOwnLocalTokenAndRejectsBusyOrOldService()
    {
        await Sta(async () =>
        {
            using var form = new MainForm();
            void Set(string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
            bool Accept(IDataObject data) => (bool)typeof(MainForm).GetMethod("IsLocalRuleDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [data])!;
            var token = Guid.NewGuid().ToString("N");
            Set("ruleDragging", true); Set("dragToken", token); Set("connected", true); Set("orderedRules", true);
            var own = new DataObject(); own.SetData("WhiteProxy.LocalRuleOrder", token);
            var foreign = new DataObject(); foreign.SetData("WhiteProxy.LocalRuleOrder", "foreign");
            Assert.True(Accept(own)); Assert.False(Accept(foreign)); Assert.False(Accept(new DataObject(DataFormats.Text, "pc/web")));
            Set("rowActionBusy", true); Assert.False(Accept(own)); Set("rowActionBusy", false);
            Set("orderedRules", false); Assert.False(Accept(own)); Set("orderedRules", true);
            Set("ruleDragging", false); Assert.False(Accept(own));
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    public async Task NewGrantDefaultsToConfiguredHours(int hours)
    {
        await Sta(async () =>
        {
            var c = Config(); c.Proxy.MaximumGrantHours = hours; IpcRequest? sent = null;
            using var form = GrantEditor.Create(c, c.Policies[0], Now, request => { sent = request; return Task.FromResult(true); });
            var end = Descendants(form).OfType<DateTimePicker>().Single(control => control.Name == "GrantEnd");
            var expected = TimeZoneInfo.ConvertTime(Now.AddHours(hours), TimeZoneInfo.FindSystemTimeZoneById(c.Proxy.TimeZoneId)).DateTime;
            Assert.Equal(expected, end.Value);
            Assert.True(form.ValidateInput!()); Assert.True(await form.SaveAsync!());
            Assert.Equal(Now.AddHours(hours), sent!.ExpiresAtUtc); Assert.Null(sent.StartsAtUtc);
        });
    }

    [Fact]
    public async Task ExistingGrantExpiryAndReservationAreNotResetToConfiguredDefault()
    {
        await Sta(async () =>
        {
            foreach (var reserved in new[] { false, true })
            {
                var c = Config(); c.Proxy.MaximumGrantHours = 4;
                var grant = new TemporaryGrant { DeviceId = "pc", ServiceId = "web", StartsAtUtc = reserved ? Now.AddMinutes(30) : Now.AddMinutes(-10), ExpiresAtUtc = Now.AddMinutes(90) };
                c.TemporaryGrants.Add(grant); IpcRequest? sent = null;
                using var form = GrantEditor.Create(c, c.Policies[0], Now, request => { sent = request; return Task.FromResult(true); });
                Assert.True(form.ValidateInput!()); Assert.True(await form.SaveAsync!());
                Assert.Equal(grant.ExpiresAtUtc, sent!.ExpiresAtUtc);
                Assert.Equal(reserved ? grant.StartsAtUtc : (DateTimeOffset?)null, sent.StartsAtUtc);
            }
        });
    }

    [Fact]
    public async Task ClonedRuleCanChangeServiceWithoutChangingOriginalOrOrder()
    {
        await Sta(async () =>
        {
            var c = Config(); c.Services.Add(new() { Id = "other", Name = "更新", Hosts = ["updates.example"] });
            c.Policies[0].Deny = true;
            var clone = DashboardRules.CloneAfter(c, DashboardRules.Key(c.Policies[0]))!;
            clone.Schedules.Add(new() { Days = [DayOfWeek.Wednesday], Start = new(12, 0), End = new(13, 0) });
            Configuration? saved = null;
            using var form = RuleEditor.Create(c, clone, candidate => { saved = candidate; return Task.FromResult(true); }, _ => true);
            var service = Descendants(form).OfType<ComboBox>().Single(control => control.Name == "RuleService");
            Assert.True(service.Enabled); service.SelectedIndex = 1;
            Assert.True(form.ValidateInput!()); Assert.True(await form.SaveAsync!());
            Assert.Equal("web", saved!.Policies[0].ServiceId); Assert.Equal("other", saved.Policies[1].ServiceId);
            Assert.Equal(clone.DeviceId, saved.Policies[1].DeviceId); Assert.True(saved.Policies[1].Deny); Assert.Single(saved.Policies[1].Schedules);
            Assert.Equal("web", c.Policies[1].ServiceId);
        });
    }

    [Fact]
    public void ServiceChangeRejectsDuplicateAndRequiresEndingActiveOrReservedGrants()
    {
        var c = Config(); var rule = c.Policies[0]; c.Services.Add(new() { Id = "other", Hosts = ["other.example"] });
        c.Policies.Add(new() { DeviceId = "pc", ServiceId = "other" });
        Assert.NotNull(RuleEditor.ChangeError(c, rule, "pc", "other", Now)); c.Policies.RemoveAt(1);
        var grant = new TemporaryGrant { DeviceId = "pc", ServiceId = "web", StartsAtUtc = Now.AddMinutes(10), ExpiresAtUtc = Now.AddHours(1) }; c.TemporaryGrants.Add(grant);
        Assert.Contains("一時許可", RuleEditor.ChangeError(c, rule, "pc", "other", Now));
        Assert.Null(RuleEditor.ChangeError(c, rule, "pc", "web", Now));
        grant.Revoked = true; Assert.Null(RuleEditor.ChangeError(c, rule, "pc", "other", Now));
        grant.Revoked = false; grant.ExpiresAtUtc = Now; Assert.Null(RuleEditor.ChangeError(c, rule, "pc", "other", Now));
    }

    [Fact]
    public async Task VersionAndServiceTableUseCurrentPresentation()
    {
        await Sta(async () =>
        {
            using var form = new MainForm();
            Assert.Contains("v" + AppVersion.Current, form.Text);
            var tabs = Descendants(form).OfType<TabControl>().Single();
            var page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "許可サービス");
            Assert.Equal(DataGridViewCellBorderStyle.SingleHorizontal, Descendants(page).OfType<DataGridView>().Single().CellBorderStyle);
            var buttons = Descendants(page).OfType<Button>().ToList();
            Assert.NotEqual(buttons.Single(button => button.Text == "サービスを追加").BackColor, buttons.Single(button => button.Text == "選択サービスを編集").BackColor);
            Assert.True(GuiInstance.IsGuiTitle("WhiteProxy — 管理コンソール（管理者）"));
            Assert.True(GuiInstance.IsGuiTitle("WhiteProxy v0.9.0 — 管理コンソール（管理者）"));
            Assert.False(GuiInstance.IsGuiTitle("WhiteProxy コンソール本体"));
            await Task.CompletedTask;
        });
    }

    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
