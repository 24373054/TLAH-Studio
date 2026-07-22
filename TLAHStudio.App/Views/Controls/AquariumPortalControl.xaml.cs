using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using Windows.Foundation;
using Windows.System.Power;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TLAHStudio.App.Views.Controls;

public sealed partial class AquariumPortalControl : UserControl
{
    private const float DesignWidth = 276f;
    private const float DesignHeight = 128f;

    private readonly List<FishActor> _fish = [];
    private readonly List<AmbientActor> _bubbleStreams = [];
    private readonly List<AmbientActor> _particleFields = [];
    private readonly Dictionary<string, LoadedImageSurface> _imageSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private ContainerVisual? _sceneRoot;
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibilitySettings;
    private IAquariumPreferencesService? _preferences;
    private SceneProfile _profile = SceneProfile.Balanced;
    private AquariumQuality _quality = AquariumQuality.Auto;
    private bool _loaded;
    private bool _hostActive = true;
    private bool _windowActive = true;
    private bool _userPaused;
    private bool _animationsRunning;
    private bool _powerSubscribed;

    public AquariumPortalControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public void SetHostActive(bool active)
    {
        if (_hostActive == active)
            return;

        _hostActive = active;
        if (!active)
            ResetInteractionVisuals();
        UpdateAnimationState();
    }

    public void BindPreferences(IAquariumPreferencesService preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (ReferenceEquals(_preferences, preferences))
            return;

        UnsubscribePreferences();
        _preferences = preferences;
        _quality = preferences.CurrentQuality;
        _profile = ResolveProfile(_quality);
        _userPaused = preferences.IsPaused;
        AquariumButton.IsChecked = !_userPaused;

        if (_loaded)
        {
            SubscribePreferences();
            RebuildScene();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        _quality = _preferences?.CurrentQuality ?? AquariumQuality.Auto;
        _profile = ResolveProfile(_quality);
        _userPaused = _preferences?.IsPaused ?? _userPaused;
        BuildScene();
        ResizeScene();
        ApplyStaticScene();
        AquariumButton.IsChecked = !_userPaused;
        _windowActive = App.MainWindow is not MainWindow window || window.IsWindowActive;
        SubscribePreferences();
        SubscribeEnvironment();
        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        _loaded = false;
        UnsubscribePreferences();
        UnsubscribeEnvironment();
        StopAnimations(applyPoster: false);
        ResetInteractionVisuals();
        ClearScene();
    }

    private void BuildScene()
    {
        if (_sceneRoot != null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(SceneHost);
        var compositor = hostVisual.Compositor;
        _sceneRoot = compositor.CreateContainerVisual();
        _sceneRoot.Size = new Vector2(DesignWidth, DesignHeight);
        ElementCompositionPreview.SetElementChildVisual(SceneHost, _sceneRoot);

        BuildParticleFields(compositor);
        BuildFish(compositor);
        BuildBubbleStreams(compositor);
    }

    private void BuildFish(Compositor compositor)
    {
        var specs = new[]
        {
            new FishSpec("goldfish-orange-white.png", 49, 27, 0.22f, 41, 0.0, 0.98f, 4.2f, 4151, 1, true),
            new FishSpec("tetra-silver-cyan.png", 35, 18, 0.39f, 34, 1.1, 0.82f, 3.1f, 4152, 3, false),
            new FishSpec("tropical-fish-coral-orange.png", 39, 22, 0.57f, 46, 2.0, 0.90f, 4.4f, 4153, 6, true),
            new FishSpec("angelfish-deep-blue.png", 35, 35, 0.70f, 53, 0.7, 0.78f, 2.8f, 4154, 8, false),
            new FishSpec("tetra-silver-cyan.png", 25, 13, 0.31f, 29, 2.8, 0.62f, 2.4f, 4155, 2, false),
            new FishSpec("tropical-fish-coral-orange.png", 27, 15, 0.51f, 37, 3.6, 0.68f, 2.7f, 4156, 7, true)
        };

        foreach (var spec in specs.Take(_profile.FishCount))
        {
            var surface = GetOrCreateSurface(spec);
            var brush = compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Uniform;

            var root = compositor.CreateContainerVisual();
            root.Size = new Vector2(spec.Width, spec.Height);
            root.CenterPoint = new Vector3(spec.Width / 2f, spec.Height / 2f, 0);
            root.Opacity = spec.Opacity;

            // The source fish face right. Two overlapping clipped sprites let the
            // tail articulate around its root without adding more decoded images.
            var tail = compositor.CreateSpriteVisual();
            tail.Size = root.Size;
            tail.Brush = brush;
            tail.CenterPoint = new Vector3(spec.Width * 0.36f, spec.Height * 0.52f, 0);
            tail.RotationAxis = Vector3.UnitZ;
            tail.Clip = compositor.CreateInsetClip(0, 0, spec.Width * 0.58f, 0);

            var body = compositor.CreateSpriteVisual();
            body.Size = root.Size;
            body.Brush = brush;
            body.CenterPoint = new Vector3(spec.Width * 0.58f, spec.Height * 0.52f, 0);
            body.Clip = compositor.CreateInsetClip(spec.Width * 0.30f, 0, 0, 0);

            root.Children.InsertAtTop(tail);
            root.Children.InsertAtTop(body);
            _sceneRoot!.Children.InsertAtTop(root);
            _fish.Add(new FishActor(root, body, tail, spec));
        }
    }

    private LoadedImageSurface GetOrCreateSurface(FishSpec spec)
    {
        if (_imageSurfaces.TryGetValue(spec.Asset, out var existing))
            return existing;

        var desiredSize = new Size(spec.Width * 4, spec.Height * 4);
        var surface = LoadedImageSurface.StartLoadFromUri(
            new Uri($"ms-appx:///Assets/Aquarium/Fish/{spec.Asset}"),
            desiredSize);
        _imageSurfaces[spec.Asset] = surface;
        return surface;
    }

    private void BuildParticleFields(Compositor compositor)
    {
        var random = new Random(4157);
        for (var fieldIndex = 0; fieldIndex < _profile.ParticleFieldCount; fieldIndex++)
        {
            var field = compositor.CreateShapeVisual();
            field.Size = new Vector2(DesignWidth, DesignHeight + 24);
            for (var index = 0; index < _profile.ParticleCount; index++)
            {
                var radius = 0.32f + (float)random.NextDouble() * 0.72f;
                var geometry = compositor.CreateEllipseGeometry();
                geometry.Center = new Vector2(
                    4 + (float)random.NextDouble() * (DesignWidth - 8),
                    3 + (float)random.NextDouble() * (DesignHeight + 18));
                geometry.Radius = new Vector2(radius, radius);
                var mote = compositor.CreateSpriteShape(geometry);
                mote.FillBrush = compositor.CreateColorBrush(Color.FromArgb(
                    (byte)(24 + random.Next(22)), 210, 241, 248));
                field.Shapes.Add(mote);
            }

            field.Opacity = fieldIndex == 0 ? 0.54f : 0.34f;
            _sceneRoot!.Children.InsertAtTop(field);
            _particleFields.Add(new AmbientActor(
                field,
                18 + fieldIndex * 7,
                fieldIndex * 5.5,
                fieldIndex == 0 ? 2.2f : -1.7f,
                field.Opacity));
        }
    }

    private void BuildBubbleStreams(Compositor compositor)
    {
        var random = new Random(4158);
        var sources = new[] { 0.12f, 0.18f, 0.70f, 0.78f, 0.86f };
        for (var streamIndex = 0; streamIndex < _profile.BubbleFieldCount; streamIndex++)
        {
            var stream = compositor.CreateShapeVisual();
            stream.Size = new Vector2(DesignWidth, DesignHeight * 2);
            for (var index = 0; index < _profile.BubbleCount; index++)
            {
                var diameter = 1.5f + (float)random.NextDouble() * 3.4f;
                var source = sources[index % sources.Length];
                var center = new Vector2(
                    DesignWidth * source + ((float)random.NextDouble() - 0.5f) * 12,
                    3 + (float)random.NextDouble() * (DesignHeight - 6));
                AddBubbleShape(compositor, stream, center, diameter);
                AddBubbleShape(
                    compositor,
                    stream,
                    new Vector2(center.X, center.Y + DesignHeight),
                    diameter);
            }

            stream.Opacity = 0.66f;
            _sceneRoot!.Children.InsertAtTop(stream);
            _bubbleStreams.Add(new AmbientActor(
                stream,
                8.4 + streamIndex * 1.35,
                0,
                4.2f,
                0.66f));
        }
    }

    private static void AddBubbleShape(
        Compositor compositor,
        ShapeVisual stream,
        Vector2 center,
        float diameter)
    {
        var geometry = compositor.CreateEllipseGeometry();
        geometry.Center = center;
        geometry.Radius = new Vector2(diameter / 2f, diameter / 2f);
        var bubble = compositor.CreateSpriteShape(geometry);
        bubble.FillBrush = compositor.CreateColorBrush(Color.FromArgb(18, 220, 248, 255));
        bubble.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(118, 226, 251, 255));
        bubble.StrokeThickness = 0.52f;
        stream.Shapes.Add(bubble);
    }

    private void ClearScene()
    {
        ElementCompositionPreview.SetElementChildVisual(SceneHost, null);
        _sceneRoot = null;
        _fish.Clear();
        _bubbleStreams.Clear();
        _particleFields.Clear();
        foreach (var surface in _imageSurfaces.Values)
            surface.Dispose();
        _imageSurfaces.Clear();
    }

    private void RebuildScene()
    {
        if (!_loaded)
            return;

        StopAnimations(applyPoster: false);
        ClearScene();
        BuildScene();
        ResizeScene();
        ApplyStaticScene();
        AquariumButton.IsChecked = !_userPaused;
        UpdateAnimationState();
    }

    private void SubscribePreferences()
    {
        if (_preferences != null)
            _preferences.PreferencesChanged -= OnPreferencesChanged;
        if (_preferences != null)
            _preferences.PreferencesChanged += OnPreferencesChanged;
    }

    private void UnsubscribePreferences()
    {
        if (_preferences != null)
            _preferences.PreferencesChanged -= OnPreferencesChanged;
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_preferences == null)
                return;

            var nextQuality = _preferences.CurrentQuality;
            var nextPaused = _preferences.IsPaused;
            var qualityChanged = nextQuality != _quality;
            _quality = nextQuality;
            _profile = ResolveProfile(nextQuality);
            _userPaused = nextPaused;
            AquariumButton.IsChecked = !nextPaused;

            if (qualityChanged)
                RebuildScene();
            else
                UpdateAnimationState();
        });

    private static SceneProfile ResolveProfile(AquariumQuality quality) => quality switch
    {
        AquariumQuality.Eco => SceneProfile.Eco,
        AquariumQuality.High => SceneProfile.High,
        AquariumQuality.Balanced => SceneProfile.Balanced,
        _ => SceneProfile.Balanced
    };

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loaded && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            ResizeScene();
    }

    private void ResizeScene()
    {
        if (_sceneRoot == null)
            return;

        var width = (float)Math.Max(1, SceneHost.ActualWidth);
        var height = (float)Math.Max(1, SceneHost.ActualHeight);
        _sceneRoot.Scale = new Vector3(width / DesignWidth, height / DesignHeight, 1);
    }

    private void SubscribeEnvironment()
    {
        try
        {
            _uiSettings = new UISettings();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        }
        catch
        {
            _uiSettings = null;
        }

        try
        {
            _accessibilitySettings = new AccessibilitySettings();
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
        }
        catch
        {
            _accessibilitySettings = null;
        }

        if (App.MainWindow is MainWindow window)
            window.Activated += OnWindowActivated;

        try
        {
            PowerManager.EnergySaverStatusChanged += OnEnergySaverStatusChanged;
            _powerSubscribed = true;
        }
        catch
        {
            _powerSubscribed = false;
        }
    }

    private void UnsubscribeEnvironment()
    {
        if (_uiSettings != null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        if (_accessibilitySettings != null)
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
        if (App.MainWindow is MainWindow window)
            window.Activated -= OnWindowActivated;
        if (_powerSubscribed)
        {
            try { PowerManager.EnergySaverStatusChanged -= OnEnergySaverStatusChanged; }
            catch { }
            _powerSubscribed = false;
        }

        _uiSettings = null;
        _accessibilitySettings = null;
    }

    private void AquariumButton_Click(object sender, RoutedEventArgs e)
    {
        _userPaused = AquariumButton.IsChecked != true;
        if (_preferences != null)
            _preferences.SetPaused(_userPaused);
        else
            UpdateAnimationState();
    }

    private void AquariumButton_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        AnimateInteractionSheen(0.12f, 150);

    private void AquariumButton_PointerExited(object sender, PointerRoutedEventArgs e) =>
        AnimateInteractionSheen(0, 190);

    private void AquariumButton_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        AnimateInteractionSheen(0.20f, 90);

    private void AquariumButton_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        AnimateInteractionSheen(AquariumButton.IsPointerOver ? 0.12f : 0, 130);

    private void AquariumButton_GotFocus(object sender, RoutedEventArgs e) =>
        AnimateFocusRing(1, 140);

    private void AquariumButton_LostFocus(object sender, RoutedEventArgs e) =>
        AnimateFocusRing(0, 180);

    private void AnimateInteractionSheen(float targetOpacity, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(InteractionSheen);
        if (HighContrastEnabled() || EnergySaverEnabled())
        {
            visual.StopAnimation(nameof(visual.Opacity));
            visual.Opacity = 0;
            return;
        }
        if (!AnimationsEnabled())
        {
            visual.Opacity = targetOpacity;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    private void AnimateFocusRing(float targetOpacity, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(FocusRing);
        if (!AnimationsEnabled() || HighContrastEnabled() || EnergySaverEnabled())
        {
            visual.StopAnimation(nameof(visual.Opacity));
            visual.Opacity = targetOpacity;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    private void ResetInteractionVisuals()
    {
        foreach (var element in new[] { InteractionSheen, FocusRing })
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Opacity));
            visual.Opacity = 0;
        }
    }

    private void UpdateAccessibleState(bool shouldAnimate)
    {
        var action = _userPaused ? "resume" : "pause";
        var state = shouldAnimate
            ? "playing"
            : _userPaused
                ? "paused"
                : !AnimationsEnabled()
                    ? "static because Windows animations are disabled"
                    : HighContrastEnabled()
                        ? "static in high contrast"
                        : EnergySaverEnabled()
                            ? "static in energy saver"
                            : !_hostActive
                                ? "idle while the sidebar is hidden"
                                : "idle while the window is inactive";
        AutomationProperties.SetName(AquariumButton, $"Living aquarium, {state}. Activate to {action}.");
        AutomationProperties.SetItemStatus(AquariumButton, state);
        ToolTipService.SetToolTip(AquariumButton, $"Living aquarium · click to {action}");
        AutomationProperties.SetHelpText(
            AquariumButton,
            $"Animated decorative aquarium. Press Enter or Space to {action}.");
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateAnimationState);

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateAnimationState);

    private void OnEnergySaverStatusChanged(object? sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateAnimationState);

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (!_loaded)
            return;

        var shouldAnimate = _hostActive && _windowActive && !_userPaused &&
                            AnimationsEnabled() && !HighContrastEnabled() && !EnergySaverEnabled();
        if (HighContrastEnabled() || EnergySaverEnabled())
            AnimateInteractionSheen(0, 0);
        if (shouldAnimate)
        {
            if (!_animationsRunning)
                StartAnimations();
        }
        else if (_animationsRunning)
        {
            StopAnimations(applyPoster: true);
        }
        UpdateAccessibleState(shouldAnimate);
    }

    private bool AnimationsEnabled()
    {
        try { return _uiSettings?.AnimationsEnabled ?? true; }
        catch { return true; }
    }

    private bool HighContrastEnabled()
    {
        try { return _accessibilitySettings?.HighContrast ?? false; }
        catch { return false; }
    }

    private static bool EnergySaverEnabled()
    {
        try { return PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
        catch { return false; }
    }

    private void StartAnimations()
    {
        StopAnimations(applyPoster: false);
        foreach (var actor in _fish)
            StartFishAnimation(actor, _profile.AnimateDepthOpacity);
        foreach (var stream in _bubbleStreams)
            StartBubbleAnimation(stream);
        foreach (var field in _particleFields)
            StartParticleAnimation(field);
        StartAmbientAnimation();
        _animationsRunning = true;
    }

    private static void StartFishAnimation(FishActor actor, bool animateDepthOpacity)
    {
        var route = CreateRoute(actor.Spec);
        var compositor = actor.Root.Compositor;
        if (!animateDepthOpacity)
            actor.Root.Opacity = actor.Spec.Opacity;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.32f, 0.02f),
            new Vector2(0.68f, 0.98f));
        var totalSeconds = actor.Spec.DurationSeconds + actor.Spec.DelaySeconds;
        var begin = (float)(actor.Spec.DelaySeconds / totalSeconds);
        var travel = 1f - begin;

        var offset = compositor.CreateVector3KeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        var rotation = compositor.CreateScalarKeyFrameAnimation();
        ScalarKeyFrameAnimation? opacity = animateDepthOpacity
            ? compositor.CreateScalarKeyFrameAnimation()
            : null;
        var first = route[0];
        offset.InsertKeyFrame(0, first.Offset);
        scale.InsertKeyFrame(0, new Vector3(first.Direction * first.Depth, first.Depth, 1));
        rotation.InsertKeyFrame(0, first.Heading);
        opacity?.InsertKeyFrame(0, actor.Spec.Opacity * first.OpacityFactor);
        if (begin > 0)
        {
            offset.InsertKeyFrame(begin, first.Offset);
            scale.InsertKeyFrame(begin, new Vector3(first.Direction * first.Depth, first.Depth, 1));
            rotation.InsertKeyFrame(begin, first.Heading);
            opacity?.InsertKeyFrame(begin, actor.Spec.Opacity * first.OpacityFactor);
        }

        foreach (var sample in route.Skip(1))
        {
            var progress = begin + sample.Progress * travel;
            offset.InsertKeyFrame(progress, sample.Offset, easing);
            scale.InsertKeyFrame(
                progress,
                new Vector3(sample.Direction * sample.Depth, sample.Depth, 1),
                easing);
            rotation.InsertKeyFrame(progress, sample.Heading, easing);
            opacity?.InsertKeyFrame(progress, actor.Spec.Opacity * sample.OpacityFactor, easing);
        }

        foreach (var animation in new KeyFrameAnimation[] { offset, scale, rotation })
        {
            animation.Duration = TimeSpan.FromSeconds(totalSeconds);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        if (opacity != null)
        {
            opacity.Duration = TimeSpan.FromSeconds(totalSeconds);
            opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        actor.Root.StartAnimation(nameof(actor.Root.Offset), offset);
        actor.Root.StartAnimation(nameof(actor.Root.Scale), scale);
        actor.Root.StartAnimation(nameof(actor.Root.RotationAngleInDegrees), rotation);
        if (opacity != null)
            actor.Root.StartAnimation(nameof(actor.Root.Opacity), opacity);

        if (actor.Spec.Articulated)
        {
            var tail = compositor.CreateScalarKeyFrameAnimation();
            tail.InsertKeyFrame(0, -6.5f);
            tail.InsertKeyFrame(0.25f, 3.2f);
            tail.InsertKeyFrame(0.5f, 6.5f);
            tail.InsertKeyFrame(0.75f, -3.2f);
            tail.InsertKeyFrame(1, -6.5f);
            tail.Duration = TimeSpan.FromSeconds(Math.Clamp(actor.Spec.DurationSeconds / 48d, 0.62, 1.05));
            tail.IterationBehavior = AnimationIterationBehavior.Forever;
            actor.Tail.StartAnimation(nameof(actor.Tail.RotationAngleInDegrees), tail);
        }
    }

    private static IReadOnlyList<RouteSample> CreateRoute(FishSpec spec)
    {
        var random = new Random(spec.Seed);
        var y = Math.Clamp(DesignHeight * spec.Lane, 7, DesignHeight - spec.Height - 6);
        var margin = spec.Width + 11;
        var baseLoop = new[]
        {
            new Vector2(-margin, y),
            new Vector2(DesignWidth * 0.10f, y - spec.Wave * 0.62f),
            new Vector2(DesignWidth * 0.34f, y + spec.Wave * 0.44f),
            new Vector2(DesignWidth * 0.63f, y - spec.Wave),
            new Vector2(DesignWidth + margin, y + spec.Wave * 0.72f),
            new Vector2(DesignWidth + margin, y + spec.Height * 0.31f),
            new Vector2(DesignWidth * 0.72f, y + spec.Height * 0.43f),
            new Vector2(DesignWidth * 0.41f, y + spec.Height * 0.18f),
            new Vector2(DesignWidth * 0.12f, y + spec.Height * 0.48f)
        };
        var phase = Math.Clamp(spec.RouteStart, 0, baseLoop.Length - 1);
        var points = Enumerable.Range(0, baseLoop.Length + 1)
            .Select(index => baseLoop[(phase + index) % baseLoop.Length])
            .ToArray();

        var lengths = new float[points.Length];
        var total = 0f;
        for (var index = 1; index < points.Length; index++)
        {
            total += Vector2.Distance(points[index - 1], points[index]);
            lengths[index] = total;
        }

        var samples = new List<RouteSample>(points.Length + 4);
        var previousDirection = 1f;
        for (var index = 0; index < points.Length; index++)
        {
            var progress = total <= 0 ? 0 : lengths[index] / total;
            var tangent = index == points.Length - 1
                ? points[index] - points[index - 1]
                : points[index + 1] - points[index];
            var direction = Math.Abs(tangent.X) < 0.01f ? previousDirection : MathF.Sign(tangent.X);
            var depth = 0.93f + 0.07f * MathF.Sin(progress * MathF.Tau + spec.Seed * 0.17f);
            var heading = Math.Clamp(
                MathF.Atan2(tangent.Y, Math.Max(1, MathF.Abs(tangent.X))) * 180f / MathF.PI,
                -7.5f,
                7.5f);

            var sampleProgress = progress;
            if (index > 0 && direction != previousDirection)
            {
                var turnWindow = Math.Min(0.011f, progress * 0.25f);
                var before = Math.Max(samples[^1].Progress + 0.0005f, progress - turnWindow);
                samples.Add(new RouteSample(
                    before,
                    new Vector3(points[index].X, points[index].Y, 0),
                    heading,
                    depth,
                    previousDirection,
                    0.88f));
                samples.Add(new RouteSample(
                    progress,
                    new Vector3(points[index].X, points[index].Y, 0),
                    0,
                    depth,
                    direction * 0.10f,
                    0.72f));
                sampleProgress = Math.Min(1, progress + turnWindow);
            }

            var jitter = index is 0 or 4 or 5 or 9
                ? 0
                : ((float)random.NextDouble() - 0.5f) * 1.4f;
            samples.Add(new RouteSample(
                sampleProgress,
                new Vector3(points[index].X, points[index].Y + jitter, 0),
                heading,
                depth,
                direction,
                0.92f + 0.08f * depth));
            previousDirection = direction;
        }

        return samples;
    }

    private static void StartBubbleAnimation(AmbientActor actor)
    {
        var compositor = actor.Visual.Compositor;
        actor.Visual.Opacity = actor.Anchor;
        actor.Visual.Scale = Vector3.One;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, Vector3.Zero);
        offset.InsertKeyFrame(0.5f, new Vector3(actor.Drift, -DesignHeight * 0.5f, 0));
        offset.InsertKeyFrame(1, new Vector3(0, -DesignHeight, 0));
        offset.Duration = TimeSpan.FromSeconds(actor.DurationSeconds);
        offset.IterationBehavior = AnimationIterationBehavior.Forever;
        actor.Visual.StartAnimation(nameof(actor.Visual.Offset), offset);

    }

    private static void StartParticleAnimation(AmbientActor actor)
    {
        var compositor = actor.Visual.Compositor;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, Vector3.Zero);
        offset.InsertKeyFrame(0.34f, new Vector3(actor.Drift, -3.5f, 0));
        offset.InsertKeyFrame(0.68f, new Vector3(-actor.Drift * 0.55f, -7.5f, 0));
        offset.InsertKeyFrame(1, new Vector3(0, -12, 0));
        offset.Duration = TimeSpan.FromSeconds(actor.DurationSeconds);
        offset.IterationBehavior = AnimationIterationBehavior.Forever;
        actor.Visual.StartAnimation(nameof(actor.Visual.Offset), offset);
    }

    private void StartAmbientAnimation()
    {
        var backdrop = ElementCompositionPreview.GetElementVisual(BackdropLayer);
        backdrop.CenterPoint = new Vector3(
            (float)Math.Max(0, BackdropLayer.ActualWidth / 2),
            (float)Math.Max(0, BackdropLayer.ActualHeight / 2),
            0);
        backdrop.Scale = new Vector3(1.025f, 1.025f, 1);

        StartPlantAnimation(FrontPlantLeft, 1.45f, -1.20f, 6.2);
        StartPlantAnimation(FrontPlantRight, -1.25f, 1.50f, 7.1);
        if (_profile.AnimateRearPlants)
        {
            StartPlantAnimation(RearPlantLeft, 0.72f, -0.68f, 8.6);
            StartPlantAnimation(RearPlantRight, -0.62f, 0.78f, 9.3);
        }
        StartCausticAnimation(CausticFieldA, -13, 9, 13.0, 0.66f);
        if (_profile.AnimateSecondCaustic)
            StartCausticAnimation(CausticFieldB, 8, -11, 16.5, 0.42f);
        else
            ElementCompositionPreview.GetElementVisual(CausticFieldB).Opacity = 0.30f;

        StartWaterLightAnimation(WaterTone, 0.29f, 0.38f, 8.2);
        StartWaterLightAnimation(WaterlineHighlight, 0.40f, 0.55f, 5.8);
    }

    private static void StartPlantAnimation(UIElement element, float first, float second, double seconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (element is FrameworkElement frameworkElement)
        {
            visual.CenterPoint = new Vector3(
                (float)Math.Max(0, frameworkElement.ActualWidth / 2),
                (float)Math.Max(0, frameworkElement.ActualHeight),
                0);
        }
        visual.RotationAxis = Vector3.UnitZ;
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.InsertKeyFrame(0, first);
        rotation.InsertKeyFrame(0.46f, second);
        rotation.InsertKeyFrame(1, first);
        rotation.Duration = TimeSpan.FromSeconds(seconds);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(visual.RotationAngleInDegrees), rotation);
    }

    private static void StartCausticAnimation(
        UIElement element,
        float startX,
        float endX,
        double seconds,
        float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (element is FrameworkElement frameworkElement)
        {
            visual.CenterPoint = new Vector3(
                (float)Math.Max(0, frameworkElement.ActualWidth / 2),
                (float)Math.Max(0, frameworkElement.ActualHeight / 2),
                0);
        }
        var offset = visual.Compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(startX, -1, 0));
        offset.InsertKeyFrame(0.5f, new Vector3(endX, 2, 0));
        offset.InsertKeyFrame(1, new Vector3(startX, -1, 0));
        offset.Duration = TimeSpan.FromSeconds(seconds);
        offset.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(visual.Offset), offset);
        visual.Opacity = opacity;
    }

    private static void StartWaterLightAnimation(
        UIElement element,
        float lowOpacity,
        float highOpacity,
        double seconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, lowOpacity);
        pulse.InsertKeyFrame(0.43f, highOpacity);
        pulse.InsertKeyFrame(0.72f, lowOpacity + (highOpacity - lowOpacity) * 0.34f);
        pulse.InsertKeyFrame(1, lowOpacity);
        pulse.Duration = TimeSpan.FromSeconds(seconds);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(visual.Opacity), pulse);
    }

    private void StopAnimations(bool applyPoster)
    {
        foreach (var actor in _fish)
        {
            actor.Root.StopAnimation(nameof(actor.Root.Offset));
            actor.Root.StopAnimation(nameof(actor.Root.Scale));
            actor.Root.StopAnimation(nameof(actor.Root.RotationAngleInDegrees));
            actor.Root.StopAnimation(nameof(actor.Root.Opacity));
            actor.Tail.StopAnimation(nameof(actor.Tail.RotationAngleInDegrees));
        }
        foreach (var actor in _bubbleStreams.Concat(_particleFields))
        {
            actor.Visual.StopAnimation(nameof(actor.Visual.Offset));
        }

        var backdrop = ElementCompositionPreview.GetElementVisual(BackdropLayer);
        backdrop.Offset = Vector3.Zero;
        foreach (var plant in new UIElement[] { FrontPlantLeft, FrontPlantRight, RearPlantLeft, RearPlantRight })
        {
            var visual = ElementCompositionPreview.GetElementVisual(plant);
            visual.StopAnimation(nameof(visual.RotationAngleInDegrees));
            visual.RotationAngleInDegrees = 0;
        }
        foreach (var caustic in new UIElement[] { CausticFieldA, CausticFieldB })
        {
            var visual = ElementCompositionPreview.GetElementVisual(caustic);
            visual.StopAnimation(nameof(visual.Offset));
            visual.Offset = Vector3.Zero;
        }
        foreach (var waterLight in new UIElement[] { WaterTone, WaterlineHighlight })
            ElementCompositionPreview.GetElementVisual(waterLight).StopAnimation(nameof(Visual.Opacity));
        _animationsRunning = false;
        if (applyPoster)
            ApplyStaticScene();
    }

    private void ApplyStaticScene()
    {
        for (var index = 0; index < _fish.Count; index++)
        {
            var actor = _fish[index];
            actor.Root.Offset = new Vector3(
                DesignWidth * (0.08f + index * 0.145f),
                Math.Clamp(DesignHeight * actor.Spec.Lane, 5, DesignHeight - actor.Spec.Height - 4),
                0);
            var direction = index % 2 == 0 ? 1f : -1f;
            var depth = 0.88f + index % 3 * 0.055f;
            actor.Root.Scale = new Vector3(direction * depth, depth, 1);
            actor.Root.RotationAngleInDegrees = 0;
            actor.Root.Opacity = actor.Spec.Opacity * 0.90f;
            actor.Tail.RotationAngleInDegrees = index % 2 == 0 ? -2.5f : 2.5f;
            actor.Body.Scale = Vector3.One;
        }
        foreach (var actor in _bubbleStreams)
            actor.Visual.Opacity = 0;
        foreach (var actor in _particleFields)
            actor.Visual.Opacity = actor.Anchor * 0.55f;

        ElementCompositionPreview.GetElementVisual(CausticFieldA).Opacity = 0.26f;
        ElementCompositionPreview.GetElementVisual(CausticFieldB).Opacity = 0.15f;
        ElementCompositionPreview.GetElementVisual(WaterTone).Opacity = 0.34f;
        ElementCompositionPreview.GetElementVisual(WaterlineHighlight).Opacity = 0.42f;
    }

    private sealed record FishSpec(
        string Asset,
        float Width,
        float Height,
        float Lane,
        double DurationSeconds,
        double DelaySeconds,
        float Opacity,
        float Wave,
        int Seed,
        int RouteStart,
        bool Articulated);

    private sealed record FishActor(
        ContainerVisual Root,
        SpriteVisual Body,
        SpriteVisual Tail,
        FishSpec Spec);

    private sealed record AmbientActor(
        Visual Visual,
        double DurationSeconds,
        double DelaySeconds,
        float Drift,
        float Anchor);

    private sealed record SceneProfile(
        int FishCount,
        int BubbleFieldCount,
        int BubbleCount,
        int ParticleFieldCount,
        int ParticleCount,
        bool AnimateRearPlants,
        bool AnimateSecondCaustic,
        bool AnimateDepthOpacity)
    {
        public static SceneProfile Eco { get; } = new(
            FishCount: 3,
            BubbleFieldCount: 1,
            BubbleCount: 8,
            ParticleFieldCount: 0,
            ParticleCount: 0,
            AnimateRearPlants: false,
            AnimateSecondCaustic: false,
            AnimateDepthOpacity: false);

        public static SceneProfile Balanced { get; } = new(
            FishCount: 4,
            BubbleFieldCount: 1,
            BubbleCount: 12,
            ParticleFieldCount: 1,
            ParticleCount: 18,
            AnimateRearPlants: false,
            AnimateSecondCaustic: false,
            AnimateDepthOpacity: false);

        public static SceneProfile High { get; } = new(
            FishCount: 6,
            BubbleFieldCount: 1,
            BubbleCount: 20,
            ParticleFieldCount: 1,
            ParticleCount: 30,
            AnimateRearPlants: true,
            AnimateSecondCaustic: true,
            AnimateDepthOpacity: true);
    }

    private readonly record struct RouteSample(
        float Progress,
        Vector3 Offset,
        float Heading,
        float Depth,
        float Direction,
        float OpacityFactor);
}
