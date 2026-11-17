using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MyFences.Models;

namespace MyFences.Services;

internal sealed class ConfigurationStore
{
    internal static string Root { get; private set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyFences");
    private string _path => System.IO.Path.Combine(Root, "config.json");
    internal static void UseTestRoot(string path) => Root = System.IO.Path.GetFullPath(path);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    internal string? RecoveryNotice { get; private set; }

    internal Configuration Load()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(_path)) return new Configuration();
        try { return Read(_path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            string preserved = _path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.Copy(_path, preserved, false);
            if (File.Exists(_path + ".bak"))
            {
                try
                {
                    var recovered = Read(_path + ".bak");
                    RecoveryNotice = "Настройки восстановлены из резервной копии. Повреждённый файл сохранён рядом с config.json.";
                    return recovered;
                }
                catch (Exception backupError) when (backupError is JsonException or InvalidDataException or IOException)
                { System.Diagnostics.Debug.WriteLine(backupError); }
            }
            // Не записываем пустую конфигурацию поверх неизвестного состояния.
            throw new InvalidDataException("Не удалось прочитать настройки. Исходный файл сохранён: " + preserved, ex);
        }
    }

    private Configuration Read(string path)
    {
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), _options)
            ?? throw new InvalidDataException("Пустая конфигурация.");
        if (config.Version != 1 || config.Fences == null || config.Fences.Any(f => f == null) ||
            config.Fences.Any(f => f.Id == Guid.Empty) || config.Fences.Select(f => f.Id).Distinct().Count() != config.Fences.Count)
            throw new InvalidDataException("Недопустимый формат конфигурации.");
        foreach (var fence in config.Fences)
        {
            fence.Title = string.IsNullOrWhiteSpace(fence.Title) ? "Блок" : fence.Title.Trim();
            fence.Width = Math.Clamp(fence.Width, 220, 8000);
            fence.Height = Math.Clamp(fence.Height, 120, 8000);
        }
        return config;
    }

    internal void Save(Configuration configuration)
    {
        Directory.CreateDirectory(Root);
        string temporary = _path + ".tmp";
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(configuration, _options));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(data); stream.Flush(true); }
        if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
        else File.Move(temporary, _path);
    }

    internal static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(System.IO.Path.Combine(Root, "errors.log"), DateTime.Now.ToString("O") + " " + error + Environment.NewLine);
        }
        catch (Exception logError) { System.Diagnostics.Debug.WriteLine(logError); }
    }
}
