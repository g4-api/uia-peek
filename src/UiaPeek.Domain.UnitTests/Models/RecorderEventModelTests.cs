using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Text.Json;

using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.UnitTests.Models
{
    [TestClass]
    [TestCategory(nameof(UiaEventModel))]
    [TestCategory("UnitTest")]
    public sealed class RecorderEventModelTests
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        [TestMethod(DisplayName = "Verify that UIA events serialize the shared zero offset by default")]
        public void RecorderEventDefaultOffsetSerializationTest()
        {
            // Arrange: create an event without producer-specific pointer geometry.
            var recordingEvent = new UiaEventModel();

            // Act: serialize through the same camel-case convention used by the SignalR protocol.
            var json = JsonSerializer.Serialize(recordingEvent, _jsonOptions);
            using var document = JsonDocument.Parse(json);
            var offset = document.RootElement.GetProperty("offset");

            // Assert: verify that both shared axes remain present with their zero defaults.
            Assert.AreEqual(0, offset.GetProperty("x").GetInt32());
            Assert.AreEqual(0, offset.GetProperty("y").GetInt32());
        }
    }
}
