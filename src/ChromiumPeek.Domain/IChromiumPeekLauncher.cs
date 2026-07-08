using ChromiumPeek.Domain.Models;

namespace ChromiumPeek.Domain
{
    /// <summary>
    /// Launches and stops Chromium browsers that have the recorder extension loaded, on
    /// behalf of a client (for example, a G4 driver).
    /// </summary>
    public interface IChromiumPeekLauncher
    {
        /// <summary>
        /// Launches a Chromium browser using the supplied driver parameters (binary and
        /// arguments), with the recorder extension loaded, and returns the launched process id.
        /// </summary>
        /// <param name="driverParameters">The client-supplied capabilities.</param>
        /// <returns>The operating-system process id of the launched browser.</returns>
        int Start(DriverParametersModel driverParameters);

        /// <summary>
        /// Stops a browser previously started by this launcher, killing the whole process tree.
        /// </summary>
        /// <param name="processId">The process id returned by <see cref="Start"/>.</param>
        /// <returns><c>true</c> if a tracked process was stopped; <c>false</c> if the id is unknown.</returns>
        bool Stop(int processId);
    }
}
