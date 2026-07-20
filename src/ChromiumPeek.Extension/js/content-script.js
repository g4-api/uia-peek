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

    // Field-session typed-text capture. Typing is recorded from `input` and coalesced until a
    // semantic commit boundary such as focus loss, form submission, field change, or page exit.
    // Ordinary pauses do not split the session. The last input time preserves interaction order,
    // and this state is per frame because each frame has its own isolated content-script world.
    let pendingTypingElement = null;
    let pendingTypingTimestamp = 0;

    // Hover-to-record state. A MoveMouseCursor is recorded once the mouse rests over an element
    // for the configured dwell; any movement restarts the timer, and one event is emitted per
    // rest. Used to grab locators for elements that are hard to inspect. Per frame (isolated
    // world). The fallback dwell guards against a missing/invalid stored value.
    const HOVER_DWELL_FALLBACK_MILLISECONDS = 5000;
    let hoverElement = null;
    let hoverClientX = 0;
    let hoverClientY = 0;
    let hoverDwellTimerId = null;

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
        const descriptor = catalog.resolveEventDescriptor({ domEvent });

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
        // (SendKeys from a field session, SubmitForm from `submit`) can fire in a background or mirror
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
     * Owns the per-event flow and closes a pending field session before form submission.
     * Guards on the settings cache and per-event toggle so disabled catalog events never
     * build their own payload or cross the messaging boundary.
     *
     * @param {Event} domEvent The DOM event being recorded.
     * @returns {void}
     */
    function onRecordableEvent(domEvent) {
        // Do nothing until settings have loaded, so toggles are always respected.
        if (!activeSettings) {
            return;
        }

        // Commit the active field before its form submission so SendKeys precedes SubmitForm even
        // when pressing Enter submits without moving focus away from the field.
        if (domEvent.type === "submit") {
            sendPendingTyping();
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

        // Forward the event to the background worker for relay to the hub.
        sendRecordingMessage(recording);
    }

    /**
     * Forwards a built recording event to the background worker.
     *
     * @remarks
     * Owns the messaging boundary for every recorded event (clicks, scroll, submit, and the
     * field-session SendKeys). Sends this frame's context and the frame-switch hint so the worker
     * emits a SwitchFrame only when a genuine in-frame gesture moved to a different frame.
     * Ignores "no receiver" rejections that occur while the worker is briefly asleep.
     *
     * @param {object} recording The event plus its hint ({ recordingEvent, isFrameSwitchTrigger }).
     * @returns {void}
     */
    function sendRecordingMessage(recording) {
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
     * Tests whether an element accepts typed text (so its typing should be recorded).
     *
     * @remarks
     * Compute-only helper. Text areas, contenteditable hosts, and text-like inputs qualify;
     * checkboxes, radios, buttons, and other non-text inputs do not (those are captured via
     * their click), so a toggle never produces a SendKeys.
     *
     * @param {Element} element The element that produced an input event.
     * @returns {boolean} True when the element accepts typed text.
     */
    function testTypeableElement(element) {
        // Without an element there is nothing typeable.
        if (!element) {
            return false;
        }

        // A contenteditable host accepts typed text directly.
        if (element.isContentEditable) {
            return true;
        }

        // A textarea always accepts typed text.
        if (element.nodeName === "TEXTAREA") {
            return true;
        }

        // An input accepts typed text unless it is a non-text control.
        if (element.nodeName === "INPUT") {
            const nonTextTypes = [
                "checkbox", "radio", "button", "submit", "reset", "file", "image", "range", "color", "hidden"
            ];
            const inputType = (element.type || "text").toLowerCase();

            return !nonTextTypes.includes(inputType);
        }

        return false;
    }

    /**
     * Builds a SendKeys recording event for a field's current text.
     *
     * @remarks
     * Compute-only. Mirrors newRecordingEventFromDom but starts from a stored element (not a
     * live DOM event), because typing is flushed after the fact. Typing carries no point.
     *
     * @param {Element} element The field that was typed into.
     * @param {number} typedTimestamp The epoch-ms time of the last keystroke.
     * @returns {object} The event plus its hint ({ recordingEvent, isFrameSwitchTrigger }).
     */
    function newSendKeysRecording(element, typedTimestamp) {
        // Resolve the SendKeys descriptor (value + redaction) from the field element.
        const descriptor = catalog.newSendKeysDescriptor({
            targetElement: element,
            isRedactPasswordsEnabled: activeSettings.isRedactPasswordsEnabled
        });

        // Build the ancestor chain rooted at the typed element.
        const chain = mapper.newChainFromElement({
            element,
            trigger: descriptor.event,
            point: null
        });

        // Stamp the event with the last input time so it orders at typing time, not flush time.
        const recordingEvent = contract.newRecordingEvent({
            chain,
            event: descriptor.event,
            machineName: document.location.hostname,
            timestamp: typedTimestamp,
            type: descriptor.type,
            value: descriptor.value
        });

        return {
            recordingEvent,
            isFrameSwitchTrigger: descriptor.isFrameSwitchTrigger !== false
        };
    }

    /**
     * Sends the pending typed-text SendKeys, if any, and closes the field session.
     *
     * @remarks
     * Owns the typing commit. Called only at semantic boundaries: focus loss, form submission,
     * page exit, or typing moving to a different field. Skips a field that has since detached so
     * a stale, unlocatable chain is never sent.
     *
     * @returns {void}
     */
    function sendPendingTyping() {
        // Capture and clear the pending state before building so a new field session starts clean.
        const element = pendingTypingElement;
        const typedTimestamp = pendingTypingTimestamp;
        pendingTypingElement = null;
        pendingTypingTimestamp = 0;

        // Nothing can be committed when there is no session or its field left the document.
        if (!element || !element.isConnected) {
            return;
        }

        // Build and forward one SendKeys carrying the field's current text.
        const recording = newSendKeysRecording(element, typedTimestamp);

        sendRecordingMessage(recording);
    }

    /**
     * Tracks typing as one field session until a semantic commit boundary emits SendKeys.
     *
     * @remarks
     * Owns the typing capture. Repeated inputs update the same session regardless of pauses, and
     * moving to a new field commits the previous one first so text is never merged across fields.
     * Respects the `input` toggle and ignores non-text targets.
     *
     * @param {Event} domEvent The input event being recorded.
     * @returns {void}
     */
    function onTypingInput(domEvent) {
        // Do nothing until settings have loaded, so toggles are always respected.
        if (!activeSettings) {
            return;
        }

        // Ignore programmatic/synthetic input: only real user typing should produce a SendKeys.
        // This also stops a value synced into a background/mirror frame (dispatched, not typed)
        // from being recorded as a duplicate SendKeys in that frame.
        if (!domEvent.isTrusted) {
            return;
        }

        // Respect the toggle that governs typed-text capture.
        if (!activeSettings.enabledEvents.input) {
            return;
        }

        // Only text-like fields produce SendKeys; non-text controls are captured via click.
        const element = getEventTarget(domEvent);

        if (!testTypeableElement(element)) {
            return;
        }

        // Commit the previous field when typing changes targets so separate fields never merge.
        if (pendingTypingElement && pendingTypingElement !== element) {
            sendPendingTyping();
        }

        // Track the field and the time of this keystroke as the SendKeys ordering timestamp.
        pendingTypingElement = element;
        pendingTypingTimestamp = Date.now();
    }

    /**
     * Commits pending typed text when the tracked field loses focus.
     *
     * @remarks
     * Owns the blur commit. Guarantees a type-then-click-away interaction is recorded and ordered
     * before the click that caused the blur.
     *
     * @param {Event} domEvent The focusout event.
     * @returns {void}
     */
    function onTypingBlur(domEvent) {
        // Commit only when the field losing focus is the one currently being tracked.
        const element = getEventTarget(domEvent);

        if (pendingTypingElement && pendingTypingElement === element) {
            sendPendingTyping();
        }
    }

    /**
     * Commits pending typed text before this frame's document leaves its current page.
     *
     * @remarks
     * Owns the page-exit boundary. The synchronous send request is started while the content
     * script is still alive so navigation or window closure does not silently discard a focused
     * field that never emitted focusout.
     *
     * @returns {void}
     */
    function onTypingPageHide() {
        sendPendingTyping();
    }

    /**
     * Resolves the configured hover dwell, falling back to a safe default.
     *
     * @remarks
     * Compute-only helper. Settings are merged over defaults, so the value is normally present;
     * the guard covers a missing or non-positive stored value.
     *
     * @returns {number} The dwell in milliseconds.
     */
    function getHoverDwellMilliseconds() {
        const configured = activeSettings.hoverDwellMilliseconds;

        return Number.isFinite(configured) && configured > 0
            ? configured
            : HOVER_DWELL_FALLBACK_MILLISECONDS;
    }

    /**
     * Builds a MoveMouseCursor recording event for the element the mouse rested on.
     *
     * @remarks
     * Compute-only. Mirrors newRecordingEventFromDom but starts from the resting element and
     * cursor position captured on the last move, since there is no live event at dwell time.
     *
     * @param {Element} element The element under the cursor at rest.
     * @param {number} clientX The cursor client X at rest.
     * @param {number} clientY The cursor client Y at rest.
     * @returns {object} The event plus its hint ({ recordingEvent, isFrameSwitchTrigger }).
     */
    function newMoveMouseCursorRecording(element, clientX, clientY) {
        const dwellMilliseconds = getHoverDwellMilliseconds();

        // Resolve the descriptor (coordinates + dwell) and a contract point for the chain.
        const descriptor = catalog.newMoveMouseCursorDescriptor({
            x: clientX,
            y: clientY,
            dwellMilliseconds
        });
        const point = contract.newPoint({ xPosition: clientX, yPosition: clientY });

        // Build the ancestor chain rooted at the rested element.
        const chain = mapper.newChainFromElement({
            element,
            trigger: descriptor.event,
            point
        });

        const recordingEvent = contract.newRecordingEvent({
            chain,
            event: descriptor.event,
            machineName: document.location.hostname,
            timestamp: Date.now(),
            type: descriptor.type,
            value: descriptor.value
        });

        return {
            recordingEvent,
            isFrameSwitchTrigger: descriptor.isFrameSwitchTrigger !== false
        };
    }

    /**
     * Emits the MoveMouseCursor once the mouse has rested for the dwell.
     *
     * @remarks
     * Owns the hover flush. Fires from the dwell timer and does not re-arm, so exactly one
     * event is recorded per rest; the next move re-arms the timer. Skips a target that has
     * since detached so a stale, unlocatable chain is never sent.
     *
     * @returns {void}
     */
    function flushHoverDwell() {
        // The timer has fired; mark it inactive and do not re-arm (one event per rest).
        hoverDwellTimerId = null;

        // Capture and clear the resting target so a later move starts a fresh dwell.
        const element = hoverElement;
        const clientX = hoverClientX;
        const clientY = hoverClientY;
        hoverElement = null;

        // Nothing to record, or the element is gone from the document.
        if (!element || !element.isConnected) {
            return;
        }

        const recording = newMoveMouseCursorRecording(element, clientX, clientY);

        sendRecordingMessage(recording);
    }

    /**
     * Tracks mouse movement and (re)arms the hover dwell timer.
     *
     * @remarks
     * Owns the hover capture. Any real movement restarts the timer, so MoveMouseCursor fires
     * only after the mouse rests for the dwell. Respects the `hover` toggle and ignores
     * synthetic moves. Recomputes the deepest target each move so shadow-DOM elements (common
     * for hard-to-inspect controls) resolve correctly.
     *
     * @param {Event} domEvent The mousemove event.
     * @returns {void}
     */
    function onHoverMove(domEvent) {
        // Do nothing until settings have loaded, so toggles are always respected.
        if (!activeSettings) {
            return;
        }

        // Ignore programmatic/synthetic movement: only a real pointer resting should record.
        if (!domEvent.isTrusted) {
            return;
        }

        // Respect the toggle that governs hover capture.
        if (!activeSettings.enabledEvents.hover) {
            return;
        }

        // Track the element under the cursor and the cursor position for this move.
        hoverElement = getEventTarget(domEvent);
        hoverClientX = domEvent.clientX;
        hoverClientY = domEvent.clientY;

        // Restart the dwell timer; the mouse must stay still for the dwell to record.
        if (hoverDwellTimerId !== null) {
            clearTimeout(hoverDwellTimerId);
        }

        hoverDwellTimerId = setTimeout(flushHoverDwell, getHoverDwellMilliseconds());
    }

    /**
     * Cancels a pending hover dwell when the pointer leaves this frame.
     *
     * @remarks
     * Owns cancellation so a dwell does not complete for an element the pointer already left
     * (for example after moving into another frame).
     *
     * @returns {void}
     */
    function onHoverLeave() {
        if (hoverDwellTimerId !== null) {
            clearTimeout(hoverDwellTimerId);
            hoverDwellTimerId = null;
        }

        hoverElement = null;
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

        // Track `input` events as a field session so ordinary typing pauses cannot split SendKeys.
        document.addEventListener("input", onTypingInput, {
            capture: true,
            passive: true
        });

        // Commit pending typed text when a field loses focus (fast type-then-click-away).
        document.addEventListener("focusout", onTypingBlur, {
            capture: true,
            passive: true
        });

        // Commit a still-focused field before navigation or window closure destroys this frame.
        globalScope.addEventListener("pagehide", onTypingPageHide, {
            capture: true,
            passive: true
        });

        // Record a MoveMouseCursor when the mouse rests over an element for the dwell; movement
        // restarts the timer and leaving the frame cancels it.
        document.addEventListener("mousemove", onHoverMove, {
            capture: true,
            passive: true
        });

        document.addEventListener("mouseleave", onHoverLeave, {
            capture: true,
            passive: true
        });
    }

    /**
     * Reports the recorder hub to the background worker when this is the launcher's bootstrap page.
     *
     * @remarks
     * The launcher opens each freshly launched browser on its own server origin, marked with the
     * bootstrap <meta>. Recognizing that marker here lets the worker connect back to the exact
     * server that launched this browser (derived from this page's origin), instead of the default
     * hub. Only the top document is considered, and the page itself is never recorded.
     *
     * @returns {boolean} True when this was the bootstrap page (so recording must be skipped).
     */
    function tryReportBootstrapHub() {
        // Only the top document can be the bootstrap page; sub-frames never carry the marker.
        if (globalScope.window !== globalScope.window.top) {
            return false;
        }

        // The marker <meta> identifies the launcher's bootstrap page.
        const marker = document.querySelector(`meta[name="${constants.BOOTSTRAP_META_NAME}"]`);

        if (!marker) {
            return false;
        }

        // The hub lives at this server's origin; the launcher opened this page on the server that
        // should receive this browser's events.
        const hubUrl = globalScope.location.origin + constants.SERVER_HUB_PATH;

        chrome.runtime.sendMessage({
            channel: MESSAGE_CHANNELS.setHub,
            hubUrl: hubUrl
        }).catch(() => {
            // The worker will respawn and re-read the persisted hub; nothing to do here.
        });

        return true;
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
        // On the launcher's bootstrap page, report the hub and skip recording entirely so the
        // bootstrap tab never produces events.
        if (tryReportBootstrapHub()) {
            return;
        }

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
