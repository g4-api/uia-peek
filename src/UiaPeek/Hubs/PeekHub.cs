using Microsoft.AspNetCore.SignalR;

using System.Threading.Tasks;

namespace UiaPeek.Hubs
{
    public class PeekHub : Hub
    {
        [HubMethodName(name: nameof(SendHeartbeat))]
        public Task SendHeartbeat()
        {
            return Clients.Caller.SendAsync("ReceiveHeartbeat", "");
        }

        [HubMethodName(name: nameof(Peek))]
        public Task Peek(int x, int y)
        {
            return Clients.Caller.SendAsync("Peek", "");
        }
    }
}
