using System.Text.Json;

namespace CodexAccountSwitcher.Services;

public sealed class PanelSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public bool BackgroundRefresh { get; set; } = true;
    public static PanelSettings Load(string root)
    {
        try
        {
            var value = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(Path.Combine(root, "panel-settings.json"))) ?? new();
            value.RefreshMinutes = new[] { 2, 5, 10, 15 }.Contains(value.RefreshMinutes) ? value.RefreshMinutes : 5;
            return value;
        }
        catch { return new(); }
    }
    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "panel-settings.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }
}
