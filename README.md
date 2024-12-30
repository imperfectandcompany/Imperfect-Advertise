# Imperfect-Advertise

A CounterStrikeSharp plugin for CS2 servers that displays rotating advertisement messages and a configurable welcome message to players.  
It supports:
- **Multi-language placeholders** (e.g. `{map_name}`).
- **Center (HTML) or chat-based** rotating ads.
- **Optional ConVar overrides** for IP, server name, or sub-name.
- **GeoIP2** integration (MaxMind) for per-country language messages.

## How It Works

1. On server startup, the plugin checks for a config file in:
   `csgo/addons/counterstrikesharp/configs/plugins/ImperfectAdvertise/ImperfectAdvertise.json`
2. If no config is found, a **default config** is auto-created with sample ads and placeholders.
3. Ads can be displayed in chat or center of the screen, cycling at specified intervals.
4. A single welcome message can appear to each newly connecting player.
5. Additional multi-language placeholders let you tailor messages based on a player’s country (requires GeoIP2 DB).

## Installation

1. **Build** the plugin into `ImperfectAdvertise.dll`.
2. **Copy**:
    - `ImperfectAdvertise.dll`
    - `MaxMind.GeoIP2.dll` (or place it into the `shared` folder if you prefer)  
      Into your server’s `addons/counterstrikesharp/plugins/ImperfectAdvertise` folder (or similar).
3. **Ensure** the required MaxMind `GeoLite2-Country.mmdb` is placed in the same folder as the plugin or accessible via `ModuleDirectory`.
4. Restart your CS2 server.

## ConVars

| ConVar                          | Default | Description                                           |
|---------------------------------|---------|-------------------------------------------------------|
| `imperfect_ads_ip`             | `""`    | Override IP used in placeholders `{IP}`.             |
| `imperfect_ads_servername`     | `""`    | Override server name for `{SERVERNAME}` placeholders.|
| `imperfect_ads_serversubname`  | `""`    | Override sub-name (like “24/7 Surf”) for messages.    |

## Commands

- `css_advert_reload` : Manually reloads the ImperfectAdvert config from disk.

## Configuration

See `ImperfectAdvertise.json` for these fields:
- **welcome_message**: The single welcome message a newly connecting player gets.
- **ads**: A list of rotating ads, each with:
    - `Interval` (in seconds)
    - `Messages` dictionary (e.g., `"Chat": "some text"` or `"Center": "some text"`)
- **language_messages**: For multi-language placeholders, e.g. `{map_name}` -> "Map is {MAP}" in English.

## Credits

- [MaxMind.GeoIP2](https://github.com/maxmind/GeoIP2-dotnet) for IP->Country resolution.
