---
name: Test report
about: Report how the driver works with your camera (also when everything works)
title: "Test report: "
labels: test-report
---

**Camera**
- Hardware Ids from Device Manager (should contain `VID_0C45&PID_6366`):
- Where you bought it / product name:

**PC**
- Windows version:
- ASCOM Platform version:
- Driver version (shown in the chooser's Properties dialog title):

**Quick check** (command prompt in `C:\Program Files\Sonix290Camera`)

```
paste the output of: SxTest probe
```

```
paste the output of: SxTest driver 1000 --count 5
```

**Programs tested**

| Program | Exposure times | Time per frame | Errors / odd images |
|---------|----------------|----------------|---------------------|
| PHD2    |                |                |                     |
| N.I.N.A.|                |                |                     |
| SharpCap|                |                |                     |

**Logs**

Attach the `driver-*.log` / `sxtest-*.log` files from `%LOCALAPPDATA%\Sonix290\Logs` for the sessions above.

**Anything else**
