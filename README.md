# Fast Song Search

A MelonLoader mod for Synth Riders that eliminates stutter during Twitch song requests.

### 1. Stutter Fix
When viewers use the `!srr` command to request songs, the game's built-in search runs synchronously on the main thread, causing noticeable stutters - especially with large song libraries. This mod replaces the slow search with a pre-built word index that executes instantly.

### 2. Queue Fix
The game has a bug where the last song in the queue never gets removed after playing. This mod detects when the played song is stuck in the queue and clears it by running the game's `!clear` command internally, while leaving the queue alone whenever the game handled the removal itself.

### 3. Duplicate Map Selection (new in 1.1.4)
When multiple maps share the same song name but have different mappers, the game silently queued whichever came first. Now the mod lists the options in chat and lets the viewer choose:

```
viewer: !srr unity
bot:    Multiple maps found for "Unity": [1] MapperA [2] MapperB [3] MapperC - reply !srr <number> to pick
viewer: !srr 2
bot:    (queues MapperB's version through the game's normal flow)
```

- Options expire after 90 seconds so a stray `!srr 2` later doesn't grab stale results
- `!srr 2` and `!srr #2` both work
- Maximum of 5 options shown
- One pending selection at a time (the game's search API doesn't expose the requesting username, so the selection is open to any viewer until it's answered or expires)
- If the mod can't post to chat for any reason, it falls back to the old behavior (first match) rather than leaving the viewer with nothing

## Installation

1. Install [MelonLoader](https://melonwiki.xyz/) for Synth Riders
2. Download `FastSongSearch.dll` from [Releases](https://github.com/0mniDreamer/FastSongSearch/releases)
3. Place `FastSongSearch.dll` in your `SynthRiders/Mods/` folder
4. Launch the game

## How It Works

**Fast Search:**
- On first song request, the mod builds a word-based index of all songs so it will stutter on first search.(Important note: This is a one-time cost that happens only on the first search after launching the game)
- Subsequent searches use the cached index for instant lookups
- The cache automatically rebuilds when changing scenes
- Falls back to the original search if any errors occur (Failsafe)

**Queue Fix:**
- Tracks the queue state (count and head song) when entering gameplay
- On returning from gameplay, verifies whether the game already removed the played song (current game builds do this themselves when the queue has multiple entries) and does nothing if so - the mod only acts when the played song is verifiably still stuck at the head of the queue
- When the stuck song is the only one in the queue, runs the game's `!clear` command internally to work around the game bug where `QueueRemove()` fails on the final entry
- When viewers requested more songs during play, the stuck song is removed with `QueueRemove()` instead, so the new requests survive
- The clear verifies the queue actually emptied and falls back through three mechanisms (internal `!clear` command handler → `QueueClear()` → `QueueRemove()`) so it keeps working across game updates

**Duplicate Selection:**
- Detects when the top search result shares its name with other maps by different mappers
- Posts the numbered options to chat via the game's own Twitch connection
- The viewer's `!srr <number>` reply flows through the same patched search, which returns the chosen map to the game's normal queueing path - no extra command handling needed

## Configuration

`UserData/MelonPreferences.cfg` under `[FastSongSearch]`:

| Entry | Default | Purpose |
|-------|---------|---------|
| `DebugLogging` | `false` | Verbose diagnostic output in the console |

## Compatibility

- Synth Riders 3.6.x (Steam/PC VR)
- MelonLoader 0.7.x
- .NET 6.0 / IL2CPP

## Changelog

### 1.1.4
- **New: Duplicate map selection** - when multiple maps share the same song name with different mappers, the options are posted to chat and the viewer picks with `!srr <number>` instead of silently getting the first match
- **Fixed: queue removal for the current game version** - the removal trigger no longer depends on the removed `SongSelection` scene; it now fires on any scene following the game-end scene
- **Fixed: extra song removed from multi-song queues** - current game builds remove the played song themselves when the queue has multiple entries; the mod now verifies the played song is actually stuck before touching the queue
- **Improved: last-song clear** - runs the game's internal `!clear` command handler (resolved at runtime), with verified fallbacks to `QueueClear()` and `QueueRemove()`
- **Improved: mid-play requests survive** - if viewers request songs while the stuck song plays, it's removed individually instead of clearing the whole queue
- New `DebugLogging` option in MelonPreferences for diagnostic output

### 1.0.1
- Initial public release: stutter fix (cached word-index search) and stuck last-song queue fix

## License

MIT License - see [LICENSE](LICENSE)

## Credits
- Synth Riders by Kluge Interactive
- MelonLoader by LavaGang
