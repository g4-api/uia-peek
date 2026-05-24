using Common.Domain.Models;

using UiaPeek.Domain.Extensions;
using UiaPeek.Domain.Models;

using UIAutomationClient;

namespace UiaPeek.Domain
{
    /// <inheritdoc />
    public class UiaPeekRepository : IUiaPeekRepository
    {
        /// <inheritdoc />
        public UiaChainModel Peek(int x, int y)
        {
            // Initialize the UI Automation engine.
            var automation = new CUIAutomation8();

            // Get the element directly under the given screen coordinates.
            var element = automation.ElementFromPoint(pt: new tagPOINT { x = x, y = y });

            // If an element was found, build and return its ancestor chain; otherwise return null.
            var chain = automation.NewAncestorChain(element) ?? new UiaChainModel();

            // Set the point information in the chain model if it exists.
            chain.Point = new RecorderPointModel { XPos = x, YPos = y };

            // Build the absolute XPath locator for the ancestor chain.
            chain.FallbackLocator = chain.ResolveLocator();
            chain.Locator = chain.FormatXpath();

            // If the formatted XPath locator is empty, fall back to the absolute locator.
            chain.Locator = string.IsNullOrEmpty(chain.Locator)
                ? chain.FallbackLocator
                : chain.Locator;

            // Indicate that this chain was triggered by a hover action.
            chain.Trigger = "Hover";

            // Return the constructed ancestor chain.
            return chain;
        }

        /// <inheritdoc />
        public UiaChainModel Peek()
        {
            // Create a new instance of the UI Automation engine.
            var automation = new CUIAutomation8();

            // Get the element that currently has keyboard focus.
            var element = automation.GetFocusedElement();

            // Build the ancestor chain for the focused element,
            // or return a new empty model if no element was found.
            var chain = automation.NewAncestorChain(element) ?? new UiaChainModel();

            // Generate the absolute XPath locator for the ancestor chain.
            chain.FallbackLocator = chain.ResolveLocator();
            chain.Locator = chain.FormatXpath();

            // Indicate that this chain was triggered by a focus action.
            chain.Trigger = "Focus";

            // If the formatted XPath locator is empty, fall back to the absolute locator.
            chain.Locator = string.IsNullOrEmpty(chain.Locator)
                ? chain.FallbackLocator
                : chain.Locator;

            // Return the ancestor chain model.
            return chain;
        }
    }
}
