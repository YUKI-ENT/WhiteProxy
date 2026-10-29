using System.IO.Pipes;
using System.Text.Json;

namespace WhiteProxy.Service;

/// <summary>クライアントのEOF・途中切断を待受自体の障害から分離する。</summary>
internal sealed class NamedPipeListener : IDisposable
{
    private readonly Func<NamedPipeServerStream> create;
    private NamedPipeServerStream pipe;
    internal NamedPipeListener(Func<NamedPipeServerStream> create)
    {
        this.create = create;
        pipe = create(); // 初回のACL・名前競合は起動時点で検出する。
    }

    internal async Task RunAsync(Func<NamedPipeServerStream, CancellationToken, Task> handle,
        Action<Exception> rejected, CancellationToken stop, TimeSpan? requestTimeout = null)
    {
        while (!stop.IsCancellationRequested)
        {
            bool accepted = false, replace = false;
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                accepted = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(10));
                await handle(pipe, timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                rejected(ex);
                // 接続待ち前に相手が切断した場合は、新しい待受ハンドルに交換する。
                replace = !accepted;
            }
            finally
            {
                if (accepted)
                {
                    // EOFを読むとIsConnectedはfalseになるが、内部状態はBrokenのまま。
                    // IsConnectedで条件分岐せず、必ずDisconnectedへリセットする。
                    try { pipe.Disconnect(); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    { rejected(ex); replace = true; }
                }
            }
            if (replace && !stop.IsCancellationRequested)
            {
                pipe.Dispose();
                pipe = create();
            }
        }
    }
    public void Dispose() => pipe.Dispose();
}
