using System.Collections.Generic;

namespace UiaPeek.Domain.Models
{
    /// <summary>
    /// Represents a chain of UI Automation nodes, starting from a trigger element
    /// and climbing upward through its ancestors until the top window.
    /// </summary>
    public class UiaChainModel
    {
        /// <summary>
        /// Gets or sets a locator string for the trigger element.
        /// </summary>
        public string Locator { get; set; } = string.Empty;

        /// <summary>
        /// The ordered sequence of nodes, beginning with the trigger element
        /// and continuing upward through its parent elements.
        /// </summary>
        public List<UiaNodeModel> Path { get; set; } = [];

        /// <summary>
        /// The screen coordinates of the trigger element (the last element in the chain).
        /// </summary>
        public UiaPointModel Point { get; set; } = null;

        /// <summary>
        /// The top-level window node in the chain,
        /// representing the ancestor closest to the desktop root.
        /// </summary>
        public UiaNodeModel TopWindow { get; set; } = null;

        /// <summary>
        /// The action or event that caused this chain to be recorded, if applicable.
        /// </summary>
        public string Trigger { get; set; } = string.Empty;
    }
}
