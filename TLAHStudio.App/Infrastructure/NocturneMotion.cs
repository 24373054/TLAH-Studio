using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace TLAHStudio.App.Infrastructure;

/// <summary>
/// Compositor-only feedback for interactive surfaces. These helpers never animate
/// Width, Height, GridLength, or Visual.Offset, so motion cannot disturb layout or
/// leave a reopened workbench pane in the wrong column.
/// </summary>
internal static class NocturneMotion
{
    private static readonly ConditionalWeakTable<ButtonBase, object> PressTargets = new();

    public static void AttachPressFeedback(ButtonBase button)
    {
        if (!PressTargets.TryAdd(button, new object()))
            return;

        button.Loaded += (_, _) => Reset(button);
        button.PointerPressed += (_, _) => AnimateScale(button, 0.975f, 70);
        button.PointerReleased += (_, _) => AnimateScale(button, 1f, 120);
        button.PointerCanceled += (_, _) => AnimateScale(button, 1f, 120);
        button.PointerCaptureLost += (_, _) => AnimateScale(button, 1f, 120);
        button.PointerExited += (_, _) => AnimateScale(button, 1f, 120);
    }

    public static void RevealOverlay(UIElement element, int durationMs = 190)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        SetCenterPoint(element, visual);
        visual.StopAnimation(nameof(visual.Opacity));
        visual.StopAnimation(nameof(visual.Scale));

        if (!AnimationsEnabled())
        {
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            return;
        }

        var compositor = visual.Compositor;
        var easing = StandardEasing(compositor);
        visual.Opacity = 0;
        visual.Scale = new Vector3(0.985f, 0.985f, 1f);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(durationMs);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation(nameof(visual.Opacity), opacity);
        visual.StartAnimation(nameof(visual.Scale), scale);
    }

    public static void CrossFadeIn(UIElement element, int durationMs = 150)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(visual.Opacity));

        if (!AnimationsEnabled())
        {
            visual.Opacity = 1;
            return;
        }

        visual.Opacity = 0;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, 1f, StandardEasing(visual.Compositor));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    public static void StartBreathing(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(visual.Opacity));

        if (!AnimationsEnabled())
        {
            visual.Opacity = 1;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0.66f);
        animation.InsertKeyFrame(0.5f, 1f);
        animation.InsertKeyFrame(1f, 0.66f);
        animation.Duration = TimeSpan.FromMilliseconds(1800);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    public static void StopBreathing(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(visual.Opacity));
        visual.Opacity = 1;
    }

    public static void Reset(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(visual.Opacity));
        visual.StopAnimation(nameof(visual.Scale));
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }

    private static void AnimateScale(UIElement element, float target, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        SetCenterPoint(element, visual);

        if (!AnimationsEnabled())
        {
            visual.Scale = Vector3.One;
            return;
        }

        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(target, target, 1f), StandardEasing(visual.Compositor));
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(visual.Scale), animation);
    }

    private static void SetCenterPoint(UIElement element, Visual visual)
    {
        if (element is FrameworkElement frameworkElement)
        {
            visual.CenterPoint = new Vector3(
                (float)Math.Max(0, frameworkElement.ActualWidth / 2),
                (float)Math.Max(0, frameworkElement.ActualHeight / 2),
                0);
        }
    }

    private static CubicBezierEasingFunction StandardEasing(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

    private static bool AnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}
