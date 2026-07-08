/**
 * Popup script for the G4 Chromium Recorder extension.
 *
 * Renders the live recorder status: connection state, hub URL, current session, event
 * count, and the scrollable stack of captured events for the active session. It reads
 * the status from the background worker on open and re-renders whenever the worker
 * broadcasts a status change. The action buttons reconnect, clear the stack, and open
 * the settings page.
 *
 * @remarks
 * Loaded as a classic script after lib/constants.js, so the shared g4Recorder namespace
 * is already available. The status snapshot is the single source of truth; this script
 * never holds recorder state of its own beyond cached element references.
 */
(function initializePopupModule(globalScope) {
    // Read the shared channel names used to talk to the background worker.
    const { MESSAGE_CHANNELS } = globalScope.g4Recorder.constants;

    // Cache the page's element references once. A popup page is short-lived, so holding
    // these references for its lifetime is simpler than re-querying on every render.
    const elements = {
        statusDot: document.getElementById("connection-status-dot"),
        statusText: document.getElementById("connection-status-text"),
        hubUrlValue: document.getElementById("hub-url-value"),
        sessionIdValue: document.getElementById("session-id-value"),
        sessionStartedValue: document.getElementById("session-started-value"),
        eventCountValue: document.getElementById("event-count-value"),
        eventsList: document.getElementById("captured-events-list"),
        eventsEmpty: document.getElementById("captured-events-empty"),
        reconnectButton: document.getElementById("reconnect-button"),
        clearStackButton: document.getElementById("clear-stack-button"),
        openSettingsButton: document.getElementById("open-settings-button")
    };

    /**
     * Maps a connection state to a readable label and dot modifier class.
     *
     * @remarks
     * Compute-only helper. Keeping the mapping in one place means the badge text and the
     * dot colour can never disagree.
     *
     * @param {string} connectionState The connection state from the status snapshot.
     * @returns {object} An object with `label` text and `modifier` class suffix.
     */
    function getConnectionPresentation(connectionState) {
        // Map each known state to its user-facing label.
        const labelsByState = {
            connected: "Connected",
            connecting: "Connecting",
            reconnecting: "Reconnecting",
            disconnected: "Disconnected"
        };

        return {
            label: labelsByState[connectionState] || "Disconnected",
            modifier: connectionState || "disconnected"
        };
    }

    /**
     * Formats a Unix-millisecond timestamp as a local clock time.
     *
     * @remarks
     * Compute-only helper. Returns an em dash for a missing timestamp so the detail row
     * always shows something.
     *
     * @param {number|null} milliseconds The timestamp to format.
     * @returns {string} The formatted time, or an em dash.
     */
    function getFormattedTime(milliseconds) {
        // Show a placeholder when there is no time to format.
        if (!milliseconds) {
            return "—";
        }

        return new Date(milliseconds).toLocaleTimeString();
    }

    /**
     * Builds a list row element for a single recording event.
     *
     * @remarks
     * Compute-only with respect to page state: it returns a detached element the caller
     * appends. Text is set with textContent so page-derived strings cannot inject markup.
     *
     * @param {object} recordingEvent The event in contract shape.
     * @returns {HTMLElement} The constructed row element.
     */
    function newEventRowElement(recordingEvent) {
        // Create the row container with its test handle.
        const rowElement = document.createElement("article");
        rowElement.className = "recorder-popup__event-row";
        rowElement.setAttribute("data-test-id", "captured-event-row");

        // The type cell shows the event category (Mouse, Keyboard, Form, Navigation).
        const typeElement = document.createElement("span");
        typeElement.className = "recorder-popup__event-type";
        typeElement.textContent = recordingEvent.type || "";

        // The name cell shows the specific event name (Click, Key Down, and so on).
        const nameElement = document.createElement("span");
        nameElement.className = "recorder-popup__event-name";
        nameElement.textContent = recordingEvent.event || "";

        // The time cell shows when the event was captured.
        const timeElement = document.createElement("span");
        timeElement.className = "recorder-popup__event-time";
        timeElement.textContent = getFormattedTime(recordingEvent.timestamp);

        // The locator cell spans the row and shows the chain's locator string.
        const locatorText = recordingEvent.chain
            ? recordingEvent.chain.locator
            : "";
        const locatorElement = document.createElement("span");
        locatorElement.className = "recorder-popup__event-locator";
        locatorElement.textContent = locatorText;

        // Assemble the row in reading order.
        rowElement.append(typeElement, nameElement, timeElement, locatorElement);

        return rowElement;
    }

    /**
     * Clears the captured-event stack in the background worker.
     *
     * @remarks
     * Owns a messaging side effect. The worker replies with a status broadcast, so this
     * handler does not update the UI directly.
     *
     * @param {Event} event The click event.
     * @returns {void}
     */
    function onClearStackClick(event) {
        event.preventDefault();

        chrome.runtime.sendMessage({ channel: MESSAGE_CHANNELS.clearStack });
    }

    /**
     * Opens the extension options page.
     *
     * @remarks
     * Owns navigation away from the popup. Uses the dedicated API so the page opens in
     * the browser's standard options surface.
     *
     * @param {Event} event The click event.
     * @returns {void}
     */
    function onOpenSettingsClick(event) {
        event.preventDefault();

        chrome.runtime.openOptionsPage();
    }

    /**
     * Asks the background worker to reconnect to the hub.
     *
     * @remarks
     * Owns a messaging side effect. The resulting connect drives a status broadcast that
     * refreshes the UI.
     *
     * @param {Event} event The click event.
     * @returns {void}
     */
    function onReconnectClick(event) {
        event.preventDefault();

        chrome.runtime.sendMessage({ channel: MESSAGE_CHANNELS.reconnect });
    }

    /**
     * Handles a status broadcast from the background worker.
     *
     * @remarks
     * Owns no state; it forwards the snapshot to the renderers. Ignores unrelated message
     * channels so other extension messaging cannot disturb the popup.
     *
     * @param {object} message The incoming runtime message.
     * @returns {void}
     */
    function onStatusMessage(message) {
        // Only act on status broadcasts addressed to the status channel.
        if (!message || message.channel !== MESSAGE_CHANNELS.statusChanged) {
            return;
        }

        showStatus(message.status);
    }

    /**
     * Requests the current status snapshot from the background worker.
     *
     * @remarks
     * Owns a messaging side effect. Used once on open so the popup shows current state
     * even when no broadcast has arrived yet.
     *
     * @returns {void}
     */
    function requestStatus() {
        // Ask for the snapshot and render it; ignore failures from a sleeping worker.
        chrome.runtime.sendMessage({ channel: MESSAGE_CHANNELS.requestStatus })
            .then((status) => {
                if (status) {
                    showStatus(status);
                }
            })
            .catch(() => {
                // The worker is briefly unavailable; the next broadcast will refresh us.
            });
    }

    /**
     * Renders the captured-event stack, newest first.
     *
     * @remarks
     * Owns DOM updates for the events region. Shows the empty placeholder when there are
     * no events so the user always sees clear feedback.
     *
     * @param {object[]} events The captured events, oldest first.
     * @returns {void}
     */
    function showEvents(events) {
        // Remove previously rendered rows but keep the empty placeholder element.
        const renderedRows = elements.eventsList.querySelectorAll(".recorder-popup__event-row");
        renderedRows.forEach((rowElement) => rowElement.remove());

        // Toggle the empty placeholder based on whether any events exist.
        const hasEvents = Array.isArray(events) && events.length > 0;
        elements.eventsEmpty.hidden = hasEvents;

        if (!hasEvents) {
            return;
        }

        // Append rows newest first so the most recent interaction is at the top.
        const orderedEvents = events.slice().reverse();
        orderedEvents.forEach((recordingEvent) => {
            elements.eventsList.append(newEventRowElement(recordingEvent));
        });
    }

    /**
     * Renders the whole status snapshot into the popup.
     *
     * @remarks
     * Owns DOM updates for the header and detail rows, then delegates the event list to
     * showEvents. This is the single entry point both the open-time request and the
     * broadcast handler use.
     *
     * @param {object} status The status snapshot from the worker.
     * @returns {void}
     */
    function showStatus(status) {
        // Update the connection badge text and dot colour from the connection state.
        const presentation = getConnectionPresentation(status.connectionState);
        elements.statusText.textContent = presentation.label;
        elements.statusDot.className = `recorder-popup__status-dot recorder-popup__status-dot--${presentation.modifier}`;

        // Update the session detail rows.
        elements.hubUrlValue.textContent = status.hubUrl || "—";
        elements.sessionIdValue.textContent = status.sessionId || "—";
        elements.sessionStartedValue.textContent = getFormattedTime(status.sessionStartedAtMilliseconds);
        elements.eventCountValue.textContent = String(status.eventCount || 0);

        // Render the captured events region.
        showEvents(status.events);
    }

    // Wire the action buttons to their handlers.
    elements.reconnectButton.addEventListener("click", onReconnectClick);
    elements.clearStackButton.addEventListener("click", onClearStackClick);
    elements.openSettingsButton.addEventListener("click", onOpenSettingsClick);

    // Refresh the popup whenever the worker broadcasts a status change.
    chrome.runtime.onMessage.addListener(onStatusMessage);

    // Request the current status once so the popup is populated immediately on open.
    requestStatus();
})(globalThis);
