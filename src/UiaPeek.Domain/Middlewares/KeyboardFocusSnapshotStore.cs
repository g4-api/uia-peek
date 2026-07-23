using System.Threading;

using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Publishes the latest focused-element chain for pre-dispatch keyboard capture.
    /// </summary>
    /// <remarks>
    /// Focus snapshots remain valid for their recorder session until a focus-change observation
    /// replaces or clears them. Atomic reference publication keeps the low-level keyboard hook free
    /// from UI Automation calls and prevents readers from observing partial chains.
    /// </remarks>
    internal sealed class KeyboardFocusSnapshotStore
    {
        #region *** Fields       ***
        private KeyboardFocusSnapshot _snapshot;
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Atomically removes the published focus snapshot.
        /// </summary>
        internal void Clear()
        {
            // Remove the complete reference so later keyboard callbacks cannot retain obsolete focus state.
            Volatile.Write(ref _snapshot, null);
        }

        /// <summary>
        /// Selects the focused-element chain owned by the requested recorder session.
        /// </summary>
        /// <param name="sessionGeneration">The active recorder generation stamped on the keyboard event.</param>
        /// <returns>The complete focus chain, or <see langword="null"/> when no valid session-owned snapshot exists.</returns>
        internal UiaChainModel Resolve(long sessionGeneration)
        {
            // Read the immutable publication once so a concurrent focus update cannot change this selection.
            var snapshot = Volatile.Read(ref _snapshot);

            if (snapshot == null || snapshot.SessionGeneration != sessionGeneration)
            {
                return null;
            }

            // Reject incomplete UIA chains because downstream recorder actions require a concrete target path.
            var chain = snapshot.Chain;

            return chain?.Path?.Count > 0
                ? chain
                : null;
        }

        /// <summary>
        /// Atomically publishes a completely materialized focus chain for one recorder session.
        /// </summary>
        /// <param name="chain">The focused-element chain resolved outside the low-level keyboard hook.</param>
        /// <param name="sessionGeneration">The recorder generation that owns the focus observation.</param>
        internal void Set(UiaChainModel chain, long sessionGeneration)
        {
            // Require a usable path before replacing the target visible to keyboard callbacks.
            if (!(chain?.Path?.Count > 0))
            {
                Clear();
                return;
            }

            // Publish one immutable owner-and-chain pair so readers cannot combine different generations.
            Volatile.Write(ref _snapshot, new KeyboardFocusSnapshot
            {
                Chain = chain,
                SessionGeneration = sessionGeneration
            });
        }
        #endregion

        #region *** Nested Types ***
        // Owns one immutable focused-element publication until the store replaces or clears it.
        private sealed class KeyboardFocusSnapshot
        {
            internal UiaChainModel Chain { get; init; }

            internal long SessionGeneration { get; init; }
        }
        #endregion
    }
}
