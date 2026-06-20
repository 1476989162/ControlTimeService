using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlCenter
{
    public class ServerConfig
    {
        [JsonPropertyName("listen_port")]
        public int ListenPort { get; set; } = 9528;

        [JsonPropertyName("web_admin_password")]
        public string WebAdminPassword { get; set; } = "lnbxSoftLizhenNiping";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static ServerConfig Load()
        {
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path))
                    return new ServerConfig();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();
            }
            catch
            {
                return new ServerConfig();
            }
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(GetConfigPath(), json);
        }

        private static string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_config.json");
        }
    }
}
