using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MaxMind.GeoIP2;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace ImperfectAdvertise
{
    /// <summary>
    /// Main plugin class implementing IPluginConfig for ImperfectAdvertisements.
    /// Supports optional ConVar overrides for IP, server name, and sub-name.
    ///
    /// If no config is found, we generate a user-friendly default config with
    /// some comment/documentation lines to help the user. That file is placed in:
    ///   configs/plugins/ImperfectAdvertise/ImperfectAdvertise.json
    ///
    /// Known placeholders in messages:
    ///   {SERVERNAME}, {SERVERSUBNAME}, {PLAYERNAME}, {MAP}, {TIME}, {DATE}, {IP}, {PORT},
    ///   {MAXPLAYERS}, {PLAYERS}, plus multi-language tags (like {map_name}).
    ///
    /// Colors: embed placeholders like {BLUE}, {RED}, {GREEN}, etc.
    /// </summary>
    public class ImperfectAdvertise : BasePlugin, IPluginConfig<Config>
    {
        /// <summary>
        /// Our plugin config after OnConfigParsed.
        /// </summary>
        public Config Config { get; set; } = new();

        // Make sure the ModuleName (ImperfectAdvertise) matches the folder name
        public override string ModuleAuthor   => "Imperfect and Company LLC";
        public override string ModuleName     => "ImperfectAdvertise";
        public override string ModuleVersion  => "1.0.3";

        // ConVar overrides for IP / server name / sub-name
        private readonly FakeConVar<string> _imperfectAdsIp = new(
            "imperfect_ads_ip",
            "Specifies an IP override for ImperfectAdvertise plugin.",
            ""
        );

        private readonly FakeConVar<string> _imperfectAdsServerName = new(
            "imperfect_ads_servername",
            "Specifies a server name override for ImperfectAdvertise plugin.",
            ""
        );

        private readonly FakeConVar<string> _imperfectAdsServerSubName = new(
            "imperfect_ads_serversubname",
            "Specifies a server sub-name override for ImperfectAdvertise plugin.",
            ""
        );

        /// <summary>
        /// Track ephemeral data (center HTML states, etc.) for up to 66 player slots.
        /// </summary>
        private readonly User?[] _users = new User?[66];

        /// <summary>
        /// Timers for rotating ads. We'll kill them on unload or if config changes.
        /// </summary>
        private readonly List<Timer> _timers = new();

        /// <summary>
        /// For multi-language usage: maps SteamID64 => player ISO code.
        /// </summary>
        private readonly Dictionary<ulong, string> _playerIsoCode = new();

        // Flag to prevent multiple default-config attempts
        private static bool _staticConfigCreated;

        #region Static Constructor (Preempt Engine Stub)

        /// <summary>
        /// Runs when .NET runtime loads this type, typically before plugin Load() or OnConfigParsed().
        /// We attempt to create our real config file early, preventing the engine from generating a stub.
        /// </summary>
        static ImperfectAdvertise()
        {
            try
            {
                if (!_staticConfigCreated)
                {
                    string appRoot = Application.RootDirectory; 
                    var dir = Path.Combine(appRoot, "configs/plugins/ImperfectAdvertise");
                    Directory.CreateDirectory(dir);

                    var path = Path.Combine(dir, "ImperfectAdvertise.json");
                    if (!File.Exists(path) || new FileInfo(path).Length < 50)
                    {
                        var defaultCfg = MakeConfig();
                        var docObject  = BuildDocObject(defaultCfg);

                        var text = JsonSerializer.Serialize(docObject, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                        });

                        File.WriteAllText(path, text);
                        Console.WriteLine("[ImperfectAdvertise] (static) Created default config at " + path);
                    }
                    else
                    {
                        Console.WriteLine("[ImperfectAdvertise] (static) Found existing ImperfectAdvertise.json, skipping default creation.");
                    }

                    _staticConfigCreated = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImperfectAdvertise] (static) Unable to create default config => {ex}");
            }
        }

        #endregion

        /// <summary>
        /// Plugin lifecycle: OnLoad.
        /// </summary>
        public override void Load(bool hotReload)
        {
            // Double-check our default config in case static constructor didn't run
            EnsureDefaultConfigExists();

            // Register FakeConVars
            RegisterFakeConVars(GetType());

            // Hook relevant events
            RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
            RegisterListener<Listeners.OnTick>(OnTick);

            // If config already has Ads, start them
            if (Config.Ads != null)
            {
                StartTimers();
            }

            // If hot reload, re-init ephemeral data for connected players
            if (hotReload)
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    _users[player.Slot] = new User();
                }
            }
        }

        public override void Unload(bool hotReload)
        {
            // Stop any running timers
            foreach (var t in _timers)
                t.Kill();
            _timers.Clear();

            base.Unload(hotReload);
        }

        /// <summary>
        /// Called after config is parsed by the engine. We apply overrides, then re-init timers.
        /// </summary>
        public void OnConfigParsed(Config config)
        {
            Config = config;

            // ConVar overrides
            var overrideIp = _imperfectAdsIp.Value?.Trim() ?? "";
            if (!string.IsNullOrEmpty(overrideIp))
            {
                Console.WriteLine($"[ImperfectAdvertise] Overriding IP with: {overrideIp}");
            }

            var overrideName = _imperfectAdsServerName.Value?.Trim() ?? "";
            if (!string.IsNullOrEmpty(overrideName))
            {
                Console.WriteLine($"[ImperfectAdvertise] Overriding ServerName with: {overrideName}");
                Config.ServerName = overrideName;
            }

            var overrideSub = _imperfectAdsServerSubName.Value?.Trim() ?? "";
            if (!string.IsNullOrEmpty(overrideSub))
            {
                Console.WriteLine($"[ImperfectAdvertise] Overriding ServerSubName with: {overrideSub}");
                Config.ServerSubName = overrideSub;
            }

            // Re-init timers with new intervals or new messages
            foreach (var t in _timers)
                t.Kill();
            _timers.Clear();

            if (Config.Ads != null)
                StartTimers();
        }

        #region EVENT HANDLERS

        private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            // If there's a welcome message
            var welcome = Config.WelcomeMessage;
            if (welcome == null)
                return HookResult.Continue;

            var msg = welcome.Message
                .Replace("{PLAYERNAME}", player.PlayerName)
                .ReplaceColorTags();

            PrintWrappedLine(0, msg, player, true);
            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
        {
            // If multi-language is active, remove iso code entry
            if (Config.LanguageMessages != null)
            {
                var p = ev.Userid;
                if (p != null) _playerIsoCode.Remove(p.SteamID);
            }
            return HookResult.Continue;
        }

        private void OnClientAuthorized(int slot, SteamID steamId)
        {
            _users[slot] = new User();

            if (Config.LanguageMessages == null)
                return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player?.IpAddress != null)
            {
                var ipOnly = player.IpAddress.Split(':')[0];
                _playerIsoCode[steamId.SteamId64] = GetPlayerIsoCode(ipOnly);
            }
        }

        /// <summary>
        /// OnTick: Update center HTML messages if they're active, until they expire.
        /// </summary>
        private void OnTick()
        {
            foreach (var player in Utilities.GetPlayers())
            {
                var user = _users[player.Slot];
                if (user == null || !user.HtmlPrint)
                    continue;

                bool showWhenDead = Config.ShowHtmlWhenDead ?? false;
                if (!showWhenDead && !player.PawnIsAlive)
                    continue;

                float durationSec = Config.HtmlCenterDuration ?? 0f;
                double currentTime = user.PrintTime / 64.0;

                // If they're still within the time limit, reprint HTML
                if (currentTime < durationSec)
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

        #endregion

        #region HELPER - Default Config

        /// <summary>
        /// Double-check in OnLoad, in case static constructor was skipped or delayed.
        /// </summary>
        private void EnsureDefaultConfigExists()
        {
            if (_staticConfigCreated) return;

            string appRoot = Application.RootDirectory;
            var dir = Path.Combine(appRoot, "configs/plugins/ImperfectAdvertise");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "ImperfectAdvertise.json");
            if (!File.Exists(path) || new FileInfo(path).Length < 50)
            {
                CreateDefaultConfig(path);
                Console.WriteLine("[ImperfectAdvertise] (Load) Re-created default config at " + path);
            }
            else
            {
                Console.WriteLine("[ImperfectAdvertise] (Load) Found ImperfectAdvertise.json, no need to re-create.");
            }

            _staticConfigCreated = true;
        }

        private static Config MakeConfig()
        {
            return new Config
            {
                PrintToCenterHtml = false,
                WelcomeMessage = new WelcomeMessage
                {
                    MessageType  = MessageType.Chat,
                    Message      = "Welcome to {SERVERNAME} | {SERVERSUBNAME}, {BLUE}{PLAYERNAME}!",
                    DisplayDelay = 5
                },
                Ads = new List<Advertisement>
                {
                    new Advertisement
                    {
                        Interval = 60f,
                        Messages = new List<Dictionary<string, string>>
                        {
                            new()
                            {
                                ["Chat"]   = "Try out {SERVERSUBNAME} - currently on {MAP}",
                                ["Center"] = "Thanks for playing on {SERVERNAME}!"
                            }
                        }
                    }
                },
                ServerName   = "ImperfectGamers",
                ServerSubName= "24/7 Surf Easy",
                DefaultLang  = "US",
                LanguageMessages = new Dictionary<string, Dictionary<string, string>>
                {
                    {
                        "map_name", new Dictionary<string, string>
                        {
                            ["US"] = "Map is {MAP}!"
                        }
                    }
                },
                MapsName = new Dictionary<string, string>
                {
                    ["surf_kitsune"] = "Surf Kitsune"
                },
                Version = 1
            };
        }

        private static Dictionary<string, object?> BuildDocObject(Config cfg)
        {
            return new Dictionary<string, object?>
            {
                ["_comment"] = new []
                {
                    "This is the default ImperfectAdvertise config.",
                    "Feel free to modify these settings, or override them with ConVars in your startup.",
                    "Use 'css_advert_reload' console command to reload after editing."
                },
                ["print_to_center_html"] = cfg.PrintToCenterHtml,
                ["html_center_duration"] = cfg.HtmlCenterDuration,
                ["show_html_when_dead"]  = cfg.ShowHtmlWhenDead,
                ["welcome_message"]      = cfg.WelcomeMessage,
                ["ads"]                  = cfg.Ads,
                ["server_name"]          = cfg.ServerName,
                ["server_subname"]       = cfg.ServerSubName,
                ["default_lang"]         = cfg.DefaultLang,
                ["language_messages"]    = cfg.LanguageMessages,
                ["maps_name"]            = cfg.MapsName,
                ["Version"]              = cfg.Version
            };
        }

        private Config CreateDefaultConfig(string filePath)
        {
            var defaultCfg = MakeConfig();
            var docObject  = BuildDocObject(defaultCfg);

            var text = JsonSerializer.Serialize(docObject, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            File.WriteAllText(filePath, text);
            Console.WriteLine("[ImperfectAdvertise] Created default config at " + filePath);

            return defaultCfg;
        }

        #endregion

        #region ADS/TIMERS

        private void StartTimers()
        {
            if (Config.Ads == null)
                return;

            foreach (var ad in Config.Ads)
            {
                _timers.Add(AddTimer(ad.Interval, () => ShowAd(ad), TimerFlags.REPEAT));
            }
        }

        private void ShowAd(Advertisement ad)
        {
            var messages = ad.NextMessages;
            foreach (var (msgType, line) in messages)
            {
                switch (msgType)
                {
                    case "Chat":
                        PrintWrappedLine(HudDestination.Chat, line);
                        break;
                    case "Center":
                        PrintWrappedLine(HudDestination.Center, line);
                        break;
                }
            }
        }

        /// <summary>
        /// Prints either a single-player welcome or a broadcast ad. 
        /// For multi-language placeholders, see ProcessMessage().
        /// </summary>
        private void PrintWrappedLine(HudDestination? destination, string message,
            CCSPlayerController? connectPlayer = null, bool isWelcome = false)
        {
            if (isWelcome && connectPlayer != null && !connectPlayer.IsBot)
            {
                // Single-person welcome
                var welcome = Config.WelcomeMessage;
                if (welcome == null) return;

                AddTimer(welcome.DisplayDelay, () =>
                {
                    if (!connectPlayer.IsValid)
                        return;

                    var processed = ProcessMessage(message, connectPlayer.SteamID)
                        .Replace("{PLAYERNAME}", connectPlayer.PlayerName);

                    switch (welcome.MessageType)
                    {
                        case MessageType.Chat:
                            connectPlayer.PrintToChat(processed);
                            break;
                        case MessageType.Center:
                            connectPlayer.PrintToCenter(processed);
                            break;
                        case MessageType.CenterHtml:
                            SetHtmlPrintSettings(connectPlayer, processed);
                            break;
                    }
                });
            }
            else
            {
                // Broadcast to everyone else
                var targets = Utilities.GetPlayers().Where(u => !u.IsBot && u.IsValid && !isWelcome);

                foreach (var pl in targets)
                {
                    var processed = ProcessMessage(message, pl.SteamID);
                    if (destination == HudDestination.Chat)
                    {
                        pl.PrintToChat($" {processed}");
                    }
                    else
                    {
                        if (Config.PrintToCenterHtml == true)
                            SetHtmlPrintSettings(pl, processed);
                        else
                            pl.PrintToCenter(processed);
                    }
                }
            }
        }

        private void SetHtmlPrintSettings(CCSPlayerController player, string processedMsg)
        {
            var user = _users[player.Slot] ?? new User();
            _users[player.Slot] = user;

            user.HtmlPrint = true;
            user.PrintTime = 0;
            user.Message   = processedMsg;
        }

        private string ProcessMessage(string message, ulong steamId)
        {
            // If multi-language is in use, parse placeholders first
            if (Config.LanguageMessages != null)
            {
                var matches = Regex.Matches(message, @"\{([^}]*)\}");
                foreach (Match match in matches)
                {
                    var tag     = match.Groups[0].Value;   // e.g. {map_name}
                    var tagName = match.Groups[1].Value;   // e.g. map_name

                    if (!Config.LanguageMessages.TryGetValue(tagName, out var translations))
                        continue;

                    var iso = _playerIsoCode.TryGetValue(steamId, out var code)
                        ? code
                        : Config.DefaultLang;

                    if (iso != null && translations.TryGetValue(iso, out var localized))
                    {
                        message = message.Replace(tag, localized);
                    }
                    else if (Config.DefaultLang != null &&
                             translations.TryGetValue(Config.DefaultLang, out var fallback))
                    {
                        message = message.Replace(tag, fallback);
                    }
                }
            }

            // Then do standard replacements
            return ReplaceMessageTags(message);
        }

        private string ReplaceMessageTags(string message)
        {
            var mapName   = NativeAPI.GetMapName();
            var sName     = Config.ServerName ?? "CS2Server";
            var subName   = Config.ServerSubName ?? "";

            var replaced = message
                .Replace("{MAP}", mapName)
                .Replace("{TIME}", DateTime.Now.ToString("HH:mm:ss"))
                .Replace("{DATE}", DateTime.Now.ToString("dd.MM.yyyy"))
                .Replace("{SERVERNAME}", sName)
                .Replace("{SERVERSUBNAME}", subName)
                .Replace("{IP}", ConVar.Find("ip")?.StringValue ?? "127.0.0.1")
                .Replace("{PORT}", ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "27015")
                .Replace("{MAXPLAYERS}", Server.MaxPlayers.ToString())
                .Replace("{PLAYERS}", Utilities.GetPlayers().Count(u => u.PlayerPawn.Value is { IsValid: true }).ToString())
                .Replace("\n", "\u2029")
                .ReplaceColorTags();

            // Map-specific rename if present
            if (Config.MapsName != null && Config.MapsName.TryGetValue(mapName, out var friendly))
            {
                replaced = replaced.Replace(mapName, friendly);
            }

            return replaced;
        }

        private string GetPlayerIsoCode(string ip)
        {
            var def = Config.DefaultLang ?? "";
            if (ip == "127.0.0.1")
                return def;

            try
            {
                var path = Path.Combine(ModuleDirectory, "GeoLite2-Country.mmdb");
                if (!File.Exists(path))
                    return def;

                using var reader = new DatabaseReader(path);
                var response = reader.Country(IPAddress.Parse(ip));
                return response.Country.IsoCode ?? def;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImperfectAdvertise] GeoIP2 error => {ex}");
            }
            return def;
        }

        #endregion

        /// <summary>
        /// Command to reload config from disk manually (engine normally does this automatically).
        /// </summary>
        [ConsoleCommand("css_advert_reload", "Reload ImperfectAdvert config")]
        public void CommandReloadConfig(CCSPlayerController? controller, CommandInfo info)
        {
            EnsureDefaultConfigExists();

            var newConfig = LoadConfigFromDisk();
            OnConfigParsed(newConfig);

            if (Config.LanguageMessages != null)
            {
                foreach (var pl in Utilities.GetPlayers())
                {
                    if (pl.IpAddress == null || pl.AuthorizedSteamID == null) continue;

                    var ipOnly = pl.IpAddress.Split(':')[0];
                    _playerIsoCode[pl.AuthorizedSteamID.SteamId64] = GetPlayerIsoCode(ipOnly);
                }
            }

            const string reloadMsg = "[ImperfectAdvertise] Config reloaded successfully!";
            if (controller == null) Console.WriteLine(reloadMsg);
            else controller.PrintToChat(reloadMsg);
        }

        private Config LoadConfigFromDisk()
        {
            string appRoot = Application.RootDirectory;
            var dir = Path.Combine(appRoot, "configs/plugins/ImperfectAdvertise");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "ImperfectAdvertise.json");
            if (!File.Exists(path) || new FileInfo(path).Length < 50)
            {
                // If missing or basically empty, create defaults
                return CreateDefaultConfig(path);
            }

            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
            return JsonSerializer.Deserialize<Config>(json, opts) ?? new Config();
        }
    }

    /// <summary>
    /// Ephemeral data per user slot (HTML message state).
    /// </summary>
    public class User
    {
        public bool   HtmlPrint { get; set; }
        public string Message   { get; set; } = string.Empty;
        public int    PrintTime { get; set; }
    }

    /// <summary>
    /// Our plugin config, implementing IBasePluginConfig. 
    /// </summary>
    public class Config : IBasePluginConfig
    {
        [JsonPropertyName("print_to_center_html")]
        public bool? PrintToCenterHtml   { get; init; }

        [JsonPropertyName("html_center_duration")]
        public float? HtmlCenterDuration { get; init; }

        [JsonPropertyName("show_html_when_dead")]
        public bool? ShowHtmlWhenDead    { get; set; }

        [JsonPropertyName("welcome_message")]
        public WelcomeMessage? WelcomeMessage { get; init; }

        [JsonPropertyName("ads")]
        public List<Advertisement>? Ads   { get; init; }

        [JsonPropertyName("server_name")]
        public string? ServerName { get; set; }

        [JsonPropertyName("server_subname")]
        public string? ServerSubName { get; set; }

        [JsonPropertyName("default_lang")]
        public string? DefaultLang   { get; init; }

        [JsonPropertyName("language_messages")]
        public Dictionary<string, Dictionary<string, string>>? LanguageMessages { get; init; }

        [JsonPropertyName("maps_name")]
        public Dictionary<string, string>? MapsName { get; init; }

        public int Version { get; set; }
    }

    public enum MessageType
    {
        Chat       = 0,
        Center     = 1,
        CenterHtml = 2
    }

    /// <summary>
    /// Single welcome message for newly connecting players.
    /// </summary>
    public class WelcomeMessage
    {
        public MessageType MessageType  { get; init; }
        public required string Message  { get; init; }
        public float DisplayDelay       { get; set; } = 2f;
    }

    /// <summary>
    /// One repeating advertisement entry with an interval and multiple message lines.
    /// </summary>
    public class Advertisement
    {
        public float Interval { get; init; }

        // e.g. "Chat": "...", "Center": "..."
        public List<Dictionary<string, string>> Messages { get; init; } = new();

        private int _currentIndex;

        [JsonIgnore]
        public Dictionary<string, string> NextMessages
            => Messages[_currentIndex++ % Messages.Count];
    }
}
