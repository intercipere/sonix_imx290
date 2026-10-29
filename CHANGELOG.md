# Changelog

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
