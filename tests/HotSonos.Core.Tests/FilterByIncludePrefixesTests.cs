using System.Xml.Linq;
using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class FilterByIncludePrefixesTests
{
    private static (string Uri, XElement Item) Track(string cifsUri) =>
        (cifsUri, new XElement("item"));

    [Fact]
    public void Filter_keeps_only_daily_folder_tracks()
    {
        var tracks = new List<(string Uri, XElement Item)>
        {
            Track("x-file-cifs://192.168.1.111/Music/Sonos/A/1.flac"),
            Track("x-file-cifs://192.168.1.111/Music/Jazz/B/2.flac"),
            Track("x-file-cifs://192.168.1.111/Music/Seasonal/Christmas/C/3.flac"),
        };

        var (scoped, filtered) = SonosController.FilterByIncludePrefixes(
            tracks,
            [@"\\192.168.1.111\Music\Sonos"]);

        Assert.Single(scoped);
        Assert.Equal(2, filtered);
        Assert.Contains("Sonos", scoped[0].Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filter_null_or_empty_keeps_all()
    {
        var tracks = new List<(string Uri, XElement Item)>
        {
            Track("x-file-cifs://h/s/a.flac"),
            Track("x-file-cifs://h/s/b.flac"),
        };
        var (a, fa) = SonosController.FilterByIncludePrefixes(tracks, null);
        var (b, fb) = SonosController.FilterByIncludePrefixes(tracks, []);
        Assert.Equal(2, a.Count);
        Assert.Equal(0, fa);
        Assert.Equal(2, b.Count);
        Assert.Equal(0, fb);
    }
}
