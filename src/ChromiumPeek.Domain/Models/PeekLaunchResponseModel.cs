namespace ChromiumPeek.Domain.Models
{
    /// <summary>
    /// Response returned after starting a peek browser, carrying the operating-system
    /// process id used to stop it later.
    /// </summary>
    public class PeekLaunchResponseModel
    {
        /// <summary>
        /// The operating-system process id of the launched browser.
        /// </summary>
        public int ProcessId { get; set; }
    }
}
