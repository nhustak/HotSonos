using HotSonos.Core;
using HotSonos.Core.Models;

// HotSonos Phase 1 harness — proves the Core UPnP client against real speakers.
//
// Usage:
//   dotnet run --project src/HotSonos.Harness -- [options] [command]
//
// Options:
//   --ip <addr>      Skip SSDP; resolve topology from this known speaker IP.
//   --room "<name>"  Target this room (default: first discovered zone).
//
// Commands (default: "info"):
//   info        List zones + favorites for the target room.
//   zones       List discovered zones/groups only.
//   favorites   List favorites/playlists for the target room.
//   state       Print current transport state.
//   play | pause | playpause | next | prev
//   fav "<name>"  Play the favorite/playlist with this title.

var (ip, room, command, commandArg) = ParseArgs(args);

var discovery = new SonosDiscovery();

Console.WriteLine(ip is null
    ? "Discovering Sonos zones via SSDP (up to 4s)…"
    : $"Resolving topology from {ip}…");

IReadOnlyList<SonosZone> zones;
try
{
    zones = ip is null
        ? await discovery.DiscoverZonesAsync(TimeSpan.FromSeconds(4))
        : await discovery.GetZonesFromAsync(ip);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Discovery failed: {ex.Message}");
    return 1;
}

if (zones.Count == 0)
{
    Console.Error.WriteLine(
        "No Sonos zones found. If SSDP is blocked on your network, re-run with --ip <speakerIp> " +
        "(find it in the Sonos app: Settings → System → About My System).");
    return 1;
}

PrintZones(zones);

if (command == "zones")
    return 0;

var target = room is null
    ? zones[0]
    : zones.FirstOrDefault(z => string.Equals(z.RoomName, room, StringComparison.OrdinalIgnoreCase));

if (target is null)
{
    Console.Error.WriteLine($"Room '{room}' not found. Rooms: {string.Join(", ", zones.Select(z => z.RoomName))}");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"Target: {target.RoomName}  (coordinator {target.CoordinatorIpAddress})");

var controller = SonosController.ForZone(target);

try
{
    switch (command)
    {
        case "info":
        case "favorites":
            await PrintFavorites(controller);
            break;

        case "state":
            Console.WriteLine($"State: {await controller.GetTransportStateAsync()}");
            break;

        case "play":
            await controller.PlayAsync();
            Console.WriteLine("▶ Play sent.");
            break;

        case "pause":
            await controller.PauseAsync();
            Console.WriteLine("⏸ Pause sent.");
            break;

        case "playpause":
            Console.WriteLine($"Toggled → {await controller.PlayPauseAsync()}");
            break;

        case "shuffle":
            await controller.ShuffleMusicLibraryAsync();
            Console.WriteLine("🔀 Shuffling Music Library.");
            break;

        case "next":
            await controller.NextAsync();
            Console.WriteLine("⏭ Next sent.");
            break;

        case "prev":
            await controller.PreviousAsync();
            Console.WriteLine("⏮ Previous sent.");
            break;

        case "fav":
            if (string.IsNullOrWhiteSpace(commandArg))
            {
                Console.Error.WriteLine("Usage: fav \"<favorite name>\"");
                return 1;
            }
            await controller.PlayFavoriteByNameAsync(commandArg);
            Console.WriteLine($"▶ Playing favorite: {commandArg}");
            break;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Command '{command}' failed: {ex.Message}");
    return 1;
}

return 0;

static void PrintZones(IReadOnlyList<SonosZone> zones)
{
    Console.WriteLine();
    Console.WriteLine($"Found {zones.Count} zone(s):");
    foreach (var z in zones)
    {
        var role = z.IsCoordinator ? "coordinator" : $"grouped → {z.CoordinatorIpAddress}";
        Console.WriteLine($"  • {z.RoomName,-20} {z.IpAddress,-15} [{role}]");
    }
}

static async Task PrintFavorites(SonosController controller)
{
    var favorites = await controller.GetFavoritesAsync();
    Console.WriteLine();
    if (favorites.Count == 0)
    {
        Console.WriteLine("No favorites found (add some in the Sonos app first).");
        return;
    }

    Console.WriteLine($"{favorites.Count} favorite(s)/playlist(s):");
    foreach (var f in favorites)
        Console.WriteLine($"  • {f.Title}{(f.IsPlayable ? "" : "   (shortcut — not directly playable)")}");
}

static (string? Ip, string? Room, string Command, string? CommandArg) ParseArgs(string[] args)
{
    string? ip = null, room = null, commandArg = null;
    var command = "info";

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--ip" when i + 1 < args.Length:
                ip = args[++i];
                break;
            case "--room" when i + 1 < args.Length:
                room = args[++i];
                break;
            default:
                command = args[i].ToLowerInvariant();
                if (command == "fav" && i + 1 < args.Length)
                    commandArg = args[++i];
                break;
        }
    }

    return (ip, room, command, commandArg);
}
