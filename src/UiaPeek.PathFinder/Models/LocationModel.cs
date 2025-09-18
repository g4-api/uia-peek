namespace UiaPeek.PathFinder.Models
{
    /// <summary>
    /// Represents the location (bounding box) of an element on the screen.
    /// </summary>
    internal class LocationModel
    {
        /// <summary>
        /// Gets or sets the bottom coordinate of the element.
        /// </summary>
        public int Bottom { get; set; }

        /// <summary>
        /// Gets or sets the left coordinate of the element.
        /// </summary>
        public int Left { get; set; }

        /// <summary>
        /// Gets or sets the right coordinate of the element.
        /// </summary>
        public int Right { get; set; }

        /// <summary>
        /// Gets or sets the top coordinate of the element.
        /// </summary>
        public int Top { get; set; }
    }
}
