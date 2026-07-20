using System;
using System.Diagnostics;
using System.Threading;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Publishes and validates the latest pre-click mouse target snapshot.
    /// </summary>
    /// <remarks>
    /// Publication uses an atomic reference replacement. Readers never observe a
    /// partially populated snapshot and never wait for UIA work to complete.
    /// </remarks>
    internal sealed class MouseTargetSnapshotStore
    {
        #region *** Fields       ***
        private readonly int _boundsTolerancePixels;
        private readonly long _maximumAgeTimestampUnits;
        private MouseTargetSnapshot _snapshot;
        #endregion

        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="MouseTargetSnapshotStore"/> class.
        /// </summary>
        /// <param name="maximumAgeMilliseconds">The maximum accepted snapshot age in milliseconds.</param>
        /// <param name="boundsTolerancePixels">The screen-pixel tolerance applied around target bounds.</param>
        internal MouseTargetSnapshotStore(int maximumAgeMilliseconds, int boundsTolerancePixels)
        {
            // Reject invalid timing configuration before converting it to monotonic timestamp units.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                value: maximumAgeMilliseconds,
                paramName: nameof(maximumAgeMilliseconds));

            // Reject a negative geometry tolerance because it inverts target containment rules.
            ArgumentOutOfRangeException.ThrowIfNegative(
                value: boundsTolerancePixels,
                paramName: nameof(boundsTolerancePixels));

            // Convert the public millisecond policy once so hook-time selection performs no floating-point setup.
            _maximumAgeTimestampUnits = (long)Math.Ceiling(
                maximumAgeMilliseconds * (double)Stopwatch.Frequency / 1000.0);
            _boundsTolerancePixels = boundsTolerancePixels;
        }
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Atomically removes the published target snapshot.
        /// </summary>
        internal void Clear()
        {
            // Remove the reference in one write so later hook callbacks cannot retain stale session data.
            Volatile.Write(ref _snapshot, null);
        }

        /// <summary>
        /// Selects the latest snapshot using only in-memory timing and geometry checks.
        /// </summary>
        /// <param name="request">The current capture session and mouse-down context.</param>
        /// <returns>The accepted snapshot or the reason selection failed.</returns>
        internal MouseTargetSnapshotResolution Resolve(MouseTargetSnapshotRequest request)
        {
            // Require complete hook context before inspecting shared snapshot state.
            ArgumentNullException.ThrowIfNull(
                argument: request,
                paramName: nameof(request));

            // Reject selection while no recorder can consume the resulting event.
            if (!request.CaptureActive)
            {
                return NewRejectedResolution(MouseTargetSnapshotStatus.CaptureInactive);
            }

            // Read the immutable reference once so concurrent publication cannot change this selection attempt.
            var snapshot = Volatile.Read(ref _snapshot);

            if (snapshot == null)
            {
                return NewRejectedResolution(MouseTargetSnapshotStatus.Missing);
            }

            // Reject data retained by a previous connection generation.
            if (snapshot.SessionGeneration != request.SessionGeneration)
            {
                return NewRejectedResolution(MouseTargetSnapshotStatus.SessionMismatch);
            }

            // Measure freshness with a monotonic clock so wall-clock changes cannot invalidate ordering.
            var ageTimestampUnits = request.CurrentTimestamp - snapshot.CapturedAtTimestamp;
            var ageMilliseconds = ageTimestampUnits * 1000.0 / Stopwatch.Frequency;
            var isExpired = ageTimestampUnits < 0 || ageTimestampUnits > _maximumAgeTimestampUnits;

            if (isExpired)
            {
                return new MouseTargetSnapshotResolution(
                    snapshot: null,
                    status: MouseTargetSnapshotStatus.Expired,
                    ageMilliseconds);
            }

            // Require a complete locator and precomputed finite bounds before trusting cached geometry.
            var chain = snapshot.Chain;
            var hasLocator = chain != null &&
                (!string.IsNullOrWhiteSpace(chain.Locator) || !string.IsNullOrWhiteSpace(chain.FallbackLocator));
            var hasFiniteBounds =
                double.IsFinite(snapshot.TargetLeft) &&
                double.IsFinite(snapshot.TargetTop) &&
                double.IsFinite(snapshot.TargetWidth) &&
                double.IsFinite(snapshot.TargetHeight) &&
                snapshot.TargetWidth > 0 &&
                snapshot.TargetHeight > 0;

            if (!hasLocator || !hasFiniteBounds)
            {
                return new MouseTargetSnapshotResolution(
                    snapshot: null,
                    status: MouseTargetSnapshotStatus.InvalidChain,
                    ageMilliseconds);
            }

            // Accept only clicks inside the observed trigger bounds so a moved pointer cannot reuse another target.
            var left = snapshot.TargetLeft - _boundsTolerancePixels;
            var top = snapshot.TargetTop - _boundsTolerancePixels;
            var right = snapshot.TargetLeft + snapshot.TargetWidth + _boundsTolerancePixels;
            var bottom = snapshot.TargetTop + snapshot.TargetHeight + _boundsTolerancePixels;
            var containsPoint = request.X >= left && request.X <= right && request.Y >= top && request.Y <= bottom;

            if (!containsPoint)
            {
                return new MouseTargetSnapshotResolution(
                    snapshot: null,
                    status: MouseTargetSnapshotStatus.OutsideBounds,
                    ageMilliseconds);
            }

            return new MouseTargetSnapshotResolution(
                snapshot,
                status: MouseTargetSnapshotStatus.Accepted,
                ageMilliseconds);
        }

        /// <summary>
        /// Atomically publishes a completely materialized target snapshot.
        /// </summary>
        /// <param name="snapshot">The immutable snapshot to expose to hook callbacks.</param>
        internal void Set(MouseTargetSnapshot snapshot)
        {
            // Require a complete snapshot before replacing the reference visible to the hook thread.
            ArgumentNullException.ThrowIfNull(
                argument: snapshot,
                paramName: nameof(snapshot));

            Volatile.Write(ref _snapshot, snapshot);
        }

        // Creates a rejected resolution with no inspected-snapshot age to keep early exits consistent.
        private static MouseTargetSnapshotResolution NewRejectedResolution(MouseTargetSnapshotStatus status)
        {
            return new MouseTargetSnapshotResolution(snapshot: null, status, ageMilliseconds: 0);
        }
        #endregion
    }
}
