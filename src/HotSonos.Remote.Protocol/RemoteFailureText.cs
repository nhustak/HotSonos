namespace HotSonos.Remote.Protocol;

public enum RemoteFailureKind
{
    WrongPin,
    Timeout,
    Unreachable,
    Failed,
    Cancelled,
}

public enum TransportFault
{
    Canceled,
    Http,
    Socket,
    Io,
    Other,
}

/// <summary>Sentences the phone shows for a connect attempt. The PC's message is appended when it adds detail.</summary>
public static class RemoteFailureText
{
    public static RemoteFailureKind Classify(bool callerCancelled, TransportFault fault) => fault switch
    {
        TransportFault.Canceled => callerCancelled ? RemoteFailureKind.Cancelled : RemoteFailureKind.Timeout,
        TransportFault.Http or TransportFault.Socket or TransportFault.Io => RemoteFailureKind.Unreachable,
        _ => RemoteFailureKind.Failed,
    };

    public static string Describe(RemoteFailureKind kind, string? detail)
    {
        var extra = OneLine(detail);
        return kind switch
        {
            RemoteFailureKind.WrongPin => "Wrong PIN. The PC rejected it.",
            RemoteFailureKind.Timeout => Join(
                "Timed out. The PC did not answer. Check the address, and that HotSonos is running with Phone listening.",
                extra),
            RemoteFailureKind.Unreachable => Join(
                "Unreachable. Nothing accepted a connection at that address.",
                extra),
            RemoteFailureKind.Cancelled => "Cancelled.",
            _ => string.IsNullOrWhiteSpace(extra) ? "The PC answered with an error." : extra,
        };
    }

    public static string PcRequired(string what) =>
        what + " needs the PC. Connect to your PC to use this. Tap the PC / Speakers line at the top.";

    static string Join(string sentence, string extra) =>
        string.IsNullOrWhiteSpace(extra) ? sentence : sentence + " " + extra;

    static string OneLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";
        var text = detail.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 180 ? text : text[..180];
    }
}
