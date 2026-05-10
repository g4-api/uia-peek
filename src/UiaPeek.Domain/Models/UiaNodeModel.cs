using Common.Domain.Models;

using UIAutomationClient;

namespace UiaPeek.Domain.Models
{
    /// <summary>
    /// Represents a single UI Automation (UIA) node within a recorded chain.
    /// Wraps an <see cref="IUIAutomationElement"/> as the underlying UI element.
    /// 
    /// This class is a strongly-typed alias for <see cref="RecorderNodeModel{TElement}"/>,
    /// allowing the recorder pipeline to work specifically with UIA elements.
    /// Extend this class when UIA nodes require additional metadata, properties,
    /// or domain-specific behavior.
    /// </remarks>
    public class UiaNodeModel : RecorderNodeModel<IUIAutomationElement>
    {
        /// <summary>
        /// 0-based index of this element among all UIA siblings under the same parent.
        /// </summary>
        public int SiblingIndex { get; set; } = 0;

        /// <summary>
        /// 0-based index of this element among UIA siblings that share the same ControlType under the same parent.
        /// </summary>
        public int SiblingIndexOfSameControlType { get; set; } = 0;
    }
}
