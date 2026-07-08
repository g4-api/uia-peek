/**
 * Reusable themed form controls for the G4 Chromium Recorder extension UI.
 *
 * Provides an on/off switch (used for boolean settings) and a themed number field with
 * custom step buttons (native spinners are hidden by the CSS). Both are pure DOM
 * enhancers over existing markup — they hold no application state — modelled on the
 * g4-test-wright control components.
 *
 * @remarks
 * Attached to the shared `globalThis.g4Recorder` namespace (see constants.js). Loaded as a
 * classic script by the options page before options.js.
 */
(function initializeFormControlsModule(globalScope) {
    // Create or reuse the single extension namespace shared across every context.
    const namespace = globalScope.g4Recorder = globalScope.g4Recorder || {};

    /**
     * Enhances a `.number-field` with themed step buttons that clamp to min/max.
     *
     * @remarks
     * Owns the field's step behaviour but no application state. The up/down buttons step
     * the value by one, clamped to the input's min and max, then report the new value.
     *
     * @param {HTMLElement} fieldElement The `.number-field` container.
     * @param {object} [options] Field options.
     * @param {(value: number) => void} [options.onChange] Called with the new value after a step.
     * @returns {{get: () => string, set: (value: number|string) => void}} The field API.
     */
    function newNumberField(fieldElement, options) {
        const settings = options || {};
        const onChange = typeof settings.onChange === "function"
            ? settings.onChange
            : () => {};

        // Resolve the field parts; without an input there is nothing to enhance.
        const inputElement = fieldElement.querySelector(".number-field__input");
        const upButton = fieldElement.querySelector(".number-field__step--up");
        const downButton = fieldElement.querySelector(".number-field__step--down");

        if (!inputElement) {
            return { get: () => "", set: () => {} };
        }

        // Steps the value by the delta, clamped to the input's min/max, then reports it.
        const stepValue = (delta) => {
            // Read the bounds and current value from the input, defaulting sensibly.
            const minimum = Number(inputElement.min);
            const maximum = Number(inputElement.max);
            const currentValue = Number(inputElement.value);
            const baseValue = Number.isFinite(currentValue)
                ? currentValue
                : minimum;

            // Apply the step, then clamp within the available bounds.
            let nextValue = baseValue + delta;

            const isBelowMinimum = Number.isFinite(minimum) && nextValue < minimum;

            if (isBelowMinimum) {
                nextValue = minimum;
            }

            const isAboveMaximum = Number.isFinite(maximum) && nextValue > maximum;

            if (isAboveMaximum) {
                nextValue = maximum;
            }

            // Apply and report the new value.
            inputElement.value = String(nextValue);

            onChange(nextValue);
        };

        // Increase on the up button.
        const onUpClick = (event) => {
            event.preventDefault();

            stepValue(1);
        };

        // Decrease on the down button.
        const onDownClick = (event) => {
            event.preventDefault();

            stepValue(-1);
        };

        if (upButton) {
            upButton.addEventListener("click", onUpClick);
        }

        if (downButton) {
            downButton.addEventListener("click", onDownClick);
        }

        return {
            get: () => inputElement.value,
            set: (value) => {
                inputElement.value = String(value);
            }
        };
    }

    /**
     * Enhances a `.switch` button with on/off behaviour.
     *
     * @remarks
     * Owns the button's on/off visual across clicks but no application state. A click
     * flips the visual immediately (so users see instant feedback) and then reports the
     * new value; `set` syncs the visual from outside without firing the change callback.
     *
     * @param {HTMLElement} switchButton The host `.switch` button (role="switch").
     * @param {object} [options] Switch options.
     * @param {(isOn: boolean) => void} [options.onChange] Called with the new value on click.
     * @returns {{get: () => boolean, set: (isOn: boolean) => void}} The switch API.
     */
    function newToggleSwitch(switchButton, options) {
        const settings = options || {};
        const onChange = typeof settings.onChange === "function"
            ? settings.onChange
            : () => {};

        // Reflect a boolean onto the button's modifier class and aria-checked, which is
        // the visual contract shared with the app and the CSS.
        const setSwitchState = (isOn) => {
            switchButton.classList.toggle("switch--on", Boolean(isOn));
            switchButton.setAttribute("aria-checked", String(Boolean(isOn)));
        };

        const getSwitchState = () => switchButton.classList.contains("switch--on");

        // Click is the only component-owned mutation path: flip the visual first, then
        // report the new value outward.
        const onSwitchClick = () => {
            const isNextOn = !getSwitchState();

            setSwitchState(isNextOn);
            onChange(isNextOn);
        };

        switchButton.addEventListener("click", onSwitchClick);

        return { get: getSwitchState, set: setSwitchState };
    }

    // Expose the form-controls API on the shared namespace.
    namespace.formControls = {
        newNumberField,
        newToggleSwitch
    };
})(globalThis);
