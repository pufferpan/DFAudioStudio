using System;
using Microsoft.UI.Dispatching;

namespace DFAudioStudio.App.ViewModels;

/// <summary>
/// 极小的 UI 线程调度助手：ViewModel 里所有 await 之后的界面/属性更新都从这里走，
/// 保证「先切回 UI 线程再改 UI」这条硬性要求。
/// </summary>
public static class UiDispatch
{
    /// <summary>在构造 ViewModel（UI 线程）时调用一次，捕获当前 DispatcherQueue。</summary>
    public static DispatcherQueue? Capture()
    {
        try { return DispatcherQueue.GetForCurrentThread(); }
        catch { return null; }
    }

    /// <summary>已在 UI 线程则直接执行，否则用 TryEnqueue 排队。</summary>
    public static void Run(DispatcherQueue? queue, Action action)
    {
        if (action is null) return;
        if (queue is null || queue.HasThreadAccess) { action(); return; }
        queue.TryEnqueue(() => action());
    }
}
