using Microsoft.Extensions.Logging;

namespace WhiteProxy.Proxy;

/// <summary>メッセージ・例外の本文や構造化パラメーターは出力せず、認証情報とURLの漏洩を防ぐ。</summary>
internal sealed class SafeLoggerFactory(Action<int, string> write) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new SafeLogger(write);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
    private sealed class SafeLogger(Action<int, string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) write(eventId.Id, exception?.GetType().Name ?? "warning");
        }
    }
}
