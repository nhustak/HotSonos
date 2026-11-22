using System.Runtime.CompilerServices;
using Android.Views;
using AView = Android.Views.View;
using MauiView = Microsoft.Maui.Controls.View;

namespace HotSonos.Remote;

/// <summary>
/// Focus, hover, and click for MAUI views that are not buttons.
/// Touch still activates on the first tap (not focus-then-tap). Tab and Enter use the key listener.
/// </summary>
static class ViewActivate
{
    static readonly ConditionalWeakTable<MauiView, Activator> Keep = new();

    public static void Install()
    {
        RemoteInput.Activate = Wire;
        RemoteInput.Hold = Hold;
        RemoteInput.SkipTab = Skip;
        ButtonFeedback.Install();
    }

    static readonly ConditionalWeakTable<MauiView, HoldListener> Held = new();
    static long _lastHold;

    static void Hold(MauiView view, Func<Task> hold)
    {
        void Hook()
        {
            if (view.Handler?.PlatformView is not AView native)
                return;
            if (Held.TryGetValue(view, out var existing) && ReferenceEquals(existing.Native, native))
                return;

            var listener = new HoldListener(native, hold);
            Held.AddOrUpdate(view, listener);
            native.LongClickable = true;
            native.Clickable = true;
            native.SetOnLongClickListener(listener);
            native.SetOnContextClickListener(listener);
        }

        view.HandlerChanged += (_, _) => Hook();
        Hook();
    }

    static void FireHold(Func<Task> hold)
    {
        var now = Environment.TickCount64;
        if (now - _lastHold < 700)
            return;
        _lastHold = now;
        _ = RunHoldAsync(hold);
    }

    static async Task RunHoldAsync(Func<Task> hold)
    {
        try
        {
            await hold().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (Application.Current?.MainPage is Page page)
                await page.DisplayAlert("HotSonos", ex.Message, "OK");
        }
    }

    sealed class HoldListener : Java.Lang.Object, AView.IOnLongClickListener, AView.IOnContextClickListener
    {
        readonly Func<Task> _hold;

        public HoldListener(AView native, Func<Task> hold)
        {
            Native = native;
            _hold = hold;
        }

        public AView Native { get; }

        public bool OnLongClick(AView? v)
        {
            FireHold(_hold);
            return true;
        }

        public bool OnContextClick(AView? v)
        {
            FireHold(_hold);
            return true;
        }
    }

    static void Skip(VisualElement view)
    {
        void Hook()
        {
            if (view.Handler?.PlatformView is AView native)
            {
                native.Focusable = false;
                native.FocusableInTouchMode = false;
            }
        }

        view.HandlerChanged += (_, _) => Hook();
        Hook();
    }

    static void Wire(MauiView view, Func<Task> activate)
    {
        void Hook()
        {
            if (view.Handler?.PlatformView is not AView native)
                return;
            if (Keep.TryGetValue(view, out var existing) && ReferenceEquals(existing.Native, native))
                return;

            var activator = new Activator(native, activate);
            Keep.AddOrUpdate(view, activator);
            native.Focusable = true;
            native.FocusableInTouchMode = false;
            native.Clickable = true;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                native.DefaultFocusHighlightEnabled = true;
            native.SetOnTouchListener(activator);
            native.SetOnHoverListener(activator);
            native.SetOnKeyListener(activator);
        }

        view.HandlerChanged += (_, _) => Hook();
        Hook();
    }

    sealed class Activator : Java.Lang.Object, AView.IOnTouchListener, AView.IOnHoverListener, AView.IOnKeyListener
    {
        readonly Func<Task> _activate;
        bool _over;

        public Activator(AView native, Func<Task> activate)
        {
            Native = native;
            _activate = activate;
        }

        public AView Native { get; }

        public bool OnHover(AView? v, MotionEvent? e)
        {
            if (e is null)
                return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.HoverEnter:
                case MotionEventActions.HoverMove:
                    _over = true;
                    Paint(down: false);
                    return true;
                case MotionEventActions.HoverExit:
                    _over = false;
                    Paint(down: false);
                    return true;
                default:
                    return false;
            }
        }

        public bool OnTouch(AView? v, MotionEvent? e)
        {
            if (v is null || e is null)
                return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    Paint(down: true);
                    Haptics.Tick();
                    return true;
                case MotionEventActions.Move:
                    Paint(down: Inside(v, e));
                    return true;
                case MotionEventActions.Up:
                    var hit = Inside(v, e);
                    Paint(down: false);
                    if (hit)
                        Fire();
                    return true;
                case MotionEventActions.Cancel:
                    Paint(down: false);
                    return true;
                default:
                    return false;
            }
        }

        public bool OnKey(AView? v, Keycode keyCode, KeyEvent? e)
        {
            if (e is null || e.Action != KeyEventActions.Down || e.RepeatCount != 0)
                return false;
            if (keyCode is not (Keycode.Enter or Keycode.NumpadEnter or Keycode.DpadCenter))
                return false;
            Fire();
            return true;
        }

        void Fire() => _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await _activate().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is Page page)
                    await page.DisplayAlert("HotSonos", ex.Message, "OK");
            }
        }

        void Paint(bool down)
        {
            // Native alpha only. Changing the MAUI background rebuilds the handler and can drop this listener mid-gesture.
            Native.Alpha = down ? 0.72f : _over ? 0.88f : 1f;
        }

        static bool Inside(AView view, MotionEvent e)
        {
            var x = e.GetX();
            var y = e.GetY();
            return x >= 0 && y >= 0 && x < view.Width && y < view.Height;
        }
    }
}

static class ConditionalWeakTableExtensions
{
    public static void AddOrUpdate<TKey, TValue>(this ConditionalWeakTable<TKey, TValue> table, TKey key, TValue value)
        where TKey : class
        where TValue : class
    {
        table.Remove(key);
        table.Add(key, value);
    }
}
