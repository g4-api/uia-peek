using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;

using UiaPeek.Domain.Middlewares;
using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.UnitTests.Middlewares
{
    [TestClass]
    [TestCategory(nameof(MouseTargetResolver))]
    [TestCategory("UnitTest")]
    public sealed class MouseTargetResolverTests
    {
        [TestMethod(DisplayName = "Verify that clearing retained targets restores release fallback")]
        public void MouseTargetClearRestoresReleaseFallbackTest()
        {
            // Arrange: provide distinct press and fallback chains.
            var pressedChain = NewChain("pressed");
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(pressedChain, fallbackChain);
            var resolver = new MouseTargetResolver(repository);
            resolver.ResolveDown(MouseButton.Left, x: 10, y: 20);

            // Act: clear retained state before resolving the release.
            resolver.Clear();
            var resolution = resolver.ResolveUp(MouseButton.Left, x: 10, y: 20);

            // Assert: verify that the release uses a fresh coordinate lookup.
            Assert.AreSame(fallbackChain, resolution.Chain);
            Assert.IsTrue(resolution.UsedFallback);
            Assert.AreEqual(2, repository.PointPeekCount);
        }

        [TestMethod(DisplayName = "Verify that mouse buttons retain independent press targets")]
        public void MouseTargetIndependentButtonsTest()
        {
            // Arrange: provide a separate chain for each pressed button.
            var leftChain = NewChain("left");
            var rightChain = NewChain("right");
            var repository = new FakeUiaPeekRepository(leftChain, rightChain);
            var resolver = new MouseTargetResolver(repository);

            // Act: press both buttons and release them in reverse order.
            resolver.ResolveDown(MouseButton.Left, x: 10, y: 20);
            resolver.ResolveDown(MouseButton.Right, x: 30, y: 40);
            var rightResolution = resolver.ResolveUp(MouseButton.Right, x: 30, y: 40);
            var leftResolution = resolver.ResolveUp(MouseButton.Left, x: 10, y: 20);

            // Assert: verify that each release receives its own press-time target.
            Assert.AreSame(rightChain, rightResolution.Chain);
            Assert.AreSame(leftChain, leftResolution.Chain);
            Assert.IsFalse(rightResolution.UsedFallback);
            Assert.IsFalse(leftResolution.UsedFallback);
            Assert.AreEqual(2, repository.PointPeekCount);
        }

        [TestMethod(DisplayName = "Verify that an orphaned mouse release uses coordinate fallback")]
        public void MouseTargetOrphanedReleaseUsesFallbackTest()
        {
            // Arrange: provide the chain currently available at the release coordinates.
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(fallbackChain);
            var resolver = new MouseTargetResolver(repository);

            // Act: resolve a release without a preceding press.
            var resolution = resolver.ResolveUp(MouseButton.Middle, x: 10, y: 20);

            // Assert: verify that legacy coordinate lookup remains available.
            Assert.AreSame(fallbackChain, resolution.Chain);
            Assert.IsTrue(resolution.UsedFallback);
            Assert.AreEqual(1, repository.PointPeekCount);
        }

        [TestMethod(DisplayName = "Verify that a release consumes its retained press target")]
        public void MouseTargetReleaseConsumesPressTargetTest()
        {
            // Arrange: provide a press chain and a fallback for a later orphaned release.
            var pressedChain = NewChain("pressed");
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(pressedChain, fallbackChain);
            var resolver = new MouseTargetResolver(repository);
            resolver.ResolveDown(MouseButton.Left, x: 10, y: 20);

            // Act: resolve two releases for the single retained press.
            var pairedResolution = resolver.ResolveUp(MouseButton.Left, x: 10, y: 20);
            var orphanedResolution = resolver.ResolveUp(MouseButton.Left, x: 10, y: 20);

            // Assert: verify that only the first release can consume the pressed target.
            Assert.AreSame(pressedChain, pairedResolution.Chain);
            Assert.IsFalse(pairedResolution.UsedFallback);
            Assert.AreSame(fallbackChain, orphanedResolution.Chain);
            Assert.IsTrue(orphanedResolution.UsedFallback);
            Assert.AreEqual(2, repository.PointPeekCount);
        }

        [TestMethod(DisplayName = "Verify that a mouse release reuses its press-time target")]
        public void MouseTargetReleaseReusesPressTargetTest()
        {
            // Arrange: make a changed post-click target observable if a second lookup occurs.
            var pressedChain = NewChain("transient-target");
            var postClickChain = NewChain("post-click-background");
            var repository = new FakeUiaPeekRepository(pressedChain, postClickChain);
            var resolver = new MouseTargetResolver(repository);

            // Act: resolve the matching down and up transitions.
            var downChain = resolver.ResolveDown(MouseButton.Left, x: 10, y: 20);
            var upResolution = resolver.ResolveUp(MouseButton.Left, x: 10, y: 20);

            // Assert: verify that release avoids the changed post-click UIA tree.
            Assert.AreSame(pressedChain, downChain);
            Assert.AreSame(pressedChain, upResolution.Chain);
            Assert.IsFalse(upResolution.UsedFallback);
            Assert.AreEqual(1, repository.PointPeekCount);
        }

        [TestMethod(DisplayName = "Verify that a repeated press replaces stale button state")]
        public void MouseTargetRepeatedPressReplacesStaleStateTest()
        {
            // Arrange: provide an obsolete target followed by the latest pressed target.
            var staleChain = NewChain("stale");
            var latestChain = NewChain("latest");
            var repository = new FakeUiaPeekRepository(staleChain, latestChain);
            var resolver = new MouseTargetResolver(repository);

            // Act: resolve two presses before the matching release.
            resolver.ResolveDown(MouseButton.Left, x: 10, y: 20);
            resolver.ResolveDown(MouseButton.Left, x: 30, y: 40);
            var resolution = resolver.ResolveUp(MouseButton.Left, x: 30, y: 40);

            // Assert: verify that the latest press owns the released target.
            Assert.AreSame(latestChain, resolution.Chain);
            Assert.IsFalse(resolution.UsedFallback);
            Assert.AreEqual(2, repository.PointPeekCount);
        }

        private static UiaChainModel NewChain(string locator)
        {
            return new UiaChainModel
            {
                Locator = locator
            };
        }

        private sealed class FakeUiaPeekRepository : IUiaPeekRepository
        {
            private readonly Queue<UiaChainModel> _pointChains;

            internal FakeUiaPeekRepository(params UiaChainModel[] pointChains)
            {
                _pointChains = new Queue<UiaChainModel>(pointChains);
            }

            internal int PointPeekCount { get; private set; }

            public UiaChainModel Peek()
            {
                throw new InvalidOperationException("Focused-element lookup is not part of these tests.");
            }

            public UiaChainModel Peek(int x, int y)
            {
                PointPeekCount++;

                if (_pointChains.Count == 0)
                {
                    throw new InvalidOperationException("No coordinate-based chain remains for this test.");
                }

                return _pointChains.Dequeue();
            }
        }
    }
}
