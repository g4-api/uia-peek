/**
 * Options script for the G4 Chromium Recorder extension.
 *
 * Loads the saved settings into the form, persists changes through the settings store,
 * and asks the background worker to apply them by reconnecting. It also supports
 * resetting every field back to the documented defaults.
 *
 * @remarks
 * Loaded as a classic script after lib/constants.js, lib/settings-store.js, and
 * components/form-controls/form-controls.js, so the shared g4Recorder namespace (constants, settings API, and
 * themed form controls) is already available. Booleans are rendered as on/off switches
 * and the number field uses the themed stepper control.
 */
(function initializeOptionsModule(globalScope) {
    // Read the shared constants, settings API, and form controls loaded before this script.
    const { constants, settings, formControls } = globalScope.g4Recorder;
    const { DEFAULT_SETTINGS, MESSAGE_CHANNELS } = constants;

    // The event-toggle keys, in the same order as the defaults. Each maps to a switch
    // whose id is `event-toggle-<key>`, so the form and the settings model stay aligned.
    const EVENT_TOGGLE_KEYS = Object.keys(DEFAULT_SETTINGS.enabledEvents);

    // How long the saved/reset confirmation stays visible before clearing.
    const STATUS_MESSAGE_VISIBLE_MILLISECONDS = 2500;

    // Cache the form's element references once for the lifetime of the options page.
    const elements = {
        form: document.getElementById("recorder-settings-form"),
        hubUrlInput: document.getElementById("hub-url-input"),
        autoConnectSwitch: document.getElementById("auto-connect-switch"),
        reconnectDelaysInput: document.getElementById("reconnect-delays-input"),
        redactPasswordsSwitch: document.getElementById("redact-passwords-switch"),
        maxStackSizeField: document.getElementById("max-stack-size-field"),
        maxStackSizeInput: document.getElementById("max-stack-size-input"),
        hoverDwellField: document.getElementById("hover-dwell-field"),
        hoverDwellInput: document.getElementById("hover-dwell-input"),
        resetDefaultsButton: document.getElementById("reset-defaults-button"),
        statusMessage: document.getElementById("settings-status-message")
    };

    // Enhanced control instances (switches + number field), populated on init. The page
    // owns these for its lifetime so the render/read helpers can drive them by name.
    let controls = null;

    /**
     * Reads the current form values into a settings object.
     *
     * @remarks
     * Compute-only. Booleans are read from the switch controls; numeric and list fields
     * are normalised so the persisted object always holds clean values.
     *
     * @returns {object} The settings object built from the form.
     */
    function getFormSettings() {
        // Collect the per-event toggles from their switch controls.
        const enabledEvents = {};

        EVENT_TOGGLE_KEYS.forEach((toggleKey) => {
            const eventSwitch = controls.eventSwitches[toggleKey];
            enabledEvents[toggleKey] = Boolean(eventSwitch && eventSwitch.get());
        });

        // Normalise the maximum stack size to a positive integer, falling back to the
        // default when the field is blank or invalid.
        const parsedStackSize = parseInt(elements.maxStackSizeInput.value, 10);
        const isStackSizeValid = Number.isFinite(parsedStackSize) && parsedStackSize > 0;
        const maximumStackSize = isStackSizeValid
            ? parsedStackSize
            : DEFAULT_SETTINGS.maximumStackSize;

        // Normalise the hover dwell: the field is whole seconds, the setting is milliseconds.
        // Fall back to the default when the field is blank or invalid.
        const parsedDwellSeconds = parseInt(elements.hoverDwellInput.value, 10);
        const isDwellValid = Number.isFinite(parsedDwellSeconds) && parsedDwellSeconds > 0;
        const hoverDwellMilliseconds = isDwellValid
            ? parsedDwellSeconds * 1000
            : DEFAULT_SETTINGS.hoverDwellMilliseconds;

        return {
            hubUrl: elements.hubUrlInput.value.trim() || DEFAULT_SETTINGS.hubUrl,
            isAutoConnectEnabled: controls.autoConnect.get(),
            isRedactPasswordsEnabled: controls.redactPasswords.get(),
            maximumStackSize,
            hoverDwellMilliseconds,
            reconnectDelaysMilliseconds: getReconnectDelays(elements.reconnectDelaysInput.value),
            enabledEvents
        };
    }

    /**
     * Parses a comma-separated list of millisecond delays into a number array.
     *
     * @remarks
     * Compute-only helper. Invalid or negative entries are dropped; an empty result
     * falls back to the default backoff so automatic reconnect always has delays.
     *
     * @param {string} delaysText The raw comma-separated text.
     * @returns {number[]} The parsed, validated delays.
     */
    function getReconnectDelays(delaysText) {
        // Keep only the entries that parse to a non-negative finite number.
        const testValidDelay = (value) => {
            const isValidDelay = Number.isFinite(value) && value >= 0;

            return isValidDelay;
        };

        // Split on commas and convert each piece to a number, dropping invalid entries.
        const parsedDelays = (delaysText || "")
            .split(",")
            .map((piece) => Number(piece.trim()))
            .filter(testValidDelay);

        // Fall back to the documented defaults when nothing valid was entered.
        if (parsedDelays.length === 0) {
            return DEFAULT_SETTINGS.reconnectDelaysMilliseconds.slice();
        }

        return parsedDelays;
    }

    /**
     * Enhances the themed controls (switches and number field) and returns their handles.
     *
     * @remarks
     * Owns creation of the control instances. The returned handles let the render and read
     * helpers drive each boolean switch and the number field by name.
     *
     * @returns {object} The control handles ({ autoConnect, redactPasswords, numberField, eventSwitches }).
     */
    function newControls() {
        // Enhance each event-toggle switch, keyed by its settings key.
        const eventSwitches = {};

        EVENT_TOGGLE_KEYS.forEach((toggleKey) => {
            const switchButton = document.getElementById(`event-toggle-${toggleKey}`);

            if (switchButton) {
                eventSwitches[toggleKey] = formControls.newToggleSwitch(switchButton);
            }
        });

        return {
            autoConnect: formControls.newToggleSwitch(elements.autoConnectSwitch),
            redactPasswords: formControls.newToggleSwitch(elements.redactPasswordsSwitch),
            numberField: formControls.newNumberField(elements.maxStackSizeField),
            hoverDwellField: formControls.newNumberField(elements.hoverDwellField),
            eventSwitches
        };
    }

    /**
     * Resets every field to the documented defaults and persists them.
     *
     * @remarks
     * Owns form, storage, and worker side effects. Reuses the same persist path as a
     * normal save so a reset behaves exactly like saving the default values.
     *
     * @param {Event} event The click event.
     * @returns {Promise<void>} Resolves once defaults are shown and persisted.
     */
    async function onResetDefaultsClick(event) {
        event.preventDefault();

        // Show the defaults in the form, then persist them through the shared save path.
        showSettings(DEFAULT_SETTINGS);

        await saveAndApply(DEFAULT_SETTINGS);

        showStatusMessage("Settings reset to defaults.");
    }

    /**
     * Handles the form submission by saving and applying the entered settings.
     *
     * @remarks
     * Owns form, storage, and worker side effects. Prevents the default submission so
     * the page does not reload.
     *
     * @param {Event} event The submit event.
     * @returns {Promise<void>} Resolves once settings are persisted and applied.
     */
    async function onSubmitSettings(event) {
        event.preventDefault();

        // Build the settings from the form and persist them through the shared path.
        const formSettings = getFormSettings();

        await saveAndApply(formSettings);

        showStatusMessage("Settings saved.");
    }

    /**
     * Persists a settings object and asks the worker to apply it.
     *
     * @remarks
     * Owns storage and messaging side effects, extracted so save and reset share one
     * implementation. The worker reconnects so a changed hub URL takes effect at once.
     *
     * @param {object} settingsToSave The settings object to persist.
     * @returns {Promise<void>} Resolves once persisted and the worker is notified.
     */
    async function saveAndApply(settingsToSave) {
        // Persist the settings so every context reads the new values.
        await settings.saveSettings(settingsToSave);

        // Notify the worker so it rebuilds the connection with the new settings.
        chrome.runtime.sendMessage({ channel: MESSAGE_CHANNELS.settingsChanged });
    }

    /**
     * Populates the form controls from a settings object.
     *
     * @remarks
     * Owns DOM updates for the form. Used both on initial load and on reset so the form
     * always mirrors the supplied settings exactly. Switch controls are set via their API
     * (which does not fire the change callback), keeping the render side-effect free.
     *
     * @param {object} settingsToShow The settings to render into the form.
     * @returns {void}
     */
    function showSettings(settingsToShow) {
        // Fill the scalar fields.
        elements.hubUrlInput.value = settingsToShow.hubUrl;
        elements.maxStackSizeInput.value = String(settingsToShow.maximumStackSize);
        elements.hoverDwellInput.value = String(Math.round(settingsToShow.hoverDwellMilliseconds / 1000));
        elements.reconnectDelaysInput.value = settingsToShow.reconnectDelaysMilliseconds.join(", ");

        // Sync the boolean switches.
        controls.autoConnect.set(settingsToShow.isAutoConnectEnabled);
        controls.redactPasswords.set(settingsToShow.isRedactPasswordsEnabled);

        // Sync each per-event switch from the enabledEvents map.
        EVENT_TOGGLE_KEYS.forEach((toggleKey) => {
            const eventSwitch = controls.eventSwitches[toggleKey];

            if (eventSwitch) {
                eventSwitch.set(Boolean(settingsToShow.enabledEvents[toggleKey]));
            }
        });
    }

    /**
     * Shows a transient confirmation message and clears it after a delay.
     *
     * @remarks
     * Owns the status element and a timer. A fresh call replaces the previous message so
     * rapid saves do not stack confirmations.
     *
     * @param {string} messageText The message to display.
     * @returns {void}
     */
    function showStatusMessage(messageText) {
        // Display the message immediately.
        elements.statusMessage.textContent = messageText;

        // Clear it after the visible window so the page returns to a clean state.
        globalScope.setTimeout(() => {
            elements.statusMessage.textContent = "";
        }, STATUS_MESSAGE_VISIBLE_MILLISECONDS);
    }

    /**
     * Initializes the options page: enhance controls, load settings, and wire actions.
     *
     * @remarks
     * Owns startup ordering. Controls are enhanced first so the settings render can drive
     * them; listeners are wired after the first render so the form is fully populated
     * before the user can interact with it.
     *
     * @returns {Promise<void>} Resolves once the form is ready.
     */
    async function initializeOptions() {
        // Enhance the themed switches and number field before rendering into them.
        controls = newControls();

        // Load the effective settings and render them into the controls.
        const loadedSettings = await settings.getSettings();

        showSettings(loadedSettings);

        // Wire the save and reset actions.
        elements.form.addEventListener("submit", onSubmitSettings);
        elements.resetDefaultsButton.addEventListener("click", onResetDefaultsClick);
    }

    // Start the options page.
    initializeOptions();
})(globalThis);
