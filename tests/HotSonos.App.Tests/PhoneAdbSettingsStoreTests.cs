using System.Text.Json;
using HotSonos.App.Models;
using HotSonos.App.Services;

namespace HotSonos.App.Tests;

public class PhoneAdbSettingsStoreTests
{
    [Fact]
    public void NewSettings_DefaultToReservedPhoneAddress()
    {
        var settings = new AppSettings();
        Assert.Equal("192.168.1.171", settings.PhoneAdbHost);
        Assert.Equal(34011, settings.PhoneAdbPort);
    }

    [Fact]
    public void EnsureShape_CoercesInvalidAddressWithoutChangingPinOrMcp()
    {
        var settings = AppSettings.CreateDefault();
        settings.RemotePin = "042815";
        settings.RemotePort = 42342;
        settings.McpPort = 42341;
        settings.McpEnabled = true;
        settings.PhoneAdbHost = " ";
        settings.PhoneAdbPort = 70000;

        settings.EnsureShape();

        Assert.Equal(PhoneAdb.DefaultHost, settings.PhoneAdbHost);
        Assert.Equal(PhoneAdb.DefaultPort, settings.PhoneAdbPort);
        Assert.Equal("042815", settings.RemotePin);
        Assert.Equal(42342, settings.RemotePort);
        Assert.Equal(42341, settings.McpPort);
        Assert.True(settings.McpEnabled);
        Assert.Equal("F8", settings.ShuffleLibrary.Key);
    }

    [Fact]
    public void EnsureShape_KeepsAValidCustomEndpoint()
    {
        var settings = AppSettings.CreateDefault();
        settings.PhoneAdbHost = " pixel.lan ";
        settings.PhoneAdbPort = 37123;
        settings.EnsureShape();
        Assert.Equal("pixel.lan", settings.PhoneAdbHost);
        Assert.Equal(37123, settings.PhoneAdbPort);
    }

    [Fact]
    public void Save_RoundTripsPhoneAdb_AndWritesAddressFile()
    {
        using var dir = new TempConfigDir();
        var store = new ConfigStore(dir.SettingsPath);
        var settings = AppSettings.CreateDefault();
        settings.RemotePin = "042815";
        settings.RemotePort = 42342;
        settings.McpPort = 42341;
        settings.McpEnabled = true;
        settings.PhoneAdbHost = "192.168.1.171";
        settings.PhoneAdbPort = 34011;

        store.Save(settings);

        using (var doc = JsonDocument.Parse(File.ReadAllText(dir.SettingsPath)))
        {
            Assert.Equal("192.168.1.171", doc.RootElement.GetProperty("PhoneAdbHost").GetString());
            Assert.Equal(34011, doc.RootElement.GetProperty("PhoneAdbPort").GetInt32());
            Assert.Equal("042815", doc.RootElement.GetProperty("RemotePin").GetString());
            Assert.Equal(42342, doc.RootElement.GetProperty("RemotePort").GetInt32());
            Assert.Equal(42341, doc.RootElement.GetProperty("McpPort").GetInt32());
            Assert.True(doc.RootElement.GetProperty("McpEnabled").GetBoolean());
        }

        Assert.Equal("192.168.1.171:34011", File.ReadAllText(dir.AddressPath).Trim());

        var loaded = store.Load();
        Assert.Equal("192.168.1.171", loaded.PhoneAdbHost);
        Assert.Equal(34011, loaded.PhoneAdbPort);
        Assert.Equal("042815", loaded.RemotePin);
        Assert.Equal(42341, loaded.McpPort);
        Assert.Equal("F8", loaded.ShuffleLibrary.Key);
    }

    [Fact]
    public void Load_CoercesInvalidJson_AndStillWritesTheAddressFile()
    {
        using var dir = new TempConfigDir();
        File.WriteAllText(dir.SettingsPath, """
            {
              "RemotePin": "042815",
              "RemotePort": 42342,
              "McpPort": 42341,
              "PhoneAdbHost": "",
              "PhoneAdbPort": 0
            }
            """);

        var loaded = new ConfigStore(dir.SettingsPath).Load();

        Assert.Equal(PhoneAdb.DefaultHost, loaded.PhoneAdbHost);
        Assert.Equal(PhoneAdb.DefaultPort, loaded.PhoneAdbPort);
        Assert.Equal("042815", loaded.RemotePin);
        Assert.Equal(42342, loaded.RemotePort);
        Assert.Equal(42341, loaded.McpPort);
        Assert.Equal("192.168.1.171:34011", File.ReadAllText(dir.AddressPath).Trim());
    }

    [Fact]
    public void Load_MissingFile_WritesTheDefaultAddress()
    {
        using var dir = new TempConfigDir();
        var loaded = new ConfigStore(dir.SettingsPath).Load();
        Assert.Equal(PhoneAdb.DefaultHost, loaded.PhoneAdbHost);
        Assert.Equal(PhoneAdb.DefaultPort, loaded.PhoneAdbPort);
        Assert.Equal(PhoneAdb.FormatEndpoint(PhoneAdb.DefaultHost, PhoneAdb.DefaultPort), File.ReadAllText(dir.AddressPath).Trim());
    }

    private sealed class TempConfigDir : IDisposable
    {
        public TempConfigDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hotsonos-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string SettingsPath => System.IO.Path.Combine(Path, "settings.json");
        public string AddressPath => System.IO.Path.Combine(Path, PhoneAdb.AddressFileName);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { /* temp cleanup */ }
        }
    }
}
