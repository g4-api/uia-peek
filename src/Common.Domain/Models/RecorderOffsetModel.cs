using System.Text.Json.Serialization;

namespace Common.Domain.Models
{
    /// <summary>
    /// Represents a pointer offset in pixels from the top-left corner of a recorded target element.
    /// </summary>
    /// <remarks>
    /// The shared recorder contract carries this model for every event. Producers that cannot
    /// calculate element-relative geometry retain the zero-valued default.
    /// </remarks>
    public class RecorderOffsetModel
    {
        #region *** Properties   ***
        /// <summary>
        /// Gets or sets the horizontal distance in pixels from the target element's left edge.
        /// </summary>
        [JsonPropertyName("x")]
        public int X { get; set; }

        /// <summary>
        /// Gets or sets the vertical distance in pixels from the target element's top edge.
        /// </summary>
        [JsonPropertyName("y")]
        public int Y { get; set; }
        #endregion
    }
}
