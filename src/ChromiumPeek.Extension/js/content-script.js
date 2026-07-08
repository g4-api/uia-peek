/**
 * Content script for the G4 Chromium Recorder extension.
 *
 * Runs in the top document and every child frame. It listens for the DOM interactions
 * named in the event catalog, converts each one into a recorder event whose JSON shape
 * matches the UiaPeek contract, and forwards it to the background service worker, which
 * relays it to the hub. The script never blocks or alters page behaviour: all listeners
 * are passive and no event is ever cancelled.
 *
 * @remarks
 * This file is the last entry in the manifest content-script list, so the constants,
 * settings, contract, locator, mapper, and catalog modules are already attached to the
 * shared g4Recorder namespace by the time this code runs.
 */
(function initializeContentScriptModule(globalScope) {
    // Read the shared modules this script composes. All are loaded earlier in the list.
    const namespace = globalScope.g4Recorder;
    const { constants, contract, locator, mapper, catalog, settings } = namespace;
    const { MESSAGE_CHANNELS } = constants;

    // The content script runs in an isolated world per frame, so a module-level settings
    // cache is the simplest correct place to hold the latest user preferences for the
    // synchronous event handlers. It is refreshed whenever settings change.
    let activeSettings = null;

    // When a <label> is clicked, the browser fires a real click on the label plus a
    // synthetic click on the labelled control. This window collapses that duplicate: the
    // synthetic click (detail === 0) that follows a real click (detail >= 1) within it is
    // skipped, so a label-wrapped control click produces a single InvokeClick.
    const SYNTHETIC_CLICK_WINDOW_MILLISECONDS = 50;
    let lastRealClickTimestamp = 0;

    /**
     * Resolves the deepest real target of a DOM event.
     *
     * @remarks
     * Compute-only helper. composedPath exposes the original target inside shadow DOM,
     * which a retargeted event.target would hide. Falls back to event.target, then to
     * the document element, so the result is always an element when one exists.
     *
     * @param {Event} domEvent The DOM event being recorded.
     * @returns {Element|null} The resolved element, or null when none is available.
     */
    function getEventTarget(domEvent) {
        // Prefer the composed path so shadow-DOM targets are not lost to retargeting.
        const composedPath = typeof domEvent.composedPath === "function"
            ? domEvent.composedPath()
            : [];
        const composedTarget = composedPath.length > 0
            ? composedPath[0]
            : null;

        // Choose the first available candidate that is an element node.
        const candidateTarget = composedTarget || domEvent.target || document.documentElement;

        // Use the candidate only when it is an element node.
        const isElementTarget = Boolean(candidateTarget)
            && candidateTarget.nodeType === Node.ELEMENT_NODE;

        if (isElementTarget) {
            return candidateTarget;
        }

        // Wheel and scroll events can target the document; fall back to its root element.
        return document.documentElement || null;
    }

    /**
     * Resolves this frame's context so the worker can detect frame switches.
     *
     * @remarks
     * Compute-only helper. The worker assigns the numeric frameId from the message
     * sender; this provides the frame URL, whether it is a sub-frame, and a locator for
     * the owning <iframe> element. window.frameElement is only readable for a same-origin
     * parent, so a cross-origin frame returns a null locator (wrapped in try/catch) and
     * the worker falls back to URL plus frameId.
     *
     * @returns {object} The frame context ({ isSubFrame, frameUrl, frameLocator }).
     */
    function getFrameContext() {
        // The top document is never a frame switch target.
        const isSubFrame = globalScope.window !== globalScope.window.top;
        const frameUrl = document.location.href;

        if (!isSubFrame) {
            return { isSubFrame, frameUrl, frameLocator: null };
        }

        // Resolve the owning <iframe> element's locator when the parent is same-origin.
        // Cross-origin access throws, so treat any failure as "no locator available".
        let frameLocator = null;

        try {
            const frameElement = globalScope.window.frameElement;

            if (frameElement) {
                frameLocator = {
                    xpath: locator.getAbsoluteXpath(frameElement),
                    cssSelector: locator.getCssSelector(frameElement)
                };
            }
        } catch (error) {
            frameLocator = null;
        }

        return { isSubFrame, frameUrl, frameLocator };
    }

    /**
     * Builds a complete recording event from a DOM event.
     *
     * @remarks
     * Compute-only. Returns null when the event is not in the catalog or is disabled in
     * settings, so the caller can simply skip it.
     *
     * @param {Event} domEvent The DOM event being recorded.
     * @returns {object|null} An object with the recording event and its frame-switch hint
     *   ({ recordingEvent, isFrameSwitchTrigger }), or null to skip.
     */
    function newRecordingEventFromDom(domEvent) {
        // Resolve the contract descriptor; unknown events yield null and are ignored.
        const descriptor = catalog.resolveEventDescriptor({
            domEvent,
            isRedactPasswordsEnabled: activeSettings.isRedactPasswordsEnabled
        });

        if (!descriptor) {
            return null;
        }

        // Resolve the element the interaction targeted; without one there is no chain.
        const targetElement = getEventTarget(domEvent);

        if (!targetElement) {
            return null;
        }

        // Resolve the trigger point and convert it to the contract point shape, when the
        // event family carries coordinates.
        const pointInput = catalog.getEventPoint(domEvent);
        const point = pointInput
            ? contract.newPoint(pointInput)
            : null;

        // Build the ancestor chain rooted at the target element.
        const chain = mapper.newChainFromElement({
            element: targetElement,
            trigger: descriptor.event,
            point
        });

        // Assemble the top-level event, stamping the page host and capture time.
        const recordingEvent = contract.newRecordingEvent({
            chain,
            event: descriptor.event,
            machineName: document.location.hostname,
            timestamp: Date.now(),
            type: descriptor.type,
            value: descriptor.value
        });

        // Return the event with the catalog's frame-switch hint. The worker uses it so only
        // genuine in-frame gestures (clicks/scroll) change the active frame; commit events
        // (SendKeys from `change`, SubmitForm from `submit`) can fire in a background or mirror
        // frame and must not trigger a SwitchFrame. Default to a trigger unless the descriptor
        // opts out, so any future event keeps switching unless it declares otherwise.
        return {
            recordingEvent,
            isFrameSwitchTrigger: descriptor.isFrameSwitchTrigger !== false
        };
    }

    /**
     * Handles any catalogued DOM event by recording and forwarding it.
     *
     * @remarks
     * Owns the per-event flow but no persistent state. Guards on the settings cache and
     * the per-event toggle so disabled events never build a payload or cross the
     * messaging boundary.
     *
     * @param {Event} domEvent The DOM event being recorded.
     * @returns {void}
     */
    function onRecordableEvent(domEvent) {
        // Do nothing until settings have loaded, so toggles are always respected.
        if (!activeSettings) {
            return;
        }

        // Find the catalog entry for this DOM event so its toggle key can be checked.
        const catalogEntry = catalog.getCatalogEntries().find(
            (entry) => entry.domEventName === domEvent.type
        );

        const isEventEnabled = catalogEntry
            && activeSettings.enabledEvents[catalogEntry.settingKey];

        if (!isEventEnabled) {
            return;
        }

        // Collapse the duplicate click a browser dispatches on a control when its <label>
        // is clicked: skip the synthetic follow-up, but keep keyboard-activated clicks
        // (also detail 0, yet not following a real click) and direct control clicks.
        if (domEvent.type === "click") {
            const isSyntheticClick = domEvent.detail === 0;
            const isFollowingRealClick =
                (Date.now() - lastRealClickTimestamp) < SYNTHETIC_CLICK_WINDOW_MILLISECONDS;

            if (isSyntheticClick && isFollowingRealClick) {
                return;
            }

            if (!isSyntheticClick) {
                lastRealClickTimestamp = Date.now();
            }
        }

        // Build the recording event; skip when the catalog declines to describe it.
        const recording = newRecordingEventFromDom(domEvent);

        if (!recording) {
            return;
        }

        // Forward the event to the background worker along with this frame's context and the
        // frame-switch hint, so the worker emits a SwitchFrame before the interaction only when a
        // genuine in-frame gesture moved to a different frame. Ignore "no receiver" rejections
        // that occur when the worker is briefly asleep between events.
        const recordingMessage = {
            channel: MESSAGE_CHANNELS.recordingEvent,
            recordingEvent: recording.recordingEvent,
            frameContext: getFrameContext(),
            isFrameSwitchTrigger: recording.isFrameSwitchTrigger
        };

        chrome.runtime.sendMessage(recordingMessage).catch(() => {
            // The worker will respawn on the next event; nothing to do here.
        });
    }

    /**
     * Attaches one passive capture-phase listener per catalogued DOM event.
     *
     * @remarks
     * Owns the document event subscriptions for this frame. Capture phase ensures events
     * are seen even when a page stops propagation, and passive listeners guarantee the
     * recorder cannot affect scrolling or default behaviour.
     *
     * @returns {void}
     */
    function startListeners() {
        // Bind the shared handler for every catalogued event name in this frame.
        catalog.getCatalogEntries().forEach((entry) => {
            document.addEventListener(entry.domEventName, onRecordableEvent, {
                capture: true,
                passive: true
            });
        });
    }

    /**
     * Initializes the content script: load settings, watch changes, then bind listeners.
     *
     * @remarks
     * Owns startup ordering. Listeners are bound only after the first settings load so
     * the very first handled event already respects the user's toggles.
     *
     * @returns {Promise<void>} Resolves once listeners are bound.
     */
    async function initializeContentScript() {
        // Load the initial settings into the cache used by the synchronous handlers.
        activeSettings = await settings.getSettings();

        // Keep the cache current so toggle changes take effect without a page reload.
        settings.watchSettings((updatedSettings) => {
            activeSettings = updatedSettings;
        });

        // Begin capturing interactions in this frame.
        startListeners();
    }

    // Start the content script for this frame.
    initializeContentScript();
})(globalThis);
