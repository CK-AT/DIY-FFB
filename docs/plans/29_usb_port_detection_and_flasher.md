# Plan 29 — USB port auto-detection + pure-C# USB flasher

Now that PCB 13 runs native USB composite CDC+HID (568246b1), every DIY-FFB
board identifies itself on the bus. Two features follow:

1. **Auto-detect the gateway port** by VID/PID instead of a bare COM-name list.
2. **Flash firmware over USB from the plugin**, implemented as a **pure C#
   client of the ESP32-S3 ROM serial bootloader** — no bundled `esptool.exe`,
   no Python, no flasher stub.

Upstream (ChrGri, `origin/main`, 0b759ffc … 44369994) shipped a USB flasher that
shells out to an embedded 12.7 MB PyInstaller `esptool.exe`. We take its
**port-handling and reset strategy** (hard-won, see §4.3) but not its transport
or its firmware source (§5).

**Related:** [Plan 19](19_msfs_pure_csharp_simconnect.md) (precedent: replace a
native dependency with a pure-C# protocol client), [Plan 26](26_telemffb_python_backend.md)
(its §"port detection" notes predate native CDC and are now stale — see §8).

---

## 1. Why

- **Port selection today** is `SerialPort.GetPortNames()` sorted
  ([DiyFfbPluginUI.xaml.cs:378](../../SimHubPlugin/DiyFfbPluginUI.xaml.cs#L378)),
  persisted as a COM name. It lists ghost ports (registry), can't tell the
  gateway from an axis, the CH343 console, or a mouse, and breaks when Windows
  renumbers the port.
- **The only firmware path in the plugin is WiFi OTA** (`OtaUpdateCoordinator`,
  HTTP pull). It needs a running, non-bootlooping image *and* WiFi. First flash,
  bootloop recovery, and WiFi-less rigs all fall back to "install PlatformIO".
  USB flashing is the recovery path OTA can never be.
- **Why not esptool.exe:** PyInstaller executables are a classic AV
  false-positive (support burden for a SimHub plugin); 12.7 MB blob; extracted to
  `%TEMP%` (upstream never re-extracts → stale exe forever); errors come back as
  stdout we'd have to grep. The ROM protocol is small and documented (§4) — we
  already chose this trade-off for SimConnect in Plan 19.

## 2. Device identities (what's on the bus)

Set at runtime in `CommManager::setup_joystick()`
([CommManager.cpp:113-129](../../ESP32/src/CommManager.cpp#L113-L129)); needs
`ARDUINO_USB_CDC_ON_BOOT=0`.

| VID:PID | Product string | Role | Use |
|---|---|---|---|
| `303B:8210+N` | `DIY-FFB-Gateway-N` | gateway, N = index | protocol connection; flash target |
| `303B:8220+N` | `DIY-FFB-Axis-N` | axis node | flash target only (protocol goes via gateway/CAN) |
| `303B:8230` | `DIY-FFB-Grip-…` | GripController | HID-only; CDC only in cal mode — list, don't connect |
| `303A:1001` | (ROM) | S3 USB-Serial/JTAG = **ROM download mode** | flash transport |
| `303A:0009` | (ROM) | S3 ROM over USB-OTG (`IMAGE_CHIP_ID`=9) | flash transport (not expected on our boards) |
| `1A86:*` | CH343 | PCB 13 UART0 console (boot/panic log, 115200); has auto-program DTR/RTS wiring | label as console; recovery flash path (§4.4) |
| other | — | unknown | listed, unlabelled |

Notes:
- Upstream's flasher treats `303A:0002` as the ROM-OTG port; that is the
  **ESP32-S2** id. For the S3 it's `0009` (esptool: `uses_usb_otg()` ⇔
  `PID == IMAGE_CHIP_ID`).
- Composite device: the COM port is the `MI_01` child; its PnP DeviceID does
  **not** carry the USB serial number (eFuse MAC). The parent
  `USB\VID_303B&PID_82xx\<serial>` does. Role+index from the PID is enough for
  identity; the MAC is a tie-breaker if two boards share an index.
- Legacy envs (`0xF011`, core 2.0.17, classic ESP32 UART-bridge boards) won't
  match; they stay selectable manually.

## 3. Part A — port auto-detection

### 3.1 `UsbDeviceScanner`
- WMI `SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'`
  (`System.Management` reference). Parse `COMn` from `Name`, `VID_/PID_/MI_`
  from `DeviceID`, classify per §2.
- Returns `UsbPort { Com, Vid, Pid, Role, Index, Label }`. Labels like
  `COM5 — DIY-FFB Gateway 1`, `COM9 — CH343 console`.
- Short cache (~1 s) + `forceRefresh`; scans run off the UI thread (WMI query is
  tens–hundreds of ms).

### 3.2 Plugin integration
- `UpdateSerialPortList()` fills the gateway dropdown from the scanner, gateways
  first.
- **Persist identity, not COM name:** new setting `GatewayTarget` = `{Role,
  Index}`; keep `ESPNow_port` as fallback for legacy/non-matching devices.
- Auto-reconnect (`AutoReconnectGateway`, 2 s timer,
  [DiyFfbPlugin.cs:4436](../../SimHubPlugin/DiyFfbPlugin.cs#L4436)) resolves
  `GatewayTarget` → current COM via the scanner, so a renumbered port still
  reconnects.
- **First-run auto-select:** nothing saved + exactly one gateway present →
  select and connect it. More than one → don't guess; user picks.
- No `WM_DEVICECHANGE` hook in v1 — the 2 s timer already polls. Revisit only if
  scan cost shows up.

## 4. Part B — pure-C# ROM flasher (`Esp32RomFlasher`)

All constants below were read from esptool **v5.4.0** as installed under
`~/.platformio/packages/tool-esptoolpy` (`loader.py`, `targets/esp32s3.py`,
`esp_pylib/serial_reset.py`) — not from memory.

### 4.1 Wire protocol
- **SLIP**: frame `0xC0 … 0xC0`; escape `0xC0→DB DC`, `0xDB→DB DD`.
- **Request**: `<BBHI` = `0x00, op, len(data), chk` + data.
  `chk` = XOR of payload bytes seeded with `0xEF` (data commands only).
- **Response**: `<BBHI` = `0x01, op, len, value` + data. Status = the **2 bytes
  right after the expected response data**; ROM appends 2 more reserved bytes
  (ignore). Status byte 0 ≠ 0 ⇒ error, byte 1 = reason. Skip responses whose
  `op` doesn't match (up to 100 reads). `ROM_INVALID_RECV_MSG = 0x05` ⇒
  unsupported command.

| Cmd | Op | Payload (ROM) |
|---|---|---|
| SYNC | `0x08` | `07 07 12 20` + 32×`55`; timeout 0.1 s; then drain 7 extra replies |
| WRITE_REG | `0x09` | `<IIII` addr, value, mask, delay_us |
| READ_REG | `0x0A` | `<I` addr → value in header `value` |
| SPI_SET_PARAMS | `0x0B` | `<IIIIII` id=0, total_size, 64 K, 4 K, 256, 0xFFFF |
| SPI_ATTACH | `0x0D` | `<I` hspi_arg=0 + `BBBB` is_legacy=0,0,0,0 (8 bytes, ROM form) |
| CHANGE_BAUDRATE | `0x0F` | `<II` new_baud, 0 (ROM: second arg 0) |
| FLASH_DEFL_BEGIN | `0x10` | `<IIIII` erase_size (uncompressed, rounded **up to 0x400**), num_blocks (compressed/0x400), 0x400, offset, encrypted=0 |
| FLASH_DEFL_DATA | `0x11` | `<IIII` len, seq, 0, 0 + block; `chk` = checksum(block); 3 attempts per block |
| FLASH_DEFL_END | `0x12` | `<I` `!reboot` — **ROM: only send when rebooting** (otherwise it exits the loader) |
| SPI_FLASH_MD5 | `0x13` | `<IIII` addr, size, 0, 0 → **32 ASCII hex chars** (ROM) |
| GET_SECURITY_INFO | `0x14` | → flags u32, crypt_cnt u8, 7× key_purpose, **chip_id u32**, api u32 |

- `FLASH_WRITE_SIZE = 0x400` for ROM (stub would use 0x4000; OTG ROM 0x800).
- Timeouts: default 3 s; erase on DEFL_BEGIN 40 s/MB (ROM erases up front);
  MD5 8 s/MB.
- **Compression:** ROM expects a **zlib stream** (2-byte header + deflate +
  Adler-32 BE). .NET 4.8 has only `DeflateStream` (raw) → emit `78 9C`, deflate
  body, Adler-32 by hand (~20 lines).

### 4.2 Session sequence (S3 via USB-Serial/JTAG)
1. **Enter ROM** (§4.3), open port, SYNC loop (7 attempts).
2. **Identify:** `GET_SECURITY_INFO` → `chip_id == 9` (ESP32-S3; S3 has
   `USES_MAGIC_VALUE = False`, so the `0x40001000` magic read isn't used).
   Refuse to flash if it doesn't match the package `chip` (§5).
3. **Disable watchdogs — mandatory on USB-Serial/JTAG.** ROM download mode via
   USB-JTAG does *not* reset the RTC WDT and SWD; they will reset the chip
   mid-flash. (esptool `ESP32S3ROM.disable_watchdogs`):
   - `WRITE_REG 0x600080B0 = 0x50D83AA1` (RTC WDT unlock), `0x60008098 = 0`,
     `0x600080B0 = 0`
   - `WRITE_REG 0x600080B8 = 0x8F1D312A` (SWD unlock), `0x600080B4 |= 1<<31`
     (auto-feed, read-modify-write), `0x600080B8 = 0`
4. `SPI_ATTACH`, `SPI_SET_PARAMS(total_size = 8 MB from package)`.
5. Per segment: zlib-compress → `FLASH_DEFL_BEGIN` → blocks → `SPI_FLASH_MD5`
   compare against the uncompressed segment's MD5.
6. **Leave download mode cleanly:** `WRITE_REG 0x6000812C value=0 mask=0x1`
   (clear `RTC_CNTL_FORCE_DOWNLOAD_BOOT` — our 1200 bps handler enters the ROM
   via `usb_persist_restart(RESTART_BOOTLOADER)`, which sets this; without the
   clear the chip boots straight back into download mode, arduino-esp32#6762).
   Then hard reset (§4.3).
7. Hand back to Part A: wait for `303B:82xx` to reappear; plugin reconnects.

No stub: over USB-Serial/JTAG baud is irrelevant (full-speed USB). Measured
2026-10-15 on PCB 13: **18.1 ± 0.1 s per keep-config flash** (1.3 MB image:
touch → ROM → write + verify → reset → re-enumerated), 20/20 back-to-back
cycles. Fine for an update/recovery tool, so the stub stays out. If that ever
changes, stub upload is a contained add-on
(MEM_BEGIN/DATA/END + stub JSON) — but **check the stub's license first**.

### 4.3 Getting into the ROM (port-type table, from upstream 44369994)

| Port seen | Action | Then |
|---|---|---|
| `303B:82xx` (our app CDC) | **1200 bps touch**: open at 1200, DTR+RTS high ~100 ms, drop both, close | resolve the new `303A:1001` port (below) |
| `303A:1001` | USB-JTAG reset sequence | connect |
| `1A86:*` / other UART bridge | classic DTR/RTS reset | connect |
| any port, user held BOOT | none | connect |

Our CDC has `enableReboot(false)` and only reboots to ROM on the 1200 bps open
([Main.cpp:202-224](../../ESP32/src/Main.cpp#L202-L224)) — DTR/RTS sequences on
the app port do nothing, by design.

**Port re-resolution** (upstream `TouchAndResolveBootloaderPortAsync` /
`WaitForPortReadyAsync`): snapshot before touch; poll every 200 ms ≤ 6 s; accept
a new `303A` port only after the original vanished *and* it's present on two
consecutive polls; then require two clean open/close cycles (250 ms apart) +
300 ms settle before SYNC. Windows lists the port before the driver accepts an
open — skipping this is what produced upstream's "could not open port".

**DTR/RTS polarity** (esp_pylib): `PIN_LOW = True` (asserted). Through the
auto-program transistors, DTR→IO0, RTS→EN. Windows `usbser.sys` only sends
`SET_CONTROL_LINE_STATE` on RTS change ⇒ after every RTS write, re-write the
current DTR (`set_rts` workaround). `System.IO.Ports` sets each line separately
via `EscapeCommFunction`, which matches esptool's Windows path (`ClassicReset`;
the atomic Unix variant isn't available on Windows either).

- **USB-JTAG reset:** RTS=F, DTR=F; 100 ms; DTR=T, RTS=F; 100 ms; RTS=T, DTR=F,
  RTS=T; 100 ms; DTR=F, RTS=F.
- **Classic reset:** DTR=F, RTS=T; 100 ms; DTR=T, RTS=F; 50 ms; DTR=F.
- **Hard reset:** RTS=T; 100 ms; RTS=F.

(T/F = .NET `DtrEnable`/`RtsEnable`.)

**Retries:** up to 5 attempts on *connection* errors only (sync timeout, open
failure, lost or busy port); 3 s back-off (≈15 s to replug — 3×1.5 s proved too
short on hardware), re-resolve the board each time (app port → touch again;
already in ROM → reset there; CH343 → same COM, classic reset). On a CH343 the
last try stays at 115200. The NVS MD5 baseline is taken once and checked after
the final write, so a failed attempt in between can't hide a change. On final
failure: tell the user to hold BOOT, tap RST (or replug), and retry.

### 4.4 CH343 fallback (PCB 13)
If the native image bootloops before `USB.begin()`, the native port never
appears. PCB 13's CH343 **has the DTR/RTS→EN/IO0 auto-program wiring**
(confirmed 2026-10-15), so recovery needs no button presses:
- Flash over the CH343: classic reset (§4.3), SYNC at 115200, then
  `CHANGE_BAUDRATE` to 921600. Same ROM protocol, UART instead of USB-JTAG, so
  **skip the watchdog disable** (esptool only does it for USB-Serial/JTAG) and
  use the classic hard reset at the end.
- The dialog offers this automatically when a board's CH343 is present but its
  `303B:82xx` port isn't, or when the native-port attempt fails.
- Hold BOOT + replug (ROM on `303A:1001`) stays the last resort for boards
  without a CH343.

**Validated 2026-10-15:** `FlasherTool` flashed PCB 13 back and forth between two
versions both via the native CDC (1200 bps touch → `303A:1001`) and via the
CH343 (classic reset, 921600), with the NVS MD5 unchanged across each flash.

Caveat: the CH343 port must not be held open by anything else (a serial monitor
on the console). Opening it with the wrong DTR/RTS state resets the chip, so
the scanner and plugin must never open it implicitly.

### 4.5 Gateway connection must yield
Before entering the ROM: close the `ProtobufSerial` gateway connection **and
suspend the 2 s auto-reconnect timer** for the whole session (incl. post-reset
re-enumeration). Otherwise the timer grabs `303B:82xx` the instant it reappears,
or races the touch. Resume and reconnect after step 7. (Upstream has no such
timer, so its flasher doesn't need this.)

## 5. Firmware source — extend `.ffbota`, don't embed in the DLL

Upstream bakes `.pio/build/*` into the DLL at plugin build time
(`copy_firmwares.bat` + `EmbedDynamicFirmware`). We already have a versioned,
MD5-checked artifact per env: `OTA/firmware_<env>.ffbota`
([ota_package_post.py](../../ESP32/scripts/ota_package_post.py)), today
`manifest.json` + `firmware.bin` only.

**Extend the package** with the merged factory image (implemented; format
stays backward-compatible, OTA reads entries by name and ignores the new one):
- add `firmware.factory.bin`: pioarduino's `esp32_create_combined_bin`
  post-action on `firmware.bin` (bootloader + partitions + boot_app0 + app,
  merged at `0x0`). It runs before our `buildprog` post-action.
- the packager only includes it when the image's app part at
  `$ESP32_APP_OFFSET` is byte-identical to `firmware.bin` and ends with it, so a
  stale merge is never shipped. Platform 6.9.0 envs (core 2.0.17) produce no
  factory image; their packages stay OTA-only.
- manifest gains `chip` (`build.mcu`), `factory_md5`, `factory_size`.

One file instead of four: no `boot_app0.bin` lookup in the framework dir, and
the segment split is derived on the flasher side (§5.1).

Flasher accepts a local `.ffbota` or fetches the published one. One artifact
serves OTA and USB. The DLL stays small, and the plugin build no longer has to
run after PlatformIO.

**`update_info.json` is not touched.** The ESP32 downloads it itself during OTA
and pulls the `.bin` its entry points to, so its schema and URLs stay
ESP32-facing. The plugin finds the board's entry (same info URL as the OTA
dialog) and derives the package URL by convention: the packager publishes
`firmware_<env>.ffbota` next to `firmware_<env>.bin` in `OTA/`, so swap the
extension. A 404 there means that board has no USB-flashable package.

### 5.1 Segments — and two traps
Verified on the current `a6-ffb-v10-ck-at` build; `partitions.bin` decoded:
nvs `0x9000/0x5000`, otadata `0xE000/0x2000`, app0 `0x10000`, app1 `0x340000`,
spiffs `0x670000`, coredump `0x7F0000`. The factory image is the four parts
merged at `0x0` (verified byte-identical); bootloader header `E9 03 02 3F`
(DIO hdr, 8 MB, 80 MHz) is written unmodified and MD5-verified.

- **Trap 1: otadata must always be written.** After any OTA the device may run
  from `app1`. Writing only `0x10000` would leave otadata pointing at app1 ⇒
  the *old* image keeps booting and the flash "didn't work".
- **Trap 2: the factory image wipes the config.** Its gap `0x9000–0xE000` is
  all `0xFF`, i.e. **writing it whole erases NVS = the stored `config`
  Preferences namespace** ([ConfigManager.cpp:116](../../ESP32/src/ConfigManager.cpp#L116)).

So the flasher ([FirmwareImage.cs](../../SimHubPlugin/Flasher/FirmwareImage.cs))
reads the NVS range from the partition table *inside the image* and splits
around it: **Update** writes `[0x0, nvs)` and `[nvs_end, end)` (otadata
included, config kept); **Factory reset** writes the image whole. Taking the
range from the image means a future partition-table change can't silently
start wiping configs.

### 5.2 "Reset config" (NVS erase)
`ERASE_REGION` is stub-only. ROM equivalent: `FLASH_DEFL_BEGIN` on
`0x9000/0x5000` (ROM erases up front) + one compressed all-`0xFF` block, then MD5
check. Same code path as a normal write.

## 6. UI
- System tab: **"USB flash…"** opens a dialog:
  - device list from the scanner (§3.1), multi-select, gateway and axes shown
    with role labels; "ROM / bootloader" entries for `303A:1001`
  - firmware: latest from `update_info.json` (board-matched) | local `.ffbota`
  - mode: **Update** (default) | **Factory reset** (+ NVS erase), with a
    clear "erases stored configuration" warning
  - per-device progress bar (bytes written), log pane, typed error messages
- Multiple devices flash **sequentially**, with axes first (same order as the
  OTA coordinator). Every axis re-enumerates on its own USB port; the gateway is
  last so the plugin reconnects once.

## 7. Phasing

| Phase | Scope | Exit criterion |
|---|---|---|
| **1** | `UsbDeviceScanner` + dropdown labels + `GatewayTarget` reconnect + first-run auto-select | renumber the COM port in Device Manager → plugin still reconnects |
| **2** ✅ | ROM flasher core (`SimHubPlugin/Flasher/`: SLIP, commands, zlib, watchdog disable, MD5) + factory image in `.ffbota` + desk harness `FlasherTool` (no UI) | `.ffbota` from `pio run` flashes PCB 13; `esptool verify-flash` matches; device boots the new version with its config intact |
| **3** ✅ | entry/reset/re-resolution (§4.3), retries, gateway yield (§4.5), force-download clear | 20× touch→flash→boot cycles from the running app with no manual intervention |
| **4** ✅ | fetch the board's published `.ffbota` ([PublishedFirmware.cs](../../SimHubPlugin/Flasher/PublishedFirmware.cs)): URL derived from its `update_info.json` `.bin` URL (file unchanged); package must match the entry's board, version and firmware MD5, refused downloads aren't cached | verified against GitHub `main` (old package refused cleanly) and a `file://` copy of a local build (accepted; MD5 mismatch refused). The plugin path is exercised by phase 5 |
| **5** ✅ | "USB Flash..." dialog in System → OTA ([UsbFlashDialog.xaml](../../SimHubPlugin/UsbFlashDialog.xaml)): device list from the scanner, published or local `.ffbota` (reported board preselected, mismatch confirmed), Update / Factory reset, progress + log; gateway released via `SuspendGateway()` and reconnected after. **One device per run** — multi-select deferred, running it per board covers it | validated in SimHub 2026-10-16: update keeps config, factory reset clears it, gateway auto-reconnects, no doubled messages (fixed: manual Connect + auto-reconnect attached `OnMessage` twice) |
| **6** | CH343 recovery path (§4.4): dialog preselects the CH343 when no DIY-FFB/bootloader port is present, and offers it after a native-USB flash gives up on a connection error. Only with exactly one WCH (VID 1A86) port — nothing ties a CH343 to a board, so with several the user picks | transport validated in phase 2; dialog auto-offer implemented 2026-10-16, not yet exercised on hardware |
| **7** (opt.) | stub loader, only if ROM throughput is too slow | — |

Phase 1 is independent and useful on its own; ship it first.

## 8. Risks / notes
- **Validate both line-control paths on hardware** (USB-JTAG and CH343). The
  sequences are copied from esptool, but .NET's per-line writes, timing, and the
  `usbser.sys` RTS quirk are exactly where a port goes wrong.
- **S3 only in v1.** Classic ESP32 boards need different register addresses,
  plus reading the flash ID for the size. They use UART bridges, so they're
  reachable with the same code later.
- **Secure download mode / flash encryption:** not used on our boards; detect
  via `GET_SECURITY_INFO` flags and refuse with a clear message rather than
  half-flash.
- **Plan 26 §"port detection" (lines ~117-159) is stale.** It says the protocol
  runs over the UART bridge with no DIY VID/PID and warns against auto-detecting
  by `0x303B`. Since 568246b1 that's inverted; update it when Phase 1 lands
  (TelemFFB's Python backend can use the same VID/PID table).
- Desk reference for debugging: `esptool --trace` on the same board shows every
  SLIP frame; diff against our C# trace.
