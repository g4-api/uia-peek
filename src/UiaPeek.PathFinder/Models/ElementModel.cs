using System;
using System.Xml.Linq;

using UIAutomationClient;

namespace UiaPeek.PathFinder.Models
{
    /// <summary>
    /// Represents an element in the user interface.
    /// </summary>
    internal class ElementModel
    {
        /// <summary>
        /// Initializes a new instance of the Element class.
        /// </summary>
        public ElementModel()
        {
            // Generate a new unique identifier using Guid.NewGuid() and assign it to the Id property.
            Id = $"{Guid.NewGuid()}";
        }

        /// <summary>
        /// Gets or sets the clickable point of the element.
        /// </summary>
        public ClickablePointModel ClickablePoint { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the element.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the location of the element on the screen.
        /// </summary>
        public LocationModel Location { get; set; }

        /// <summary>
        /// Gets or sets the XML representation of the element.
        /// </summary>
        public XNode Node { get; set; }

        /// <summary>
        /// Gets or sets the UI Automation element associated with this element.
        /// </summary>
        public IUIAutomationElement UIAutomationElement { get; set; }
    }
}
