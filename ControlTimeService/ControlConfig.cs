using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlTimeService
{
    public class ControlConfig
    {
        [JsonPropertyName("server_url")]
        public string ServerUrl { get; set; }

        [JsonPropertyName("update_manifest_url")]
        public string UpdateManifestUrl { get; set; }

        [JsonPropertyName("update_package_url")]
        public string UpdatePackageUrl { get; set; }

        [JsonPropertyName("update_version")]
        public string UpdateVersion { get; set; }

        [JsonPropertyName("update_check_interval_minutes")]
        public int UpdateCheckIntervalMinutes { get; set; } = 60;

        public static ControlConfig Load()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "control_config.json");
                if (!File.Exists(configPath))
                    return new ControlConfig();

                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<ControlConfig>(json, JsonOptions) ?? new ControlConfig();
            }
            catch
            {
                return new ControlConfig();
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("package_url")]
        public string PackageUrl { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }
    }
}
