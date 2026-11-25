<div align="center">

# TRACE17

**Lightweight Windows Tray Hardware Telemetry, Click-Through Game FPS HUD & All-Device WHEA Event 17 Monitor**

![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0A7EA4?style=flat-square)
![.NET Framework 4.x](https://img.shields.io/badge/.NET%20Framework-4.x%20(C%23)-1C212B?style=flat-square&labelColor=12161D&color=4FE3F7)
![Zero Dependencies](https://img.shields.io/badge/Build-Built--in%20csc.exe-58E09E?style=flat-square)

</div>

---

**Trace17** is a zero-bloat, native Windows tray utility built in C# / WinForms for enthusiasts who want live system thermals, an on-demand in-game FPS overlay without DLL injection, and immediate attribution whenever **WHEA-Logger Event 17** PCIe errors occur on any NVMe SSD, root port, or GPU.

## Features

| Capability | Source / Method | Notes |
| :--- | :--- | :--- |
| **CPU Usage & Temp** | Windows PerfCounters + optional HWiNFO64 SM2 / Gadget / LHM WMI | Usage works standalone; CPU °C auto-reads from HWiNFO64 Shared Memory / Gadget or LibreHardwareMonitor when running |
| **GPU Temp & Usage** | Intel `PresentMonAPI2.dll` (`PresentMonSharedService`) + PerfCounter fallback | Auto-selects discrete GPU (AMD / NVIDIA / Intel) without admin rights |
| **RAM Usage** | Win32 `GlobalMemoryStatusEx` | Live percentage and `Used / Total GB` |
| **NVMe / SSD Temps & Usage** | Native `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceTemperatureProperty`) + `PhysicalDisk` counters | Reads SMART/NVMe temperatures for every physical drive without elevation; filters virtual/cloud drives |
| **Game FPS Overlay Toggle** | `PresentMonAPI2.dll` foreground process tracking | Translucent, click-through (`WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`) top-left HUD — **anti-cheat friendly** (no DLL injection) |
| **All-Device WHEA 17 Guard** | `Microsoft-Windows-WHEA-Logger` Event ID `17` + PCIe `Enum\PCI` & `Win32_DiskDrive` resolver | Monitors **all** PCIe/storage devices, resolves exact drive letter + model + `B:D:F`, alerts on first event and escalates bursts (10+ in 60s) |

---

## What Are WHEA Event 17 Errors?

**`Microsoft-Windows-WHEA-Logger Event ID 17`** (*Corrected Hardware Error*) is a Windows System Event Log warning generated when a **PCI Express** component (an NVMe SSD, PCIe Root Port, GPU, Wi-Fi/USB controller, or riser cable) encounters a link-level error that hardware ECC/CRC/replay mechanisms recovered from.

While a single Event 17 won't immediately crash Windows, **storms or recurring bursts** indicate PCIe link instability and commonly cause:
- Micro-stutters, frame-time spikes, or brief freezes in games
- Temporary NVMe drive stalls or sudden drive disconnects
- Event Log flooding and eventual uncorrectable crashes (WHEA BSODs) if signal degradation worsens

### How Trace17 Helps Pinpoint the Culprit
Stock Windows Event Viewer logs raw bus numbers like `Bus=0x0, Device=0x6, Function=0x1, VendorID=0x8086, DeviceID=0x7ECA`. **Trace17** automatically walks Windows PnP and disk-to-PCIe mappings so alerts and logs identify the exact device:

```text
WHEA 17  #1  [D: PCIe Root · WD_BLACK SN7100 1TB (0:6:1 · 8086:7ECA)]  record 134186  corr=0x1 uncorr=0x0
```

### Common Fixes for WHEA Event 17 Errors

If Trace17 catches recurring WHEA 17 warnings on a specific drive, root port, or GPU, try these proven fixes in order:

1. **Disable PCI Express Link State Power Management (Power Plan)**
   * Open **Control Panel → Power Options → Change plan settings → Change advanced power settings**.
   * Expand **PCI Express → Link State Power Management** and set it to **Off**.
   * Optionally switch to the **High Performance** power plan (or keep **Balanced** with Link State Power Management **Off**) to stop aggressive L0s/L1 power-saving transitions that trigger link-retraining errors.

2. **Step Down PCIe Link Speed in BIOS (e.g., Auto / Gen5 / Gen4 → PCIe Gen3 or Gen4)**
   * Enter UEFI/BIOS and locate the **PCIe / M.2 Link Speed** setting for the slot identified by Trace17 (or the PCIe x16 slot if using a GPU riser cable).
   * Change it from **Auto** (or **Gen5 / Gen4**) down one generation—such as **Gen4 → Gen3** (or **Gen5 → Gen4**).
   * Lowering link speed reduces high-frequency signal integrity issues caused by motherboard trace routing, PCIe riser cables, or noisy power-state transitions.

3. **Disable PCIe Native ASPM in BIOS**
   * Look for **PCIe Native ASPM**, **PEG ASPM**, **DMI Link ASPM**, or **L1 Substates** in your motherboard BIOS and set them to **Disabled**.

4. **Check Thermals & Reseat the Drive / GPU**
   * Watch your NVMe and GPU temperatures in Trace17—overheating NVMe controllers (`> 70–75°C`) frequently throw correctable link errors.
   * Power off, unplug PSU, and firmly **reseat the M.2 SSD** (or move it to a CPU-wired vs. chipset-wired M.2 slot) or **GPU / riser cable**.

5. **Update SSD Firmware, Chipset Drivers & Motherboard BIOS**
   * Install the latest firmware for your NVMe SSD (e.g., WD Dashboard, Samsung Magician, Crucial Storage Executive), latest **motherboard BIOS**, and **Intel/AMD chipset drivers**.

---

## Getting Started

### Prerequisites
* **Windows 10 / 11 (64-bit)**
* **.NET Framework 4.x** (built into Windows — used for compilation via `csc.exe`)
* *(Optional, for live FPS & GPU telemetry)* **Intel PresentMon Shared Service** (`C:\Program Files\Intel\PresentMonSharedService\PresentMonAPI2.dll`, bundled with Intel PresentMon / Intel Graphics Software / CapFrameX)
* *(Optional, for CPU °C on modern Intel Core Ultra / AMD Ryzen)* **HWiNFO64** running with *Shared Memory Support* or *HWiNFO Gadget* enabled, or **LibreHardwareMonitor**

### Build from Source

No Visual Studio or NuGet restore is required. From PowerShell in the repository folder:

```powershell
.\build.ps1 -SelfTest
```

This compiles `Trace17.exe` (`x64`) using Windows' built-in C# compiler and runs the synthetic verification suite (`--self-test`).

### Usage & CLI Modes

```powershell
# Launch normally in the system tray
.\Trace17.exe

# Launch and immediately open the telemetry panel
.\Trace17.exe --show

# Safe preview mode: simulated CPU/GPU/RAM/SSD telemetry, simulated FPS HUD,
# and synthetic multi-device WHEA 17 alerts (writes nothing to Windows Event Log)
.\Trace17.exe --simulate

# Run offline synthetic unit checks (writes logs\verification-synthetic.txt)
.\Trace17.exe --self-test

# Run live hardware, PresentMon, PCIe mapping, and Event Log check (writes logs\verification-live.txt)
.\Trace17.exe --verify-live
```

### Tray & Panel Controls
* **Left-click tray icon (`17`)**: Open/focus the telemetry panel
* **FPS Overlay: Off / On** *(panel button or tray menu)*: Toggle the click-through top-left FPS + vitals HUD for the active foreground game
* **View logs**: Open `logs\trace17.log` (rotates automatically at ~2 MiB, keeping one `.previous` backup)
* **Mute sounds**: Persist alert sound mute state in `sound-muted.flag`

### Run at Windows Startup

```powershell
# Add Trace17 to per-user Windows Startup
powershell -ExecutionPolicy Bypass -File .\Manage-Startup.ps1 -Action Enable

# Remove from Startup
powershell -ExecutionPolicy Bypass -File .\Manage-Startup.ps1 -Action Disable

# Uninstall executable, flags, and Startup shortcut (keeps source & logs)
powershell -ExecutionPolicy Bypass -File .\Manage-Startup.ps1 -Action Uninstall
```

---

## Project Structure

```text
Trace17/
├── src/
│   ├── Monitor.cs        # All-device WHEA 17 parser, PCIe B:D:F & disk resolver, burst logic
│   ├── Telemetry.cs      # PresentMonAPI2 P/Invoke, NVMe IOCTL temps, PerfCounters, HWiNFO64 reader
│   ├── Tray.cs           # Tray icon, rounded obsidian PanelWindow, click-through FpsOverlayForm
│   └── Verification.cs   # --self-test (synthetic) & --verify-live diagnostics
├── build.ps1             # One-command x64 build script via Windows csc.exe
├── Manage-Startup.ps1    # Windows Startup shortcut & uninstall manager
└── README.md
```
