using Microsoft.AspNetCore.SignalR;

using System;
using System.IO;
using System.Threading.Tasks;

using Common.Domain.Models;
using ChromiumPeek.Domain.Middlewares;
using ChromiumPeek.Domain.Models;

namespace ChromiumPeek.Domain.Hubs
{
    /// <summary>
    /// SignalR hub for handling UI Automation (UIA) peek operations.
    /// Provides real-time communication for heartbeat checks and
    /// ancestor chain inspection at specific screen coordinates.
    /// </summary>
    /// <param name="domain">Domain aggregate exposing the launcher and repository.</param>
    /// <param name="eventCapture">Service that broadcasts recorder events to consumers.</param>
    public class ChromiumPeekHub(IChromiumPeekDomain domain, IChromiumEventCaptureService eventCapture) : Hub
    {
        // Server-to-client method the recorder extension listens on (via hubConnection.on) to
        // close its browser windows for a graceful stop. Must match the name the extension
        // registers; changing it requires updating the extension's constants in lockstep.
        public const string CloseBrowserClientMethod = "CloseBrowser";

        // Domain aggregate exposing the repository used for querying UIA elements at coordinates.
        private readonly IChromiumPeekDomain _domain = domain;

        // Service that owns the broadcast of recorder events to connected consumers.
        private readonly IChromiumEventCaptureService _eventCapture = eventCapture;

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
            var peekResponse = _domain.Repository.Peek(x: point.XPos, y: point.YPos);

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
            var peekResponse = _domain.Repository.Peek();

            // Send the result back to the calling client.
            return Clients.Caller.SendAsync(
                method: "ReceivePeek",
                arg1: new HubResponseModel(peekResponse));
        }

        // Relays a recording event pushed by a producer (for example, the Chromium
        // recorder extension) to every connected consumer. The browser extension cannot
        // host a socket, so it connects as a client and invokes this method; the capture
        // service re-broadcasts the event using the same "ReceiveRecordingEvent" message and
        // envelope as the desktop capture service, preserving the UiaPeek contract.
        [HubMethodName(name: nameof(SendRecordingEvent))]
        public Task SendRecordingEvent(ChromiumEventModel recordingEvent)
        {
            // Delegate the fan-out to the capture service so the broadcast envelope lives in one
            // place and stays identical to the UIA broadcast.
            return _eventCapture.BroadcastRecordingEventAsync(recordingEvent);
        }

        // Launches a Chromium browser with the recorder extension loaded and returns its
        // operating-system process id to the calling client, which uses it to stop the browser
        // later. Replaces the former REST endpoint so start/stop travel over the same SignalR
        // connection that carries recording events.
        [HubMethodName(name: nameof(StartRecorder))]
        public int StartRecorder(DriverParametersModel driverParameters)
        {
            // Surface invalid input (missing binary, missing extension) as a HubException so the
            // caller's invoke promise rejects with a clean message instead of a generic failure.
            try
            {
                return _domain.Launcher.Start(driverParameters);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
            {
                throw new HubException(e.Message);
            }
        }

        // Stops a browser previously started through StartRecorder. The launcher first asks the
        // extension to close the browser gracefully, then forces a kill if it does not exit.
        [HubMethodName(name: nameof(StopRecorder))]
        public Task<bool> StopRecorder(int processId)
        {
            return _domain.Launcher.StopAsync(processId);
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
