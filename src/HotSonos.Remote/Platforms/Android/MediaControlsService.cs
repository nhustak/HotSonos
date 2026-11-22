using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using AndroidX.Core.App;
using AndroidX.Media;
using HotSonos.Remote.Protocol;
using MediaStyle = AndroidX.Media.App.NotificationCompat.MediaStyle;

namespace HotSonos.Remote;

/// <summary>
/// Foreground media session. The Pixel Watch and the lock screen read this notification.
/// Transport and volume go back through <see cref="MediaControls"/> to the phone's existing client.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class MediaControlsService : Service
{
    public const int NotificationId = 42342;
    public const string ChannelId = "hotsonos.media";

    const string ActionPlay = "com.tangenttech.hotsonos.media.PLAY";
    const string ActionPause = "com.tangenttech.hotsonos.media.PAUSE";
    const string ActionNext = "com.tangenttech.hotsonos.media.NEXT";
    const string ActionPrevious = "com.tangenttech.hotsonos.media.PREVIOUS";

    MediaSessionCompat? _session;
    SonosVolumeProvider? _volume;
    TransportCallback? _callback;
    MediaControlState? _shown;
    Bitmap? _bitmap;
    string? _artToken;
    Handler? _handler;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            EnsureChannel();
            if (_session is null)
                CreateSession();
            MediaControlsBridge.Attach(this);
            if (intent?.Action is { Length: > 0 } action)
                Dispatch(action);
            var latest = MediaControlsBridge.Latest;
            if (latest is { } pending)
                Apply(pending.State, pending.Art, forceNotification: true);
            else
                Apply(MediaControlMapping.FromStatus(null), null, forceNotification: true);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("HotSonos", "Media session failed to start: " + ex.Message);
            try { StopSelf(); }
            catch (Exception) { }
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        MediaControlsBridge.Detach(this);
        ReleaseSession();
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    public void Apply(MediaControlState state, byte[]? art, bool forceNotification)
    {
        var looper = Looper.MainLooper;
        if (looper is not null && Looper.MyLooper() != looper)
        {
            (_handler ??= new Handler(looper)).Post(() => Apply(state, art, forceNotification));
            return;
        }

        if (_session is null || _volume is null)
            return;

        var artChanged = UpdateArt(state, art);
        if (state.HasVolume)
            _volume.CurrentVolume = state.VolumePercent;

        var bitmap = _bitmap;
        try
        {
            _session.SetMetadata(BuildMetadata(state, bitmap));
        }
        catch (Exception)
        {
            _session.SetMetadata(BuildMetadata(state, null));
        }

        _session.SetPlaybackState(BuildPlayback(state));

        if (!forceNotification && !artChanged && _shown is { } shown && shown.SameFace(state))
            return;

        _shown = state;
        Promote(BuildNotification(state, bitmap));
    }

    void CreateSession()
    {
        var session = new MediaSessionCompat(this, "HotSonos");
        _callback = new TransportCallback();
        var looper = Looper.MainLooper;
        if (looper is null)
            session.SetCallback(_callback);
        else
            session.SetCallback(_callback, _handler ??= new Handler(looper));

        var volume = new SonosVolumeProvider(0);
        session.SetPlaybackToRemote(volume);
        session.SetSessionActivity(OpenIntent());
        session.Active = true;
        _session = session;
        _volume = volume;
    }

    void ReleaseSession()
    {
        var session = _session;
        _session = null;
        _volume = null;
        _callback = null;
        _shown = null;
        if (session is not null)
        {
            try
            {
                session.Active = false;
                session.Release();
            }
            catch (Exception)
            {
                // Already released on a previous stop.
            }
        }

        _bitmap?.Dispose();
        _bitmap = null;
        _artToken = null;
    }

    bool UpdateArt(MediaControlState state, byte[]? art)
    {
        if (!state.ShowArt || art is not { Length: > 0 })
        {
            if (_bitmap is null)
                return false;
            _bitmap.Dispose();
            _bitmap = null;
            _artToken = null;
            return true;
        }

        if (state.ArtToken == _artToken && _bitmap is not null)
            return false;

        var decoded = Decode(art);
        if (decoded is null)
            return false;
        _bitmap?.Dispose();
        _bitmap = decoded;
        _artToken = state.ArtToken;
        return true;
    }

    static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MediaControlMapping.MaxArtBytes)
            return null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
            var sample = 1;
            var width = Math.Max(bounds.OutWidth, 1);
            var height = Math.Max(bounds.OutHeight, 1);
            while (width / sample > 320 || height / sample > 320)
                sample *= 2;
            var options = new BitmapFactory.Options { InSampleSize = Math.Max(1, sample) };
            return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    void Dispatch(string action)
    {
        switch (action)
        {
            case ActionPlay:
                MediaControls.RequestTransport(MediaTransportRequest.Play);
                break;
            case ActionPause:
                MediaControls.RequestTransport(MediaTransportRequest.Pause);
                break;
            case ActionNext:
                MediaControls.RequestTransport(MediaTransportRequest.Next);
                break;
            case ActionPrevious:
                MediaControls.RequestTransport(MediaTransportRequest.Previous);
                break;
        }
    }

    void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
            return;
        var channel = new NotificationChannel(ChannelId, "Media controls", NotificationImportance.Low)
        {
            Description = "Now playing on Sonos, for a paired watch and the lock screen",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        manager.CreateNotificationChannel(channel);
    }

    Notification BuildNotification(MediaControlState state, Bitmap? art)
    {
        var session = _session ?? throw new InvalidOperationException("Media session is not running.");
        var token = session.SessionToken ?? throw new InvalidOperationException("Media session has no token.");
        var playing = state.PublishedPlayback == MediaControlPlayback.Playing;
        var style = new MediaStyle();
        style.SetMediaSession(token);
        style.SetShowActionsInCompactView(0, 1, 2);
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_hotsonos);
        builder.SetContentTitle(state.Title);
        builder.SetContentText(MediaControlMapping.NotificationText(state));
        builder.SetContentIntent(OpenIntent());
        builder.SetVisibility(NotificationCompat.VisibilityPublic);
        builder.SetCategory(Notification.CategoryTransport);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetShowWhen(false);
        builder.SetStyle(style);
        builder.AddAction(Action(Android.Resource.Drawable.IcMediaPrevious, "Previous", ActionPrevious, 1));
        builder.AddAction(playing
            ? Action(Android.Resource.Drawable.IcMediaPause, "Pause", ActionPause, 2)
            : Action(Android.Resource.Drawable.IcMediaPlay, "Play", ActionPlay, 2));
        builder.AddAction(Action(Android.Resource.Drawable.IcMediaNext, "Next", ActionNext, 3));
        if (art is not null)
            builder.SetLargeIcon(art);
        return builder.Build() ?? throw new InvalidOperationException("Media notification was not built.");
    }

    NotificationCompat.Action Action(int icon, string title, string action, int requestCode) =>
        new(icon, title, PendingIntent.GetService(
            this,
            requestCode,
            new Intent(this, typeof(MediaControlsService)).SetAction(action),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));

    PendingIntent OpenIntent() =>
        PendingIntent.GetActivity(
            this,
            0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

    static MediaMetadataCompat BuildMetadata(MediaControlState state, Bitmap? art)
    {
        var meta = new MediaMetadataCompat.Builder();
        meta.PutString(MediaMetadataCompat.MetadataKeyTitle, state.Title);
        meta.PutString(MediaMetadataCompat.MetadataKeyArtist, state.Artist);
        meta.PutString(MediaMetadataCompat.MetadataKeyAlbum, state.Album);
        if (state.DurationMs > 0)
            meta.PutLong(MediaMetadataCompat.MetadataKeyDuration, state.DurationMs);
        if (art is not null)
            meta.PutBitmap(MediaMetadataCompat.MetadataKeyAlbumArt, art);
        return meta.Build() ?? throw new InvalidOperationException("Media metadata was not built.");
    }

    static PlaybackStateCompat BuildPlayback(MediaControlState state)
    {
        var code = state.PublishedPlayback == MediaControlPlayback.Playing
            ? PlaybackStateCompat.StatePlaying
            : PlaybackStateCompat.StatePaused;
        var playback = new PlaybackStateCompat.Builder();
        playback.SetActions(
            PlaybackStateCompat.ActionPlay
            | PlaybackStateCompat.ActionPause
            | PlaybackStateCompat.ActionPlayPause
            | PlaybackStateCompat.ActionSkipToNext
            | PlaybackStateCompat.ActionSkipToPrevious);
        playback.SetState(code, state.PositionMs, state.PlaybackSpeed);
        return playback.Build() ?? throw new InvalidOperationException("Playback state was not built.");
    }

    void Promote(Notification notification)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);
    }

    sealed class TransportCallback : MediaSessionCompat.Callback
    {
        public override void OnPlay() => MediaControls.RequestTransport(MediaTransportRequest.Play);

        public override void OnPause() => MediaControls.RequestTransport(MediaTransportRequest.Pause);

        public override void OnSkipToNext() => MediaControls.RequestTransport(MediaTransportRequest.Next);

        public override void OnSkipToPrevious() => MediaControls.RequestTransport(MediaTransportRequest.Previous);
    }

    sealed class SonosVolumeProvider : VolumeProviderCompat
    {
        public SonosVolumeProvider(int current)
            : base(VolumeControlAbsolute, MediaControlMapping.VolumeMax, MediaControlMapping.ClampVolume(current))
        {
        }

        public override void OnSetVolumeTo(int volume)
        {
            var next = MediaControlMapping.ClampVolume(volume);
            CurrentVolume = next;
            MediaControls.RequestVolume(next);
        }

        public override void OnAdjustVolume(int direction)
        {
            var next = MediaControlMapping.AdjustVolume(CurrentVolume, direction);
            if (next is null)
                return;
            CurrentVolume = next.Value;
            MediaControls.RequestVolume(next.Value);
        }
    }
}

/// <summary>Holds the latest snapshot so the service can publish it the moment it starts.</summary>
static class MediaControlsBridge
{
    static readonly object Gate = new();
    static MediaControlsService? _service;
    static MediaControlState? _state;
    static byte[]? _art;

    public static void Attach(MediaControlsService service)
    {
        lock (Gate)
            _service = service;
    }

    public static void Detach(MediaControlsService service)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_service, service))
                _service = null;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _state = null;
            _art = null;
        }
    }

    public static (MediaControlState State, byte[]? Art)? Latest
    {
        get
        {
            lock (Gate)
                return _state is null ? null : (_state, _art);
        }
    }

    public static void Publish(MediaControlState state, byte[]? art)
    {
        MediaControlsService? service;
        lock (Gate)
        {
            _state = state;
            _art = art;
            service = _service;
        }

        service?.Apply(state, art, forceNotification: false);
    }
}
