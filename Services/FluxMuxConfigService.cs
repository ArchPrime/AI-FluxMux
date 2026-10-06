using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Threading;

namespace FluxMux.Avalonia.Services;

public sealed class FluxMuxConfigService
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public FluxMuxConfigService(string configPath)
    {
        _configPath = configPath;
    }

    public string LastLoadNotice { get; private set; } = string.Empty;

    /// <summary>
    /// Path of the rolling backup of the last good config. Written on every successful
    /// save; used as a fallback if the live config is missing or corrupt on load.
    /// </summary>
    private string PrevPath => _configPath + ".prev";

    public FluxMuxConfigDocument Load()
    {
        var root = JsonFileQuarantine.ReadObjectOrEmpty(_configPath, out var notice);
        LastLoadNotice = notice;

        // If the live config was missing or unreadable, try the rolling backup before
        // starting with empty settings. This is the key resilience fix: a bluescreen
        // that corrupts the live config should not wipe all settings — the previous
        // good copy is recovered instead.
        if (root.Count == 0 && !string.IsNullOrWhiteSpace(notice))
        {
            var prevRoot = JsonFileQuarantine.ReadObjectOrEmpty(PrevPath, out var prevNotice);
            if (prevRoot.Count > 0)
            {
                notice = "The main config could not be read, so the last good backup was restored. "
                    + "If settings look wrong, check the quarantined files next to "
                    + Path.GetFileName(_configPath) + ".";
                root = prevRoot;
            }
        }

        LastLoadNotice = notice;
        return new FluxMuxConfigDocument(root);
    }

    public void Save(FluxMuxConfigDocument document)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = document.Root.ToJsonString(_serializerOptions);

        // Rolling backup: before overwriting the live config, keep a copy of the
        // current good one. If the new write later corrupts the live file (e.g. a
        // crash mid-move), Load() can fall back to this.
        TryCopyPreviousConfig();

        WriteAllTextResilient(_configPath, json);
    }

    private void TryCopyPreviousConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, PrevPath, overwrite: true);
            }
        }
        catch
        {
            // Best effort. If the backup can't be written, the save still proceeds;
            // we just lose the fallback for this particular save.
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> without ever throwing.
    /// The config file can be held open by another process (a second AI-FluxMux instance,
    /// an editor, or a file watcher on .vscode). A failed save must never take the app down,
    /// so this writes to a temp file, retries while the target is locked, then atomically
    /// replaces it. On final failure it swallows the error rather than propagating it.
    /// </summary>
    private static void WriteAllTextResilient(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.WriteAllText(tempPath, contents);
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (IOException)
            {
                // Target (or temp) is locked by another process; back off and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same class of failure on some setups; retry.
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort; a stray .tmp file is harmless.
                }
            }

            Thread.Sleep(120 * (attempt + 1));
        }

        // Give up quietly. Losing one geometry/settings save is acceptable;
        // crashing the whole app over a locked config file is not.
    }

    public string ConfigPath => _configPath;
}

public sealed class FluxMuxConfigDocument
{
    public FluxMuxConfigDocument(JsonObject root)
    {
        Root = root;
    }

    public JsonObject Root { get; }

    public string GetString(string key, string defaultValue = "")
    {
        var value = Root[key];
        return value?.GetValue<string>() ?? defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        var value = Root[key];
        if (value is null)
        {
            return defaultValue;
        }

        try
        {
            return value.GetValue<int>();
        }
        catch
        {
            return defaultValue;
        }
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        var value = Root[key];
        if (value is null)
        {
            return defaultValue;
        }

        try
        {
            return value.GetValue<bool>();
        }
        catch
        {
            return defaultValue;
        }
    }

    public IReadOnlyList<string> GetStringArray(string key)
    {
        if (Root[key] is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(node => node?.ToString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    public void SetString(string key, string value)
    {
        Root[key] = value;
    }

    public void SetInt(string key, int value)
    {
        Root[key] = value;
    }

    public void SetBool(string key, bool value)
    {
        Root[key] = value;
    }

    public void SetStringArray(string key, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                array.Add(value.Trim());
            }
        }

        Root[key] = array;
    }

    public IReadOnlyList<string> GetObjectKeys(string key)
    {
        if (Root[key] is not JsonObject obj)
        {
            return Array.Empty<string>();
        }

        var keys = new List<string>();
        foreach (var kvp in obj)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                keys.Add(kvp.Key);
            }
        }

        return keys;
    }

    public IReadOnlyList<JsonObject> GetObjectArray(string key)
    {
        if (Root[key] is not JsonArray array)
        {
            return Array.Empty<JsonObject>();
        }

        return array
            .OfType<JsonObject>()
            .ToList();
    }

    public void SetObjectArray(string key, IEnumerable<JsonObject> objects)
    {
        var array = new JsonArray();
        foreach (var obj in objects)
        {
            array.Add(obj);
        }

        Root[key] = array;
    }
}
