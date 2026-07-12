using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace TLAHStudio.App.Infrastructure;

/// <summary>
/// Small, reusable composition motions for the workbench. They are intentionally
/// limited to reveal and state feedback so layout, reading, and scrolling never
/// compete with decorative animation.
/// </summary>
internal static class NocturneMotion
{
    public static void Reveal(UIElement element, float verticalOffset = 10, int durationMs = 220)
    {
        if (!AnimationsEnabled())
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.Opacity = 0;
        visual.Offset = new Vector3(0, verticalOffset, 0);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(durationMs);

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(0, verticalOffset, 0));
        offset.InsertKeyFrame(1f, Vector3.Zero);
        offset.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Offset", offset);
    }

    public static void StartBreathing(UIElement element)
    {
        if (!AnimationsEnabled())
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0.66f);
        animation.InsertKeyFrame(0.5f, 1f);
        animation.InsertKeyFrame(1f, 0.66f);
        animation.Duration = TimeSpan.FromMilliseconds(1800);
        animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", animation);
    }

    public static void StopBreathing(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }

    private static bool AnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            // Fall back to native transitions when the setting is unavailable.
            return true;
        }
    }
}
