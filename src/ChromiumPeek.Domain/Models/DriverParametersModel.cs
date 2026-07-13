namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Represents the driver parameters supplied by the client. Additional fields such as
    /// driver, driverBinaries, and firstMatch are accepted but ignored for now; only the
    /// capabilities (browser binary and args) are consumed by the peek launcher.
    /// </summary>
    public class DriverParametersModel
    {
        /// <summary>
        /// The W3C capabilities describing the browser to launch.
        /// </summary>
        public CapabilitiesModel Capabilities { get; set; }
    }
}
