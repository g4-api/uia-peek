using ChromiumPeek.Domain.Models;

using System.Threading.Tasks;

namespace ChromiumPeek.Domain.Middlewares
{
    /// <summary>
    /// Broadcasts Chromium recorder events to connected SignalR consumers.
    /// </summary>
    /// <remarks>
    /// This is the Chromium counterpart to the desktop <c>UiaEventCaptureService</c>. Unlike the
    /// UIA service it does not capture input: the browser extension already builds the full event
    /// (including the DOM chain) and pushes it to the hub, so this service only owns the broadcast
    /// step. It exists so the "ReceiveRecordingEvent" envelope is produced in one place and stays
    /// byte-shaped identically to the UIA broadcast, letting consumers treat both sources uniformly.
    /// </remarks>
    public interface IChromiumEventCaptureService
    {
        /// <summary>
        /// Broadcasts a recorder event to every connected consumer using the shared
        /// "ReceiveRecordingEvent" message and envelope.
        /// </summary>
        /// <param name="recordingEvent">The event pushed by the recorder extension.</param>
        /// <returns>A task that completes once the event has been handed to the transport.</returns>
        Task BroadcastRecordingEventAsync(ChromiumEventModel recordingEvent);
    }
}
