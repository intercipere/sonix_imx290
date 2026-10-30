# Changelog

## 0.7.4

- **Gain is now the sensor's own gain**, in 0.3 dB steps, written to the sensor together with the exposure timing
  and checked the same way. The sensor's high conversion gain is always on (about 6 dB, with less read noise than
  the same gain without it), so Gain 0..100 = 6..36 dB, all analog. Up to now Gain was the camera's UVC gain,
  which only stretches the camera's already 8-bit image and, in uncompressed 1920x1080, did not reach the sensor at
  all. For faint targets use Gain 40-90: at low gain the sensor noise is smaller than one 8-bit step, so dim areas
  show steps. Settings (Advanced): `SensorGain` (off = the old UVC gain), `SensorGainMax` (up to 240; above 100 the
  gain is digital), `HcgFromGain` (20 = no HCG below 6 dB, Gain 0 = 0 dB; -1 = never). The UVC gain is held at 0.
- **Gain at connect** in the setup dialog (default 50 = 21 dB). PHD2 never sets the gain of an ASCOM camera, so it
  always runs at this value; N.I.N.A. and SharpCap set their own.

## 0.7.3

- **Simpler setup dialog**: only the video mode (the modes this camera offers: MJPEG 1920x1080, YUY2 1920x1080 with the
  firmware patch, YUY2 640x480) and the log folder. All other settings are under "Advanced...". The dialog now saves
  only values that differ from the defaults, so values saved by an older version no longer override new defaults.
- Uncompressed 1920x1080: if no frame arrives within 5 s of connecting, the line length (HMAX) is written again (up
  to three times). On a slow USB port the first write can get lost (seen once in three connects in PHD2).
- The slow USB port warning (grainier image, GitHub issue #1) appears only in MJPEG, where the 15 fps workaround
  applies; the uncompressed modes are not affected.
- **Uncompressed 1920x1080** for cameras whose firmware offers YUY2 1920x1080 (the stock firmware lists YUY2 only
  up to 640x480). Select "YUY2 1920x1080" in the setup dialog. In this mode the camera
  delivers no frame at its default line length, so the driver writes a longer line (HMAX, setting `HdYuy2Hmax`,
  default 0xB500) right after every stream start and switches off the firmware's own per-frame sensor writes.
  1H is 484 us (setting `HdYuy2NsPerHmax`), so the shortest frame is 0.54 s and the longest exposure the sensor
  allows is over two minutes; tested 50 ms to 15 s. YUY2 luma uses the studio range 16-235.
- In this mode the driver also lets the camera read 1104 sensor lines per frame (output still 1080, setting
  `HdYuy2InputLines`). With the firmware's 1080 the camera sent the last lines of each frame in one burst and,
  depending on the USB timing, cut it short, so Windows dropped about a quarter of the frames (in bursts every
  ~5 s). Measured with the larger window: 1 of 301 frames lost. Should a frame still go missing, the driver waits
  for the next one instead of rewriting.
- `ExposureMax` follows the connected mode's line time.
- The capture graph runs without a reference clock. Windows stamps each frame at the camera's listed frame interval,
  and with a clock the graph held every frame back to its stamp: a mode that lists a longer interval than the camera
  really runs at (1080p YUY2 lists 1 fps) delivered at most one frame per second, so exposures with a shorter frame
  failed verification (SharpCap: "camera did not take the exposure" at 0.3 s).

## 0.6.3

- **Timing writes are reliable on fast ports too.** The camera firmware's own per-frame sensor write overwrote or
  garbled about a third of the driver's writes at 30 fps (measured: 6/20 single writes took, 17/20 with three;
  occasionally a wrong frame length). The driver now writes VMAX/SHS1 once while the sensor is in STANDBY
  (STANDBY on, write, STANDBY off): 135/135 test writes took, and every timing change in the driver tests took on
  the first write. The first exposure after connecting is ~0.2 s faster. Setting `StandbyWrites` (default on).
- The camera's stuck 8-byte frames (which follow about 1 in 20 such writes) are now cleared after the first one
  instead of the second, saving one exposure period.
- Disconnecting resets the camera to 30 fps the same way, so the next program finds it at its default.
- SxTest: `wstat` (write statistics), `driver --seq` (cycle exposure times).

## 0.6.2

- Connecting in slow USB port mode shows a warning box (the client waits until OK is clicked): PHD2 does not
  display the camera description, so the 0.6.1 warning was not visible there. Setting `WarnSlowPort` (default
  on) turns the box off, e.g. for automated tests.

## 0.6.1

- **Slow USB ports work, with a warning.** On some USB controllers (AMD 500-series chipset ports) every
  register access takes ~10-25 ms instead of ~1-2 ms, and the camera firmware's own per-frame I2C job wiped the driver's half-written
  sensor writes: no exposure longer than 33 ms could be set ("camera did not take the exposure"). The driver now
  times register access at connect and, on a slow port, streams at 15 fps, sets the first timing during a
  retried STANDBY, and keeps the frame period at least 0.25 s (`SlowPortReadMs`, `SlowPortMinPeriodMs`).
  The image can look grainier in this mode (under investigation), so it is not recommended for guiding: the
  camera description shows *"slow USB port"*, the log a warning, and the README recommends a port on the CPU's
  USB controller. (0.6.0 was a local test build only.)
- New setting `FrameIntervalMs` (UVC frame interval at connect; at 15 fps the sensor line time doubles, which
  the driver takes into account).
- SxTest: new test commands `sbwrite`, `sbstat`, `asicw`, `xuwatch`, `uvcfps` and options `--xudelay`,
  `--short`, `exp --pause`; `formats` shows the frame interval range.
- The README in the package no longer mentions the removed issue template.
- Known issue: on a fast port the first timing write after plugging in occasionally does not take or is followed
  by 8-byte frames that a stream restart does not always clear; reconnecting helps (fixed in 0.6.3).

## 0.5.0 – first public test release

- Chooser name is now **Sonix IMX290** (was "Sonix IMX290 (UVC, long exposure)"). ProgID
  `ASCOM.Sonix290.Camera` and install folder are unchanged.
- Release binaries no longer embed local build paths.
- Setup .exe (Inno Setup): installs and registers for 64- and 32-bit programs, appears in *Settings → Apps*,
  offers to close programs that have the driver loaded, checks for .NET 4.8 and the ASCOM Platform. Upgrades and
  uninstall leave no stale COM registrations. The zip with `install.cmd` stays available.

## 0.4.2

- `CameraState` is returned as a 32-bit value, as ASCOM defines it. With a 16-bit value .NET clients such as
  ConformU could not read it and skipped all exposure tests. ConformU 4.5.3 now reports no errors or issues.

## 0.4.1

- A bad timing write is detected after one far-off frame instead of after a full averaging window
  (recovery in about a second instead of up to 7 s for short exposures).

## 0.4.0

- Exposures of 0.5 s and longer restart the sensor's frame cycle with a STANDBY toggle at the request
  (`StandbyRestart`). Each frame starts when the client asks; PHD2 at 1 s went from 1.4-1.7 s alternating to a
  steady ~1.4 s per frame, at full exposure.
- Frames with a broken payload trigger one stream restart instead of failing the exposure.

## 0.3.0

- `AutoExposurePriorityOff` is on by default again: without it PHD2 received only empty 8-byte frames.
- Broken payloads no longer cause pointless timing rewrites.

## 0.2.0

- Exposure verification uses host arrival times (DirectShow sample times are synthetic for short frames) with
  a tolerance for Windows' ~16 ms timestamp grid; exposures below 0.5 s no longer fail.
- Repeat exposures may use the frame already running (`AcceptEarlyStartPct`), instead of always waiting for
  the next one.
- Setting an unchanged gain does nothing, and a gain change no longer rewrites the timing.
- Burst I2C writes by default; YUY2 decoding in `SxTest`.

## 0.1.0

- First working driver: long exposures up to 7.6 s via the sensor frame length, verified by frame interval.
