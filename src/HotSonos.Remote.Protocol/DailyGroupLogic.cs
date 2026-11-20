namespace HotSonos.Remote.Protocol;

/// <summary>
/// Daily speaker-group edits shared by the Control tab and the phone.
/// Checking a named group turns off All speakers and copies that room list.
/// </summary>
public sealed class DailyGroupModel
{
    public bool AllSpeakers { get; set; } = true;
    public List<string> Rooms { get; set; } = [];
    public List<DailyPresetModel> Presets { get; set; } = [];
}

public sealed class DailyPresetModel
{
    public string Name { get; set; } = "";
    public List<string> Rooms { get; set; } = [];
}

public static class DailyGroupLogic
{
    public static List<string> Normalize(IEnumerable<string>? rooms) =>
        (rooms ?? [])
            .Select(r => (r ?? "").Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool SameRooms(IEnumerable<string>? left, IEnumerable<string>? right)
    {
        var a = Normalize(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = Normalize(right).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a.SetEquals(b);
    }

    /// <summary>
    /// All speakers, or a custom list. Turning All off with an empty list seeds
    /// <paramref name="seedRoom"/> so the house is not left with nobody checked.
    /// The previous custom list is kept while All is on.
    /// </summary>
    public static void SetAll(DailyGroupModel model, bool all, string? seedRoom)
    {
        model.AllSpeakers = all;
        model.Rooms = Normalize(model.Rooms);
        if (!all && model.Rooms.Count == 0 && !string.IsNullOrWhiteSpace(seedRoom))
            model.Rooms = [seedRoom.Trim()];
    }

    public static string? SelectRooms(DailyGroupModel model, IEnumerable<string>? rooms)
    {
        model.AllSpeakers = false;
        model.Rooms = Normalize(rooms);
        return null;
    }

    public static string? SavePreset(DailyGroupModel model, string? name, IEnumerable<string>? rooms)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return "Type a name for the speaker group.";
        if (name.Length > 40)
            return "Group name must be 40 characters or less.";

        var list = Normalize(rooms);
        if (list.Count == 0)
            return "Check at least one room before saving a group.";

        model.Presets = model.Presets
            .Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        model.Presets.Add(new DailyPresetModel { Name = name, Rooms = list });
        return null;
    }

    public static string? RemovePreset(DailyGroupModel model, string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return "Name is required.";

        var next = model.Presets
            .Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (next.Count == model.Presets.Count)
            return "Group not found.";

        model.Presets = next;
        return null;
    }

    /// <summary>On copies the preset's rooms and turns All off. Off returns to All speakers.</summary>
    public static string? SelectPreset(DailyGroupModel model, string? name, bool on)
    {
        if (!on)
        {
            model.AllSpeakers = true;
            return null;
        }

        var preset = model.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null || Normalize(preset.Rooms).Count == 0)
            return "Group not found.";

        model.AllSpeakers = false;
        model.Rooms = Normalize(preset.Rooms);
        return null;
    }

    public static string? ActivePresetName(DailyGroupModel model)
    {
        if (model.AllSpeakers)
            return null;
        return model.Presets
            .FirstOrDefault(p => SameRooms(p.Rooms, model.Rooms))
            ?.Name;
    }

    /// <summary>
    /// The saved Daily preset, named as a setting. An empty room list is All speakers,
    /// the same fallback the tray uses when nothing is checked. This is not live topology.
    /// </summary>
    public static string DescribePreset(bool allSpeakers, IEnumerable<string>? rooms, IEnumerable<DailyPresetModel>? presets)
    {
        if (allSpeakers)
            return "All speakers";

        var list = Normalize(rooms);
        if (list.Count == 0)
            return "All speakers";

        var name = presets?.FirstOrDefault(p => SameRooms(p.Rooms, list))?.Name?.Trim();
        return string.IsNullOrEmpty(name) ? string.Join(", ", list) : name;
    }

    /// <summary>
    /// Phone Now screen: the per-speaker check list is how a subset is picked.
    /// When All speakers is on, hide it. The switch and the All speakers line are the state.
    /// A named group (All off) keeps the list so rooms can be changed.
    /// </summary>
    public static bool ShowSpeakerChecklist(bool allSpeakers) => !allSpeakers;
}
