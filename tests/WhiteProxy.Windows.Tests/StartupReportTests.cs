using System.Security.AccessControl;
using System.Security.Principal;
using WhiteProxy.Service;
using WhiteProxy.Core;
using System.Net.Sockets;

namespace WhiteProxy.Windows.Tests;

public sealed class StartupReportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WhiteProxy-startup-test-" + Guid.NewGuid().ToString("N"));
    public StartupReportTests() => Directory.CreateDirectory(root);
    [Fact]
    public void StartupFailureIsReadableWithoutIncludingExceptionBody()
    {
        StartupReport.TryWriteFailure(root, "ログ保存先の準備", new UnauthorizedAccessException("SECRET-PATH-OR-QUERY"));
        var report = StartupReport.Read(root);
        Assert.NotNull(report);
        Assert.Equal("Failed", report.State);
        Assert.Equal("UnauthorizedAccessException", report.ErrorType);
        Assert.Contains("アクセスが拒否", report.Message);
        Assert.DoesNotContain("SECRET", File.ReadAllText(StartupReport.FilePath(root)));
        var acl = new FileInfo(StartupReport.FilePath(root)).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        var users = Assert.Single(acl, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        Assert.Equal(AccessControlType.Allow, users.AccessControlType);
        Assert.Equal(FileSystemRights.Read, users.FileSystemRights & ~FileSystemRights.Synchronize);
    }
    [Fact]
    public void ReportMovesFromStartingToReadyWithoutLeavingTemporaryFiles()
    {
        StartupReport.Write(root, "設定読込", "Starting", "");
        StartupReport.Write(root, "管理GUI接続待受", "Ready", "");
        Assert.Equal("Ready", StartupReport.Read(root)?.State);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public void MissingOrMalformedReportDoesNotPreventDiagnostics()
    {
        Assert.Null(StartupReport.Read(root));
        File.WriteAllText(StartupReport.FilePath(root), "invalid");
        Assert.Null(StartupReport.Read(root));
    }
    [Theory]
    [InlineData(10049, "このPC")]
    [InlineData(10048, "既に使用")]
    public void WrappedBindErrorsExplainAddressOrPortWithoutRawExceptionBody(int code, string expected)
    {
        var error = new Exception("SECRET-PATH-OR-QUERY", new SocketException(code));
        var description = StartupReport.DescribeFailure("待受開始", error);
        Assert.Contains(expected, description); Assert.DoesNotContain("SECRET", description);
    }

    [Fact]
    public void UnassignedListenIpHasSpecificSafeStartupDiagnostic()
    {
        var description = StartupReport.DescribeFailure("待受開始", new ListenAddressUnavailableException());
        Assert.Contains("割り当てられていません", description); Assert.Contains("IPv4/CIDR", description);
    }
    public void Dispose()
    {
        // Usersは状態ファイルを読めるだけ。テスト実行者にだけ削除権限を戻して片付ける。
        var file = StartupReport.FilePath(root);
        if (File.Exists(file))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(file).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(acl);
        }
        Directory.Delete(root, true);
    }
}
