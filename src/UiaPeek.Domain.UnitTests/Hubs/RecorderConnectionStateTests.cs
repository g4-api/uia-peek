using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Linq;
using System.Threading.Tasks;

using UiaPeek.Domain.Hubs;

namespace UiaPeek.Domain.UnitTests.Hubs
{
    [TestClass]
    [TestCategory(nameof(RecorderConnectionState))]
    [TestCategory("UnitTest")]
    public sealed class RecorderConnectionStateTests
    {
        [TestMethod(DisplayName = "Verify that the first connection starts an active recorder generation")]
        public void RecorderConnectionFirstClientStartsGenerationTest()
        {
            // Arrange: isolate a new process-local connection state instance.
            var state = new RecorderConnectionState();

            // Act: register the first recorder client.
            var generation = state.AddConnection();

            // Assert: verify that capture becomes active under the first generation.
            Assert.AreEqual(1, state.ActiveConnections);
            Assert.IsTrue(state.CaptureActive);
            Assert.AreEqual(1L, generation);
            Assert.AreEqual(generation, state.SessionGeneration);
        }

        [TestMethod(DisplayName = "Verify that a new session advances the recorder generation")]
        public void RecorderConnectionNewSessionAdvancesGenerationTest()
        {
            // Arrange: complete one connection lifecycle before starting another.
            var state = new RecorderConnectionState();
            var firstGeneration = state.AddConnection();
            state.RemoveConnection();

            // Act: register the first client of the next session.
            var nextGeneration = state.AddConnection();

            // Assert: verify that disconnected sessions never share snapshot generations.
            Assert.AreEqual(firstGeneration + 1, nextGeneration);
            Assert.AreEqual(1, state.ActiveConnections);
            Assert.IsTrue(state.CaptureActive);
        }

        [TestMethod(DisplayName = "Verify that concurrent clients share one recorder generation")]
        public void RecorderConnectionParallelClientsShareGenerationTest()
        {
            // Arrange: isolate an empty connection state.
            var state = new RecorderConnectionState();
            var generations = new long[20];

            // Act: register multiple clients concurrently in the same continuous session.
            Parallel.For(0, generations.Length, index =>
            {
                generations[index] = state.AddConnection();
            });

            // Assert: verify that only the zero-to-one transition advances generation state.
            Assert.IsTrue(generations.All(generation => generation == 1L));
            Assert.AreEqual(generations.Length, state.ActiveConnections);
            Assert.IsTrue(state.CaptureActive);
        }

        [TestMethod(DisplayName = "Verify that duplicate disconnects cannot make the connection count negative")]
        public void RecorderConnectionRepeatedDisconnectRemainsNonnegativeTest()
        {
            // Arrange: register and remove the only active recorder client.
            var state = new RecorderConnectionState();
            state.AddConnection();
            var firstRemainingCount = state.RemoveConnection();

            // Act: apply a duplicate disconnect notification.
            var duplicateRemainingCount = state.RemoveConnection();

            // Assert: verify that capture remains inactive at a stable zero count.
            Assert.AreEqual(0, firstRemainingCount);
            Assert.AreEqual(0, duplicateRemainingCount);
            Assert.AreEqual(0, state.ActiveConnections);
            Assert.IsFalse(state.CaptureActive);
        }
    }
}
