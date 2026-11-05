using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class ZoomHostTests
{
    /// <summary>
    /// Test 6: Ctrl+wheel steps 0.1 and clamps at 1.0 and 3.0; plain wheel does nothing;
    /// a new ZoomHost on the same ZoomScale after detach shows the kept value.
    /// </summary>
    [Fact]
    public Task SteppingClampingPlainWheelAndRemount() => ViewSession.RunAsync(async () =>
    {
        ZoomScale scale = new();
        Border child1 = new() { Width = 200, Height = 200, Background = Brushes.Transparent };
        ZoomHost host1 = new(scale) { Width = 200, Height = 200, Child = child1 };
        Window window = ViewSession.Show(host1, 300, 300);
        Point point = new(50, 50);

        // Initial scale factor is 1.0.
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        // Plain wheel does nothing: scale and transform are unchanged.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        // Non-Ctrl modifiers (Alt, Shift) also do nothing.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Alt);
        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        // Ctrl+wheel up steps by 0.1 per notch.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        ViewSession.Settle();
        Assert.Equal(1.1, scale.Value);
        AssertScale(host1, 1.1);

        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        ViewSession.Settle();
        Assert.Equal(1.2, scale.Value);
        AssertScale(host1, 1.2);

        // Stepping up multiple times clamps at 3.0.
        for (int i = 0; i < 25; i++)
        {
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        }

        ViewSession.Settle();
        Assert.Equal(3.0, scale.Value);
        AssertScale(host1, 3.0);

        // Further Ctrl+wheel up remains clamped at 3.0.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        ViewSession.Settle();
        Assert.Equal(3.0, scale.Value);
        AssertScale(host1, 3.0);

        // Ctrl+wheel down steps down 0.1 per notch.
        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
        ViewSession.Settle();
        Assert.Equal(2.9, scale.Value);
        AssertScale(host1, 2.9);

        // Stepping down multiple times clamps at 1.0.
        for (int i = 0; i < 25; i++)
        {
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
        }

        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        // Further Ctrl+wheel down remains clamped at 1.0.
        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
        ViewSession.Settle();
        Assert.Equal(1.0, scale.Value);
        AssertScale(host1, 1.0);

        // Step to an intermediate value (1.5) before detaching.
        for (int i = 0; i < 5; i++)
        {
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        }

        ViewSession.Settle();
        Assert.Equal(1.5, scale.Value);
        AssertScale(host1, 1.5);

        // Detach host1 from the visual tree.
        window.Content = null;
        ViewSession.Settle();

        // A new ZoomHost on the same ZoomScale inherits the kept value.
        Border child2 = new() { Width = 200, Height = 200, Background = Brushes.Transparent };
        ZoomHost host2 = new(scale) { Width = 200, Height = 200, Child = child2 };
        Assert.Equal(1.5, host2.Scale.Value);
        AssertScale(host2, 1.5);

        // Mount host2 in the window.
        window.Content = host2;
        ViewSession.Settle();
        AssertScale(host2, 1.5);

        // While mounted, changing the shared scale updates host2.
        scale.Value = 2.0;
        ViewSession.Settle();
        AssertScale(host2, 2.0);

        // Detach host2, change scale to 2.5 while detached, and verify reattach reads latest.
        window.Content = null;
        ViewSession.Settle();
        scale.Value = 2.5;
        window.Content = host2;
        ViewSession.Settle();
        AssertScale(host2, 2.5);

        // Changed event only fires when value actually changes.
        int changeCount = 0;
        scale.Changed += () => changeCount++;
        scale.Value = 2.5;
        Assert.Equal(0, changeCount);
        scale.Value = 4.0;
        Assert.Equal(1, changeCount);
        Assert.Equal(3.0, scale.Value);
        scale.Value = 3.0;
        Assert.Equal(1, changeCount);
        scale.Value = 0.5;
        Assert.Equal(2, changeCount);
        Assert.Equal(1.0, scale.Value);
        scale.Value = 1.0;
        Assert.Equal(2, changeCount);

        window.Content = null;
        ViewSession.Settle();
        window.Close();

        // Verify detached host is not retained by the shared scale.
        WeakReference weakHost = MountAndDetach(scale);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weakHost.IsAlive);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MountAndDetach(ZoomScale scale)
    {
        ZoomHost host = new(scale) { Width = 50, Height = 50 };
        Window tempWindow = ViewSession.Show(host, 100, 100);
        tempWindow.Content = null;
        ViewSession.Settle();
        tempWindow.Close();
        return new WeakReference(host);
    }

    private static void AssertScale(ZoomHost host, double expected)
    {
        ScaleTransform transform = Assert.IsType<ScaleTransform>(host.LayoutTransform);
        Assert.Equal(expected, transform.ScaleX, precision: 3);
        Assert.Equal(expected, transform.ScaleY, precision: 3);
    }
}
