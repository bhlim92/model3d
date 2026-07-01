$ErrorActionPreference = "Stop"

Write-Output "Stopping any existing instances..."
Stop-Process -Name "Aura3DRobotConverter" -Force -ErrorAction SilentlyContinue

Write-Output "Starting the application..."
cd "c:\Users\samsung\proj\model3d\Aura3DRobotConverter"
Start-Process -FilePath "dotnet" -ArgumentList "run" -WindowStyle Hidden

Write-Output "Waiting for the application to build, load, and open the dialog (10 seconds)..."
Start-Sleep -Seconds 10

Write-Output "Capturing screen..."
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screenBounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $screenBounds.Width, $screenBounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($screenBounds.Location, [System.Drawing.Point]::Empty, $screenBounds.Size)

$savePath = "C:\Users\samsung\Downloads\dialog_test_result.png"
$artifactPath = "C:\Users\samsung\.gemini\antigravity\brain\bd87449f-d102-4343-86a1-c774119df9e2\dialog_test_result.png"

$bitmap.Save($savePath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Save($artifactPath, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Output "Stopping the application..."
Stop-Process -Name "Aura3DRobotConverter" -Force -ErrorAction SilentlyContinue

Write-Output "Screenshot successfully saved to $savePath"
