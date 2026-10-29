# Ancient Ley Lines

The stone circles of the Meadows were never just ruins. Each one sits on a knot of ley lines, and the gaps between its stones are gates to distant places: a quiet dolmen in the Meadows, a runestone deep in the Swamp, the burning edge of the Ashlands.

Ancient Ley Lines turns every stone circle into a hub of ancient portals. They open as you explore, so the world gets smaller the more of it you know.

## Features

- **Ancient portals.** Every gap between a circle's standing stones holds a portal. Each one leads to a fixed dolmen or runestone somewhere in the world.
- **Opened by exploration.** A portal stays silent until you've set foot in the biome it leads to. Walking through one never drops you, in Meadows gear, into a biome you haven't seen yet.
- **Latitude matters.** Circles near the world's centre line lead to the early biomes. Circles farther north or south lead to the late ones, and the outermost circles reach the edge of the world.
- **Whispers, not coordinates.** Hover over a portal to hear where it leads: *"A calm place of soft grass and gentle wind, far, far away."*
- **Mind the edges.** Portals to the Ashlands and the Deep North don't open when you walk into them. You have to choose to go: *Brave the heat*, *Challenge the blizzard*.
- **Ways back.**
  - Dolmens get a gate of their own. It opens both ways as soon as the circle portal leading there is open.
  - At a runestone you've just arrived at, press **G** to return to the portal you came through.
- **Nothing to build, nothing saved.** The portals are part of the locations themselves, so they appear in existing worlds and disappear cleanly if you remove the mod.
- **Multiplayer.** Every player gets the same portals. The server decides what the ley lines do.

<details>
<summary><b>The ritual (spoilers)</b></summary>

Building portals inside a stone circle strains the ley lines, and the stones let you know. With each gate they whisper a rune, and everyone near the circle hears it. Build five, give them all the name the runes spell, and the stones answer: every circle in the world gets new destinations. Any other name, and the stones don't know it.

The ley lines need time to recover: 30 in-game days by default. While they recover the stones stay silent, and if you try again too soon, the gates crumble. Each shift the stones take a new name. The one who performs the ritual doesn't come through it unscathed.

</details>

## Installation

**With a mod manager (recommended):** install *Ancient Ley Lines* through r2modman or Thunderstore Mod Manager.

**By hand:**
1. Install [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
2. Copy `WorldPortal.dll` into `Valheim/BepInEx/plugins/AncientLeyLines/`.

**Multiplayer:** install it on the server and on every client. The server sends the list of locations, and it decides when the ritual shifts the ley lines.

## Configuration

The config file is `BepInEx/config/dioksy.worldportal.cfg`. It's created the first time you start the game with the mod installed. Options marked *server* are read on the host.

| Section | Option | Default | What it does |
|---|---|---|---|
| General | `MaxPortals` | `5` | Maximum portals per stone circle, from 1 to 5. A circle gets one per gap between its stones. |
| General | `LeaveEntrance` | `false` | Leave the widest gap without a portal, as an entrance. This means one portal fewer per circle. |
| General | `OpenByBiome` | `true` | Portals open once you've been in their destination's biome. If off, the destination itself has to be explored on your map. |
| General | `ReturnKey` | `G` | Key to return from a runestone you arrived at. |
| General | `TargetLocations` | `Dolmen,Runestone` | Location prefab name prefixes the portals lead to (*server*). For example, add `StoneHenge` for the Plains' standing stones (`StoneHenge1`–`6`), but they usually have goblins around them. |
| General | `GateLocations` | `Dolmen` | Destinations that get a two-way gate in their centre. Anything in `TargetLocations` without a runestone should be listed here too, or there's no way back. |
| General | `UndiscoveredLocations` | *(empty)* | Destinations that are usable before you've discovered them. |
| Ritual | `Enabled` | `true` | Turn the ritual on or off. |
| Ritual | `Portals` | `5` | Same-named portals needed in one circle. |
| Ritual | `CooldownDays` | `30` | In-game days between shifts (*server*). |

### Bands

> ⚠️ **Caution:** these options control where portals lead. Changing them changes the destinations of every portal in the world, and it can leave some biomes with few or no portals. The defaults were tuned by simulating 20 generated worlds.

| Option | Default | What it does |
|---|---|---|
| `AnchorPercentile` | `0.95` | Scales the latitude bands to the spread of stone circles in your world. |
| `Band0` … `Band3` | *(see file)* | Biome weights for each band, from the centre line outward. `Edge` means the Ashlands in the south and the Deep North in the north. |

## Compatibility

- The mod doesn't change any vanilla prefabs and doesn't add pieces or items.
- Other mods that move or replace the vanilla `StoneCircle`, `Dolmen` or `Runestone` locations may change which circles get portals and where they lead.

## Building from source

The mod needs the .NET SDK and a Valheim install.

```
dotnet build -c Release -p:ValheimDir="C:\path\to\Valheim"
```

A Release build writes a ready-to-upload Thunderstore zip to `dist/`. Every build also copies the DLL into your r2modman `Default` profile, if that profile exists.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](LICENSE)
