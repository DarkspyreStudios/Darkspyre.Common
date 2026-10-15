using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Darkspyre.Common.Avalonia.Tests;

/// <summary>The headless Avalonia session the viewer tests run in, drawing with Skia so pictures can be checked.</summary>
internal static class ViewSession
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(ViewSession));

    /// <summary>Avalonia's entry point for the headless session.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="test"/> on the session's UI thread.</summary>
    public static Task RunAsync(Func<Task> test) => Session.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

    /// <summary>Shows <paramref name="content"/> in a window and lets layout and queued work run.</summary>
    public static Window Show(Control content, double width = 900, double height = 700)
    {
        Window window = new() { Content = content, Width = width, Height = height };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Runs every job queued on the UI thread and one render tick.</summary>
    public static void Settle()
    {
        for (int pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Settles until <paramref name="condition"/> holds, or fails after ten seconds.</summary>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Settle();
            if (condition()) return;
            if (DateTime.UtcNow > end) throw new TimeoutException("The view never reached the expected state.");
            await Task.Delay(10);
        }
    }

    /// <summary>Every control of type <typeparamref name="T"/> under <paramref name="root"/>.</summary>
    public static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private sealed class TestApplication : Application
    {
        public override void Initialize() => this.Styles.Add(new FluentTheme());
    }
}
