/**
 * Background service worker for the G4 Chromium Recorder extension.
 *
 * This is the single long-lived owner of the SignalR connection. It connects to the
 * C# ChromiumPeek hub, forwards interaction events captured by the content scripts to
 * the hub for re-broadcast, generates navigation events, maintains the per-connection
 * recording session and its captured-event stack, and answers status requests from the
 * popup and options pages.
 *
 * @remarks
 * This worker is a classic (non-module) service worker so it can load the vendored UMD
 * SignalR build with importScripts; module workers cannot use importScripts. All shared
 * code is attached to the global g4Recorder namespace by the imported files.
 *
 * Recording is "always-on while connected": each successful connect (including an
 * automatic reconnect) starts a new session, which clears the captured-event stack.
 */

// Load the vendored official SignalR client first so its global is available to the
// connection wrapper, then load the shared modules the worker depends on. Paths are
// resolved from the extension root.
importScripts(
    "/js/signalr.min.js",
    "/lib/constants.js",
    "/lib/settings-store.js",
    "/lib/recorder-contract.js",
    "/lib/signalr-connection.js"
);

// Pull the shared modules and constants loaded above into local references.
const { constants, connection, settings } = self.g4Recorder;
const { EVENT_TYPES, MESSAGE_CHANNELS } = constants;

// The service worker instance is the natural singleton that owns the connection and
// session, so module-level mutable state is required here. Each field is documented.

// The active recorder connection handle, or null while disconnected.
let recorderConnection = null;

// The effective settings snapshot used to build the current connection.
let currentSettings = null;

// A shared in-flight connect attempt. Concurrent events (for example a cold-start
// navigation and a queued click) await this single promise instead of each starting a
// competing connection.
let connectInFlight = null;

// The captured events for the current session, newest last. Cleared each session and
// capped to the configured maximum so the worker's memory cannot grow without bound.
let capturedEvents = [];

// Reorder buffer for outgoing events. Events arrive from independent async sources
// (content-script interactions, tab activation, navigation) that race, so they are held
// briefly and then sent in capture-time order. The hold absorbs the cross-source race;
// the sort key is (orderingTimestamp, sequence).
const EVENT_FLUSH_DELAY_MILLISECONDS = 80;

// Pending events awaiting an ordered flush, each as { event, orderingTimestamp, sequence }.
let pendingEvents = [];

// Monotonic enqueue counter used as the secondary sort key so a switch event always
// precedes its same-timestamp interaction.
let nextEventSequence = 0;

// The pending flush timer handle, or null when no flush is scheduled.
let flushTimerId = null;

// True while a flush is draining, to prevent overlapping drains.
let isFlushing = false;

// Recording floor: events captured before this epoch-ms timestamp are dropped at flush.
// Raised on every session reset (connect/reconnect) and on Clear, so an event captured
// before that moment (for example one held in the buffer or awaiting the connection during
// a reconnect) can never reappear in the cleared/new session.
let floorTimestampMilliseconds = 0;

// The identity and start time of the current session, both null while no session is active.
const sessionState = {
    sessionId: null,
    startedAtMilliseconds: null
};

// Per-tab full-page navigation history, keyed by tab id, used to recover the direction
// of a forward/back navigation (which webNavigation does not report). The worker owns
// this map; it is in-memory and resets if the worker is recycled, in which case the
// first navigation afterward is classified as OpenUrl until the stack rebuilds.
const tabNavigationHistories = new Map();

// Storage.session key prefix for the active frameId per tab. The active frame is used to
// detect frame switches (so a SwitchFrame / SwitchParentFrame can be emitted before the
// interaction that moved between frames). It is persisted in chrome.storage.session so
// detection survives a service-worker restart instead of duplicating or missing switches.
const ACTIVE_FRAME_KEY_PREFIX = "g4Recorder/activeFrame/";

// Storage.session key for the ordered window-handle list (tab ids in creation order,
// index 0 = first/main). This mirrors the driver's WindowHandles list so SwitchWindow and
// CloseWindow report the same index G4 uses. Persisted so indices survive worker restarts.
const WINDOW_HANDLES_KEY = "g4Recorder/windowHandles";

// Storage.session key holding the hub URL injected by the launcher's bootstrap page. It is kept
// in session storage (not sync) so it survives service-worker restarts within this browser session
// but is cleared when the browser closes, keeping the launched profile clean. When present it
// overrides the stored/default hub so an auto-launched browser connects to the server that
// launched it.
const INJECTED_HUB_KEY = "g4Recorder/injectedHubUrl";

// In-memory copy of the window-handle list (the source of truth during a worker lifetime)
// and a load-once promise. Reads are synchronous against this array to avoid storage races
// between the close (onRemoved) and re-activation (onActivated) handlers.
let windowHandleOrder = null;
let windowHandlesLoadPromise = null;

// Friendly frame-switch event names carried in the `event` field of a context event.
const FRAME_SWITCH_EVENT_NAMES = Object.freeze({
    switchFrame: "SwitchFrame",
    switchParentFrame: "SwitchParentFrame"
});

// Friendly navigation event names carried in the `event` field of a navigation event.
const NAVIGATION_EVENT_NAMES = Object.freeze({
    openUrl: "OpenUrl",
    redoNavigation: "RedoNavigation",
    undoNavigation: "UndoNavigation",
    updatePage: "UpdatePage"
});

/**
 * Accepts a fully built recording event for ordered delivery.
 *
 * @remarks
 * Ensures a connection (connecting on demand so a cold worker does not silently drop the
 * event) and then enqueues the event into the reorder buffer. Actual stack update and hub
 * send happen during the ordered flush, so events from racing sources (interactions, tab
 * activation, navigation) are delivered in capture-time order rather than processing order.
 * When auto-connect is disabled and there is no connection, the event is dropped.
 *
 * @param {object} recordingEvent The event in contract shape.
 * @returns {Promise<void>} Resolves once the event has been buffered.
 */
async function addRecordingEvent(recordingEvent) {
    // Only accept events while connected; ensureConnected brings up a cold/idle worker.
    const isReady = await ensureConnected();

    if (!isReady) {
        return;
    }

    enqueueRecordingEvent(recordingEvent);
}

/**
 * Appends a tab to the window-handle list when it is not already tracked.
 *
 * @remarks
 * Owns the in-memory handle list and persists it. The caller must have awaited
 * ensureWindowHandles first so the list is loaded.
 *
 * @param {number} tabId The tab to track.
 * @returns {void}
 */
function addWindowHandle(tabId) {
    // Ignore tabs already in the list so indices stay stable.
    if (windowHandleOrder.includes(tabId)) {
        return;
    }

    windowHandleOrder.push(tabId);

    persistWindowHandles();
}

/**
 * Reads settings, builds a fresh connection, and starts it.
 *
 * @remarks
 * Owns the connection lifecycle. Any existing connection is stopped first so settings
 * changes always produce a single clean connection. Start failures are swallowed
 * because automatic reconnect (or a later manual reconnect) will retry.
 *
 * @returns {Promise<void>} Resolves once a start attempt has completed.
 */
async function connectWithSettings() {
    // Tear down any previous connection so we never run two sockets at once.
    await stopConnection();

    // Load the effective settings that define the reconnect behaviour and toggles.
    currentSettings = await settings.getSettings();

    // Resolve the hub URL, preferring one injected by the launcher's bootstrap page so an
    // auto-launched browser connects to the server that launched it, not the stored/default hub.
    const hubUrl = await getEffectiveHubUrl();

    // Build a connection wired to the worker's lifecycle callbacks so session handling
    // happens in one place regardless of how the connection state changes.
    recorderConnection = connection.newRecorderConnection({
        hubUrl: hubUrl,
        reconnectDelaysMilliseconds: currentSettings.reconnectDelaysMilliseconds,
        onConnected: onConnected,
        onReconnecting: onReconnecting,
        onDisconnected: onDisconnected,
        onCloseBrowser: onCloseBrowserRequested
    });

    // Attempt the connection; report status first so the UI shows "connecting", and let
    // a failed start fall through to automatic reconnect.
    sendStatusToListeners();

    try {
        await recorderConnection.start();
    } catch (error) {
        console.error("[g4-recorder] hub connection failed:", error);

        sendStatusToListeners();
    }
}

/**
 * Resolves the hub URL the connection should use.
 *
 * @remarks
 * Compute-only over storage. A hub injected by the launcher's bootstrap page (persisted in
 * session storage) takes precedence so an auto-launched browser connects back to the server that
 * launched it; otherwise the user-configured/default hub from settings is used, which keeps the
 * options page working for a manually installed extension.
 *
 * @returns {Promise<string>} The effective hub URL.
 */
async function getEffectiveHubUrl() {
    // Prefer the launcher-injected hub when present.
    try {
        const stored = await chrome.storage.session.get(INJECTED_HUB_KEY);
        const injectedHubUrl = stored ? stored[INJECTED_HUB_KEY] : null;

        if (typeof injectedHubUrl === "string" && injectedHubUrl.length > 0) {
            return injectedHubUrl;
        }
    } catch (error) {
        // Session storage may be briefly unavailable; fall through to the settings hub.
    }

    // Fall back to the settings hub (stored value or default), loading settings if needed.
    const effectiveSettings = currentSettings || await settings.getSettings();

    return effectiveSettings.hubUrl;
}

/**
 * Handles a bootstrap-page request to point this browser at a specific hub.
 *
 * @remarks
 * Owns the injected-hub state. Persists the hub in session storage so it survives worker restarts
 * for this browser session, then reconnects when it differs from the current target so the switch
 * from the default hub to the launching server's hub takes effect immediately.
 *
 * @param {string} hubUrl The hub URL derived from the bootstrap page's origin.
 * @returns {Promise<void>} Resolves once the hub is persisted and any reconnect has started.
 */
async function onSetHubRequested(hubUrl) {
    // Ignore an empty or malformed value so a bad message cannot clear a good hub.
    if (typeof hubUrl !== "string" || hubUrl.length === 0) {
        return;
    }

    // Determine the current target before persisting so we only reconnect on a real change.
    const currentHubUrl = await getEffectiveHubUrl();

    await chrome.storage.session.set({ [INJECTED_HUB_KEY]: hubUrl });

    // Reconnect only when the target actually changed (for example the first bootstrap after the
    // startup auto-connect to the default hub).
    if (currentHubUrl !== hubUrl) {
        connectWithSettings();
    }
}

/**
 * Emits a frame-switch event when an interaction's frame differs from the tab's last one.
 *
 * @remarks
 * Owns the per-tab active-frame state, persisted in chrome.storage.session so detection
 * survives a worker restart. Moving to an ancestor of the previous frame is reported as
 * SwitchParentFrame; moving deeper or sideways (including into a frame from the top
 * document) is reported as SwitchFrame. The persisted state is updated before the toggle
 * check so tracking stays correct even when one switch type is disabled. Emits at most one
 * switch event per actual frame change (subsequent same-frame interactions emit nothing).
 *
 * @param {object} options Frame-switch inputs.
 * @param {number|undefined} options.tabId The id of the tab the interaction came from.
 * @param {number} options.frameId The frameId the interaction came from.
 * @param {object} options.frameContext The sending frame's context (url + locator).
 * @param {number} options.triggerTimestamp The epoch-ms timestamp of the triggering interaction.
 * @returns {Promise<void>} Resolves once any switch event has been recorded.
 */
async function emitFrameSwitchIfChanged(options) {
    const { tabId, frameId, frameContext, triggerTimestamp } = options;

    // Without a tab id the per-tab frame state cannot be tracked.
    if (tabId === undefined) {
        return;
    }

    // Compare against the last known frame, defaulting an unseen tab to the top document.
    const lastFrameId = await getActiveFrameId(tabId);
    const isFrameChanged = frameId !== lastFrameId;

    // Nothing to do until the active frame actually changes (dedup repeated same-frame use).
    if (!isFrameChanged) {
        return;
    }

    // Decide direction: moving to an ancestor of the previous frame is a parent switch.
    const isMovingToParent = await testAncestorFrame({
        tabId,
        ancestorFrameId: frameId,
        descendantFrameId: lastFrameId
    });
    const eventName = isMovingToParent
        ? FRAME_SWITCH_EVENT_NAMES.switchParentFrame
        : FRAME_SWITCH_EVENT_NAMES.switchFrame;

    // Persist the new active frame before the toggle check so tracking stays correct even
    // when the matching switch type is disabled.
    await setActiveFrameId(tabId, frameId);

    // Respect the toggle that matches the resolved direction.
    const toggleKey = isMovingToParent
        ? "switchParentFrame"
        : "switchFrame";
    const isSwitchEnabled = currentSettings
        && currentSettings.enabledEvents
        && currentSettings.enabledEvents[toggleKey];

    if (!isSwitchEnabled) {
        return;
    }

    // Build and record the switch event before the interaction that triggered it; sharing
    // the interaction's timestamp keeps it ordered immediately before that interaction.
    const switchEvent = newSwitchFrameEvent({
        frameId,
        frameContext,
        eventName,
        timestamp: triggerTimestamp
    });

    await addRecordingEvent(switchEvent);
}

/**
 * Enqueues a recording event into the reorder buffer and schedules a flush.
 *
 * @remarks
 * Owns the buffer state. The ordering key is the event's true epoch-ms timestamp (the
 * same value sent to the hub) so events that arrive out of order across sources are sorted
 * back into capture order; a monotonic sequence is the secondary key so a switch event
 * always precedes its same-timestamp interaction. The timestamp is intentionally NOT
 * clamped non-decreasing: that would force a later-stamped event that enqueued first (for
 * example a SwitchWindow that a click triggered) to tie with and sort ahead of the earlier
 * click, defeating the reorder.
 *
 * @param {object} recordingEvent The event in contract shape.
 * @returns {void}
 */
function enqueueRecordingEvent(recordingEvent) {
    // Use the event's epoch-ms timestamp for ordering, falling back to now if absent.
    const orderingTimestamp = typeof recordingEvent.timestamp === "number"
        ? recordingEvent.timestamp
        : Date.now();

    // Buffer the event with its ordering key and enqueue sequence.
    pendingEvents.push({
        event: recordingEvent,
        orderingTimestamp,
        sequence: nextEventSequence
    });
    nextEventSequence += 1;

    scheduleEventFlush();
}

/**
 * Ensures settings are loaded and, when auto-connect is enabled, that a live hub
 * connection exists, connecting on demand.
 *
 * @remarks
 * Owns the connect-on-demand path that makes recording resilient to MV3 service-worker
 * termination: a cold worker woken by a navigation or a content-script message loads its
 * settings and brings the connection up here, instead of dropping the event because the
 * socket was not ready yet. A single in-flight connect promise is shared across callers
 * so a burst of events does not start competing connections. Honours auto-connect: when
 * it is disabled and there is no connection, the worker stays idle.
 *
 * @returns {Promise<boolean>} True when a live connection is available.
 */
async function ensureConnected() {
    // Load settings on a cold worker before any toggle or connection decision is made.
    if (!currentSettings) {
        currentSettings = await settings.getSettings();
    }

    // A live connection needs no further work.
    if (testConnectionLive()) {
        return true;
    }

    // Without auto-connect the recorder stays idle until the user connects explicitly.
    if (!currentSettings.isAutoConnectEnabled) {
        return false;
    }

    // Share a single connect attempt across concurrent callers so a burst of events does
    // not spin up competing connections.
    if (!connectInFlight) {
        connectInFlight = connectWithSettings().finally(() => {
            connectInFlight = null;
        });
    }

    await connectInFlight;

    // Re-check the state after the attempt, since the start may still have failed.
    return testConnectionLive();
}

/**
 * Ensures the window-handle list is loaded (or seeded) into memory.
 *
 * @remarks
 * Owns the load-once lifecycle for the handle list. On first use it loads the persisted
 * list from storage.session, or seeds it from the currently-open tabs (ordered by id, so
 * the oldest/main window is index 0). Subsequent calls return the in-memory array.
 *
 * @returns {Promise<number[]>} The in-memory window-handle list.
 */
async function ensureWindowHandles() {
    // Return the already-loaded list without touching storage.
    if (windowHandleOrder !== null) {
        return windowHandleOrder;
    }

    // Load (or seed) exactly once, sharing the promise across concurrent callers.
    if (!windowHandlesLoadPromise) {
        windowHandlesLoadPromise = loadOrSeedWindowHandles();
    }

    windowHandleOrder = await windowHandlesLoadPromise;

    return windowHandleOrder;
}

/**
 * Drains the reorder buffer in capture-time order, sending each event to the hub.
 *
 * @remarks
 * Owns the buffer drain. Each batch is sorted by (orderingTimestamp, sequence) so events
 * are delivered in the order the user acted, regardless of which async source produced
 * them. A single drain runs at a time (isFlushing); events that arrive while draining are
 * handled in the next loop iteration. Each event updates the popup stack and is sent
 * one-at-a-time so hub order matches the sorted order.
 *
 * @returns {Promise<void>} Resolves once the buffer is empty.
 */
async function flushPendingEvents() {
    // Prevent overlapping drains; a running drain will pick up newly arrived events.
    if (isFlushing) {
        return;
    }

    isFlushing = true;

    // Drain batches until nothing is left, ordering each batch before sending.
    try {
        while (pendingEvents.length > 0) {
            // Take the current batch and order it by capture time, then enqueue sequence.
            const batch = pendingEvents;
            pendingEvents = [];

            batch.sort((left, right) => {
                if (left.orderingTimestamp !== right.orderingTimestamp) {
                    return left.orderingTimestamp - right.orderingTimestamp;
                }

                return left.sequence - right.sequence;
            });

            // Record to the session stack and forward to the hub, one event at a time.
            for (const pending of batch) {
                // Drop events captured before the recording floor (a Clear or a session
                // reset happened after they were captured); they must not reappear.
                const isBelowFloor = pending.orderingTimestamp < floorTimestampMilliseconds;

                if (isBelowFloor) {
                    continue;
                }

                recordEventToStack(pending.event);

                await sendRecordingEventToHub(pending.event);
            }
        }
    } finally {
        isFlushing = false;
    }
}

/**
 * Reads the persisted active frameId for a tab.
 *
 * @remarks
 * Compute-only over storage. Defaults to the top document (0) for a tab that has not been
 * seen yet, so a first interaction inside an iframe is detected as a switch.
 *
 * @param {number} tabId The tab to read.
 * @returns {Promise<number>} The active frameId, or 0 when unset.
 */
async function getActiveFrameId(tabId) {
    // Read the per-tab key from session storage; treat a missing value as the top frame.
    const storageKey = `${ACTIVE_FRAME_KEY_PREFIX}${tabId}`;
    const storageResult = await chrome.storage.session.get(storageKey);
    const storedFrameId = storageResult[storageKey];

    return typeof storedFrameId === "number"
        ? storedFrameId
        : 0;
}

/**
 * Extracts the hostname from a URL string.
 *
 * @remarks
 * Compute-only helper. Returns an empty string for opaque or invalid URLs so callers
 * always receive a string.
 *
 * @param {string} urlText The URL to parse.
 * @returns {string} The hostname, or an empty string.
 */
function getHostName(urlText) {
    // Parsing can throw for non-http schemes, so treat any failure as "no host".
    try {
        return new URL(urlText).hostname;
    } catch (error) {
        return "";
    }
}

/**
 * Builds the status snapshot shared with the popup and options pages.
 *
 * @remarks
 * Compute-only. The snapshot is intentionally self-contained so a page can render the
 * whole status from a single message without further queries.
 *
 * @returns {object} The status snapshot.
 */
function getStatusSnapshot() {
    // Resolve the current connection state, defaulting to disconnected when no
    // connection object exists yet.
    const connectionState = recorderConnection
        ? recorderConnection.getState()
        : connection.CONNECTION_STATES.disconnected;

    // Resolve the hub URL to display, falling back to the default before settings load.
    const hubUrl = currentSettings
        ? currentSettings.hubUrl
        : constants.DEFAULT_HUB_URL;

    return {
        connectionState,
        hubUrl,
        sessionId: sessionState.sessionId,
        sessionStartedAtMilliseconds: sessionState.startedAtMilliseconds,
        eventCount: capturedEvents.length,
        events: capturedEvents
    };
}

/**
 * Returns the handle-list index of a tab, or -1 when it is not tracked.
 *
 * @remarks
 * Compute-only over the in-memory list. The caller must have awaited ensureWindowHandles.
 *
 * @param {number} tabId The tab to locate.
 * @returns {number} The 0-based handle index, or -1.
 */
function getWindowIndex(tabId) {
    return windowHandleOrder.indexOf(tabId);
}

/**
 * Loads the persisted window-handle list, or seeds it from the open tabs.
 *
 * @remarks
 * Owns the seeding side effect. A stored list is returned as-is; otherwise the currently
 * open tabs are queried and ordered by id ascending (oldest/main first) to approximate the
 * driver's handle order, then persisted. tab.id is available without the "tabs" permission.
 *
 * @returns {Promise<number[]>} The loaded or seeded handle list.
 */
async function loadOrSeedWindowHandles() {
    // Prefer a previously persisted list so indices stay stable across worker restarts.
    const storageResult = await chrome.storage.session.get(WINDOW_HANDLES_KEY);
    const storedHandles = storageResult[WINDOW_HANDLES_KEY];

    if (Array.isArray(storedHandles)) {
        return storedHandles;
    }

    // Seed from the open tabs, ordered by id so the oldest/main window is index 0.
    let openTabs = [];

    try {
        openTabs = await chrome.tabs.query({}) || [];
    } catch (error) {
        openTabs = [];
    }

    const seededHandles = openTabs
        .map((tab) => tab.id)
        .filter((tabId) => typeof tabId === "number")
        .sort((left, right) => left - right);

    await chrome.storage.session.set({ [WINDOW_HANDLES_KEY]: seededHandles });

    return seededHandles;
}

/**
 * Builds a CloseWindow recording event for a closed tab.
 *
 * @remarks
 * Compute-only. Carries the 0-based handle index G4's CloseWindow action consumes, plus
 * the tabId for traceability.
 *
 * @param {object} options Event inputs.
 * @param {number} options.tabId The id of the closed tab.
 * @param {number} options.index The 0-based handle index that was closed.
 * @returns {object} The CloseWindow event in contract shape.
 */
function newCloseWindowEvent(options) {
    const { tabId, index } = options;
    const { contract } = self.g4Recorder;

    const switchChain = contract.newChain({
        locator: "",
        path: [],
        point: null,
        topWindow: null,
        trigger: "CloseWindow"
    });

    return contract.newRecordingEvent({
        chain: switchChain,
        event: "CloseWindow",
        machineName: "",
        timestamp: Date.now(),
        type: EVENT_TYPES.context,
        value: {
            tabId,
            index
        }
    });
}

/**
 * Builds a navigation recording event from a webNavigation record and a descriptor.
 *
 * @remarks
 * Compute-only. Navigation has no DOM element, so the chain carries an empty path and
 * the destination URL becomes the locator, keeping the contract shape intact. The
 * descriptor supplies the resolved event name and the URL navigated away from.
 *
 * @param {object} options Event inputs.
 * @param {object} options.navigationDetails The webNavigation onCommitted detail record.
 * @param {object} options.descriptor The resolved descriptor ({ event, fromUrl }).
 * @returns {object} The navigation event in contract shape.
 */
function newNavigationEvent(options) {
    const { navigationDetails, descriptor } = options;
    const { contract } = self.g4Recorder;

    // Derive a host identity from the destination URL for the machine descriptor.
    const destinationUrl = navigationDetails.url || "";
    const hostName = getHostName(destinationUrl);

    // Build a minimal chain whose locator is the destination URL and whose path is
    // empty, since there is no element behind a navigation.
    const navigationChain = contract.newChain({
        locator: destinationUrl,
        path: [],
        point: null,
        topWindow: null,
        trigger: descriptor.event
    });

    return contract.newRecordingEvent({
        chain: navigationChain,
        event: descriptor.event,
        machineName: hostName,
        timestamp: navigationDetails.timeStamp || Date.now(),
        type: EVENT_TYPES.navigation,
        value: {
            url: destinationUrl,
            fromUrl: descriptor.fromUrl,
            frameId: navigationDetails.frameId,
            transitionType: navigationDetails.transitionType || "",
            transitionQualifiers: navigationDetails.transitionQualifiers || []
        }
    });
}

/**
 * Builds a frame-switch recording event (SwitchFrame or SwitchParentFrame).
 *
 * @remarks
 * Compute-only. The locator is the iframe element's XPath when the active frame is
 * same-origin (so the switch is replayable); otherwise it falls back to the frame URL.
 * The top document yields the top URL as the locator and an empty element locator.
 *
 * @param {object} options Event inputs.
 * @param {number} options.frameId The frameId moved into (0 for the top document).
 * @param {object} options.frameContext The active frame's context (url + locator).
 * @param {string} options.eventName The resolved event name (SwitchFrame / SwitchParentFrame).
 * @param {number} [options.timestamp] Epoch-ms timestamp to stamp; defaults to now.
 * @returns {object} The switch event in contract shape.
 */
function newSwitchFrameEvent(options) {
    const { frameId, frameContext, eventName, timestamp } = options;
    const { contract } = self.g4Recorder;

    // Inherit the triggering interaction's timestamp when provided so the switch sorts
    // immediately before it; otherwise stamp now.
    const eventTimestamp = typeof timestamp === "number"
        ? timestamp
        : Date.now();

    // Pull the frame URL and the iframe element locator from the sender's context.
    const frameUrl = frameContext ? frameContext.frameUrl : "";
    const frameLocator = frameContext ? frameContext.frameLocator : null;
    const hostName = getHostName(frameUrl);

    // Prefer the iframe element's XPath as the chain locator; fall back to the frame URL
    // when no element locator is available (cross-origin frame or the top document).
    const hasFrameXpath = Boolean(frameLocator && frameLocator.xpath);
    const locatorText = hasFrameXpath
        ? frameLocator.xpath
        : frameUrl;

    // Resolve the element locators once, defaulting to empty strings when absent.
    const frameXpath = frameLocator
        ? frameLocator.xpath
        : "";
    const frameCssSelector = frameLocator
        ? frameLocator.cssSelector
        : "";

    const switchChain = contract.newChain({
        locator: locatorText,
        path: [],
        point: null,
        topWindow: null,
        trigger: eventName
    });

    return contract.newRecordingEvent({
        chain: switchChain,
        event: eventName,
        machineName: hostName,
        timestamp: eventTimestamp,
        type: EVENT_TYPES.context,
        value: {
            frameId,
            frameUrl,
            xpath: frameXpath,
            cssSelector: frameCssSelector
        }
    });
}

/**
 * Builds a SwitchWindow recording event from a tab activation.
 *
 * @remarks
 * Compute-only. Carries the tab id and its 0-based index. The URL/title are intentionally
 * omitted because reading them would require the broader "tabs" permission.
 *
 * @param {object} options Event inputs.
 * @param {number} options.tabId The id of the newly activated tab.
 * @param {number} options.index The 0-based index of the tab in its window.
 * @returns {object} The SwitchWindow event in contract shape.
 */
function newSwitchWindowEvent(options) {
    const { tabId, index } = options;
    const { contract } = self.g4Recorder;

    const switchChain = contract.newChain({
        locator: "",
        path: [],
        point: null,
        topWindow: null,
        trigger: "SwitchWindow"
    });

    return contract.newRecordingEvent({
        chain: switchChain,
        event: "SwitchWindow",
        machineName: "",
        timestamp: Date.now(),
        type: EVENT_TYPES.context,
        value: {
            tabId,
            index
        }
    });
}

/**
 * Handles a hub request to close the browser during a graceful stop.
 *
 * @remarks
 * Owns a browser side effect. The hub sends this message from StopRecorder before it forces
 * a process kill; closing every window quits Chromium because each recorder launch runs in
 * its own dedicated user-data directory, so no unrelated windows are affected. Closing
 * cleanly lets the launcher avoid killing a process tree that Chromium's subprocesses escape.
 *
 * @returns {Promise<void>} Resolves once a close has been requested for every window.
 */
async function onCloseBrowserRequested() {
    // Log receipt so it is visible in the service-worker console whether the hub's CloseBrowser
    // broadcast actually reached this extension (an MV3 worker suspended during an idle gap drops
    // the socket, and the message is then never delivered).
    console.log("[g4-recorder] CloseBrowser received; closing browser windows.");

    try {
        // Enumerate the open browser windows; default to an empty list so a missing result is safe.
        const browserWindows = await chrome.windows.getAll() || [];

        console.log(`[g4-recorder] Closing ${browserWindows.length} window(s).`);

        // Request closing each window; the browser exits once the last one closes. Each removal is
        // isolated so one window that refuses to close (for example a page blocking unload) cannot
        // abort closing the others, and any failure is surfaced instead of silently swallowed.
        await Promise.all(browserWindows.map(async (browserWindow) => {
            try {
                await chrome.windows.remove(browserWindow.id);
            } catch (error) {
                console.error(`[g4-recorder] Failed to close window ${browserWindow.id}:`, error);
            }
        }));

        // As a fallback, close any remaining tabs directly; this covers windows that survived the
        // window-level removal so the browser can still quit when the last tab is gone.
        const remainingTabs = await chrome.tabs.query({}) || [];

        if (remainingTabs.length > 0) {
            console.warn(`[g4-recorder] ${remainingTabs.length} tab(s) still open; removing them directly.`);

            const remainingTabIds = remainingTabs
                .map((tab) => tab.id)
                .filter((tabId) => typeof tabId === "number");

            await chrome.tabs.remove(remainingTabIds).catch((error) => {
                console.error("[g4-recorder] Failed to remove remaining tabs:", error);
            });
        }
    } catch (error) {
        console.error("[g4-recorder] onCloseBrowserRequested failed:", error);
    }
}

/**
 * Handles a successful connect or reconnect by starting a new session.
 *
 * @remarks
 * Owns session state. Per the agreed behaviour, every connect resets the captured-event
 * stack so each connection records a clean session.
 *
 * @returns {void}
 */
function onConnected() {
    // Start a fresh session identity and clear the previous session's events.
    resetSession();

    // Publish the new connected status and empty stack to any open page.
    sendStatusToListeners();
}

/**
 * Handles a permanent disconnect by ending the session.
 *
 * @remarks
 * Owns session state. The captured events are retained until the next connect so a user
 * who reopens the popup right after a drop can still see what was recorded.
 *
 * @returns {void}
 */
function onDisconnected() {
    // Clear the session identity but keep the events for post-mortem inspection.
    sessionState.sessionId = null;
    sessionState.startedAtMilliseconds = null;

    // Publish the disconnected status to any open page.
    sendStatusToListeners();
}

/**
 * Handles a committed top-frame navigation by classifying and recording it.
 *
 * @remarks
 * Owns no state directly; it delegates classification to updateNavigationHistory and
 * recording to addRecordingEvent. Only top-frame (frameId 0) web-scheme navigations are
 * recorded so sub-frame and browser-internal commits do not create noise. It is async
 * because a navigation often wakes a cold worker, so it must wait for settings to load
 * and the connection to come up before it can decide and record.
 *
 * @param {object} navigationDetails The webNavigation onCommitted detail record.
 * @returns {Promise<void>} Resolves once the navigation has been recorded or skipped.
 */
async function onNavigationCommitted(navigationDetails) {
    // Restrict recording to the top document and recordable web schemes.
    const isTopFrame = navigationDetails.frameId === 0;
    const isWebNavigation = testWebUrl(navigationDetails.url);

    if (!isTopFrame || !isWebNavigation) {
        return;
    }

    // Load settings and bring the connection up first, since this event commonly wakes a
    // cold worker. Without a live connection the event cannot be delivered, so stop here.
    const isReady = await ensureConnected();

    if (!isReady) {
        return;
    }

    // Respect the navigation toggle so users can silence navigation noise if desired.
    const isNavigationEnabled = currentSettings
        && currentSettings.enabledEvents
        && currentSettings.enabledEvents.navigation;

    if (!isNavigationEnabled) {
        return;
    }

    // Classify the navigation and advance the per-tab stack. The stack is always updated
    // so direction detection stays correct, but a null event means this navigation is not
    // reported on its own (for example a link click, which is already captured as a Click).
    const descriptor = updateNavigationHistory(navigationDetails);

    if (!descriptor.event) {
        return;
    }

    // Build the navigation event and await the record call so the worker stays alive until
    // the hub send completes; a full-page navigation can otherwise suspend the worker
    // after the popup stack update but before the WebSocket frame is flushed.
    const navigationEvent = newNavigationEvent({ navigationDetails, descriptor });

    await addRecordingEvent(navigationEvent);
}

/**
 * Handles a transient reconnect by republishing status.
 *
 * @remarks
 * Owns no state; it only refreshes the UI so the popup can show the reconnecting state.
 *
 * @returns {void}
 */
function onReconnecting() {
    sendStatusToListeners();
}

/**
 * Records an interaction forwarded by a content script, emitting a SwitchFrame first when
 * the active frame changed.
 *
 * @remarks
 * Owns the ordered delivery of a frame switch followed by its interaction: it awaits the
 * SwitchFrame send before the interaction send so the two arrive at the hub in order. It
 * is awaited by the message router (which returns true and replies on completion) so the
 * worker stays alive through both sends even on a cold start.
 *
 * @param {object} message The recording-event message ({ recordingEvent, frameContext, isFrameSwitchTrigger }).
 * @param {object} sender The message sender metadata.
 * @returns {Promise<void>} Resolves once the events have been recorded.
 */
async function onRecordingEventMessage(message, sender) {
    // Ensure settings are loaded and the connection is up before anything else, so the
    // frame-switch toggle check below reads real settings (not null on a cold worker) and
    // the switch event can actually be delivered. Without this, the switch is silently
    // skipped while the interaction still sends.
    const isReady = await ensureConnected();

    if (!isReady) {
        return;
    }

    // Resolve the originating tab and frame from the sender metadata.
    const tabId = sender.tab
        ? sender.tab.id
        : undefined;
    const frameId = sender.frameId || 0;

    // Only genuine in-frame user gestures (clicks/scroll) may change the active frame and emit a
    // SwitchFrame. Commit events like SendKeys (from a field session) and SubmitForm (from `submit`) can
    // fire in a background or mirror frame the user never interacted with — for example a
    // duplicate search form in a same-origin sub-frame — so they must not trigger a frame switch.
    // Default to true so any event lacking the hint keeps the previous behavior.
    const isFrameSwitchTrigger = message.isFrameSwitchTrigger !== false;

    // Emit a SwitchFrame / SwitchParentFrame first when a gesture moved frames. The switch
    // inherits the interaction's timestamp so it sorts immediately before it.
    if (isFrameSwitchTrigger) {
        await emitFrameSwitchIfChanged({
            tabId,
            frameId,
            frameContext: message.frameContext,
            triggerTimestamp: message.recordingEvent.timestamp
        });
    }

    // Record the interaction itself.
    await addRecordingEvent(message.recordingEvent);
}

/**
 * Routes runtime messages from content scripts and extension pages.
 *
 * @remarks
 * Owns the worker's external command surface. Returns true for the status request so
 * Chrome keeps the message channel open for the asynchronous response.
 *
 * @param {object} message The incoming message with a `channel` field.
 * @param {object} sender The message sender metadata.
 * @param {(response: object) => void} sendResponse The response callback.
 * @returns {boolean} True when a response will be sent asynchronously.
 */
function onRuntimeMessage(message, sender, sendResponse) {
    // A content script captured an interaction and forwarded the built event. Handle it
    // asynchronously and reply on completion; returning true keeps the message channel
    // (and the worker) alive through the connect/send, so events are not dropped on a
    // cold worker.
    if (message.channel === MESSAGE_CHANNELS.recordingEvent) {
        onRecordingEventMessage(message, sender)
            .then(() => sendResponse({ isRecorded: true }))
            .catch((error) => {
                console.error("[g4-recorder] failed to record interaction:", error);

                sendResponse({ isRecorded: false });
            });

        return true;
    }

    // A page asked for the current status; answer asynchronously with a snapshot.
    if (message.channel === MESSAGE_CHANNELS.requestStatus) {
        sendResponse(getStatusSnapshot());

        return false;
    }

    // A page asked to clear the captured-event stack without dropping the connection.
    // Empty the stack and the reorder buffer, cancel any pending flush, and raise the
    // recording floor so an event captured before the clear (buffered or still awaiting the
    // connection) cannot land back in the list afterwards.
    if (message.channel === MESSAGE_CHANNELS.clearStack) {
        capturedEvents = [];
        pendingEvents = [];
        floorTimestampMilliseconds = Date.now();

        if (flushTimerId !== null) {
            clearTimeout(flushTimerId);
            flushTimerId = null;
        }

        sendStatusToListeners();

        return false;
    }

    // The launcher's bootstrap page reported which hub this launched browser must use. Persist it
    // and reconnect asynchronously; returning true keeps the worker alive through the reconnect.
    if (message.channel === MESSAGE_CHANNELS.setHub) {
        onSetHubRequested(message.hubUrl)
            .then(() => sendResponse({ isHubSet: true }))
            .catch((error) => {
                console.error("[g4-recorder] failed to set hub:", error);

                sendResponse({ isHubSet: false });
            });

        return true;
    }

    // A page asked to reconnect, usually after changing the hub URL in settings.
    if (message.channel === MESSAGE_CHANNELS.reconnect) {
        connectWithSettings();

        return false;
    }

    // Settings changed elsewhere; rebuild the connection so new settings take effect.
    if (message.channel === MESSAGE_CHANNELS.settingsChanged) {
        connectWithSettings();

        return false;
    }

    // Unknown channels are ignored so unrelated messaging cannot disturb the worker.
    return false;
}

/**
 * Tracks a newly created tab in the window-handle list.
 *
 * @remarks
 * Owns no event output; it only keeps the handle list current so SwitchWindow and
 * CloseWindow report the correct index. Async because the list may need loading first.
 *
 * @param {object} tab The chrome.tabs.onCreated tab.
 * @returns {Promise<void>} Resolves once the tab is tracked.
 */
async function onTabCreated(tab) {
    // Ignore tabs without an id (should not happen) before touching the list.
    if (typeof tab.id !== "number") {
        return;
    }

    await ensureWindowHandles();

    addWindowHandle(tab.id);
}

/**
 * Records a SwitchWindow event when the user activates a different tab.
 *
 * @remarks
 * Owns no persistent state of its own. The reported index is the tab's position in the
 * window-handle list (driver handle order, 0 = main), matching G4's SwitchWindow. Async
 * because it waits for the connection and the handle list before recording.
 *
 * @param {object} activeInfo The chrome.tabs.onActivated detail ({ tabId, windowId }).
 * @returns {Promise<void>} Resolves once the SwitchWindow event has been recorded.
 */
async function onTabActivated(activeInfo) {
    // Bring the connection up first so a tab switch on a cold worker is still delivered.
    const isReady = await ensureConnected();

    if (!isReady) {
        return;
    }

    // Respect the switchWindow toggle.
    const isSwitchWindowEnabled = currentSettings
        && currentSettings.enabledEvents
        && currentSettings.enabledEvents.switchWindow;

    if (!isSwitchWindowEnabled) {
        return;
    }

    // Resolve the handle-order index, tracking the tab if it was somehow not seen yet.
    await ensureWindowHandles();
    addWindowHandle(activeInfo.tabId);

    const windowIndex = getWindowIndex(activeInfo.tabId);

    // Build and record the SwitchWindow event with the handle-order index.
    const switchWindowEvent = newSwitchWindowEvent({ tabId: activeInfo.tabId, index: windowIndex });

    await addRecordingEvent(switchWindowEvent);
}

/**
 * Records a CloseWindow event and forgets a tab's tracked state when it closes.
 *
 * @remarks
 * Owns cleanup of the per-tab navigation history, active-frame entry, and handle list. The
 * closed tab's handle index is resolved and the tab removed from the list before any await
 * that could let the following onActivated run, so the subsequent SwitchWindow reports the
 * correct (shifted) index. Async because it waits for the connection before recording.
 *
 * @param {number} tabId The id of the closed tab.
 * @returns {Promise<void>} Resolves once the CloseWindow event has been recorded.
 */
async function onTabRemoved(tabId) {
    tabNavigationHistories.delete(tabId);

    // Drop the persisted active-frame entry for the closed tab; ignore failures.
    removeActiveFrameId(tabId).catch(() => {
        // Storage may be unavailable during shutdown; nothing to recover.
    });

    // Resolve the closed tab's handle index, then remove it from the list immediately so a
    // following onActivated computes its index against the updated list.
    await ensureWindowHandles();

    const closedIndex = getWindowIndex(tabId);
    removeWindowHandle(tabId);

    // Nothing to report for a tab that was never tracked.
    if (closedIndex < 0) {
        return;
    }

    // Bring the connection up so a close on a cold worker is still delivered.
    const isReady = await ensureConnected();

    if (!isReady) {
        return;
    }

    // Respect the closeWindow toggle.
    const isCloseWindowEnabled = currentSettings
        && currentSettings.enabledEvents
        && currentSettings.enabledEvents.closeWindow;

    if (!isCloseWindowEnabled) {
        return;
    }

    // Build and record the CloseWindow event with the closed handle index.
    const closeWindowEvent = newCloseWindowEvent({ tabId, index: closedIndex });

    await addRecordingEvent(closeWindowEvent);
}

/**
 * Persists the in-memory window-handle list to storage.session.
 *
 * @remarks
 * Owns a storage side effect, fire-and-forget. Called after each mutation so the handle
 * order survives a service-worker restart.
 *
 * @returns {void}
 */
function persistWindowHandles() {
    chrome.storage.session.set({ [WINDOW_HANDLES_KEY]: windowHandleOrder }).catch(() => {
        // Storage may be briefly unavailable; the next mutation will persist again.
    });
}

/**
 * Records an event into the session stack and refreshes any open page.
 *
 * @remarks
 * Owns the in-memory stack. Appends the event, trims to the configured maximum so memory
 * cannot grow without bound, and broadcasts the status so the popup reflects the new
 * event. Called from the ordered flush so the stack order matches the delivered order.
 *
 * @param {object} recordingEvent The event in contract shape.
 * @returns {void}
 */
function recordEventToStack(recordingEvent) {
    // Append to the session stack.
    capturedEvents.push(recordingEvent);

    // Resolve the configured cap, treating a missing setting as no trimming.
    const maximumStackSize = currentSettings
        ? currentSettings.maximumStackSize
        : 0;

    // Trim from the front when the stack has grown beyond a positive cap.
    const isStackOverCapacity = maximumStackSize > 0 && capturedEvents.length > maximumStackSize;

    if (isStackOverCapacity) {
        capturedEvents = capturedEvents.slice(capturedEvents.length - maximumStackSize);
    }

    // Notify any open page so the popup reflects the new event immediately.
    sendStatusToListeners();
}

/**
 * Removes the persisted active frameId for a tab.
 *
 * @remarks
 * Owns a storage side effect. Called when a tab closes so the session store does not grow
 * unbounded over a long recording session.
 *
 * @param {number} tabId The tab whose active-frame entry should be removed.
 * @returns {Promise<void>} Resolves once the entry is removed.
 */
async function removeActiveFrameId(tabId) {
    const storageKey = `${ACTIVE_FRAME_KEY_PREFIX}${tabId}`;

    await chrome.storage.session.remove(storageKey);
}

/**
 * Removes a tab from the window-handle list when it closes.
 *
 * @remarks
 * Owns the in-memory handle list and persists it. The caller must have awaited
 * ensureWindowHandles. Removing a closed handle shifts the indices of later handles, which
 * is exactly the driver-handle behaviour G4 expects.
 *
 * @param {number} tabId The closed tab to drop.
 * @returns {void}
 */
function removeWindowHandle(tabId) {
    const handleIndex = windowHandleOrder.indexOf(tabId);

    // Nothing to remove for a tab that was never tracked.
    if (handleIndex < 0) {
        return;
    }

    windowHandleOrder.splice(handleIndex, 1);

    persistWindowHandles();
}

/**
 * Starts a fresh session: new identity, new start time, empty stack.
 *
 * @remarks
 * Owns session state. Extracted so connect and reconnect share identical reset logic.
 *
 * @returns {void}
 */
function resetSession() {
    // Assign a new identity and timestamp so consumers can distinguish sessions.
    sessionState.sessionId = crypto.randomUUID();
    sessionState.startedAtMilliseconds = Date.now();

    // Clear the previous session's captured events and any buffered-but-unsent events so
    // they cannot leak into the new session, and raise the recording floor so an event
    // captured before this reset (for example one held during a reconnect) is dropped.
    capturedEvents = [];
    pendingEvents = [];
    floorTimestampMilliseconds = Date.now();
}

/**
 * Schedules an ordered flush of the reorder buffer after the hold window.
 *
 * @remarks
 * Owns the flush timer. Coalesces a burst of events into one ordered flush; the hold
 * window absorbs the cross-source race (for example a click and the tab activation it
 * triggers) so they can be ordered by timestamp together.
 *
 * @returns {void}
 */
function scheduleEventFlush() {
    // Coalesce into a single pending timer.
    if (flushTimerId !== null) {
        return;
    }

    flushTimerId = setTimeout(() => {
        flushTimerId = null;

        flushPendingEvents();
    }, EVENT_FLUSH_DELAY_MILLISECONDS);
}

/**
 * Sends a single event to the hub, isolating per-event failures.
 *
 * @remarks
 * Owns a network side effect. A rejection (for example a payload the hub cannot bind) is
 * logged but never interrupts the drain, mirroring the desktop recorder's per-event
 * isolation.
 *
 * @param {object} recordingEvent The event in contract shape.
 * @returns {Promise<void>} Resolves once the send attempt has completed.
 */
async function sendRecordingEventToHub(recordingEvent) {
    // The connection may have dropped between enqueue and flush; nothing to send then.
    if (!recorderConnection) {
        return;
    }

    // Forward the event; a not-connected state resolves to a no-op inside the wrapper.
    try {
        await recorderConnection.sendRecordingEvent(recordingEvent);
    } catch (error) {
        console.error("[g4-recorder] hub rejected recording event:", error);
    }
}

/**
 * Publishes the current status snapshot to any listening extension page.
 *
 * @remarks
 * Owns a messaging side effect. The send rejects when no page is open to receive it,
 * which is expected and ignored.
 *
 * @returns {void}
 */
function sendStatusToListeners() {
    // Broadcast the snapshot; swallow the "no receiver" rejection that occurs whenever
    // the popup and options page are both closed.
    const statusMessage = {
        channel: MESSAGE_CHANNELS.statusChanged,
        status: getStatusSnapshot()
    };

    chrome.runtime.sendMessage(statusMessage).catch(() => {
        // No open page is listening; nothing to do.
    });
}

/**
 * Persists the active frameId for a tab.
 *
 * @remarks
 * Owns a storage side effect. Stored in chrome.storage.session so frame-switch detection
 * survives a service-worker restart.
 *
 * @param {number} tabId The tab to update.
 * @param {number} frameId The frameId now active in the tab.
 * @returns {Promise<void>} Resolves once the value is written.
 */
async function setActiveFrameId(tabId, frameId) {
    const storageKey = `${ACTIVE_FRAME_KEY_PREFIX}${tabId}`;

    await chrome.storage.session.set({ [storageKey]: frameId });
}

/**
 * Stops and disposes the current connection, if any.
 *
 * @remarks
 * Owns the connection lifecycle. Safe to call when already disconnected.
 *
 * @returns {Promise<void>} Resolves once the connection is stopped.
 */
async function stopConnection() {
    // Nothing to stop when no connection exists.
    if (!recorderConnection) {
        return;
    }

    // Stop the socket, tolerating errors from an already-closing connection.
    try {
        await recorderConnection.stop();
    } catch (error) {
        console.warn("[g4-recorder] Error while stopping connection:", error);
    }

    recorderConnection = null;
}

/**
 * Tests whether a navigation originated from the address bar (a typed URL or search).
 *
 * @remarks
 * Compute-only helper. Only these navigations are reported as OpenUrl; link clicks, form
 * submits, and redirects are represented by the captured interaction instead.
 *
 * @param {string} transitionType The webNavigation transitionType.
 * @param {string[]} transitionQualifiers The webNavigation transitionQualifiers.
 * @returns {boolean} True when the navigation came from the address bar.
 */
function testAddressBarNavigation(transitionType, transitionQualifiers) {
    // Transition types produced by typing a URL or a search/keyword into the address bar.
    const addressBarTransitionTypes = ["typed", "generated", "keyword", "keyword_generated"];

    // Either a typed transition type or the explicit from_address_bar qualifier counts.
    const isTypedTransition = addressBarTransitionTypes.includes(transitionType);
    const isFromAddressBar = Array.isArray(transitionQualifiers)
        && transitionQualifiers.includes("from_address_bar");
    const isAddressBarNavigation = isTypedTransition || isFromAddressBar;

    return isAddressBarNavigation;
}

/**
 * Tests whether one frame is an ancestor of another within a tab.
 *
 * @remarks
 * Compute-only over the frame tree. Uses chrome.webNavigation.getAllFrames to build a
 * frameId -> parentFrameId map, then walks the descendant's parent chain looking for the
 * candidate ancestor. The top document (0) is an ancestor of every sub-frame. Used to
 * tell a move "up" the tree (SwitchParentFrame) from a move deeper or sideways
 * (SwitchFrame).
 *
 * @param {object} options Hierarchy inputs.
 * @param {number} options.tabId The tab whose frame tree to inspect.
 * @param {number} options.ancestorFrameId The candidate ancestor frame.
 * @param {number} options.descendantFrameId The frame to walk upward from.
 * @returns {Promise<boolean>} True when ancestorFrameId is an ancestor of descendantFrameId.
 */
async function testAncestorFrame(options) {
    const { tabId, ancestorFrameId, descendantFrameId } = options;

    // The previous frame being the top document means we can only be going deeper.
    if (descendantFrameId === 0) {
        return false;
    }

    // The top document is an ancestor of every sub-frame, so a move to it is always "up".
    if (ancestorFrameId === 0) {
        return true;
    }

    // Resolve the tab's frame tree; treat a failure as "not an ancestor" (defaults to
    // SwitchFrame), which is the safe, non-surprising classification.
    let allFrames = [];

    try {
        allFrames = await chrome.webNavigation.getAllFrames({ tabId }) || [];
    } catch (error) {
        return false;
    }

    // Map each frame to its parent so the descendant's chain can be walked upward.
    const parentByFrameId = new Map(allFrames.map((frame) => [frame.frameId, frame.parentFrameId]));

    // Walk up from the descendant; a bounded loop guards against malformed cycles.
    let currentFrameId = descendantFrameId;

    for (let step = 0; step < 100; step += 1) {
        const parentFrameId = parentByFrameId.has(currentFrameId)
            ? parentByFrameId.get(currentFrameId)
            : -1;

        // Reached the root or an unknown frame without finding the candidate ancestor.
        if (parentFrameId === -1) {
            return false;
        }

        // Found the candidate as a parent somewhere up the chain.
        if (parentFrameId === ancestorFrameId) {
            return true;
        }

        currentFrameId = parentFrameId;
    }

    return false;
}

/**
 * Tests whether the current hub connection is live.
 *
 * @remarks
 * Compute-only helper over the module-level connection handle, extracted so the
 * connect-on-demand flow does not repeat the same state check.
 *
 * @returns {boolean} True when a connection exists and reports the connected state.
 */
function testConnectionLive() {
    // A connection is live only when the handle exists and reports the connected state.
    const isConnectionLive = Boolean(recorderConnection)
        && recorderConnection.getState() === connection.CONNECTION_STATES.connected;

    return isConnectionLive;
}

/**
 * Tests whether a URL uses a recordable web scheme.
 *
 * @remarks
 * Compute-only helper. Only http and https navigations are recorded; browser-internal
 * schemes (chrome://, about:, devtools://) are treated as non-web and ignored.
 *
 * @param {string} urlText The URL to test.
 * @returns {boolean} True for http(s) URLs.
 */
function testWebUrl(urlText) {
    // Treat any unparseable or non-http(s) URL as not recordable.
    try {
        const protocol = new URL(urlText).protocol;
        const isWebProtocol = protocol === "http:" || protocol === "https:";

        return isWebProtocol;
    } catch (error) {
        return false;
    }
}

/**
 * Classifies a top-frame navigation and advances the per-tab history stack.
 *
 * @remarks
 * Owns the per-tab navigation history (module-level tabNavigationHistories). It both
 * resolves the navigation kind (OpenUrl, UpdatePage, UndoNavigation, RedoNavigation) and
 * advances the stack so the next navigation can be classified. Direction for a
 * forward/back navigation is recovered by matching the destination URL against the
 * neighbours of the current entry, since webNavigation reports forward_back without a
 * direction. Returns the resolved event name and the URL navigated away from.
 *
 * @param {object} navigationDetails The webNavigation onCommitted detail record.
 * @returns {object} A descriptor: { event, fromUrl }.
 */
function updateNavigationHistory(navigationDetails) {
    const { tabId, url, transitionType, transitionQualifiers } = navigationDetails;

    // Resolve or create this tab's history stack and current position.
    const history = tabNavigationHistories.get(tabId) || { entries: [], index: -1 };

    // The URL being left is whatever the current position points at.
    const fromUrl = history.index >= 0
        ? history.entries[history.index]
        : "";

    // A reload stays on the same entry, so report it without changing the stack.
    if (transitionType === "reload") {
        tabNavigationHistories.set(tabId, history);

        return { event: NAVIGATION_EVENT_NAMES.updatePage, fromUrl };
    }

    // A forward/back navigation is matched against the neighbours of the current entry
    // to recover the direction the user travelled.
    const isForwardBack = Array.isArray(transitionQualifiers)
        && transitionQualifiers.includes("forward_back");

    if (isForwardBack) {
        // Going back lands on the entry immediately before the current position.
        const isBackAvailable = history.index > 0;
        const isBackMatch = isBackAvailable && history.entries[history.index - 1] === url;

        if (isBackMatch) {
            history.index -= 1;
            tabNavigationHistories.set(tabId, history);

            return { event: NAVIGATION_EVENT_NAMES.undoNavigation, fromUrl };
        }

        // Going forward lands on the entry immediately after the current position.
        const isForwardAvailable = history.index < history.entries.length - 1;
        const isForwardMatch = isForwardAvailable && history.entries[history.index + 1] === url;

        if (isForwardMatch) {
            history.index += 1;
            tabNavigationHistories.set(tabId, history);

            return { event: NAVIGATION_EVENT_NAMES.redoNavigation, fromUrl };
        }

        // The destination is not an adjacent entry (stack lost or duplicate URLs); fall
        // through and treat it as a fresh navigation so the event is still recorded.
    }

    // A fresh navigation drops any forward history, appends the URL, and points at it.
    // The stack is updated for every fresh navigation so direction detection stays
    // correct, even for ones that are not reported as their own event.
    const trimmedEntries = history.entries.slice(0, history.index + 1);
    trimmedEntries.push(url);

    const nextHistory = { entries: trimmedEntries, index: trimmedEntries.length - 1 };
    tabNavigationHistories.set(tabId, nextHistory);

    // Only an address-bar (typed) navigation is reported as OpenUrl. Link clicks, form
    // submits, and redirects are already represented by the captured interaction, so they
    // advance the stack but emit no navigation event (event left null).
    const isAddressBarNavigation = testAddressBarNavigation(transitionType, transitionQualifiers);
    const event = isAddressBarNavigation
        ? NAVIGATION_EVENT_NAMES.openUrl
        : null;

    return { event, fromUrl };
}

// Route every runtime message through the single handler above.
chrome.runtime.onMessage.addListener(onRuntimeMessage);

// Record top-frame full-document navigations (open, reload, back, forward). Guarded so
// that, if the webNavigation API is unavailable (for example the permission did not take
// effect after a manifest change), the failure is reported clearly instead of throwing
// and aborting the remaining listener registrations below.
if (chrome.webNavigation && chrome.webNavigation.onCommitted) {
    chrome.webNavigation.onCommitted.addListener(onNavigationCommitted);
} else {
    console.error(
        "[g4-recorder] chrome.webNavigation is unavailable - navigation events are "
        + "disabled. Confirm the 'webNavigation' permission is present and fully reload "
        + "the extension (remove and re-add it if a soft reload does not apply it)."
    );
}

// Track newly created tabs in the window-handle list so indices stay correct.
if (chrome.tabs && chrome.tabs.onCreated) {
    chrome.tabs.onCreated.addListener(onTabCreated);
}

// Record a SwitchWindow event when the user activates a different tab.
if (chrome.tabs && chrome.tabs.onActivated) {
    chrome.tabs.onActivated.addListener(onTabActivated);
}

// Record a CloseWindow event (and forget tracked state) once a tab is closed.
if (chrome.tabs && chrome.tabs.onRemoved) {
    chrome.tabs.onRemoved.addListener(onTabRemoved);
}

// Connect on browser startup and on install/update so the recorder is ready without
// requiring the user to open the popup first. Routed through the guarded ensureConnected
// so these paths share the single in-flight connect and honour the auto-connect setting.
chrome.runtime.onStartup.addListener(ensureConnected);
chrome.runtime.onInstalled.addListener(ensureConnected);

// Also attempt a connection as soon as the worker spins up. This covers worker restarts
// that are not tied to startup or install (for example a restart triggered by an event).
ensureConnected();
