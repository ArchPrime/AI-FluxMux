using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services;

public static class JsonFileQuarantine
{
    public static JsonObject ReadObjectOrEmpty(string path, out string notice)
    {
        notice = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is not null)
            {
                return root;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        var bakName = Quarantine(path);
        var fileName = Path.GetFileName(path);
        notice = string.IsNullOrWhiteSpace(bakName)
            ? "AI-FluxMux could not read " + fileName + " and could not move the broken file aside. Starting with empty settings."
            : "AI-FluxMux could not read " + fileName + "; the broken file was renamed to " + bakName + " and a new empty config was used.";
        return new JsonObject();
    }

    public static string Quarantine(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            var bak = Path.GetFileName(path) + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = string.IsNullOrWhiteSpace(directory) ? bak : Path.Combine(directory, bak);
            File.Move(path, dest);
            return Path.GetFileName(dest);
        }
        catch
        {
            return string.Empty;
        }
    }
}