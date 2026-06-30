using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aura3DRobotConverter.Models;

namespace Aura3DRobotConverter.Services
{
    public class ExporterService
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private const int DaemonPort = 8081;

        public async Task<string> ExportRobotModelAsync(RobotConfig config, string sessionDir)
        {
            try
            {
                var payload = new
                {
                    config = config,
                    output_dir = sessionDir
                };

                string jsonString = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.PostAsync($"http://localhost:{DaemonPort}/export", content);
                if (!response.IsSuccessStatusCode)
                {
                    string errMsg = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Exporter daemon returned status: {response.StatusCode}. Details: {errMsg}");
                }

                string resJson = await response.Content.ReadAsStringAsync();
                using (JsonDocument doc = JsonDocument.Parse(resJson))
                {
                    JsonElement root = doc.RootElement;
                    if (root.GetProperty("success").GetBoolean())
                    {
                        return root.GetProperty("zip_filename").GetString() ?? "urdf_package.zip";
                    }
                    else
                    {
                        throw new Exception(root.GetProperty("error").GetString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# Exporter Error] {ex.Message}");
                throw;
            }
        }
    }
}
