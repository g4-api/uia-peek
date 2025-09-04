using UiaPeek.Extensions;
using UiaPeek.Models;

using UIAutomationClient;

namespace UiaPeek.Domain
{
    /// <summary>
    /// Provides access to UI Automation elements and ancestor chain information.
    /// </summary>
    public class UiaPeekRepository
    {
        /// <summary>
        /// Retrieves the ancestor chain of the UI Automation element located at the given screen coordinates.
        /// </summary>
        /// <param name="x">The X-coordinate on the screen.</param>
        /// <param name="y">The Y-coordinate on the screen.</param>
        /// <returns>A <see cref="UiaChainModel"/> representing the ancestor chain of the element at the specified point, or <c>null</c> if no element is found.</returns>
        public UiaChainModel Peek(int x, int y)
        {
            // Initialize the UI Automation engine.
            var automation = new CUIAutomation8();

            // Get the element directly under the given screen coordinates.
            var element = automation.ElementFromPoint(pt: new tagPOINT { x = x, y = y });

            // If an element was found, build and return its ancestor chain; otherwise return null.
            var chain = automation.NewAncestorChain(element) ?? new UiaChainModel();

            // Set the point information in the chain model if it exists.
            chain.Point = new UiaPointModel { XPos = x, YPos = y };

            // Build the absolute XPath locator for the ancestor chain.
            chain.Locator = chain.ResolveLocator();

            // Return the constructed ancestor chain.
            return chain;
        }
    }
}
