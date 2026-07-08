using Microsoft.AspNetCore.SignalR;

using System.Threading.Tasks;

using UiaPeek.Domain.Models;

using Common.Domain.Models;

namespace UiaPeek.Domain.Hubs
{
    /// <summary>
    /// SignalR hub for handling UI Automation (UIA) peek operations.
    /// Provides real-time communication for heartbeat checks and
    /// ancestor chain inspection at specific screen coordinates.
    /// </summary>
    public class UiaPeekHub(IUiaPeekRepository repository) : Hub
    {
        // Repository used for querying UIA elements at coordinates.
        private readonly IUiaPeekRepository _repository = repository;

        // Sends a heartbeat message to the caller.
        // This can be used by clients to verify the connection is alive.
        [HubMethodName(name: nameof(SendHeartbeat))]
        public Task SendHeartbeat()
        {
            // Notify the calling client with a heartbeat message.
            return Clients.Caller.SendAsync(
                method: "ReceiveHeartbeat",
                arg1: new HubResponseModel("Heartbeat received - connection is alive"));
        }

        // Resolves the UIA element at the given screen coordinates and
        // returns its ancestor chain back to the caller.
        [HubMethodName(name: $"{nameof(SendPeek)}At")]
        public Task SendPeek(RecorderPointModel point)
        {
            // Query the repository to get the UIA ancestor chain at the given coordinates.
            var peekResponse = _repository.Peek(x: point.XPos, y: point.YPos);

            // Send the result back to the calling client.
            return Clients.Caller.SendAsync(
                method: "ReceivePeek",
                arg1: new HubResponseModel(peekResponse));
        }

        // Resolves the UIA element at the given screen coordinates and
        // returns its ancestor chain back to the caller.
        [HubMethodName(name: $"{nameof(SendPeek)}Focused")]
        public Task SendPeek()
        {
            // Query the repository to get the UIA ancestor chain from the currently focused element.
            var peekResponse = _repository.Peek();

            // Send the result back to the calling client.
            return Clients.Caller.SendAsync(
                method: "ReceivePeek",
                arg1: new HubResponseModel(peekResponse));
        }

        /// <summary>
        /// Lightweight envelope for hub-to-client messages that carry a single value.
        /// </summary>
        /// <param name="value">The payload to send to the client.</param>
        private sealed class HubResponseModel(object value)
        {
            /// <summary>
            /// The payload carried by this response. Using <see cref="object"/> allows
            /// any serializable value (string, number, DTO, etc.).
            /// </summary>
            public object Value { get; init; } = value;
        }
    }
}
