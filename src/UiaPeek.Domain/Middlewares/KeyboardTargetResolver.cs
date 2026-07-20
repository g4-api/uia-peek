using System;
using System.Collections.Generic;

using Common.Domain.Models;

using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Resolves focused UI Automation targets for keyboard transitions and retains
    /// the first press target until the matching physical key is released.
    /// </summary>
    /// <remarks>
    /// Key actions can close a dialog or transfer focus before Key Up is processed.
    /// Retaining the first Key Down state keeps both transitions bound to one target.
    /// </remarks>
    internal sealed class KeyboardTargetResolver
    {
        #region *** Fields       ***
        private readonly Dictionary<KeyboardKeyIdentity, KeyboardPressState> _pressedTargets = [];
        private readonly IUiaPeekRepository _repository;
        private readonly object _syncRoot = new();
        #endregion

        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="KeyboardTargetResolver"/> class.
        /// </summary>
        /// <param name="repository">The repository used for focused-element UIA lookups.</param>
        internal KeyboardTargetResolver(IUiaPeekRepository repository)
        {
            // Require the focused-element provider before accepting keyboard state.
            ArgumentNullException.ThrowIfNull(
                argument: repository,
                paramName: nameof(repository));

            // Retain the provider used only for first presses and orphaned releases.
            _repository = repository;
        }
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Removes every retained keyboard press so state cannot cross capture lifecycles.
        /// </summary>
        internal void Clear()
        {
            // Synchronize lifecycle cleanup with any focused-element lookup in progress.
            lock (_syncRoot)
            {
                _pressedTargets.Clear();
            }
        }

        /// <summary>
        /// Resolves the first target for a pressed physical key and reuses it for auto-repeat.
        /// </summary>
        /// <param name="identity">The stable physical-key identity shared by Down and Up.</param>
        /// <param name="keyText">The display text resolved for the first press.</param>
        /// <returns>The retained keyboard target and its resolution source.</returns>
        internal KeyboardTargetResolution ResolveDown(KeyboardKeyIdentity identity, string keyText)
        {
            // Serialize lookup and publication so lifecycle cleanup cannot retain a partially resolved press.
            lock (_syncRoot)
            {
                // Preserve the first physical press target across all auto-repeat Down messages.
                if (_pressedTargets.TryGetValue(identity, out var retainedState))
                {
                    return new KeyboardTargetResolution(
                        retainedState.Chain,
                        retainedState.KeyText,
                        KeyboardTargetSource.RepeatedPress);
                }

                // Resolve focus once for the first Down so later UI changes cannot invalidate the paired Up target.
                var chain = _repository.Peek();
                var pressState = new KeyboardPressState
                {
                    Chain = chain,
                    KeyText = keyText
                };

                // Publish the complete state only after both target and text have been materialized.
                _pressedTargets.Add(identity, pressState);

                return new KeyboardTargetResolution(
                    chain,
                    keyText,
                    KeyboardTargetSource.FocusedPress);
            }
        }

        /// <summary>
        /// Resolves a released physical key from its retained first-press target.
        /// </summary>
        /// <param name="identity">The stable physical-key identity shared by Down and Up.</param>
        /// <returns>The paired state, or a focused-element fallback for an orphaned release.</returns>
        internal KeyboardTargetResolution ResolveUp(KeyboardKeyIdentity identity)
        {
            // Consume a paired press exactly once so later releases cannot reuse obsolete state.
            lock (_syncRoot)
            {
                if (_pressedTargets.Remove(identity, out var retainedState))
                {
                    return new KeyboardTargetResolution(
                        retainedState.Chain,
                        retainedState.KeyText,
                        KeyboardTargetSource.PairedRelease);
                }

                // Preserve legacy focused-element behavior when recording begins after Key Down.
                var fallbackChain = _repository.Peek();

                return new KeyboardTargetResolution(
                    fallbackChain,
                    keyText: string.Empty,
                    KeyboardTargetSource.OrphanFallback);
            }
        }
        #endregion

        #region *** Nested Types ***
        private sealed class KeyboardPressState
        {
            internal UiaChainModel Chain { get; init; }

            internal string KeyText { get; init; }
        }
        #endregion
    }

    /// <summary>
    /// Identifies one physical keyboard key independently of display text and layout.
    /// </summary>
    internal readonly record struct KeyboardKeyIdentity
    {
        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="KeyboardKeyIdentity"/> structure.
        /// </summary>
        /// <param name="virtualKey">The Windows virtual-key code.</param>
        /// <param name="scanCode">The hardware scan code reported by the low-level hook.</param>
        /// <param name="extended">A value indicating whether Windows marked the key as extended.</param>
        internal KeyboardKeyIdentity(uint virtualKey, uint scanCode, bool extended)
        {
            Extended = extended;
            ScanCode = scanCode;
            VirtualKey = virtualKey;
        }
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets a value indicating whether Windows marked the key as extended.
        /// </summary>
        internal bool Extended { get; }

        /// <summary>
        /// Gets the hardware scan code reported by the low-level hook.
        /// </summary>
        internal uint ScanCode { get; }

        /// <summary>
        /// Gets the Windows virtual-key code.
        /// </summary>
        internal uint VirtualKey { get; }
        #endregion
    }

    /// <summary>
    /// Identifies how a keyboard transition obtained its UI Automation target.
    /// </summary>
    internal enum KeyboardTargetSource
    {
        FocusedPress,
        OrphanFallback,
        PairedRelease,
        RepeatedPress
    }

    /// <summary>
    /// Contains the resolved keyboard target, retained key text, and resolution source.
    /// </summary>
    internal readonly struct KeyboardTargetResolution
    {
        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="KeyboardTargetResolution"/> structure.
        /// </summary>
        /// <param name="chain">The resolved UI Automation target chain.</param>
        /// <param name="keyText">The key text retained from the first press.</param>
        /// <param name="source">The source used to resolve the transition.</param>
        internal KeyboardTargetResolution(UiaChainModel chain, string keyText, KeyboardTargetSource source)
        {
            Chain = chain;
            KeyText = keyText;
            Source = source;
        }
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets the resolved UI Automation target chain.
        /// </summary>
        internal UiaChainModel Chain { get; }

        /// <summary>
        /// Gets the key text retained from the first press, or an empty value for an orphaned release.
        /// </summary>
        internal string KeyText { get; }

        /// <summary>
        /// Gets the source used to resolve the keyboard transition.
        /// </summary>
        internal KeyboardTargetSource Source { get; }
        #endregion
    }
}
