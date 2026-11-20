using HotSonos.App.Models;
using HotSonos.Core;
using HotSonos.Remote.Protocol;

namespace HotSonos.App.Services;

/// <summary>
/// Live ZoneGroupTopology next to the saved Daily preset. Nothing here joins or leaves a group.
/// </summary>
public sealed record GroupingDisplay(string Summary, string PresetLabel, bool? MatchesDaily)
{
    public static GroupingDisplay From(SonosManager sonos, AppSettings settings)
    {
        settings.EnsureShape();
        var picture = sonos.LivePicture;
        var allow = settings.GetDailyGroupRoomAllowList();
        var all = allow is null;
        bool? matches = picture.Groups.Count == 0
            ? null
            : LiveGrouping.MatchesDaily(picture, all, allow);
        var presets = settings.DailySpeakerPresets
            .Select(p => new DailyPresetModel { Name = p.Name, Rooms = p.Rooms ?? [] })
            .ToList();
        return new GroupingDisplay(picture.Summary, DailyGroupLogic.DescribePreset(all, allow, presets), matches);
    }
}
