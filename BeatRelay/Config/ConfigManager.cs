using System;
using System.IO;
using System.Text.Json;

namespace BeatRelay.Config;

public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public ConfigManager(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException("Config directory is required.", nameof(configDirectory));
        }

        ConfigDirectory = configDirectory;
        ConfigPath = Path.Combine(configDirectory, "BeatRelay.json");
    }

    public string ConfigDirectory { get; }

    public string ConfigPath { get; }

    public OverlayConfig LoadOrCreate()
    {
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(ConfigPath))
        {
            var created = new OverlayConfig();
            Save(created);
            return created;
        }

        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<OverlayConfig>(json, SerializerOptions) ?? new OverlayConfig();
        config.Normalize(!json.Contains("\"ScaleSemanticsVersion\""));
        Save(config);
        return config;
    }

    public void Save(OverlayConfig config)
    {
        config.Normalize();
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
