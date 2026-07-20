using System.Threading;

namespace UiaPeek.Domain.Hubs
{
    /// <summary>
    /// Tracks active UIA recorder hub connections and identifies each continuous
    /// recording session with a monotonically increasing generation.
    /// </summary>
    /// <remarks>
    /// The event-capture service uses this state to avoid hover UIA work when no
    /// recorder is listening and to reject snapshots retained by an older session.
    /// </remarks>
    internal sealed class RecorderConnectionState
    {
        #region *** Constants    ***
        internal static readonly RecorderConnectionState Instance = new();
        #endregion

        #region *** Fields       ***
        private int _activeConnections;
        private long _sessionGeneration;
        private readonly object _syncRoot = new();
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets the number of recorder clients currently connected to the UIA hub.
        /// </summary>
        internal int ActiveConnections => Volatile.Read(ref _activeConnections);

        /// <summary>
        /// Gets a value indicating whether at least one recorder client is connected.
        /// </summary>
        internal bool CaptureActive => ActiveConnections > 0;

        /// <summary>
        /// Gets the generation assigned to the current or most recent recording session.
        /// </summary>
        internal long SessionGeneration => Interlocked.Read(ref _sessionGeneration);
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Registers one recorder connection and starts a new generation when it is the first active client.
        /// </summary>
        /// <returns>The generation associated with the registered connection.</returns>
        internal long AddConnection()
        {
            lock (_syncRoot)
            {
                // Advance the generation before publishing the first active connection.
                if (_activeConnections == 0)
                {
                    Interlocked.Increment(ref _sessionGeneration);
                }

                // Publish active capture only after the current generation is visible to hook readers.
                Volatile.Write(ref _activeConnections, _activeConnections + 1);
                return SessionGeneration;
            }
        }

        /// <summary>
        /// Removes one recorder connection without allowing the active count to become negative.
        /// </summary>
        /// <returns>The number of recorder clients that remain connected.</returns>
        internal int RemoveConnection()
        {
            lock (_syncRoot)
            {
                // Ignore duplicate disconnect notifications without allowing a negative count.
                if (_activeConnections == 0)
                {
                    return 0;
                }

                // Publish the remaining active-client count as one synchronized transition.
                var remainingConnections = _activeConnections - 1;
                Volatile.Write(ref _activeConnections, remainingConnections);
                return remainingConnections;
            }
        }
        #endregion
    }
}
