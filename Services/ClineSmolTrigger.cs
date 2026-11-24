// ClineSmolTrigger.cs — Triggers Cline's /smol (compact) command via keyboard simulation
// plus a UI-Automation click of Cline's chat send button.
//
// When FluxMux is acting as a gateway for a Cline-like client (one that reinflates full
// history every turn), Cline sends the full history again on the next turn, so shortening
// what AI-FluxMux forwards does not shrink Cline's own copy. The only way to actually reduce Cline's context is to
// trigger Cline's own compaction (/smol), which writes a .compaction.json file that Cline
// reads on subsequent turns.
//
// Cline does NOT expose a VS Code command for /smol (verified: no cline.compact / cline.smol
// in package.json contributes.commands). The only programmatic path is:
//   1. Focus the VS Code window
//   2. Open the Command Palette (Ctrl+Shift+P)
//   3. Type "Jump to Chat Input" (the title of cline.focusChatInput) + Enter
//   4. Type "/smol" into the now-focused chat input
//   5. CLICK Cline's chat send button (the "go" arrow) via UI Automation.
//
// IMPORTANT: In Cline, pressing Enter does NOT submit a slash command — only the send
// ("go" arrow) button does. So step 5 must click that button. The button lives inside
// VS Code's Chromium webview; we find it through the Windows UI Automation API by its
// CSS class "codicon-send" (Cline's stable class for the chat send icon) and invoke it
// via the Invoke pattern. If the button cannot be located, we fall back to pressing Enter
// (which leaves the command typed so the user can click send manually).
//
// The UI Automation work is done by LOADING System.Windows.Automation REFLECTIVELY from
// the Windows Desktop shared runtime (UIAutomationClient.dll / UIAutomationTypes.dll),
// because the main app targets net10.0 (cross-platform Avalonia) and deliberately avoids
// a WPF/net10.0-windows reference. On non-Windows, or if the runtime is missing, the
// reflection fails gracefully and the caller falls back to Enter.
//
// This is best-effort: it depends on the VS Code window being findable, the Command Palette
// accepting synthetic input, the Cline webview accepting keyboard events, and the send
// button being present (Cline chat panel open). If any step fails, the user can still
// manually type /smol and click send in Cline.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;

namespace FluxMux.Avalonia.Services
{
    /// <summary>
    /// Triggers Cline's /smol (compact) command by simulating keyboard input into the
    /// VS Code window and then clicking Cline's chat send button via UI Automation.
    /// Used as a "Hail Mary" for Cline-like clients when the context is approaching
    /// the limit.
    /// </summary>
    public static class ClineSmolTrigger
    {
        // ── Win32 P/Invoke ──────────────────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        // ── SendInput structures ────────────────────────────────────────────────────

        [Flags]
        private enum KEYEVENTF : uint
        {
            KEYEVENTF_EXTENDEDKEY = 0x0001,
            KEYEVENTF_KEYUP = 0x0002,
            KEYEVENTF_UNICODE = 0x0004,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type; // 0 = INPUT_KEYBOARD
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Virtual key codes
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_MENU = 0x12; // Alt key — used to unlock SetForegroundWindow
        private const int ASFW_ANY = -1;

        // ── Public API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Triggers Cline's /smol command by focusing the VS Code window, opening the
        /// Command Palette, invoking "Jump to Chat Input", then typing /smol.
        /// </summary>
        /// <param name="vsCodeWindowTitle">
        /// The title of the VS Code window to target. If null, the first visible
        /// "Visual Studio Code" window is used.
        /// </param>
        /// <param name="command">
        /// The slash command to type into Cline's chat input. Defaults to "/smol".
        /// </param>
        /// <returns>A result describing what happened.</returns>
        public static SmolTriggerResult TriggerSmol(string? vsCodeWindowTitle = null, string command = "/smol")
        {
            try
            {
                // Step 1: Find and focus the VS Code window.
                IntPtr hwnd = FindVsCodeWindow(vsCodeWindowTitle);
                if (hwnd == IntPtr.Zero)
                {
                    return new SmolTriggerResult(false, "Could not find a VS Code window. Is VS Code running?");
                }

                if (!FocusWindow(hwnd))
                {
                    return new SmolTriggerResult(false, $"Found VS Code window (handle {hwnd}) but could not bring it to the foreground.");
                }

                // Small delay to let the window settle.
                System.Threading.Thread.Sleep(400);

                // Step 2: Wait for Cline to be idle before issuing the command. Cline REFUSES
                // /smol while a turn is running (its compact handler checks isRunning and bails).
                // The send button is disabled during a turn and enabled when idle, so we poll
                // its enabled state. This is the "synchronise command issuance with an idle
                // chat" step. We give it up to 90s to become idle; if it never does, we report
                // that clearly rather than firing a command Cline will reject.
                const int idleTimeoutMs = 90_000;
                const int idlePollMs = 1_500;
                if (!WaitForClineIdle(hwnd, idleTimeoutMs, idlePollMs))
                {
                    return new SmolTriggerResult(false,
                        $"Cline's chat was not idle within {idleTimeoutMs / 1000}s (send button stayed disabled / not found). " +
                        "Cline refuses /smol while a turn is running, so the command was NOT issued. " +
                        "Try again once Cline finishes its current turn, or type /smol manually when it is idle.");
                }

                // The send button being enabled doesn't guarantee Cline is truly idle — it can
                // be enabled while Cline is still finishing post-processing (parsing the response,
                // updating the UI, etc.). Wait a few seconds to give Cline time to settle.
                System.Threading.Thread.Sleep(5_000);

                // Step 3: Set the /smol command into Cline's chat input via UI Automation's
                // ValuePattern. This is far more reliable than keyboard simulation: the Cline
                // chat input is a Chromium webview element that does NOT reliably receive
                // synthetic SendInput keystrokes (even when VS Code is foreground). But it DOES
                // expose a UIA Value pattern that lets us set its text directly.
                string setValueOutcome = SetClineChatInput(hwnd, command);
                if (setValueOutcome.StartsWith("UIA-DIAG:", StringComparison.Ordinal))
                {
                    // Fallback: try the old keyboard path (Command Palette + Jump to Chat Input + type).
                    // This may work on some systems; keep the diagnostic so the log shows why
                    // the direct SetValue failed.
                    SendKeyCombo(VK_CONTROL, VK_SHIFT, 'P');
                    System.Threading.Thread.Sleep(900);
                    SendText("Jump to Chat Input");
                    System.Threading.Thread.Sleep(900);
                    SendKey(VK_RETURN);
                    System.Threading.Thread.Sleep(500);
                    SendText(command);
                    System.Threading.Thread.Sleep(400);
                    setValueOutcome = $"direct SetValue failed ({setValueOutcome}); fell back to keyboard simulation (Command Palette + Jump to Chat Input + type {command}).";
                }

                // Step 4: Click Cline's chat send button via UI Automation. In Cline, Enter does
                // NOT submit a slash command — only the send ("go" arrow) button does. We only
                // get here when the button was confirmed enabled (idle), so the click should
                // actually submit the command.
                // Give Cline a moment to register the text before clicking send.
                System.Threading.Thread.Sleep(500);
                // Re-focus the VS Code window right before clicking, in case focus was lost
                // during the UIA operations.
                FocusWindow(hwnd);
                System.Threading.Thread.Sleep(200);
                string sendOutcome = ClickClineSendButton(hwnd);
                if (sendOutcome.StartsWith("UIA-DIAG:", StringComparison.Ordinal))
                {
                    // Last-resort fallback: press Enter. This may not submit the command, but
                    // it is better than doing nothing. Keep the diagnostic so the log shows
                    // exactly why the UI Automation click failed.
                    SendKey(VK_RETURN);
                    sendOutcome = $"send button not found via UI Automation; pressed Enter as a fallback (may not submit - click Cline's send arrow manually if {command} did not run). {sendOutcome}";
                }

                return new SmolTriggerResult(true,
                    $"Waited for Cline idle, then set {command} into Cline's chat input ({setValueOutcome}). " + sendOutcome +
                    " Cline should now be compacting the conversation.");
            }
            catch (Exception ex)
            {
                return new SmolTriggerResult(false, $"Failed to trigger Cline's /smol: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the VS Code window by title. If no title is specified, looks for any
        /// visible window whose title contains "Visual Studio Code".
        /// </summary>
        private static IntPtr FindVsCodeWindow(string? title)
        {
            if (!string.IsNullOrEmpty(title))
            {
                // VS Code uses Chrome_WidgetWin_1 as its top-level window class.
                IntPtr hwnd = FindWindow("Chrome_WidgetWin_1", title);
                if (hwnd != IntPtr.Zero) return hwnd;

                // Fallback: try without the class name.
                hwnd = FindWindow(null, title);
                if (hwnd != IntPtr.Zero) return hwnd;

                return IntPtr.Zero;
            }

            // No specific title: try the known workspace title first.
            IntPtr hwnd2 = FindWindow("Chrome_WidgetWin_1", "Workspace - Visual Studio Code");
            if (hwnd2 != IntPtr.Zero) return hwnd2;

            // Fallback: try any Chrome_WidgetWin_1 window and check its title.
            hwnd2 = FindWindow("Chrome_WidgetWin_1", null);
            if (hwnd2 != IntPtr.Zero)
            {
                var sb = new StringBuilder(512);
                if (GetWindowText(hwnd2, sb, sb.Capacity) > 0 &&
                    sb.ToString().Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
                {
                    return hwnd2;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Brings the window to the foreground and restores it if minimized.
        /// Uses AttachThreadInput to work around Windows foreground-lock restrictions.
        /// </summary>
        private static bool FocusWindow(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                System.Threading.Thread.Sleep(200);
            }

            // Allow any process to set the foreground window. This bypasses the
            // foreground-lock restriction that prevents SetForegroundWindow from
            // working when another app has focus.
            AllowSetForegroundWindow(-1); // ASFW_ANY (0xFFFFFFFF as int)

            // Attach to the foreground thread to bypass foreground-lock restrictions.
            IntPtr fgHwnd = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fgHwnd, out _);
            uint ourThread = GetCurrentThreadId();

            bool attached = false;
            if (fgThread != 0 && fgThread != ourThread)
            {
                attached = AttachThreadInput(ourThread, fgThread, true);
            }

            bool result = SetForegroundWindow(hwnd);

            if (attached)
            {
                AttachThreadInput(ourThread, fgThread, false);
            }

            // Alt-key tap trick: if SetForegroundWindow still failed, simulate an Alt key
            // press which temporarily disables the foreground lock, then try again.
            if (!result)
            {
                // Alt down
                var altDown = new INPUT[] { MakeKeyInput(0x12, false) };
                SendInput((uint)altDown.Length, altDown, Marshal.SizeOf<INPUT>());
                System.Threading.Thread.Sleep(50);
                result = SetForegroundWindow(hwnd);
                // Alt up
                var altUp = new INPUT[] { MakeKeyInput(0x12, true) };
                SendInput((uint)altUp.Length, altUp, Marshal.SizeOf<INPUT>());
            }

            return result;
        }

        // ── Keyboard input helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Sends a key combination (modifier keys + main key) via SendInput.
        /// </summary>
        private static void SendKeyCombo(ushort modifierVk, ushort secondModifierVk, char mainKey)
        {
            var inputs = new INPUT[]
            {
                MakeKeyInput(modifierVk, false),
                MakeKeyInput(secondModifierVk, false),
                MakeKeyInput((ushort)mainKey, false),
                MakeKeyInput((ushort)mainKey, true),
                MakeKeyInput(secondModifierVk, true),
                MakeKeyInput(modifierVk, true),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// Sends a single key press via SendInput.
        /// </summary>
        private static void SendKey(ushort vk)
        {
            var inputs = new INPUT[]
            {
                MakeKeyInput(vk, false),
                MakeKeyInput(vk, true),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// Sends a string of text via SendInput using Unicode key events.
        /// </summary>
        private static void SendText(string text)
        {
            var inputs = new INPUT[text.Length * 2];
            int i = 0;
            foreach (char c in text)
            {
                inputs[i++] = MakeUnicodeInput(c, false);
                inputs[i++] = MakeUnicodeInput(c, true);
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        // ── UI Automation (reflective): click Cline's chat "send" button ────────────
        //
        // Cline's chat input does not submit a slash command on Enter; it requires the
        // send ("go" arrow) button to be clicked. That button lives inside VS Code's
        // Chromium webview. We locate it through the Windows UI Automation API and
        // invoke it via the Invoke pattern.
        //
        // The main app targets net10.0 (cross-platform Avalonia) and deliberately avoids
        // a WPF/net10.0-windows reference. So instead of compiling against
        // System.Windows.Automation, we LOAD IT REFLECTIVELY at runtime from the
        // Windows Desktop shared runtime (UIAutomationClient.dll + UIAutomationTypes.dll),
        // which is present on any Windows machine running .NET. This keeps the code
        // cross-platform-compilable; the reflection simply fails (returns null) on
        // non-Windows or if the runtime is missing, and the caller falls back to Enter.
        //
        // The send button is identified by its CSS class "codicon-send" (Cline's stable
        // class for the chat send icon), not by its accessible name (which is empty).

        [SupportedOSPlatform("windows")]
        private static readonly Lazy<Assembly?> _uiaClientAssembly = new(LoadUiaClientAssembly);
        [SupportedOSPlatform("windows")]
        private static readonly Lazy<Assembly?> _uiaTypesAssembly = new(LoadUiaTypesAssembly);

        // A dedicated, non-collectible AssemblyLoadContext that resolves the full
        // Windows Desktop dependency chain (WindowsBase, UIAutomationProvider,
        // DirectWriteForwarder, PresentationCore, ...). The main app targets net10.0 and
        // does not itself reference these, so the default load context may not resolve
        // them; pre-loading every DLL from the runtime dir into this ALC guarantees they
        // do. Created lazily and shared between the two Load* methods.
        [SupportedOSPlatform("windows")]
        private static readonly Lazy<AssemblyLoadContext?> _uiaLoadContext = new(CreateUiaLoadContext);

        /// <summary>
        /// Locates the Windows Desktop shared runtime directory that contains
        /// UIAutomationClient.dll. Returns null if it cannot be found.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string? FindWindowsDesktopRuntimeDir()
        {
            // The Windows Desktop shared runtime lives under the .NET install dir.
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                candidates.Add(System.IO.Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App"));
            }
            // Common install locations.
            candidates.Add(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App");
            candidates.Add(@"C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App");

            foreach (var baseDir in candidates)
            {
                if (!System.IO.Directory.Exists(baseDir)) continue;
                // Pick the highest version subfolder.
                var versions = System.IO.Directory.GetDirectories(baseDir)
                    .Select(System.IO.Path.GetFileName)
                    .Where(n => System.Version.TryParse(n, out _))
                    .OrderByDescending(n => System.Version.Parse(n!))
                    .ToList();
                if (versions.Count > 0)
                {
                    return System.IO.Path.Combine(baseDir, versions[0]!);
                }
            }
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static AssemblyLoadContext? CreateUiaLoadContext()
        {
            try
            {
                string? dir = FindWindowsDesktopRuntimeDir();
                if (dir == null) return null;
                var alc = new AssemblyLoadContext("FluxMuxUia", isCollectible: false);
                foreach (var dll in System.IO.Directory.GetFiles(dir, "*.dll"))
                {
                    try { alc.LoadFromAssemblyPath(dll); }
                    catch { /* ignore individual load failures; the ones we need will load */ }
                }
                return alc;
            }
            catch
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static Assembly? LoadUiaClientAssembly()
        {
            try
            {
                var alc = _uiaLoadContext.Value;
                if (alc == null) return null;
                string? dir = FindWindowsDesktopRuntimeDir();
                if (dir == null) return null;
                string path = System.IO.Path.Combine(dir, "UIAutomationClient.dll");
                if (!System.IO.File.Exists(path)) return null;
                return alc.LoadFromAssemblyPath(path);
            }
            catch
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static Assembly? LoadUiaTypesAssembly()
        {
            try
            {
                var alc = _uiaLoadContext.Value;
                if (alc == null) return null;
                string? dir = FindWindowsDesktopRuntimeDir();
                if (dir == null) return null;
                string path = System.IO.Path.Combine(dir, "UIAutomationTypes.dll");
                if (!System.IO.File.Exists(path)) return null;
                return alc.LoadFromAssemblyPath(path);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the given text (e.g. "/smol") into Cline's chat input using UI Automation's
        /// Value pattern. The Cline chat input is a Chromium webview element that does not
        /// reliably receive synthetic keyboard events, but it exposes a UIA Value pattern
        /// (ControlType.Edit, Name "Type a message...") that lets us set its text directly.
        /// Returns a human-readable description. On success the string starts with "set";
        /// on failure it starts with "UIA-DIAG:" and describes where the lookup failed.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string SetClineChatInput(IntPtr vsCodeHwnd, string text)
        {
            if (!OperatingSystem.IsWindows())
            {
                return "UIA-DIAG: not on Windows";
            }

            try
            {
                Assembly? clientAsm = _uiaClientAssembly.Value;
                Assembly? typesAsm = _uiaTypesAssembly.Value;
                if (clientAsm == null || typesAsm == null)
                {
                    return "UIA-DIAG: could not load UIAutomationClient/UIAutomationTypes from the Windows Desktop runtime";
                }

                Type autoElemType = clientAsm.GetType("System.Windows.Automation.AutomationElement")
                    ?? throw new InvalidOperationException("AutomationElement type not found");
                Type treeScopeType = typesAsm.GetType("System.Windows.Automation.TreeScope")
                    ?? throw new InvalidOperationException("TreeScope type not found");
                Type conditionType = clientAsm.GetType("System.Windows.Automation.Condition")
                    ?? throw new InvalidOperationException("Condition type not found");

                // Get the root automation element for the VS Code window.
                object root = autoElemType.GetMethod("FromHandle")!.Invoke(null, new object[] { vsCodeHwnd })!;

                // FindAll(TreeScope.Descendants, Condition.TrueCondition)
                object descendants = Enum.ToObject(treeScopeType, 4)!;
                object trueCondition = conditionType.GetField("TrueCondition")!.GetValue(null)!;
                MethodInfo findAll = autoElemType.GetMethod("FindAll", new[] { treeScopeType, conditionType })!;
                object collection = findAll.Invoke(root, new[] { descendants, trueCondition })!;

                // ValuePattern type + its static Pattern field.
                Type valuePatternType = clientAsm.GetType("System.Windows.Automation.ValuePattern")
                    ?? throw new InvalidOperationException("ValuePattern type not found");
                object valuePatternId = valuePatternType.GetField("Pattern")!.GetValue(null)!;

                // Enumerate all descendants and find the Cline chat input:
                // an Edit control whose Name contains "Type a message".
                object enumerator = collection.GetType().GetMethod("GetEnumerator")!.Invoke(collection, null)!;
                MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext")!;
                PropertyInfo currentProp = enumerator.GetType().GetProperty("Current")!;

                while ((bool)moveNext.Invoke(enumerator, null)!)
                {
                    object el = currentProp.GetValue(enumerator)!;
                    // el may be a wrapper that has a Current property, or the AutomationElement itself.
                    object current = el;
                    var currentPropOnEl = el.GetType().GetProperty("Current");
                    if (currentPropOnEl != null)
                    {
                        try { current = currentPropOnEl.GetValue(el)!; }
                        catch { /* fall back to el */ }
                    }

                    string? name = (string?)current.GetType().GetProperty("Name")!.GetValue(current);
                    if (name == null || !name.Contains("Type a message", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Verify it exposes the Value pattern and is not read-only.
                    var patternArgs = new object?[] { valuePatternId, null };
                    bool hasValue = (bool)autoElemType.GetMethod("TryGetCurrentPattern")!.Invoke(el, patternArgs)!;
                    if (!hasValue || patternArgs[1] == null)
                    {
                        continue;
                    }
                    object valuePattern = patternArgs[1]!;
                    object vpCurrent = valuePattern.GetType().GetProperty("Current")!.GetValue(valuePattern)!;
                    bool isReadOnly = (bool)vpCurrent.GetType().GetProperty("IsReadOnly")!.GetValue(vpCurrent)!;
                    if (isReadOnly)
                    {
                        continue;
                    }

                    // Set the text.
                    valuePattern.GetType().GetMethod("SetValue")!.Invoke(valuePattern, new[] { text });
                    return $"set chat input to \"{text}\" via UI Automation Value pattern";
                }

                return "UIA-DIAG: Cline chat input (Edit named 'Type a message...') not found. Is the Cline chat panel open and visible?";
            }
            catch (Exception ex)
            {
                return $"UIA-DIAG: exception during chat-input SetValue: {ex.Message}";
            }
        }

        /// <summary>
        /// Reads the current text from Cline's chat input via UI Automation's ValuePattern.
        /// Returns the text, or null if the input cannot be found or read.
        /// Used to verify whether a submitted command was accepted (input cleared) or
        /// rejected (input still has text).
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string? GetClineChatInputText(IntPtr vsCodeHwnd)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                Assembly? clientAsm = _uiaClientAssembly.Value;
                Assembly? typesAsm = _uiaTypesAssembly.Value;
                if (clientAsm == null || typesAsm == null)
                {
                    return null;
                }

                Type autoElemType = clientAsm.GetType("System.Windows.Automation.AutomationElement")!;
                Type treeScopeType = typesAsm.GetType("System.Windows.Automation.TreeScope")!;
                Type conditionType = clientAsm.GetType("System.Windows.Automation.Condition")!;
                Type valuePatternType = clientAsm.GetType("System.Windows.Automation.ValuePattern")!;

                object root = autoElemType.GetMethod("FromHandle")!.Invoke(null, new object[] { vsCodeHwnd })!;
                object descendants = Enum.ToObject(treeScopeType, 4)!;
                object trueCondition = conditionType.GetField("TrueCondition")!.GetValue(null)!;
                MethodInfo findAll = autoElemType.GetMethod("FindAll", new[] { treeScopeType, conditionType })!;
                object collection = findAll.Invoke(root, new[] { descendants, trueCondition })!;

                object enumerator = collection.GetType().GetMethod("GetEnumerator")!.Invoke(collection, null)!;
                MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext")!;
                PropertyInfo currentProp = enumerator.GetType().GetProperty("Current")!;

                object valuePatternId = valuePatternType.GetField("Pattern")!.GetValue(null)!;

                while ((bool)moveNext.Invoke(enumerator, null)!)
                {
                    object el = currentProp.GetValue(enumerator)!;
                    object current = el;
                    var currentPropOnEl = el.GetType().GetProperty("Current");
                    if (currentPropOnEl != null)
                    {
                        try { current = currentPropOnEl.GetValue(el)!; }
                        catch { /* fall back to el */ }
                    }

                    string? name = (string?)current.GetType().GetProperty("Name")!.GetValue(current);
                    if (name == null || !name.Contains("Type a message", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var patternArgs = new object?[] { valuePatternId, null };
                    bool hasValue = (bool)autoElemType.GetMethod("TryGetCurrentPattern")!.Invoke(el, patternArgs)!;
                    if (!hasValue || patternArgs[1] == null)
                    {
                        continue;
                    }
                    object valuePattern = patternArgs[1]!;
                    object vpCurrent = valuePattern.GetType().GetProperty("Current")!.GetValue(valuePattern)!;
                    string? value = (string?)vpCurrent.GetType().GetProperty("Value")!.GetValue(vpCurrent);
                    return value;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks the state of Cline's chat send button (codicon-send) via UI Automation.
        /// Returns (found, enabled). When Cline is running a turn, the send button is
        /// disabled (the webview adds a "disabled" class and UIA reports IsEnabled=false);
        /// when idle it is enabled. This is the signal we use to detect an idle chat.
        /// If UI Automation is unavailable or the button isn't found, found=false.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static (bool found, bool enabled) GetClineSendButtonState(IntPtr vsCodeHwnd)
        {
            if (!OperatingSystem.IsWindows())
            {
                return (false, false);
            }

            try
            {
                Assembly? clientAsm = _uiaClientAssembly.Value;
                Assembly? typesAsm = _uiaTypesAssembly.Value;
                if (clientAsm == null || typesAsm == null)
                {
                    return (false, false);
                }

                Type autoElemType = clientAsm.GetType("System.Windows.Automation.AutomationElement")!;
                Type treeScopeType = typesAsm.GetType("System.Windows.Automation.TreeScope")!;
                Type conditionType = clientAsm.GetType("System.Windows.Automation.Condition")!;

                object root = autoElemType.GetMethod("FromHandle")!.Invoke(null, new object[] { vsCodeHwnd })!;
                object descendants = Enum.ToObject(treeScopeType, 4)!;
                object trueCondition = conditionType.GetField("TrueCondition")!.GetValue(null)!;
                MethodInfo findAll = autoElemType.GetMethod("FindAll", new[] { treeScopeType, conditionType })!;
                object collection = findAll.Invoke(root, new[] { descendants, trueCondition })!;

                object enumerator = collection.GetType().GetMethod("GetEnumerator")!.Invoke(collection, null)!;
                MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext")!;
                PropertyInfo currentProp = enumerator.GetType().GetProperty("Current")!;

                while ((bool)moveNext.Invoke(enumerator, null)!)
                {
                    object el = currentProp.GetValue(enumerator)!;
                    // el may be a wrapper that has a Current property, or the AutomationElement itself.
                    object current = el;
                    var currentPropOnEl = el.GetType().GetProperty("Current");
                    if (currentPropOnEl != null)
                    {
                        try { current = currentPropOnEl.GetValue(el)!; }
                        catch { /* fall back to el */ }
                    }
                    string? className = (string?)current.GetType().GetProperty("ClassName")!.GetValue(current);
                    if (className == null || !className.Contains("codicon-send"))
                    {
                        continue;
                    }
                    bool enabled = (bool)current.GetType().GetProperty("IsEnabled")!.GetValue(current)!;
                    return (true, enabled);
                }

                return (false, false);
            }
            catch
            {
                // Best-effort: if the UIA tree can't be read right now, report "not found"
                // so the caller treats it as "not confirmed idle" and keeps polling.
                return (false, false);
            }
        }

        /// <summary>
        /// Waits until Cline's chat send button is enabled (i.e. Cline is idle between turns),
        /// polling every <paramref name="pollIntervalMs"/> up to <paramref name="timeoutMs"/>.
        /// Returns true if the button became enabled within the timeout; false otherwise.
        /// This is the core of the "synchronise command issuance with an idle chat" protocol:
        /// Cline refuses /smol while a turn is running, so we must wait for the idle window.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static bool WaitForClineIdle(IntPtr vsCodeHwnd, int timeoutMs, int pollIntervalMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var (found, enabled) = GetClineSendButtonState(vsCodeHwnd);
                if (found && enabled)
                {
                    // Confirmation: wait a brief moment and verify the button is still enabled.
                    // This avoids a race condition where the button is momentarily enabled
                    // while Cline is still finishing its response.
                    System.Threading.Thread.Sleep(500);
                    var (foundConfirm, enabledConfirm) = GetClineSendButtonState(vsCodeHwnd);
                    if (foundConfirm && enabledConfirm)
                    {
                        return true;
                    }
                    // Button became disabled again — keep polling.
                }
                System.Threading.Thread.Sleep(pollIntervalMs);
            }
            // Final check after the loop.
            var (foundFinal, enabledFinal) = GetClineSendButtonState(vsCodeHwnd);
            return foundFinal && enabledFinal;
        }

        /// <summary>
        /// Attempts to locate and click Cline's chat send button inside the given VS Code
        /// window using UI Automation. Returns a human-readable description of what happened.
        /// On success the string starts with "clicked"; on failure it starts with "UIA-DIAG:"
        /// and describes exactly where the lookup failed (so the caller can log it).
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string ClickClineSendButton(IntPtr vsCodeHwnd)
        {
            if (!OperatingSystem.IsWindows())
            {
                return "UIA-DIAG: not on Windows";
            }

            Assembly? clientAsm = _uiaClientAssembly.Value;
            Assembly? typesAsm = _uiaTypesAssembly.Value;
            if (clientAsm == null || typesAsm == null)
            {
                return "UIA-DIAG: could not load UIAutomationClient/UIAutomationTypes from the Windows Desktop runtime";
            }

            try
            {
                Type autoElemType = clientAsm.GetType("System.Windows.Automation.AutomationElement")
                    ?? throw new MissingMethodException("AutomationElement type not found");
                Type treeScopeType = typesAsm.GetType("System.Windows.Automation.TreeScope")
                    ?? throw new MissingMethodException("TreeScope type not found");
                Type conditionType = clientAsm.GetType("System.Windows.Automation.Condition")
                    ?? throw new MissingMethodException("Condition type not found");
                Type invokePatternType = clientAsm.GetType("System.Windows.Automation.InvokePattern")
                    ?? throw new MissingMethodException("InvokePattern type not found");

                // AutomationElement.FromHandle(hwnd)
                object root = autoElemType.GetMethod("FromHandle")!.Invoke(null, new object[] { vsCodeHwnd })!;

                // TreeScope.Descendants (= 4)
                object descendants = System.Enum.ToObject(treeScopeType, 4)!;

                // Condition.TrueCondition
                object trueCondition = conditionType.GetField("TrueCondition")!.GetValue(null)!;

                // root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                MethodInfo findAll = autoElemType.GetMethod("FindAll", new[] { treeScopeType, conditionType })
                    ?? throw new MissingMethodException("FindAll(TreeScope, Condition) not found");
                object collection = findAll.Invoke(root, new[] { descendants, trueCondition })!;
                int totalDescendants = (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;

                // Walk all descendants; find the element whose ClassName contains "codicon-send".
                var enumerator = collection.GetType().GetMethod("GetEnumerator")!.Invoke(collection, null)!;
                MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext")!;
                PropertyInfo currentProp = enumerator.GetType().GetProperty("Current")!;

                int sendCandidates = 0;
                while ((bool)moveNext.Invoke(enumerator, null)!)
                {
                    object el = currentProp.GetValue(enumerator)!;
                    // el may be a wrapper (e.g. AutomationElementInformation) that has a Current property,
                    // or it may be the AutomationElement itself. Try both paths.
                    object current = el;
                    var currentPropOnEl = el.GetType().GetProperty("Current");
                    if (currentPropOnEl != null)
                    {
                        try { current = currentPropOnEl.GetValue(el)!; }
                        catch
                        {
                            // Current property getter failed — fall back to using el directly.
                            // This can happen if the UIA tree is in an inconsistent state.
                        }
                    }
                    string? className = (string?)current.GetType().GetProperty("ClassName")!.GetValue(current);
                    if (className == null || !className.Contains("codicon-send"))
                    {
                        continue;
                    }

                    sendCandidates++;

                    // Verify it is enabled.
                    bool enabled = (bool)current.GetType().GetProperty("IsEnabled")!.GetValue(current)!;
                    if (!enabled)
                    {
                        continue;
                    }

                    // PRIMARY: try the UIA Invoke pattern first. It works even if the window
                    // isn't in the foreground, and doesn't depend on screen coordinates.
                    try
                    {
                        object invokePatternId = invokePatternType.GetField("Pattern")!.GetValue(null)!;
                        var patternArgs = new object?[] { invokePatternId, null };
                        bool hasInvoke = (bool)autoElemType.GetMethod("TryGetCurrentPattern")!.Invoke(el, patternArgs)!;
                        if (hasInvoke && patternArgs[1] != null)
                        {
                            object invokePattern = patternArgs[1]!;
                            invokePattern.GetType().GetMethod("Invoke")!.Invoke(invokePattern, null);
                            return "clicked Cline's send button (codicon-send) via UI Automation Invoke pattern";
                        }
                    }
                    catch
                    {
                        // Invoke failed — fall through to physical click.
                    }

                    // FALLBACK: do a real mouse click at the button's screen coordinates.
                    try
                    {
                        object rectObj = current.GetType().GetProperty("BoundingRectangle")!.GetValue(current)!;
                        double x = (double)rectObj.GetType().GetProperty("X")!.GetValue(rectObj)!;
                        double y = (double)rectObj.GetType().GetProperty("Y")!.GetValue(rectObj)!;
                        double w = (double)rectObj.GetType().GetProperty("Width")!.GetValue(rectObj)!;
                        double h = (double)rectObj.GetType().GetProperty("Height")!.GetValue(rectObj)!;
                        if (w > 0 && h > 0)
                        {
                            int cx = (int)(x + w / 2.0);
                            int cy = (int)(y + h / 2.0);
                            SetCursorPos(cx, cy);
                            System.Threading.Thread.Sleep(80);
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                            System.Threading.Thread.Sleep(40);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                            return $"clicked Cline's send button (codicon-send) at screen ({cx},{cy}) via physical mouse click";
                        }
                    }
                    catch
                    {
                        // BoundingRectangle unavailable or click failed.
                    }
                }

                return $"UIA-DIAG: no clickable codicon-send button found (scanned {totalDescendants} descendants, {sendCandidates} candidate(s) but none enabled/clickable). Is the Cline chat panel open and visible?";
            }
            catch (Exception ex)
            {
                // UI Automation is best-effort; report the failure reason so it can be logged.
                // Unwrap TargetInvocationException to surface the real underlying error
                // (e.g. "element is disabled" when Cline is mid-turn).
                string detail = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? $"{ex.Message} (inner: {tie.InnerException.Message})"
                    : ex.Message;
                // Include the stack trace to help diagnose which reflection call failed.
                string stack = ex.StackTrace != null ? string.Join(" | ", ex.StackTrace.Split('\n').Take(5)) : "";
                return $"UIA-DIAG: exception during UI Automation lookup: {detail} [stack: {stack}]";
            }
        }

        private static INPUT MakeKeyInput(ushort vk, bool keyUp)
        {
            return new INPUT
            {
                type = 0, // INPUT_KEYBOARD
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyUp ? (uint)KEYEVENTF.KEYEVENTF_KEYUP : 0u,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    }
                }
            };
        }

        private static INPUT MakeUnicodeInput(char c, bool keyUp)
        {
            return new INPUT
            {
                type = 0, // INPUT_KEYBOARD
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = (uint)(KEYEVENTF.KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF.KEYEVENTF_KEYUP : 0)),
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    }
                }
            };
        }
    }

    /// <summary>
    /// Result of a Cline /smol trigger attempt.
    /// </summary>
    public readonly record struct SmolTriggerResult(bool Success, string Message)
    {
        public override string ToString() => Success ? Message : $"FAILED: {Message}";
    }
}