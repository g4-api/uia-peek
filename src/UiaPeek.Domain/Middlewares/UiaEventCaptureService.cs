using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UiaPeek.Domain.Hubs;
using UiaPeek.Domain.Models;

using UIAutomationClient;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Background service that installs and manages global low-level keyboard
    /// and mouse hooks using the Win32 API.  
    /// Captured input events are translated into structured models and
    /// broadcast in real time to connected SignalR clients via <see cref="UiaPeekHub"/>.
    /// </summary>
    public sealed class UiaEventCaptureService : BackgroundService
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

        // Retrieves the cursor position in physical desktop coordinates for stationary hover refresh.
        [DllImport("user32.dll")]
        private static extern bool GetPhysicalCursorPos(out Point lpPoint);

        // Retrieves a module handle for the specified module.
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

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
        private const int HoverBoundsTolerancePixels = 2;
        private const int HoverMaximumAgeMilliseconds = 1000;
        private const int HoverMinimumResolutionIntervalMilliseconds = 50;
        private const int HoverRefreshIntervalMilliseconds = 250;

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
        // Shared hub state prevents hover resolution when no recorder client can consume events.
        private readonly RecorderConnectionState _connectionState = RecorderConnectionState.Instance;

        // Thread-safe queue for captured input events awaiting processing.
        private readonly ConcurrentQueue<EventRecord> _eventsQueue = new();

        // SignalR hub context for broadcasting captured input events to connected clients.
        private readonly IHubContext<UiaPeekHub> _hub;

        // Indicates that a newer pointer observation is waiting for background UIA resolution.
        private int _isHoverPending;

        // Indicates that recent input may have changed focus, so a fresh focus snapshot is due.
        private int _isFocusPending;

        // Monotonic generation that prevents an older UIA lookup from overwriting a newer focus observation.
        private long _focusObservationGeneration;

        // Receives UI Automation focus notifications and invalidates obsolete pre-key targets.
        private FocusChangedEventHandler _focusChangedEventHandler;

        // Owns the UI Automation subscription for the service lifetime.
        private IUIAutomation _focusObserverAutomation;

        // Delegate reference for the low-level keyboard hook callback.  
        // Must be kept alive to prevent garbage collection while the hook is active.
        private HookProcess _keyboardCallback;

        // Resolver that preserves target identity and key text from first Down through matching Up.
        private readonly KeyboardTargetResolver _keyboardTargetResolver;

        // Atomically publishes session-owned focus chains to the low-level keyboard hook.
        private readonly KeyboardFocusSnapshotStore _keyboardFocusSnapshotStore = new();

        // Logger for diagnostics, error reporting, and lifecycle information.
        private readonly ILogger<UiaEventCaptureService> _logger;

        // Monotonic timestamp of the most recent hover-resolution attempt.
        private long _lastHoverResolutionTimestamp;

        // Session generation that owns the currently retained keyboard press state.
        private long _lastKeyboardSessionGeneration;

        // Session generation for which the focus cache was last initialized.
        private long _lastFocusSessionGeneration;

        // Session generation for which the hover cache was last initialized.
        private long _lastHoverSessionGeneration;

        // Resolver that preserves the press-time UIA target for each mouse button.
        private readonly MouseTargetResolver _mouseTargetResolver;

        // Delegate reference for the low-level mouse hook callback.  
        // Must be kept alive to prevent garbage collection while the hook is active.
        private HookProcess _mouseCallback;

        // Atomically published pre-click target cache consumed by mouse-down callbacks.
        private readonly MouseTargetSnapshotStore _mouseTargetSnapshotStore = new(
            maximumAgeMilliseconds: HoverMaximumAgeMilliseconds,
            boundsTolerancePixels: HoverBoundsTolerancePixels);

        // Repository used to resolve the current UI element chain at the time of an event.
        private readonly IUiaPeekRepository _repository;

        // Auto-reset event used to signal the background processing task when new events are available.
        private readonly AutoResetEvent _signal = new(initialState: false);
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="UiaEventCaptureService"/> class
        /// and starts the background listener responsible for processing captured UI events.
        /// </summary>
        /// <param name="hub">The SignalR hub context used to broadcast resolved UI events to connected clients.</param>
        /// <param name="logger">The logger used to record diagnostic, debug, and error information.</param>
        /// <param name="repository">The data repository responsible for persisting or enriching captured UI event data.</param>
        public UiaEventCaptureService(
            IHubContext<UiaPeekHub> hub,
            ILogger<UiaEventCaptureService> logger,
            IUiaPeekRepository repository)
        {
            // Validate and assign constructor dependencies.
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _keyboardTargetResolver = new KeyboardTargetResolver(repository);
            _mouseTargetResolver = new MouseTargetResolver(repository);

            // Create a new CancellationTokenSource to control background worker lifetime.
            // This token will be used to cooperatively stop the event listener when the service shuts down.
            var tokenSource = new CancellationTokenSource();

            // Start the background worker that listens for captured events
            // and processes them asynchronously as they are enqueued.
            StartEventsListener(tokenSource);
        }

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
                    _logger.LogError("Failed to install global keyboard hook. " +
                        "Win32 error code: {ErrorCode}", keyboardError);
                    return;
                }

                // Check if the mouse hook failed to install.
                if (mouseHook == IntPtr.Zero)
                {
                    _logger.LogError("Failed to install global mouse hook. " +
                        "Win32 error code: {ErrorCode}", mouseError);
                    return;
                }

                // If neither hook installed, warn the user.
                if (keyboardHook == IntPtr.Zero && mouseHook == IntPtr.Zero)
                {
                    _logger.LogWarning("No global hooks could be installed. " +
                        "Ensure this process is running in an interactive session with " +
                        "sufficient privileges (e.g., Administrator).");
                    return;
                }

                // Subscribe to real focus transitions so elapsed time never invalidates a stable keyboard target.
                StartFocusMonitoring();

                _logger.LogInformation("Input monitoring service is running.");

                // Ensure that when the service is stopped, a WM_QUIT message
                // is posted to break out of the message loop gracefully.
                using var reg = stoppingToken.Register(() =>
                {
                    _logger.LogInformation("Shutdown requested. Posting quit message to stop input monitoring.");
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

                // Remove the focus subscription before releasing hook-owned state so callbacks cannot republish it.
                StopFocusMonitoring();

                // Clean up the keyboard hook if it was installed.
                if (keyboardHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(keyboardHook);
                    _logger.LogInformation("Keyboard hook successfully uninstalled.");
                }

                // Clean up the mouse hook if it was installed.
                if (mouseHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(mouseHook);
                    _logger.LogInformation("Mouse hook successfully uninstalled.");
                }

                // Release short-lived input state after the hook lifecycle ends.
                _keyboardFocusSnapshotStore.Clear();
                _keyboardTargetResolver.Clear();
                _mouseTargetSnapshotStore.Clear();
                _mouseTargetResolver.Clear();
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
            // Extract keyboard event details from the pointer.
            var key = Marshal.PtrToStructure<KeyboardHook>(lParam);

            // Build a unified event record for the captured keyboard event.
            var eventRecord = EventRecord.ConvertFromKey(
                key,
                nCode,
                wParam,
                timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Stamp the capture-time session state so the background worker can honor it even if the
            // recorder disconnects (Stop) before this event is drained. This keeps the final key press
            // from being dropped by the live capture-active gate at processing time.
            eventRecord.CaptureActiveAtCapture = _connectionState.CaptureActive;
            eventRecord.SessionGenerationAtCapture = _connectionState.SessionGeneration;

            // Attach the pre-press focus snapshot to key-down events. The hook runs before the key's
            // message is dispatched, so the snapshot still holds the element that had focus at the
            // physical press (for example Cancel), even when this key then closes the dialog. Only Down
            // needs it; Up pairs to the retained Down target.
            var isKeyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;

            if (isKeyDown)
            {
                eventRecord.CapturedKeyboardTarget = _keyboardFocusSnapshotStore.Resolve(
                    eventRecord.SessionGenerationAtCapture);
            }

            // Enqueue the event for processing in the background task.
            _eventsQueue.Enqueue(eventRecord);

            // Mark focus as possibly changed (navigation keys move focus) so the worker refreshes the
            // snapshot for the next press.
            SetFocusObservation();

            // Signal the background task that a new event is available.
            _signal.Set();

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

            // Publish only the latest move observation so hover tracking never enters the recording stream.
            if (wParam == WM_MOUSEMOVE)
            {
                SetMouseObservation();
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Extract native data only for recordable messages after the high-frequency move fast path.
            var mouse = Marshal.PtrToStructure<MouseHook>(lParam);
            var monotonicTimestamp = Stopwatch.GetTimestamp();

            // Build a unified event record for the captured mouse event.
            var eventRecord = EventRecord.ConvertFromMouse(
                mouse,
                nCode,
                wParam,
                timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Attach a pre-dispatch chain to button-down events using only immutable in-memory state.
            if (TestMouseButtonDown(wParam))
            {
                var snapshotResolution = _mouseTargetSnapshotStore.Resolve(new MouseTargetSnapshotRequest
                {
                    CaptureActive = _connectionState.CaptureActive,
                    CurrentTimestamp = monotonicTimestamp,
                    SessionGeneration = _connectionState.SessionGeneration,
                    X = mouse.pt.X,
                    Y = mouse.pt.Y
                });

                eventRecord.CapturedMouseTarget = snapshotResolution.Accepted
                    ? snapshotResolution.Snapshot.Chain
                    : null;
                eventRecord.MouseSnapshotAgeMilliseconds = snapshotResolution.AgeMilliseconds;
                eventRecord.MouseSnapshotStatus = snapshotResolution.Status;
            }

            // Enqueue the event for processing in the background task.
            _eventsQueue.Enqueue(eventRecord);

            // A click moves focus, so refresh the focus snapshot for a subsequent key press.
            SetFocusObservation();

            // Signal the background task that a new event is available.
            _signal.Set();

            // Continue the hook chain.
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Publishes one coalesced pointer observation for background UIA resolution.
        // The method performs no UIA work and returns immediately when no recorder is connected.
        private void SetMouseObservation()
        {
            // Avoid allocation and worker wakeups when no active session can consume a snapshot.
            if (!_connectionState.CaptureActive)
            {
                return;
            }

            // Collapse every movement burst into a single worker-owned current-position lookup.
            Interlocked.Exchange(ref _isHoverPending, 1);

            // Wake the worker so it can resolve the newest point without broadcasting a mouse-move event.
            _signal.Set();
        }

        // Registers one process-wide UI Automation focus observer for the service lifetime. Registration
        // failure leaves input hooks operational and relies on their existing post-input refresh signals.
        private void StartFocusMonitoring()
        {
            // Materialize the COM client and managed callback before registration so both remain strongly referenced.
            var automation = new CUIAutomation8();
            var handler = new FocusChangedEventHandler(SetFocusChangedObservation);

            try
            {
                // Subscribe without a cache request because the event worker resolves the complete chain separately.
                automation.AddFocusChangedEventHandler(cacheRequest: null, handler);

                // Publish ownership only after successful registration so shutdown removes a real subscription.
                _focusObserverAutomation = automation;
                _focusChangedEventHandler = handler;
            }
            catch (Exception exception)
            {
                // Release the failed COM registration client while preserving keyboard and mouse recording.
                if (Marshal.IsComObject(automation))
                {
                    Marshal.FinalReleaseComObject(automation);
                }

                _logger.LogWarning(exception, "UI Automation focus monitoring failed to start.");
            }
        }

        // Removes the UI Automation focus observer before hook cleanup. The method clears owned references first
        // so a reentrant or late callback cannot keep the subscription lifecycle reachable through this service.
        private void StopFocusMonitoring()
        {
            // Detach service ownership before invoking COM cleanup so repeated shutdown paths remain idempotent.
            var automation = _focusObserverAutomation;
            var handler = _focusChangedEventHandler;
            _focusObserverAutomation = null;
            _focusChangedEventHandler = null;

            if (automation == null)
            {
                return;
            }

            try
            {
                // Remove the exact handler instance registered at startup so focus callbacks stop before state cleanup.
                if (handler != null)
                {
                    automation.RemoveFocusChangedEventHandler(handler);
                }
            }
            catch (Exception exception)
            {
                // Keep shutdown progressing when the UI Automation provider has already disconnected.
                _logger.LogDebug(exception, "UI Automation focus monitoring failed to stop cleanly.");
            }
            finally
            {
                // Release the COM client after removal so the service owns no focus-monitoring resources.
                if (Marshal.IsComObject(automation))
                {
                    Marshal.FinalReleaseComObject(automation);
                }
            }
        }

        // Flags that recent input may have moved focus so the worker refreshes the focus snapshot.
        // Returns immediately when no recorder is connected to avoid needless worker wakeups.
        private void SetFocusObservation()
        {
            // Skip when no active session can consume a snapshot.
            if (!_connectionState.CaptureActive)
            {
                return;
            }

            // Request one focus refresh on the next worker iteration.
            Interlocked.Increment(ref _focusObservationGeneration);
            Interlocked.Exchange(ref _isFocusPending, 1);

            // Wake the worker so focus changes without an input event are published before the next key.
            _signal.Set();
        }

        // Invalidates the last publication after UI Automation reports a real focus transition, then
        // schedules resolution of the new target outside the COM callback and low-level hook threads.
        private void SetFocusChangedObservation()
        {
            // Ignore global focus activity while no recorder session can consume the resulting chain.
            if (!_connectionState.CaptureActive)
            {
                return;
            }

            // Remove the old target before requesting replacement so rapid keys never reuse known-obsolete focus.
            _keyboardFocusSnapshotStore.Clear();

            // Schedule the current focus lookup on the event worker so the COM notification returns promptly.
            SetFocusObservation();
        }

        // Refreshes the pre-press focus snapshot on the worker when input activity flagged a possible
        // focus change. Publishing is atomic, so key-down callbacks never read a partial snapshot.
        private void ResolveFocusTarget()
        {
            // Clear session-owned focus state once no recorder can consume it.
            if (!_connectionState.CaptureActive)
            {
                _keyboardFocusSnapshotStore.Clear();
                _lastFocusSessionGeneration = 0;
                Interlocked.Exchange(ref _isFocusPending, 0);
                return;
            }

            // Initialize each recorder generation with an immediate focus lookup so its first key has a target.
            var sessionGeneration = _connectionState.SessionGeneration;
            var isNewSession = _lastFocusSessionGeneration != sessionGeneration;

            if (isNewSession)
            {
                _keyboardFocusSnapshotStore.Clear();
                _lastFocusSessionGeneration = sessionGeneration;
                Interlocked.Increment(ref _focusObservationGeneration);
                Interlocked.Exchange(ref _isFocusPending, 1);
            }

            // Refresh only after startup, input, or an actual UI Automation focus notification.
            var hasPending = Interlocked.Exchange(ref _isFocusPending, 0) == 1;

            if (!hasPending)
            {
                return;
            }

            // Stamp the observation generation before UIA work so newer focus signals can invalidate this result.
            var focusObservationGeneration = Volatile.Read(ref _focusObservationGeneration);
            UiaChainModel chain;

            try
            {
                // Resolve the currently focused element off the hook thread before publishing it.
                chain = _repository.Peek();
            }
            catch (Exception exception)
            {
                // Isolate transient provider failures so later focus observations remain serviceable.
                _keyboardFocusSnapshotStore.Clear();
                _logger.LogDebug(exception, "Skipped focus target resolution.");
                return;
            }

            // Discard work completed after the recorder session ended or changed generations.
            var isSessionCurrent = _connectionState.CaptureActive &&
                _connectionState.SessionGeneration == sessionGeneration;
            var isObservationCurrent =
                Volatile.Read(ref _focusObservationGeneration) == focusObservationGeneration;

            if (!isSessionCurrent || !isObservationCurrent)
            {
                return;
            }

            // Publish only a usable chain (with a path) so key-down stamping never yields an empty target.
            if (!(chain?.Path?.Count > 0))
            {
                _keyboardFocusSnapshotStore.Clear();
                return;
            }

            // Publish without a wall-clock lifetime because validity ends on focus or session transitions.
            _keyboardFocusSnapshotStore.Set(chain, sessionGeneration);
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

        // Processes a captured keyboard event record, builds a structured event model,
        // and broadcasts it to connected SignalR clients.
        private void ResolveKeyboardEvent(EventRecord eventRecord)
        {
            // Validate the hook code and event type.
            var isValidCode = eventRecord.NCode >= 0;
            var isKeyDown = eventRecord.WParam == WM_KEYDOWN || eventRecord.WParam == WM_SYSKEYDOWN;
            var isKeyUp = eventRecord.WParam == WM_KEYUP || eventRecord.WParam == WM_SYSKEYUP;

            // Determine if this is a keyboard message worth processing.
            var isKeyMsg = isValidCode && (isKeyDown || isKeyUp);

            // If it's not a keyboard event, let Windows continue processing as usual.
            if (!isKeyMsg)
            {
                return;
            }

            // Initialize session ownership before resolving Down so a new generation cannot clear its
            // fresh state. Uses the capture-time session state stamped on the record, not the live
            // state, so an event captured during an active session is still broadcast when the session
            // ends before the worker drains it.
            if (!InitializeKeyboardTargetState(eventRecord.CaptureActiveAtCapture, eventRecord.SessionGenerationAtCapture))
            {
                return;
            }

            // Derive one physical identity so target and text state use the same Down-to-Up boundary.
            var keyboard = eventRecord.EventData.Key;
            var identity = new KeyboardKeyIdentity(
                virtualKey: keyboard.vkCode,
                scanCode: keyboard.scanCode,
                extended: (keyboard.flags & LLKHF_EXTENDED) != 0);
            KeyboardTargetResolution targetResolution;

            // Resolve the first press or consume its retained state according to the native transition.
            if (isKeyDown)
            {
                // Resolve display text once so repeat and release events remain consistent with the first press.
                var keyText = ResolveKeyName(keyboard);
                targetResolution = _keyboardTargetResolver.ResolveDown(identity, keyText, eventRecord.CapturedKeyboardTarget);
            }
            else
            {
                // Reuse the first-press state and reserve focused lookup for an unmatched release.
                targetResolution = _keyboardTargetResolver.ResolveUp(identity);
            }

            // Report missing pre-dispatch focus explicitly instead of silently assigning a post-key destination.
            if (targetResolution.Source == KeyboardTargetSource.MissingSnapshot)
            {
                _logger.LogWarning(
                    "Recorded keyboard Down without a pre-dispatch focus snapshot; " +
                    "VirtualKey: {VirtualKey}, ScanCode: {ScanCode}, Extended: {Extended}.",
                    identity.VirtualKey,
                    identity.ScanCode,
                    identity.Extended);
            }

            // Preserve key-name fallback for sessions that begin after the physical press.
            var isOrphanedRelease = targetResolution.Source == KeyboardTargetSource.OrphanFallback;
            var resolvedKeyText = isOrphanedRelease
                ? ResolveKeyName(keyboard)
                : targetResolution.KeyText;

            // Report the internal resolution path without extending the recorder event contract.
            _logger.LogDebug(
                "Resolved keyboard {Transition} from {Source}; VirtualKey: {VirtualKey}, ScanCode: {ScanCode}, " +
                "Extended: {Extended}, Locator: {Locator}, PathCount: {PathCount}.",
                isKeyDown ? "Down" : "Up",
                targetResolution.Source,
                identity.VirtualKey,
                identity.ScanCode,
                identity.Extended,
                targetResolution.Chain?.Locator,
                targetResolution.Chain?.Path?.Count ?? 0);

            // Build a structured event model with context.
            var message = new UiaEventModel
            {
                Chain = targetResolution.Chain,
                Event = isKeyDown ? "Key Down" : "Key Up",
                Timestamp = eventRecord.Timestamp,
                Type = "Keyboard",
                Value = new
                {
                    ScanCode = keyboard.scanCode,
                    VirtualKey = keyboard.vkCode,
                    Key = resolvedKeyText
                }
            };

            // Broadcast the captured event to all connected SignalR clients.
            _hub.Clients.All.SendAsync("ReceiveRecordingEvent", new
            {
                Value = message
            });
        }

        // Processes a captured mouse event record, builds a structured event model,
        // and broadcasts it to connected SignalR clients.
        private void ResolveMouseEvent(EventRecord eventRecord)
        {
            var mouse = eventRecord.EventData.Mouse;

            // Handle all mouse events EXCEPT wheel events (vertical/horizontal scroll).
            if (eventRecord.WParam != WM_MOUSEWHEEL && eventRecord.WParam != WM_MOUSEHWHEEL)
            {
                // Resolve the target once so the chain and its derived pointer offset describe the same UIA element.
                var chain = ResolveMouseTarget(eventRecord);
                var offset = MouseTargetResolver.ResolveOffset(chain, mouse.pt.X, mouse.pt.Y);

                // Build a structured event model with absolute and element-relative pointer coordinates.
                var clickMessage = new UiaEventModel
                {
                    Chain = chain,
                    Event = GetMouseEventName(eventRecord.WParam),
                    Offset = offset,
                    Timestamp = eventRecord.Timestamp,
                    Type = "Mouse",
                    Value = new
                    {
                        mouse.pt.X,
                        mouse.pt.Y
                    }
                };

                // Broadcast the captured mouse click event to all connected SignalR clients.
                _hub.Clients.All.SendAsync("ReceiveRecordingEvent", new { Value = clickMessage });

                // Exit after handling non-wheel events.
                return;
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
            if (eventRecord.WParam == WM_MOUSEWHEEL)
            {
                direction = delta > 0 ? "Up" : "Down";
            }
            else if (eventRecord.WParam == WM_MOUSEHWHEEL)
            {
                direction = delta > 0 ? "Right" : "Left";
            }

            // Build a structured wheel event with scroll details.
            var wheelMessage = new UiaEventModel
            {
                Chain = _repository.Peek(x: mouse.pt.X, y: mouse.pt.Y),
                Event = $"{GetMouseEventName(eventRecord.WParam)} {direction}",
                Timestamp = eventRecord.Timestamp,
                Type = "Mouse",
                Value = new
                {
                    Notches = notches, // Number of notches scrolled.
                    mouse.pt.X,
                    mouse.pt.Y
                }
            };

            // Broadcast the captured wheel event to all connected SignalR clients.
            _hub.Clients.All.SendAsync(method: "ReceiveRecordingEvent", arg1: new
            {
                Value = wheelMessage
            });
        }

        // Resolves button releases from their press-time targets so transient UI
        // elements remain stable after the application reacts to the release.
        private UiaChainModel ResolveMouseTarget(EventRecord eventRecord)
        {
            var mouse = eventRecord.EventData.Mouse;

            switch (eventRecord.WParam)
            {
                case WM_LBUTTONDOWN:
                    return ResolveMouseDown(MouseButton.Left, eventRecord);

                case WM_MBUTTONDOWN:
                    return ResolveMouseDown(MouseButton.Middle, eventRecord);

                case WM_RBUTTONDOWN:
                    return ResolveMouseDown(MouseButton.Right, eventRecord);

                case WM_LBUTTONUP:
                    return ResolveMouseUp(MouseButton.Left, mouse.pt.X, mouse.pt.Y);

                case WM_MBUTTONUP:
                    return ResolveMouseUp(MouseButton.Middle, mouse.pt.X, mouse.pt.Y);

                case WM_RBUTTONUP:
                    return ResolveMouseUp(MouseButton.Right, mouse.pt.X, mouse.pt.Y);

                default:
                    // Preserve coordinate-based resolution for non-button mouse events.
                    return _repository.Peek(mouse.pt.X, mouse.pt.Y);
            }
        }

        // Selects the hook-attached hover chain before falling back to worker-time coordinate resolution.
        private UiaChainModel ResolveMouseDown(MouseButton button, EventRecord eventRecord)
        {
            var mouse = eventRecord.EventData.Mouse;
            var hasPreClickTarget = eventRecord.CapturedMouseTarget != null;
            var queueDelayMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - eventRecord.Timestamp;

            // Retain the selected target so the matching release cannot observe a post-click UI tree.
            var chain = _mouseTargetResolver.ResolveDown(new MouseDownTargetRequest
            {
                Button = button,
                CapturedChain = eventRecord.CapturedMouseTarget,
                X = mouse.pt.X,
                Y = mouse.pt.Y
            });

            // Report the target source and timing without changing the external recording-event contract.
            if (hasPreClickTarget)
            {
                _logger.LogDebug(
                    "Resolved {Button} mouse press from HoverSnapshot at ({X}, {Y}); " +
                    "SnapshotAgeMs: {SnapshotAgeMs:F1}, QueueDelayMs: {QueueDelayMs}, Locator: {Locator}.",
                    button,
                    mouse.pt.X,
                    mouse.pt.Y,
                    eventRecord.MouseSnapshotAgeMilliseconds,
                    queueDelayMilliseconds,
                    chain?.Locator);
            }
            else
            {
                _logger.LogDebug(
                    "Resolved {Button} mouse press from DownWorkerFallback at ({X}, {Y}); " +
                    "SnapshotStatus: {SnapshotStatus}, QueueDelayMs: {QueueDelayMs}, Locator: {Locator}.",
                    button,
                    mouse.pt.X,
                    mouse.pt.Y,
                    eventRecord.MouseSnapshotStatus,
                    queueDelayMilliseconds,
                    chain?.Locator);
            }

            return chain;
        }

        // Resolves a button release and reports when its matching press was absent.
        private UiaChainModel ResolveMouseUp(MouseButton button, int x, int y)
        {
            // Prefer the target captured before the application reacts to the release.
            var resolution = _mouseTargetResolver.ResolveUp(button, x, y);

            // Surface incomplete pairs without dropping the release event.
            if (resolution.UsedFallback)
            {
                _logger.LogDebug(
                    "Resolved orphaned {Button} mouse release from coordinates ({X}, {Y}).",
                    button,
                    x,
                    y);
            }
            else
            {
                _logger.LogDebug(
                    "Resolved {Button} mouse release from PairedRelease at ({X}, {Y}); Locator: {Locator}.",
                    button,
                    x,
                    y,
                    resolution.Chain?.Locator);
            }

            return resolution.Chain;
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

        // Tests whether a native mouse message begins a button transition that needs a pre-click target.
        private static bool TestMouseButtonDown(IntPtr message)
        {
            return message == WM_LBUTTONDOWN || message == WM_MBUTTONDOWN || message == WM_RBUTTONDOWN;
        }

        // Initializes keyboard state for the active recorder generation and rejects idle input, using
        // the session state captured when the event was recorded rather than the live state. The worker
        // owns generation transitions so retained presses never cross connection lifecycles.
        private bool InitializeKeyboardTargetState(bool captureActiveAtCapture, long sessionGenerationAtCapture)
        {
            // Skip events captured while no recorder was connected; there is nothing to broadcast to and
            // no session state to retain. Using the capture-time flag (not the live one) keeps an event
            // captured during an active session broadcastable even after the session ends.
            if (!captureActiveAtCapture)
            {
                _keyboardTargetResolver.Clear();
                _lastKeyboardSessionGeneration = 0;
                return false;
            }

            // Reset state before the first keyboard event of each newly connected recorder generation.
            // Using the capture-time generation keeps a Down and its paired Up (same generation) from
            // being split by a live-state change between them.
            if (_lastKeyboardSessionGeneration != sessionGenerationAtCapture)
            {
                _keyboardTargetResolver.Clear();
                _lastKeyboardSessionGeneration = sessionGenerationAtCapture;
            }

            return true;
        }

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

        // Resolves one coalesced hover target on the existing event worker.
        // Real input events retain priority, and snapshots are published only for a stable physical cursor point.
        private void ResolveHoverTarget()
        {
            // Clear session-owned state once the final recorder client disconnects.
            if (!_connectionState.CaptureActive)
            {
                // Clear keyboard presses independently because they can precede hover-session initialization.
                if (_lastKeyboardSessionGeneration != 0)
                {
                    _keyboardTargetResolver.Clear();
                    _lastKeyboardSessionGeneration = 0;
                }

                // Clear pointer-owned state after the active hover generation ends.
                if (_lastHoverSessionGeneration != 0)
                {
                    _mouseTargetSnapshotStore.Clear();
                    _mouseTargetResolver.Clear();
                    _lastHoverResolutionTimestamp = 0;
                    _lastHoverSessionGeneration = 0;
                    Interlocked.Exchange(ref _isHoverPending, 0);
                }

                return;
            }

            // Initialize a clean cache when the first client starts a new recording generation.
            var sessionGeneration = _connectionState.SessionGeneration;
            if (_lastHoverSessionGeneration != sessionGeneration)
            {
                _mouseTargetSnapshotStore.Clear();
                _mouseTargetResolver.Clear();
                _lastHoverResolutionTimestamp = 0;
                _lastHoverSessionGeneration = sessionGeneration;
                Interlocked.Exchange(ref _isHoverPending, 1);
            }

            // Combine movement-triggered sampling with a periodic refresh for a stationary pointer.
            var currentTimestamp = Stopwatch.GetTimestamp();
            var hasPendingMovement = Interlocked.Exchange(ref _isHoverPending, 0) == 1;
            var elapsedMilliseconds = GetElapsedMilliseconds(_lastHoverResolutionTimestamp, currentTimestamp);
            var isRefreshDue = _lastHoverResolutionTimestamp == 0 ||
                elapsedMilliseconds >= HoverRefreshIntervalMilliseconds;

            if (!hasPendingMovement && !isRefreshDue)
            {
                return;
            }

            // Throttle movement bursts while retaining a pending flag for the next worker iteration.
            var isThrottled = _lastHoverResolutionTimestamp != 0 &&
                elapsedMilliseconds < HoverMinimumResolutionIntervalMilliseconds;

            if (isThrottled)
            {
                Interlocked.Exchange(ref _isHoverPending, 1);
                return;
            }

            // Sample physical coordinates immediately before UIA lookup so scaling matches the repository contract.
            if (!GetPhysicalCursorPos(out var pointBeforeResolution))
            {
                _lastHoverResolutionTimestamp = currentTimestamp;
                _logger.LogDebug("Skipped hover target resolution because the physical cursor position was unavailable.");
                return;
            }

            UiaChainModel chain;

            try
            {
                // Materialize the complete chain off the hook thread before exposing it as a pre-click snapshot.
                chain = _repository.Peek(pointBeforeResolution.X, pointBeforeResolution.Y);
            }
            catch (Exception exception)
            {
                // Isolate transient provider failures so later pointer observations remain serviceable.
                _lastHoverResolutionTimestamp = Stopwatch.GetTimestamp();
                _logger.LogDebug(
                    exception,
                    "Skipped hover target resolution at ({X}, {Y}).",
                    pointBeforeResolution.X,
                    pointBeforeResolution.Y);
                return;
            }

            // Record completion time for freshness calculations and sampling throttling.
            var completedTimestamp = Stopwatch.GetTimestamp();
            _lastHoverResolutionTimestamp = completedTimestamp;

            // Discard work completed after the recorder session ended or changed generations.
            var isSessionCurrent = _connectionState.CaptureActive &&
                _connectionState.SessionGeneration == sessionGeneration;

            if (!isSessionCurrent)
            {
                return;
            }

            // Reject a sample when the pointer moved during UIA traversal and request a fresh observation.
            var hasCurrentPoint = GetPhysicalCursorPos(out var pointAfterResolution);
            var hasPointerMoved = !hasCurrentPoint ||
                pointAfterResolution.X != pointBeforeResolution.X ||
                pointAfterResolution.Y != pointBeforeResolution.Y;

            if (hasPointerMoved)
            {
                Interlocked.Exchange(ref _isHoverPending, 1);
                return;
            }

            // Extract serialized trigger geometry now so hook-time selection performs no path traversal or COM work.
            var trigger = chain?.Path?.FindLast(node => node != null && node.IsTriggerElement);
            var bounds = trigger?.Bounds;
            var hasValidBounds = bounds != null &&
                double.IsFinite(bounds.Left) &&
                double.IsFinite(bounds.Top) &&
                double.IsFinite(bounds.Width) &&
                double.IsFinite(bounds.Height) &&
                bounds.Width > 0 &&
                bounds.Height > 0;
            var hasLocator = chain != null &&
                (!string.IsNullOrWhiteSpace(chain.Locator) || !string.IsNullOrWhiteSpace(chain.FallbackLocator));

            if (!hasValidBounds || !hasLocator)
            {
                _logger.LogTrace(
                    "Ignored incomplete hover target at ({X}, {Y}); locator or trigger bounds were unavailable.",
                    pointBeforeResolution.X,
                    pointBeforeResolution.Y);
                return;
            }

            // Publish the completed chain atomically for pre-dispatch selection by the next mouse-down callback.
            _mouseTargetSnapshotStore.Set(new MouseTargetSnapshot
            {
                CapturedAtTimestamp = completedTimestamp,
                Chain = chain,
                SessionGeneration = sessionGeneration,
                TargetHeight = bounds.Height,
                TargetLeft = bounds.Left,
                TargetTop = bounds.Top,
                TargetWidth = bounds.Width,
                X = pointBeforeResolution.X,
                Y = pointBeforeResolution.Y
            });
        }

        // Converts monotonic timestamp units to elapsed milliseconds without using wall-clock time.
        private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp == 0)
            {
                return double.PositiveInfinity;
            }

            return (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        // Starts the background worker that waits for a signal and drains the events queue,
        // dispatching each record to the appropriate resolver. The worker runs until the
        // provided tokenSource is canceled.
        private void StartEventsListener(CancellationTokenSource tokenSource)
        {
            // Local worker loop: blocks on _signal, drains _eventsQueue, routes by EventType.
            void StartLoop(CancellationTokenSource cts)
            {
                // Optional: when cancellation is requested, wake the waiter so the loop can exit promptly.
                using var _ = cts.Token.Register(() => _signal.Set());

                while (!cts.Token.IsCancellationRequested)
                {
                    // Wait up to 50ms to re-check cancellation periodically.
                    _signal.WaitOne(50);

                    // Drain the queue; keep exceptions isolated per event.
                    while (_eventsQueue.TryDequeue(out var eventRecord))
                    {
                        try
                        {
                            if (eventRecord.EventType == EventType.Mouse)
                            {
                                ResolveMouseEvent(eventRecord);
                            }
                            else if (eventRecord.EventType == EventType.Keyboard)
                            {
                                ResolveKeyboardEvent(eventRecord);
                            }
                            else
                            {
                                // Unknown/unsupported event type—log at Debug/Information based on policy.
                                _logger.LogDebug("Ignored event with unsupported type: {Type}", eventRecord.EventType);
                            }
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            // E_ACCESSDENIED (0x80070005): the target window runs at a higher
                            // integrity level. Skip it; run elevated / uiAccess to resolve these.
                            _logger.LogWarning(e,
                                "Skipped event; access denied to a higher-privilege UI element. Type: {Type}, Timestamp: {Ts}",
                                eventRecord.EventType, eventRecord.Timestamp);
                        }
                        catch (COMException e) when ((uint)e.HResult == 0x80040201)
                        {
                            // Transient UIA failure: the target provider couldn't service the
                            // request (window closing/redrawing). Expected during live tracking; skip it.
                            _logger.LogDebug(e,
                                "Skipped event; UI Automation could not resolve the element (transient). Type: {Type}, Timestamp: {Ts}",
                                eventRecord.EventType, eventRecord.Timestamp);
                        }
                        catch (Exception e)
                        {
                            // Per-event fault isolation so one bad item doesn't kill the loop.
                            _logger.LogError(e,
                                "Failed to resolve event. Type: {Type}, Timestamp: {Ts}",
                                eventRecord.EventType, eventRecord.Timestamp);
                        }
                    }

                    // Resolve at most one coalesced hover observation after all real input events are serviced.
                    ResolveHoverTarget();

                    // Refresh the pre-press focus snapshot so the next key-down is attributed to the
                    // element that had focus before the key's own effect changes it.
                    ResolveFocusTarget();
                }
            }

            // Start the worker on a dedicated thread-pool thread and pass the token for cooperative cancellation.
            Task.Factory.StartNew(() =>
                {
                    try
                    {
                        StartLoop(tokenSource);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on cancellation.
                    }
                    catch (Exception ex)
                    {
                        // Unexpected top-level failure—log and let the service decide how to recover.
                        _logger.LogCritical(ex, "Event processing worker crashed.");
                    }
                    finally
                    {
                        _logger.LogInformation("Event processing worker exited.");
                    }
                },
                tokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            // Once the worker finishes for any reason, dispose the CTS.
            .ContinueWith(_ => tokenSource.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        #endregion

        #region *** Nested Types ***
        private enum EventType : byte { Mouse = 1, Keyboard = 2 }

        // Bridges UI Automation focus notifications into the worker-owned focus-resolution pipeline.
        // The callback performs no UIA traversal and therefore returns without blocking provider event delivery.
        private sealed class FocusChangedEventHandler : IUIAutomationFocusChangedEventHandler
        {
            private readonly Action _onFocusChanged;

            internal FocusChangedEventHandler(Action onFocusChanged)
            {
                // Require the service callback before exposing the handler to COM event delivery.
                ArgumentNullException.ThrowIfNull(
                    argument: onFocusChanged,
                    paramName: nameof(onFocusChanged));

                _onFocusChanged = onFocusChanged;
            }

            /// <inheritdoc />
            public void HandleFocusChangedEvent(IUIAutomationElement sender)
            {
                // Forward only the transition signal because the event worker owns chain materialization and failures.
                _onFocusChanged();
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EventPayload
        {
            [FieldOffset(0)] public MouseHook Mouse;
            [FieldOffset(0)] public KeyboardHook Key;
        }

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

        /// <summary>
        /// Represents a single recorded event within the system,
        /// capturing all relevant low-level and contextual information
        /// such as event data, timing, and native parameters.
        /// </summary>
        private struct EventRecord
        {
            /// <summary>
            /// The pre-dispatch mouse target selected by the hook, or null when unavailable.
            /// </summary>
            public UiaChainModel CapturedMouseTarget;

            /// <summary>
            /// The pre-press focused-element target stamped on a key-down before the key's own effect
            /// (for example Enter closing a dialog) can change focus, or null when unavailable.
            /// </summary>
            public UiaChainModel CapturedKeyboardTarget;

            /// <summary>
            /// Whether a recorder was connected (capture active) at the moment this event was captured.
            /// The worker honors this instead of the live state so a trailing event is not dropped when
            /// the session ends before the event is drained.
            /// </summary>
            public bool CaptureActiveAtCapture;

            /// <summary>
            /// The recorder session generation at the moment this event was captured, used to keep a
            /// Down and its paired Up bound to the same session across a live-state change.
            /// </summary>
            public long SessionGenerationAtCapture;

            /// <summary>
            /// The raw data associated with the event.
            /// This object may contain metadata, input parameters,
            /// or serialized information relevant to event handling logic.
            /// </summary>
            public EventPayload EventData;

            /// <summary>
            /// A textual identifier describing the event category or nature,
            /// such as "mouse", "keyboard", or other custom-defined types.
            /// </summary>
            public EventType EventType;

            /// <summary>
            /// The native <see cref="IntPtr"/> representing the event’s additional message-specific information.
            /// Commonly used in Windows hook callbacks to store extra parameters (e.g., mouse position, key code).
            /// </summary>
            public IntPtr LParam;

            /// <summary>
            /// The age of the inspected hover snapshot when this mouse event was captured.
            /// </summary>
            public double MouseSnapshotAgeMilliseconds;

            /// <summary>
            /// The producer-internal status of pre-click snapshot selection.
            /// </summary>
            public MouseTargetSnapshotStatus MouseSnapshotStatus;

            /// <summary>
            /// The hook code that indicates the type of hook event received.
            /// Typically provided by the system when using low-level Windows hooks.
            /// </summary>
            public int NCode;

            /// <summary>
            /// The timestamp (in ticks or milliseconds) representing when the event occurred.
            /// Used for chronological ordering and timing analysis of recorded events.
            /// </summary>
            public long Timestamp;

            /// <summary>
            /// The native <see cref="IntPtr"/> representing the event’s message identifier.
            /// Often used to specify the type of system message (e.g., WM_KEYDOWN, WM_MOUSEMOVE).
            /// </summary>
            public IntPtr WParam;

            /// <summary>
            /// Creates a new <see cref="EventRecord"/> from a keyboard hook event.
            /// </summary>
            /// <param name="key">The keyboard hook information captured by the low-level callback.</param>
            /// <param name="nCode">The system-provided hook code identifying the event type.</param>
            /// <param name="wParam">The Windows message identifier (e.g., WM_KEYDOWN, WM_KEYUP).</param>
            /// <param name="timestamp">The timestamp (in ticks or milliseconds) when the event was captured.</param>
            /// <returns>A fully constructed <see cref="EventRecord"/> representing the keyboard event.</returns>
            public static EventRecord ConvertFromKey(in KeyboardHook key, int nCode, IntPtr wParam, long timestamp)
            {
                return new EventRecord
                {
                    EventType = EventType.Keyboard,
                    EventData = new EventPayload { Key = key },
                    LParam = IntPtr.Zero,               // Not used for keyboard events in this context
                    NCode = nCode,
                    Timestamp = timestamp,
                    WParam = wParam                     // Message type (WM_KEYDOWN/WM_KEYUP)
                };
            }

            /// <summary>
            /// Creates a new <see cref="EventRecord"/> from a mouse hook event.
            /// </summary>
            /// <param name="mouse">The mouse hook information captured by the low-level callback.</param>
            /// <param name="nCode">The system-provided hook code identifying the event type.</param>
            /// <param name="wParam">The Windows message identifier (e.g., WM_LBUTTONDOWN, WM_MOUSEMOVE).</param>
            /// <param name="timestamp">The timestamp (in ticks or milliseconds) when the event was captured.</param>
            /// <returns>A fully constructed <see cref="EventRecord"/> representing the mouse event.</returns>
            public static EventRecord ConvertFromMouse(in MouseHook mouse, int nCode, IntPtr wParam, long timestamp)
            {
                return new EventRecord
                {
                    EventType = EventType.Mouse,
                    EventData = new EventPayload { Mouse = mouse },
                    LParam = IntPtr.Zero,               // Not used for mouse events in this context
                    NCode = nCode,
                    WParam = wParam,                    // Message type (WM_MOUSEMOVE, etc.)
                    Timestamp = timestamp
                };
            }
        }
        #endregion
    }
}
