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
    /// <summary>
    /// Background service that installs and manages global low-level keyboard
    /// and mouse hooks using the Win32 API.  
    /// Captured input events are translated into structured models and
    /// broadcast in real time to connected SignalR clients via <see cref="PeekHub"/>.  
    /// </summary>
    /// <param name="hub">SignalR hub context used to broadcast captured input events to all connected clients.</param>
    /// <param name="logger">Logger for diagnostics, error reporting, and lifecycle information about the service.</param>
    /// <param name="repository">Repository used to resolve the current UI element chain at the time of an event,providing context for recorded input.</param>
    public sealed class EventCaptureService(
        IHubContext<PeekHub> hub,
        ILogger<EventCaptureService> logger,
        IUiaPeekRepository repository) : BackgroundService
    {
        #region *** User32    ***
        // Passes the hook information to the next hook procedure in the current hook chain.
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // Dispatches a message to a window procedure.
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Message lpMsg);

        // Retrieves the active keyboard layout for the specified thread.
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        // Copies the status of 256 virtual keys to the provided buffer.
        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        // Retrieves a human-readable key name for a virtual key / scan code combination.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int nSize);

        // Retrieves the status of the specified virtual key.
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        // Retrieves a message from the calling thread's message queue.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern sbyte GetMessage(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        // Retrieves a module handle for the specified module.
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // Posts a WM_QUIT message to the thread message queue, signaling message loop termination.
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        // Installs an application-defined hook procedure into a hook chain.
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProcess lpfn, IntPtr hMod, uint dwThreadId);

        // Translates virtual-key messages into character messages (e.g., WM_CHAR) and posts them to the message queue.
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message lpMsg);

        // Converts a virtual-key code and keyboard state to the corresponding Unicode character(s)
        // using a specified keyboard layout.
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
            int cchBuff,
            uint wFlags,
            IntPtr dwhkl);

        // Removes a hook procedure installed in a hook chain.
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
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

        #region *** Delegates ***
        /// <summary>
        /// Defines the signature for hook procedures used with <see cref="SetWindowsHookEx"/>.  
        /// This delegate is invoked for low-level keyboard and mouse events captured globally,  
        /// allowing custom processing before the event continues down the hook chain.
        /// </summary>
        /// <param name="nCode">Hook code (≥0 to process, &lt;0 to skip).</param>
        /// <param name="wParam">Event message identifier (e.g., WM_KEYDOWN, WM_MOUSEMOVE).</param>
        /// <param name="lParam">Pointer to event data (KBDLLHOOKSTRUCT / MSLLHOOKSTRUCT).</param>
        /// <returns>Result of <see cref="CallNextHookEx"/> for proper propagation.</returns>
        private delegate IntPtr HookProcess(int nCode, IntPtr wParam, IntPtr lParam);
        #endregion

        #region *** Fields    ***
        // Cache of pressed keys: stores the resolved key text on KeyDown so that
        // the corresponding KeyUp event can reuse the exact same string.
        private static readonly Dictionary<uint, string> _keysLog = [];

        // Delegate reference for the low-level keyboard hook callback.  
        // Must be kept alive to prevent garbage collection while the hook is active.
        private HookProcess _keyboardCallback;

        // Delegate reference for the low-level mouse hook callback.  
        // Must be kept alive to prevent garbage collection while the hook is active.
        private HookProcess _mouseCallback;
        #endregion

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
                keyboardHook = InitializeWindowsHook(WH_KEYBOARD_LL, _keyboardCallback, out var keyboardError);

                // Install the global mouse hook.
                mouseHook = InitializeWindowsHook(WH_MOUSE_LL, _mouseCallback, out var mouseError);

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

        #region *** Methods   ***
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
            var kbd = Marshal.PtrToStructure<KeyboardHook>(lParam);

            // Variable to hold the resolved key text.
            string keyText;

            // Handle key down and key up events separately.
            if (isKeyDown)
            {
                // Compute human-readable key text on KeyDown.
                keyText = ResolveKeyName(kbd);

                // Cache the key text for use when the corresponding KeyUp occurs.
                _keysLog[kbd.vkCode] = keyText;
            }
            else
            {
                // Attempt to retrieve cached text to ensure consistent KeyUp logging.
                if (!_keysLog.TryGetValue(kbd.vkCode, out keyText))
                {
                    // Fallback: resolve text if no cached entry exists (rare case).
                    keyText = ResolveKeyName(kbd);
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
                Value = new
                {
                    ScanCode = kbd.scanCode,
                    VirtualKey = kbd.vkCode,
                    Key = keyText
                }
            };

            // Broadcast the captured event to all connected SignalR clients.
            hub.Clients.All.SendAsync("ReceiveRecordingEvent", new
            {
                Value = message
            });

            // Always pass the event to the next hook to avoid disrupting the system.
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Low-level Windows mouse hook callback.
        // Captures mouse input events (clicks, scrolls, etc.), builds structured
        // event data, and broadcasts them via SignalR to connected clients.
        private IntPtr ReceiveMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // If the hook code is invalid, pass the event down the chain immediately.
            if (nCode < 0)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Explicitly ignore mouse move events (too frequent, not useful for logging).
            if (wParam == WM_MOUSEMOVE)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Extract mouse event details from the pointer.
            var mouse = Marshal.PtrToStructure<MouseHook>(lParam);

            // Handle all mouse events EXCEPT wheel events (vertical/horizontal scroll).
            if (wParam != WM_MOUSEWHEEL && wParam != WM_MOUSEHWHEEL)
            {
                // Build a structured event model with context (clicks, button up/down, etc.).
                var clickMessage = new RecordingEventModel
                {
                    Chain = repository.Peek(x: mouse.pt.X, y: mouse.pt.Y), // UI element at cursor position.
                    Event = GetMouseEventName(wParam), // Resolve readable event name.
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Type = "Mouse",
                    Value = new
                    {
                        mouse.pt.X,
                        mouse.pt.Y
                    }
                };

                // Broadcast the captured mouse click event to all connected SignalR clients.
                hub.Clients.All.SendAsync("ReceiveRecordingEvent", new { Value = clickMessage });

                // Always continue the hook chain.
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Handle vertical & horizontal wheel events.
            // HIGHWORD(mouseData) is a signed delta (in multiples of WHEEL_DELTA = 120).
            var delta = unchecked((short)((mouse.mouseData >> 16) & 0xFFFF));
            var notches = Math.Abs(delta) / WHEEL_DELTA;

            // High-resolution devices may report deltas smaller than WHEEL_DELTA.
            // Normalize to at least 1 notch for consistency.
            if (notches == 0)
            {
                notches = 1;
            }

            // Determine scroll direction based on event type and delta sign.
            var direction = string.Empty;
            if (wParam == WM_MOUSEWHEEL)
            {
                direction = delta > 0 ? "Up" : "Down";
            }
            else if (wParam == WM_MOUSEHWHEEL)
            {
                direction = delta > 0 ? "Right" : "Left";
            }

            // Build a structured wheel event with scroll details.
            var wheelMessage = new RecordingEventModel
            {
                Chain = repository.Peek(x: mouse.pt.X, y: mouse.pt.Y),
                Event = $"{GetMouseEventName(wParam)} {direction}",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "Mouse",
                Value = new
                {
                    Notches = notches, // Number of notches scrolled.
                    mouse.pt.X,
                    mouse.pt.Y
                }
            };

            // Broadcast the captured wheel event to all connected SignalR clients.
            hub.Clients.All.SendAsync(method: "ReceiveRecordingEvent", arg1: new
            {
                Value = wheelMessage
            });

            // Continue the hook chain.
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Resolves a human-readable string for a given keyboard event.
        // Attempts to produce the actual typed character (layout-aware), and if not
        // possible, falls back to a key name (localized or VK-based).
        private static string ResolveKeyName(in KeyboardHook keyboard)
        {
            // Try to resolve the typed character (respects Shift, CapsLock, current layout).
            var typed = ConvertToChar(keyboard);

            // If we got a visible character, return it directly.
            if (!string.IsNullOrEmpty(typed))
            {
                // Look at the first char to filter out control or whitespace.
                char c = typed[0];

                // If it's a visible, non-control glyph (e.g., 'a', 'A', '!'), return it directly.
                if (!char.IsControl(c) && c != ' ')
                {
                    return typed;
                }
            }

            // Fallback: resolve by key name (localized where possible).
            string name = GetKeyName(keyboard);

            // Normalize space key to lowercase "space" for consistency.
            return string.Equals(name, "Space", StringComparison.OrdinalIgnoreCase)
                ? "space"
                : name;
        }

        // Converts the current key press described by a low-level keyboard hook struct
        // into the *typed* character string, honoring Shift/CapsLock and the *current* keyboard layout.
        private static string ConvertToChar(KeyboardHook keyboard)
        {
            // Helper: ensure the "pressed" (high bit) state matches the actual key state
            // so that ToUnicodeEx can compute the correct character with modifiers applied.
            static void SetKeyStateBit(byte[] keyboardState, int virtualKey)
            {
                // Get the real-time key state for this virtual key.
                var state = GetKeyState(virtualKey);

                // High bit (0x80) indicates key is currently down
                if ((state & 0x8000) != 0)
                {
                    keyboardState[virtualKey] |= 0x80;
                }
                else
                {
                    keyboardState[virtualKey] &= 0x7F;
                }
            }

            // Helper: ensure the "toggle" (low bit) state (e.g., CapsLock/NumLock) matches reality.
            static void SetToggleBit(byte[] keyboardState, int virtualKey)
            {
                // Get the real-time key state for this virtual key.
                var state = GetKeyState(virtualKey);

                // Low bit (0x01) indicates toggled ON
                if ((state & 0x0001) != 0)
                {
                    // toggled on (e.g., CapsLock)
                    keyboardState[virtualKey] |= 0x01;
                }
                else
                {
                    // toggled off
                    keyboardState[virtualKey] &= 0xFE;
                }
            }

            // Capture full keyboard state (256 entries) as the base snapshot.
            // This pulls per-virtual-key flags that ToUnicodeEx uses for translation.
            var keyboardState = new byte[256];
            GetKeyboardState(keyboardState);

            // Explicitly refresh modifier keys in this thread context to avoid stale flags.
            // (The hook thread may not share the same implicit state as the foreground thread.)
            SetKeyStateBit(keyboardState, VK_SHIFT);
            SetKeyStateBit(keyboardState, VK_LSHIFT);
            SetKeyStateBit(keyboardState, VK_RSHIFT);
            SetKeyStateBit(keyboardState, VK_CONTROL);
            SetKeyStateBit(keyboardState, VK_LCONTROL);
            SetKeyStateBit(keyboardState, VK_RCONTROL);
            SetKeyStateBit(keyboardState, VK_MENU);
            SetKeyStateBit(keyboardState, VK_LMENU);
            SetKeyStateBit(keyboardState, VK_RMENU);

            // Ensure toggles (Caps/Num/Scroll) are correct for this translation.
            SetToggleBit(keyboardState, VK_CAPITAL);
            SetToggleBit(keyboardState, VK_NUMLOCK);
            SetToggleBit(keyboardState, VK_SCROLL);

            // Translate using the *current* active layout for thread 0 (foreground).
            var stringBuilder = new StringBuilder(8);
            var layout = GetKeyboardLayout(0);

            // ToUnicodeEx returns:
            //  > 0 : number of UTF-16 code units written
            //    0 : no translation (non-character key)
            //  < 0 : dead key; key press sets a diacritic state waiting for the next key
            int rc = ToUnicodeEx(
                keyboard.vkCode,
                keyboard.scanCode,
                keyboardState,
                stringBuilder,
                stringBuilder.Capacity,
                0, // flags: 0 => regular behavior (no menu-accelerator translation)
                layout);

            if (rc > 0)
            {
                // Extract exactly the number of UTF-16 code units produced.
                // Could be more than one code unit (e.g., surrogate pairs).
                var s = stringBuilder.ToString(0, rc);

                // Return as-is; caller may choose to collapse/normalize further if desired.
                return s;
            }

            // rc == 0 => non-text key (e.g., Shift), or rc < 0 => dead-key prime (no visible glyph yet)
            return string.Empty;
        }

        // Resolves a human-readable (localized, layout-aware) key name for a given
        // low-level keyboard hook event. Falls back to a VK-code string if the OS
        // cannot provide a name.
        static string GetKeyName(KeyboardHook keyboard)
        {
            // Converts a virtual-key code into a human-readable string label.
            // Handles common control keys, navigation keys, modifiers, function keys,
            // alphanumeric keys, and provides a fallback for unknown codes.
            static string ConvertToString(int virtualKey) => virtualKey switch
            {
                // Control keys
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x1B => "Esc",
                0x20 => "Space",

                // Modifier keys
                0x10 => "Shift",
                0x11 => "Ctrl",
                0x12 => "Alt",
                0x5B => "LWin",
                0x5C => "RWin",
                0x5D => "Apps",   // Context menu key
                0xA0 => "LShift",
                0xA1 => "RShift",
                0xA2 => "LCtrl",
                0xA3 => "RCtrl",
                0xA4 => "LAlt",
                0xA5 => "RAlt",

                // Lock keys
                0x14 => "CapsLock",
                0x90 => "NumLock",
                0x91 => "ScrollLock",

                // Navigation keys
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

                // Function keys (F1–F12)
                >= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",

                // Number keys '0'–'9'
                >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),

                // Uppercase letters 'A'–'Z'
                >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),

                // Fallback: show raw virtual-key code
                _ => $"VK_{virtualKey}"
            };

            // Compose LPARAM-style value for GetKeyNameText:
            // - Bits 16..23: scan code
            // - Bit 24: extended key (e.g., right Alt/Ctrl, arrow keys on keypad, etc.)
            int lParam = (int)(keyboard.scanCode << 16);

            // If the event is for an extended key, set bit 24 as required by the API.
            if ((keyboard.flags & LLKHF_EXTENDED) != 0)
            {
                lParam |= 1 << 24;
            }

            // Ask Windows for a localized/display name for this key.
            var sb = new StringBuilder(64);
            int length = GetKeyNameText(lParam, sb, sb.Capacity);

            // If the OS returned a name (length > 0), use it; otherwise,
            // fall back to a deterministic VK-based string representation.
            return length > 0
                ? sb.ToString(0, length)
                : ConvertToString((int)keyboard.vkCode);
        }

        // Gets a descriptive string for a mouse event based on its Windows message identifier.
        private static string GetMouseEventName(IntPtr wParam) => wParam switch
        {
            // Left button press/release
            WM_LBUTTONDOWN => "Left Down",
            WM_LBUTTONUP => "Left Up",

            // Right button press/release
            WM_RBUTTONDOWN => "Right Down",
            WM_RBUTTONUP => "Right Up",

            // Middle button press/release
            WM_MBUTTONDOWN => "Middle Down",
            WM_MBUTTONUP => "Middle Up",

            // Vertical or horizontal scroll wheel
            WM_MOUSEWHEEL or WM_MOUSEHWHEEL => "Wheel",

            // Unknown/unhandled message: return raw message code
            _ => $"msg=0x{wParam:X}"
        };

        // Attempts to install a low-level Windows hook (e.g., keyboard or mouse).
        // Tries with the current module handle first, then falls back to using <c>IntPtr.Zero</c>.
        private static IntPtr InitializeWindowsHook(int idHook, HookProcess process, out int lastError)
        {
            // First attempt: associate hook with the current module
            var hook = SetWindowsHookEx(idHook, process, GetModuleHandle(null), 0);

            if (hook != IntPtr.Zero)
            {
                // Success on first attempt → no error
                lastError = 0;
                return hook;
            }

            // Failure: capture error from first attempt
            lastError = Marshal.GetLastWin32Error();

            // Fallback: try again with IntPtr.Zero (common for .NET apps without native modules)
            hook = SetWindowsHookEx(idHook, process, IntPtr.Zero, 0);

            // If successful on second attempt, clear the error
            if (hook == IntPtr.Zero)
            {
                // Still failed → capture last error
                lastError = Marshal.GetLastWin32Error();
            }

            // Return the hook handle (or IntPtr.Zero if both attempts failed)
            return hook;
        }
        #endregion

        #region *** Structs   ***
        /// <summary>
        /// Contains information about a low-level keyboard input event.
        /// Used with WH_KEYBOARD_LL hooks.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHook
        {
            /// <summary>
            /// The virtual-key code of the key.
            /// </summary>
            public uint vkCode;

            /// <summary>
            /// The hardware scan code of the key.
            /// </summary>
            public uint scanCode;

            /// <summary>
            /// Event-injection flags and extended key information.
            /// </summary>
            public uint flags;

            /// <summary>
            /// The time stamp for this message, in milliseconds.
            /// </summary>
            public uint time;

            /// <summary>
            /// Additional information associated with the message.
            /// </summary>
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// Represents a message retrieved from a thread's message queue.
        /// Equivalent to the Win32 MSG structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            /// <summary>
            /// Handle to the window that received the message.
            /// </summary>
            public IntPtr hwnd;

            /// <summary>
            /// The message identifier (e.g., WM_KEYDOWN).
            /// </summary>
            public uint message;

            /// <summary>
            /// Additional message information (word parameter).
            /// </summary>
            public UIntPtr wParam;

            /// <summary>
            /// Additional message information (long parameter).
            /// </summary>
            public IntPtr lParam;

            /// <summary>
            /// The time at which the message was posted.
            /// </summary>
            public uint time;

            /// <summary>
            /// The cursor position, in screen coordinates, when the message was posted.
            /// </summary>
            public Point pt;

            /// <summary>
            /// Reserved/private value used internally by Windows.
            /// </summary>
            public uint lPrivate;
        }

        /// <summary>
        /// Contains information about a low-level mouse input event.
        /// Used with WH_MOUSE_LL hooks.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHook
        {
            /// <summary>
            /// The X and Y coordinates of the cursor, in screen coordinates.
            /// </summary>
            public Point pt;

            /// <summary>
            /// Additional mouse-specific data (wheel delta, X buttons, etc.).
            /// </summary>
            public uint mouseData;

            /// <summary>
            /// Event-injection flags and extra information about the mouse message.
            /// </summary>
            public uint flags;

            /// <summary>
            /// The time stamp for this message, in milliseconds.
            /// </summary>
            public uint time;

            /// <summary>
            /// Additional information associated with the message.
            /// </summary>
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// Defines the X and Y coordinates of a point.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            /// <summary>
            /// The X coordinate, in pixels.
            /// </summary>
            public int X;

            /// <summary>
            /// The Y coordinate, in pixels.
            /// </summary>
            public int Y;
        }
        #endregion
    }
}
