namespace UiaPeek.Domain.Models
{
    /// <summary>
    /// Represents a single event recorded during a UI automation session.
    /// Captures details about the event type, its timing, related UI element chain,
    /// and any associated value payload.
    /// </summary>
    public class RecordingEventModel
    {
        /// <summary>
        /// Gets or sets the UI Automation chain associated with this event.
        /// This provides the hierarchical element path where the event occurred.
        /// </summary>
        public UiaChainModel Chain { get; set; }

        /// <summary>
        /// Gets or sets the specific event identifier or name (e.g., "Click", "KeyDown").
        /// </summary>
        public string Event { get; set; }

        /// <summary>
        /// Gets or sets the timestamp (in Unix epoch milliseconds) when the event occurred.
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the category or type of the event (e.g., "Mouse", "Keyboard").
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Gets or sets the value associated with this event.
        /// For example, a pressed key, mouse button, or entered text.
        /// </summary>
        public object Value { get; set; }
    }
}
