using UiaPeek.Domain.Models;

namespace UiaPeek.Domain
{
    /// <summary>
    /// Represents a repository for accessing UI Automation elements and their ancestor chains.
    /// </summary>
    public interface IUiaPeekRepository
    {
        /// <summary>
        /// Retrieves the ancestor chain of the UI Automation element located at the given screen coordinates.
        /// </summary>
        /// <param name="x">The X-coordinate on the screen.</param>
        /// <param name="y">The Y-coordinate on the screen.</param>
        /// <returns>A <see cref="UiaChainModel"/> representing the ancestor chain of the element at the specified point, or <c>null</c> if no element is found.</returns>
        UiaChainModel Peek();

        /// <summary>
        /// Retrieves the currently focused UI Automation element and constructs
        /// its ancestor chain representation, including an absolute XPath locator.
        /// </summary>
        /// <returns>A <see cref="UiaChainModel"/> representing the focused element and its ancestors,or an empty model if no element is currently focused.</returns>
        UiaChainModel Peek(int x, int y);
    }
}