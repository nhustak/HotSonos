using Android.Views;
using AView = Android.Views.View;

namespace HotSonos.Remote;

/// <summary>Mouse-wheel and trackpad scroll for lists and the tab strip. ChromeOS delivers these as generic motion.</summary>
static class MouseWheel
{
    public static bool TryScroll(Android.App.Activity activity, MotionEvent e)
    {
        if (e.ActionMasked != MotionEventActions.Scroll)
            return false;

        var root = activity.Window?.DecorView;
        if (root is null)
            return false;

        var target = DeepestScrollable(root, e.RawX, e.RawY);
        if (target is null)
            return false;

        var vertical = e.GetAxisValue(Axis.Vscroll);
        var horizontal = e.GetAxisValue(Axis.Hscroll);
        if (vertical == 0 && horizontal == 0)
            return false;

        var density = target.Resources?.DisplayMetrics?.Density ?? 1f;
        var factor = Factor(target, density);
        var canVertical = target.CanScrollVertically(1) || target.CanScrollVertically(-1);
        var canHorizontal = target.CanScrollHorizontally(1) || target.CanScrollHorizontally(-1);
        int dx;
        int dy;
        if (canVertical)
        {
            dx = canHorizontal ? (int)(-horizontal * factor) : 0;
            dy = (int)(-vertical * factor);
        }
        else
        {
            // Tab strip: a vertical wheel moves the horizontal row.
            var along = horizontal != 0 ? horizontal : vertical;
            dx = (int)(-along * factor);
            dy = 0;
        }

        if (dx == 0 && dy == 0)
            return false;

        target.ScrollBy(dx, dy);
        return true;
    }

    static float Factor(AView view, float density)
    {
        var config = view.Context is null ? null : ViewConfiguration.Get(view.Context);
        if (config is not null && OperatingSystem.IsAndroidVersionAtLeast(26))
            return Math.Max(16f, config.ScaledVerticalScrollFactor);
        return Math.Max(16f, 64f * density);
    }

    static AView? DeepestScrollable(AView view, float rawX, float rawY)
    {
        if (!Contains(view, rawX, rawY))
            return null;

        if (view is ViewGroup group)
        {
            for (var i = group.ChildCount - 1; i >= 0; i--)
            {
                var child = group.GetChildAt(i);
                if (child is null)
                    continue;
                var found = DeepestScrollable(child, rawX, rawY);
                if (found is not null)
                    return found;
            }
        }

        return CanScroll(view) ? view : null;
    }

    static bool CanScroll(AView view)
    {
        if (view is Android.Widget.AbsSeekBar)
            return false;
        return view.CanScrollVertically(1) || view.CanScrollVertically(-1)
            || view.CanScrollHorizontally(1) || view.CanScrollHorizontally(-1);
    }

    static bool Contains(AView view, float rawX, float rawY)
    {
        if (view.Visibility != ViewStates.Visible || view.Width <= 0 || view.Height <= 0)
            return false;
        var loc = new int[2];
        view.GetLocationOnScreen(loc);
        return rawX >= loc[0] && rawX < loc[0] + view.Width
            && rawY >= loc[1] && rawY < loc[1] + view.Height;
    }
}
