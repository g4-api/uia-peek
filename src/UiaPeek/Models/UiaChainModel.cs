using System.Collections.Generic;

namespace UiaPeek.Models
{
    /// <summary>
    /// Represents a chain of UI Automation nodes, from a given element up to its top window.
    /// </summary>
    public class UiaChainModel
    {
        /// <summary>
        /// The ordered path of nodes, starting from the original element
        /// and moving upward through its ancestors until the top window.
        /// </summary>
        public List<UiaNodeModel> Path { get; set; } = [];

        /// <summary>
        /// The top-level window node in the chain.
        /// This is the ancestor closest to the desktop root.
        /// </summary>
        public UiaNodeModel TopWindow { get; set; } = null;
    }
}
