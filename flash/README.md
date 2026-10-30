# SxFlash: uncompressed 1080p for the Sonix IMX290/IMX291 USB camera

> [!WARNING]
> **Experimental and barely tested.** SxFlash has been run on exactly **two cameras**, both with the same firmware,
> on one Windows 11 PC. It may not work on your camera, and a failure can leave the camera unusable. It is published
> so that people who accept that risk can try it and report back. If you are not sure, do not use it: the camera
> works with the [ASCOM driver](../README.md) without any patch (MJPEG 1920x1080).

SxFlash adds an **uncompressed YUY2 1920x1080** video mode to the cheap Sonix SN9C292B + Sony IMX290/IMX291 USB
cameras (USB ID `0C45:6366`, firmware `SONIX_IPC-292B----IMX291`). Out of the box these cameras send full HD only
as MJPEG; the uncompressed mode is limited to 640x480. With the patch and the
[Sonix IMX290 ASCOM driver](../README.md) (0.7 or later) you get uncompressed 8-bit
1920x1080 frames without JPEG artefacts, and long exposures (the sensor allows up to two minutes; tested up
to 15 s).

> [!CAUTION]
> **This tool changes the camera's firmware.** It has many safety checks and has been tested, but a mistake, a
> bad cable or a power cut during the write can leave the camera unusable. A camera in that state can then only be
> recovered with an external SPI flash programmer. **Use it only on a camera you can afford to lose.** There is no
> warranty of any kind (see [LICENSE](../LICENSE)).

## What the patch changes, and what not

- It rewrites one 4 KB block of the camera's 512 KB flash (the block with the USB descriptors and the video mode
  table, 0x10000-0x10FFF). Nothing else on the flash is touched.
- The camera then lists **YUY2 1920x1080** (at a nominal 5 fps) **instead of YUY2 320x240**. All other modes
  (MJPEG, H.264, YUY2 640x480) stay as they were.
- **The new mode needs the ASCOM driver.** In this mode the camera only delivers frames after the driver has set a
  longer sensor line time, which it does every time it starts the camera. Other programs (Windows Camera, OBS, a
  plain webcam mode in SharpCap) show the mode in their lists but get no picture from it. Use the stock modes there.
- The tool ships no firmware. It reads your camera, changes 39 bytes in memory, recomputes the checksums, checks
  the result against the known value, and writes that. Everything can be undone (see Restore).

## Requirements

- Windows 10 or 11 (.NET Framework 4.8, built into Windows).
- A camera with a **known firmware**. The tool checks this itself: it compares the whole flash with a list of known
  firmware versions and refuses anything else. So far one version is known (two cameras tested, both identical).
- Preferably a USB port on the **CPU's USB controller**. On desktop PCs these are usually the rear ports next to the
  video outputs. Some chipset ports make the camera's register access ten times slower: the tool works there too, but
  says so, and everything takes about ten times as long (a write about 30 minutes instead of 3).
- The PC must stay on during a write. The tool keeps Windows from going to sleep by itself while it works; do not
  put it to sleep or shut it down yourself.
- **Only this one Sonix camera connected**, and **no other program using any camera** (close PHD2, SharpCap,
  N.I.N.A., video calls). The tool checks both and refuses otherwise.

## User guide

Download `SxFlash.exe` from the repository's Releases page (the releases named *SxFlash*, separate from the driver
releases) and start it (no installation). The window title shows the tool and patch version. The red banner at the top repeats the warning above.

### 1. Back up the flash

Click **1. Back up the camera's flash...** and choose where to save the file. The tool reads the whole flash twice
(about 75 seconds; on a slow port about 11 minutes) and saves the file only if both reads match. **Keep this file**: it is your way back. A backup
is never overwritten.

The tool then tells you what it found:

| Message | Meaning |
|---|---|
| *Known firmware: ..., original* | A stock camera. It can be patched. |
| *... already with patch v1 (the newest)* | This camera is patched already. Restore can undo it. |
| *... with the older patch vN* | Patched with an earlier version. **Patch** updates it in one write. |
| *UNSUPPORTED* | A firmware the tool does not know. Nothing can be written. Please send the backup file and the log (see Reporting) so it can be checked and added. |

### 2. Patch

Leave **Patch the camera** selected and click **2. Patch the camera...**. The tool first runs its checks; nothing
is written yet:

- the camera still holds what the backup says (if you made the backup in this session and did not unplug the
  camera since, it uses that backup; otherwise it reads the flash once more, about 40 seconds),
- no other program is using a camera,
- the flash chip's ID and status, a direct read of the 4 KB block (about 30 seconds), and a write-enable test.

When all checks pass, a window asks **"All checks passed. Last chance: patch the original firmware to v1?"**. Click
OK to write, Cancel to stop (nothing is written then).

The write takes about **2.5 minutes** (on a slow port about 21 minutes): erase, write 16 pages and read each back, read the whole block back, then read
the whole flash back through the camera's own firmware and compare all 128 KB. The log shows each step. **Do not
unplug the camera or close the window during this time**; the window refuses to close while it writes.

### 3. Replug

At the end the tool says **VERIFIED** and asks you to unplug the camera and plug it in again: the camera reads its
firmware only when it powers up. The tool waits for that, then reads the camera's video modes and reports:

*Done: the camera now offers YUY2 1920x1080, the patch is active. You can close this program now.*

### 4. Use it

In the ASCOM driver's setup dialog (the chooser's *Properties* button, or the camera settings in PHD2, N.I.N.A. or
SharpCap) choose **YUY2 1920x1080 (uncompressed)**. Each frame takes at least 0.54 s in this mode; shorter
exposures are still exact. The driver's README has the details.

### Restore the original firmware

Select **Restore the original firmware from the backup** and click **2. Restore the original firmware...**. Any
backup of this camera works, made before or after patching: the tool rebuilds the original from it exactly and
checks the result against the known value. The steps are the same as for patching (checks, *Last chance*, write,
replug). Afterwards the camera lists YUY2 320x240 again instead of 1920x1080.

### Updating to a newer patch version

A newer version of SxFlash may bring a newer version of the patch (the window title shows the version, e.g.
*patch v1*). Make a backup with the new tool and click **2. Patch the camera...**: it recognises the older patch and
updates it in one write (*update the patch from v1 to v2*). If the camera already holds the newest version, it says
*nothing to do* and writes nothing.

## When something goes wrong

| What you see | What it means and what to do |
|---|---|
| *Slow USB port: ... takes about N minutes* | The camera is on a slow (chipset) USB port. It works, only slower; a rear port on the CPU's controller is faster. Keep the PC on until it is done. |
| *REFUSED: N Sonix cameras connected* | Unplug every Sonix camera except the one to patch. |
| *REFUSED ...: other programs are using a camera now: phd2.exe* | Close the program named there and try again. Nothing was written. |
| *check stopped: lost the flash controller* | Another program probably started or stopped the camera during the checks. Nothing was written; the camera is unchanged. Close every camera program and try again. |
| *REFUSED: the camera's flash differs from the original ... outside the sector* | The backup is from another camera or another firmware. Make a new backup of this camera. |
| *The write FAILED. Do NOT unplug the camera.* | The block may be half written. **Keep the camera plugged in and powered**, do not close the program. Select **Restore**, tick **A write failed and the camera's state is unknown**, and click **2. Restore the original firmware...** (the tool retries a failed write itself up to three times before it says this). Then save the log and report it. |
| The camera no longer appears after a replug | Not tested so far. Do not try further writes; report it with your backup file and the log. Recovery then needs an SPI flash programmer. |

## Reporting

Every run writes a log to `%LOCALAPPDATA%\Sonix290\Logs\sxflash-*.log`; **Save log...** saves the window's log
and **Open log folder** opens the folder. For a report (unsupported firmware, a failure, or simply "worked on my
camera"), please open an issue with the log and, for an unsupported firmware, the backup file.

## Command line

`SxFlash.exe` with arguments runs without the window (from cmd or PowerShell):

```
SxFlash backup FILE                 read the flash twice, save FILE (never overwrites)
SxFlash info FILE                   offline: which firmware and state FILE holds, and what the patch would change
SxFlash patch FILE [--write]        camera to the newest patch (FILE = a backup of this camera, any known state)
SxFlash restore FILE [--write] [--current any]
                                    camera to the original firmware; --current any only after a failed write
SxFlash verify FILE                 compare the camera's flash with FILE
SxFlash probe                       read-only check of the connection
```

Without `--write`, `patch` and `restore` run every check and write nothing.

## How it works

The camera's USB bridge (Sonix SN9C292B) exposes a vendor UVC extension unit that gives access to its registers,
including the controller of the SPI flash chip. SxFlash opens the camera **without ever starting the video
stream** (the firmware reads the flash when the stream starts or stops), waits until the flash controller is idle,
takes it over and talks to the flash chip with standard SPI commands. It checks the controller before and after
every command, erases only the one 4 KB block, programs and reads back every page, and finally compares the whole
128 KB as the camera's firmware reads it. The patch itself is a list of changed bytes per known firmware; after
applying it the ten checksums in the block are recomputed, and the result must match the recorded SHA-256 before
anything is written.

## Tested

- Two cameras with the same firmware (`0C45:6366`, `SONIX_IPC-292B----IMX291`), Windows 11, CPU USB port:
  backup, patch, restore (from the original and from a patched backup), "nothing to do", and the recovery restore;
  every write verified (about 142 s each). On a slow AMD chipset port (20 ms per register access): backup, restore
  and patch on one camera, both writes verified (about 31 minutes each). With the ASCOM driver both patched cameras deliver verified exposures
  from 0.1 s to 3 s in uncompressed 1080p.
- Not tested: a camera that no longer starts after a failed write, other firmware versions, Windows 10.

## Building

.NET SDK 8 or later on Windows, in this folder: `dotnet build -c Release` gives `bin\Release\net48\SxFlash.exe`
(a single file). `Driver\` is a frozen copy of the driver's camera access code (from driver 0.6.3), so changes to
the driver cannot change the flash tool without a deliberate update.

## Licence

MIT, see [LICENSE](../LICENSE). Provided as is, without warranty of any kind; you use it at your own risk.
