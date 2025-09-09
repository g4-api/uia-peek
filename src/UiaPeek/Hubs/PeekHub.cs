using Microsoft.AspNetCore.SignalR;

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using UiaPeek.Domain;
using UiaPeek.Domain.Models;

namespace UiaPeek.Hubs
{
    /// <summary>
    /// SignalR hub for handling UI Automation (UIA) peek operations.
    /// Provides real-time communication for heartbeat checks and
    /// ancestor chain inspection at specific screen coordinates.
    /// </summary>
    public class PeekHub(UiaPeekRepository repository) : Hub
    {
        // Collection of active recording sessions keyed by a unique session id.
        private readonly static ConcurrentDictionary<string, ConcurrentBag<UiaChainModel>> s_sessions = new();

        // Repository used for querying UIA elements at coordinates.
        private readonly UiaPeekRepository _repository = repository;

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
        public Task SendPeek(UiaPointModel point)
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

        // Starts a new recording session for the current SignalR caller.
        [HubMethodName(name: $"{nameof(StartRecordingSession)}")]
        public Task StartRecordingSession()
        {
            // Generate a unique identifier for this caller's recording session.
            var session = Guid.NewGuid().ToString();

            // Initialize storage for this session's recorded events/actions.
            // Assumes `_sessions` is a (thread-safe) dictionary keyed by session id.
            s_sessions[session] = [];

            // Notify ONLY the invoking client that the session has started and
            // return the session id as the payload. The client should listen to
            // "RecordingSessionStarted" and extract the `Value` field.
            return Clients.Caller.SendAsync(
                method: "RecordingSessionStarted",
                arg1: new HubResponseModel(session));
        }

        // Stops an existing recording session for the current SignalR caller.
        [HubMethodName(name: $"{nameof(StopRecordingSession)}")]
        public Task StopRecordingSession(string session)
        {
            // Remove the session from the active sessions collection.
            s_sessions.TryRemove(session, out var chains);

            // Notify ONLY the invoking client that the session has stopped.
            return Clients.Caller.SendAsync(
                method: "RecordingSessionStopped",
                arg1: new HubResponseModel(chains));
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
