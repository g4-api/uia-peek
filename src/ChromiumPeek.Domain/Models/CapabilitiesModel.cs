namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Represents the W3C capabilities object. Only the alwaysMatch set is used by the peek
    /// launcher; firstMatch and other members are accepted but ignored for now.
    /// </summary>
    public class CapabilitiesModel
    {
        /// <summary>
        /// The capabilities every candidate session must match.
        /// </summary>
        public AlwaysMatchModel AlwaysMatch { get; set; }
    }
}
