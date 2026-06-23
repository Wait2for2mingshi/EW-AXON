using System;
using System.Windows.Threading;

/// <summary>
/// UI 线程的节流合并器：在指定间隔内只触发一次 flush，避免频繁刷新造成阻塞。
/// </summary>
internal sealed class UiCoalescer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly Action _flush;
    private DispatcherTimer _timer;
    private bool _pending;

    /// <summary>
    /// 创建合并器，flush 必须在 UI 线程可执行，interval 为节流时间。
    /// </summary>
    public UiCoalescer(Dispatcher dispatcher, TimeSpan interval, Action flush)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _flush = flush;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval
        };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            _pending = false;
            try { _flush(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UiCoalescer] flush error: " + ex); }

        };
    }

    /// <summary>
    /// 请求一次 flush，若在间隔内已存在待执行的 flush 则忽略。
    /// </summary>
    public void Request()
    {
        if (_pending) return;
        _pending = true;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>停止计时器，不再触发 flush。</summary>
    public void Dispose() => _timer?.Stop();
}
