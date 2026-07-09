$ErrorActionPreference = "Stop"

Write-Output "Stopping any existing instances..."
Stop-Process -Name "Aura3DRobotConverter" -Force -ErrorAction SilentlyContinue

Write-Output "Starting the application with test model..."
cd "c:\Users\samsung\proj\model3d\Aura3DRobotConverter"
Start-Process -FilePath "dotnet" -ArgumentList "run -- C:\Users\samsung\proj\model3d\urdf\differential_drive.urdf" -WindowStyle Hidden

Write-Output "Waiting for the application to build, load, and open the dialog (10 seconds)..."
Start-Sleep -Seconds 10

Write-Output "Finding and clicking QACapture button via UIAutomation..."
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "QACapture")

$btn = $null
for ($i = 0; $i -lt 15; $i++) {
    $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($btn -ne $null) { break }
    Start-Sleep -Seconds 1
}

if ($btn -ne $null) {
    try {
        $invokePattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) -as [System.Windows.Automation.InvokePattern]
        $invokePattern.Invoke()
        Write-Output "Successfully clicked QACapture button!"
        Start-Sleep -Seconds 3
    } catch {
        Write-Output "Failed to invoke QACapture button: $_"
    }
} else {
    Write-Output "Could not find QACapture button."
}

$savePath = "C:\Users\samsung\Downloads\nlp_automation_capture.png"
$artifactPath = "C:\Users\samsung\.gemini\antigravity\brain\e3ce6f0d-669c-4a05-a757-a7ce5759dba9\aura3d_urdf_capture.png"

if (Test-Path $savePath) {
    Copy-Item -Path $savePath -Destination $artifactPath -Force
    Write-Output "Screenshot successfully saved to $savePath and copied to artifact directory."
} else {
    Write-Output "Error: Captured screenshot not found at $savePath"
}

Write-Output "Stopping the application..."
Stop-Process -Name "Aura3DRobotConverter" -Force -ErrorAction SilentlyContinue

Write-Output "Screenshot successfully saved to $savePath"
