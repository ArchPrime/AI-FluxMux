// ClineSmolTrigger.cs — Triggers Cline's /smol (compact) command via keyboard simulation.
//
// When FluxMux is acting as a gateway for a Cline-like client (one that reinflates full
// history every turn), the forward-prune and context-paging ContextEngine features are
// defeated: Cline sends the full history again on the next turn, undoing any prune and
// breaking the KV cache prefix. The only way to actually reduce Cline's context is to
// trigger Cline's own compaction (/smol), which writes a .compaction.json file that Cline
// reads on subsequent turns.
//
// Cline does NOT expose a VS Code command for /smol (verified: no cline.compact / cline.smol
// in package.json contributes.commands). The only programmatic path is:
//   1. Focus the VS Code window
//   2. Open the Command Palette (Ctrl+Shift+P)
//   3. Type "Jump to Chat Input" (the title of cline.focusChatInput) + Enter
//   4. Type "/smol" + Enter into the now-focused chat input
//
// This is best-effort: it depends on the VS Code window being findable, the Command Palette
// accepting synthetic input, and the Cline webview accepting keyboard events. If any step
// fails, the user can still manually type /smol in Cline.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FluxMux.Avalonia.Services
{
    /// <summary>
    /// Triggers Cline's /smol (compact) command by simulating keyboard input into the
    /// VS Code window. Used as a "Hail Mary" for Cline-like clients when the context
    /// is approaching the limit.
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

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

                // Step 2: Open the Command Palette (Ctrl+Shift+P).
                SendKeyCombo(VK_CONTROL, VK_SHIFT, 'P');
                System.Threading.Thread.Sleep(900);

                // Step 3: Type "Jump to Chat Input" and press Enter.
                SendText("Jump to Chat Input");
                System.Threading.Thread.Sleep(900);
                SendKey(VK_RETURN);
                System.Threading.Thread.Sleep(500);

                // Step 4: Type the /smol command and press Enter.
                SendText(command);
                System.Threading.Thread.Sleep(300);
                SendKey(VK_RETURN);

                return new SmolTriggerResult(true,
                    $"Triggered Cline's {command} command via Command Palette → Jump to Chat Input. " +
                    "Cline should now be compacting the conversation. If nothing happens, type " +
                    command + " manually in Cline's chat input.");
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