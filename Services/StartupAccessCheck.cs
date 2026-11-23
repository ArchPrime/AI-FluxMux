using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Start-up check that AI-FluxMux can write the files it needs, with a plain-language fix.
///
/// Why: on 23 Nov 2026 a sandboxing tool left a "Low Mandatory Level" integrity label on the
/// project folder. Windows then ran the built exe as a low-integrity process: it could not write
/// its Logs folder (UnauthorizedAccessException on Proxy_Runtime_State.json), every local model
/// launch failed behind a generic "Local model failed to start" dialog, and Windows showed an
/// "Open File - Security Warning". Nothing named the cause, and it took hours to find. This check
/// names it in seconds. It never blocks start-up: it reports and lets the app continue.
/// </summary>
public static class StartupAccessCheck
{
    /// <summary>Windows mandatory integrity RIDs (S-1-16-x).</summary>
    internal const int MediumIntegrityRid = 0x2000;

    /// <summary>
    /// Problems found, each a short sentence; empty when everything is writable and the process
    /// runs at Medium integrity or higher.
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<string> directoriesThatMustBeWritable)
    {
        var problems = new List<string>();
        var integrity = TryGetProcessIntegrityRid();
        if (integrity is int rid && rid < MediumIntegrityRid)
        {
            problems.Add("AI-FluxMux is running at " + DescribeIntegrity(rid)
                + " integrity, so Windows blocks it from writing your normal files. This happens when the"
                + " AI-FluxMux folder (or a folder above it) carries a Low integrity label, often left by a"
                + " sandboxing tool.");
        }

        foreach (var directory in directoriesThatMustBeWritable)
        {
            if (!string.IsNullOrWhiteSpace(directory) && !CanWrite(directory))
            {
                problems.Add("Cannot write to " + directory + ".");
            }
        }

        return problems;
    }

    /// <summary>The message shown when <see cref="Check"/> finds problems.</summary>
    public static string BuildMessage(IReadOnlyList<string> problems, string appFolder)
    {
        return "AI-FluxMux cannot write some of its files, so starting local models and saving state will fail."
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, problems)
            + Environment.NewLine + Environment.NewLine
            + "To fix a Low integrity label: open PowerShell as administrator and run" + Environment.NewLine
            + "    icacls \"" + appFolder.TrimEnd('\\') + "\" /setintegritylevel \"(OI)(CI)M\" /T /C /Q" + Environment.NewLine
            + "(use the topmost folder that has the label if it was set higher up), then start AI-FluxMux again."
            + Environment.NewLine
            + "For a plain permissions problem, give your Windows account Modify rights on the folders above."
            + Environment.NewLine + Environment.NewLine
            + "AI-FluxMux will keep starting so you can read Help, but local launches will not work until this is fixed.";
    }

    internal static string DescribeIntegrity(int rid) => rid switch
    {
        < 0x1000 => "Untrusted",
        < MediumIntegrityRid => "Low",
        < 0x3000 => "Medium",
        < 0x4000 => "High",
        _ => "System",
    };

    /// <summary>A real create-and-delete probe: reflects the token's effective rights, not just the ACL.</summary>
    internal static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".fluxmux-write-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The integrity RID of this process's token, or null when it cannot be read.</summary>
    internal static int? TryGetProcessIntegrityRid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return null;
            }

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label } → Label.Sid is the first pointer.
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
