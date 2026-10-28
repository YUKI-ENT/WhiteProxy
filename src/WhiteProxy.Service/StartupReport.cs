using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Net.Sockets;
using WhiteProxy.Core;

namespace WhiteProxy.Service;

public sealed record StartupReport(DateTimeOffset TimeUtc, string Stage, string State, string Message,
    string ErrorType, int HResult, int ProcessId, string FailureLocation = "")
{
    public static string FilePath(string root) => Path.Combine(root, "service-status.json");
    public static StartupReport? Read(string root)
    {
        try
        {
            var path = FilePath(root);
            if (new FileInfo(path).Length > 16384) return null;
            return JsonSerializer.Deserialize<StartupReport>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Write(string root, string stage, string state, string message, Exception? exception = null)
    {
        // 通信内容・設定内容・例外本文は含めない。起動状態だけを一般ユーザーにも読取り許可する。
        var report = new StartupReport(DateTimeOffset.UtcNow, stage, state, message,
            exception?.GetType().Name ?? "", exception?.HResult ?? 0, Environment.ProcessId, FailureMethod(exception));
        var path = FilePath(root); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(report));
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.NetworkServiceSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(temp).SetAccessControl(acl);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void TryWriteFailure(string root, string stage, Exception exception)
    {
        try
        {
            var method = FailureMethod(exception);
            Write(root, stage, "Failed", DescribeFailure(stage, exception) + (method.Length == 0 ? "" : " 発生箇所: " + method), exception);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
    private static string FailureMethod(Exception? exception)
    {
        if (exception is null) return "";
        // ソースパスや例外本文を公開せず、切り分けに必要なメソッド名だけを残す。
        var method = new System.Diagnostics.StackTrace(exception, false).GetFrame(0)?.GetMethod();
        return method is null ? "" : method.DeclaringType?.FullName + "." + method.Name;
    }
    internal static string DescribeFailure(string stage, Exception exception)
    {
        var cause = exception.GetBaseException();
        if (exception is ListenAddressUnavailableException || cause is ListenAddressUnavailableException || cause is SocketException { SocketErrorCode: SocketError.AddressNotAvailable })
            return $"{stage}: {ListenAddresses.UnavailableMessage}";
        if (cause is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            return $"{stage}: 待受ポートは既に使用されています。別のプロキシー本体・コンソール・アプリの起動を確認してください。";
        return exception switch
        {
            UnauthorizedAccessException => $"{stage}でアクセスが拒否されました。サービスの実行アカウントと保存先ACLを確認し、「サービスのプログラムを更新」を実行してください。",
            SocketException => $"{stage}に失敗しました。待受IP・ポートの競合、コンソール本体の二重起動を確認してください。",
            _ => $"{stage}に失敗しました（{exception.GetType().Name}, 0x{exception.HResult:X8}）。「設定・サービス」の状態と診断情報を確認してください。"
        };
    }
}
