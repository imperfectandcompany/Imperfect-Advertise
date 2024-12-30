using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MaxMind.GeoIP2;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Imperfect_Advertise
{
    /// <summary>
    /// Main plugin class implementing IPluginConfig&lt;Config&gt;.
    /// </summary>
    public partial class Ads : BasePlugin, IPluginConfig<Config>
    {
        public override string ModuleAuthor => "thesamefabius (refactored by Imperfect style)";
        public override string ModuleName => "Imperfect-Advertisements";
        public override string ModuleVersion => "1.0.9";

        /// <summary>
        /// Holds the plugin's runtime config once parsed.
        /// </summary>
        public Config Config { get; set; } = null!;

        /// <summary>
        /// Example: A FakeConVar to let users override the IP logic at runtime.
        /// e.g.: +imperfect_ads_ip "123.45.67.8"
        /// </summary>
        public FakeConVar<string> ImperfectAdsIp = new(
            "imperfect_ads_ip",
            "Specifies an IP override for the advertisements plugin",
            "",
            ConVarFlags.FCVAR_NONE
        );

        /// <summary>
        /// Per-player data. (Index = player slot)
        /// </summary>
        private readonly User?[] _users = new User?[66];

        private readonly List<Timer> _timers = new();
        private readonly Dictionary<ulong, string> _playerIsoCode = new();

        public override void Load(bool hotReload)
        {
            // Register the FakeConVar so the engine sees it (and can set it from command line or server cfg).
            RegisterFakeConVars(GetType());

            // Hook up events
            RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFull);
            RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);
            RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
            RegisterListener<Listeners.OnTick>(OnTick);

            // Start timers if config was already loaded. If OnConfigParsed not yet called, no big deal—will re-init if needed.
            if (Config?.Ads != null)
                StartTimers();

            // On hot reload, re-initialize the _users array for connected players
            if (hotReload)
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    _users[player.Slot] = new User();
                }
            }
        }

        /// <summary>
        /// Called by the CounterStrikeSharp framework once the config is parsed.
        /// This is where we finalize (or override) the plugin config logic.
        /// </summary>
        public void OnConfigParsed(Config config)
        {
            // Store the config
            Config = config;

            // If our new ConVar is set, we can override something in config if needed.
            string overrideIp = ImperfectAdsIp.Value?.Trim() ?? "";
            if (!string.IsNullOrEmpty(overrideIp))
            {
                // e.g. if your plugin needed to store an IP in Config, we do:
                // Config.SomeIp = overrideIp;
                // or just store it in a field
                Console.WriteLine($"[Imperfect-Advertisements] Overriding IP with: {overrideIp}");
            }

            // If the plugin is already loaded and timers are running, let's kill & re-start them
            foreach (var t in _timers) t.Kill();
            _timers.Clear();

            // Start again
            if (Config.Ads != null)
                StartTimers();
        }

        public override void Unload(bool hotReload)
        {
            // Kill timers
            foreach (var t in _timers)
                t.Kill();
            _timers.Clear();

            base.Unload(hotReload);
        }

        private HookResult EventPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
        {
            if (Config.LanguageMessages == null) return HookResult.Continue;
            var player = ev.Userid;
            if (player is null) return HookResult.Continue;

            _playerIsoCode.Remove(player.SteamID);
            return HookResult.Continue;
        }

        private void OnClientAuthorized(int slot, SteamID id)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            _users[slot] = new User();

            if (Config.LanguageMessages == null) return;

            if (player?.IpAddress != null)
            {
                var ipOnly = player.IpAddress.Split(':')[0];
                _playerIsoCode.TryAdd(id.SteamId64, GetPlayerIsoCode(ipOnly));
            }
        }

        private HookResult EventPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
        {
            if (Config.WelcomeMessage == null) return HookResult.Continue;

            var player = ev.Userid;
            if (player is null || !player.IsValid || player.SteamID == null)
                return HookResult.Continue;

            var welcomeMsg = Config.WelcomeMessage;
            var msg = welcomeMsg.Message
                .Replace("{PLAYERNAME}", player.PlayerName)
                .ReplaceColorTags();

            PrintWrappedLine(0, msg, player, isWelcome: true);
            return HookResult.Continue;
        }

        private void OnTick()
        {
            foreach (var player in Utilities.GetPlayers())
            {
                var user = _users[player.Slot];
                if (user == null) continue;
                if (!user.HtmlPrint) continue;

                bool showWhenDead = Config.ShowHtmlWhenDead ?? false;
                bool playerIsDead = !player.PawnIsAlive;

                // If we only want to show while alive, skip if they're dead.
                // If showWhenDead is true, we allow it to continue while they're dead.
                if (!showWhenDead && playerIsDead)
                    continue;

                var duration = Config.HtmlCenterDuration ?? 0f;
                double allowedSeconds = duration;

                if (TimeSpan.FromSeconds(user.PrintTime / 64.0).TotalSeconds < allowedSeconds)
                {
                    player.PrintToCenterHtml(user.Message);
                    user.PrintTime++;
                }
                else
                {
                    user.HtmlPrint = false;
                }
            }
        }

        private void ShowAd(Advertisement ad)
        {
            var messages = ad.NextMessages;
            foreach (var (type, message) in messages)
            {
                switch (type)
                {
                    case "Chat":
                        PrintWrappedLine(HudDestination.Chat, message);
                        break;
                    case "Center":
                        PrintWrappedLine(HudDestination.Center, message);
                        break;
                }
            }
        }

        private void StartTimers()
        {
            if (Config.Ads == null) return;
            foreach (var ad in Config.Ads)
            {
                _timers.Add(AddTimer(ad.Interval, () => ShowAd(ad), TimerFlags.REPEAT));
            }
        }

        [ConsoleCommand("css_advert_reload", "Reload advertisement config")]
        public void ReloadAdvertConfig(CCSPlayerController? controller, CommandInfo command)
        {
            // In Imperfect style, typically the engine calls OnConfigParsed() for you after a config parse,
            // but if you still want a manual refresh, do it here:

            // 1) Re-read the config from disk if you want
            var newConfig = LoadConfigFromDisk();
            // 2) Then manually call OnConfigParsed to re-init
            OnConfigParsed(newConfig);

            // [Optional] re-init any IP lookups
            if (Config.LanguageMessages != null)
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player.IpAddress == null || player.AuthorizedSteamID == null) continue;
                    var ipOnly = player.IpAddress.Split(':')[0];
                    _playerIsoCode.TryAdd(player.AuthorizedSteamID.SteamId64, GetPlayerIsoCode(ipOnly));
                }
            }

            const string msg = "\x08[\x0C Advert \x08] config reloaded!";
            if (controller == null)
                Console.WriteLine(msg);
            else
                controller.PrintToChat(msg);
        }

        private void PrintWrappedLine(HudDestination? destination, string message,
            CCSPlayerController? connectPlayer = null, bool isWelcome = false)
        {
            if (connectPlayer != null && !connectPlayer.IsBot && isWelcome)
            {
                var welcomeMessage = Config.WelcomeMessage;
                if (welcomeMessage == null) return;

                AddTimer(welcomeMessage.DisplayDelay, () =>
                {
                    if (connectPlayer == null || !connectPlayer.IsValid || connectPlayer.SteamID == null) return;
                    var processed = ProcessMessage(message, connectPlayer.SteamID)
                        .Replace("{PLAYERNAME}", connectPlayer.PlayerName);

                    switch (welcomeMessage.MessageType)
                    {
                        case MessageType.Chat:
                            connectPlayer.PrintToChat(processed);
                            break;
                        case MessageType.Center:
                            connectPlayer.PrintToChat(processed);
                            break;
                        case MessageType.CenterHtml:
                            SetHtmlPrintSettings(connectPlayer, processed);
                            break;
                    }
                });
            }
            else
            {
                // For normal ads
                foreach (var player in Utilities.GetPlayers()
                             .Where(u => !isWelcome && !u.IsBot && u.IsValid && u.SteamID != null))
                {
                    var processed = ProcessMessage(message, player.SteamID);
                    if (destination == HudDestination.Chat)
                    {
                        player.PrintToChat($" {processed}");
                    }
                    else
                    {
                        if (Config.PrintToCenterHtml == true)
                            SetHtmlPrintSettings(player, processed);
                        else
                            player.PrintToCenter(processed);
                    }
                }
            }
        }

        private void SetHtmlPrintSettings(CCSPlayerController player, string message)
        {
            var user = _users[player.Slot];
            if (user == null)
            {
                _users[player.Slot] = new User { HtmlPrint = true, PrintTime = 0, Message = message };
                return;
            }
            user.HtmlPrint = true;
            user.PrintTime = 0;
            user.Message = message;
        }

        private string ProcessMessage(string message, ulong steamId)
        {
            if (Config.LanguageMessages == null)
                return ReplaceMessageTags(message);

            var matches = Regex.Matches(message, @"\{([^}]*)\}");
            foreach (Match match in matches)
            {
                var tag = match.Groups[0].Value;
                var tagName = match.Groups[1].Value;

                if (!Config.LanguageMessages.TryGetValue(tagName, out var language)) continue;

                var isoCode = _playerIsoCode.TryGetValue(steamId, out var playerCountryIso)
                    ? playerCountryIso
                    : Config.DefaultLang;

                if (isoCode != null && language.TryGetValue(isoCode, out var tagReplacement))
                    message = message.Replace(tag, tagReplacement);
                else if (Config.DefaultLang != null &&
                         language.TryGetValue(Config.DefaultLang, out var defaultReplacement))
                    message = message.Replace(tag, defaultReplacement);
            }

            return ReplaceMessageTags(message);
        }

        private string ReplaceMessageTags(string message)
        {
            var mapName = NativeAPI.GetMapName();
            var replaced = message
                .Replace("{MAP}", mapName)
                .Replace("{TIME}", DateTime.Now.ToString("HH:mm:ss"))
                .Replace("{DATE}", DateTime.Now.ToString("dd.MM.yyyy"))
                .Replace("{SERVERNAME}", ConVar.Find("hostname")?.StringValue ?? "CS2Server")
                .Replace("{IP}", ConVar.Find("ip")?.StringValue ?? "127.0.0.1")
                .Replace("{PORT}", ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "27015")
                .Replace("{MAXPLAYERS}", Server.MaxPlayers.ToString())
                .Replace("{PLAYERS}", Utilities.GetPlayers()
                    .Count(u => u.PlayerPawn.Value != null && u.PlayerPawn.Value.IsValid).ToString())
                .Replace("\n", "\u2029");

            replaced = replaced.ReplaceColorTags();

            if (Config.MapsName != null && Config.MapsName.TryGetValue(mapName, out var customName))
            {
                replaced = replaced.Replace(mapName, customName);
            }

            return replaced;
        }

        private Config LoadConfigFromDisk()
        {
            // This is the function your “engine” calls to parse the plugin’s config from disk
            // or create a default if missing. You might keep it private or protected. 
            var directory = Path.Combine(Application.RootDirectory, "configs/plugins/Advertisement");
            Directory.CreateDirectory(directory);

            var configPath = Path.Combine(directory, "Advertisement.json");
            if (!File.Exists(configPath))
                return CreateConfig(configPath);

            var jsonData = File.ReadAllText(configPath);
            var opts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
            var config = JsonSerializer.Deserialize<Config>(jsonData, opts);
            return config ?? new Config();
        }

        private Config CreateConfig(string configPath)
        {
            var config = new Config
            {
                PrintToCenterHtml = false,
                WelcomeMessage = new WelcomeMessage
                {
                    MessageType = MessageType.Chat,
                    Message = "Welcome, {BLUE}{PLAYERNAME}",
                    DisplayDelay = 5
                },
                Ads = new List<Advertisement>
                {
                    new Advertisement
                    {
                        Interval = 35,
                        Messages = new List<Dictionary<string, string>>
                        {
                            new()
                            {
                                ["Chat"] = "{map_name}",
                                ["Center"] = "Section 1 Center 1"
                            },
                            new()
                            {
                                ["Chat"] = "{current_time}"
                            }
                        }
                    },
                    new Advertisement
                    {
                        Interval = 40,
                        Messages = new List<Dictionary<string, string>>
                        {
                            new()
                            {
                                ["Chat"] = "Section 2 Chat 1"
                            },
                            new()
                            {
                                ["Chat"] = "Section 2 Chat 2",
                                ["Center"] = "Section 2 Center 1"
                            }
                        }
                    }
                },
                DefaultLang = "US",
                LanguageMessages = new Dictionary<string, Dictionary<string, string>>
                {
                    {
                        "map_name", new Dictionary<string, string>
                        {
                            ["RU"] = "Текущая карта: {MAP}",
                            ["US"] = "Current map: {MAP}",
                            ["CN"] = "{GRAY}当前地图: {RED}{MAP}"
                        }
                    },
                    {
                        "current_time", new Dictionary<string, string>
                        {
                            ["RU"] = "{GRAY}Текущее время: {RED}{TIME}",
                            ["US"] = "{GRAY}Current time: {RED}{TIME}",
                            ["CN"] = "{GRAY}当前时间: {RED}{TIME}"
                        }
                    }
                },
                MapsName = new Dictionary<string, string>
                {
                    ["de_mirage"] = "Mirage",
                    ["de_dust2"] = "Dust II"
                }
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            Console.WriteLine("[Advertisements] Created default config at: " + configPath);
            return config;
        }

        private string GetPlayerIsoCode(string ip)
        {
            // e.g. use the convar override if you want (like ImperfectAdsIp).
            // string overrideIp = ImperfectAdsIp.Value?.Trim();
            // if (!string.IsNullOrEmpty(overrideIp)) { ip = overrideIp; } // just an example if you wanted

            var defaultLang = Config.DefaultLang ?? "";
            if (ip == "127.0.0.1") return defaultLang;

            try
            {
                var geoDbPath = Path.Combine(ModuleDirectory, "GeoLite2-Country.mmdb");
                if (!File.Exists(geoDbPath))
                    return defaultLang;

                using var reader = new DatabaseReader(geoDbPath);
                var response = reader.Country(IPAddress.Parse(ip));
                return response.Country.IsoCode ?? defaultLang;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GeoIP2 Error: {ex}");
            }

            return defaultLang;
        }
    }

    public class User
    {
        public bool HtmlPrint { get; set; }
        public string Message { get; set; } = string.Empty;
        public int PrintTime { get; set; }
    }
}
