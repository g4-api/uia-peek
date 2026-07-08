namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Represents the Chromium-specific options supplied by the client under the W3C vendor
    /// extension key "goog:chromeOptions". Only the binary path and launch arguments are
    /// consumed by the peek launcher today.
    /// </summary>
    public class ChromeOptionsModel
    {
        /// <summary>
        /// The full path to the Chromium/Chrome executable to launch.
        /// </summary>
        public string Binary { get; set; }

        /// <summary>
        /// Extra command-line arguments appended after the peek base flags.
        /// </summary>
        public string[] Args { get; set; }
    }
}
