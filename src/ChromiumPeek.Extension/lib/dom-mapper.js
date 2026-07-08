/**
 * DOM-to-contract mapping for the G4 Chromium Recorder extension.
 *
 * Converts a DOM element into a recorder node, and a trigger element into a full chain
 * (the element plus every ancestor up to the document root). Field mapping follows the
 * approved scheme:
 *   controlType  <- ARIA role or tag name
 *   automationId <- id or data-test-id
 *   name         <- accessible name (aria-label, labelledby, alt, title, or text)
 *   className    <- class attribute
 *   bounds       <- getBoundingClientRect
 * UIA-only fields are left at their contract defaults by the contract builder.
 *
 * @remarks
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded
 * as a content script in every frame. Depends on the contract and locator modules,
 * which are listed before it in the manifest content-script order.
 */
(function initializeDomMapperModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // Pull the sibling modules this mapper builds on. Both are loaded earlier in the
    // content-script list, so they are guaranteed to be present here.
    const contract = namespace.contract;
    const locator = namespace.locator;

    // Maximum number of input characters retained in node properties. Long values are
    // truncated so a single field cannot bloat the payload sent over the socket.
    const MAXIMUM_VALUE_PROPERTY_LENGTH = 256;

    // Matches any run of whitespace, used to collapse multi-line text into one line.
    const WHITESPACE_PATTERN = /\s+/g;

    /**
     * Builds a chain (trigger node plus ancestors) for a recorded interaction.
     *
     * @remarks
     * Compute-only. The path starts at the trigger element and climbs to the document
     * root, mirroring the UiaPeek ChainModel. The first node is flagged as the trigger
     * and the last as the top window.
     *
     * @param {object} options Chain inputs.
     * @param {Element} options.element The trigger element the interaction targeted.
     * @param {string} options.trigger The event name that produced this chain.
     * @param {object|null} options.point The trigger point, or null when not applicable.
     * @returns {object} The chain in contract shape.
     */
    function newChainFromElement(options) {
        const { element, trigger, point } = options;

        // Collect the trigger element and each ancestor element up to the root so the
        // path can be built from the bottom up, matching the desktop recorder.
        const elementChain = getElementChain(element);

        // Resolve the machine descriptor once and reuse it for every node in the chain,
        // since all nodes in one document share the same host context.
        const machine = newMachineForDocument(element.ownerDocument);

        // Map each element to a node, flagging the trigger (first) and root (last).
        const lastIndex = elementChain.length - 1;
        const path = elementChain.map((chainElement, index) => newNodeFromElement({
            element: chainElement,
            isTriggerElement: index === 0,
            isTopWindow: index === lastIndex,
            machine
        }));

        // The top window node is the final node in the bottom-up path, when present.
        const topWindow = path.length > 0
            ? path[path.length - 1]
            : null;

        return contract.newChain({
            locator: locator.getAbsoluteXpath(element),
            path,
            point,
            topWindow,
            trigger
        });
    }

    /**
     * Maps a single DOM element to a recorder node.
     *
     * @remarks
     * Compute-only. Web-specific detail that has no contract field (CSS selector, tag
     * name, key attributes) is placed in `properties` so consumers can still use it.
     *
     * @param {object} options Node inputs.
     * @param {Element} options.element The element to map.
     * @param {boolean} options.isTriggerElement Whether this element is the trigger.
     * @param {boolean} options.isTopWindow Whether this element is the top window.
     * @param {object} options.machine The shared machine descriptor for the document.
     * @returns {object} The node in contract shape.
     */
    function newNodeFromElement(options) {
        const { element, isTriggerElement, isTopWindow, machine } = options;

        // Resolve the friendly control type and accessible name once for reuse below.
        const controlType = getControlType(element);
        const accessibleName = getAccessibleName(element);

        // Read geometry from the live layout box; detached nodes yield a zero rectangle.
        const boundingRectangle = element.getBoundingClientRect();

        // SVG and other namespaced elements expose className as an object, so only a
        // string class attribute is recorded directly.
        const className = typeof element.className === "string"
            ? element.className
            : "";

        return contract.newNode({
            automationId: getAutomationId(element),
            bounds: contract.newBounds(boundingRectangle),
            className,
            controlType,
            isTopWindow,
            isTriggerElement,
            machine,
            name: accessibleName,
            properties: newNodeProperties(element)
        });
    }

    /**
     * Resolves the accessible name of an element.
     *
     * @remarks
     * Compute-only helper. Resolution order follows common accessibility practice:
     * aria-label, then aria-labelledby text, then alt/title/value, then trimmed text
     * content. The first non-empty source wins.
     *
     * @param {Element} element The element whose name is needed.
     * @returns {string} The accessible name, or an empty string when none is found.
     */
    function getAccessibleName(element) {
        // An explicit aria-label always takes precedence over derived names.
        const ariaLabel = element.getAttribute && element.getAttribute("aria-label");

        if (ariaLabel) {
            return ariaLabel.trim();
        }

        // aria-labelledby points at other elements whose text forms the name.
        const labelledByText = getLabelledByText(element);

        if (labelledByText) {
            return labelledByText;
        }

        // Common attribute-based names cover images, buttons, and titled controls.
        const attributeName = getFirstAttributeValue(element, ["alt", "title", "placeholder"]);

        if (attributeName) {
            return attributeName.trim();
        }

        // Fall back to the visible text, collapsed and trimmed to a single line.
        const textContent = (element.textContent || "").replace(WHITESPACE_PATTERN, " ").trim();

        return textContent.slice(0, MAXIMUM_VALUE_PROPERTY_LENGTH);
    }

    /**
     * Resolves the automation id of an element.
     *
     * @remarks
     * Compute-only helper. A real id wins; otherwise a data-test-id is used because it
     * is the most stable test handle in this codebase's conventions.
     *
     * @param {Element} element The element whose automation id is needed.
     * @returns {string} The automation id, or an empty string when none is found.
     */
    function getAutomationId(element) {
        // A native id is the closest analogue to a desktop AutomationId.
        if (element.id) {
            return element.id;
        }

        // Fall back to the test handle convention used across this project's UIs.
        const testId = element.getAttribute && element.getAttribute("data-test-id");

        return testId || "";
    }

    /**
     * Resolves a friendly control type for an element.
     *
     * @remarks
     * Compute-only helper. An explicit ARIA role is the most semantic signal; without
     * one, the tag name (capitalised) is used so the field is never empty.
     *
     * @param {Element} element The element whose control type is needed.
     * @returns {string} The friendly control type.
     */
    function getControlType(element) {
        // An explicit role best describes the element's purpose to consumers.
        const role = element.getAttribute && element.getAttribute("role");

        if (role) {
            return role.trim();
        }

        // Without a role, present the tag name in a readable capitalised form.
        const tagName = element.nodeName.toLowerCase();

        return `${tagName.charAt(0).toUpperCase()}${tagName.slice(1)}`;
    }

    /**
     * Collects an element and each of its ancestor elements up to the document root.
     *
     * @remarks
     * Compute-only helper. The returned list is bottom-up: index 0 is the element and
     * the final index is the outermost ancestor (usually the html element).
     *
     * @param {Element} element The element to start from.
     * @returns {Element[]} The element followed by its ancestors.
     */
    function getElementChain(element) {
        // Walk parent links from the element to the root, recording each element node.
        const elementChain = [];
        let currentElement = element;

        while (currentElement && currentElement.nodeType === Node.ELEMENT_NODE) {
            elementChain.push(currentElement);

            currentElement = currentElement.parentElement;
        }

        return elementChain;
    }

    /**
     * Returns the first non-empty value among the given attribute names.
     *
     * @remarks
     * Compute-only helper extracted so attribute lookups do not rely on a dense `||`
     * chain that would hide the priority order of the attribute names.
     *
     * @param {Element} element The element to read attributes from.
     * @param {string[]} attributeNames The attribute names to try, in priority order.
     * @returns {string} The first non-empty attribute value, or an empty string.
     */
    function getFirstAttributeValue(element, attributeNames) {
        // Without a getAttribute method there is nothing to read.
        if (typeof element.getAttribute !== "function") {
            return "";
        }

        // Return the first attribute that carries a non-empty value, in priority order.
        for (const attributeName of attributeNames) {
            const attributeValue = element.getAttribute(attributeName);

            if (attributeValue) {
                return attributeValue;
            }
        }

        return "";
    }

    /**
     * Resolves the combined text referenced by an element's aria-labelledby attribute.
     *
     * @remarks
     * Compute-only helper. Returns an empty string when the attribute is absent or none
     * of the referenced ids resolve, so callers can treat it as "no labelledby name".
     *
     * @param {Element} element The element whose aria-labelledby should be resolved.
     * @returns {string} The combined referenced text, trimmed.
     */
    function getLabelledByText(element) {
        // Without the attribute there is nothing to resolve.
        const labelledBy = element.getAttribute && element.getAttribute("aria-labelledby");

        if (!labelledBy) {
            return "";
        }

        // Each whitespace-separated id may reference an element contributing name text.
        const ownerDocument = element.ownerDocument;
        const referencedIds = labelledBy.split(/\s+/).filter(Boolean);
        const referencedTexts = [];

        referencedIds.forEach((referencedId) => {
            const referencedElement = ownerDocument && ownerDocument.getElementById(referencedId);

            if (referencedElement) {
                referencedTexts.push((referencedElement.textContent || "").trim());
            }
        });

        return referencedTexts.join(" ").replace(WHITESPACE_PATTERN, " ").trim();
    }

    /**
     * Builds the machine descriptor for a document.
     *
     * @remarks
     * Compute-only helper. A page cannot read the host machine name, so the document
     * hostname stands in for the name and the origin for the public address.
     *
     * @param {Document} ownerDocument The document the recorded element belongs to.
     * @returns {object} The machine descriptor in contract shape.
     */
    function newMachineForDocument(ownerDocument) {
        // Derive a host identity from the document location when it is available.
        const documentLocation = ownerDocument && ownerDocument.location;
        const hostName = documentLocation ? documentLocation.hostname : "";
        const originAddress = documentLocation ? documentLocation.origin : "";

        return contract.newMachine({
            name: hostName,
            publicAddress: originAddress
        });
    }

    /**
     * Collects web-specific properties for a node into a free-form map.
     *
     * @remarks
     * Compute-only helper. The contract leaves `properties` open, so it carries the CSS
     * selector and other web detail that has no dedicated contract field.
     *
     * @param {Element} element The element whose properties are collected.
     * @returns {object} The properties map.
     */
    function newNodeProperties(element) {
        // Start with locators and identity that are always useful to a consumer.
        const tagName = element.nodeName.toLowerCase();
        const properties = {
            tagName,
            cssSelector: locator.getCssSelector(element)
        };

        // Record the navigable target for links so consumers can correlate navigation.
        const hrefValue = getFirstAttributeValue(element, ["href"]);
        const isLinkWithHref = tagName === "a" && Boolean(hrefValue);

        if (isLinkWithHref) {
            properties.href = hrefValue;
        }

        // Record the input type so consumers can distinguish text, checkbox, etc.
        const typeValue = getFirstAttributeValue(element, ["type"]);

        if (typeValue) {
            properties.type = typeValue;
        }

        return properties;
    }

    // Expose the mapper API on the shared namespace.
    namespace.mapper = {
        newChainFromElement,
        newNodeFromElement
    };
})(globalThis);
