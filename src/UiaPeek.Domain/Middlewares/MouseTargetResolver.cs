using System;
using System.Collections.Generic;

using Common.Domain.Models;

using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Resolves UI Automation targets for mouse-button transitions and retains
    /// each pressed target until the matching release is observed.
    /// </summary>
    /// <remarks>
    /// A release can remove a transient menu item or dialog before a later UIA
    /// hit-test runs. Reusing the press-time chain preserves the clicked target.
    /// </remarks>
    internal sealed class MouseTargetResolver
    {
        #region *** Fields       ***
        private readonly Dictionary<MouseButton, UiaChainModel> _pressedTargets = [];
        private readonly IUiaPeekRepository _repository;
        private readonly object _syncRoot = new();
        #endregion

        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="MouseTargetResolver"/> class.
        /// </summary>
        /// <param name="repository">The repository used for coordinate-based UIA hit-tests.</param>
        internal MouseTargetResolver(IUiaPeekRepository repository)
        {
            // Reject an incomplete resolver before it begins retaining input state.
            ArgumentNullException.ThrowIfNull(
                argument: repository,
                paramName: nameof(repository));

            // Retain the repository used for press and orphan-release lookups.
            _repository = repository;
        }
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Removes all retained press-time targets.
        /// </summary>
        internal void Clear()
        {
            // Synchronize lifecycle cleanup with any in-flight input resolution.
            lock (_syncRoot)
            {
                _pressedTargets.Clear();
            }
        }

        /// <summary>
        /// Resolves and retains the target under a pressed mouse button.
        /// </summary>
        /// <param name="request">The mouse button, coordinates, and optional pre-click chain.</param>
        /// <returns>The pre-click chain when supplied, or the UIA chain found at the press coordinates.</returns>
        internal UiaChainModel ResolveDown(MouseDownTargetRequest request)
        {
            // Require complete press context before selecting or retaining a target.
            ArgumentNullException.ThrowIfNull(
                argument: request,
                paramName: nameof(request));

            // Prefer the pre-dispatch hover chain so mouse-down UI mutations cannot change identity.
            var chain = request.CapturedChain ?? _repository.Peek(request.X, request.Y);

            // Replace stale state when a duplicate press arrives for the same button.
            lock (_syncRoot)
            {
                _pressedTargets[request.Button] = chain;
            }

            return chain;
        }

        /// <summary>
        /// Resolves and retains the target under a pressed mouse button using coordinate fallback.
        /// </summary>
        /// <param name="button">The mouse button entering the pressed state.</param>
        /// <param name="x">The horizontal screen coordinate captured by the hook.</param>
        /// <param name="y">The vertical screen coordinate captured by the hook.</param>
        /// <returns>The UIA chain found at the press coordinates.</returns>
        internal UiaChainModel ResolveDown(MouseButton button, int x, int y)
        {
            // Route legacy callers through the complete request-based resolution path.
            return ResolveDown(new MouseDownTargetRequest
            {
                Button = button,
                X = x,
                Y = y
            });
        }

        /// <summary>
        /// Resolves the pointer offset from the top-left corner of a UIA chain's trigger element.
        /// </summary>
        /// <param name="chain">The resolved UIA chain containing the trigger element geometry.</param>
        /// <param name="x">The horizontal physical screen coordinate captured by the mouse hook.</param>
        /// <param name="y">The vertical physical screen coordinate captured by the mouse hook.</param>
        /// <returns>The calculated pixel offset, or zero on both axes when valid geometry is unavailable.</returns>
        internal static RecorderOffsetModel ResolveOffset(UiaChainModel chain, int x, int y)
        {
            // Select the explicitly marked trigger so ancestor geometry cannot shift the recorded click point.
            var trigger = chain?.Path?.FindLast(node => node != null && node.IsTriggerElement);
            var bounds = trigger?.Bounds;
            var isValidBounds = bounds != null &&
                double.IsFinite(bounds.Left) &&
                double.IsFinite(bounds.Top) &&
                double.IsFinite(bounds.Width) &&
                double.IsFinite(bounds.Height) &&
                bounds.Width > 0 &&
                bounds.Height > 0;

            // Preserve the shared zero default when UIA cannot provide a usable target rectangle.
            if (!isValidBounds)
            {
                return new RecorderOffsetModel();
            }

            // Convert UIA's double rectangle coordinates to integer replay offsets without clamping edge clicks.
            return new RecorderOffsetModel
            {
                X = (int)Math.Round(x - bounds.Left, MidpointRounding.AwayFromZero),
                Y = (int)Math.Round(y - bounds.Top, MidpointRounding.AwayFromZero)
            };
        }

        /// <summary>
        /// Resolves a released mouse button from its retained press-time target.
        /// </summary>
        /// <param name="button">The mouse button entering the released state.</param>
        /// <param name="x">The horizontal screen coordinate captured by the hook.</param>
        /// <param name="y">The vertical screen coordinate captured by the hook.</param>
        /// <returns>
        /// The retained press-time chain, or a coordinate-based fallback when no
        /// matching press was captured.
        /// </returns>
        internal MouseTargetResolution ResolveUp(MouseButton button, int x, int y)
        {
            // Consume the retained target exactly once for the matching release.
            lock (_syncRoot)
            {
                if (_pressedTargets.Remove(button, out var chain))
                {
                    return new MouseTargetResolution(chain, usedFallback: false);
                }
            }

            // Preserve legacy behavior for sessions that begin after a press event.
            var fallbackChain = _repository.Peek(x, y);
            return new MouseTargetResolution(fallbackChain, usedFallback: true);
        }
        #endregion
    }

    /// <summary>
    /// Identifies a mouse button whose press-time target is retained.
    /// </summary>
    internal enum MouseButton
    {
        Left,
        Middle,
        Right
    }

    /// <summary>
    /// Contains the complete producer context used to resolve a pressed mouse target.
    /// </summary>
    internal sealed class MouseDownTargetRequest
    {
        #region *** Properties   ***
        /// <summary>
        /// Gets the mouse button entering the pressed state.
        /// </summary>
        internal MouseButton Button { get; init; }

        /// <summary>
        /// Gets the pre-dispatch UIA chain selected by the mouse hook, or null when unavailable.
        /// </summary>
        internal UiaChainModel CapturedChain { get; init; }

        /// <summary>
        /// Gets the horizontal screen coordinate captured by the hook.
        /// </summary>
        internal int X { get; init; }

        /// <summary>
        /// Gets the vertical screen coordinate captured by the hook.
        /// </summary>
        internal int Y { get; init; }
        #endregion
    }

    /// <summary>
    /// Contains the resolved mouse target and its resolution source.
    /// </summary>
    internal readonly struct MouseTargetResolution
    {
        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="MouseTargetResolution"/> structure.
        /// </summary>
        /// <param name="chain">The resolved UIA target chain.</param>
        /// <param name="usedFallback">A value indicating that no matching press target existed.</param>
        internal MouseTargetResolution(UiaChainModel chain, bool usedFallback)
        {
            Chain = chain;
            UsedFallback = usedFallback;
        }
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets the resolved UIA target chain.
        /// </summary>
        internal UiaChainModel Chain { get; }

        /// <summary>
        /// Gets a value indicating whether coordinate-based fallback resolution was used.
        /// </summary>
        internal bool UsedFallback { get; }
        #endregion
    }
}
