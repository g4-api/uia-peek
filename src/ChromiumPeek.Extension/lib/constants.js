/**
 * Shared constants for the G4 Chromium Recorder extension.
 *
 * This module defines every value that more than one part of the extension needs
 * to agree on: the SignalR hub URL and method names, the runtime message channels
 * used between the content scripts and the background service worker, the storage
 * key, and the default user settings.
 *
 * @remarks
 * The file attaches its exports to a single global namespace (`globalThis.g4Recorder`)
 * instead of using ES imports. This is intentional: the same file must load in three
 * different execution contexts that do not share a module system here:
 *   - content scripts (classic scripts listed in the manifest, isolated world),
 *   - the background service worker (loaded with `importScripts`),
 *   - the popup and options HTML pages (loaded with `<script>` tags).
 * A shared global namespace is the only loading strategy that works the same way in
 * all three contexts while keeping the extension fully offline.
 */
(function initializeConstantsModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // The default SignalR hub endpoint. The official SignalR client receives the
    // http(s) URL and upgrades it to a WebSocket connection at runtime.
    const DEFAULT_HUB_URL = "http://localhost:9956/hub/v4/g4/peek";

    // Names of the hub methods the extension invokes on the server. These must match
    // the C# ChromiumPeekHub method names exactly (case-insensitive on the wire).
    const HUB_METHOD_NAMES = {
        sendRecordingEvent: "SendRecordingEvent"
    };

    // Name of the server-to-client broadcast. The extension does not consume it, but
    // it is documented here so the contract with consumer clients stays discoverable.
    const SERVER_BROADCAST_NAME = "ReceiveRecordingEvent";

    // Name of the server-to-client message the hub sends to ask this extension to close its
    // browser windows for a graceful stop. Must match ChromiumPeekHub.CloseBrowserClientMethod.
    const SERVER_CLOSE_BROWSER_NAME = "CloseBrowser";

    // The hub path appended to a server origin to form a full hub URL. Used when the launcher's
    // bootstrap page reports which server this freshly launched browser must connect to.
    const SERVER_HUB_PATH = "/hub/v4/g4/peek";

    // The <meta> name that marks the launcher's bootstrap page. When the content script sees this
    // marker it reports the page origin's hub to the background worker instead of recording, so an
    // auto-launched browser connects back to the exact server that launched it.
    const BOOTSTRAP_META_NAME = "g4-recorder-bootstrap";

    // Runtime message channel names used with chrome.runtime messaging. Every message
    // exchanged between the content scripts, popup, options page, and background worker
    // uses one of these stable identifiers as its `channel` field.
    const MESSAGE_CHANNELS = {
        recordingEvent: "g4-recorder/recording-event",
        requestStatus: "g4-recorder/request-status",
        statusChanged: "g4-recorder/status-changed",
        clearStack: "g4-recorder/clear-stack",
        reconnect: "g4-recorder/reconnect",
        settingsChanged: "g4-recorder/settings-changed",
        // Sent by the content script from the launcher's bootstrap page to tell the worker which
        // hub this launched browser must connect to (the launching server's origin + hub path).
        setHub: "g4-recorder/set-hub"
    };

    // Logical event-type buckets carried in the `type` field of a recording event.
    // These mirror the UiaPeek convention (Mouse/Keyboard) and extend it for the web.
    // "Context" covers frame and window switches that change the interaction surface
    // rather than acting on an element.
    const EVENT_TYPES = {
        mouse: "Mouse",
        keyboard: "Keyboard",
        form: "Form",
        navigation: "Navigation",
        context: "Context"
    };

    // The chrome.storage.sync key under which all user settings are persisted.
    const SETTINGS_STORAGE_KEY = "g4RecorderSettings";

    // Default user settings. The settings store merges any stored values on top of
    // these defaults so a missing key always resolves to a safe value.
    const DEFAULT_SETTINGS = {
        hubUrl: DEFAULT_HUB_URL,
        isAutoConnectEnabled: true,
        isRedactPasswordsEnabled: false,
        maximumStackSize: 200,
        // How long the mouse must rest over an element before a Hover MoveMouseCursor is
        // recorded. Only used when enabledEvents.hover is on.
        hoverDwellMilliseconds: 5000,
        reconnectDelaysMilliseconds: [0, 2000, 5000, 10000, 30000],
        enabledEvents: {
            click: true,
            doubleClick: true,
            contextMenu: true,
            input: true,
            change: true,
            keyDown: true,
            keyUp: true,
            keyCombination: true,
            submit: true,
            // Scroll/wheel is off by default: a single gesture fires many wheel events,
            // which floods the recording. It can be re-enabled on the settings page.
            wheel: false,
            // Hover is off by default: it would otherwise fire whenever the mouse pauses.
            // Enable it to record a MoveMouseCursor for grabbing hard-to-inspect locators.
            hover: false,
            navigation: true,
            switchFrame: true,
            switchParentFrame: true,
            switchWindow: true,
            closeWindow: true
        }
    };

    // Expose the constants on the shared namespace as a frozen object so no consumer
    // can accidentally mutate cross-context configuration at runtime.
    namespace.constants = Object.freeze({
        BOOTSTRAP_META_NAME,
        DEFAULT_HUB_URL,
        DEFAULT_SETTINGS,
        EVENT_TYPES,
        HUB_METHOD_NAMES,
        MESSAGE_CHANNELS,
        SERVER_BROADCAST_NAME,
        SERVER_CLOSE_BROWSER_NAME,
        SERVER_HUB_PATH,
        SETTINGS_STORAGE_KEY
    });
})(globalThis);
