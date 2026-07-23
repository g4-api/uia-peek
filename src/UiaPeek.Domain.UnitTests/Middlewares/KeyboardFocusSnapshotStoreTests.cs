using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Threading.Tasks;

using UiaPeek.Domain.Middlewares;
using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.UnitTests.Middlewares
{
    [TestClass]
    [TestCategory(nameof(KeyboardFocusSnapshotStore))]
    [TestCategory("UnitTest")]
    public sealed class KeyboardFocusSnapshotStoreTests
    {
        [TestMethod(DisplayName = "Verify that clearing the store removes the session focus target")]
        public void KeyboardFocusSnapshotClearRemovesTargetTest()
        {
            // Arrange: publish a complete focus target for an active recorder generation.
            var store = new KeyboardFocusSnapshotStore();
            store.Set(NewChain("line-number"), sessionGeneration: 7);

            // Act: clear the session-owned publication before the next keyboard press.
            store.Clear();
            var chain = store.Resolve(sessionGeneration: 7);

            // Assert: verify that explicit invalidation prevents reuse of the previous focused element.
            Assert.IsNull(chain);
        }

        [TestMethod(DisplayName = "Verify that concurrent focus publication exposes only complete chains")]
        public void KeyboardFocusSnapshotConcurrentPublicationRemainsCompleteTest()
        {
            // Arrange: prepare two immutable focus targets within one recorder generation.
            var store = new KeyboardFocusSnapshotStore();
            var lineNumberChain = NewChain("line-number");
            var goToChain = NewChain("go-to");
            store.Set(lineNumberChain, sessionGeneration: 7);

            // Act: replace and resolve complete publications concurrently under the same session.
            Parallel.For(0, 1000, index =>
            {
                store.Set(index % 2 == 0 ? lineNumberChain : goToChain, sessionGeneration: 7);
                var chain = store.Resolve(sessionGeneration: 7);

                // Assert: every observed chain remains one complete publication.
                Assert.IsNotNull(chain);
                Assert.IsTrue(chain.Locator == "line-number" || chain.Locator == "go-to");
            });
        }

        [TestMethod(DisplayName = "Verify that an invalid focus chain clears the previous publication")]
        public void KeyboardFocusSnapshotInvalidChainClearsTargetTest()
        {
            // Arrange: publish a valid target before a later focus observation fails to produce a path.
            var store = new KeyboardFocusSnapshotStore();
            store.Set(NewChain("line-number"), sessionGeneration: 7);

            // Act: publish the incomplete replacement under the same session.
            store.Set(new UiaChainModel(), sessionGeneration: 7);
            var chain = store.Resolve(sessionGeneration: 7);

            // Assert: verify that the known-obsolete target cannot survive an invalid replacement.
            Assert.IsNull(chain);
        }

        [TestMethod(DisplayName = "Verify that the latest focus publication replaces the previous target")]
        public void KeyboardFocusSnapshotLatestPublicationSelectedTest()
        {
            // Arrange: publish the Line number focus target before navigation.
            var store = new KeyboardFocusSnapshotStore();
            store.Set(NewChain("line-number"), sessionGeneration: 7);

            // Act: publish the Go to focus target produced by the first Tab transition.
            var goToChain = NewChain("go-to");
            store.Set(goToChain, sessionGeneration: 7);
            var resolvedChain = store.Resolve(sessionGeneration: 7);

            // Assert: verify that the next key receives the latest complete focus target.
            Assert.AreSame(goToChain, resolvedChain);
        }

        [TestMethod(DisplayName = "Verify that a focus snapshot remains valid until focus or session changes")]
        public void KeyboardFocusSnapshotRemainsValidUntilTransitionTest()
        {
            // Arrange: publish one stable target without attaching a wall-clock lifetime.
            var store = new KeyboardFocusSnapshotStore();
            var lineNumberChain = NewChain("line-number");
            store.Set(lineNumberChain, sessionGeneration: 7);

            // Act: resolve the unchanged focus target repeatedly without another publication.
            UiaChainModel resolvedChain = null;

            for (var index = 0; index < 1000; index++)
            {
                resolvedChain = store.Resolve(sessionGeneration: 7);
            }

            // Assert: verify that elapsed processing and repeated reads never expire stable focus.
            Assert.AreSame(lineNumberChain, resolvedChain);
        }

        [TestMethod(DisplayName = "Verify that a previous recorder generation cannot reuse the focus target")]
        public void KeyboardFocusSnapshotSessionMismatchRejectedTest()
        {
            // Arrange: publish a complete focus target under recorder generation seven.
            var store = new KeyboardFocusSnapshotStore();
            store.Set(NewChain("line-number"), sessionGeneration: 7);

            // Act: resolve after a new recorder generation begins.
            var chain = store.Resolve(sessionGeneration: 8);

            // Assert: verify that focus state never crosses recording-session boundaries.
            Assert.IsNull(chain);
        }

        // Creates one complete focus chain so store tests detect partial or obsolete publications.
        private static UiaChainModel NewChain(string locator)
        {
            return new UiaChainModel
            {
                Locator = locator,
                Path = [new UiaNodeModel()]
            };
        }
    }
}
