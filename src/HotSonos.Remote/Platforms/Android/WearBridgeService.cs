using Android.App;
using Android.Gms.Wearable;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote;

/// <summary>
/// Receives Wearable Data Layer messages from the watch companion.
/// Play services binds this service. It does not add a watch hardware feature,
/// so a Chromebook that never receives a message is unchanged.
/// </summary>
[Service(Exported = true)]
[IntentFilter(
    new[] { "com.google.android.gms.wearable.MESSAGE_RECEIVED" },
    DataScheme = "wear",
    DataHost = "*",
    DataPathPrefix = "/hotsonos")]
public sealed class WearBridgeService : WearableListenerService
{
    public override void OnMessageReceived(IMessageEvent messageEvent)
    {
        var node = messageEvent.SourceNodeId;
        var payload = messageEvent.GetData();
        var text = payload is null || payload.Length == 0
            ? ""
            : System.Text.Encoding.UTF8.GetString(payload);
        _ = Task.Run(() => WearBridge.OnCommand(node, text));
    }
}
