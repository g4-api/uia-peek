/**
 * Settings store for the G4 Chromium Recorder extension.
 *
 * Wraps chrome.storage.sync so the rest of the extension can read and write user
 * settings without repeating merge-with-defaults logic. Stored values are always
 * layered on top of the defaults from the constants module, so a partially stored
 * settings object never produces an undefined field.
 *
 * @remarks
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js for why
 * a namespace is used instead of ES imports). This module is loaded by the background
 * service worker, the popup page, and the options page.
 */
(function initializeSettingsStoreModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // Pull the storage key and defaults from the constants module. constants.js is
    // always loaded before this file in every context that uses settings.
    const { DEFAULT_SETTINGS, SETTINGS_STORAGE_KEY } = namespace.constants;

    /**
     * Reads the persisted settings and merges them on top of the defaults.
     *
     * @remarks
     * Compute-only with respect to extension state: it only reads storage and returns
     * a new object. The merge is shallow for top-level fields and one level deep for
     * the `enabledEvents` map so newly added default event toggles still appear even
     * when an older settings object is stored.
     *
     * @returns {Promise<object>} The effective settings object.
     */
    async function getSettings() {
        // Read the single stored settings object; an unset key resolves to undefined.
        const storageResult = await chrome.storage.sync.get(SETTINGS_STORAGE_KEY);
        const storedSettings = storageResult?.[SETTINGS_STORAGE_KEY] || {};

        // Merge stored top-level fields over the defaults so missing keys fall back.
        const mergedSettings = { ...DEFAULT_SETTINGS, ...storedSettings };

        // Merge the nested event-toggle map separately so a stored partial map does
        // not drop event toggles that were added to the defaults in a later version.
        mergedSettings.enabledEvents = {
            ...DEFAULT_SETTINGS.enabledEvents,
            ...(storedSettings.enabledEvents || {})
        };

        return mergedSettings;
    }

    /**
     * Persists a partial settings patch on top of the currently stored settings.
     *
     * @remarks
     * Owns side effects: it writes to chrome.storage.sync. The function reads the
     * current effective settings first so callers can save a single changed field
     * without having to supply the whole object.
     *
     * @param {object} settingsPatch A partial settings object to merge and persist.
     * @returns {Promise<object>} The full settings object that was persisted.
     */
    async function saveSettings(settingsPatch) {
        // Start from the current effective settings so unspecified fields are kept.
        const currentSettings = await getSettings();

        // Layer the incoming patch over the current settings as one update stage.
        const nextSettings = { ...currentSettings, ...settingsPatch };

        // Merge the nested event-toggle map explicitly to avoid replacing the whole
        // map when the caller only changed one toggle.
        nextSettings.enabledEvents = {
            ...currentSettings.enabledEvents,
            ...(settingsPatch.enabledEvents || {})
        };

        // Write the merged settings back under the single storage key.
        await chrome.storage.sync.set({ [SETTINGS_STORAGE_KEY]: nextSettings });

        return nextSettings;
    }

    /**
     * Registers a listener that fires whenever the stored settings change.
     *
     * @remarks
     * Owns an event subscription. The returned function removes the listener so a
     * page can clean up on unload. The callback receives the freshly merged settings
     * rather than the raw storage change record to keep callers simple.
     *
     * @param {(settings: object) => void} onSettingsChanged Callback invoked with the new settings.
     * @returns {() => void} A function that removes the registered listener.
     */
    function watchSettings(onSettingsChanged) {
        // Translate the low-level storage change record into a merged settings object
        // before notifying the caller, so listeners never see partial storage state.
        const onStorageChanged = async (changes, areaName) => {
            // Ignore changes from other storage areas or unrelated keys.
            const isSyncArea = areaName === "sync";
            const isSettingsKeyChanged = Boolean(changes[SETTINGS_STORAGE_KEY]);
            const isRelevantChange = isSyncArea && isSettingsKeyChanged;

            if (!isRelevantChange) {
                return;
            }

            // Re-read the effective settings so the callback always gets full defaults.
            const settings = await getSettings();

            onSettingsChanged(settings);
        };

        // Subscribe to storage changes for the lifetime requested by the caller.
        chrome.storage.onChanged.addListener(onStorageChanged);

        // Hand back a disposer so callers can detach the listener on cleanup.
        return function removeSettingsWatcher() {
            chrome.storage.onChanged.removeListener(onStorageChanged);
        };
    }

    // Expose the settings API on the shared namespace.
    namespace.settings = {
        getSettings,
        saveSettings,
        watchSettings
    };
})(globalThis);
