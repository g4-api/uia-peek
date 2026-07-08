# UiaPeek — UI Automation & Chromium Recording Tools

[![Build & Release](https://github.com/g4-api/uia-peek/actions/workflows/release-pipline.yml/badge.svg)](https://github.com/g4-api/uia-peek/actions/workflows/release-pipline.yml)

## Table of Contents

* [What is this?](#what-is-this)
* [The Two Tools at a Glance](#the-two-tools-at-a-glance)
* [Quick Start](#quick-start)
  * [Download and Run](#download-and-run)
  * [Verify the Services](#verify-the-services)
* [Recording Event Contract (shared)](#recording-event-contract-shared)
* [UiaPeek — Desktop Recorder](#uiapeek--desktop-recorder)
  * [Command-Line Usage](#command-line-usage)
  * [cURL API Usage](#curl-api-usage)
  * [SignalR Hub Usage](#signalr-hub-usage)
* [ChromiumPeek — Browser Recorder](#chromiumpeek--browser-recorder)
  * [How It Works](#how-it-works)
  * [SignalR Hub Usage (ChromiumPeek)](#signalr-hub-usage-chromiumpeek)
  * [Launching and Stopping a Browser](#launching-and-stopping-a-browser)
  * [Health Check](#health-check)
* [SignalR Client Examples](#signalr-client-examples)
  * [Python Example](#python-example)
  * [JavaScript Example](#javascript-example)
  * [C# Example](#c-example)
* [PeekClient — Monitor Both Hubs](#peekclient--monitor-both-hubs)
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

## What is this?

This repository provides **two recording tools that share one event contract**:

* **UiaPeek** — a Windows **UI Automation** inspection and recording tool. It peeks at desktop UI elements (returning their **ancestor chain**, like an XPath for Windows UI) and records global keyboard and mouse events enriched with UI context.
* **ChromiumPeek** — a **browser** recording tool. A bundled Chromium **recorder extension** captures in-page interactions (with a DOM locator chain) and streams them to a hub, which relays them to consumers.

Both tools **broadcast the same `ReceiveRecordingEvent` message over SignalR**, so a consumer can watch desktop and browser interactions through a single, uniform contract.

Use cases:

* Debug and inspect desktop applications and web pages to troubleshoot UI behavior.
* Build automated UI tests or robotic process automation (RPA) workflows.
* Record user input with UI context for replay or analysis.
* Monitor UI and user actions in real time.

---

## The Two Tools at a Glance

| Tool           | Records                          | Hub port | How recording starts                         | REST surface   |
| -------------- | -------------------------------- | -------- | -------------------------------------------- | -------------- |
| **UiaPeek**    | Windows desktop (global input)   | `9955`   | Automatically (always-on capture)            | `peek` + `ping` |
| **ChromiumPeek** | Chromium browser (via extension) | `9956`   | When a recorder browser is running           | `ping` only    |

Both hubs share the path `/hub/v4/g4/peek` and emit the same `ReceiveRecordingEvent` payload (see [Recording Event Contract](#recording-event-contract-shared)).

---

## Quick Start

> **Requirements:** .NET 10 Runtime/SDK. UiaPeek requires **Windows** (and Administrator privileges are recommended for global input hooks). ChromiumPeek additionally requires a **Chromium/Chrome** browser with the recorder extension loaded.

### Download and Run

1. Go to **Releases** and download the latest artifact:
   👉 **Releases:** [https://github.com/g4-api/uia-peek/releases](https://github.com/g4-api/uia-peek/releases)
2. Unzip the archive to a folder with write permissions (e.g., `C:\Tools\UiaPeek`).
3. Run the executable(s) from that folder:
   * `UiaPeek.exe` — desktop recorder + peek API (listens on `9955`). Run **as administrator** for global hooks.
   * `ChromiumPeek.exe` — browser recorder hub (listens on `9956`).

### Verify the Services

Each service exposes a Swagger UI and a health-check endpoint:

```bash
# UiaPeek
curl http://localhost:9955/api/v4/g4/ping
# Output: Pong

# ChromiumPeek
curl http://localhost:9956/api/v4/g4/ping
# Output: Pong
```

* UiaPeek Swagger: `http://localhost:9955/swagger`
* ChromiumPeek Swagger: `http://localhost:9956/swagger`

> **CORS tip:** If you call from a webview or browser, ensure your origin is allowed via the `ORIGINS` env var or `AllowedOrigins` config. VS Code webviews (`vscode-webview://`) and the Chromium recorder extension (`chrome-extension://`) are already handled.

---

## Recording Event Contract (shared)

Both tools broadcast recorded interactions to connected clients using the SignalR message **`ReceiveRecordingEvent`**. The payload is always wrapped in a `{ "value": { ... } }` envelope with this shape:

* `chain` — the element chain (locator + ordered `path` of nodes) where the event occurred.
* `event` — the specific event name (e.g., `Down`, `Click`).
* `machineName` — the machine that recorded the event.
* `timestamp` — Unix epoch **milliseconds**.
* `type` — the event category (e.g., `Keyboard`, `Mouse`).
* `value` — the event payload (e.g., the pressed key, or coordinates).

**Sample `ReceiveRecordingEvent` payload (UiaPeek keyboard event):**

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
                    "bounds": { "height": 519, "X": 104, "Y": 104, "width": 993 },
                    "controlTypeId": 50030,
                    "controlType": "Document",
                    "isTopWindow": false,
                    "isTriggerElement": true,
                    "name": "Text Area",
                    "processId": 31036
                }
            ],
            "trigger": "Focus"
        },
        "event": "Down",
        "timestamp": 1757618366611,
        "type": "Keyboard",
        "value": { "scanCode": 30, "virtualKey": 65, "key": "a" }
    }
}
```

> **Note:** The `path` array is ordered **top-down**. The **last element** is always the **target element** (the trigger) and carries more metadata than its ancestors to keep the payload compact. ChromiumPeek events use the same top-level shape; only the chain **node contents** differ (DOM elements instead of UIA elements).

---

## UiaPeek — Desktop Recorder

UiaPeek listens on **`http://localhost:9955`**. Recording events flow **automatically** as soon as the service is running — there is no session to start; move the mouse or type and events are broadcast to every connected client.

### Command-Line Usage

```bash
# Peek at focused element
uiapeek peek -f

# Peek at specific coordinates
uiapeek peek -x 100 -y 200
```

### cURL API Usage

The **PeekController** provides REST endpoints to peek at UI elements (UiaPeek only).

```bash
# Peek at specific coordinates (x=250, y=300)
curl "http://localhost:9955/api/v4/g4/peek?x=250&y=300"

# Peek at the currently focused element
curl "http://localhost:9955/api/v4/g4/peek?focused=true"
```

The response is the ancestor `chain` object (same shape as the `chain` field in a recording event).

### SignalR Hub Usage

**Hub URL:** `http://localhost:9955/hub/v4/g4/peek`

**Client → Server Methods**

* `SendHeartbeat()`
* `SendPeekAt({ xPos, yPos })`
* `SendPeekFocused()`

**Server → Client Events**

* `ReceiveHeartbeat` — heartbeat confirmation
* `ReceivePeek` — ancestor chain result
* `ReceiveRecordingEvent` — keyboard/mouse events with UI context (broadcast automatically)

See [SignalR Client Examples](#signalr-client-examples) for Python, JavaScript, and C# clients.

---

## ChromiumPeek — Browser Recorder

ChromiumPeek listens on **`http://localhost:9956`**. Unlike UiaPeek, it does not capture anything by itself — a **Chromium recorder extension** running in a browser produces the events.

### How It Works

1. A client asks ChromiumPeek to launch a browser (`StartRecorder`), or you start a browser that already has the recorder extension loaded.
2. The extension connects to the hub and pushes in-page interactions via `SendRecordingEvent`.
3. The hub re-broadcasts each interaction to every connected consumer as `ReceiveRecordingEvent` — the **same** message and envelope UiaPeek uses.
4. On `StopRecorder`, the hub asks the extension to close the browser gracefully (a `CloseBrowser` message), then force-stops the process if it does not exit in time.

> The extension has its own setup and usage guide: **[`src/ChromiumPeek.Extension/README.md`](src/ChromiumPeek.Extension/README.md)**.

### SignalR Hub Usage (ChromiumPeek)

**Hub URL:** `http://localhost:9956/hub/v4/g4/peek`

**Client → Server Methods**

* `StartRecorder(driverParameters)` → returns the launched browser **process id**
* `StopRecorder(processId)` → returns `true` if a tracked browser was stopped, `false` if the id is unknown/already closed
* `SendHeartbeat()`
* `SendRecordingEvent(event)` — used by the **extension** (producer); consumers do not call this

**Server → Client Events**

* `ReceiveHeartbeat` — heartbeat confirmation
* `ReceiveRecordingEvent` — browser interactions with a DOM chain
* `CloseBrowser` — sent to the **extension** to close the browser during a graceful stop

> **No REST peek:** ChromiumPeek is controlled entirely over SignalR. The only REST endpoint is the `ping` health check.

### Launching and Stopping a Browser

`StartRecorder` takes a W3C-style driver-parameters object; only the Chromium binary and optional arguments are used:

```json
{
    "capabilities": {
        "alwaysMatch": {
            "goog:chromeOptions": {
                "binary": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
                "args": ["--disable-gpu"]
            }
        }
    }
}
```

Each launch gets its own dedicated user-data directory, so it never collides with an already-running browser. Keep the returned process id to stop it later with `StopRecorder(processId)`.

### Health Check

```bash
curl http://localhost:9956/api/v4/g4/ping
# Output: Pong
```

---

## SignalR Client Examples

The **listening** pattern is identical for both hubs — only the URL changes (`9955` for UiaPeek, `9956` for ChromiumPeek). The Chromium-only `StartRecorder`/`StopRecorder` calls are shown alongside.

### Python Example

Using the community client **signalrcore**.

```bash
pip install signalrcore
```

```python
from signalrcore.hub_connection_builder import HubConnectionBuilder

# Use 9955 for UiaPeek or 9956 for ChromiumPeek.
hub = (
    HubConnectionBuilder()
    .with_url("http://localhost:9955/hub/v4/g4/peek")
    .with_automatic_reconnect({"type": "raw", "keep_alive_interval": 10, "reconnect_interval": 5})
    .build()
)

def on_heartbeat(payload):
    print("Heartbeat:", payload)

def on_peek(payload):
    print("Peek:", payload)

def on_recording_event(payload):
    event = (payload[0] if isinstance(payload, list) else payload).get("value", {})
    print(f"RecordingEvent: type={event.get('type')} event={event.get('event')} value={event.get('value')}")

hub.on("ReceiveHeartbeat", on_heartbeat)
hub.on("ReceivePeek", on_peek)
hub.on("ReceiveRecordingEvent", on_recording_event)

hub.start()

# UiaPeek: events flow automatically. Optional peek/heartbeat calls:
hub.send("SendHeartbeat", [])
hub.send("SendPeekFocused", [])

# ChromiumPeek only: launch a recorder browser (events start once it is recording).
# hub.send("StartRecorder", [{
#     "capabilities": {"alwaysMatch": {"goog:chromeOptions": {
#         "binary": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "args": []
#     }}}
# }])

input("Press <Enter> to stop...\n")
hub.stop()
```

### JavaScript Example

Using **@microsoft/signalr** (browser or Node.js).

```bash
npm i @microsoft/signalr
```

```javascript
import * as signalR from "@microsoft/signalr";

// Use 9955 for UiaPeek or 9956 for ChromiumPeek.
const connection = new signalR.HubConnectionBuilder()
    .withUrl("http://localhost:9955/hub/v4/g4/peek")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveHeartbeat", (payload) => console.log("Heartbeat:", payload));
connection.on("ReceivePeek", (payload) => console.log("Peek chain:", payload?.value));
connection.on("ReceiveRecordingEvent", (payload) => {
    const event = payload?.value || {};
    console.log("RecordingEvent:", event.type, event.event, event.value);
});

async function main() {
    await connection.start();

    // UiaPeek: events flow automatically. Optional peek/heartbeat:
    await connection.invoke("SendHeartbeat");
    await connection.invoke("SendPeekFocused");

    // ChromiumPeek only: launch and later stop a recorder browser.
    // const processId = await connection.invoke("StartRecorder", {
    //     capabilities: { alwaysMatch: { "goog:chromeOptions": {
    //         binary: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", args: []
    //     } } }
    // });
    // await connection.invoke("StopRecorder", processId);
}

main().catch(console.error);
```

### C# Example

Using **Microsoft.AspNetCore.SignalR.Client**.

```bash
dotnet add package Microsoft.AspNetCore.SignalR.Client
```

```csharp
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

// Use 9955 for UiaPeek or 9956 for ChromiumPeek.
var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:9955/hub/v4/g4/peek")
    .WithAutomaticReconnect()
    .Build();

connection.On<JsonElement>("ReceiveRecordingEvent", envelope =>
{
    var recordingEvent = envelope.GetProperty("value");
    var type = recordingEvent.GetProperty("type").GetString();
    var name = recordingEvent.GetProperty("event").GetString();
    Console.WriteLine($"RecordingEvent: {type} / {name}");
});

await connection.StartAsync();

// UiaPeek: events flow automatically. Optional peek/heartbeat:
await connection.InvokeAsync("SendHeartbeat");
await connection.InvokeAsync("SendPeekFocused");

// ChromiumPeek only: launch and later stop a recorder browser.
// var driverParameters = new
// {
//     capabilities = new
//     {
//         alwaysMatch = new Dictionary<string, object?>
//         {
//             ["goog:chromeOptions"] = new { binary = @"C:\Program Files\Google\Chrome\Application\chrome.exe", args = Array.Empty<string>() }
//         }
//     }
// };
// var processId = await connection.InvokeAsync<int>("StartRecorder", driverParameters);
// var isStopped = await connection.InvokeAsync<bool>("StopRecorder", processId);

Console.WriteLine("Press <Enter> to exit...");
Console.ReadLine();
```

---

## PeekClient — Monitor Both Hubs

**PeekClient** (`src/PeekClient`) is a .NET 10 console test harness that connects to **both** hubs at once and prints every recorder event to the console. It is the quickest way to watch desktop and browser events side by side.

```bash
cd src/PeekClient
dotnet run                # compact colored line per event
dotnet run -- --verbose   # full JSON per event
```

* **Choose sources** — toggle each hub in `appsettings.json` (`Hubs:UiaEnabled` / `Hubs:ChromiumEnabled`) or at the command line (`--no-uia`, `--no-chromium`, `--uia`, `--chromium`).
* **Drive Chromium** — set `Chromium:BinaryPath` in `appsettings.json`, then press `s` to launch a recorder browser and `x` to stop it.
* **Keys** — `s` start Chromium · `x` stop Chromium · `v` toggle verbose · `h` help · `q` quit.

UIA events appear as soon as you move/click/type; Chromium events appear once a recorder browser is running.

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
  │  [ ▶ Start / ⬛ Stop]                                │
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
