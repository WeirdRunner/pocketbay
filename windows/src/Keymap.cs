using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pocketbay;

/// Serialised in camelCase ("tap", "joystick", …) to match the Mac app.
public enum ControlType
{
    Tap,       // hold a key → hold a finger on this spot
    Joystick,  // four direction keys drive a virtual stick
    Aim,       // toggle key turns mouse movement into camera drags
    Fire,      // mouse button fires while aiming
    Look,      // hold a key to free-look while aiming
}

public static class ControlTypeInfo
{
    public static string Title(this ControlType t) => t switch
    {
        ControlType.Tap => "Button", ControlType.Joystick => "Joystick", ControlType.Aim => "Mouse aim",
        ControlType.Fire => "Fire", _ => "Free look",
    };
}

/// One mapped control. Positions are normalised (0…1) to the Android screen.
/// Field names match the Mac app's JSON so layout files work on both.
public sealed class Control
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    [JsonPropertyName("type")] public ControlType Type { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("key")] public InputKey? Key { get; set; }
    /// Joystick directions: up, left, down, right.
    [JsonPropertyName("keys")] public List<InputKey>? Keys { get; set; }
    [JsonPropertyName("boostKey")] public InputKey? BoostKey { get; set; }
    /// Joystick radius as a fraction of screen height.
    [JsonPropertyName("radius")] public double? Radius { get; set; }
    [JsonPropertyName("sensitivity")] public double? Sensitivity { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    /// Opens a menu (bag, map): pressing it during mouse aim shows the cursor.
    [JsonPropertyName("showsCursor")] public bool? ShowsCursor { get; set; }
    /// Press opens, release closes.
    [JsonPropertyName("holdToOpen")] public bool? HoldToOpen { get; set; }

    public IEnumerable<InputKey> Inputs()
    {
        if (Key is { } k) yield return k;
        if (Keys != null) foreach (var d in Keys) yield return d;
        if (BoostKey is { } b) yield return b;
    }

    public Control Clone()
    {
        var c = (Control)MemberwiseClone();
        if (Keys != null) c.Keys = [.. Keys];
        return c;
    }

    static InputKey K(int mac) => InputKey.KeyCode(mac);

    public static Control New(ControlType type, double x = 0.5, double y = 0.5) => type switch
    {
        ControlType.Tap => new() { Type = type, X = x, Y = y, Label = "Button" },
        ControlType.Joystick => new() { Type = type, X = x, Y = y, Keys = [K(13), K(0), K(1), K(2)], BoostKey = K(56), Radius = 0.12, Label = "Move" },
        ControlType.Aim => new() { Type = type, X = x, Y = y, Key = K(50), Sensitivity = 1.0, Label = "Mouse aim" },
        ControlType.Fire => new() { Type = type, X = x, Y = y, Key = InputKey.Mouse(0), Label = "Fire" },
        _ => new() { Type = type, X = x, Y = y, Key = K(58), Sensitivity = 1.0, Label = "Free look" },
    };
}

public sealed class Keymap
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
    [JsonPropertyName("name")] public string Name { get; set; } = "BGMI";
    /// Android packages this layout applies to; empty = every app.
    [JsonPropertyName("packages")] public List<string> Packages { get; set; } = [];
    [JsonPropertyName("controls")] public List<Control> Controls { get; set; } = [];

    public Keymap Clone() => new() { Id = Id, Name = Name, Packages = [.. Packages], Controls = Controls.Select(c => c.Clone()).ToList() };

    static InputKey K(int mac) => InputKey.KeyCode(mac);

    /// BGMI "Layout 2" HUD at 16:9 — identical to the Mac app's default.
    public static Keymap Bgmi() => new()
    {
        Name = "BGMI",
        Packages = ["com.pubg.imobile"],
        Controls =
        [
            new() { Type = ControlType.Joystick, X = 0.181, Y = 0.690, Keys = [K(13), K(0), K(1), K(2)], BoostKey = K(56), Radius = 0.085, Label = "Move" },
            new() { Type = ControlType.Aim, X = 0.746, Y = 0.235, Key = K(50), Sensitivity = 1.0, Label = "Mouse aim" },
            new() { Type = ControlType.Fire, X = 0.850, Y = 0.747, Key = InputKey.Mouse(0), Label = "Fire" },
            new() { Type = ControlType.Look, X = 0.509, Y = 0.515, Key = K(58), Sensitivity = 1.0, Label = "Free look" },
            new() { Type = ControlType.Tap, X = 0.960, Y = 0.521, Key = InputKey.Mouse(1), Label = "Scope" },
            new() { Type = ControlType.Tap, X = 0.954, Y = 0.686, Key = K(49), Label = "Jump" },
            new() { Type = ControlType.Tap, X = 0.854, Y = 0.934, Key = K(8), Label = "Crouch" },
            new() { Type = ControlType.Tap, X = 0.938, Y = 0.940, Key = K(6), Label = "Prone" },
            new() { Type = ControlType.Tap, X = 0.560, Y = 0.362, Key = K(15), Label = "Reload" },
            new() { Type = ControlType.Tap, X = 0.432, Y = 0.905, Key = K(18), Label = "Gun 1" },
            new() { Type = ControlType.Tap, X = 0.564, Y = 0.905, Key = K(19), Label = "Gun 2" },
            new() { Type = ControlType.Tap, X = 0.691, Y = 0.937, Key = K(5), Label = "Throw" },
            new() { Type = ControlType.Tap, X = 0.307, Y = 0.934, Key = K(4), Label = "Heal" },
            new() { Type = ControlType.Tap, X = 0.672, Y = 0.686, Key = K(3), Label = "Open door" },
            new() { Type = ControlType.Tap, X = 0.676, Y = 0.539, Key = K(14), Label = "Get in vehicle" },
            new() { Type = ControlType.Tap, X = 0.071, Y = 0.909, Key = K(48), Label = "Bag", ShowsCursor = true, HoldToOpen = true },
            new() { Type = ControlType.Tap, X = 0.932, Y = 0.127, Key = K(46), Label = "Map", ShowsCursor = true, HoldToOpen = true },
        ],
    };
}

/// Saved layouts: one JSON file each in %APPDATA%\Pocketbay\Layouts.
public static class KeymapStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static string FileFor(string id) => Path.Combine(Paths.Layouts, id + ".json");

    public static Keymap? Read(string path)
    {
        try { return JsonSerializer.Deserialize<Keymap>(File.ReadAllText(path), Json); }
        catch (Exception e) { Log.Write($"Bad layout {path}: {e.Message}"); return null; }
    }

    public static List<Keymap> All()
    {
        var maps = Directory.GetFiles(Paths.Layouts, "*.json").Select(Read).OfType<Keymap>().ToList();
        if (maps.Count == 0)
        {
            var d = Keymap.Bgmi();
            Save(d);
            maps.Add(d);
        }
        return maps.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Keymap Active()
    {
        var maps = All();
        var id = Settings.Current.ActiveLayout;
        return maps.FirstOrDefault(m => m.Id == id) ?? maps[0];
    }

    public static void SetActive(string id) { Settings.Current.ActiveLayout = id; Settings.Save(); }

    public static void Save(Keymap map) => File.WriteAllText(FileFor(map.Id), JsonSerializer.Serialize(map, Json));

    public static void Delete(string id) { try { File.Delete(FileFor(id)); } catch { } }

    public static void Export(Keymap map, string path) => File.WriteAllText(path, JsonSerializer.Serialize(map, Json));
}

/// Small persisted preferences.
public sealed class Settings
{
    public string? ActiveLayout { get; set; }
    public bool KeymapEnabled { get; set; } = true;
    public bool ShowKeyHints { get; set; } = true;
    /// "standard" (emulator default), "angle" (ANGLE/Vulkan) or "software".
    public string Graphics { get; set; } = "angle";
    /// Bumped when the default graphics mode changes, to migrate old settings once.
    public int GraphicsDefaultVersion { get; set; }
    public bool LicenseAccepted { get; set; }
    public double[]? WindowRect { get; set; }

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings)) ?? new(); }
        catch { s = new(); }
        // v1.0.7: compatibility graphics (ANGLE/Vulkan) became the default because Unreal
        // Engine games (BGMI) crash after their logo with the emulator's standard GLES driver.
        if (s.GraphicsDefaultVersion < 2)
        {
            if (s.Graphics == "standard") s.Graphics = "angle";
            s.GraphicsDefaultVersion = 2;
            try { File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }
        return s;
    }

    public static void Save()
    {
        try { File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
