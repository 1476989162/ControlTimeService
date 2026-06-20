using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlTimeService
{
    public class StoredClientRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("computerName")]
        public string ComputerName { get; set; }

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; }

        [JsonPropertyName("firstSeen")]
        public DateTime FirstSeen { get; set; }

        [JsonPropertyName("lastSeen")]
        public DateTime LastSeen { get; set; }

        [JsonPropertyName("config")]
        public Dictionary<string, DaySchedule> Config { get; set; }

        [JsonPropertyName("appPolicy")]
        public AppPolicy AppPolicy { get; set; }

        [JsonPropertyName("clientMessages")]
        public List<ClientMessage> ClientMessages { get; set; }

        public ClientInfo ToClientInfo(bool isOnline)
        {
            return new ClientInfo
            {
                Id = Id,
                Name = ComputerName,
                IpAddress = IpAddress,
                FirstSeen = FirstSeen,
                LastHeartbeat = LastSeen,
                Status = isOnline ? "Online" : "Offline",
                Config = Config,
                AppPolicy = AppPolicy,
                ClientMessages = ClientMessages
            };
        }
    }

    public class ClientRegistryManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly string _path;
        private Dictionary<string, StoredClientRecord> _records = new Dictionary<string, StoredClientRecord>();

        public ClientRegistryManager()
        {
            _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clients_registry.json");
            Load();
        }

        public IReadOnlyCollection<StoredClientRecord> GetAllRecords() => _records.Values;

        public StoredClientRecord Get(string clientId)
        {
            return _records.TryGetValue(clientId, out var record) ? record : null;
        }

        public StoredClientRecord UpsertFromRegistration(ClientInfo client)
        {
            if (string.IsNullOrEmpty(client?.Id))
                return null;

            if (!_records.TryGetValue(client.Id, out var record))
            {
                record = new StoredClientRecord
                {
                    Id = client.Id,
                    FirstSeen = DateTime.Now
                };
                _records[client.Id] = record;
            }

            if (!string.IsNullOrWhiteSpace(client.Name))
                record.ComputerName = client.Name;

            if (!string.IsNullOrWhiteSpace(client.IpAddress))
                record.IpAddress = client.IpAddress;

            record.LastSeen = DateTime.Now;
            Save();
            return record;
        }

        public void UpdateComputerName(string clientId, string computerName, string ipAddress = null)
        {
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrWhiteSpace(computerName))
                return;

            if (!_records.TryGetValue(clientId, out var record))
            {
                record = new StoredClientRecord
                {
                    Id = clientId,
                    FirstSeen = DateTime.Now
                };
                _records[clientId] = record;
            }

            record.ComputerName = computerName;
            if (!string.IsNullOrWhiteSpace(ipAddress))
                record.IpAddress = ipAddress;

            record.LastSeen = DateTime.Now;
            Save();
        }

        public void UpdateLastSeen(string clientId)
        {
            if (_records.TryGetValue(clientId, out var record))
            {
                record.LastSeen = DateTime.Now;
                Save();
            }
        }

        public void SaveConfig(string clientId, Dictionary<string, DaySchedule> config)
        {
            EnsureRecord(clientId);
            _records[clientId].Config = config;
            Save();
        }

        public void SaveAppPolicy(string clientId, AppPolicy policy)
        {
            EnsureRecord(clientId);
            _records[clientId].AppPolicy = policy;
            Save();
        }

        public void SaveMessages(string clientId, List<ClientMessage> messages)
        {
            EnsureRecord(clientId);
            _records[clientId].ClientMessages = messages;
            Save();
        }

        public List<ClientMessage> GetMessages(string clientId)
        {
            return _records.TryGetValue(clientId, out var record) ? record.ClientMessages : null;
        }

        private void EnsureRecord(string clientId)
        {
            if (!_records.ContainsKey(clientId))
            {
                _records[clientId] = new StoredClientRecord
                {
                    Id = clientId,
                    FirstSeen = DateTime.Now,
                    LastSeen = DateTime.Now
                };
            }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;

            try
            {
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<StoredClientRecord>>(json, JsonOptions);
                if (list == null) return;

                _records = list
                    .Where(r => !string.IsNullOrEmpty(r.Id))
                    .GroupBy(r => r.Id)
                    .ToDictionary(g => g.Key, g => g.Last());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载客户端注册表失败: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions);
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存客户端注册表失败: {ex.Message}");
            }
        }
    }
}
