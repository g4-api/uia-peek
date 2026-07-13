/**
 * DOM locator generation for the G4 Chromium Recorder extension.
 *
 * Produces two locators for any element: an absolute XPath (the primary locator,
 * matching the UiaPeek convention of an absolute path) and a reasonably unique CSS
 * selector (stored alongside the node for consumers that prefer CSS).
 *
 * @remarks
 * Both locators are scoped to the element's own document. Inside an iframe the path
 * is relative to that frame's document root, because a single cross-frame string
 * cannot be expressed in plain XPath or CSS. Frame context is recorded separately on
 * the node properties by the mapper.
 *
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded
 * as a content script in every frame.
 */
(function initializeDomLocatorModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    // Characters that must be backslash-escaped inside a CSS selector when the platform
    // CSS.escape function is unavailable. Declared once so the fallback escaper does not
    // execute an inline literal regex on every call.
    const CSS_SPECIAL_CHARACTER_PATTERN = /([ !"#$%&'()*+,.\/:;<=>?@\[\\\]^`{|}~])/g;

    /**
     * Builds an absolute XPath for an element, from the document root down to it.
     *
     * @remarks
     * Compute-only. Each step uses the lowercase tag name and a 1-based index among
     * siblings that share the same tag, which is the standard absolute-XPath form and
     * stays stable across documents that do not assign ids.
     *
     * @param {Element} element The element to locate.
     * @returns {string} The absolute XPath, or an empty string for a non-element node.
     */
    function getAbsoluteXpath(element) {
        // Guard against non-element inputs so callers can pass any event target safely.
        if (!element || element.nodeType !== Node.ELEMENT_NODE) {
            return "";
        }

        // Accumulate path steps from the element upward, then reverse for a root-down path.
        const pathSteps = [];
        let currentElement = element;

        while (currentElement && currentElement.nodeType === Node.ELEMENT_NODE) {
            // Compute the 1-based index of this element among same-tag siblings so the
            // step uniquely identifies it even when several siblings share a tag.
            const tagName = currentElement.nodeName.toLowerCase();
            const siblingIndex = getSameTagIndex(currentElement);
            const stepText = `${tagName}[${siblingIndex}]`;

            pathSteps.push(stepText);

            // Climb to the parent element; stop once we leave the element tree.
            currentElement = currentElement.parentElement;
        }

        // Reverse the collected steps so the path reads from the document root down.
        pathSteps.reverse();

        return `/${pathSteps.join("/")}`;
    }

    /**
     * Builds a reasonably unique CSS selector for an element.
     *
     * @remarks
     * Compute-only. Prefers a stable id when present; otherwise walks up the tree
     * building `tag:nth-of-type(n)` steps until reaching the root or an element with an
     * id. The result is unique within the element's own document in the common case.
     *
     * @param {Element} element The element to locate.
     * @returns {string} The CSS selector, or an empty string for a non-element node.
     */
    function getCssSelector(element) {
        // Guard against non-element inputs so callers can pass any event target safely.
        if (!element || element.nodeType !== Node.ELEMENT_NODE) {
            return "";
        }

        // Accumulate selector steps from the element upward, then reverse for a top-down selector.
        const selectorSteps = [];
        let currentElement = element;

        while (currentElement && currentElement.nodeType === Node.ELEMENT_NODE) {
            // A unique id lets us anchor the selector and stop climbing immediately.
            const isIdPresent = Boolean(currentElement.id);
            const isIdUsable = isIdPresent && testUniqueId(currentElement);

            if (isIdUsable) {
                selectorSteps.push(`#${getCssEscaped(currentElement.id)}`);
                break;
            }

            // Without a usable id, qualify the tag by its position among same-tag siblings.
            const tagName = currentElement.nodeName.toLowerCase();
            const siblingIndex = getSameTagIndex(currentElement);
            const stepText = `${tagName}:nth-of-type(${siblingIndex})`;

            selectorSteps.push(stepText);

            // Climb to the parent element; stop once we leave the element tree.
            currentElement = currentElement.parentElement;
        }

        // Reverse the collected steps so the selector reads from the ancestor down.
        selectorSteps.reverse();

        return selectorSteps.join(" > ");
    }

    /**
     * Escapes a string for safe use inside a CSS selector.
     *
     * @remarks
     * Compute-only helper. Uses the native CSS.escape when available and falls back to
     * escaping the characters that most commonly break id selectors otherwise.
     *
     * @param {string} value The raw value to escape.
     * @returns {string} The escaped value.
     */
    function getCssEscaped(value) {
        // Prefer the platform escaper, which handles the full CSS grammar correctly.
        const isNativeEscapeAvailable = typeof globalScope.CSS !== "undefined"
            && typeof globalScope.CSS.escape === "function";

        if (isNativeEscapeAvailable) {
            return globalScope.CSS.escape(value);
        }

        // Fallback: escape characters that frequently appear in ids and break selectors.
        return value.replace(CSS_SPECIAL_CHARACTER_PATTERN, "\\$1");
    }

    /**
     * Computes the 1-based index of an element among siblings that share its tag.
     *
     * @remarks
     * Compute-only helper shared by both the XPath and CSS builders, extracted so the
     * two locators always agree on indexing rules.
     *
     * @param {Element} element The element whose index is needed.
     * @returns {number} The 1-based same-tag index.
     */
    function getSameTagIndex(element) {
        // Count preceding siblings of the same tag to derive the 1-based position.
        let sameTagIndex = 1;
        let previousSibling = element.previousElementSibling;

        while (previousSibling) {
            // Only siblings with the same tag affect the index in both XPath and CSS.
            const isSameTag = previousSibling.nodeName === element.nodeName;

            if (isSameTag) {
                sameTagIndex += 1;
            }

            previousSibling = previousSibling.previousElementSibling;
        }

        return sameTagIndex;
    }

    /**
     * Tests whether an element's id is unique within its document.
     *
     * @remarks
     * Compute-only helper. Duplicate ids are invalid but common in real pages, so the
     * CSS builder only anchors on an id when it actually resolves to one element.
     *
     * @param {Element} element The element whose id should be tested.
     * @returns {boolean} True when the id resolves to exactly one element.
     */
    function testUniqueId(element) {
        // Resolve the owning document so the lookup works inside iframes too.
        const ownerDocument = element.ownerDocument;

        if (!ownerDocument) {
            return false;
        }

        // The id is usable only when it matches a single element in its document.
        const matchedElements = ownerDocument.querySelectorAll(`#${getCssEscaped(element.id)}`);

        return matchedElements.length === 1;
    }

    // Expose the locator API on the shared namespace.
    namespace.locator = {
        getAbsoluteXpath,
        getCssSelector
    };
})(globalThis);
