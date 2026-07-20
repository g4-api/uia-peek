using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Represents a fully materialized UIA target observed before a mouse-button transition.
    /// </summary>
    /// <remarks>
    /// Instances are published once and then treated as immutable so the low-level
    /// hook can safely retain the model through an event record without invoking UIA.
    /// </remarks>
    internal sealed class MouseTargetSnapshot
    {
        #region *** Properties   ***
        /// <summary>
        /// Gets the monotonic timestamp recorded after the UIA chain was materialized.
        /// </summary>
        internal long CapturedAtTimestamp { get; init; }

        /// <summary>
        /// Gets the materialized target chain that existed before the click transition.
        /// </summary>
        internal UiaChainModel Chain { get; init; }

        /// <summary>
        /// Gets the recorder-session generation that owned the observation.
        /// </summary>
        internal long SessionGeneration { get; init; }

        /// <summary>
        /// Gets the height of the observed trigger element in physical screen pixels.
        /// </summary>
        internal double TargetHeight { get; init; }

        /// <summary>
        /// Gets the horizontal position of the observed trigger element.
        /// </summary>
        internal double TargetLeft { get; init; }

        /// <summary>
        /// Gets the vertical position of the observed trigger element.
        /// </summary>
        internal double TargetTop { get; init; }

        /// <summary>
        /// Gets the width of the observed trigger element in physical screen pixels.
        /// </summary>
        internal double TargetWidth { get; init; }

        /// <summary>
        /// Gets the horizontal screen coordinate used for the observation.
        /// </summary>
        internal int X { get; init; }

        /// <summary>
        /// Gets the vertical screen coordinate used for the observation.
        /// </summary>
        internal int Y { get; init; }
        #endregion
    }

    /// <summary>
    /// Describes why a pre-click snapshot was accepted or rejected.
    /// </summary>
    internal enum MouseTargetSnapshotStatus
    {
        NotApplicable,
        Accepted,
        CaptureInactive,
        Expired,
        InvalidChain,
        Missing,
        OutsideBounds,
        SessionMismatch
    }

    /// <summary>
    /// Contains the in-memory values required to select a pre-click mouse target.
    /// </summary>
    internal sealed class MouseTargetSnapshotRequest
    {
        #region *** Properties   ***
        /// <summary>
        /// Gets a value indicating whether a recorder client is currently connected.
        /// </summary>
        internal bool CaptureActive { get; init; }

        /// <summary>
        /// Gets the monotonic timestamp captured by the mouse hook.
        /// </summary>
        internal long CurrentTimestamp { get; init; }

        /// <summary>
        /// Gets the current recorder-session generation.
        /// </summary>
        internal long SessionGeneration { get; init; }

        /// <summary>
        /// Gets the mouse-down horizontal screen coordinate.
        /// </summary>
        internal int X { get; init; }

        /// <summary>
        /// Gets the mouse-down vertical screen coordinate.
        /// </summary>
        internal int Y { get; init; }
        #endregion
    }

    /// <summary>
    /// Contains the result of selecting a pre-click target snapshot.
    /// </summary>
    internal readonly struct MouseTargetSnapshotResolution
    {
        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="MouseTargetSnapshotResolution"/> structure.
        /// </summary>
        /// <param name="snapshot">The accepted snapshot, or null when selection failed.</param>
        /// <param name="status">The status that describes the selection result.</param>
        /// <param name="ageMilliseconds">The age of the inspected snapshot in milliseconds.</param>
        internal MouseTargetSnapshotResolution(
            MouseTargetSnapshot snapshot,
            MouseTargetSnapshotStatus status,
            double ageMilliseconds)
        {
            Snapshot = snapshot;
            Status = status;
            AgeMilliseconds = ageMilliseconds;
        }
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets a value indicating whether the snapshot passed every selection rule.
        /// </summary>
        internal bool Accepted => Status == MouseTargetSnapshotStatus.Accepted;

        /// <summary>
        /// Gets the age of the inspected snapshot in milliseconds.
        /// </summary>
        internal double AgeMilliseconds { get; }

        /// <summary>
        /// Gets the accepted snapshot, or null when selection failed.
        /// </summary>
        internal MouseTargetSnapshot Snapshot { get; }

        /// <summary>
        /// Gets the status that describes the selection result.
        /// </summary>
        internal MouseTargetSnapshotStatus Status { get; }
        #endregion
    }
}
