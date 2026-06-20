using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlCenter
{
    public class UpdateServerConfig
    {
        [JsonPropertyName("default_package_url")]
        public string DefaultPackageUrl { get; set; }

        [JsonPropertyName("default_version")]
        public string DefaultVersion { get; set; }

        [JsonPropertyName("default_manifest_url")]
        public string DefaultManifestUrl { get; set; }

        [JsonPropertyName("local_package_filename")]
        public string LocalPackageFilename { get; set; } = "ControlTimeService.zip";

        [JsonPropertyName("auto_push_on_save")]
        public bool AutoPushOnSave { get; set; } = true;

        [JsonPropertyName("last_pushed_version")]
        public string LastPushedVersion { get; set; }

        public string GetLocalPackagePath()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
            var fileName = string.IsNullOrWhiteSpace(LocalPackageFilename)
                ? "ControlTimeService.zip"
                : LocalPackageFilename;
            return Path.Combine(dir, fileName);
        }

        public string ResolvePackageUrl(string serverBaseUrl)
        {
            var fileName = string.IsNullOrWhiteSpace(LocalPackageFilename)
                ? "ControlTimeService.zip"
                : LocalPackageFilename;

            if (!string.IsNullOrWhiteSpace(DefaultPackageUrl))
            {
                var url = DefaultPackageUrl.Trim();
                if (url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("127.0.0.1", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(serverBaseUrl))
                    {
                        var uri = new Uri(url);
                        url = $"{serverBaseUrl.TrimEnd('/')}{uri.AbsolutePath}{uri.Query}";
                    }
                }

                return NormalizePackageUrl(url, fileName);
            }

            var localPath = GetLocalPackagePath();
            if (File.Exists(localPath) && !string.IsNullOrWhiteSpace(serverBaseUrl))
            {
                return $"{serverBaseUrl.TrimEnd('/')}/api/updates/{Uri.EscapeDataString(fileName)}";
            }

            return null;
        }

        /// <summary>
        /// 若 URL 只是服务器根地址（无 .zip 路径），自动补全为 /api/updates/{fileName}
        /// </summary>
        private static string NormalizePackageUrl(string url, string fileName)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return url;

            if (url.Contains("/api/updates/", StringComparison.OrdinalIgnoreCase))
                return url;

            return $"{url.TrimEnd('/')}/api/updates/{Uri.EscapeDataString(fileName)}";
        }

        public string ResolveManifestUrl(string serverBaseUrl)
        {
            if (!string.IsNullOrWhiteSpace(DefaultManifestUrl))
                return DefaultManifestUrl.Trim();

            if (!string.IsNullOrWhiteSpace(serverBaseUrl))
                return $"{serverBaseUrl.TrimEnd('/')}/api/updates/manifest.json";

            return null;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static UpdateServerConfig Load()
        {
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path))
                    return new UpdateServerConfig();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<UpdateServerConfig>(json, JsonOptions) ?? new UpdateServerConfig();
            }
            catch
            {
                return new UpdateServerConfig();
            }
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(GetConfigPath(), json);
        }

        private static string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_config.json");
        }
    }
}
