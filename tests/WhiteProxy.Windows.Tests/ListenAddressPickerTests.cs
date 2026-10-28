using System.Net;
using WhiteProxy.Admin;
using WhiteProxy.Core;

namespace WhiteProxy.Windows.Tests;

public class ListenAddressPickerTests
{
    private static List<Choice> Options() => [new("127.0.0.1", "このPC内だけ — 127.0.0.1"),
        new("192.168.200.111", "192.168.200.111 — イーサネット"), new("0.0.0.0", "すべてのNIC — 0.0.0.0")];

    [Fact]
    public void PickerUsesLabelsButStoresOnlySelectedIpAndDoesNotAllowTyping()
    {
        Sta(() =>
        {
            using var picker = ListenAddressPicker.Create("192.168.200.111", Options());
            Assert.Equal(ComboBoxStyle.DropDownList, picker.DropDownStyle);
            Assert.Equal("192.168.200.111", Assert.IsType<Choice>(picker.SelectedItem).Id);
            Assert.Contains("イーサネット", picker.Text);
        });
    }

    [Fact]
    public void MissingSavedIpDoesNotSilentlySwitchToAnyOrLoopback()
    {
        Sta(() =>
        {
            var options = Options(); using var picker = ListenAddressPicker.Create("192.168.0.0", options);
            var selected = Assert.IsType<Choice>(picker.SelectedItem);
            Assert.Equal("192.168.0.0", selected.Id); Assert.Contains("未割当", selected.Name);
            Assert.Contains("選び直", ListenAddressPicker.Hint(selected.Id, options));
            Assert.NotNull(ListenAddresses.Validate(selected.Id, [IPAddress.Parse("192.168.200.111"), IPAddress.Loopback]));
        });
    }

    [Fact]
    public void SpecialTargetsExplainLocalOnlyAndSourceFiltering()
    {
        Sta(() =>
        {
            using var picker = ListenAddressPicker.Create("0.0.0.0", Options());
            Assert.Equal("0.0.0.0", Assert.IsType<Choice>(picker.SelectedItem).Id);
            Assert.Contains("許可ルール", ListenAddressPicker.Hint("0.0.0.0", Options()));
            Assert.Contains("LAN側の端末からは接続できません", ListenAddressPicker.Hint("127.0.0.1", Options()));
        });
    }

    [Fact]
    public void CurrentChoicesHaveUniquePlainIpv4KeysAndVisibleAddresses()
    {
        var options = ListenAddressPicker.CurrentOptions();
        Assert.Equal(options.Count, options.Select(o => o.Id).Distinct().Count());
        Assert.All(options, choice => { Assert.True(IPAddress.TryParse(choice.Id, out _)); Assert.Contains(choice.Id, choice.Name); });
        Assert.Contains(options, choice => choice.Id == "127.0.0.1"); Assert.Contains(options, choice => choice.Id == "0.0.0.0");
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
