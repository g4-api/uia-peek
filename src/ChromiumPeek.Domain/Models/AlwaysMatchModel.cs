using System.Text.Json.Serialization;

namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Represents the W3C "alwaysMatch" capability set. Only the Chromium options are used
    /// by the peek launcher.
    /// </summary>
    public class AlwaysMatchModel
    {
        /// <summary>
        /// The Chromium-specific options, keyed by the W3C vendor extension "goog:chromeOptions".
        /// </summary>
        [JsonPropertyName("goog:chromeOptions")]
        public ChromeOptionsModel ChromeOptions { get; set; }
    }
}
