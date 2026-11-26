<p align="center">
  <a href="https://revolando.com"><img src="https://revolando.com/assets/img/revolando-icon.svg" alt="REVOLANDO" width="72"></a>
</p>

<h1 align="center">OVR Monitor</h1>

<p align="center">
  <b>Keep Quest Link alive, see your cable, measure the real USB data rate.</b><br>
  A tiny Windows tray app for Meta Quest + PC VR. One 70&nbsp;KB exe, no drivers, no third-party code.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/badge/download-latest%20release-2ea043?style=for-the-badge&logo=windows" alt="Download"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=for-the-badge" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue?style=for-the-badge" alt="Apache 2.0"></a>
  <img src="https://img.shields.io/badge/dependencies-none-success?style=for-the-badge" alt="No dependencies">
</p>

<p align="center">
  <img src="docs/main.png" alt="OVR Monitor main window" width="513">
</p>

---

## Why this exists

It started with one annoying line.

Every so often **Quest Link stopped working**: the headset would not connect, Unreal's *VR Preview* button turned grey, and the fix was always the same, open an admin terminal and type

```powershell
Restart-Service OVRService
```

So the first version was a small script that pinged the service and offered a one-click restart.

Then we wanted to *see* what was going through the cable. And that is where it got interesting: the first real measurement showed that the headset, which supports 5 Gbps USB 3, had been running at **USB 2.0 (480 Mbps)** all along. The PC ports were fine, the headset was fine; the "USB 3" cable was not making its SuperSpeed contacts. Swapping ports did nothing, a new cable helped, and a four-plug test showed something nobody tells you: **with that cable, plugging the USB-C end one way gave USB 3, the other way USB 2**, every single time.

That turned a quick script into a proper tool. The first data-rate version used a third-party USB capture driver. We replaced it with Windows' own **Event Tracing for Windows (ETW)** and compared the two side by side during a live VR session: the PC → Quest stream matched to within about 0.1 %. Nothing else to install, nothing running in the kernel that Windows did not already ship.

What we measured on the way, for the curious:

| | |
|---|---|
| Link idle (headset in the Link home) | ~4 Mbps to the headset |
| Unreal VR Preview running | **~200 Mbps** to the headset, ~30 Mbps back |
| Share of a 5 Gbps USB 3 link | ~5 % |
| Share of the same stream on USB 2.0 | ~half of the 480 Mbps bus |

If your Link image is blurry, stutters or drops, check the cable before anything else. OVR Monitor shows you in one glance.

## Features

- **OVRService watchdog**: checks the Quest Link service on an interval you choose, tray icon turns red and a notification pops up if it stops.
- **One-click Re-Start**: restarts OVRService (with a warning, because it drops the live Link connection).
- **Cable diagram**: PC on the left, headset on the right, the cable in between, coloured by speed. Shows the PC-side connector (USB-A / USB-C), any hub in between, and the negotiated speed: USB 2.0, USB 3 Gen 1 (5 Gbps), Gen 2 (10 Gbps). If the headset could do USB 3 but the link fell back to USB 2, it tells you to check the cable or port.
- **Real USB data rate**: live graph of bytes going **to** the Quest (exact) and **from** the Quest (approximate), read from Windows' USB 3 driver events.
- **Wi-Fi graph (optional)**: received / sent on your network adapter, useful for Air Link.
- **History**: one-minute averages and 1-second peaks, plus an event log (service stopped / started, headset plugged / unplugged, link speed changed, restarts), shown on a 1 h / 24 h / 7 d / 30 d graph with event markers. Kept for 30 days by default, adjustable.
- **Admin mode without nagging**: asks for administrator rights **once**, then starts elevated from a Windows scheduled task, at logon and from the exe, with no further prompts.
- **Small and self-contained**: a single exe of about 70 KB, built with the C# compiler that ships with Windows. No installer, no runtime to download, no telemetry.

<p align="center">
  <img src="docs/history.png" alt="History window" width="776">
</p>

## Download and run

1. Get `OVRMonitor.exe` from the [latest release](../../releases/latest).
2. Put it anywhere, for example `C:\Tools\OVRMonitor\`, and run it.
3. Click the **gear** icon, tick **Run as administrator (asks only once)** and press OK. Approve the Windows prompt.

   <img src="docs/settings.png" alt="Settings" width="366">
   This is needed for the USB data rate (ETW needs admin) and for restarting the service without a prompt.
4. Optional: keep **Start with Windows** ticked so it lives in your tray.

Closing the window hides it to the tray. Use **Exit** in the tray menu to quit.

> Windows SmartScreen may warn about an unknown publisher, because the exe is not code-signed. You can check the source and build it yourself in a few seconds, see below.

## Build it yourself

No Visual Studio needed. Windows already contains the .NET Framework 4 C# compiler.

```powershell
git clone https://github.com/Revolando-Ltd/ovr-monitor.git
cd ovr-monitor
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` draws the icon, then compiles `src\*.cs` into `OVRMonitor.exe` with `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`.

## How it works

| What | How (all built into Windows) |
|---|---|
| Service state and restart | `System.ServiceProcess.ServiceController` |
| Finding the headset | Configuration Manager API (`cfgmgr32`), USB vendor ID `2833` |
| Negotiated speed, port type | USB hub IOCTLs: `GET_NODE_CONNECTION_INFORMATION_EX(_V2)`, `GET_PORT_CONNECTOR_PROPERTIES` |
| Data rate | Real-time ETW session on `Microsoft-Windows-USB-USBXHCI`, event 41 (transfer), size read with TDH |
| Wi-Fi rate | `System.Net.NetworkInformation` interface counters |
| Elevation without prompts | A per-user scheduled task with *Run with highest privileges* |
| Settings and history | `%APPDATA%\OVRMonitor\` (`settings.json`, `history\*.csv`) |

**About the "from Quest" number:** for transfers coming *into* the PC, Windows logs the size of the buffer that was offered, not the bytes that actually arrived. Under load it reads roughly 10-15 % high, at idle much more, so it is shown with a `~`. The *to Quest* direction (the video stream) is exact.

## Uninstall

Open the gear menu, untick **Run as administrator** and **Start with Windows** (this removes the scheduled task and the startup entry), exit from the tray, delete the exe. To remove the history as well, delete `%APPDATA%\OVRMonitor`.

## Contributing

Issues and pull requests are welcome. Ideas that would fit well: support for other PC VR headsets, Air Link specific stats, a dark/light theme switch, translations.

You are free to use, modify and redistribute OVR Monitor, including in your own projects, under the [Apache License 2.0](LICENSE). The one thing we ask (and the license requires) is that you **keep the attribution**: the copyright line and the [NOTICE](NOTICE) file go with any copy or derivative work.

## License

Copyright 2026 **[REVOLANDO LTD](https://revolando.com)**, London.
Licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE).

OVR Monitor is an independent project and is not affiliated with, endorsed by or sponsored by Meta Platforms, Inc. Meta, Meta Quest, Quest and Oculus are trademarks of Meta Platforms, Inc.

<p align="center">
  <a href="https://revolando.com"><img src="https://revolando.com/assets/img/revolando-icon.svg" alt="REVOLANDO" width="40"></a><br>
  <sub>Made by <a href="https://revolando.com">REVOLANDO</a>, Digital Dreamland</sub>
</p>
