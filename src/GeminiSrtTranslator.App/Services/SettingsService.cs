using System;
using System.IO;
using System.Text.Json;

namespace GeminiSrtTranslator.Services;

public sealed class AppSettings
{
    public string? GeminiApiKey { get; set; }
}

public static class SettingsService
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = GetSettingsDirectory();
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(Path.Combine(directory, SettingsFileName), json);
    }

    private static string GetSettingsDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "GeminiSrtTranslator");
    }

    private static string GetSettingsPath()
        => Path.Combine(GetSettingsDirectory(), SettingsFileName);
}
