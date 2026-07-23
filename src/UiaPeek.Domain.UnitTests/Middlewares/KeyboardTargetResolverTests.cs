using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Collections.Generic;

using UiaPeek.Domain.Middlewares;
using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.UnitTests.Middlewares
{
    [TestClass]
    [TestCategory(nameof(KeyboardTargetResolver))]
    [TestCategory("UnitTest")]
    public sealed class KeyboardTargetResolverTests
    {
        [TestMethod(DisplayName = "Verify that clearing retained keyboard state restores focused fallback")]
        public void KeyboardTargetClearRestoresFallbackTest()
        {
            // Arrange: retain one press target and reserve a different focused fallback for release.
            var pressedChain = NewChain("pressed");
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(fallbackChain);
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 13, scanCode: 28, extended: false);
            resolver.ResolveDown(identity, keyText: "Enter", capturedChain: pressedChain);

            // Act: clear lifecycle state before resolving the matching physical release.
            resolver.Clear();
            var resolution = resolver.ResolveUp(identity);

            // Assert: verify that cleared state cannot cross a capture-session boundary.
            Assert.AreSame(fallbackChain, resolution.Chain);
            Assert.AreEqual(KeyboardTargetSource.OrphanFallback, resolution.Source);
            Assert.AreEqual(1, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that extended-key state separates otherwise equal keyboard identities")]
        public void KeyboardTargetExtendedIdentitySeparatesStateTest()
        {
            // Arrange: provide distinct targets for keys that differ only by the extended flag.
            var standardChain = NewChain("standard");
            var extendedChain = NewChain("extended");
            var repository = new FakeUiaPeekRepository();
            var resolver = new KeyboardTargetResolver(repository);
            var standardIdentity = NewIdentity(virtualKey: 13, scanCode: 28, extended: false);
            var extendedIdentity = NewIdentity(virtualKey: 13, scanCode: 28, extended: true);

            // Act: retain and release both physical identities independently.
            resolver.ResolveDown(standardIdentity, keyText: "Enter", capturedChain: standardChain);
            resolver.ResolveDown(extendedIdentity, keyText: "Num Enter", capturedChain: extendedChain);
            var extendedRelease = resolver.ResolveUp(extendedIdentity);
            var standardRelease = resolver.ResolveUp(standardIdentity);

            // Assert: verify that extended metadata prevents one held key from consuming another target.
            Assert.AreSame(extendedChain, extendedRelease.Chain);
            Assert.AreSame(standardChain, standardRelease.Chain);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that independently held keyboard keys retain separate targets")]
        public void KeyboardTargetIndependentKeysTest()
        {
            // Arrange: provide one focused chain for each physical key press.
            var enterChain = NewChain("enter");
            var tabChain = NewChain("tab");
            var repository = new FakeUiaPeekRepository();
            var resolver = new KeyboardTargetResolver(repository);
            var enterIdentity = NewIdentity(virtualKey: 13, scanCode: 28, extended: false);
            var tabIdentity = NewIdentity(virtualKey: 9, scanCode: 15, extended: false);

            // Act: press both keys and release them in reverse order.
            resolver.ResolveDown(enterIdentity, keyText: "Enter", capturedChain: enterChain);
            resolver.ResolveDown(tabIdentity, keyText: "Tab", capturedChain: tabChain);
            var tabRelease = resolver.ResolveUp(tabIdentity);
            var enterRelease = resolver.ResolveUp(enterIdentity);

            // Assert: verify that each release receives its matching press-time target and text.
            Assert.AreSame(tabChain, tabRelease.Chain);
            Assert.AreEqual("Tab", tabRelease.KeyText);
            Assert.AreSame(enterChain, enterRelease.Chain);
            Assert.AreEqual("Enter", enterRelease.KeyText);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that an orphaned keyboard release uses focused-element fallback")]
        public void KeyboardTargetOrphanedReleaseUsesFallbackTest()
        {
            // Arrange: provide the focused chain available after recording begins during a held key.
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(fallbackChain);
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 27, scanCode: 1, extended: false);

            // Act: resolve Key Up without a captured Key Down transition.
            var resolution = resolver.ResolveUp(identity);

            // Assert: verify that legacy focus lookup remains available and text remains caller-resolved.
            Assert.AreSame(fallbackChain, resolution.Chain);
            Assert.AreEqual(string.Empty, resolution.KeyText);
            Assert.AreEqual(KeyboardTargetSource.OrphanFallback, resolution.Source);
            Assert.AreEqual(1, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that keyboard release reuses the target captured on first press")]
        public void KeyboardTargetPairedReleaseReusesPressTest()
        {
            // Arrange: place Cancel under focus and reserve an empty result representing the closed dialog.
            var cancelChain = NewChain("cancel");
            var repository = new FakeUiaPeekRepository();
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 27, scanCode: 1, extended: false);

            // Act: resolve the Down and Up transitions around the dialog-closing key action.
            var downResolution = resolver.ResolveDown(identity, keyText: "Escape", capturedChain: cancelChain);
            var upResolution = resolver.ResolveUp(identity);

            // Assert: verify that Up retains Cancel without performing the empty post-dialog lookup.
            Assert.AreSame(cancelChain, downResolution.Chain);
            Assert.AreSame(cancelChain, upResolution.Chain);
            Assert.AreEqual(1, upResolution.Chain.Path.Count);
            Assert.AreEqual("Escape", upResolution.KeyText);
            Assert.AreEqual(KeyboardTargetSource.PairedRelease, upResolution.Source);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that keyboard release consumes retained press state exactly once")]
        public void KeyboardTargetReleaseConsumesStateTest()
        {
            // Arrange: provide a first-press chain followed by the later focused fallback.
            var pressedChain = NewChain("pressed");
            var fallbackChain = NewChain("fallback");
            var repository = new FakeUiaPeekRepository(fallbackChain);
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 13, scanCode: 28, extended: false);
            resolver.ResolveDown(identity, keyText: "Enter", capturedChain: pressedChain);

            // Act: resolve two Up transitions for one retained physical press.
            var pairedRelease = resolver.ResolveUp(identity);
            var orphanedRelease = resolver.ResolveUp(identity);

            // Assert: verify that only the first release consumes the press-time target.
            Assert.AreSame(pressedChain, pairedRelease.Chain);
            Assert.AreEqual(KeyboardTargetSource.PairedRelease, pairedRelease.Source);
            Assert.AreSame(fallbackChain, orphanedRelease.Chain);
            Assert.AreEqual(KeyboardTargetSource.OrphanFallback, orphanedRelease.Source);
            Assert.AreEqual(1, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that repeated Key Down retains the first target and key text")]
        public void KeyboardTargetRepeatedDownRetainsFirstPressTest()
        {
            // Arrange: provide a valid first target and an empty post-transition focus result.
            var firstChain = NewChain("first");
            var repository = new FakeUiaPeekRepository();
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 9, scanCode: 15, extended: false);

            // Act: deliver auto-repeat Down before the single physical Up transition.
            var firstDown = resolver.ResolveDown(identity, keyText: "Tab", capturedChain: firstChain);
            var repeatedDown = resolver.ResolveDown(identity, keyText: "Changed", capturedChain: null);
            var release = resolver.ResolveUp(identity);

            // Assert: verify that repeats cannot replace the original target or its text.
            Assert.AreSame(firstChain, firstDown.Chain);
            Assert.AreSame(firstChain, repeatedDown.Chain);
            Assert.AreSame(firstChain, release.Chain);
            Assert.AreEqual("Tab", repeatedDown.KeyText);
            Assert.AreEqual("Tab", release.KeyText);
            Assert.AreEqual(KeyboardTargetSource.RepeatedPress, repeatedDown.Source);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that a missing press snapshot remains unresolved without a live focus lookup")]
        public void KeyboardTargetMissingSnapshotRemainsUnresolvedTest()
        {
            // Arrange: reserve a post-dispatch focus result that cannot identify the original key recipient.
            var destinationChain = NewChain("destination");
            var repository = new FakeUiaPeekRepository(destinationChain);
            var resolver = new KeyboardTargetResolver(repository);
            var identity = NewIdentity(virtualKey: 9, scanCode: 15, extended: false);

            // Act: resolve both transitions without a pre-dispatch snapshot.
            var downResolution = resolver.ResolveDown(identity, keyText: "Tab", capturedChain: null);
            var upResolution = resolver.ResolveUp(identity);

            // Assert: verify that neither transition adopts the later focused destination.
            Assert.IsNull(downResolution.Chain);
            Assert.IsNull(upResolution.Chain);
            Assert.AreEqual(KeyboardTargetSource.MissingSnapshot, downResolution.Source);
            Assert.AreEqual(KeyboardTargetSource.PairedRelease, upResolution.Source);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        [TestMethod(DisplayName = "Verify that successive navigation keys retain each pre-dispatch focus target")]
        public void KeyboardTargetSuccessiveNavigationRetainsTargetsTest()
        {
            // Arrange: model the Line number, Go to, and Cancel focus sequence from the recorder regression.
            var lineNumberChain = NewChain("line-number");
            var goToChain = NewChain("go-to");
            var cancelChain = NewChain("cancel");
            var repository = new FakeUiaPeekRepository();
            var resolver = new KeyboardTargetResolver(repository);
            var tabIdentity = NewIdentity(virtualKey: 9, scanCode: 15, extended: false);
            var enterIdentity = NewIdentity(virtualKey: 13, scanCode: 28, extended: true);

            // Act: resolve each physical press from the focus snapshot published before its own effect.
            var firstTab = resolver.ResolveDown(tabIdentity, keyText: "Tab", capturedChain: lineNumberChain);
            resolver.ResolveUp(tabIdentity);
            var secondTab = resolver.ResolveDown(tabIdentity, keyText: "Tab", capturedChain: goToChain);
            resolver.ResolveUp(tabIdentity);
            var enter = resolver.ResolveDown(enterIdentity, keyText: "Num Enter", capturedChain: cancelChain);
            resolver.ResolveUp(enterIdentity);

            // Assert: verify that the recorded actions target the elements that received each key.
            Assert.AreSame(lineNumberChain, firstTab.Chain);
            Assert.AreSame(goToChain, secondTab.Chain);
            Assert.AreSame(cancelChain, enter.Chain);
            Assert.AreEqual(0, repository.FocusPeekCount);
        }

        // Creates one non-empty chain so target-retention tests detect accidental focused fallback.
        private static UiaChainModel NewChain(string locator)
        {
            return new UiaChainModel
            {
                Locator = locator,
                Path = [new UiaNodeModel()]
            };
        }

        // Creates the stable identity shared by one physical key's Down and Up hook messages.
        private static KeyboardKeyIdentity NewIdentity(uint virtualKey, uint scanCode, bool extended)
        {
            return new KeyboardKeyIdentity(virtualKey, scanCode, extended);
        }

        private sealed class FakeUiaPeekRepository : IUiaPeekRepository
        {
            private readonly Queue<UiaChainModel> _focusChains;

            internal FakeUiaPeekRepository(params UiaChainModel[] focusChains)
            {
                _focusChains = new Queue<UiaChainModel>(focusChains);
            }

            internal int FocusPeekCount { get; private set; }

            public UiaChainModel Peek()
            {
                // Count focused lookups so paired and repeated transitions prove they avoid extra UIA work.
                FocusPeekCount++;

                // Fail deterministically when a test exercises an unexpected lookup path.
                if (_focusChains.Count == 0)
                {
                    throw new InvalidOperationException("No focused chain remains for this test.");
                }

                return _focusChains.Dequeue();
            }

            public UiaChainModel Peek(int x, int y)
            {
                throw new InvalidOperationException("Coordinate lookup is not part of keyboard target tests.");
            }
        }
    }
}
