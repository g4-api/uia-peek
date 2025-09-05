using Microsoft.AspNetCore.SignalR;

using System.Threading.Tasks;

using UiaPeek.Domain;
using UiaPeek.Models;

namespace UiaPeek.Hubs
{
    /// <summary>
    /// SignalR hub for handling UI Automation (UIA) peek operations.
    /// Provides real-time communication for heartbeat checks and
    /// ancestor chain inspection at specific screen coordinates.
    /// </summary>
    public class PeekHub(UiaPeekRepository repository) : Hub
    {
        // Repository used for querying UIA elements at coordinates.
        private readonly UiaPeekRepository _repository = repository;

        // Sends a heartbeat message to the caller.
        // This can be used by clients to verify the connection is alive.
        [HubMethodName(name: nameof(SendHeartbeat))]
        public Task SendHeartbeat()
        {
            // Notify the calling client with a heartbeat message.
            return Clients.Caller.SendAsync("ReceiveHeartbeat", "Heartbeat received - connection is alive");
        }

        // Resolves the UIA element at the given screen coordinates and
        // returns its ancestor chain back to the caller.
        [HubMethodName(name: $"{nameof(SendPeek)}At")]
        public Task SendPeek(UiaPointModel point)
        {
            // Query the repository to get the UIA ancestor chain at the given coordinates.
            var peekResponse = _repository.Peek(x: point.XPos, y: point.YPos);

            // Send the result back to the calling client.
            return Clients.Caller.SendAsync("ReceivePeek", peekResponse);
        }

        // Resolves the UIA element at the given screen coordinates and
        // returns its ancestor chain back to the caller.
        [HubMethodName(name: $"{nameof(SendPeek)}Focused")]
        public Task SendPeek()
        {
            // Query the repository to get the UIA ancestor chain from the currently focused element.
            var peekResponse = _repository.Peek();

            // Send the result back to the calling client.
            return Clients.Caller.SendAsync("ReceivePeek", peekResponse);
        }
    }
}
