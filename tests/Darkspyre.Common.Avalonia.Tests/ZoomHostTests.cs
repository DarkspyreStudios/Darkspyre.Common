using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

/// <summary>Tests for <see cref="ZoomHost"/> and its shared <see cref="ZoomScale"/>.</summary>
public sealed class ZoomHostTests
{
    /// <summary>
    /// Test 6: Ctrl+wheel steps 0.1 and clamps at 1.0 and 3.0; plain wheel does nothing;
    /// a new ZoomHost on the same ZoomScale after detach shows the kept value.
    /// </summary>
    [Fact]
    public Task SteppingClampingPlainWheelAndRemount() => ViewSession.RunAsync(() =>
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

        // Step to 1.5 before detaching.
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
        AssertScale(host2, 1.5);

        // Mount host2 in the window and verify kept scale is preserved.
        window.Content = host2;
        ViewSession.Settle();
        AssertScale(host2, 1.5);

        // While mounted, changing the shared scale updates host2.
        scale.Value = 2.0;
        ViewSession.Settle();
        AssertScale(host2, 2.0);

        window.Close();
        return Task.CompletedTask;
    });

    private static void AssertScale(ZoomHost host, double expected)
    {
        ScaleTransform transform = Assert.IsType<ScaleTransform>(host.LayoutTransform);
        Assert.Equal(expected, transform.ScaleX, precision: 3);
        Assert.Equal(expected, transform.ScaleY, precision: 3);
    }
}
