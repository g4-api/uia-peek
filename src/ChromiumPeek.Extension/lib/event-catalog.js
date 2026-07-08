/**
 * Event catalog for the G4 Chromium Recorder extension.
 *
 * Translates a raw DOM event into the contract's three descriptive fields: `type`
 * (the category, for example "Mouse"), `event` (the specific name, for example
 * "Click"), and `value` (the event-specific payload). It also resolves which settings
 * toggle and which trigger point apply to each event.
 *
 * @remarks
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded
 * as a content script in every frame. Depends on the constants module for the type
 * names and the settings toggle keys.
 */
(function initializeEventCatalogModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // Pull the shared event-type bucket names so descriptors stay consistent.
    const { EVENT_TYPES } = namespace.constants;

    // Wheel devices report deltas in arbitrary units; this divisor normalises a typical
    // notch to one, matching the desktop recorder's WHEEL_DELTA handling.
    const WHEEL_NOTCH_DELTA_PIXELS = 120;

    // The DOM event names the recorder binds, paired with the settings toggle key that
    // enables each one. Declared once here so the content script and the catalog agree.
    const CATALOG_ENTRIES = [
        { domEventName: "click", settingKey: "click" },
        { domEventName: "dblclick", settingKey: "doubleClick" },
        { domEventName: "contextmenu", settingKey: "contextMenu" },
        { domEventName: "input", settingKey: "input" },
        { domEventName: "change", settingKey: "change" },
        { domEventName: "keydown", settingKey: "keyDown" },
        { domEventName: "keyup", settingKey: "keyUp" },
        { domEventName: "submit", settingKey: "submit" },
        { domEventName: "wheel", settingKey: "wheel" }
    ];

    /**
     * Returns the catalog entries describing which DOM events to bind.
     *
     * @remarks
     * Compute-only. The content script uses this list to attach one listener per entry
     * rather than hard-coding event names, keeping the binding and the catalog in sync.
     *
     * @returns {object[]} The catalog entries, each with a domEventName and settingKey.
     */
    function getCatalogEntries() {
        return CATALOG_ENTRIES;
    }

    /**
     * Resolves the trigger point for a DOM event, when it has pointer coordinates.
     *
     * @remarks
     * Compute-only. Keyboard and form events have no meaningful point, so they return
     * null and the chain records no point, exactly like the desktop recorder.
     *
     * @param {Event} domEvent The DOM event being recorded.
     * @returns {object|null} A point input ({ xPosition, yPosition }) or null.
     */
    function getEventPoint(domEvent) {
        // Only pointer-style events expose client coordinates worth recording.
        const hasClientCoordinates = typeof domEvent.clientX === "number"
            && typeof domEvent.clientY === "number";

        if (!hasClientCoordinates) {
            return null;
        }

        return {
            xPosition: domEvent.clientX,
            yPosition: domEvent.clientY
        };
    }

    /**
     * Resolves the contract descriptor (type, event, value) for a DOM event.
     *
     * @remarks
     * Compute-only. Each branch builds the value payload appropriate to its event
     * family. Password redaction is applied here so no sensitive value ever leaves the
     * content script when the user enables the option.
     *
     * @param {object} options Descriptor inputs.
     * @param {Event} options.domEvent The DOM event being recorded.
     * @param {boolean} options.isRedactPasswordsEnabled Whether to mask secret inputs.
     * @returns {object|null} The descriptor ({ type, event, value }) or null to ignore.
     */
    function resolveEventDescriptor(options) {
        const { domEvent, isRedactPasswordsEnabled } = options;
        const domEventName = domEvent.type;

        // Mouse-family events share a click-style descriptor with client coordinates.
        const isClickFamily = domEventName === "click"
            || domEventName === "dblclick"
            || domEventName === "contextmenu";

        if (isClickFamily) {
            return newClickDescriptor(domEvent);
        }

        // Wheel events become an InvokeScroll action carrying direction and notches.
        if (domEventName === "wheel") {
            return newScrollDescriptor(domEvent);
        }

        // A committed field change is captured as a single SendKeys for the typed text.
        // Checkbox/radio toggles are skipped here because the click already captures them,
        // so their change would otherwise add a redundant SendKeys.
        if (domEventName === "change") {
            if (testToggleInput(domEvent.target)) {
                return null;
            }

            return newSendKeysDescriptor({ domEvent, isRedactPasswordsEnabled });
        }

        // A form submission becomes a SubmitForm action.
        if (domEventName === "submit") {
            return newSubmitDescriptor(domEvent);
        }

        // A key combination (Ctrl/Alt/Meta + key) is a distinct, deferred action. The
        // placeholder testKeyCombination() returns false today, so this never fires yet;
        // see the TODO(key-combination) block on newKeyCombinationDescriptor below.
        const isKeyCombinationEvent = domEventName === "keydown" && testKeyCombination(domEvent);

        if (isKeyCombinationEvent) {
            return newKeyCombinationDescriptor(domEvent);
        }

        // TODO: Single-key and live typing are deferred. keydown/keyup and per-keystroke
        // `input` will later become discrete key actions (Enter/Tab/Esc/arrows) and/or a
        // finer SendKeys stream. For now they are placeholders that emit nothing; typed
        // text is captured as one SendKeys on `change` above.
        const isDeferredKeyEvent = domEventName === "keydown"
            || domEventName === "keyup"
            || domEventName === "input";

        if (isDeferredKeyEvent) {
            return null;
        }

        // Any unrecognised event is ignored so the recorder never emits unknown shapes.
        return null;
    }

    /**
     * Resolves the readable scroll direction for a wheel event.
     *
     * @remarks
     * Compute-only helper extracted so the axis-and-sign decision does not clutter the
     * wheel descriptor builder.
     *
     * @param {object} options Direction inputs.
     * @param {WheelEvent} options.domEvent The wheel event.
     * @param {boolean} options.isVerticalScroll Whether the vertical axis dominates.
     * @returns {string} One of "Up", "Down", "Left", or "Right".
     */
    function getWheelDirection(options) {
        const { domEvent, isVerticalScroll } = options;

        // A dominant vertical axis maps a positive delta to a downward scroll.
        if (isVerticalScroll) {
            return domEvent.deltaY > 0
                ? "Down"
                : "Up";
        }

        // A dominant horizontal axis maps a positive delta to a rightward scroll.
        return domEvent.deltaX > 0
            ? "Right"
            : "Left";
    }

    /**
     * Builds the descriptor for a click-family event.
     *
     * @remarks
     * Compute-only helper. The event name uses the G4 action style: InvokeClick,
     * InvokeDoubleClick, and InvokeContextClick (right-click / context menu).
     *
     * @param {MouseEvent} domEvent The click-family event.
     * @returns {object} The descriptor.
     */
    function newClickDescriptor(domEvent) {
        // Map the raw DOM name to the G4-style action name consumers expect.
        const eventNamesByType = {
            click: "InvokeClick",
            dblclick: "InvokeDoubleClick",
            contextmenu: "InvokeContextClick"
        };

        return {
            type: EVENT_TYPES.mouse,
            event: eventNamesByType[domEvent.type] || "InvokeClick",
            value: {
                button: domEvent.button,
                X: Math.round(domEvent.clientX || 0),
                Y: Math.round(domEvent.clientY || 0)
            }
        };
    }

    /**
     * Placeholder: builds the SendKeysCombination descriptor for a key combination.
     *
     * @remarks
     * DEFERRED — not implemented yet. Currently returns null so nothing is emitted; the
     * guarded caller in resolveEventDescriptor never reaches it because testKeyCombination
     * returns false today.
     *
     * TODO(key-combination): Record keyboard shortcuts (Ctrl/Alt/Meta + key) as a distinct
     *   "SendKeysCombination" event. To implement:
     *   - Build and return:
     *       {
     *         type: EVENT_TYPES.keyboard,
     *         event: "SendKeysCombination",
     *         value: {
     *           combination: "Control+Shift+A", // ordered modifiers + the resolved key
     *           key: domEvent.key,              // e.g. "a"
     *           code: domEvent.code,            // e.g. "KeyA"
     *           isCtrlKey: Boolean(domEvent.ctrlKey),
     *           isAltKey: Boolean(domEvent.altKey),
     *           isShiftKey: Boolean(domEvent.shiftKey),
     *           isMetaKey: Boolean(domEvent.metaKey)
     *         }
     *       }
     *   - Normalise `combination` with a stable modifier order (Ctrl, Alt, Shift, Meta)
     *     followed by the non-modifier key, so replay is deterministic.
     *   - Dedup: a combination must NOT also produce a SendKeys (typing) or a deferred
     *     keyDown event for the same keystroke.
     *
     * @param {KeyboardEvent} domEvent The keydown event.
     * @returns {object|null} The descriptor, or null while deferred.
     */
    function newKeyCombinationDescriptor(domEvent) {
        // TODO(key-combination): build the SendKeysCombination descriptor (see block above).
        return null;
    }

    /**
     * Builds the descriptor for a wheel event as an InvokeScroll action.
     *
     * @remarks
     * Compute-only helper. The readable direction and normalised notch count are carried
     * in the value so the single event name stays InvokeScroll.
     *
     * @param {WheelEvent} domEvent The wheel event.
     * @returns {object} The descriptor.
     */
    function newScrollDescriptor(domEvent) {
        // Resolve the dominant axis so the recorded direction reflects the user's intent.
        const isVerticalScroll = Math.abs(domEvent.deltaY) >= Math.abs(domEvent.deltaX);
        const direction = getWheelDirection({ domEvent, isVerticalScroll });

        // Normalise the raw delta to whole notches, never reporting fewer than one.
        const dominantDelta = isVerticalScroll
            ? domEvent.deltaY
            : domEvent.deltaX;
        const notches = Math.max(1, Math.round(Math.abs(dominantDelta) / WHEEL_NOTCH_DELTA_PIXELS));

        return {
            type: EVENT_TYPES.mouse,
            event: "InvokeScroll",
            value: {
                direction,
                notches,
                deltaX: Math.round(domEvent.deltaX || 0),
                deltaY: Math.round(domEvent.deltaY || 0),
                X: Math.round(domEvent.clientX || 0),
                Y: Math.round(domEvent.clientY || 0)
            }
        };
    }

    /**
     * Builds the descriptor for a committed field change as a SendKeys action.
     *
     * @remarks
     * Compute-only helper. Fires on `change` (field commit) so typing is captured as one
     * SendKeys per field rather than per keystroke. When redaction is enabled and the
     * target is a password (or a field marked sensitive), the text is masked so the
     * secret never leaves the page.
     *
     * @param {object} options Descriptor inputs.
     * @param {Event} options.domEvent The change event.
     * @param {boolean} options.isRedactPasswordsEnabled Whether to mask secret inputs.
     * @returns {object} The descriptor.
     */
    function newSendKeysDescriptor(options) {
        const { domEvent, isRedactPasswordsEnabled } = options;
        const targetElement = domEvent.target;

        // Decide whether this field's text must be masked before it is recorded.
        const isSensitiveField = testSensitiveField(targetElement);
        const isRedactionRequired = isRedactPasswordsEnabled && isSensitiveField;

        // Resolve the value payload: masked secret, toggle state, or raw field text.
        const value = newSendKeysValue({
            targetElement,
            isRedactionRequired
        });

        return {
            type: EVENT_TYPES.keyboard,
            event: "SendKeys",
            value
        };
    }

    /**
     * Builds the value payload for a SendKeys action.
     *
     * @remarks
     * Compute-only helper extracted so the redaction and toggle rules live in one place.
     * The typed text is carried in `text` to match the SendKeys action shape.
     *
     * @param {object} options Value inputs.
     * @param {Element} options.targetElement The element that produced the event.
     * @param {boolean} options.isRedactionRequired Whether the text must be masked.
     * @returns {object} The value payload.
     */
    function newSendKeysValue(options) {
        const { targetElement, isRedactionRequired } = options;

        // A masked secret never exposes length or content beyond a fixed placeholder.
        if (isRedactionRequired) {
            return { text: "***", isRedacted: true };
        }

        // Checkboxes and radios are better described by their checked state.
        const isToggleInput = targetElement
            && targetElement.nodeName === "INPUT"
            && (targetElement.type === "checkbox" || targetElement.type === "radio");

        if (isToggleInput) {
            return { text: targetElement.value || "", checked: Boolean(targetElement.checked) };
        }

        // Otherwise record the current field text, capped to a safe length.
        const rawValue = targetElement && typeof targetElement.value === "string"
            ? targetElement.value
            : "";

        return { text: rawValue.slice(0, 512) };
    }

    /**
     * Builds the descriptor for a form submission as a SubmitForm action.
     *
     * @remarks
     * Compute-only helper. The form's id and name (when present) are carried in the
     * value so a consumer can correlate the submission with a specific form.
     *
     * @param {Event} domEvent The submit event.
     * @returns {object} The descriptor.
     */
    function newSubmitDescriptor(domEvent) {
        // Capture lightweight form identity when available; both default to empty.
        const formElement = domEvent.target;
        const formId = formElement && formElement.id
            ? formElement.id
            : "";
        const formName = formElement && formElement.getAttribute
            ? formElement.getAttribute("name") || ""
            : "";

        return {
            type: EVENT_TYPES.form,
            event: "SubmitForm",
            value: {
                formId,
                formName
            }
        };
    }

    /**
     * Placeholder: tests whether a keydown is a key combination (Ctrl/Alt/Meta + key).
     *
     * @remarks
     * DEFERRED — not implemented yet. Returns false so no combination is detected and the
     * caller falls through to the deferred keydown placeholder.
     *
     * TODO(key-combination): return true when the keydown is a shortcut, i.e. a modifier is
     *   held together with a non-modifier key. To implement:
     *   - const isModifierHeld = domEvent.ctrlKey || domEvent.altKey || domEvent.metaKey;
     *     (Shift alone does NOT count — a bare Shift+letter is normal typing; Shift only
     *     counts alongside Ctrl/Alt/Meta, e.g. Ctrl+Shift+I.)
     *   - const isModifierKey = ["Control", "Alt", "Shift", "Meta"].includes(domEvent.key);
     *     (Ignore keydowns whose key is itself a modifier.)
     *   - return isModifierHeld && !isModifierKey;
     *   Gating note: this is gated by settings.enabledEvents.keyCombination. Because one
     *   `keydown` can be either a plain key (deferred keyDown) or a combination, keep the
     *   resolution here rather than adding a second `keydown` catalog entry (which would
     *   double-bind the content-script listener). When wiring the toggle, pass the
     *   keyCombination flag through so a disabled toggle skips emission.
     *
     * @param {KeyboardEvent} domEvent The keydown event.
     * @returns {boolean} True when the keydown is a key combination; false while deferred.
     */
    function testKeyCombination(domEvent) {
        // TODO(key-combination): detect a modifier + non-modifier key (see block above).
        return false;
    }

    /**
     * Tests whether an element holds a secret that should be masked.
     *
     * @remarks
     * Compute-only helper. Password inputs and fields explicitly marked with
     * data-sensitive are treated as secret regardless of the redaction setting; the
     * caller decides whether to act on the result.
     *
     * @param {Element} targetElement The element that produced a form event.
     * @returns {boolean} True when the element should be treated as sensitive.
     */
    function testSensitiveField(targetElement) {
        // Without an element there is nothing sensitive to protect.
        if (!targetElement || typeof targetElement.getAttribute !== "function") {
            return false;
        }

        // A native password input is the primary sensitive case.
        const isPasswordInput = targetElement.nodeName === "INPUT"
            && targetElement.type === "password";

        // Pages may opt other fields in with an explicit data-sensitive marker.
        const isMarkedSensitive = targetElement.getAttribute("data-sensitive") !== null;

        return isPasswordInput || isMarkedSensitive;
    }

    /**
     * Tests whether an element is a checkbox or radio toggle.
     *
     * @remarks
     * Compute-only helper. Toggles are recorded via their click, so their `change` event is
     * skipped to avoid a redundant SendKeys.
     *
     * @param {Element} targetElement The element that produced a change event.
     * @returns {boolean} True for a checkbox or radio input.
     */
    function testToggleInput(targetElement) {
        // Only INPUT elements of type checkbox or radio are toggles.
        const isToggleInput = Boolean(targetElement)
            && targetElement.nodeName === "INPUT"
            && (targetElement.type === "checkbox" || targetElement.type === "radio");

        return isToggleInput;
    }

    // Expose the catalog API on the shared namespace.
    namespace.catalog = {
        getCatalogEntries,
        getEventPoint,
        resolveEventDescriptor
    };
})(globalThis);
