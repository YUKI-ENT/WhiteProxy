using WhiteProxy.Admin;

namespace WhiteProxy.Windows.Tests;

public class GuiInstanceTests
{
    [Fact]
    public void OtherGuiCannotAcquireUntilOwnerExits()
    {
        var name = @"Local\WhiteProxy.GuiTest-" + Guid.NewGuid().ToString("N");
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { using var first = new GuiInstance(name); Assert.True(first.Acquired); ready.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { error = ex; ready.Set(); }
        });
        thread.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5))); Assert.Null(error);
            using var second = new GuiInstance(name); Assert.False(second.Acquired);
        }
        finally { release.Set(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
        Assert.Null(error);
        using var next = new GuiInstance(name); Assert.True(next.Acquired);
    }

    [Fact]
    public void CrashedGuiDoesNotPreventNextLaunch()
    {
        var name = @"Local\WhiteProxy.GuiCrash-" + Guid.NewGuid().ToString("N");
        Mutex? abandoned = null;
        var thread = new Thread(() => { abandoned = new Mutex(true, name); }); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        // Join後もネイティブスレッドの終了処理が残る場合があるため、Mutex放棄の完了を待つ。
        try { Assert.True(SpinWait.SpinUntil(() => { using var next = new GuiInstance(name); return next.Acquired; }, TimeSpan.FromSeconds(5))); }
        finally { abandoned?.Dispose(); }
    }
}
