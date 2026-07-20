/**
 * Recorder contract builders for the G4 Chromium Recorder extension.
 *
 * These builders produce objects whose JSON shape matches the C# UiaPeek/ChromiumPeek
 * response contract exactly, so the C# hub can deserialize them into ChromiumEventModel
 * and re-broadcast them to consumers byte-shaped like a UiaEventModel.
 *
 * Contract reference (camelCase on the wire unless noted):
 *   RecorderEventModel : { chain, event, machineName, offset, timestamp, type, value }
 *   ChainModel         : { locator, path[], point, topWindow, trigger }
 *   RecorderNodeModel  : { automationId, bounds, className, controlTypeId, controlType,
 *                          frameworkId, isTopWindow, isTriggerElement, machine, name,
 *                          patterns[], processId, properties, runtimeId[] }
 *   BoundsRectangle    : { height, width, X, Y }   (X = Left, Y = Top via JsonPropertyName)
 *   RecorderPointModel : { X, Y }                  (X = XPos, Y = YPos via JsonPropertyName)
 *   MachineDataModel   : { name, publicAddress }
 *   PatternDataModel   : { id, name }
 *
 * @remarks
 * The browser has no UI Automation concepts, so UIA-only fields (controlTypeId,
 * frameworkId, patterns, processId, runtimeId) are emitted as empty/zero defaults.
 * This keeps the JSON shape identical while signalling "not applicable" to consumers.
 *
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded
 * by the content scripts, which build the full event before sending it to the worker.
 */
(function initializeRecorderContractModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    /**
     * Creates a bounds rectangle in the contract shape from a DOM client rectangle.
     *
     * @remarks
     * Compute-only. The keys X and Y are intentionally uppercase because the C# model
     * maps them to Left and Top through JsonPropertyName attributes.
     *
     * @param {DOMRect|object} clientRectangle A getBoundingClientRect-like rectangle.
     * @returns {object} The bounds rectangle in contract shape.
     */
    function newBounds(clientRectangle) {
        // Fall back to a zeroed rectangle when no geometry is available (detached node).
        const rectangle = clientRectangle || { height: 0, width: 0, left: 0, top: 0 };

        return {
            height: Math.round(rectangle.height || 0),
            width: Math.round(rectangle.width || 0),
            X: Math.round(rectangle.left || 0),
            Y: Math.round(rectangle.top || 0)
        };
    }

    /**
     * Creates the chain object that wraps a recorded element path.
     *
     * @remarks
     * Compute-only. The path begins with the trigger element and climbs upward to the
     * document root, matching the UiaPeek ChainModel convention.
     *
     * @param {object} options Chain inputs.
     * @param {string} options.locator The absolute locator string for the trigger element.
     * @param {object[]} options.path The ordered nodes from trigger up to the root.
     * @param {object|null} options.point The trigger point, or null when not applicable.
     * @param {object|null} options.topWindow The top-level node in the chain.
     * @param {string} options.trigger The action or event that produced the chain.
     * @returns {object} The chain in contract shape.
     */
    function newChain(options) {
        const { locator, path, point, topWindow, trigger } = options;

        return {
            locator: locator || "",
            path: path || [],
            point: point || null,
            topWindow: topWindow || null,
            trigger: trigger || ""
        };
    }

    /**
     * Creates the machine descriptor for a recorded node.
     *
     * @remarks
     * Compute-only. A browser cannot read the host machine name, so the page hostname
     * is used as a best-available identifier and the page origin as the public address.
     * This deviation from the desktop contract is documented rather than left implicit.
     *
     * @param {object} options Machine inputs.
     * @param {string} options.name The machine/host name to report.
     * @param {string} options.publicAddress The public address to report.
     * @returns {object} The machine descriptor in contract shape.
     */
    function newMachine(options) {
        const machineOptions = options || {};

        return {
            name: machineOptions.name || "",
            publicAddress: machineOptions.publicAddress || ""
        };
    }

    /**
     * Creates a single node in the contract shape.
     *
     * @remarks
     * Compute-only. UIA-only fields default to empty/zero because they have no DOM
     * equivalent. Web-specific detail (CSS selector, tag name, attributes) is carried
     * in `properties`, which the contract leaves open as a free-form map.
     *
     * @param {object} options Node inputs (already mapped from a DOM element).
     * @returns {object} The node in contract shape.
     */
    function newNode(options) {
        const nodeOptions = options || {};

        return {
            automationId: nodeOptions.automationId || "",
            bounds: nodeOptions.bounds || newBounds(null),
            className: nodeOptions.className || "",
            controlTypeId: nodeOptions.controlTypeId || 0,
            controlType: nodeOptions.controlType || "",
            frameworkId: nodeOptions.frameworkId || "Chromium",
            isTopWindow: Boolean(nodeOptions.isTopWindow),
            isTriggerElement: Boolean(nodeOptions.isTriggerElement),
            machine: nodeOptions.machine || newMachine(null),
            name: nodeOptions.name || "",
            patterns: nodeOptions.patterns || [],
            processId: nodeOptions.processId || 0,
            properties: nodeOptions.properties || {},
            runtimeId: nodeOptions.runtimeId || []
        };
    }

    /**
     * Creates an element-relative pointer offset in the shared recorder contract shape.
     *
     * @remarks
     * Compute-only. Chromium currently publishes the zero default so every recorder exposes
     * the shared field; DOM-relative calculation can populate the same model in a later change.
     *
     * @param {object} options Offset inputs.
     * @param {number} options.xPosition Horizontal pixels from the target rectangle's left edge.
     * @param {number} options.yPosition Vertical pixels from the target rectangle's top edge.
     * @returns {object} The relative offset in the shared contract shape.
     */
    function newOffset(options) {
        const offsetOptions = options || {};

        return {
            x: Math.round(offsetOptions.xPosition || 0),
            y: Math.round(offsetOptions.yPosition || 0)
        };
    }

    /**
     * Creates a screen-point object in the contract shape.
     *
     * @remarks
     * Compute-only. Keys X and Y are uppercase because the C# model maps them to XPos
     * and YPos through JsonPropertyName attributes. Browser coordinates are viewport
     * relative (clientX/clientY) since true screen coordinates are not exposed to pages.
     *
     * @param {object} options Point inputs.
     * @param {number} options.xPosition The horizontal coordinate in pixels.
     * @param {number} options.yPosition The vertical coordinate in pixels.
     * @returns {object} The point in contract shape.
     */
    function newPoint(options) {
        const pointOptions = options || {};

        return {
            X: Math.round(pointOptions.xPosition || 0),
            Y: Math.round(pointOptions.yPosition || 0)
        };
    }

    /**
     * Creates the top-level recording event in the contract shape.
     *
     * @remarks
     * Compute-only. This is the object the content script sends to the background
     * worker, which forwards it verbatim to the hub via SendRecordingEvent.
     *
     * @param {object} options Event inputs.
     * @param {object} options.chain The chain produced by newChain.
     * @param {string} options.event The specific event name (for example "Click").
     * @param {string} options.machineName The reporting machine/host name.
     * @param {object} [options.offset] The element-relative pointer offset, or zero when omitted.
     * @param {number} options.timestamp The Unix epoch milliseconds when it occurred.
     * @param {string} options.type The event category (for example "Mouse").
     * @param {*} options.value The event payload value.
     * @returns {object} The recording event in contract shape.
     */
    function newRecordingEvent(options) {
        const { chain, event, machineName, offset, timestamp, type, value } = options;

        // The C# contract types Timestamp as a 64-bit integer, but some sources (for
        // example webNavigation.timeStamp) report a fractional double. Round to a whole
        // number so the hub can bind the value to a long; fall back to the current time
        // when no usable timestamp was provided.
        const resolvedTimestamp = Number.isFinite(timestamp)
            ? Math.round(timestamp)
            : Date.now();

        return {
            chain: chain || null,
            event: event || "",
            machineName: machineName || "",
            offset: offset || newOffset(null),
            timestamp: resolvedTimestamp,
            type: type || "",
            value: value === undefined ? null : value
        };
    }

    // Expose the contract builders on the shared namespace.
    namespace.contract = {
        newBounds,
        newChain,
        newMachine,
        newNode,
        newOffset,
        newPoint,
        newRecordingEvent
    };
})(globalThis);
