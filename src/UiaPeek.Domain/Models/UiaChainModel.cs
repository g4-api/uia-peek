using Common.Domain.Models;

namespace UiaPeek.Domain.Models
{
    /// <summary>
    /// Represents a chain of UIA (UI Automation) nodes recorded by UiaPeek.
    /// Uses <see cref="UiaNodeModel"/> as the node type.
    ///
    /// This class acts as a strongly-typed alias for <see cref="ChainModel{TNode}"/>,
    /// providing clearer intent within the UIA domain. Extend this class when
    /// UIA-specific chain metadata or behavior is required.
    /// </summary>
    public class UiaChainModel : ChainModel<UiaNodeModel>
    {
        /// <summary>
        /// Gets or sets a fallback locator string for the trigger element.
        /// </summary>
        public string FallbackLocator { get; set; } = string.Empty;
    }
}
