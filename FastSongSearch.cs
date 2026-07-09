using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppSynth.Twitch;
using Il2CppMiKu.NET.Charting;

[assembly: MelonInfo(typeof(FastSongSearch.FastSongSearchMod), "Fast Song Search", "1.1.4", "OmniDreamer")]
[assembly: MelonGame("Kluge Interactive", "SynthRiders")]

namespace FastSongSearch
{
    /// <summary>
    /// MelonLoader mod that fixes Twitch song request issues in Synth Riders:
    /// 1. Eliminates stutter when viewers use !srr (cached search index)
    /// 2. Fixes songs not being removed from queue after playing
    /// 3. NEW in 1.1.4: When multiple maps share the same name (different mappers),
    ///    lists the options in chat and lets the viewer pick with "!srr <number>"
    ///    instead of silently queueing the first match.
    /// </summary>
    public class FastSongSearchMod : MelonMod
    {
        private static string _lastScene = "";
        private static int _queueCountBeforeGame = 0;
        private static string _firstQueuedSongName = "";

        internal static MelonPreferences_Entry<bool> DebugLogging;

        public override void OnInitializeMelon()
        {
            var category = MelonPreferences.CreateCategory("FastSongSearch");
            DebugLogging = category.CreateEntry("DebugLogging", false,
                description: "Enable verbose diagnostic logging");

            LoggerInstance.Msg($"Fast Song Search 1.1.4 loaded - Twitch requests optimized!{(DebugLogging.Value ? " (debug logging ON)" : "")}");
        }

        internal static void DebugLog(string message)
        {
            if (DebugLogging != null && DebugLogging.Value)
                MelonLogger.Msg($"[Debug] {message}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            DebugLog($"Scene loaded: \"{sceneName}\" (last: \"{_lastScene}\") | IsGame={IsGameScene(sceneName)} WasInGame={WasInGame(_lastScene)}");

            // Remove played song when leaving the game-end scene.
            // The menu scene is the selected stage environment (e.g. "03.Roof Top")
            // on current game builds, so we can't match a fixed scene name -
            // any scene that loads after a gameend scene is the return to menu.
            // This runs BEFORE the snapshot below so that if the next scene is
            // another gameplay scene, the fresh snapshot doesn't clobber this one.
            if (WasInGame(_lastScene))
            {
                DebugLog($"Left game-end scene - pre-game count was {_queueCountBeforeGame}");
                if (_queueCountBeforeGame > 0 && !string.IsNullOrEmpty(_firstQueuedSongName))
                {
                    QueueManager.RemoveFirstSongFromQueue(_queueCountBeforeGame, _firstQueuedSongName);
                }
                else
                {
                    DebugLog("Removal skipped - no snapshot recorded when gameplay started");
                }
                _queueCountBeforeGame = 0;
                _firstQueuedSongName = "";
            }

            // Diagnostic: a gameend scene loaded but we never detected the
            // gameplay scene before it - the scene-name heuristic missed it.
            if (WasInGame(sceneName) && !IsGameScene(_lastScene))
            {
                MelonLogger.Warning($"[FastSongSearch] Gameplay scene \"{_lastScene}\" was not detected as a game scene - queue snapshot was skipped. Please report this scene name.");
            }

            // Snapshot queue when entering gameplay
            if (IsGameScene(sceneName) && !IsGameScene(_lastScene))
            {
                try
                {
                    var queue = TwitchBot.GetSongsInQueue();
                    _queueCountBeforeGame = queue?.Count ?? 0;
                    _firstQueuedSongName = (_queueCountBeforeGame > 0) ? queue[0]?.Name ?? "" : "";
                    DebugLog($"Entering gameplay - queue snapshot: {_queueCountBeforeGame} song(s), first: \"{_firstQueuedSongName}\"");
                }
                catch (Exception ex)
                {
                    DebugLog($"Queue snapshot failed: {ex.Message}");
                }
            }

            _lastScene = sceneName;
            SongSearchCache.Invalidate();
        }

        private static bool IsGameScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            var lower = sceneName.ToLowerInvariant();
            return lower.Contains("stage") && !lower.Contains("gameend") && !lower.Contains("gamestart");
        }

        private static bool WasInGame(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return sceneName.ToLowerInvariant().Contains("gameend");
        }
    }

    /// <summary>
    /// Holds the pending duplicate-name options awaiting a "!srr <number>" reply.
    ///
    /// NOTE: The game's static SearchSongsByName(query, availableSongs) signature
    /// (the only surface we patch) does not expose the requesting username, so this
    /// session is GLOBAL: one pending selection at a time, any viewer can answer it.
    /// It expires after SESSION_TIMEOUT_SECONDS so a stray "!srr 2" later doesn't
    /// grab stale results. Per-user sessions are a future probe target (would need
    /// the username surfaced somewhere on the game's call path).
    /// </summary>
    internal static class PendingSelection
    {
        private const int SESSION_TIMEOUT_SECONDS = 90;
        internal const int MAX_OPTIONS = 5;

        private static readonly object _lock = new();
        private static List<Chart> _options = new();
        private static DateTime _createdAt = DateTime.MinValue;

        public static void Store(List<Chart> options)
        {
            lock (_lock)
            {
                _options = options;
                _createdAt = DateTime.Now;
            }
        }

        public static Chart TakeSelection(int number)
        {
            lock (_lock)
            {
                if (_options.Count == 0)
                    return null;

                if ((DateTime.Now - _createdAt).TotalSeconds > SESSION_TIMEOUT_SECONDS)
                {
                    _options.Clear();
                    return null;
                }

                if (number < 1 || number > _options.Count)
                    return null;

                var selected = _options[number - 1];
                _options.Clear();
                return selected;
            }
        }

        public static bool HasActiveSession()
        {
            lock (_lock)
            {
                if (_options.Count == 0) return false;
                if ((DateTime.Now - _createdAt).TotalSeconds > SESSION_TIMEOUT_SECONDS)
                {
                    _options.Clear();
                    return false;
                }
                return true;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _options.Clear();
                _createdAt = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Parse "2" or "#2" into a selection number. Returns 0 if not a selection.
        /// </summary>
        public static int ParseSelectionNumber(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            query = query.Trim().TrimStart('#');
            if (int.TryParse(query, out int num) && num >= 1 && num <= MAX_OPTIONS)
                return num;
            return 0;
        }
    }

    /// <summary>
    /// Finds and caches the live TwitchBot instance for invoking instance methods.
    /// </summary>
    internal static class BotLocator
    {
        private static TwitchBot _instance;

        public static TwitchBot GetInstance()
        {
            if (_instance == null)
            {
                try
                {
                    _instance = UnityEngine.Object.FindObjectOfType<TwitchBot>();
                }
                catch { }
            }
            return _instance;
        }

        public static void Invalidate()
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Sends messages to Twitch chat via the game's own TwitchBot connection.
    /// SendChatMessage was confirmed to exist in the dnSpy dump, but we never
    /// compiled against it, so it's resolved by reflection at runtime and handles
    /// both static and instance shapes. Degrades gracefully (logs once) if the
    /// member isn't found on either branch.
    /// </summary>
    internal static class ChatSender
    {
        private static MethodInfo _sendMethod;
        private static bool _resolved = false;
        private static bool _warned = false;

        public static bool TrySend(string message)
        {
            try
            {
                if (!_resolved)
                    Resolve();

                if (_sendMethod == null)
                {
                    WarnOnce("SendChatMessage not found on TwitchBot - duplicate options can't be posted to chat.");
                    return false;
                }

                if (_sendMethod.IsStatic)
                {
                    _sendMethod.Invoke(null, new object[] { message });
                    return true;
                }

                var instance = BotLocator.GetInstance();
                if (instance == null)
                {
                    WarnOnce("No live TwitchBot instance found - can't post to chat.");
                    return false;
                }

                _sendMethod.Invoke(instance, new object[] { message });
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce($"SendChatMessage failed: {ex.Message}");
                BotLocator.Invalidate(); // stale instance? re-find next time
                return false;
            }
        }

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                _sendMethod = typeof(TwitchBot).GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "SendChatMessage" &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));

                if (_sendMethod != null)
                {
                    FastSongSearchMod.DebugLog(
                        $"Resolved SendChatMessage ({(_sendMethod.IsStatic ? "static" : "instance")})");
                }
            }
            catch (Exception ex)
            {
                FastSongSearchMod.DebugLog($"SendChatMessage resolve error: {ex.Message}");
            }
        }

        private static void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            MelonLogger.Warning($"[FastSongSearch] {message}");
        }
    }

    /// <summary>
    /// Pre-indexed song search for instant lookups.
    /// Detects same-name / different-mapper duplicates and offers a chat selection
    /// instead of silently returning the first match.
    /// </summary>
    internal static class SongSearchCache
    {
        private static Dictionary<string, List<Chart>> _wordIndex = new();
        private static List<Chart> _allSongs = new();
        private static bool _isBuilt = false;
        private static readonly object _lock = new();

        public static void Invalidate()
        {
            lock (_lock)
            {
                _wordIndex.Clear();
                _allSongs.Clear();
                _isBuilt = false;
            }

            // Chart references may be destroyed on scene change - never hand out
            // a stale selection, and drop any cached bot instance too.
            PendingSelection.Clear();
            BotLocator.Invalidate();
        }

        private static void Build(Il2CppSystem.Collections.Generic.List<Chart> songs)
        {
            if (_isBuilt) return;

            lock (_lock)
            {
                if (_isBuilt) return;

                _wordIndex.Clear();
                _allSongs.Clear();

                for (int i = 0; i < songs.Count; i++)
                {
                    var chart = songs[i];
                    if (chart == null) continue;

                    _allSongs.Add(chart);
                    IndexChart(chart);
                }

                _isBuilt = true;
            }
        }

        private static void IndexChart(Chart chart)
        {
            var fields = new[] { chart.Name, chart.Author, chart.Beatmapper };

            foreach (var field in fields)
            {
                if (string.IsNullOrEmpty(field)) continue;

                var words = field.ToLowerInvariant()
                    .Split(new[] { ' ', '-', '_', '(', ')', '[', ']', '.' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var word in words)
                {
                    if (word.Length < 2) continue;

                    if (!_wordIndex.TryGetValue(word, out var list))
                    {
                        list = new List<Chart>();
                        _wordIndex[word] = list;
                    }

                    if (!list.Contains(chart))
                        list.Add(chart);
                }
            }
        }

        public static Chart Search(string query, Il2CppSystem.Collections.Generic.List<Chart> songs)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            if (!_isBuilt)
                Build(songs);

            query = query.Trim();

            // ---- Selection reply? ("!srr 2" arrives here as query "2" or "#2") ----
            var selectionNumber = PendingSelection.ParseSelectionNumber(query);
            if (selectionNumber > 0 && PendingSelection.HasActiveSession())
            {
                var selected = PendingSelection.TakeSelection(selectionNumber);
                if (selected != null)
                {
                    FastSongSearchMod.DebugLog($"Selection #{selectionNumber} -> \"{selected.Name}\" by {MapperOf(selected)}");
                    return selected;
                }
            }

            query = query.ToLowerInvariant();
            var queryWords = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (queryWords.Length == 0)
                return null;

            var scores = new Dictionary<Chart, int>();

            // Score by word matches
            foreach (var word in queryWords)
            {
                if (_wordIndex.TryGetValue(word, out var exactMatches))
                {
                    foreach (var chart in exactMatches)
                        AddScore(scores, chart, 10);
                }

                foreach (var kvp in _wordIndex)
                {
                    if (kvp.Key.Contains(word) || word.Contains(kvp.Key))
                    {
                        foreach (var chart in kvp.Value)
                            AddScore(scores, chart, 3);
                    }
                }
            }

            // Fallback to substring search
            if (scores.Count == 0)
            {
                foreach (var chart in _allSongs)
                {
                    var name = chart.Name?.ToLowerInvariant() ?? "";
                    if (name.Contains(query))
                        AddScore(scores, chart, 15);
                }
            }

            if (scores.Count == 0)
                return null;

            // Boost exact and prefix matches.
            // NOTE: v1.0.1 early-returned on the first exact name match here, which
            // is exactly what hid same-name duplicates. Now we boost and continue so
            // all exact-name matches surface for duplicate detection below.
            foreach (var chart in scores.Keys.ToList())
            {
                var name = chart.Name?.ToLowerInvariant() ?? "";

                if (name == query)
                    scores[chart] += 50;
                else if (name.StartsWith(query))
                    scores[chart] += 20;
            }

            var best = scores.OrderByDescending(x => x.Value).First().Key;

            // ---- Duplicate detection: same name as best match, different mapper ----
            var duplicates = FindSameNameDuplicates(best, scores);
            if (duplicates.Count > 1)
            {
                PendingSelection.Store(duplicates);

                var posted = ChatSender.TrySend(BuildOptionsMessage(duplicates));
                if (posted)
                {
                    FastSongSearchMod.DebugLog($"{duplicates.Count} same-name maps for \"{best.Name}\" - awaiting !srr <number>");
                    // Return null so nothing gets queued until the viewer picks.
                    // (The game may also post its own "not found" style message -
                    // cosmetic; our options message tells the viewer what to do.)
                    return null;
                }

                // Chat unavailable - don't strand the viewer with silence.
                // Fall back to old behavior (first match) rather than queueing nothing.
                PendingSelection.Clear();
                FastSongSearchMod.DebugLog("Chat send failed - falling back to first match");
            }

            return best;
        }

        /// <summary>
        /// Collect charts whose name matches the best result's name, deduped by
        /// (name, mapper). Only returns 2+ entries when mappers actually differ.
        /// </summary>
        private static List<Chart> FindSameNameDuplicates(Chart best, Dictionary<Chart, int> scores)
        {
            var results = new List<Chart>();
            var bestName = best.Name?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(bestName))
            {
                results.Add(best);
                return results;
            }

            var seenMappers = new HashSet<string>();

            // Best result first so "[1]" is always what v1.0.1 would have picked
            foreach (var kvp in scores.OrderByDescending(x => x.Value))
            {
                var chart = kvp.Key;
                var name = chart.Name?.ToLowerInvariant() ?? "";
                if (name != bestName) continue;

                var mapper = MapperOf(chart).ToLowerInvariant();
                if (seenMappers.Add(mapper))
                {
                    results.Add(chart);
                    if (results.Count >= PendingSelection.MAX_OPTIONS)
                        break;
                }
            }

            return results;
        }

        internal static string MapperOf(Chart chart)
        {
            if (!string.IsNullOrEmpty(chart.Beatmapper)) return chart.Beatmapper;
            if (!string.IsNullOrEmpty(chart.Author)) return chart.Author;
            return "unknown";
        }

        private static string BuildOptionsMessage(List<Chart> options)
        {
            var parts = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                parts.Add($"[{i + 1}] {MapperOf(options[i])}");
            }

            var msg = $"Multiple maps found for \"{options[0].Name}\": {string.Join(" ", parts)} - reply !srr <number> to pick";

            // Twitch hard limit is 500 chars; stay well under it
            if (msg.Length > 400)
                msg = msg.Substring(0, 397) + "...";

            return msg;
        }

        private static void AddScore(Dictionary<Chart, int> scores, Chart chart, int points)
        {
            if (!scores.ContainsKey(chart))
                scores[chart] = 0;
            scores[chart] += points;
        }
    }

    /// <summary>
    /// Handles removing songs from the Twitch request queue.
    /// Works around a game bug where the last song doesn't get removed:
    /// when the played song was the last one in the queue, this runs the
    /// game's own !clear command internally.
    ///
    /// The clear tries three mechanisms in order, verifying the queue count
    /// after each, so it self-heals across game updates and branches:
    ///   1. The game's !clear command handler (ClearCommand and variants),
    ///      resolved by reflection - this is the exact path the !clear chat
    ///      command takes.
    ///   2. TwitchBot.QueueClear() - the static wrapper !clear calls
    ///      (this alone worked on earlier game builds).
    ///   3. TwitchBot.QueueRemove() on the remaining entry - last resort.
    /// </summary>
    internal static class QueueManager
    {
        private static readonly string[] ClearCommandCandidates =
        {
            "ClearCommand",
            "ClearQueueCommand",
            "QueueClearCommand",
            "CmdClear"
        };

        private static MethodInfo _clearCommand;
        private static bool _clearCommandResolved = false;

        public static void RemoveFirstSongFromQueue(int queueCountBeforeGame, string playedSongName)
        {
            try
            {
                var queue = TwitchBot.GetSongsInQueue();
                if (queue == null || queue.Count == 0)
                {
                    FastSongSearchMod.DebugLog("Queue empty on return - game removed the played song itself");
                    return;
                }

                int count = queue.Count;
                string headName = queue[0]?.Name ?? "";
                FastSongSearchMod.DebugLog($"Queue has {count} song(s) now; pre-game count was {queueCountBeforeGame}; head: \"{headName}\", played: \"{playedSongName}\"");

                // Current game builds remove the played song themselves when the
                // queue has multiple entries (observed: 3 -> 2 without our help),
                // but still fail when the played song was the only one (the
                // original stuck-last-song bug). Only act when the played song is
                // verifiably still at the head of the queue:
                //   - head name changed -> game removed it -> nothing to do
                //   - head name matches but count dropped below the pre-game
                //     count -> a same-name duplicate scenario where the game
                //     already removed one -> nothing to do
                if (headName != playedSongName)
                {
                    FastSongSearchMod.DebugLog("Head song changed - game removed the played song itself");
                    return;
                }

                if (count < queueCountBeforeGame)
                {
                    FastSongSearchMod.DebugLog("Count dropped and head is a same-name duplicate - game removed the played song itself");
                    return;
                }

                // Played song is still stuck at the head.
                // If it's the only song, QueueRemove is broken (game bug) - run
                // the internal !clear. If viewers added songs during play,
                // QueueRemove works fine with 2+ entries and spares the new
                // requests, so prefer it over clearing.
                if (count == 1)
                {
                    ClearLastSong();
                }
                else
                {
                    TwitchBot.QueueRemove(queue[0]);
                    FastSongSearchMod.DebugLog($"Stuck song removed via QueueRemove; queue now has {TwitchBot.GetSongsInQueue()?.Count ?? -1} song(s)");
                }
            }
            catch (Exception ex)
            {
                FastSongSearchMod.DebugLog($"RemoveFirstSongFromQueue error: {ex.Message}");
            }
        }

        private static void ClearLastSong()
        {
            // ---- 1) Run the game's !clear command handler internally ----
            if (TryInvokeClearCommand())
            {
                if (QueueIsEmpty())
                {
                    FastSongSearchMod.DebugLog("Last song cleared via internal !clear command handler");
                    return;
                }
                FastSongSearchMod.DebugLog("ClearCommand invoked but queue not empty - falling back");
            }

            // ---- 2) Static QueueClear (what !clear ultimately wraps) ----
            try { TwitchBot.QueueClear(); } catch { }
            if (QueueIsEmpty())
            {
                FastSongSearchMod.DebugLog("Last song cleared via TwitchBot.QueueClear()");
                return;
            }

            // ---- 3) Last resort: QueueRemove on the remaining entry ----
            try
            {
                var queue = TwitchBot.GetSongsInQueue();
                if (queue != null && queue.Count > 0 && queue[0] != null)
                {
                    TwitchBot.QueueRemove(queue[0]);
                }
            }
            catch { }

            if (QueueIsEmpty())
                FastSongSearchMod.DebugLog("Last song cleared via QueueRemove fallback");
            else
                MelonLogger.Warning("[FastSongSearch] Could not clear last song from queue - all three mechanisms failed. Enable DebugLogging and send the console output.");
        }

        private static bool QueueIsEmpty()
        {
            try
            {
                var queue = TwitchBot.GetSongsInQueue();
                return queue == null || queue.Count == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve and invoke the game's !clear command handler by reflection.
        /// Sibling commands seen in dnSpy (RemoveCommand, OopsCommand) take
        /// (string, string, bool, bool, bool) - username/args plus permission
        /// flags - so unknown string params get "" and bools get true
        /// (broadcaster-level permissions). Returns true if a handler was
        /// found and invoked without throwing.
        /// </summary>
        private static bool TryInvokeClearCommand()
        {
            try
            {
                if (!_clearCommandResolved)
                    ResolveClearCommand();

                if (_clearCommand == null)
                    return false;

                var parameters = _clearCommand.GetParameters();
                var args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (pType == typeof(string))
                        args[i] = "";
                    else if (pType == typeof(bool))
                        args[i] = true;   // permission flags: act as broadcaster
                    else if (pType.IsValueType)
                        args[i] = Activator.CreateInstance(pType);
                    else
                        args[i] = null;
                }

                object target = null;
                if (!_clearCommand.IsStatic)
                {
                    target = BotLocator.GetInstance();
                    if (target == null)
                    {
                        FastSongSearchMod.DebugLog("ClearCommand is instance-based but no live TwitchBot found");
                        return false;
                    }
                }

                _clearCommand.Invoke(target, args);
                return true;
            }
            catch (Exception ex)
            {
                FastSongSearchMod.DebugLog($"ClearCommand invoke failed: {ex.Message}");
                return false;
            }
        }

        private static void ResolveClearCommand()
        {
            _clearCommandResolved = true;
            try
            {
                var methods = typeof(TwitchBot).GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance);

                foreach (var candidate in ClearCommandCandidates)
                {
                    _clearCommand = methods.FirstOrDefault(m => m.Name == candidate);
                    if (_clearCommand != null)
                    {
                        FastSongSearchMod.DebugLog(
                            $"Resolved {candidate} ({(_clearCommand.IsStatic ? "static" : "instance")}, {_clearCommand.GetParameters().Length} params)");
                        return;
                    }
                }

                FastSongSearchMod.DebugLog("No clear-command handler found on TwitchBot - will use QueueClear directly");
            }
            catch (Exception ex)
            {
                FastSongSearchMod.DebugLog($"ClearCommand resolve error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPatch(typeof(TwitchBot), nameof(TwitchBot.SearchSongsByName))]
        [HarmonyPrefix]
        public static bool FastSearchPrefix(
            string query,
            Il2CppSystem.Collections.Generic.List<Chart> availableSongs,
            ref Chart __result)
        {
            try
            {
                __result = SongSearchCache.Search(query, availableSongs);
                return false;
            }
            catch
            {
                return true; // Fall back to original on error
            }
        }
    }
}
