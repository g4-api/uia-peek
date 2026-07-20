using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Diagnostics;
using System.Threading.Tasks;

using UiaPeek.Domain.Middlewares;
using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.UnitTests.Middlewares
{
    [TestClass]
    [TestCategory(nameof(MouseTargetSnapshotStore))]
    [TestCategory("UnitTest")]
    public sealed class MouseTargetSnapshotStoreTests
    {
        [TestMethod(DisplayName = "Verify that an active fresh snapshot containing the click point is accepted")]
        public void MouseTargetSnapshotActiveFreshPointAcceptedTest()
        {
            // Arrange: publish a complete snapshot for the active recording generation.
            var store = NewStore();
            var snapshot = NewSnapshot("edit");
            store.Set(snapshot);

            // Act: select the snapshot for a click inside its trigger bounds.
            var resolution = store.Resolve(NewRequest());

            // Assert: verify that the exact immutable snapshot is returned with measured age.
            Assert.IsTrue(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.Accepted, resolution.Status);
            Assert.AreSame(snapshot, resolution.Snapshot);
            Assert.AreEqual(500.0, resolution.AgeMilliseconds, delta: 0.1);
        }

        [TestMethod(DisplayName = "Verify that clearing the store removes the published snapshot")]
        public void MouseTargetSnapshotClearRemovesPublishedTargetTest()
        {
            // Arrange: publish a valid snapshot and then clear session-owned state.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));

            // Act: clear and resolve the same click context.
            store.Clear();
            var resolution = store.Resolve(NewRequest());

            // Assert: verify that no target survives explicit cleanup.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.Missing, resolution.Status);
            Assert.IsNull(resolution.Snapshot);
        }

        [TestMethod(DisplayName = "Verify that concurrent publication exposes only complete snapshots")]
        public void MouseTargetSnapshotConcurrentPublicationRemainsCompleteTest()
        {
            // Arrange: prepare two complete immutable snapshots and publish an initial target.
            var store = NewStore();
            var firstSnapshot = NewSnapshot("first");
            var secondSnapshot = NewSnapshot("second");
            store.Set(firstSnapshot);

            // Act: replace and read snapshot references concurrently under one valid click context.
            Parallel.For(0, 1000, index =>
            {
                store.Set(index % 2 == 0 ? firstSnapshot : secondSnapshot);
                var resolution = store.Resolve(NewRequest());

                // Assert: every observed reference remains complete and selectable during publication.
                Assert.IsTrue(resolution.Accepted);
                Assert.IsNotNull(resolution.Snapshot);
                Assert.IsTrue(
                    resolution.Snapshot.Chain.Locator == "first" ||
                    resolution.Snapshot.Chain.Locator == "second");
            });
        }

        [TestMethod(DisplayName = "Verify that an inactive recorder rejects the published snapshot")]
        public void MouseTargetSnapshotInactiveRecorderRejectedTest()
        {
            // Arrange: publish a valid snapshot while constructing inactive hook context.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));
            var request = NewRequest(captureActive: false, sessionGeneration: 7);

            // Act: attempt selection without an active recorder client.
            var resolution = store.Resolve(request);

            // Assert: verify that idle producer activity cannot retain a click target.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.CaptureInactive, resolution.Status);
        }

        [TestMethod(DisplayName = "Verify that nonpositive trigger bounds reject the snapshot")]
        public void MouseTargetSnapshotInvalidBoundsRejectedTest()
        {
            // Arrange: publish a locator with zero-width trigger geometry.
            var store = NewStore();
            store.Set(NewSnapshot(locator: "edit", targetWidth: 0));

            // Act: select the geometrically incomplete target.
            var resolution = store.Resolve(NewRequest());

            // Assert: verify that invalid geometry cannot identify the clicked element.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.InvalidChain, resolution.Status);
        }

        [TestMethod(DisplayName = "Verify that a snapshot without a locator is rejected")]
        public void MouseTargetSnapshotInvalidChainRejectedTest()
        {
            // Arrange: publish bounds without a usable locator contract.
            var store = NewStore();
            store.Set(NewSnapshot(string.Empty));

            // Act: select the incomplete target.
            var resolution = store.Resolve(NewRequest());

            // Assert: verify that geometry alone cannot become a recorded element identity.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.InvalidChain, resolution.Status);
        }

        [TestMethod(DisplayName = "Verify that a newer snapshot atomically replaces the previous target")]
        public void MouseTargetSnapshotLatestPublicationSelectedTest()
        {
            // Arrange: create two complete targets for the same click area and session.
            var store = NewStore();
            var firstSnapshot = NewSnapshot("first");
            var latestSnapshot = NewSnapshot("latest");
            store.Set(firstSnapshot);

            // Act: replace the target before hook-time selection.
            store.Set(latestSnapshot);
            var resolution = store.Resolve(NewRequest());

            // Assert: verify that readers observe the latest complete reference.
            Assert.AreSame(latestSnapshot, resolution.Snapshot);
            Assert.AreEqual("latest", resolution.Snapshot.Chain.Locator);
        }

        [TestMethod(DisplayName = "Verify that a click outside trigger bounds rejects the snapshot")]
        public void MouseTargetSnapshotOutsideBoundsRejectedTest()
        {
            // Arrange: publish a target whose bounds end far before the click point.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));
            var request = NewRequest(x: 500, y: 500);

            // Act: select the target for the moved pointer.
            var resolution = store.Resolve(request);

            // Assert: verify that another screen location cannot reuse a stale hover identity.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.OutsideBounds, resolution.Status);
        }

        [TestMethod(DisplayName = "Verify that a previous recorder generation rejects the snapshot")]
        public void MouseTargetSnapshotPreviousSessionRejectedTest()
        {
            // Arrange: publish a target owned by generation seven.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));
            var request = NewRequest(captureActive: true, sessionGeneration: 8);

            // Act: select after the recorder reconnects under a new generation.
            var resolution = store.Resolve(request);

            // Assert: verify that snapshots never cross recording-session boundaries.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.SessionMismatch, resolution.Status);
        }

        [TestMethod(DisplayName = "Verify that an expired snapshot is rejected")]
        public void MouseTargetSnapshotStaleTargetRejectedTest()
        {
            // Arrange: publish a snapshot older than the configured one-second lifetime.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));
            var request = NewRequest(currentTimestamp: GetTimestamp(2501));

            // Act: select the stale target.
            var resolution = store.Resolve(request);

            // Assert: verify that old UI state cannot identify a later click.
            Assert.IsFalse(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.Expired, resolution.Status);
            Assert.IsTrue(resolution.AgeMilliseconds > 1000);
        }

        [TestMethod(DisplayName = "Verify that bounds tolerance accepts a click at the adjacent edge")]
        public void MouseTargetSnapshotToleranceAcceptsAdjacentPointTest()
        {
            // Arrange: publish bounds from X 100 through 140 with a two-pixel tolerance.
            var store = NewStore();
            store.Set(NewSnapshot("edit"));
            var request = NewRequest(x: 142, y: 120);

            // Act: select the snapshot at the tolerated outer edge.
            var resolution = store.Resolve(request);

            // Assert: verify that small physical-coordinate rounding does not lose the target.
            Assert.IsTrue(resolution.Accepted);
            Assert.AreEqual(MouseTargetSnapshotStatus.Accepted, resolution.Status);
        }

        private static long GetTimestamp(int milliseconds)
        {
            return (long)(milliseconds * (double)Stopwatch.Frequency / 1000.0);
        }

        private static MouseTargetSnapshotRequest NewRequest()
        {
            return NewRequest(captureActive: true, sessionGeneration: 7);
        }

        private static MouseTargetSnapshotRequest NewRequest(bool captureActive, long sessionGeneration)
        {
            return new MouseTargetSnapshotRequest
            {
                CaptureActive = captureActive,
                CurrentTimestamp = GetTimestamp(1500),
                SessionGeneration = sessionGeneration,
                X = 120,
                Y = 120
            };
        }

        private static MouseTargetSnapshotRequest NewRequest(long currentTimestamp)
        {
            return new MouseTargetSnapshotRequest
            {
                CaptureActive = true,
                CurrentTimestamp = currentTimestamp,
                SessionGeneration = 7,
                X = 120,
                Y = 120
            };
        }

        private static MouseTargetSnapshotRequest NewRequest(int x, int y)
        {
            return new MouseTargetSnapshotRequest
            {
                CaptureActive = true,
                CurrentTimestamp = GetTimestamp(1500),
                SessionGeneration = 7,
                X = x,
                Y = y
            };
        }

        private static MouseTargetSnapshot NewSnapshot(string locator)
        {
            return NewSnapshot(locator, targetWidth: 40);
        }

        private static MouseTargetSnapshot NewSnapshot(string locator, double targetWidth)
        {
            return new MouseTargetSnapshot
            {
                CapturedAtTimestamp = GetTimestamp(1000),
                Chain = new UiaChainModel
                {
                    Locator = locator,
                    Path =
                    [
                        new UiaNodeModel
                        {
                            Bounds = new UiaNodeModel.BoundsRectangle
                            {
                                Height = 40,
                                Left = 100,
                                Top = 100,
                                Width = targetWidth
                            },
                            IsTriggerElement = true
                        }
                    ]
                },
                SessionGeneration = 7,
                TargetHeight = 40,
                TargetLeft = 100,
                TargetTop = 100,
                TargetWidth = targetWidth,
                X = 120,
                Y = 120
            };
        }

        private static MouseTargetSnapshotStore NewStore()
        {
            return new MouseTargetSnapshotStore(
                maximumAgeMilliseconds: 1000,
                boundsTolerancePixels: 2);
        }

    }
}
