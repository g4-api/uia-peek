namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Request body for starting a peek browser. Wraps the client's driver parameters under
    /// the "driverParameters" key.
    /// </summary>
    public class StartPeekRequestModel
    {
        /// <summary>
        /// The driver parameters (capabilities) describing the browser to launch.
        /// </summary>
        public DriverParametersModel DriverParameters { get; set; }
    }
}
