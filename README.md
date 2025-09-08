# Table of Content

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

## UiaPeek Path Finder — Quick Start

UiaPeek Path Finder shows the UI-Automation path (an XPath-like locator) for whatever is currently under your mouse pointer. Hover any app control; the locator appears in the text box for copy/paste into your automation or QA tools.

### Requirements

* Windows desktop.
* If you need to peek elevated apps, run Path Finder **as Administrator**.

### Launch

* Open **UiaPeek Path Finder v1.0**.
* You’ll see a title, a locator text box, a **Start/Stop** button, and a **Faster/Slower** slider.

### Start peeking

* Click **▶ Start** (or press **Alt+S**).
* Move your mouse over the target application.
* The locator appears and updates in the text box. (The internal “/Desktop” prefix is stripped for convenience.)

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
