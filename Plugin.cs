using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Target = (string name, UnityEngine.Vector3 pos, Heightmap.Biome biome);

namespace WorldPortal
{
    [BepInPlugin("dioksy.worldportal", "Ley Lines", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        // Last portal trip, kept in the character save: where we landed and the portal to go back to.
        const string ArrivalKey = "worldportal_arrival", ReturnKey = "worldportal_return";
        // "Edge" in the band weights: AshLands for southern circles, DeepNorth for northern ones.
        const Heightmap.Biome Edge = Heightmap.Biome.None;

        static ManualLogSource Log;
        static ConfigEntry<int> MaxPortals;
        static ConfigEntry<float> BandAnchor;
        static ConfigEntry<KeyCode> ReturnHotkey;
        // Location prefab name prefixes that get portals. ponytail: not in the config until other circle-like locations are tested.
        const string CircleLocations = "StoneCircle";
        static ConfigEntry<string> TargetLocations, UndiscoveredLocations, GateLocations;
        static ConfigEntry<string>[] BandWeights;
        static GameObject SwirlTemplate;
        static bool loggedStones;
        // Every circle and target location in the world, sent by the server once per session.
        static List<Target> Targets;
        // Each circle's planned destination per portal slot (null = nothing left to assign).
        static readonly List<(Vector3 circle, List<Target?> dests)> Plan = new List<(Vector3, List<Target?>)>();

        // The ritual: enough portals in one stone circle, all with the same name, reroll the whole plan.
        // Shift count and last shift day are global keys ("name value"): saved with the world and synced to every client.
        // Used names are stored as comma-separated stable hashes (global key values can't hold spaces).
        const string ShiftKey = "worldportal_shift", LastShiftKey = "worldportal_lastshift", UsedNamesKey = "worldportal_names";
        static ConfigEntry<bool> RitualEnabled, OpenByBiome, LeaveEntrance;
        static ConfigEntry<int> RitualPortals, RitualCooldownDays;
        static Plugin Instance;
        static float ritualSentAt = -100f;

        static readonly Func<Minimap, Vector3, bool> IsExplored = AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(AccessTools.Method(typeof(Minimap), "IsExplored"));
        static readonly Func<Player, bool> TakeInput = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.Method(typeof(Player), "TakeInput"));
        static readonly Action<Player> SetupAwake = AccessTools.MethodDelegate<Action<Player>>(AccessTools.Method(typeof(Player), "SetupAwake"));

        void Awake()
        {
            Log = Logger;
            Instance = this;
            RitualEnabled = Config.Bind("Ritual", "Enabled", true, "Building portals inside a stone circle strains the ley lines; enough of them with the same name reroll every circle's destinations.");
            RitualPortals = Config.Bind("Ritual", "Portals", 5, "Portals with the same name needed inside one stone circle.");
            RitualCooldownDays = Config.Bind("Ritual", "CooldownDays", 30, "In-game days the ley lines need to recover after a shift. Completing the ritual sooner crumbles the portals and restarts the cooldown. Read on the server.");
            MaxPortals = Config.Bind("General", "MaxPortals", 5, new ConfigDescription("Max portals per stone circle. A circle gets one per gap between its stones.", new AcceptableValueRange<int>(1, 5)));
            LeaveEntrance = Config.Bind("General", "LeaveEntrance", false, "Leave the widest gap between the stones without a portal, as an entrance. This means one portal fewer per circle. Off: every gap gets one; they're wide enough to walk past.");
            OpenByBiome = Config.Bind("General", "OpenByBiome", true, "Portals open once you've set foot in their destination's biome. Off: only once the destination itself is explored on your map.");
            TargetLocations = Config.Bind("General", "TargetLocations", "Dolmen,Runestone", "Location prefab name prefixes the portals lead to (comma separated). For example, add StoneHenge for the Plains' standing stones (StoneHenge1-6), but they usually have goblins around them. Read on the server.");
            UndiscoveredLocations = Config.Bind("General", "UndiscoveredLocations", "", "Destinations usable before you've discovered them (comma separated, empty = none). Portals to anything else stay dark until you have.");
            BandAnchor = Config.Bind("Bands", "AnchorPercentile", 0.95f, "Caution: changing anything in this section changes where every portal leads. Circles get a latitude band from 0 (the world's east-west centre line) to 3, separately for the north and south half. The bands are scaled so this percentile of the circles' distance from the centre line is the far end of band 3; circles beyond it count as band 3. 1 = the farthest circle.");
            BandWeights = new[]
            {
                Config.Bind("Bands", "Band0", "Meadows:45,BlackForest:45,Swamp:5,Mountain:5", "Biome weights for portals of circles nearest the centre line (Biome:weight, comma separated). Biomes: Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, AshLands, DeepNorth, or Edge = AshLands for southern circles and DeepNorth for northern ones."),
                Config.Bind("Bands", "Band1", "Meadows:10,BlackForest:20,Swamp:25,Mountain:25,Plains:20", "Biome weights for band 1."),
                Config.Bind("Bands", "Band2", "BlackForest:5,Swamp:20,Mountain:20,Plains:30,Mistlands:25", "Biome weights for band 2."),
                Config.Bind("Bands", "Band3", "Plains:10,Mistlands:30,Edge:60", "Biome weights for the outermost band. Every circle here also gets at least one Edge portal."),
            };
            ReturnHotkey = Config.Bind("General", "ReturnKey", KeyCode.G, "Key to travel back from the runestone you arrived at to the portal you came from.");
            GateLocations = Config.Bind("General", "GateLocations", "Dolmen", "Location prefab name prefixes that get a gate (portal swirl you walk into) in their centre (comma separated). It opens both ways once the circle portal leading here is open.");
            Harmony.CreateAndPatchAll(typeof(Plugin));
        }

        static bool Matches(string name, string prefixes) =>
            prefixes.Split(',').Select(p => p.Trim()).Any(p => p.Length > 0 && name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        static string Save(Vector3 v) => FormattableString.Invariant($"{v.x},{v.y},{v.z}");

        static Vector3? Load(Player player, string key)
        {
            if (!player.m_customData.TryGetValue(key, out var s)) return null;
            var p = s.Split(',').Select(f => float.Parse(f, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(p[0], p[1], p[2]);
        }

        // True when the thing at pos belongs to the location our last portal trip landed at.
        static bool CanReturn(Player player, Vector3 pos)
        {
            var arrival = Load(player, ArrivalKey);
            if (arrival == null) return false;
            var d = pos - arrival.Value;
            d.y = 0;
            return d.magnitude < 25f;
        }

        static bool Teleport(Player player, Vector3 pos, Quaternion rot)
        {
            if (player.IsTeleportable(false)) return player.TeleportTo(pos, rot, true);
            player.Message(MessageHud.MessageType.Center, "$msg_noteleport", 0, null, false);
            return false;
        }

        // Back to the portal we came through, else to the fallback spot.
        static void TryReturn(Player player, Vector3? fallback = null)
        {
            var back = Load(player, ReturnKey) ?? fallback;
            if (back == null || !Teleport(player, back.Value, player.transform.rotation)) return;
            player.m_customData.Remove(ArrivalKey);
            player.m_customData.Remove(ReturnKey);
        }

        static bool Reachable(Target t) => Matches(t.name, UndiscoveredLocations.Value) || (OpenByBiome.Value
            ? Player.m_localPlayer && KnowsBiome(Player.m_localPlayer, t.biome)
            : IsExplored(Minimap.instance, t.pos));

        // The game remembers biome sectors by (localized, variant) display name, so keep our own list of plain biomes.
        // ponytail: biomes count from the first visit with the mod installed; seeding from older saves would mean parsing those names.
        const string BiomesKey = "worldportal_biomes";

        static bool KnowsBiome(Player player, Heightmap.Biome biome) =>
            player.m_customData.TryGetValue(BiomesKey, out var known) && known.Split(',').Contains(biome.ToString());

        // Runs every time you cross into a sector, not just the first, so returning to a biome records it too.
        [HarmonyPatch(typeof(Player), "AddKnownBiome"), HarmonyPostfix]
        static void RememberBiome(Player __instance, BiomeSector __0)
        {
            if (KnowsBiome(__instance, __0.Biome)) return;
            __instance.m_customData.TryGetValue(BiomesKey, out var known);
            __instance.m_customData[BiomesKey] = string.IsNullOrEmpty(known) ? __0.Biome.ToString() : $"{known},{__0.Biome}";
        }

        // The circle whose open portal leads to the gate at pos, if any.
        static Vector3? LinkedCircle(Vector3 gate)
        {
            foreach (var (circle, dests) in Plan)
                for (var slot = 0; slot < dests.Count; slot++)
                    if (dests[slot] is Target d && Flat(d.pos, gate) < 25f)
                        return Destination(circle, slot) != null ? circle : (Vector3?)null;
            return null;
        }

        static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        // The planned destination for this circle's portal slot, if you've discovered it (or it's allowed undiscovered).
        static Target? Destination(Vector3 circle, int slot)
        {
            if (!Minimap.instance) return null;
            var entry = Plan.FirstOrDefault(p => Flat(p.circle, circle) < 50f);
            var dest = entry.dests != null && slot < entry.dests.Count ? entry.dests[slot] : null;
            return dest != null && Reachable(dest.Value) ? dest : null;
        }

        static List<(Heightmap.Biome biome, int weight)> ParseWeights(ConfigEntry<string> entry)
        {
            var result = new List<(Heightmap.Biome, int)>();
            foreach (var part in entry.Value.Split(',').Select(p => p.Split(':')).Where(p => p.Length == 2))
            {
                var name = part[0].Trim();
                if (name.Equals("Edge", StringComparison.OrdinalIgnoreCase)) { result.Add((Edge, int.Parse(part[1]))); continue; }
                try { result.Add(((Heightmap.Biome)Enum.Parse(typeof(Heightmap.Biome), name, true), int.Parse(part[1]))); }
                catch (ArgumentException) { Log.LogError($"{entry.Definition.Key}: unknown biome '{name}'"); }
            }
            return result;
        }

        static float Percentile(List<float> values, float p)
        {
            if (values.Count == 0) return 1f;
            values.Sort();
            var i = Mathf.Clamp01(p) * (values.Count - 1);
            var lo = (int)i;
            return lo + 1 < values.Count ? Mathf.Lerp(values[lo], values[lo + 1], i - lo) : values[lo];
        }

        // One static plan for the whole world, built identically on every client from the server's location list.
        // Each circle gets a latitude band; each portal draws a biome from its band's weights (seeded by the circle's
        // position), then takes the unused destination of that biome farthest from everything picked so far, which spreads
        // destinations over the map, preferring the circle's own half of the world.
        // Tuned by simulating 20 generated worlds: all 8 biomes reachable in every one, ~95% of the map within 1.5km of a destination.
        static void BuildPlan(int shift)
        {
            Plan.Clear();
            var circles = Targets.Where(t => Matches(t.name, CircleLocations)).OrderBy(t => Mathf.Abs(t.pos.z)).ToList();
            // After a shift, process circles in a different order too: the farthest-point picks then chain differently.
            if (shift > 0)
            {
                var order = new System.Random(shift);
                circles = circles.OrderBy(_ => order.Next()).ToList();
            }
            var targets = Targets.Where(t => !Matches(t.name, CircleLocations)).ToList();
            var weights = BandWeights.Select(ParseWeights).ToArray();
            var north = Mathf.Max(1f, Percentile(circles.Where(c => c.pos.z >= 0).Select(c => c.pos.z).ToList(), BandAnchor.Value));
            var south = Mathf.Max(1f, Percentile(circles.Where(c => c.pos.z < 0).Select(c => -c.pos.z).ToList(), BandAnchor.Value));
            var used = new HashSet<int>();
            var minDist = targets.Select(_ => float.MaxValue).ToArray();
            foreach (var c in circles)
            {
                var z = c.pos.z;
                // Each half is 3.5 bands wide: half of band 0, then bands 1-3.
                var band = Mathf.Min(3, (int)(Mathf.Abs(z) / (z >= 0 ? north : south) * 3.5f + 0.5f));
                var bandWeights = weights[band];
                var rng = new System.Random(((int)c.pos.x * 73856093) ^ ((int)z * 19349663) ^ (shift * 83492791));
                var dests = new List<Target?>();
                for (var slot = 0; slot < MaxPortals.Value; slot++)
                {
                    var roll = rng.Next(Math.Max(1, bandWeights.Sum(w => w.weight)));
                    var biome = bandWeights.FirstOrDefault(w => (roll -= w.weight) < 0).biome;
                    if (slot == 0 && band == 3) biome = Edge; // every outer circle reaches the edge of the world
                    if (biome == Edge) biome = z < 0 ? Heightmap.Biome.AshLands : Heightmap.Biome.DeepNorth;
                    var pick = Enumerable.Range(0, targets.Count)
                        .Where(i => !used.Contains(i) && targets[i].biome == biome)
                        .OrderByDescending(i => targets[i].pos.z < 0 == z < 0)
                        .ThenByDescending(i => minDist[i])
                        .ThenBy(i => Flat(targets[i].pos, c.pos))
                        .DefaultIfEmpty(-1).First();
                    if (pick < 0) { dests.Add(null); continue; }
                    used.Add(pick);
                    dests.Add(targets[pick]);
                    for (var i = 0; i < targets.Count; i++) minDist[i] = Mathf.Min(minDist[i], Flat(targets[i].pos, targets[pick].pos));
                }
                Plan.Add((c.pos, dests));
            }
            Log.LogInfo($"Portal plan: {circles.Count} circles, {Plan.Sum(p => p.dests.Count(d => d != null))} destinations: " +
                string.Join(", ", Plan.SelectMany(p => p.dests).Where(d => d != null).GroupBy(d => d.Value.biome).Select(g => $"{g.Key} {g.Count()}")));
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        static void BuildTemplates(ZNetScene __instance)
        {
            if (SwirlTemplate) return;
            var holder = new GameObject("WorldPortalTemplates");
            holder.SetActive(false); // inactive parent keeps the clones' Awake from running
            DontDestroyOnLoad(holder);
            var wood = __instance.GetPrefab("portal_wood");

            // The wood portal's swirl effect and trigger, without the frame. Used by circle portals and return gates.
            var tw = wood.GetComponent<TeleportWorld>();
            SwirlTemplate = new GameObject("WorldPortalSwirl");
            SwirlTemplate.transform.SetParent(holder.transform);
            foreach (var part in new[] { tw.m_target_found.transform, wood.GetComponentInChildren<TeleportWorldTrigger>(true).transform })
            {
                var copy = Instantiate(part.gameObject, SwirlTemplate.transform);
                copy.transform.localPosition = wood.transform.InverseTransformPoint(part.position);
                copy.transform.localRotation = Quaternion.Inverse(wood.transform.rotation) * part.rotation;
                copy.SetActive(true);
                var trigger = copy.GetComponent<TeleportWorldTrigger>();
                if (!trigger) continue;
                DestroyImmediate(trigger);
                copy.AddComponent<PortalTrigger>();
            }
        }

        [HarmonyPatch(typeof(Game), "Start"), HarmonyPostfix]
        static void RegisterRpcs()
        {
            Targets = null;
            ZRoutedRpc.instance.Register("WorldPortal_RequestTargets", sender =>
            {
                if (!ZNet.instance.IsServer()) return;
                var targets = ZoneSystem.instance.m_locationInstances.Values
                    .Where(l => Matches(l.m_location.m_prefabName, TargetLocations.Value) || Matches(l.m_location.m_prefabName, CircleLocations)).ToList();
                var pkg = new ZPackage();
                pkg.Write(targets.Count);
                foreach (var l in targets)
                {
                    pkg.Write(l.m_location.m_prefabName);
                    pkg.Write(l.m_position);
                    pkg.Write((int)WorldGenerator.instance.GetBiome(l.m_position));
                }
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "WorldPortal_Targets", pkg);
            });
            ZRoutedRpc.instance.Register<ZPackage>("WorldPortal_Targets", (sender, pkg) =>
            {
                var count = pkg.ReadInt();
                Targets = Enumerable.Range(0, count).Select(_ => (pkg.ReadString(), pkg.ReadVector3(), (Heightmap.Biome)pkg.ReadInt())).ToList();
                BuildPlan(GlobalInt(ShiftKey, 0));
            });

            // Server decides. A name that already caused a shift: no reaction. Otherwise shift if the ley lines have
            // recovered, else overload; either way the cooldown restarts and the ritualist's client crumbles the portals.
            ZRoutedRpc.instance.Register<Vector3, string>("WorldPortal_Ritual", (sender, circle, name) =>
            {
                if (!ZNet.instance.IsServer()) return;
                var usedNames = GlobalString(UsedNamesKey);
                var hash = name.GetStableHashCode().ToString();
                if (usedNames.Split(',').Contains(hash))
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, "WorldPortal_NameUsed");
                    return;
                }
                var day = EnvMan.instance.GetDay();
                var recovered = day - GlobalInt(LastShiftKey, int.MinValue / 2) >= RitualCooldownDays.Value;
                ZoneSystem.instance.SetGlobalKey($"{LastShiftKey} {day}");
                if (!recovered)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, "WorldPortal_Overloaded", circle);
                    return;
                }
                var shift = GlobalInt(ShiftKey, 0) + 1;
                ZoneSystem.instance.SetGlobalKey($"{ShiftKey} {shift}");
                ZoneSystem.instance.SetGlobalKey($"{UsedNamesKey} {(usedNames.Length > 0 ? usedNames + "," : "")}{hash}");
                Log.LogInfo($"Ley lines shifted (shift {shift}) by a ritual at {circle}");
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "WorldPortal_Shifted", sender, shift, circle);
            });
            ZRoutedRpc.instance.Register<long, int, Vector3>("WorldPortal_Shifted", (sender, ritualist, shift, circle) =>
            {
                if (Targets?.Count > 0) BuildPlan(shift);
                var player = Player.m_localPlayer;
                if (!player) return;
                if (ritualist != ZNet.GetUID())
                {
                    player.Message(MessageHud.MessageType.Center, "The ley lines shift…", 0, null, false);
                    return;
                }
                CrumbleAll(circle);
                Instance.StartCoroutine(KnockedOut(player));
            });
            ZRoutedRpc.instance.Register<Vector3>("WorldPortal_Overloaded", (sender, circle) =>
            {
                var player = Player.m_localPlayer;
                if (!player) return;
                CrumbleAll(circle);
                GameCamera.instance.AddShake(player.transform.position, 30f, 2f, false);
                player.Message(MessageHud.MessageType.Center, "The ley lines are overloaded. The stones reject the gates.", 0, null, false);
            });
            ZRoutedRpc.instance.Register("WorldPortal_NameUsed", sender =>
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "The stones remember that name. They will not answer it twice.", 0, null, false));
        }

        static string GlobalString(string key) =>
            ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(key, out string value) ? value : "";

        static int GlobalInt(string key, int fallback) => int.TryParse(GlobalString(key), out var n) ? n : fallback;

        // Client-side guess for messages only; the server makes the real call when the ritual completes.
        static bool Recovering() => EnvMan.instance &&
            EnvMan.instance.GetDay() - GlobalInt(LastShiftKey, int.MinValue / 2) < RitualCooldownDays.Value;

        static Location CircleAt(Vector3 pos)
        {
            var location = Location.GetLocation(pos, false);
            return location && Matches(Utils.GetPrefabName(location.gameObject), CircleLocations) ? location : null;
        }

        // Built (networked) vanilla portals inside the circle; ghosts have no valid ZDO.
        static List<TeleportWorld> PortalsIn(Location circle)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(circle.transform.position, circle.m_exteriorRadius, pieces);
            return pieces.Select(p => p.GetComponent<TeleportWorld>()).Where(t => t && t.GetComponent<ZNetView>().IsValid()).ToList();
        }

        static readonly string[] Strain =
        {
            "The ley lines are being strained.",
            "The ley lines groan beneath the stones.",
            "The stones tremble. The ley lines strain to breaking.",
            "Something old stirs beneath the circle.",
        };

        [HarmonyPatch(typeof(Player), "PlacePiece"), HarmonyPostfix]
        static void WarnStrain(Piece piece, Vector3 pos)
        {
            if (!RitualEnabled.Value || !piece.GetComponent<TeleportWorld>()) return;
            var circle = CircleAt(pos);
            if (!circle) return;
            var count = PortalsIn(circle).Count;
            string message;
            if (Recovering())
                message = count >= RitualPortals.Value ? "The stones are silent. The ley lines have not recovered." : "The ley lines are still recovering.";
            else if (count >= RitualPortals.Value)
                message = $"{(RitualPortals.Value == 5 ? "Five" : RitualPortals.Value.ToString())} gates, one name, and the stones will answer.";
            else
                message = Strain[Mathf.Clamp(count - 1 + Strain.Length - (RitualPortals.Value - 1), 0, Strain.Length - 1)];
            GameCamera.instance.AddShake(pos, 30f, 0.4f * Mathf.Min(count, RitualPortals.Value), false);
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, message, 0, null, false);
        }

        // The tag reaches the portal's owner by RPC, so check a moment after naming it.
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.SetText)), HarmonyPostfix]
        static void CheckRitual(TeleportWorld __instance)
        {
            if (RitualEnabled.Value && CircleAt(__instance.transform.position)) Instance.StartCoroutine(CheckRitualSoon(__instance.transform.position));
        }

        static System.Collections.IEnumerator CheckRitualSoon(Vector3 pos)
        {
            yield return new WaitForSeconds(1f);
            var circle = CircleAt(pos);
            if (!circle || Time.time - ritualSentAt < 10f) yield break;
            var portals = PortalsIn(circle);
            var names = portals.Select(p => p.GetText().Trim().ToLowerInvariant()).Distinct().ToList();
            if (portals.Count < RitualPortals.Value || names.Count != 1 || names[0].Length == 0) yield break;
            ritualSentAt = Time.time;
            ZRoutedRpc.instance.InvokeRoutedRPC("WorldPortal_Ritual", circle.transform.position, names[0]);
        }

        static void CrumbleAll(Vector3 circlePos)
        {
            var circle = CircleAt(circlePos);
            if (circle) foreach (var portal in PortalsIn(circle)) Crumble(portal);
        }

        // Breaks the portal (with its destroy effect) and gives back half of what it cost.
        static void Crumble(TeleportWorld portal)
        {
            var pos = portal.transform.position + Vector3.up;
            foreach (var req in portal.GetComponent<Piece>().m_resources.Where(r => r.m_resItem && r.m_amount / 2 > 0))
                Instantiate(req.m_resItem.gameObject, pos, Quaternion.identity).GetComponent<ItemDrop>().SetStack(req.m_amount / 2);
            portal.GetComponent<WearNTear>().Remove(true); // true: no drops of its own
        }

        // Shake and blur, black out into a teleport to another circle, wake up on the ground.
        static System.Collections.IEnumerator KnockedOut(Player player)
        {
            var fx = CameraEffects.instance;
            var (forceDof, autoFocus, minDist, maxDist) = (fx.m_forceDof, fx.m_dofAutoFocus, fx.m_dofMinDistance, fx.m_dofMaxDistance);
            fx.m_forceDof = true; // ponytail: blur via the game's depth of field; players with DoF off in settings only get the shake
            fx.m_dofAutoFocus = false;
            for (var t = 0f; t < 3f; t += Time.deltaTime)
            {
                GameCamera.instance.AddShake(player.transform.position, 10f, 0.5f + t, false);
                fx.m_dofMinDistance = fx.m_dofMaxDistance = Mathf.Lerp(maxDist, 0.2f, t / 3f);
                yield return null;
            }
            var here = player.transform.position;
            var circles = (Targets ?? new List<Target>()).Where(c => Matches(c.name, CircleLocations) && Flat(c.pos, here) > 100f).ToList();
            var landing = circles.Count > 0 ? circles[UnityEngine.Random.Range(0, circles.Count)].pos : here;
            player.m_customData.Remove(ArrivalKey);
            player.m_customData.Remove(ReturnKey);
            player.TeleportTo(landing + Vector3.forward * 2f, player.transform.rotation, true); // the teleport's own black screen is the fade
            yield return new WaitForSeconds(1f);
            (fx.m_forceDof, fx.m_dofAutoFocus, fx.m_dofMinDistance, fx.m_dofMaxDistance) = (forceDof, autoFocus, minDist, maxDist);
            while (player && player.IsTeleporting()) yield return null;
            if (!player) yield break;
            // Same lying-down wake-up the game plays when you log in.
            player.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_wakeup, true);
            SetupAwake(player);
            yield return new WaitForSeconds(4f);
            player.Message(MessageHud.MessageType.Center, "The ley lines shift…", 0, null, false);
        }

        // Circles get portals and dolmens get a return gate, as part of the location itself:
        // there whenever it's loaded, nothing saved, nothing to break.
        [HarmonyPatch(typeof(Location), "Awake"), HarmonyPostfix]
        static void DecorateLocation(Location __instance)
        {
            if (!SwirlTemplate) return;
            var name = Utils.GetPrefabName(__instance.gameObject);
            if (Matches(name, GateLocations.Value))
                Instantiate(SwirlTemplate, __instance.transform.position, __instance.transform.rotation, __instance.transform);
            if (Matches(name, CircleLocations))
                PlacePortals(__instance);
        }

        // Finds the standing stones by shape (tall solid colliders around the centre) and fills every gap but the widest.
        static void PlacePortals(Location circle)
        {
            var centre = circle.transform.position;
            var colliders = circle.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.bounds.size.y > 1.5f).ToList();
            if (!loggedStones)
            {
                loggedStones = true;
                Log.LogInfo("Stone circle colliders (name, height, distance from centre): " + string.Join("; ", colliders.Select(c =>
                    $"{c.name} {c.bounds.size.y:0.0}m {Vector3.Distance(c.bounds.center, centre):0.0}m")));
            }
            var stones = colliders
                .Select(c => { var d = c.bounds.center - centre; return new Vector2(d.x, d.z); })
                .Where(d => d.magnitude > 2f && d.magnitude < circle.m_exteriorRadius)
                .ToList();

            // Colliders of the same stone sit at about the same angle; merge them.
            var angles = new List<float>();
            foreach (var a in stones.Select(d => Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg).OrderBy(a => a))
                if (angles.Count == 0 || a - angles[angles.Count - 1] > 15f) angles.Add(a);
            if (angles.Count > 1 && angles[0] + 360f - angles[angles.Count - 1] < 15f) angles.RemoveAt(angles.Count - 1);
            if (angles.Count < 3)
            {
                Log.LogWarning($"Stone circle at {centre}: found {angles.Count} stones, no portals placed.");
                return;
            }

            var radius = stones.Average(d => d.magnitude);
            var gaps = angles.Select((a, i) => (from: a, to: i + 1 < angles.Count ? angles[i + 1] : angles[0] + 360f)).ToList();
            var entrance = gaps.OrderByDescending(g => g.to - g.from).First();
            var spots = gaps.Where(g => !LeaveEntrance.Value || g != entrance).Take(MaxPortals.Value)
                .Select(g => (g.from + g.to) / 2f * Mathf.Deg2Rad)
                .Select(a => centre + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius).ToList();
            // All portals at the height of the highest ground under them: level as a set, none buried.
            var height = spots.Max(p => ZoneSystem.instance.GetGroundHeight(p));
            var slot = 0;
            foreach (var spot in spots)
            {
                var pos = new Vector3(spot.x, height, spot.z);
                var inward = centre - pos;
                inward.y = 0;
                var portal = Instantiate(SwirlTemplate, pos, Quaternion.LookRotation(inward), circle.transform).AddComponent<CirclePortal>();
                portal.Slot = slot++;
                portal.Circle = centre;
            }
        }

        // What the portal whispers about where it leads.
        static string Lore(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return "A calm place of soft grass and gentle wind";
                case Heightmap.Biome.BlackForest: return "A dark wood where the trees grow close";
                case Heightmap.Biome.Swamp: return "A drowned land that smells of rot";
                case Heightmap.Biome.Mountain: return "Cold stone and howling wind, high above the clouds";
                case Heightmap.Biome.Plains: return "Golden fields beneath a burning sun";
                case Heightmap.Biome.Mistlands: return "A land swallowed by mist";
                case Heightmap.Biome.AshLands: return "Fire and ash, where the sky burns red";
                case Heightmap.Biome.DeepNorth: return "Endless ice, where the cold never sleeps";
                default: return "Restless waters";
            }
        }

        static string Farness(float meters) =>
            meters < 1500f ? "not far from here" :
            meters < 4000f ? "far away" :
            meters < 7000f ? "far, far away" :
            "at the very edge of the world";

        // The edges of the world don't open by walking in; you have to mean it.
        static string Dare(Heightmap.Biome biome) =>
            biome == Heightmap.Biome.AshLands ? "Brave the heat" :
            biome == Heightmap.Biome.DeepNorth ? "Challenge the blizzard" : null;

        class CirclePortal : MonoBehaviour, Hoverable, Interactable
        {
            public int Slot;
            public Vector3 Circle;
            Target? dest;
            EffectFade[] fades;
            float nextCheck;

            void Start()
            {
                fades = GetComponentsInChildren<EffectFade>(true);

                // No frame to look at, so give the hover raycast something: a box on a layer you can walk through.
                var hover = new GameObject("hover") { layer = LayerMask.NameToLayer("piece_nonsolid") };
                hover.transform.SetParent(transform, false);
                var box = hover.AddComponent<BoxCollider>();
                box.center = fades.Length > 0 ? fades[0].transform.localPosition : Vector3.up * 1.5f;
                box.size = new Vector3(2.5f, 3f, 0.5f);
            }

            // Re-picks every 2s so newly discovered places light the portal up without sorting every frame.
            void Update()
            {
                if (Time.time < nextCheck) return;
                nextCheck = Time.time + 2f;
                dest = Destination(Circle, Slot);
                foreach (var fade in fades) fade.SetActive(dest != null);
            }

            public string GetHoverName() => "Ancient Portal";
            public float GetHoverOffset() => 0f;

            public string GetHoverText()
            {
                if (dest == null) return "Ancient Portal\nThe stones are silent. Explore further.";
                var dare = Dare(dest.Value.biome);
                return Localization.instance.Localize($"Ancient Portal\n<i>{Lore(dest.Value.biome)}, {Farness(Flat(dest.Value.pos, Circle))}.</i>" +
                    (dare == null ? "" : $"\n[<color=yellow><b>$KEY_Use</b></color>] {dare}"));
            }

            public void WalkIn(Player player)
            {
                if (dest != null && Dare(dest.Value.biome) == null) Enter(player);
            }

            public bool Interact(Humanoid user, bool hold, bool alt)
            {
                if (hold || dest == null || Dare(dest.Value.biome) == null || user != Player.m_localPlayer) return false;
                Enter(Player.m_localPlayer);
                return true;
            }

            public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

            void Enter(Player player)
            {
                var target = dest.Value;
                // ponytail: lands near the location centre on the side we came from; relies on distant-teleport ground snapping for height.
                var away = Circle - target.pos;
                away.y = 0;
                var arrive = target.pos + away.normalized * (Matches(target.name, CircleLocations) ? 2f : 5f);
                if (Teleport(player, arrive, Quaternion.LookRotation(-away)))
                {
                    player.m_customData[ArrivalKey] = Save(target.pos);
                    // Step back into the circle, clear of the portal, so we don't walk straight back through it.
                    player.m_customData[ReturnKey] = Save(transform.position + transform.forward * 2f);
                }
            }
        }

        class PortalTrigger : MonoBehaviour
        {
            CirclePortal portal;
            EffectFade[] gateFades; // return gates only; circle portals light themselves
            float nextCheck;

            void Start()
            {
                portal = GetComponentInParent<CirclePortal>();
                if (!portal) gateFades = transform.parent.GetComponentsInChildren<EffectFade>(true);

                // Any portal or gate needs the plan; ask the server once.
                if (Targets != null || ZRoutedRpc.instance == null) return;
                Targets = new List<Target>(); // marks the request as sent
                ZRoutedRpc.instance.InvokeRoutedRPC("WorldPortal_RequestTargets");
            }

            // A gate opens once the circle portal leading here is open, and stays open both ways.
            void Update()
            {
                if (gateFades == null || Time.time < nextCheck) return;
                nextCheck = Time.time + 2f;
                var open = LinkedCircle(transform.position) != null || Player.m_localPlayer && CanReturn(Player.m_localPlayer, transform.position);
                foreach (var fade in gateFades) fade.SetActive(open);
            }

            void OnTriggerEnter(Collider other)
            {
                var player = other.GetComponentInParent<Player>();
                if (!player || player != Player.m_localPlayer) return;
                if (portal) { portal.WalkIn(player); return; }
                var circle = LinkedCircle(transform.position);
                if (CanReturn(player, transform.position)) TryReturn(player);
                else if (circle != null) TryReturn(player, circle.Value + Vector3.forward * 2f);
            }
        }

        [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.GetHoverText)), HarmonyPostfix]
        static void ShowReturn(RuneStone __instance, ref string __result)
        {
            if (Player.m_localPlayer && CanReturn(Player.m_localPlayer, __instance.transform.position))
                __result += $"\n[<color=yellow><b>{ReturnHotkey.Value}</b></color>] Return to the stone circle";
        }

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        static void ReturnOnKey(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !TakeInput(__instance) || !ZInput.GetKeyDown(ReturnHotkey.Value, false)) return;
            var stone = __instance.GetHoverObject()?.GetComponentInParent<RuneStone>();
            if (stone && CanReturn(__instance, stone.transform.position)) TryReturn(__instance);
        }
    }
}
