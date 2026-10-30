# Sonix IMX290 ASCOM camera driver

An ASCOM camera driver for the cheap Sony IMX290/IMX291 USB cameras built around the **Sonix SN9C292B** UVC
bridge (USB `0C45:6366`). With the standard Windows webcam driver these cameras stop at about 0.5 s of exposure.
This driver sets the sensor's frame length directly and gives **real exposures from 0.1 ms to 7.6 s** to PHD2,
N.I.N.A., SharpCap and any other ASCOM client.

> **Status: test release.** It works on the developer's camera with PHD2, N.I.N.A., SharpCap and ConformU
> (no errors or issues). It has not been tried on other units yet; that is what this release is for.
> Please report what you see (see [Testing](#testing-and-reporting)).

## Is my camera supported?

Only cameras with this exact USB bridge are supported. Check in Device Manager → the camera → Properties →
Details → *Hardware Ids*: it must contain **`VID_0C45&PID_6366`**. These are sold as "IMX290 / IMX291 USB
camera / guide camera / starlight camera" modules; the firmware identifies as `SONIX_IPC-292B----IMX291`.
Other IMX290 cameras (ZWO, QHY, Player One, ToupTek, other UVC bridges) have their own drivers and will not work
with this one.

`SxTest probe` (below) tells you in a few seconds whether your camera answers the way this driver expects.

## What you get

- Monochrome 1920×1080, 8 bit (`MaxADU` 255), optional 2×2 binning and subframes; colour (RGB) as an option.
- Exposure 0.0001 – 7.62 s. Above 33 ms the exposure is set by the sensor's frame length; below it by the
  electronic shutter.
- Gain 0–100 = sensor gain 6–36 dB in 0.3 dB steps (high conversion gain, all analog); for faint targets 40–90.
- From 0.5 s on, every exposure starts when the client asks for it, so looping clients run at about
  exposure + 0.1 s (plus the client's own overhead; PHD2 at 1 s: ~1.4 s per frame).

**Limits.** The camera only delivers MJPEG that it has already processed (gamma, contrast, compression), so the
images are fine for guiding, framing and live viewing but **not for photometry or calibrated imaging**. There is
no cooler, temperature sensor or ST-4 port, and `StopExposure` is not implemented (`AbortExposure` is).

## Requirements

- Windows 10 or 11 (x64), .NET Framework 4.8 (included in Windows 10 1903 and later).
- [ASCOM Platform](https://ascom-standards.org) 6.6 or later.
- Settings → Privacy & security → Camera → *Let desktop apps access your camera*: **on**.
- Only one program can use the camera at a time. Close the Windows Camera app, browser tabs, and any other
  astronomy program that has it open.

## Install

1. Download `Sonix290Camera-<version>-Setup.exe` from the [Releases](../../releases) page.
2. Close every program that might use the camera or has the driver loaded (the setup offers to close them).
3. Run the setup. It installs to `C:\Program Files\Sonix290Camera` and registers the driver for 64-bit and
   32-bit programs.
4. In your program's ASCOM camera chooser, select **Sonix IMX290**.

The setup is not code-signed yet, so Windows SmartScreen may show *"Windows protected your PC"*: click
**More info → Run anyway**.

To update, run the new setup over the old one. To uninstall, use *Settings → Apps → Sonix IMX290 ASCOM Driver*.
Your settings (`HKCU\Software\Sonix290Camera`) and logs are kept.

**Without the setup:** the release also has `Sonix290Camera.zip`. Unzip it, right-click `install.cmd` → *Run as
administrator*; `uninstall.cmd` removes it again.

### Using it

- **PHD2:** Camera = *ASCOM (Late) Camera*, then pick *Sonix IMX290* in the chooser. 1-4 s exposures are typical.
- **N.I.N.A.:** Equipment → Camera → *Sonix IMX290*.
- **SharpCap:** choose the ASCOM camera *Sonix IMX290*.

The first exposure after connecting, and the first one after changing the exposure time, takes longer
(the driver writes the new timing and checks that the camera took it, up to a few seconds). After that each
frame takes about the exposure time.

> **Warning: USB port matters.** On some USB controllers (seen on the chipset ports of an AMD 500-series
> board) the camera's register access is about ten times slower, and timing writes fail at the camera's normal
> 30 fps. The driver detects this at connect and switches to a *slow USB port mode*: it streams at 15 fps, gets
> the first timing through a retried STANDBY (the first exposure takes a few seconds longer), and keeps every
> frame at least 0.25 s long (shorter exposures are still exact, but come at most 4 per second). Exposures work,
> but in MJPEG **the image can look noticeably grainier** in this mode (under investigation, see the issues), so it
> is **not recommended for guiding**. Use a port on the CPU's USB controller instead (on desktop PCs usually a rear
> port). In MJPEG the driver then shows a warning box when connecting (click OK to continue; the advanced setting
> `WarnSlowPort` turns it off) and the camera description says *"slow USB port"*. The uncompressed modes keep
> their frame rate and show no warning. The log has `slow USB port mode` in either case.

### Settings

The chooser's *Properties* button opens the setup dialog. It has one choice, the **video mode**, and lists the
modes your camera offers:

- **MJPEG 1920x1080** (compressed, the default): every camera of this type; exposures 0.1 ms to 7.6 s.
- **YUY2 1920x1080** (uncompressed): only cameras with a firmware patch for this mode list it; no JPEG
  artefacts, exposures up to two minutes, each frame takes at least 0.54 s. The patch is made with the separate,
  **experimental** flash tool in [flash/](flash/README.md); read its warnings first.
- **YUY2 640x480** (uncompressed): every camera of this type; lower resolution.

*Open log folder* opens the logs. *Advanced...* shows all other settings (stored in
`HKCU\Software\Sonix290Camera`); their defaults are the values measured for this camera, and you should not need
to change them. *Defaults* there restores them. If you installed an earlier test version, click **Defaults** once.

## How it works

The camera's USB bridge exposes a vendor UVC extension unit that gives access to the bridge's registers, and
through its I2C master to the sensor's registers. The driver:

1. streams MJPEG 1920×1080 through DirectShow and fixes the camera's automatic controls (exposure, white balance,
   backlight compensation) at connect;
2. sets the exposure by writing the sensor's frame length VMAX (18 bits) and shutter SHS1, which the stock
   firmware limits to 16 bits;
3. restarts the sensor's frame cycle with a short STANDBY toggle when an exposure is requested, so the frame
   starts at the request instead of somewhere in a free-running cycle;
4. trusts nothing the camera reports about itself: every returned frame is checked by its arrival interval, and
   the timing is written again if the camera did not take it.

The driver writes only volatile sensor and bridge registers, which reset when the camera is unplugged.
**It never writes the camera's flash memory.** (The separate tool in [flash/](flash/README.md) does, on request.)

## Testing and reporting

Please open an [issue](../../issues/new), even when everything works. Name your camera (Hardware Ids from Device
Manager), Windows and ASCOM Platform version and the driver version. Most useful:

1. The result of the quick check, run in a command prompt in `C:\Program Files\Sonix290Camera`:

   ```
   SxTest probe
   SxTest driver 1000 --count 5
   ```

   `probe` should show your camera's device path with `vid_0c45&pid_6366`, `ASIC 0x101F = 0x92` three times and
   `sensor slave 0x1A: ACK`. `driver 1000` should list five 1 s exposures with `measured` close to `expected`.
2. Which program, which exposure times, and what happened (time per frame, errors, odd images).
3. The log files from `%LOCALAPPDATA%\Sonix290\Logs` (`driver-*.log` from the client session, `sxtest-*.log`).
   They contain your Windows user name in file paths; edit that out if you prefer.

`SxTest` with no arguments lists all its measurement commands.

## Building from source

Needs the .NET SDK 8 or later (`winget install Microsoft.DotNet.SDK.8`); the projects target .NET Framework 4.8
through reference assemblies, so no Visual Studio is required.

```
driver\build.cmd
```

builds the driver and `SxTest` and assembles `driver\dist\Sonix290Camera\` plus a zip, and, if
[Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed (`winget install JRSoftware.InnoSetup`), the setup
`driver\dist\Sonix290Camera-<version>-Setup.exe` from `driver\installer\Sonix290Camera.iss`. `build.sh` builds the
zip on Linux. For engine work you can run `driver\SxTest\bin\Release\net48\SxTest.exe` directly without
installing; only the ASCOM/COM path needs `install.cmd`.

| Path | Contents |
|------|----------|
| `driver/Sonix290.Camera/` | The driver: `Camera.cs` (ASCOM ICameraV3 over IDispatch), `CameraEngine.cs` (exposure, verification, STANDBY restart), `UvcDevice.cs` (DirectShow graph), `SonixXu.cs` (extension unit, I2C, VMAX/SHS1), `Native.cs` (COM interop), `Imaging.cs`, `Support.cs` (settings, log), `SetupForm.cs` |
| `driver/SxTest/` | Command-line measurement tool on the same code |
| `driver/package/` | `install.cmd`, `uninstall.cmd`, `comtest.vbs` (native IDispatch test, 32/64-bit) |
| `driver/installer/` | Inno Setup script for the setup .exe |

The driver has no dependency on the ASCOM assemblies or on third-party DirectShow libraries; the COM interfaces
are declared from the Windows SDK headers.

## Credits

The extension unit → bridge register → sensor I2C path was worked out by members of the astronomy community from
SONiX's own Linux test application (`SONiX_UVC_TestAP`, mirrored in
[CberYellowstone/sn9c292b-osd-off-kit](https://github.com/CberYellowstone/sn9c292b-osd-off-kit)). The sensor
registers follow Sony's IMX290/IMX291 documentation. This driver is an independent implementation.

## Licence

[MIT](LICENSE).
