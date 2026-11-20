using System.IO;
using System.Text.Json;
using HotSonos.App.Models;
using HotSonos.App.Services;

namespace HotSonos.App.Wpf.Tests;

public class PhoneAdbSettingsTests
{
    [Fact]
    public void Save_WritesHostAndPort_AndEndpointFile_WithoutAPairingCode()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new ConfigStore(path);
            var settings = AppSettings.CreateDefault();
            settings.PhoneAdbHost = "192.168.1.50";
            settings.PhoneAdbPort = 37123;
            store.Save(settings);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("192.168.1.50", doc.RootElement.GetProperty("PhoneAdbHost").GetString());
            Assert.Equal(37123, doc.RootElement.GetProperty("PhoneAdbPort").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("PhoneAdbPairCode", out _));
            Assert.False(doc.RootElement.TryGetProperty("PairingCode", out _));
            Assert.Equal("192.168.1.50:37123", File.ReadAllText(Path.Combine(dir, "phone-adb.txt")));

            var loaded = store.Load();
            Assert.Equal("192.168.1.50", loaded.PhoneAdbHost);
            Assert.Equal(37123, loaded.PhoneAdbPort);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_FillsDefaultsWhenTheFilePredatesWirelessDebugging()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "ActiveRoom": "Office" }""");
            var loaded = new ConfigStore(path).Load();
            Assert.Equal("Office", loaded.ActiveRoom);
            Assert.Equal("192.168.1.171", loaded.PhoneAdbHost);
            Assert.Equal(34011, loaded.PhoneAdbPort);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_ReplacesInvalidHostAndPortBeforeWritingTheEndpointFile()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var settings = AppSettings.CreateDefault();
            settings.PhoneAdbHost = "  ";
            settings.PhoneAdbPort = 70000;
            new ConfigStore(path).Save(settings);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("192.168.1.171", doc.RootElement.GetProperty("PhoneAdbHost").GetString());
            Assert.Equal(34011, doc.RootElement.GetProperty("PhoneAdbPort").GetInt32());
            Assert.Equal("192.168.1.171:34011", File.ReadAllText(Path.Combine(dir, "phone-adb.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateDefault_UsesTheCurrentPhone()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal("192.168.1.171", settings.PhoneAdbHost);
        Assert.Equal(34011, settings.PhoneAdbPort);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-phoneadb-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
