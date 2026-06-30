using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aura3DRobotConverter.Models;

namespace Aura3DRobotConverter.Services
{
    public class ParserBridge
    {
        private Process? _daemonProcess;
        private readonly HttpClient _httpClient = new HttpClient();
        private const int DaemonPort = 8081;

        public void StartDaemon(string workspacePath)
        {
            try
            {
                StopDaemon();

                string scriptPath = Path.Combine(workspacePath, "step_parser_daemon.py");
                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"[C# Bridge] Daemon script not found at {scriptPath}");
                    return;
                }

                _daemonProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\" {DaemonPort}",
                        WorkingDirectory = workspacePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                _daemonProcess.Start();
                Console.WriteLine("[C# Bridge] Started CAD Parser Python Daemon process.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# Bridge] Failed to start daemon: {ex.Message}");
            }
        }

        public void StopDaemon()
        {
            if (_daemonProcess != null && !_daemonProcess.HasExited)
            {
                try
                {
                    _daemonProcess.Kill();
                    _daemonProcess.Dispose();
                    _daemonProcess = null;
                    Console.WriteLine("[C# Bridge] Terminated CAD Parser Python Daemon.");
                }
                catch { }
            }
        }

        public async Task<RobotConfig?> ParseStepFileAsync(string stepFilePath, string outputDir)
        {
            try
            {
                byte[] stepBytes = await File.ReadAllBytesAsync(stepFilePath);
                string base64Data = Convert.ToBase64String(stepBytes);

                var payload = new
                {
                    step_data = base64Data,
                    output_dir = outputDir,
                    filename = Path.GetFileName(stepFilePath)
                };

                string jsonString = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.PostAsync($"http://localhost:{DaemonPort}/parse", content);
                if (!response.IsSuccessStatusCode)
                {
                    string errMsg = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Daemon returned error status: {response.StatusCode}. Details: {errMsg}");
                }

                string resJson = await response.Content.ReadAsStringAsync();
                using (JsonDocument doc = JsonDocument.Parse(resJson))
                {
                    JsonElement root = doc.RootElement;
                    if (root.GetProperty("success").GetBoolean())
                    {
                        JsonElement dataEl = root.GetProperty("data");
                        return JsonSerializer.Deserialize<RobotConfig>(dataEl.GetRawText());
                    }
                    else
                    {
                        throw new Exception(root.GetProperty("error").GetString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# Bridge Error] {ex.Message}");
                throw;
            }
        }
    }
}
