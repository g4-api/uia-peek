# G4 Chromium Recorder

A vanilla-JavaScript Manifest V3 browser extension that records interactions in the
browser and streams them to the G4 **ChromiumPeek** SignalR hub using the same response
contract as the **UiaPeek** desktop recorder. A consumer client connected to the hub
receives each interaction as a `ReceiveRecordingEvent` broadcast whose payload is shaped
exactly like a `UiaEventModel`.

The extension is fully offline: the official Microsoft SignalR client is vendored into
the project and no script is ever fetched from a network at runtime.

## How it works

```text
DOM event (any frame)
  -> content-script.js builds a RecorderEventModel (XPath locator + CSS, ARIA mapping)
  -> chrome.runtime message to the background service worker
  -> background.js invokes SendRecordingEvent on the hub (SignalR over WebSocket)
  -> C# ChromiumPeekHub re-broadcasts ReceiveRecordingEvent { value: <event> }
  -> consumer clients receive the event
```

Recording is **always-on while connected**. Each successful connect (including an
automatic reconnect) starts a new session and clears the captured-event stack shown in
the popup.

## Contract

The payload matches the UiaPeek/ChromiumPeek C# models (camelCase on the wire):

- `RecorderEventModel` — `{ chain, event, machineName, offset, timestamp, type, value }`
- `ChainModel` — `{ locator, path[], point, topWindow, trigger }`
- `RecorderNodeModel` — `{ automationId, bounds, className, controlTypeId, controlType,
  frameworkId, isTopWindow, isTriggerElement, machine, name, patterns[], processId,
  properties, runtimeId[] }`
- `BoundsRectangle` — `{ height, width, X, Y }` (X = Left, Y = Top)
- `RecorderPointModel` — `{ X, Y }`
- `RecorderOffsetModel` — `{ x, y }`, currently emitted as the shared Chromium default `{ x: 0, y: 0 }`

### DOM → contract mapping

| Contract field | Source in the browser |
| --- | --- |
| `locator` | absolute XPath of the trigger element |
| `properties.cssSelector` | unique CSS selector of the element |
| `controlType` | ARIA `role`, else the tag name |
| `automationId` | element `id`, else `data-test-id` |
| `name` | accessible name (aria-label, aria-labelledby, alt/title/placeholder, text) |
| `className` | the element `class` attribute |
| `bounds` | `getBoundingClientRect()` |
| `machine.name` / `machine.publicAddress` | page hostname / page origin |

UI Automation-only fields (`controlTypeId`, `frameworkId`, `patterns`, `processId`,
`runtimeId`) have no DOM equivalent and are emitted as empty/zero defaults so the JSON
shape stays identical.

### Captured events

Event names follow the G4 action style.

Interaction events:

| `event` | Trigger | `type` |
| --- | --- | --- |
| `InvokeClick` | click | Mouse |
| `InvokeDoubleClick` | double click | Mouse |
| `InvokeContextClick` | right-click / context menu | Mouse |
| `InvokeScroll` | wheel/scroll (direction + notches in `value`) | Mouse |
| `SendKeys` | typed-field commit (focus loss, field change, form submission, or page exit); `value.text` = typed text, password-redactable | Keyboard |
| `SubmitForm` | form submit (`value` carries `formId`/`formName`) | Form |

> Single-key actions (`keydown`/`keyup`) and **key
> combinations** (Ctrl/Alt/Cmd + key, planned as a distinct `SendKeysCombination` event)
> are **deferred** — placeholders marked `// TODO(key-combination)` that emit nothing for
> now; trusted `input` events update one field session, which is captured as one `SendKeys`
> on commit regardless of pauses between keystrokes.

Navigation events (type `Navigation`, top frame, full-page only):

| `event` | Meaning |
| --- | --- |
| `OpenUrl` | Address-bar navigation: a typed URL or search (`from_address_bar` / typed transitions). Link clicks/form submits/redirects are **not** OpenUrl — they are the `InvokeClick` / `SubmitForm` already captured. |
| `UpdatePage` | A reload / refresh. |
| `RedoNavigation` | Forward. |
| `UndoNavigation` | Back. |

Context events (type `Context`):

| `event` | Trigger | `value` |
| --- | --- | --- |
| `SwitchFrame` | an interaction moves into a frame (deeper or sideways in the frame tree) | `{ frameId, frameUrl, xpath, cssSelector }` — the iframe element locator is resolved for same-origin frames; cross-origin falls back to URL + frameId |
| `SwitchParentFrame` | an interaction moves up to an ancestor frame (e.g. back to the top document) | same shape as `SwitchFrame` for the frame moved into |
| `SwitchWindow` | the active tab changes | `{ tabId, index }` |
| `CloseWindow` | a tab closes | `{ tabId, index }` |

`SwitchWindow` / `CloseWindow` `index` is the 0-based position in the **driver window-handle
list** (creation order, `0` = first/main), matching G4's `SwitchWindow(n)` / `CloseWindow(n)`
semantics — not the Chrome tab-strip position. The recorder tracks this list (seeded from the
open tabs, then updated on tab create/close) and persists it in `chrome.storage.session`.
Closing a tab emits `CloseWindow(index)`, then the browser activating another tab emits the
following `SwitchWindow`. No `tabs` permission is required (`id`/`index` are available without
it).

A switch event is emitted **once per actual frame change** (then subsequent interactions in
the same frame are just the action). Direction is resolved from the frame tree via
`chrome.webNavigation.getAllFrames`, and the active frame per tab is persisted in
`chrome.storage.session` so detection survives a service-worker restart. Expected order
for a frame interaction: `SwitchFrame` → `InvokeClick`/`SendKeys`/…; returning to the
parent: `SwitchParentFrame` → action.

All events can be toggled on the settings page.

## Project layout

```text
manifest.json
js/
  signalr.min.js        vendored official @microsoft/signalr 8.0.7 (offline)
  background.js         classic service worker: connection, session, stack, relay
  content-script.js     DOM capture in every frame
lib/
  constants.js          shared config, channels, defaults
  settings-store.js     chrome.storage.sync wrapper
  recorder-contract.js  contract builders
  dom-locator.js        absolute XPath + CSS selector
  dom-mapper.js         element -> node, chain builder
  event-catalog.js      DOM event -> { type, event, value }
  signalr-connection.js wrapper over the official SignalR client
  form-controls.js      on/off switch + themed number field enhancers
css/                    parameters / layout / visual split per component (+ fonts.css,
                        form-controls.*), palette matched to the G4 designer
fonts/                  Inter (body/UI) + Space Grotesk (titles), self-hosted (offline)
user-interface/
  popup.html / popup.js
  options.html / options.js
icons/
```

## Location & loading

The extension lives at
`E:\Development\csharp\uia-peek\src\ChromiumPeek.Extension` and is linked into the
`ChromiumPeek` host project. On build, `ChromiumPeek.csproj` copies it into the output at
`bin\Debug\net10.0\ChromiumPeek.Extension`, and `StartChromiumWithExtension` launches
Chrome with the browser load-extension argument pointing at that output folder. So
running `ChromiumPeek` auto-loads the extension — no manual step required.

### Manual load (optional)

1. Build/start the C# `ChromiumPeek` hub so it listens on `http://localhost:9956`.
2. Open `chrome://extensions` in a Chromium browser.
3. Enable **Developer mode**.
4. Click **Load unpacked** and select
   `E:\Development\csharp\uia-peek\src\ChromiumPeek.Extension`.
5. Open the popup to confirm the status shows **Connected**.

## Settings

Open the popup and click **Settings** (or use the browser's extension options):

- **Hub URL** — the SignalR hub endpoint (default `http://localhost:9956/hub/v4/g4/peek`).
- **Connect automatically** — connect on browser start.
- **Reconnect delays** — backoff for automatic reconnect.
- **Redact passwords** — mask values from password/sensitive fields (default off).
- **Maximum events per session** — popup stack cap.
- **Events to capture** — per-event-type toggles.

> Changing the hub host or port may require updating `host_permissions` in
> `manifest.json`, because extension host permissions are static.

## Server-side requirement

The C# `ChromiumPeek` project must expose a relay method the extension invokes:

```csharp
public Task SendRecordingEvent(ChromiumEventModel recordingEvent)
{
    return Clients.All.SendAsync(
        method: "ReceiveRecordingEvent",
        arg1: new HubResponseModel(recordingEvent));
}
```

The hub's CORS origin check must also allow the extension origin
(`chrome-extension://...`) so the WebSocket upgrade passes SignalR origin validation.

## Notes & limitations

- The connection uses `skipNegotiation` with the WebSockets transport so the official
  SignalR client runs inside a service worker (no `XMLHttpRequest`/`document`).
- Browser coordinates are viewport-relative (`clientX`/`clientY`); true screen
  coordinates are not available to web pages.
- Locators are scoped to each frame's own document; cross-frame interactions are
  recorded with the frame's local path.
