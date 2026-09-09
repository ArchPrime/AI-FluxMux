using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Avalonia weaves compiled XAML into the assembly after CoreCompile.
/// A side-output build that reuses obj\Debug can copy the un-woven DLL into
/// the launcher folder; that copy still has !AvaloniaResources but no populate
/// trampoline, and AI-FluxMux exits before a window appears.
/// </summary>
public static class CompiledXamlGuard
{
    public const string PopulateTrampolineMarker = "XamlIlPopulateTrampoline";

    public static bool AssemblyContainsCompiledXaml(Assembly assembly)
    {
        if (assembly is null)
        {
            return false;
        }

        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return true;
        }

        return FileContainsCompiledXaml(location);
    }

    public static bool FileContainsCompiledXaml(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return false;
        }

        try
        {
            return ContainsAscii(File.ReadAllBytes(assemblyPath), PopulateTrampolineMarker);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool ContainsAscii(byte[] haystack, string asciiNeedle)
    {
        if (haystack.Length == 0 || string.IsNullOrEmpty(asciiNeedle))
        {
            return false;
        }

        var needle = Encoding.ASCII.GetBytes(asciiNeedle);
        var lastStart = haystack.Length - needle.Length;
        for (var i = 0; i <= lastStart; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
