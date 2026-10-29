' Native IDispatch test of the registered driver, the way PHD2 (32-bit) talks to it.
'   64-bit: cscript //nologo comtest.vbs [seconds] [count]
'   32-bit: C:\Windows\SysWOW64\cscript.exe //nologo comtest.vbs [seconds] [count]
Dim secs, count, cam, i, t0, img, x, y, sum, n
secs = 1.0 : count = 3
If WScript.Arguments.Count > 0 Then secs = CDbl(WScript.Arguments(0))
If WScript.Arguments.Count > 1 Then count = CInt(WScript.Arguments(1))
Set cam = CreateObject("ASCOM.Sonix290.Camera")
WScript.Echo "driver: " & cam.Name & " " & cam.DriverVersion & ", interface " & cam.InterfaceVersion
cam.Connected = True
WScript.Echo "size " & cam.CameraXSize & "x" & cam.CameraYSize & ", exposure " & cam.ExposureMin & ".." & cam.ExposureMax & " s, gain " & cam.GainMin & ".." & cam.GainMax
For i = 1 To count
  t0 = Timer
  cam.StartExposure secs, True
  Do While Not cam.ImageReady
    WScript.Sleep 50
  Loop
  img = cam.ImageArray
  WScript.Echo "ImageArray (Int32) bounds " & (UBound(img, 1) + 1) & "x" & (UBound(img, 2) + 1)
  img = cam.ImageArrayVariant
  sum = 0 : n = 0
  For x = 0 To UBound(img, 1) Step 16
    For y = 0 To UBound(img, 2) Step 16
      sum = sum + img(x, y) : n = n + 1
    Next
  Next
  WScript.Echo "exposure " & i & ": " & FormatNumber(Timer - t0, 2) & " s total, LastExposureDuration " & cam.LastExposureDuration & _
    ", start " & cam.LastExposureStartTime & ", array " & (UBound(img, 1) + 1) & "x" & (UBound(img, 2) + 1) & ", mean " & FormatNumber(sum / n, 2)
Next
cam.Connected = False
WScript.Echo "ok"
