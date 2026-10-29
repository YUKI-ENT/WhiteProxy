using System.Xml.Linq;
using WhiteProxy.Admin;

namespace WhiteProxy.Windows.Tests;

public class TrayAutostartTests
{
    [Fact]
    public void LogonTaskUsesInteractiveElevatedUserAndNoBatteryOrRuntimeLimit()
    {
        var path = @"C:\Program Files\A & B\WhiteProxy.exe";
        var document = XDocument.Parse(TrayAutostart.TaskXml(path, "S-1-5-21-123"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => Assert.Single(document.Descendants(ns + name)).Value;
        Assert.Equal(path, Value("Command"));
        Assert.Equal("--tray", Value("Arguments"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("PT15S", Value("Delay"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.All(document.Descendants(ns + "UserId"), id => Assert.Equal("S-1-5-21-123", id.Value));
        Assert.DoesNotContain("Password", document.ToString());
    }
}
