using System.Runtime.CompilerServices;
using Android.Views;
using AView = Android.Views.View;
using AButton = Android.Widget.Button;

namespace HotSonos.Remote;

/// <summary>
/// Hover and press for MAUI buttons. Alpha only, so the resting color, corner radius,
/// and the button's own touch listener stay as they are. Hold-to-repeat on the volume keys
/// keeps working.
/// </summary>
static class ButtonFeedback
{
    static readonly ConditionalWeakTable<AView, Feedback> Wired = new();

    public static void Install()
    {
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping(nameof(IView.Background), (handler, view) =>
        {
            if (handler.PlatformView is not AButton native || view is not Button button)
                return;
            if (Wired.TryGetValue(native, out _))
                return;

            var feedback = new Feedback(button, native);
            Wired.Add(native, feedback);
            native.Hover += feedback.OnHover;
            feedback.HookPress();
        });
    }

    sealed class Feedback
    {
        readonly Button _button;
        readonly AButton _native;
        bool _over;
        bool _down;

        public Feedback(Button button, AButton native)
        {
            _button = button;
            _native = native;
        }

        public void HookPress()
        {
            _button.Pressed += (_, _) =>
            {
                _down = true;
                Paint();
            };
            _button.Released += (_, _) =>
            {
                _down = false;
                Paint();
            };
        }

        public void OnHover(object? sender, AView.HoverEventArgs e)
        {
            if (e.Event is null)
                return;
            switch (e.Event.ActionMasked)
            {
                case MotionEventActions.HoverEnter:
                case MotionEventActions.HoverMove:
                    _over = true;
                    break;
                case MotionEventActions.HoverExit:
                    _over = false;
                    _down = false;
                    break;
                default:
                    return;
            }

            Paint();
        }

        void Paint() => _native.Alpha = _down ? 0.72f : _over ? 0.88f : 1f;
    }
}
