/**
 * SignalR connection wrapper for the G4 Chromium Recorder extension.
 *
 * Thin adapter over the official Microsoft SignalR client (vendored at js/signalr.min.js
 * and loaded into the service worker before this file). It builds a connection that
 * speaks the same wire protocol as the C# ChromiumPeek hub, exposes a small surface for
 * the background worker (start, stop, send, query state), and forwards lifecycle changes
 * through callbacks so the worker can manage recording sessions.
 *
 * @remarks
 * The connection uses skipNegotiation with the WebSockets transport. This avoids the
 * negotiate HTTP call and the long-polling fallback, both of which rely on browser APIs
 * (XMLHttpRequest, document) that are not available inside a service worker. With this
 * configuration the official client runs correctly in the worker context and fully
 * offline.
 *
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded
 * only by the background service worker; content scripts never hold a connection.
 */
(function initializeSignalrConnectionModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // Pull the hub method names and server-to-client message names so invocations and inbound
    // handlers stay in sync with the server contract.
    const { HUB_METHOD_NAMES, SERVER_CLOSE_BROWSER_NAME } = namespace.constants;

    // Human-readable connection states surfaced to the rest of the extension. These are
    // independent of the SignalR client's internal enum so the UI has stable labels.
    const CONNECTION_STATES = Object.freeze({
        connected: "connected",
        connecting: "connecting",
        disconnected: "disconnected",
        reconnecting: "reconnecting"
    });

    /**
     * Creates a recorder connection bound to a hub URL.
     *
     * @remarks
     * Owns the SignalR client instance and the current connection-state value. The
     * returned object is the only handle the worker keeps; calling stop disposes the
     * underlying client. Lifecycle callbacks are invoked on connect, reconnect, and
     * close so the worker can start or clear a recording session in step with the socket.
     *
     * @param {object} options Connection inputs.
     * @param {string} options.hubUrl The hub URL to connect to.
     * @param {number[]} options.reconnectDelaysMilliseconds Backoff delays for automatic reconnect.
     * @param {() => void} options.onConnected Called when the socket becomes connected.
     * @param {() => void} options.onReconnecting Called when the client starts reconnecting.
     * @param {() => void} options.onDisconnected Called when the socket closes for good.
     * @param {() => void} [options.onCloseBrowser] Called when the hub asks the extension to close the browser.
     * @returns {object} The connection handle.
     */
    function newRecorderConnection(options) {
        const {
            hubUrl,
            reconnectDelaysMilliseconds,
            onConnected,
            onReconnecting,
            onDisconnected,
            onCloseBrowser
        } = options;

        // The official client is exposed as a global by the vendored UMD build. Fail
        // loudly if it is missing so a packaging mistake is obvious during testing.
        const signalRClient = globalScope.signalR;

        if (!signalRClient) {
            throw new Error("SignalR client is not loaded; js/signalr.min.js must be imported first.");
        }

        // Track the current state behind the handle so callers can query it cheaply
        // without reaching into the SignalR client's internals.
        let connectionState = CONNECTION_STATES.disconnected;

        // Build the connection with skipNegotiation + WebSockets so it works in the
        // service worker and offline. Automatic reconnect uses the supplied backoff.
        const hubConnection = new signalRClient.HubConnectionBuilder()
            .withUrl(hubUrl, {
                skipNegotiation: true,
                transport: signalRClient.HttpTransportType.WebSockets
            })
            .withAutomaticReconnect(reconnectDelaysMilliseconds || [0, 2000, 5000, 10000])
            .build();

        // Mark the connection as reconnecting and notify the worker so it can reflect
        // the transient state and prepare to clear the session on the next connect.
        hubConnection.onreconnecting(() => {
            connectionState = CONNECTION_STATES.reconnecting;

            onReconnecting();
        });

        // A successful reconnect is treated like a fresh connect: the worker starts a
        // new session so the captured-event stack resets per the agreed behaviour.
        hubConnection.onreconnected(() => {
            connectionState = CONNECTION_STATES.connected;

            onConnected();
        });

        // A permanent close (reconnect exhausted or explicit stop) returns the handle
        // to the disconnected state and lets the worker tear down the session.
        hubConnection.onclose(() => {
            connectionState = CONNECTION_STATES.disconnected;

            onDisconnected();
        });

        // Register the server-initiated close handler so the hub can ask the extension to close
        // the browser during a graceful stop. Registered once here; the official client keeps
        // "on" handlers across automatic reconnects, so it survives transient drops.
        if (onCloseBrowser) {
            hubConnection.on(SERVER_CLOSE_BROWSER_NAME, onCloseBrowser);
        }

        /**
         * Returns the current human-readable connection state.
         *
         * @returns {string} One of the CONNECTION_STATES values.
         */
        function getState() {
            return connectionState;
        }

        /**
         * Sends a recording event to the hub for re-broadcast to consumers.
         *
         * @remarks
         * Owns a network side effect. Uses invoke rather than send so the promise rejects
         * when the server method is missing or throws (for example a stale hub build
         * without SendRecordingEvent, or a payload the hub cannot deserialize). With send
         * those failures are swallowed and the event silently never reaches the hub, so
         * invoke is used to make delivery verifiable and surface errors to the caller.
         *
         * @param {object} recordingEvent The event in contract shape.
         * @returns {Promise<void>} Resolves once the server has processed the invocation.
         */
        function sendRecordingEvent(recordingEvent) {
            // Drop events while not connected so a queued backlog cannot flush stale
            // interactions into a later session.
            const isConnected = connectionState === CONNECTION_STATES.connected;

            if (!isConnected) {
                return Promise.resolve();
            }

            return hubConnection.invoke(HUB_METHOD_NAMES.sendRecordingEvent, recordingEvent);
        }

        /**
         * Opens the connection to the hub.
         *
         * @remarks
         * Owns the start side effect and updates the tracked state. On success the
         * onConnected callback fires so the worker can begin a recording session.
         *
         * @returns {Promise<void>} Resolves once connected, rejects on failure.
         */
        async function start() {
            // Reflect the in-progress state immediately so the UI can show "connecting".
            connectionState = CONNECTION_STATES.connecting;

            // Attempt the connection; on failure restore the disconnected state so a
            // later retry starts from a clean baseline.
            try {
                await hubConnection.start();

                connectionState = CONNECTION_STATES.connected;

                onConnected();
            } catch (error) {
                connectionState = CONNECTION_STATES.disconnected;

                throw error;
            }
        }

        /**
         * Closes the connection to the hub.
         *
         * @remarks
         * Owns the stop side effect. The onclose handler set above will fire and drive
         * the disconnected callback, so the worker's session teardown runs in one place.
         *
         * @returns {Promise<void>} Resolves once the connection is stopped.
         */
        async function stop() {
            await hubConnection.stop();
        }

        return {
            getState,
            sendRecordingEvent,
            start,
            stop
        };
    }

    // Expose the connection API and the state labels on the shared namespace.
    namespace.connection = {
        CONNECTION_STATES,
        newRecorderConnection
    };
})(globalThis);
