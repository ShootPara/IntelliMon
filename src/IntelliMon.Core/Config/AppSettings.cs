using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelliMon.Core.Config;

/// <summary>
/// Application settings loaded from %APPDATA%\IntelliMon\settings.json.
/// All threshold values are percentages in the range [0, 100].
/// </summary>
public sealed class AppSettings
{
    /// <summary>CPU usage threshold percent. Process is flagged when CPU % >= this value.</summary>
    [JsonPropertyName("cpuThresholdPercent")]
    public double CpuThresholdPercent { get; set; } = 20.0;

    /// <summary>Memory (Working Set) threshold as percent of total physical RAM.</summary>
    [JsonPropertyName("memoryThresholdPercent")]
    public double MemoryThresholdPercent { get; set; } = 10.0;

    /// <summary>Attributed disk active time threshold percent.</summary>
    [JsonPropertyName("diskThresholdPercent")]
    public double DiskThresholdPercent { get; set; } = 15.0;

    /// <summary>How often the overlay rotates to the next qualifying process (seconds).</summary>
    [JsonPropertyName("rotationIntervalSeconds")]
    public double RotationIntervalSeconds { get; set; } = 3.0;

    /// <summary>Gemini API key for AI process enrichment. Leave empty to disable AI enrichment.</summary>
    [JsonPropertyName("geminiApiKey")]
    public string? GeminiApiKey { get; set; }

    /// <summary>Whether to start IntelliMon with Windows.</summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Returns the path to the settings directory: %APPDATA%\IntelliMon\
    /// </summary>
    public static string GetSettingsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IntelliMon");
    }

    /// <summary>
    /// Returns the full path to settings.json.
    /// </summary>
    public static string GetSettingsFilePath()
    {
        return Path.Combine(GetSettingsDirectory(), "settings.json");
    }
}

/// <summary>
/// Loads and saves application settings from/to settings.json in %APPDATA%\IntelliMon\.
/// </summary>
public static class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads settings from disk, creating default settings if the file does not exist.
    /// </summary>
    public static AppSettings Load()
    {
        string path = AppSettings.GetSettingsFilePath();
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings to disk, creating the directory if necessary.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        string dir = AppSettings.GetSettingsDirectory();
        Directory.CreateDirectory(dir);
        string path = AppSettings.GetSettingsFilePath();
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(path, json);
    }
}
