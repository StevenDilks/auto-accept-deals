using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using MelonLoader;
using MelonLoader.Utils;

namespace AutoAcceptDeals;

internal enum LocationMode { Global, PerRegion }
internal enum TimeMode { Fixed, Randomize, WaitForPlayer }

internal sealed record DiscoveredLocation(string Name, string Guid);

/// <summary>
/// Static, single-threaded settings store. All members must be called from the Unity main thread.
/// </summary>
internal static class Settings
{
    private const int CurrentSchemaVersion = 1;
    private const string SettingsFolderName = "AutoAcceptDeals";
    private const string SettingsFileName = "settings.json";

    private static readonly Dictionary<EMapRegion, string?> _regionLocations = new();
    private static readonly Dictionary<EMapRegion, IReadOnlyList<DiscoveredLocation>> _discoveredLocations = new();
    private static bool _persistEnabled = true;
    private static bool _suppressionWarned;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int RoundingMultiple { get; private set; }
    public static LocationMode LocationMode { get; private set; }
    public static string? GlobalLocationGuid { get; private set; }
    public static IReadOnlyDictionary<EMapRegion, string?> RegionLocations => _regionLocations;
    public static TimeMode TimeMode { get; private set; }
    public static EDealWindow FixedWindow { get; private set; }
    public static IReadOnlyDictionary<EMapRegion, IReadOnlyList<DiscoveredLocation>> DiscoveredLocations => _discoveredLocations;

    private static string SettingsPath =>
        Path.Combine(MelonEnvironment.UserDataDirectory, SettingsFolderName, SettingsFileName);

    public static void Load()
    {
        ApplyDefaults();
        _persistEnabled = true;
        _suppressionWarned = false;

        var path = SettingsPath;

        if (!File.Exists(path))
        {
            if (TryPersist())
                MelonLogger.Msg($"Settings file not found; wrote defaults to {path}");
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _persistEnabled = false;
            MelonLogger.Warning($"Settings file unreadable at {path}; using defaults (file left untouched): {ex.Message}");
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            _persistEnabled = false;
            MelonLogger.Warning($"Settings file unreadable at {path}; using defaults (file left untouched): {ex.Message}");
            return;
        }

        bool needsRewrite;
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _persistEnabled = false;
                MelonLogger.Warning($"Settings file at {path} is not a JSON object; using defaults (file left untouched).");
                return;
            }

            bool schemaVersionPresent = root.TryGetProperty("schemaVersion", out var svEl)
                                        && svEl.ValueKind == JsonValueKind.Number
                                        && svEl.TryGetInt32(out _);
            int schemaVersion = TryGetInt(root, "schemaVersion") ?? 0;
            if (schemaVersion > CurrentSchemaVersion)
            {
                _persistEnabled = false;
                MelonLogger.Warning(
                    $"Settings file at {path} has schemaVersion {schemaVersion}, newer than supported {CurrentSchemaVersion}; using defaults (file left untouched).");
                return;
            }

            if (!schemaVersionPresent && root.EnumerateObject().Any(p => p.Name != "schemaVersion"))
            {
                MelonLogger.Warning(
                    $"Settings file at {path} has no schemaVersion; assuming legacy and upgrading to v{CurrentSchemaVersion}.");
            }

            needsRewrite = ApplyFromJson(root);
        }

        MelonLogger.Msg($"Loaded settings from {path}");
        if (needsRewrite) TryPersist();
    }

    public static void SetRoundingMultiple(int v)
    {
        if (v < 0) throw new ArgumentOutOfRangeException(nameof(v), "RoundingMultiple must be >= 0.");
        if (RoundingMultiple == v) return;
        RoundingMultiple = v;
        TryPersist();
    }

    public static void SetLocationMode(LocationMode m)
    {
        if (LocationMode == m) return;
        LocationMode = m;
        TryPersist();
    }

    public static void SetGlobalLocationGuid(string? guid)
    {
        if (GlobalLocationGuid == guid) return;
        GlobalLocationGuid = guid;
        TryPersist();
    }

    public static void SetRegionLocation(EMapRegion r, string? guid)
    {
        if (_regionLocations.TryGetValue(r, out var existing) && existing == guid) return;
        _regionLocations[r] = guid;
        TryPersist();
    }

    public static void SetTimeMode(TimeMode m)
    {
        if (TimeMode == m) return;
        TimeMode = m;
        TryPersist();
    }

    public static void SetFixedWindow(EDealWindow w)
    {
        if (FixedWindow == w) return;
        FixedWindow = w;
        TryPersist();
    }

    public static void RecordDiscoveredLocations(EMapRegion r, IEnumerable<DiscoveredLocation> locs)
    {
        _discoveredLocations[r] = locs.ToArray();
        TryPersist();
    }

    private static void ApplyDefaults()
    {
        RoundingMultiple = 0;
        LocationMode = LocationMode.Global;
        GlobalLocationGuid = null;
        TimeMode = TimeMode.WaitForPlayer;
        FixedWindow = EDealWindow.Morning;

        _regionLocations.Clear();
        _discoveredLocations.Clear();
        foreach (var region in Enum.GetValues<EMapRegion>())
        {
            _regionLocations[region] = null;
            _discoveredLocations[region] = Array.Empty<DiscoveredLocation>();
        }
    }

    private static bool ApplyFromJson(JsonElement root)
    {
        bool needsRewrite = false;

        if (!TryReadInt(root, "roundingMultiple", v => v >= 0, v => RoundingMultiple = v,
                "roundingMultiple must be a non-negative integer"))
            needsRewrite = true;

        if (!TryReadEnum<LocationMode>(root, "locationMode", v => LocationMode = v))
            needsRewrite = true;

        if (!TryReadNullableString(root, "globalLocationGuid", v => GlobalLocationGuid = v))
            needsRewrite = true;

        if (!TryReadEnum<TimeMode>(root, "timeMode", v => TimeMode = v))
            needsRewrite = true;

        if (!TryReadEnum<EDealWindow>(root, "fixedWindow", v => FixedWindow = v))
            needsRewrite = true;

        if (root.TryGetProperty("regionLocations", out var rlEl))
        {
            if (rlEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in rlEl.EnumerateObject())
                {
                    if (!Enum.TryParse<EMapRegion>(prop.Name, ignoreCase: false, out var region))
                    {
                        MelonLogger.Warning($"Settings: regionLocations contains unknown region '{prop.Name}'; ignoring.");
                        needsRewrite = true;
                        continue;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                        _regionLocations[region] = null;
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                        _regionLocations[region] = prop.Value.GetString();
                    else
                    {
                        MelonLogger.Warning($"Settings: regionLocations.{prop.Name} value is invalid; using default (null).");
                        needsRewrite = true;
                    }
                }
            }
            else
            {
                MelonLogger.Warning("Settings: regionLocations must be a JSON object; using defaults.");
                needsRewrite = true;
            }
        }
        else needsRewrite = true;

        if (root.TryGetProperty("discoveredLocations", out var dlEl))
        {
            if (dlEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dlEl.EnumerateObject())
                {
                    if (!Enum.TryParse<EMapRegion>(prop.Name, ignoreCase: false, out var region))
                    {
                        MelonLogger.Warning($"Settings: discoveredLocations contains unknown region '{prop.Name}'; ignoring.");
                        needsRewrite = true;
                        continue;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        MelonLogger.Warning($"Settings: discoveredLocations.{prop.Name} must be an array; using default (empty).");
                        needsRewrite = true;
                        continue;
                    }
                    var list = new List<DiscoveredLocation>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            MelonLogger.Warning($"Settings: discoveredLocations.{prop.Name} has a non-object entry; skipping.");
                            needsRewrite = true;
                            continue;
                        }
                        var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
                        var guid = item.TryGetProperty("guid", out var gEl) && gEl.ValueKind == JsonValueKind.String ? gEl.GetString() : null;
                        if (name == null || guid == null)
                        {
                            MelonLogger.Warning($"Settings: discoveredLocations.{prop.Name} has an entry missing name/guid; skipping.");
                            needsRewrite = true;
                            continue;
                        }
                        list.Add(new DiscoveredLocation(name, guid));
                    }
                    _discoveredLocations[region] = list;
                }
            }
            else
            {
                MelonLogger.Warning("Settings: discoveredLocations must be a JSON object; using defaults.");
                needsRewrite = true;
            }
        }
        else needsRewrite = true;

        return needsRewrite;
    }

    private static bool TryReadInt(JsonElement root, string name, Func<int, bool> validate, Action<int> apply, string requirement)
    {
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) && validate(v))
        {
            apply(v);
            return true;
        }
        MelonLogger.Warning($"Settings: {requirement}; using default.");
        return false;
    }

    private static bool TryReadEnum<TEnum>(JsonElement root, string name, Action<TEnum> apply) where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            // Reject numeric strings — Enum.TryParse would happily turn "1" into the enum at ordinal 1.
            if (s != null && Array.IndexOf(Enum.GetNames(typeof(TEnum)), s) >= 0
                          && Enum.TryParse<TEnum>(s, ignoreCase: false, out var v))
            {
                apply(v);
                return true;
            }
        }
        var observed = el.ValueKind == JsonValueKind.String ? $"'{el.GetString()}'" : el.ValueKind.ToString();
        MelonLogger.Warning($"Settings: {name} value {observed} is not a known {typeof(TEnum).Name}; using default.");
        return false;
    }

    private static bool TryReadNullableString(JsonElement root, string name, Action<string?> apply)
    {
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) { apply(null); return true; }
        if (el.ValueKind == JsonValueKind.String) { apply(el.GetString()); return true; }
        MelonLogger.Warning($"Settings: {name} value is invalid; using default (null).");
        return false;
    }

    private static int? TryGetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            return v;
        return null;
    }

    private static bool TryPersist()
    {
        if (!_persistEnabled)
        {
            if (!_suppressionWarned)
            {
                MelonLogger.Warning(
                    $"Settings changed in memory but writes are disabled because the file at {SettingsPath} could not be parsed at startup. Fix or delete the file and restart to re-enable persistence.");
                _suppressionWarned = true;
            }
            return false;
        }
        try
        {
            SaveInternal();
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to write settings to {SettingsPath}: {ex.Message}");
            return false;
        }
    }

    private static void SaveInternal()
    {
        var dto = new SettingsDto
        {
            SchemaVersion = CurrentSchemaVersion,
            RoundingMultiple = RoundingMultiple,
            LocationMode = LocationMode.ToString(),
            GlobalLocationGuid = GlobalLocationGuid,
            TimeMode = TimeMode.ToString(),
            FixedWindow = FixedWindow.ToString(),
        };

        foreach (var region in Enum.GetValues<EMapRegion>())
        {
            var key = region.ToString();
            dto.RegionLocations[key] = _regionLocations.TryGetValue(region, out var g) ? g : null;
            var list = _discoveredLocations.TryGetValue(region, out var locs) ? locs : Array.Empty<DiscoveredLocation>();
            dto.DiscoveredLocations[key] = list
                .Select(l => new DiscoveredLocationDto { Name = l.Name, Guid = l.Guid })
                .ToList();
        }

        var path = SettingsPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // File.Move clears tmp on success; this only fires if Move threw.
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private sealed class SettingsDto
    {
        public int SchemaVersion { get; set; }
        public int RoundingMultiple { get; set; }
        public string LocationMode { get; set; } = "";
        public string? GlobalLocationGuid { get; set; }
        public Dictionary<string, string?> RegionLocations { get; set; } = new();
        public string TimeMode { get; set; } = "";
        public string FixedWindow { get; set; } = "";
        public Dictionary<string, List<DiscoveredLocationDto>> DiscoveredLocations { get; set; } = new();
    }

    private sealed class DiscoveredLocationDto
    {
        public string Name { get; set; } = "";
        public string Guid { get; set; } = "";
    }
}
