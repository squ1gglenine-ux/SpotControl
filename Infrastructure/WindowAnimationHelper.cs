using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpotCont.Infrastructure;

public static class WindowAnimationHelper
{
    public static Task ShowAsync(Window window, ScaleTransform scaleTransform, int durationMs = 120)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stop(window, scaleTransform);

        scaleTransform.ScaleX = 0.98;
        scaleTransform.ScaleY = 0.98;
        window.Opacity = 0;

        if (!window.IsVisible)
        {
            window.Show();
        }

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var opacityAnimation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };
        var scaleAnimation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };

        scaleAnimation.Completed += (_, _) =>
        {
            scaleTransform.ScaleX = 1;
            scaleTransform.ScaleY = 1;
            window.Opacity = 1;
            completion.TrySetResult();
        };

        window.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());

        return completion.Task;
    }

    public static Task HideAsync(Window window, ScaleTransform scaleTransform, int durationMs = 100)
    {
        if (!window.IsVisible)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stop(window, scaleTransform);

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        var opacityAnimation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };
        var scaleAnimation = new DoubleAnimation(0.98, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };

        scaleAnimation.Completed += (_, _) =>
        {
            window.Hide();
            window.Opacity = 1;
            scaleTransform.ScaleX = 1;
            scaleTransform.ScaleY = 1;
            completion.TrySetResult();
        };

        window.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());

        return completion.Task;
    }

    private static void Stop(Window window, ScaleTransform scaleTransform)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
