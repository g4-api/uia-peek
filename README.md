# UiaPeek — UI Automation Peek & Record Tool

## Table of Contents

* [What is UiaPeek?](#what-is-uiapeek)
* [Quick Start](#quick-start)
  * [Download and Run](#download-and-run)
  * [Verify the Service](#verify-the-service)
* [Comprehensive Usage](#comprehensive-usage)
  * [Command-Line Usage](#command-line-usage)
  * [cURL API Usage (PeekController)](#curl-api-usage-peekcontroller)
  * [SignalR Hub Usage](#signalr-hub-usage)
    * [Python Example](#python-example)
    * [JavaScript Example](#javascript-example)
    * [C# Example](#c-example)
* [UiaPeek Path Finder — Quick Start](#uiapeek-path-finder--quick-start)
  * [Requirements](#requirements)
  * [Launch](#launch)
  * [Start peeking](#start-peeking)
  * [Adjust update speed](#adjust-update-speed)
  * [Copy the locator](#copy-the-locator)
  * [Stop peeking](#stop-peeking)
  * [Typical workflow](#typical-workflow)
  * [Tips](#tips)
  * [Troubleshooting](#troubleshooting)
  * [Keyboard & accessibility](#keyboard--accessibility)
  * [Exit](#exit)
* [License](#license)

---

## What is UiaPeek?

**UiaPeek** is a Windows UI Automation **inspection and recording tool**. It provides the ability to:

* **Peek**: Inspect UI elements at screen coordinates or the currently focused element and return their **ancestor chain** (like an XPath for Windows UI).
* **Record**: Capture global keyboard and mouse events in real time, enriched with UI context, and broadcast them to connected clients.
* **Expose**: Provide results via REST APIs, a SignalR hub stream, or a command-line interface.

Use cases:

* Debug and inspect Windows desktop applications to troubleshoot UI behavior and layout issues.
* Create automated UI tests or implement advanced robotic process automation (RPA) workflows.
* Recording user input with UI context for replay or analysis.
* Real-time monitoring of UI and user actions.

---

## Quick Start

> **Requirements:** Windows OS, .NET 8 Runtime/SDK. Administrator privileges are recommended for global input hooks.

### Download and Run

1. Go to **Releases** and download the latest artifact for your platform:
   👉 **Releases:** [https://github.com/g4-api/uia-peek/releases](https://github.com/g4-api/uia-peek/releases)
2. Unzip the archive to a folder with write permissions (e.g., `C:\Tools\UiaPeek`).
3. Run - as administrator - the executable (`UiaPeek.exe`) from that folder.

### Verify the Service

Once running:

* Open Swagger: `http://localhost:9955/swagger`
* Health check:

```bash
curl http://localhost:9955/api/v4/g4/ping
# Output: Pong
```

> **CORS tip:** If you call from a webview or browser, ensure your origin is allowed via `ORIGINS` env var or `AllowedOrigins` config. VS Code webviews start with `vscode-webview://` which is already handled.

---

## Comprehensive Usage

### Command-Line Usage

```bash
# Peek at focused element
uiapeek peek -f

# Peek at specific coordinates
uiapeek peek -x 100 -y 200
```

---

### cURL API Usage (PeekController)

The **PeekController** provides REST endpoints to peek at UI elements.

```bash
# Peek at specific coordinates (x=250, y=300)
curl "http://localhost:9955/api/v4/g4/peek?x=250&y=300"

# Peek at the currently focused element
curl "http://localhost:9955/api/v4/g4/peek?focused=true"
```

**Sample JSON Response:**

```json
{
    "value": {
        "chain": {
            "locator": "/Desktop/Window[@Name='...']/Document[@Name='Text Area']",
            "path": [
                {
                    "automationId": "Console Window",
                    "className": "ConsoleWindowClass",
                    "controlTypeId": 50032,
                    "controlType": "Window",
                    "isTopWindow": true,
                    "isTriggerElement": false,
                    "name": "...exe",
                    "processId": 31036
                },
                {
                    "automationId": "Text Area",
                    "bounds": {
                        "height": 519,
                        "X": 104,
                        "Y": 104,
                        "width": 993
                    },
                    "controlTypeId": 50030,
                    "controlType": "Document",
                    "isTopWindow": false,
                    "isTriggerElement": true,
                    "machine": {
                        "name": "DESKTOP-12345",
                        "publicAddress": "172.23.32.1"
                    },
                    "name": "Text Area",
                    "patterns": [
                        {
                            "id": 10018,
                            "name": "LegacyIAccessible"
                        },
                        {
                            "id": 10014,
                            "name": "Text"
                        }
                    ],
                    "processId": 31036,
                    "runtimeId": [
                        42,
                        590408,
                        4,
                        -1
                    ]
                }
            ],
            "topWindow": {
                "automationId": "Console Window",
                "className": "ConsoleWindowClass",
                "controlTypeId": 50032,
                "controlType": "Window",
                "isTopWindow": true,
                "isTriggerElement": false,
                "name": "...exe",
                "processId": 31036
            },
            "trigger": "Focus"
        },
        "event": "Down",
        "timestamp": 1757618366611,
        "type": "Keyboard",
        "value": {
            "scanCode": 30,
            "virtualKey": 65,
            "key": "a"
        }
    }
}
```

> **Note:** The `path` array is ordered **top-down**. The **last element** in the path is always the **target element** (the trigger). This element includes **more metadata** than its ancestors to reduce payload size and avoid bloating.

---

### SignalR Hub Usage

**Hub URL:** `http://localhost:9955/hub/v4/g4/peek`

**Client → Server Methods**

* `SendHeartbeat()`
* `SendPeekAt({ x, y })`
* `SendPeekFocused()`
* `StartRecordingSession()`
* `StopRecordingSession(sessionId)`

**Server → Client Events**

* `ReceiveHeartbeat` — heartbeat confirmation
* `ReceivePeek` — ancestor chain result
* `ReceiveRecordingEvent` — keyboard/mouse events with UI context

> **Note:** Recording events flow only after `StartRecordingSession()`.

---

#### Python Example

Using the community client **signalrcore**.

```bash
pip install signalrcore
```

```python
from signalrcore.hub_connection_builder import HubConnectionBuilder

hub = (
    HubConnectionBuilder()
    .with_url("http://localhost:9955/hub/v4/g4/peek")
    .build()
)

# ---- Wire server -> client callbacks ----

def on_heartbeat(payload):
    print("Heartbeat:", payload)

def on_peek(payload):
    print("Peek:", payload)

def on_recording_event(payload):
    ev = payload.get("value", {})
    print(f"RecordingEvent: type={ev.get('type')} event={ev.get('event')} value={ev.get('value')}")

hub.on("ReceiveHeartbeat", on_heartbeat)
hub.on("ReceivePeek", on_peek)
hub.on("ReceiveRecordingEvent", on_recording_event)

hub.start()

# ---- Call client -> server hub methods ----
hub.send("SendHeartbeat", [])
hub.send("SendPeekAt", [{"xPos": 250, "yPos": 300}])
hub.send("SendPeekFocused", [])
hub.send("StartRecordingSession", [])

input("Press <Enter> to stop...\n")
# If you captured a session id from RecordingSessionStarted, you can stop it:
# hub.send("StopRecordingSession", [session_id])

hub.stop()
```

---

#### JavaScript Example

Using **@microsoft/signalr** (browser or Node.js).

```bash
npm i @microsoft/signalr
```

```javascript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("http://localhost:9955/hub/v4/g4/peek")
    .withAutomaticReconnect()
    .build();

// ---- Wire server -> client callbacks ----
connection.on("ReceiveHeartbeat", (payload) => {
    console.log("Heartbeat:", payload);
});

connection.on("ReceivePeek", (payload) => {
    console.log("Peek chain:", payload?.value);
});

connection.on("ReceiveRecordingEvent", (payload) => {
    const ev = payload?.value || {};
    console.log("RecordingEvent:", ev.type, ev.event, ev.value);
});

async function main() {
    await connection.start();
    await connection.invoke("SendHeartbeat");
    await connection.invoke("SendPeekAt", { xPos: 250, yPos: 300 });
    await connection.invoke("SendPeekFocused");
    await connection.invoke("StartRecordingSession");
}

main().catch(console.error);
```

---

#### C# Example

Using **Microsoft.AspNetCore.SignalR.Client**.

```bash
# .NET CLI
dotnet add package Microsoft.AspNetCore.SignalR.Client
```

```csharp
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:9955/hub/v4/g4/peek")
            .WithAutomaticReconnect()
            .Build();

        // ---- Wire server -> client callbacks ----
        connection.On<object>("ReceiveHeartbeat", payload =>
        {
            Console.WriteLine($"Heartbeat: {payload}");
        });

        connection.On<object>("ReceivePeek", payload =>
        {
            Console.WriteLine("Peek chain received");
            Console.WriteLine(payload);
        });

        connection.On<object>("ReceiveRecordingEvent", payload =>
        {
            Console.WriteLine($"RecordingEvent: {payload}");
        });

        await connection.StartAsync();
        await connection.InvokeAsync("SendHeartbeat");
        await connection.InvokeAsync("SendPeekAt", new { xPos = 250, yPos = 300 });
        await connection.InvokeAsync("SendPeekFocused");
        await connection.InvokeAsync("StartRecordingSession");

        Console.WriteLine("Press <Enter> to exit...");
        Console.ReadLine();
    }
}
```
---

## UiaPeek Path Finder — Quick Start

UiaPeek Path Finder shows the UI-Automation path (an XPath-like locator) for whatever is currently under your mouse pointer. Hover any app control; the locator appears in the text box for copy/paste into your automation or QA tools.

### Requirements

* Windows desktop.
* If you need to peek elevated apps, run Path Finder **as Administrator**.

### Launch

* Open **UiaPeek Path Finder v1.0** by running `UiaPeek.PathFinder.exe`.
* You’ll see a title, a locator text box, a **Start/Stop** button, and a **Faster/Slower** slider.

  ```none
  ╭── UiaPeek — Title ────────────────────────────────────╮
  │                                                       │
  │  Locator                                              │
  │  ┌─────────────────────────────────────────────────┐  │
  │  │                                                 │  │
  │  └─────────────────────────────────────────────────┘  │
  │                                                       │
  │  [ ▶ Start / ⬛ Stop]                                  │
  │                                                       │
  │  Faster  ◄───────┈┈┈┈┈●┈┈┈┈────────►  Slower          │
  │                                                       │
  ╰───────────────────────────────────────────────────────╯
  ```

### Start peeking

* Click **▶ Start** (or press **Alt+S**).
* Move your mouse over the target application.
* The locator appears and updates in the text box.

### Adjust update speed

* Use the **Faster ←→ Slower** slider to change refresh rate.
* Faster ≈ \~500 ms; Slower ≈ \~3000 ms.
* Faster feels live; slower reduces CPU and makes copying easier.

### Copy the locator

* Select the locator in the text box and press **Ctrl+C**.
* Paste into your test or automation scripts.
* If the text changes while selecting, press **⬛ Stop** first, then copy.

### Stop peeking

* Click **⬛ Stop** (or **Alt+S**) to pause tracking.

### Typical workflow

* Open the target app.
* Start Path Finder and hover precisely over the desired control (button, textbox, menu item).
* Adjust the slider if updates are too fast or too slow.
* Stop, copy the locator, and paste where needed.

### Tips

* Be precise: tiny mouse movements can change the target element.
* Hover the actionable sub-area (icon/text) if a widget is composite.
* Keep the Path Finder window out of the target area to avoid peeking it.
* Run as Administrator when inspecting admin-level windows.

### Troubleshooting

* **No updates**: ensure you pressed **Start**; try a slower rate; run as Administrator for elevated apps.
* **Hard to copy**: press **Stop**, then copy; or slide toward **Slower**.
* **Unexpected element**: re-hover the exact clickable region; complex controls may have nested elements.

### Keyboard & accessibility

* **Alt+S** toggles Start/Stop.
* **Ctrl+C** copies the locator from the text box.

### Exit

* Close the window (X or Alt+F4). Any active tracking stops automatically.

---

## License

MIT License. See `LICENSE` for details.
