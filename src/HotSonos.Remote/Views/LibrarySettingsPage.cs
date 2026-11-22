using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

sealed class LibrarySettingsPage : RemotePage
{
    readonly Label _status = RemoteUi.Muted("");
    readonly VerticalStackLayout _folderNotes = new() { Spacing = 6 };
    readonly VerticalStackLayout _folderRows = new() { Spacing = 6 };
    readonly Entry _master = RemoteUi.Field(@"Master folder, optional (\\server\masters)");
    readonly Entry _addRoot = RemoteUi.Field(@"Add folder (\\server\share\Music)");
    readonly Entry _newTag = RemoteUi.Field("New tag label");
    readonly VerticalStackLayout _tags = new();
    readonly Switch _tagMaster = new();
    readonly List<(string Path, Switch Daily)> _roots = [];
    bool _masterReady;

    public LibrarySettingsPage()
    {
        Content = RemoteUi.Page(
            "Library",
            RemoteUi.Back(),
            _status,
            RemoteUi.Section(
                PhoneSections.LibraryFolders,
                "Folders",
                _folderNotes,
                _folderRows,
                RemoteUi.Muted("Checked folders are the Daily mix."),
                _addRoot,
                RemoteUi.Button("Add folder", AddRootAsync),
                RemoteUi.Button("Save library folders", SaveRootsAsync),
                RemoteUi.Button("Discover from Sonos", DiscoverAsync),
                RemoteUi.Button("Rescan", () => RescanAsync(false), danger: true),
                RemoteUi.Button("Force rescan", () => RescanAsync(true), danger: true)),
            RemoteUi.Section(
                PhoneSections.LibraryMaster,
                "Master folder",
                RemoteUi.Muted("A master folder is where tag writes are copied."),
                _master,
                SettingsForm.Labeled("Write tags to the master copy", _tagMaster),
                RemoteUi.Button("Save tag copy", SaveMasterAsync)),
            RemoteUi.Section(
                PhoneSections.LibraryTags,
                "Tags",
                _newTag,
                RemoteUi.Button("Create tag", CreateTagAsync),
                RemoteUi.Muted("Delete removes the key from every file."),
                _tags));
    }

    protected override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    protected override void OnLinkChanged() => _ = LoadAsync();

    Task LoadAsync() => Task.WhenAll(LoadConfigAsync(), LoadTagsAsync(), LoadMasterAsync());

    async Task LoadMasterAsync()
    {
        _masterReady = false;
        if (!AppServices.Link.OnPc)
            return;
        var call = await AppServices.Client.GetSettingsAsync(CancellationToken.None);
        if (call.Value is not { Ok: true } s)
            return;
        _tagMaster.IsToggled = s.TagUpdateMaster;
        _masterReady = true;
    }

    async Task SaveMasterAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Those settings");
            return;
        }

        if (!_masterReady)
        {
            _status.Text = "Settings are still loading.";
            return;
        }

        await Act(ct => AppServices.Client.SaveSettingsAsync(new RemoteSettingsBody { TagUpdateMaster = _tagMaster.IsToggled }, ct), _status);
    }

    async Task LoadConfigAsync()
    {
        _folderNotes.Clear();
        _folderRows.Clear();
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _folderNotes.Add(RemoteUi.NeedsPc("Library folders, master folder, rescan, and tags"));
            return;
        }

        var call = await AppServices.Client.GetLibraryConfigAsync(CancellationToken.None);
        var config = call.Value;
        if (config is null)
        {
            _folderNotes.Add(RemoteUi.Muted(PairingPrompt.Miss(call, "Could not load library settings.")));
            return;
        }

        var scanning = config.Scanning ? " Scanning." : "";
        _folderNotes.Add(RemoteUi.Muted($"{config.TrackCount} tracks in the cache.{scanning} {config.Phase}"));
        if (!string.IsNullOrWhiteSpace(config.LastError))
            _folderNotes.Add(RemoteUi.Muted(config.LastError));
        if (!string.IsNullOrWhiteSpace(config.Note))
            _folderNotes.Add(RemoteUi.Muted(config.Note));
        _roots.Clear();
        foreach (var root in config.Roots)
        {
            var daily = new Switch { IsToggled = root.InDaily };
            _roots.Add((root.Path, daily));
            _folderRows.Add(RootRow(daily, root.Path));
        }

        if (_roots.Count == 0)
            _folderNotes.Add(RemoteUi.Muted("No library folders yet. Discover from Sonos, or add a UNC path."));
        _master.Text = config.MasterRoot ?? "";
    }

    Task AddRootAsync()
    {
        var path = (_addRoot.Text ?? "").Trim();
        if (path.Length == 0)
        {
            _status.Text = "Type a folder path first.";
            return Task.CompletedTask;
        }

        var daily = new Switch { IsToggled = true };
        _roots.Add((path, daily));
        _addRoot.Text = "";
        _folderRows.Insert(0, RootRow(daily, path));
        _status.Text = "Added. Save library folders to keep it.";
        return Task.CompletedTask;
    }

    static View RootRow(Switch daily, string path)
    {
        var label = new Label
        {
            Text = path,
            TextColor = Swatches.Ink,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        Grid.SetColumn(daily, 0);
        Grid.SetColumn(label, 1);
        row.Add(daily);
        row.Add(label);
        return row;
    }

    async Task SaveRootsAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Library folders");
            return;
        }

        if (_roots.Count == 0 && !await Confirm("Clear library folders", "Save an empty folder list on the PC?"))
            return;
        var body = new RemoteLibraryConfigBody
        {
            MasterRoot = _master.Text ?? "",
            Roots = _roots.Select(r => new RemoteLibraryRootBody { Path = r.Path, InDaily = r.Daily.IsToggled }).ToList(),
        };
        var call = await AppServices.Client.SaveLibraryConfigAsync(body, CancellationToken.None);
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            _status.Text = PairingPrompt.Miss(call, "Could not save.");
            return;
        }

        _status.Text = call.Value?.Error ?? call.Value?.Note ?? call.Detail ?? (call.Kind == RemoteCallKind.Ok ? "Saved." : "Could not save.");
        if (call.Kind == RemoteCallKind.Ok && call.Value?.Ok == true)
            await LoadConfigAsync();
    }

    async Task DiscoverAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Discover from Sonos");
            return;
        }

        if (!await Confirm("Discover from Sonos", "Replace the library folder list with the folders Sonos has indexed?"))
            return;
        var call = await AppServices.Client.DiscoverLibraryAsync(CancellationToken.None);
        if (call.Kind == RemoteCallKind.Unauthorized)
        {
            _status.Text = PairingPrompt.Miss(call, "Discover failed.");
            return;
        }

        _status.Text = call.Value?.Error ?? call.Value?.Note ?? call.Detail ?? "Discover finished.";
        await LoadConfigAsync();
    }

    async Task RescanAsync(bool force)
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Rescan");
            return;
        }

        var message = force
            ? "Re-read tags for every file? This can take a long time."
            : "Rescan the library for new and changed files?";
        if (!await Confirm(force ? "Force rescan" : "Rescan", message))
            return;
        await Act(ct => AppServices.Client.RescanAsync(force, ct), _status);
    }

    async Task CreateTagAsync()
    {
        if (!AppServices.Link.OnPc)
        {
            _status.Text = RemoteFailureText.PcRequired("Tags");
            return;
        }

        await Act(ct => AppServices.Client.CreateTagAsync(_newTag.Text ?? "", ct), _status);
        _newTag.Text = "";
        await LoadTagsAsync();
    }

    async Task LoadTagsAsync()
    {
        if (!AppServices.Link.DrivePc || !AppServices.Link.OnPc)
        {
            _tags.Clear();
            _tags.Add(RemoteUi.NeedsPc("Tags, rescan, and writing HOTSONOS_TAGS"));
            return;
        }

        var call = await AppServices.Client.GetTagsAsync(CancellationToken.None);
        _tags.Clear();
        foreach (var tag in call.Value?.Tags ?? [])
        {
            var key = tag.Key;
            var label = tag.Label;
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(new GridLength(2, GridUnitType.Star)), new ColumnDefinition() } };
            var rename = RemoteUi.Button(label, () => RenameAsync(key, label));
            var delete = RemoteUi.Button("Delete", () => DeleteAsync(key, label), danger: true);
            Grid.SetColumn(rename, 0);
            Grid.SetColumn(delete, 1);
            row.Add(rename);
            row.Add(delete);
            _tags.Add(row);
        }
    }

    async Task RenameAsync(string key, string label)
    {
        var next = await DisplayPromptAsync("Rename tag", "New label. Files keep the same key.", initialValue: label);
        if (string.IsNullOrWhiteSpace(next))
            return;
        await Act(ct => AppServices.Client.RenameTagAsync(key, next.Trim(), ct), _status);
        await LoadTagsAsync();
    }

    async Task DeleteAsync(string key, string label)
    {
        if (!await Confirm("Delete tag", $"Delete {label} and strip it from every file that has it?"))
            return;
        await Act(ct => AppServices.Client.DeleteTagAsync(key, ct), _status);
        await LoadTagsAsync();
    }
}
