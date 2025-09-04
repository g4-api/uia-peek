using System;
using System.Text.Json.Serialization;

using UiaPeek.Extensions;

using UIAutomationClient;

namespace UiaPeek.Models
{
    /// <summary>
    /// Represents a UI Automation (UIA) node in the element tree.
    /// </summary>
    public class UiaNodeModel
    {
        /// <summary>
        /// The automation-specific identifier assigned to the element (AutomationId).
        /// </summary>
        public string AutomationId { get; set; }

        /// <summary>
        /// The bounding rectangle of the UI element on screen.
        /// </summary>
        public BoundsRectangle Bounds { get; set; }

        /// <summary>
        /// The class name of the UI element (for example, the underlying window class).
        /// </summary>
        public string ClassName { get; set; }

        /// <summary>
        /// The numeric identifier of the control type (matches UIA_ControlTypeIds).
        /// </summary>
        public int ControlTypeId { get; set; }

        /// <summary>
        /// The friendly name of the control type (for example, "Button", "Edit").
        /// </summary>
        public string ControlType { get; set; }

        /// <summary>
        /// Gets or sets the underlying IUIAutomationElement instance.
        /// </summary>
        [JsonIgnore]
        public IUIAutomationElement Element { get; set; }

        /// <summary>
        /// Indicates whether this node represents the top-level window in the hierarchy.
        /// </summary>
        public bool IsTopWindow { get; set; }

        /// <summary>
        /// Indicates whether this node is the target element of interest.
        /// </summary>
        public bool IsTargetElement { get; set; }

        /// <summary>
        /// Gets or sets a locator string for the element.
        /// </summary>
        public string Locator { get; set; } = string.Empty;

        /// <summary>
        /// Information about the machine hosting the UI element.
        /// </summary>
        public MachineDataModel Machine { get; set; }

        /// <summary>
        /// The display name of the UI element.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The set of UI Automation patterns supported by this element.
        /// </summary>
        public PatternDataModel[] Patterns { get; set; }

        /// <summary>
        /// The process identifier (PID) that owns the UI element.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// The runtime identifier assigned by UIA to uniquely identify the element.
        /// </summary>
        public int[] RuntimeId { get; set; }

        /// <summary>
        /// Represents the bounding rectangle of a UI element in screen coordinates.
        /// </summary>
        public class BoundsRectangle
        {
            /// <summary>
            /// The height of the rectangle in pixels.
            /// </summary>
            public double Height { get; set; }

            /// <summary>
            /// The x-coordinate of the left edge of the rectangle.
            /// </summary>
            [JsonPropertyName("X")]
            public double Left { get; set; }

            /// <summary>
            /// The y-coordinate of the top edge of the rectangle.
            /// </summary>
            [JsonPropertyName("Y")]
            public double Top { get; set; }

            /// <summary>
            /// The width of the rectangle in pixels.
            /// </summary>
            public double Width { get; set; }
        }

        /// <summary>
        /// Represents machine information for the system hosting the UI element.
        /// </summary>
        public class MachineDataModel
        {
            /// <summary>
            /// The machine's display name or hostname.
            /// </summary>
            public string Name { get; set; } = Environment.MachineName;

            /// <summary>
            /// The publicly accessible network address (e.g., IP or DNS).
            /// </summary>
            public string PublicAddress { get; set; } = ControllerUtilities.GetLocalEndpoint();
        }

        /// <summary>
        /// Represents a UI Automation (UIA) control pattern supported by an element.
        /// </summary>
        public class PatternDataModel
        {
            /// <summary>
            /// The unique identifier of the UIA pattern (matches UIA_PatternIds).
            /// </summary>
            public int Id { get; set; }

            /// <summary>
            /// The friendly name of the UIA pattern (for example, "Invoke", "Scroll").
            /// </summary>
            public string Name { get; set; }
        }
    }
}
