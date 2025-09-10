using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UiaPeek.Domain.Hubs;
using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    public sealed class EventCaptureService(
        IHubContext<PeekHub> hub,
        ILogger<EventCaptureService> logger,
        IUiaPeekRepository repository) : BackgroundService
    {
        #region *** User32    ***
        [DllImport("user32.dll", SetLastError = true)]
        private static extern sbyte GetMessage(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message lpMsg);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Message lpMsg);
        
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProcess lpfn, IntPtr hMod, uint dwThreadId);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);
        
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int nSize);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
            int cchBuff,
            uint wFlags,
            IntPtr dwhkl);
        #endregion

        #region *** Constants ***
        // Amount that the mouse wheel reports per notch (used to normalize wheel deltas)
        private const int WHEEL_DELTA = 120;

        // Low-level keyboard hook identifier for SetWindowsHookEx
        private const int WH_KEYBOARD_LL = 13;

        // Low-level mouse hook identifier for SetWindowsHookEx
        private const int WH_MOUSE_LL = 14;

        // Extended-key flag in KBDLLHOOKSTRUCT.flags (e.g. indicates an extended key)
        private const uint LLKHF_EXTENDED = 0x01;

        private const int WM_KEYDOWN = 0x0100;     // Windows message for a key being pressed
        private const int WM_KEYUP = 0x0101;       // Windows message for a key being released
        private const int WM_SYSKEYDOWN = 0x0104;  // Windows message for a system key being pressed (e.g., Alt+Key)
        private const int WM_SYSKEYUP = 0x0105;    // Windows message for a system key being released

        private const int WM_LBUTTONDOWN = 0x0201; // Left mouse button pressed
        private const int WM_LBUTTONUP = 0x0202;   // Left mouse button released
        private const int WM_MBUTTONDOWN = 0x0207; // Middle mouse button pressed
        private const int WM_MBUTTONUP = 0x0208;   // Middle mouse button released
        private const int WM_MOUSEHWHEEL = 0x020E; // Horizontal mouse wheel moved
        private const int WM_MOUSEMOVE = 0x0200;   // Mouse moved
        private const int WM_MOUSEWHEEL = 0x020A;  // Vertical mouse wheel moved
        private const int WM_RBUTTONDOWN = 0x0204; // Right mouse button pressed
        private const int WM_RBUTTONUP = 0x0205;   // Right mouse button released

        private const int VK_CAPITAL = 0x14;       // Caps Lock virtual key (toggle)
        private const int VK_CONTROL = 0x11;       // Control virtual key (generic)
        private const int VK_LCONTROL = 0xA2;      // Left Control virtual key
        private const int VK_LMENU = 0xA4;         // Left Alt (Menu) virtual key
        private const int VK_LSHIFT = 0xA0;        // Left Shift virtual key
        private const int VK_MENU = 0x12;          // Alt (Menu) virtual key (generic)
        private const int VK_NUMLOCK = 0x90;       // Num Lock virtual key (toggle)
        private const int VK_RCONTROL = 0xA3;      // Right Control virtual key
        private const int VK_RMENU = 0xA5;         // Right Alt (Menu) virtual key
        private const int VK_RSHIFT = 0xA1;        // Right Shift virtual key
        private const int VK_SCROLL = 0x91;        // Scroll Lock virtual key (toggle)
        private const int VK_SHIFT = 0x10;         // Shift virtual key (generic)
        #endregion

        private delegate IntPtr HookProcess(int nCode, IntPtr wParam, IntPtr lParam);

        // Cache what we printed on keydown so keyup matches exactly
        private static readonly Dictionary<uint, string> _keysLog = [];

        // Fields to hold onto the delegate instances and hook handles, preventing GC
        private HookProcess _keyboardCallback;
        private HookProcess _mouseCallback;

        /// <inheritdoc />
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Store the callback delegates to prevent garbage collection.
            // These methods are invoked whenever the hooks capture input.
            _keyboardCallback = ReceiveKeyboardEvent;
            _mouseCallback = ReceiveMouseEvent;

            // Hook handles (initialized to null pointers).
            var keyboardHook = IntPtr.Zero;
            var mouseHook = IntPtr.Zero;

            // Run the message loop in a dedicated background thread.
            return Task.Factory.StartNew(action: () =>
            {
                // Install the global keyboard hook.
                keyboardHook = SetHook(WH_KEYBOARD_LL, _keyboardCallback, out var keyboardError);

                // Install the global mouse hook.
                mouseHook = SetHook(WH_MOUSE_LL, _mouseCallback, out var mouseError);

                // Check if the keyboard hook failed to install.
                if (keyboardHook == IntPtr.Zero)
                {
                    logger.LogError("Failed to install global keyboard hook. " +
                        "Win32 error code: {ErrorCode}", keyboardError);
                    return;
                }

                // Check if the mouse hook failed to install.
                if (mouseHook == IntPtr.Zero)
                {
                    logger.LogError("Failed to install global mouse hook. " +
                        "Win32 error code: {ErrorCode}", mouseError);
                    return;
                }

                // If neither hook installed, warn the user.
                if (keyboardHook == IntPtr.Zero && mouseHook == IntPtr.Zero)
                {
                    logger.LogWarning("No global hooks could be installed. " +
                        "Ensure this process is running in an interactive session with " +
                        "sufficient privileges (e.g., Administrator).");
                    return;
                }

                logger.LogInformation("Input monitoring service is running.");

                // Ensure that when the service is stopped, a WM_QUIT message
                // is posted to break out of the message loop gracefully.
                using var reg = stoppingToken.Register(() =>
                {
                    logger.LogInformation("Shutdown requested. Posting quit message to stop input monitoring.");
                    PostQuitMessage(0);
                });

                // Windows message loop — required for hooks to receive events.
                // GetMessage blocks until a message is retrieved.
                while (GetMessage(out Message msg, IntPtr.Zero, 0, 0) > 0)
                {
                    // Translate virtual key messages into character messages.
                    TranslateMessage(ref msg);

                    // Dispatch the message to the correct window procedure.
                    DispatchMessage(ref msg);
                }

                // Clean up the keyboard hook if it was installed.
                if (keyboardHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(keyboardHook);
                    logger.LogInformation("Keyboard hook successfully uninstalled.");
                }

                // Clean up the mouse hook if it was installed.
                if (mouseHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(mouseHook);
                    logger.LogInformation("Mouse hook successfully uninstalled.");
                }
            },
            cancellationToken: stoppingToken,
            creationOptions: TaskCreationOptions.LongRunning, // Run as a dedicated thread
            scheduler: TaskScheduler.Default);
        }

        // Low-level Windows keyboard hook callback.
        // Captures keyboard events (key down and key up), resolves the key text,
        // and broadcasts the event through SignalR to connected clients.
        private IntPtr ReceiveKeyboardEvent(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Identify the message type (key down / key up).
            var msg = wParam;
            var isValidCode = nCode >= 0;
            var isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            var isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Determine if this is a keyboard message worth processing.
            var isKeyMsg = isValidCode && (isKeyDown || isKeyUp);

            // If it's not a keyboard event, let Windows continue processing as usual.
            if (!isKeyMsg)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Extract keyboard event details from the pointer.
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Variable to hold the resolved key text.
            string keyText;

            if (isKeyDown)
            {
                // Compute human-readable key text on KeyDown.
                keyText = ResolveKeyText(kbd);

                // Cache the key text for use when the corresponding KeyUp occurs.
                _keysLog[kbd.vkCode] = keyText;
            }
            else
            {
                // Attempt to retrieve cached text to ensure consistent KeyUp logging.
                if (!_keysLog.TryGetValue(kbd.vkCode, out keyText))
                {
                    // Fallback: resolve text if no cached entry exists (rare case).
                    keyText = ResolveKeyText(kbd);
                }

                // Remove the cached entry to keep memory clean.
                _keysLog.Remove(kbd.vkCode);
            }

            // Build a structured event model with context.
            var message = new RecordingEventModel
            {
                Chain = repository.Peek(),
                Event = isKeyDown ? "Down" : "Up",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "Keyboard",
                Value = keyText
            };

            // Broadcast the captured event to all connected SignalR clients.
            hub.Clients.All.SendAsync("ReceiveRecordingEvent", new
            {
                Value = message
            });

            // Always pass the event to the next hook to avoid disrupting the system.
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr ReceiveMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                int msg = (int)wParam;
                if (msg == WM_MOUSEMOVE)
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // still ignore move

                // Handle vertical & horizontal wheel with direction
                if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                {
                    // HIGHWORD(mouseData) is a signed delta in multiples of WHEEL_DELTA (120)
                    short delta = unchecked((short)((ms.mouseData >> 16) & 0xFFFF));
                    int notches = Math.Abs(delta) / WHEEL_DELTA;
                    if (notches == 0) notches = 1; // high-res devices can report smaller deltas; normalize to 1

                    if (msg == WM_MOUSEWHEEL)
                    {
                        string dir = delta > 0 ? "up" : "down";
                        Console.WriteLine($"[mouse] wheel {dir} ({notches}) at {ms.pt.X},{ms.pt.Y}");
                    }
                    else // WM_MOUSEHWHEEL
                    {
                        string dir = delta > 0 ? "right" : "left";
                        Console.WriteLine($"[mouse] hwheel {dir} ({notches}) at {ms.pt.X},{ms.pt.Y}");
                    }

                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }

                // Other mouse buttons (unchanged)
                Console.WriteLine($"[mouse] {MapMouseMessage(wParam)} at {ms.pt.X},{ms.pt.Y}");
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // ----------------- key name / char helpers -----------------
        private static string ResolveKeyText(in KBDLLHOOKSTRUCT kbd)
        {
            string typed = VkToChar(kbd.vkCode, kbd.scanCode);
            if (!string.IsNullOrEmpty(typed))
            {
                char c = typed[0];
                if (!char.IsControl(c) && c != ' ')
                {
                    return typed; // printable glyph like "a", "A", "!"
                }
            }

            string name = GetKeyReadableName(kbd.vkCode, kbd.scanCode, kbd.flags);
            return string.Equals(name, "Space", StringComparison.OrdinalIgnoreCase)
                ? "space"
                : name;
        }

        private static string GetKeyReadableName(uint vk, uint scanCode, uint flags)
        {
            // First try localized key name (layout aware)
            int lp = (int)(scanCode << 16);
            if ((flags & LLKHF_EXTENDED) != 0) lp |= 1 << 24;

            var sb = new StringBuilder(64);
            int len = GetKeyNameText(lp, sb, sb.Capacity);
            if (len > 0) return sb.ToString(0, len);

            // Fallback to VK-based name
            return VkToString((int)vk);
        }

        // Produce the actual typed character, respecting Shift/Caps and current layout
        private static string VkToChar(uint vkCode, uint scanCode)
        {
            // Gather full keyboard state with modifiers/toggles
            byte[] ks = new byte[256];
            // Base state
            GetKeyboardState(ks);

            // Explicitly update modifier/toggle keys (ensures correctness in hook thread)
            SetKeyStateBit(ks, VK_SHIFT);
            SetKeyStateBit(ks, VK_LSHIFT);
            SetKeyStateBit(ks, VK_RSHIFT);
            SetKeyStateBit(ks, VK_CONTROL);
            SetKeyStateBit(ks, VK_LCONTROL);
            SetKeyStateBit(ks, VK_RCONTROL);
            SetKeyStateBit(ks, VK_MENU);
            SetKeyStateBit(ks, VK_LMENU);
            SetKeyStateBit(ks, VK_RMENU);

            SetToggleBit(ks, VK_CAPITAL);
            SetToggleBit(ks, VK_NUMLOCK);
            SetToggleBit(ks, VK_SCROLL);

            // Translate using current layout
            var sb = new StringBuilder(8);
            IntPtr layout = GetKeyboardLayout(0);

            int rc = ToUnicodeEx(vkCode, scanCode, ks, sb, sb.Capacity, 0, layout);

            // rc > 0 => count of UTF-16 chars written
            // rc == 0 => no translation (non-char key)
            // rc < 0 => dead-key; we can return empty and let next key produce composition
            if (rc > 0)
            {
                var s = sb.ToString(0, rc);
                // normalize surrogate pairs if any; also keep just first glyph for simplicity
                return s;
            }
            return string.Empty;
        }

        private static void SetKeyStateBit(byte[] ks, int vk)
        {
            short state = GetKeyState(vk);
            if ((state & 0x8000) != 0) ks[vk] |= 0x80;   // key is down
            else ks[vk] &= 0x7F;
        }

        private static void SetToggleBit(byte[] ks, int vk)
        {
            short state = GetKeyState(vk);
            if ((state & 0x0001) != 0) ks[vk] |= 0x01;   // toggled on (e.g., CapsLock)
            else ks[vk] &= 0xFE;
        }

        private static string MapMouseMessage(IntPtr wParam) => (int)wParam switch
        {
            WM_LBUTTONDOWN => "left down",
            WM_LBUTTONUP => "left up",
            WM_RBUTTONDOWN => "right down",
            WM_RBUTTONUP => "right up",
            WM_MBUTTONDOWN => "middle down",
            WM_MBUTTONUP => "middle up",
            WM_MOUSEWHEEL => "wheel",
            _ => $"msg=0x{(int)wParam:X}"
        };

        private static string VkToString(int vk) => vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "LWin",
            0x5C => "RWin",
            0x5D => "Apps",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xA0 => "LShift",
            0xA1 => "RShift",
            0xA2 => "LCtrl",
            0xA3 => "RCtrl",
            0xA4 => "LAlt",
            0xA5 => "RAlt",
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",         // F1..F12
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),   // '0'..'9'
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),   // 'A'..'Z'
            _ => $"VK_{vk}"
        };

        // ----------------- hook setup -----------------

        private static IntPtr SetHook(int idHook, HookProcess proc, out int lastErr)
        {
            var hook = SetWindowsHookEx(idHook, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                lastErr = Marshal.GetLastWin32Error();
                hook = SetWindowsHookEx(idHook, proc, IntPtr.Zero, 0);
                if (hook == IntPtr.Zero) lastErr = Marshal.GetLastWin32Error();
                return hook;
            }
            lastErr = 0;
            return hook;
        }

        // ----------------- interop & consts -----------------

        

        #region *** Structs ***
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }
        #endregion
    }
}
