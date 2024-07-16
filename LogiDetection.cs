using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("LogiDetection", "herbs.acab", "1.0.3")]
    [Description("Detects potential use of anti-recoil macros by monitoring player behavior and sends alerts via a webhook")]

    public class LogiDetection : RustPlugin
    {
        private const float AccuracyThreshold = 0.8f; // Example threshold for suspicious accuracy
        private const float ClickIntervalThreshold = 0.1f; // Threshold for suspiciously consistent click intervals
        private const int CheckInterval = 10; // Time in seconds between checks
        private const int CheckWindow = 60; // Time window in seconds for accuracy checks
        private const int MinShotsForCheck = 10; // Minimum shots required to perform checks

        private Dictionary<ulong, List<ShotRecord>> playerShots = new Dictionary<ulong, List<ShotRecord>>();
        private Dictionary<ulong, List<float>> clickIntervals = new Dictionary<ulong, List<float>>();

        private class ShotRecord
        {
            public float Time;
            public bool Hit;
            public ShotRecord(float time, bool hit)
            {
                Time = time;
                Hit = hit;
            }
        }

        private class ConfigData
        {
            public string WebhookUrl { get; set; }
        }

        private ConfigData config;

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData
            {
                WebhookUrl = "https://your-webhook-url"
            };
            SaveConfig(config);
        }

        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }

        private void LoadConfig()
        {
            config = Config.ReadObject<ConfigData>();
        }

        void OnServerInitialized()
        {
            LoadConfig();
            timer.Every(CheckInterval, CheckPlayers);
        }

        void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null || !info.IsProjectile()) return;

            if (!playerShots.ContainsKey(attacker.userID))
                playerShots[attacker.userID] = new List<ShotRecord>();

            bool hit = info.HitEntity != null && info.HitEntity is BasePlayer;
            playerShots[attacker.userID].Add(new ShotRecord(Time.realtimeSinceStartup, hit));

            // Track click intervals
            if (!clickIntervals.ContainsKey(attacker.userID))
                clickIntervals[attacker.userID] = new List<float>();

            if (clickIntervals[attacker.userID].Count > 0)
            {
                float interval = Time.realtimeSinceStartup - clickIntervals[attacker.userID][clickIntervals[attacker.userID].Count - 1];
                clickIntervals[attacker.userID].Add(interval);
            }
            else
            {
                clickIntervals[attacker.userID].Add(Time.realtimeSinceStartup);
            }

            // Remove old records beyond the check window
            playerShots[attacker.userID].RemoveAll(record => record.Time < Time.realtimeSinceStartup - CheckWindow);
        }

        private void CheckPlayers()
        {
            foreach (var entry in playerShots)
            {
                var player = BasePlayer.FindByID(entry.Key);
                if (player == null) continue;

                var shotRecords = entry.Value;
                if (shotRecords.Count < MinShotsForCheck) continue; // Not enough data to evaluate

                int hitCount = shotRecords.FindAll(record => record.Hit).Count;
                float accuracy = (float)hitCount / shotRecords.Count;

                if (accuracy > AccuracyThreshold)
                {
                    Puts($"[Suspicious] Player '{player.displayName}' ({player.UserIDString}) has suspicious accuracy: {accuracy * 100}%");
                    SendWebhookAlert(player, accuracy, "Suspicious Accuracy");
                }

                // Check for consistent click intervals
                if (clickIntervals.ContainsKey(player.userID))
                {
                    var intervals = clickIntervals[player.userID];
                    if (intervals.Count > 1)
                    {
                        float avgInterval = 0;
                        for (int i = 1; i < intervals.Count; i++)
                        {
                            avgInterval += intervals[i] - intervals[i - 1];
                        }
                        avgInterval /= intervals.Count - 1;

                        if (avgInterval < ClickIntervalThreshold)
                        {
                            Puts($"[Suspicious] Player '{player.displayName}' ({player.UserIDString}) has consistent click intervals: {avgInterval}");
                            SendWebhookAlert(player, avgInterval, "Consistent Click Intervals");
                        }
                    }
                }
            }
        }

        private void SendWebhookAlert(BasePlayer player, float value, string reason)
        {
            if (string.IsNullOrEmpty(config.WebhookUrl)) return;

            var payload = new
            {
                title = "Suspicious Activity Detected",
                color = 16711680, // Red color
                fields = new List<object>
                {
                    new { name = "Player", value = player.displayName, inline = true },
                    new { name = "Steam ID", value = player.UserIDString, inline = true },
                    new { name = "Reason", value = reason, inline = true },
                    new { name = "Value", value = $"{value}", inline = true },
                    new { name = "Time", value = DateTime.Now.ToString("g"), inline = true }
                }
            };

            var jsonPayload = JsonConvert.SerializeObject(payload);

            using (var webClient = new WebClient())
            {
                webClient.Headers[HttpRequestHeader.ContentType] = "application/json";
                webClient.UploadString(config.WebhookUrl, jsonPayload);
            }
        }
    }
}
