namespace UiaPeek.PathFinder.Models
{
    /// <summary>
    /// Represents a clickable point on the screen.
    /// </summary>
    /// <param name="xpos">The X-coordinate of the clickable point.</param>
    /// <param name="ypos">The Y-coordinate of the clickable point.</param>
    internal class ClickablePointModel(int xpos, int ypos)
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClickablePointModel"/> class with default coordinates (0, 0).
        /// </summary>
        public ClickablePointModel()
            : this(xpos: 0, ypos: 0)
        { }

        /// <summary>
        /// Gets or sets the X-coordinate of the clickable point.
        /// </summary>
        public int XPos { get; set; } = xpos;

        /// <summary>
        /// Gets or sets the Y-coordinate of the clickable point.
        /// </summary>
        public int YPos { get; set; } = ypos;
    }
}
