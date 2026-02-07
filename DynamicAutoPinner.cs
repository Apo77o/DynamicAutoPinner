using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using System.Text.RegularExpressions;

namespace DynamicAutoPinner
{
    [BepInPlugin("com.apo77o.dynamicautopinner", "Dynamic Auto Pinner", "2.2.0")]
    public class DynamicAutoPinner : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Enabled { get; private set; }
        public static ConfigEntry<float> ScanRadius { get; private set; }
        public static ConfigEntry<float> ScanInterval { get; private set; }
        public static ConfigEntry<bool> WishboneRequiredForHidden { get; private set; }
        public static ConfigEntry<float> WishboneRangeMultiplier { get; private set; }
        public static ConfigEntry<bool> FarmFilter { get; private set; }
        public static ConfigEntry<bool> RegrowFilter { get; private set; }
        public static ConfigEntry<float> GroupDistanceOre { get; private set; }
        public static ConfigEntry<float> GroupDistanceFarm { get; private set; }
        public static ConfigEntry<int> GroupCap { get; private set; }
        public static ConfigEntry<bool> ServerEnforceConfigs { get; private set; }
        public static ConfigEntry<bool> PinSync { get; private set; }
        public static ConfigEntry<float> SyncThrottle { get; private set; }
        public static ConfigEntry<bool> IconsOnly { get; private set; }
        public static ConfigEntry<string> Language { get; private set; }
        public static ConfigEntry<int> OreIcon { get; private set; }
        public static ConfigEntry<int> SpawnerIcon { get; private set; }
        public static ConfigEntry<int> FarmIcon { get; private set; }
        public static ConfigEntry<KeyCode> HotkeyToggle { get; private set; }
        public static ConfigEntry<KeyCode> HotkeyReload { get; private set; }
        public static ConfigEntry<KeyCode> HotkeyRegen { get; private set; }
        public static ConfigEntry<KeyCode> HotkeyExport { get; private set; }
        public static ConfigEntry<float> BoatThrottleSpeed { get; private set; }
        public static ConfigEntry<int> VehiclePinExpireTime { get; private set; }
        public static ConfigEntry<string> ExtraPrefabs { get; private set; }
        private Harmony harmony;
        private float scanTimer;
        private Dictionary<PinCategory, List<string>> categoryPrefabs = new();
        private Dictionary<string, PinCluster> existingClusters = new();
        private Dictionary<string, Dictionary<string, string>> langDict = new();
        private bool isScanning;
        private readonly System.Random rng = new();
        private Dictionary<string, Vector3> noPinZones = new();
        private Dictionary<string, float> noPinRadii = new();
        private List<string> hiddenPrefabs = new();
        private Collider[] buffer = new Collider[1024];
        private const string RPC_ADD_PIN = "DynamicAutoPinner_AddPin";
        public enum PinCategory { Ore, Spawner, Farm, Structure, Chest, Mine, Leviathan, Vehicle }
        public struct PinCluster
        {
            public Vector3 pos;
            public int count;
            public string prefab;
            public PinCategory cat;
        }
        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Enable auto-pinning");
            ScanRadius = Config.Bind("Scanner", "Radius", 100f, "Scan radius (m) - match map discovery");
            ScanInterval = Config.Bind("Scanner", "Interval", 5f, "Scan interval (s)");
            WishboneRequiredForHidden = Config.Bind("Wishbone", "RequiredForHidden", false, "Require wishbone for buried pins");
            WishboneRangeMultiplier = Config.Bind("Wishbone", "RangeMultiplier", 1f, "Wishbone range multiplier (1.0 = vanilla)");
            FarmFilter = Config.Bind("Filters", "Farms", true, "Filter PlantEverything clones/regrows");
            RegrowFilter = Config.Bind("Filters", "Regrow", true, "Skip <5min spawns");
            GroupDistanceOre = Config.Bind("Grouping", "OreDistance", 10f, "Merge ores within (m)");
            GroupDistanceFarm = Config.Bind("Grouping", "FarmDistance", 20f, "Merge farms within (m)");
            GroupCap = Config.Bind("Grouping", "Cap", 50, "Max per group; merge to super-group if exceeded");
            ServerEnforceConfigs = Config.Bind("MP", "EnforceConfigs", false, "Server overrides client configs/version");
            PinSync = Config.Bind("MP", "PinSync", false, "Sync pins across players");
            SyncThrottle = Config.Bind("MP", "SyncThrottle", 30f, "Sync delay (s, min 1)");
            IconsOnly = Config.Bind("UI", "IconsOnly", false, "No text on pins (MP compat)");
            Language = Config.Bind("UI", "Language", "en", "Lang: en/de/fr");
            OreIcon = Config.Bind("Icons", "Ore", 2, "Vanilla icon 0-23");
            SpawnerIcon = Config.Bind("Icons", "Spawner", 5, "Vanilla icon");
            FarmIcon = Config.Bind("Icons", "Farm", 10, "Vanilla icon");
            HotkeyToggle = Config.Bind("Hotkeys", "Toggle", KeyCode.F1, "Toggle mod");
            HotkeyReload = Config.Bind("Hotkeys", "Reload", KeyCode.F5, "Reload prefabs");
            HotkeyRegen = Config.Bind("Hotkeys", "Regen", KeyCode.F9, "Regen nearby pins");
            HotkeyExport = Config.Bind("Hotkeys", "Export", KeyCode.F10, "Export/import pins");
            BoatThrottleSpeed = Config.Bind("Scanner", "BoatThrottleSpeed", 5f, "Skip scans above speed (0 = off)");
            VehiclePinExpireTime = Config.Bind("Vehicles", "PinExpireTime", 600, "Vehicle last-pos expiry (s)");
            ExtraPrefabs = Config.Bind("Advanced", "ExtraPrefabs", "", "Comma-separated modded prefabs");
            harmony = new Harmony("com.apo77o.dynamicautopinner");
            harmony.PatchAll();
            Config.SaveOnConfigSet = true;
            InvokeRepeating(nameof(LoadDynamicPrefabs), 1f, 1f);
            InvokeRepeating(nameof(LoadLanguages), 2f, 2f);
            Logger.LogInfo("Dynamic Auto Pinner loaded!");
            RegisterRPCs();
        }
        private void Update()
        {
            if (!Enabled.Value || Player.m_localPlayer == null) return;
            scanTimer += Time.deltaTime;
            if (scanTimer >= ScanInterval.Value && !isScanning)
            {
                scanTimer = 0f;
                StartCoroutine(ScanCoroutine());
            }
            if (UnityEngine.Input.GetKeyDown(HotkeyToggle.Value)) Enabled.Value = !Enabled.Value;
            if (UnityEngine.Input.GetKeyDown(HotkeyReload.Value)) { LoadDynamicPrefabs(); Logger.LogInfo("Prefabs reloaded!"); }
            if (UnityEngine.Input.GetKeyDown(HotkeyRegen.Value)) RegenNearbyPins();
            if (UnityEngine.Input.GetKeyDown(HotkeyExport.Value)) ExportPins();
        }
        private void LoadDynamicPrefabs()
        {
            if (ObjectDB.instance == null) return;
            categoryPrefabs.Clear();
            foreach (var cat in (PinCategory[])Enum.GetValues(typeof(PinCategory)))
                categoryPrefabs[cat] = new List<string>();
            int oreCount = 0, farmCount = 0;
            hiddenPrefabs.Clear();
            foreach (GameObject prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null) continue;
                string name = prefab.name.ToLower();
                if (name.StartsWith("piece_") || name.StartsWith("item_") || name.StartsWith("decor_") ||
                    name.Contains("remains") || name.Contains("random")) continue;
                if (prefab.GetComponent<MineRock>() != null) { categoryPrefabs[PinCategory.Ore].Add(name); oreCount++; }
                else if (prefab.GetComponent<SpawnArea>() != null) categoryPrefabs[PinCategory.Spawner].Add(name);
                else if (prefab.GetComponent<Pickable>() != null && !name.Contains("item")) { categoryPrefabs[PinCategory.Farm].Add(name); farmCount++; }
                else if (prefab.GetComponent<DungeonGenerator>() != null || name.Contains("crypt")) categoryPrefabs[PinCategory.Structure].Add(name);
                else if (prefab.GetComponent<Container>() != null) categoryPrefabs[PinCategory.Chest].Add(name);
                else if (prefab.GetComponent<MineRock>() != null) categoryPrefabs[PinCategory.Mine].Add(name);
                else if (name.Contains("crypt") || name.Contains("dungeon") || name.Contains("mine_")) categoryPrefabs[PinCategory.Structure].Add(name);
                else if (name.Contains("tar")) categoryPrefabs[PinCategory.Farm].Add(name);
                if (name.Contains("leviathan")) categoryPrefabs[PinCategory.Leviathan].Add(name);
            }
            hiddenPrefabs.Add("silvervein");
            hiddenPrefabs.Add("silvervein_frac");
            hiddenPrefabs.Add("muddyscrap_pile");
            hiddenPrefabs.Add("mudpile_frac");
            hiddenPrefabs.Add("mudpile_old");
            hiddenPrefabs.Add("treasurechest_meadows_buried");
            hiddenPrefabs.Add("treasurechest_blackforest_buried");
            if (!string.IsNullOrEmpty(ExtraPrefabs.Value))
            {
                foreach (string p in ExtraPrefabs.Value.Split(','))
                    categoryPrefabs[PinCategory.Farm].Add(p.Trim().ToLower());
            }
            string pluginDir = Path.Combine(Paths.PluginPath, "DynamicAutoPinner");
            Directory.CreateDirectory(pluginDir);
            var csv = new StringBuilder("Category,Prefab\n");
            foreach (var kv in categoryPrefabs)
                foreach (string p in kv.Value)
                    csv.AppendLine($"{kv.Key},{p}");
            File.WriteAllText(Path.Combine(pluginDir, "prefabs.csv"), csv.ToString());
            Logger.LogInfo($"Dynamic Prefabs: {oreCount} ores, {farmCount} farms +{categoryPrefabs[PinCategory.Structure].Count} structures!");
        }
        private void LoadLanguages()
        {
            string langPath = Path.Combine(Paths.PluginPath, "DynamicAutoPinner", "lang");
            if (!Directory.Exists(langPath)) Directory.CreateDirectory(langPath);
            langDict.Clear();
            string[] langs = { "en", "de", "fr" };
            foreach (string l in langs)
            {
                string file = Path.Combine(langPath, $"{l}.json");
                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file);
                    Dictionary<string, string> dict = SimpleJsonToDict(json);
                    langDict[l] = dict;
                }
            }
        }
        private Dictionary<string, string> SimpleJsonToDict(string json)
        {
            try
            {
                json = json.Trim().Trim('{', '}');
                var dict = new Dictionary<string, string>();
                var pairs = json.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    try
                    {
                        var parts = pair.Split(new char[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            string key = UnescapeJsonString(parts[0].Trim().Trim('"'));
                            string value = UnescapeJsonString(parts[1].Trim().Trim('"'));
                            dict[key] = value;
                        }
                        else
                        {
                            Logger.LogWarning($"Skipping invalid pair in JSON: {pair}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Error parsing pair '{pair}': {ex.Message}");
                    }
                }
                return dict;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to parse language file: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }
        private string UnescapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return input.Replace("\\\"", "\"")
                        .Replace("\\\\", "\\")
                        .Replace("\\n", "\n")
                        .Replace("\\r", "\r")
                        .Replace("\\t", "\t")
                        .Replace("\\b", "\b")
                        .Replace("\\f", "\f");
        }
        private IEnumerator ScanCoroutine()
        {
            yield return ScanCoroutine(0f);
        }
        private IEnumerator ScanCoroutine(float overrideRadius = 0f)
        {
            isScanning = true;
            Player player = Player.m_localPlayer;
            if (player == null) { isScanning = false; yield break; }
            Vector3 pos = player.transform.position;
            float radius = overrideRadius > 0f ? overrideRadius : ScanRadius.Value;
            bool noMap = (bool)AccessTools.Field(typeof(Player), nameof(Player.m_noMap)).GetValue(player);
            if (noMap) { isScanning = false; yield break; }
            if (player.GetControlledShip() != null && player.GetControlledShip().GetSpeed() > BoatThrottleSpeed.Value)
            {
                isScanning = false;
                yield break;
            }
            bool hasWishbone = player.GetInventory().HaveItem("$item_wishbone");
            if (hasWishbone) radius *= WishboneRangeMultiplier.Value;
            ParsePlayerPins();
            LayerMask layers = -1;
            Dictionary<PinCategory, List<Vector3>> finds = new();
            for (int page = 0; page < 4; page++)
            {
                Vector3 offset = new Vector3(rng.Next(-10, 10), 0, rng.Next(-10, 10));
                int hitCount = Physics.OverlapSphereNonAlloc(pos + offset, radius, buffer, layers);
                for (int i = 0; i < hitCount; i++)
                {
                    Collider col = buffer[i];
                    if (col == null) continue;
                    ZNetView zview = col.GetComponentInParent<ZNetView>() ?? col.GetComponent<ZNetView>();
                    if (zview == null || !zview.IsValid()) continue;
                    Vector3 wpos = col.transform.position;
                    ZDO zdo = zview.GetZDO();
                    int prefabHash = (int)AccessTools.Field(typeof(ZDO), nameof(ZDO.m_prefab)).GetValue(zdo);
                    GameObject prefabObj = ZNetScene.instance.GetPrefab(prefabHash);
                    if (prefabObj == null) continue;
                    string prefab = prefabObj.name.ToLower();
                    string qpos = QuantizePos(wpos);
                    string hash = $"{prefab}:{qpos}";
                    if (finds.Values.Any(l => l.Any(p => Vector3.Distance(p, wpos) < 1f))) continue;
                    PinCategory? cat = GetCategory(prefab);
                    if (!cat.HasValue) continue;
                    if (IsHiddenResource(prefab) && WishboneRequiredForHidden.Value && !hasWishbone) continue;
                    if (!IsValidPin(zview, prefab)) continue;
                    if (InNoPinZone(wpos)) continue;
                    if (!finds.ContainsKey(cat.Value)) finds[cat.Value] = new List<Vector3>();
                    finds[cat.Value].Add(wpos);
                }
                yield return null;
            }
            if (GetPins().Count(p => Vector3.Distance(p.m_pos, pos) < 200f) > 50) { isScanning = false; yield break; }
            foreach (var kv in finds)
            {
                List<Vector3> poss = kv.Value;
                if (poss.Count == 0) continue;
                float groupDist = kv.Key == PinCategory.Ore ? GroupDistanceOre.Value : GroupDistanceFarm.Value;
                var clusters = ClusterPositions(poss, groupDist, kv.Key);
                foreach (var cluster in clusters.Take(GroupCap.Value))
                {
                    string label = IconsOnly.Value ? "" : GetLabel(cluster.prefab, Language.Value) + (cluster.count > 1 ? $" x{cluster.count}" : "");
                    int icon = GetIcon(cluster.cat);
                    string clusterHash = GetClusterHash(cluster.pos, cluster.prefab);
                    if (existingClusters.ContainsKey(clusterHash)) continue;
                    if (Minimap.instance == null) continue;
                    Minimap.PinData pin = Minimap.instance.AddPin(cluster.pos, (Minimap.PinType)icon, label, false, false);
                    pin.m_name = "AUTO_" + label;
                    existingClusters[clusterHash] = cluster;
                    if (PinSync.Value && ZNet.instance.IsServer())
                    {
                        StartCoroutine(SendPinRPC(pin));
                    }
                }
            }
            isScanning = false;
        }
        private PinCategory? GetCategory(string prefab)
        {
            prefab = prefab.ToLower();
            foreach (var kv in categoryPrefabs)
                if (kv.Value.Contains(prefab)) return kv.Key;
            return null;
        }
        private List<PinCluster> ClusterPositions(List<Vector3> poss, float maxDist, PinCategory cat)
        {
            var clusters = new List<PinCluster>();
            var remaining = new List<Vector3>(poss);
            while (remaining.Count > 0)
            {
                Vector3 center = remaining[0];
                var group = new List<Vector3> { center };
                remaining.RemoveAt(0);
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (Vector3.Distance(center, remaining[i]) < maxDist)
                    {
                        group.Add(remaining[i]);
                        remaining.RemoveAt(i);
                    }
                }
                if (IsFuzzyGridOrLine(group))
                {
                }
                Vector3 avgPos = group.Aggregate(Vector3.zero, (a, b) => a + b) / group.Count;
                clusters.Add(new PinCluster { pos = avgPos, count = group.Count, prefab = "group", cat = cat });
            }
            if (clusters.Count > GroupCap.Value)
            {
                Vector3 superCenter = clusters.Select(c => c.pos).Aggregate(Vector3.zero, (a, b) => a + b) / clusters.Count;
                clusters.Clear();
                clusters.Add(new PinCluster { pos = superCenter, count = poss.Count, prefab = "super_group", cat = cat });
            }
            return clusters;
        }
        private bool IsFuzzyGridOrLine(List<Vector3> points, float tolerance = 1f)
        {
            if (points.Count < 3) return false;
            var sortedX = points.OrderBy(p => p.x).ToList();
            var dx = sortedX[1].x - sortedX[0].x;
            for (int i = 2; i < sortedX.Count; i++)
            {
                if (Mathf.Abs((sortedX[i].x - sortedX[i - 1].x) - dx) > tolerance) return false;
            }
            var sortedZ = points.OrderBy(p => p.z).ToList();
            var dz = sortedZ[1].z - sortedZ[0].z;
            for (int i = 2; i < sortedZ.Count; i++)
            {
                if (Mathf.Abs((sortedZ[i].z - sortedZ[i - 1].z) - dz) > tolerance) return false;
            }
            return true;
        }
        private string QuantizePos(Vector3 p) => $"{p.x:F2}:{p.z:F2}";
        private string GetClusterHash(Vector3 pos, string prefab) => $"{prefab}:{QuantizePos(pos)}";
        private string GetLabel(string prefab, string lang) => langDict.ContainsKey(lang) && langDict[lang].ContainsKey(prefab) ? langDict[lang][prefab] : prefab;
        private int GetIcon(PinCategory cat)
        {
            return cat switch
            {
                PinCategory.Ore => OreIcon.Value,
                PinCategory.Spawner => SpawnerIcon.Value,
                PinCategory.Farm => FarmIcon.Value,
                _ => 1
            };
        }
        private bool IsValidPin(ZNetView zview, string prefab)
        {
            if (FarmFilter.Value && (prefab.Contains("(clone)") || prefab.Contains("pe_"))) return false;
            if (RegrowFilter.Value)
            {
                ZDO zdo = zview.GetZDO();
                long created = zdo.GetLong("created", 0);
                if (created > 0 && (ZNet.instance.GetTime().Ticks - created) / TimeSpan.TicksPerSecond < 300) return false;
            }
            return zview.GetZDO().GetOwner() != Player.m_localPlayer.GetPlayerID();
        }
        private bool IsHiddenResource(string prefab)
        {
            return hiddenPrefabs.Contains(prefab.ToLower());
        }
        private void PurgeAutoPins()
        {
            var pins = GetPins().Where(p => p.m_name.StartsWith("AUTO_")).ToList();
            foreach (var pin in pins) Minimap.instance.RemovePin(pin);
            existingClusters.Clear();
            Logger.LogInfo($"Purged {pins.Count} auto pins.");
        }
        private void ExportPins()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"clusters\":[");
            bool first = true;
            foreach (var cluster in existingClusters.Values)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{");
                sb.Append($"\"pos\":{{ \"x\":{cluster.pos.x}, \"y\":{cluster.pos.y}, \"z\":{cluster.pos.z} }},");
                sb.Append($"\"count\":{cluster.count},");
                sb.Append($"\"prefab\":\"{cluster.prefab.Replace("\"", "\\\"")}\",");
                sb.Append($"\"cat\":{(int)cluster.cat}");
                sb.Append("}");
            }
            sb.Append("]}");
            string json = sb.ToString();
            string worldName = ZNet.instance.GetWorldName();
            string pluginDir = Path.Combine(Paths.PluginPath, "DynamicAutoPinner");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, $"{worldName}_pins.json"), json);
            Logger.LogInfo("Pins exported!");
        }
        private void RegisterRPCs()
        {
            ZRpc.Register(RPC_ADD_PIN, new ZRpc.RpcMethod(RPC_AddPin));
        }
        private static void RPC_AddPin(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                Vector3 pos = pkg.ReadVector3();
                int icon = pkg.ReadInt();
                string label = pkg.ReadString();
                if (icon < 0 || icon > 23 || label.Length > 100) return;
                Minimap.instance.AddPin(pos, (Minimap.PinType)icon, label, false, false);
            }
            catch (Exception ex)
            {
                BepInEx.Logging.Logger.CreateLogSource("DynamicAutoPinner").LogError($"RPC_AddPin failed: {ex}");
            }
        }
        private void ParsePlayerPins()
        {
            noPinZones.Clear();
            noPinRadii.Clear();
            var playerPins = GetPins().Where(p => GetOwnerID(p) == Player.m_localPlayer.GetPlayerID());
            foreach (var pin in playerPins.ToList())
            {
                var match = Regex.Match(pin.m_name, @"pinthis\s+(.+?)\s+(\d+\.?\d*)");
                if (match.Success && float.TryParse(match.Groups[2].Value, out float radius))
                {
                    string label = match.Groups[1].Value;
                    noPinZones[pin.m_name] = pin.m_pos;
                    noPinRadii[pin.m_name] = radius;
                    Minimap.instance.RemovePin(pin);
                    Minimap.instance.AddPin(pin.m_pos, pin.m_type, label, pin.m_save, false, GetOwnerID(pin));
                }
            }
        }
        private bool InNoPinZone(Vector3 pos)
        {
            foreach (var zone in noPinRadii)
            {
                if (Vector3.Distance(pos, noPinZones[zone.Key]) < zone.Value) return true;
            }
            return false;
        }
        private IEnumerator SendPinRPC(Minimap.PinData pin)
        {
            yield return new WaitForSeconds(SyncThrottle.Value);
            ZPackage pkg = new ZPackage();
            pkg.Write(pin.m_pos);
            pkg.Write((int)pin.m_type);
            pkg.Write(pin.m_name);
            var peersField = AccessTools.Field(typeof(ZNet), nameof(ZNet.m_peers));
            var peers = peersField.GetValue(ZNet.instance) as IList;
            if (peers == null) yield break;
            foreach (var peer in peers)
            {
                var rpcField = AccessTools.Field(peer.GetType(), nameof(ZRpc.m_rpc));
                var rpc = rpcField.GetValue(peer) as ZRpc;
                if (rpc == null) continue;
                rpc.Invoke(RPC_ADD_PIN, pkg);
            }
        }
        private void RegenNearbyPins()
        {
            StartCoroutine(ScanCoroutine(300f));
            Logger.LogInfo("Regenerating nearby auto-pins...");
        }
        public static void AddVehiclePin(Vector3 pos, string label)
        {
            if (Minimap.instance == null) return;
            Minimap.instance.AddPin(pos, Minimap.PinType.Icon3, label, false, false);
            Game.instance.StartCoroutine(ExpirePin(pos, VehiclePinExpireTime.Value));
        }
        private static IEnumerator ExpirePin(Vector3 pos, float time)
        {
            yield return new WaitForSeconds(time);
            Collider[] hits = Physics.OverlapSphere(pos, 5f);
            bool vehicleStillThere = hits.Any(c => c.GetComponent<Ship>() != null || c.GetComponent<Vagon>() != null);
            if (vehicleStillThere) yield break;
            var pin = GetClosestPin(pos, 5f);
            if (pin != null && pin.m_name.StartsWith("Last ")) Minimap.instance.RemovePin(pin);
        }
        private static List<Minimap.PinData> GetPins()
        {
            var field = AccessTools.Field(typeof(Minimap), nameof(Minimap.m_pins));
            var value = field.GetValue(Minimap.instance);
            if (value == null) return new List<Minimap.PinData>();
            return value as List<Minimap.PinData>;
        }
        private static Minimap.PinData GetClosestPin(Vector3 pos, float dist)
        {
            var method = AccessTools.Method(typeof(Minimap), nameof(Minimap.GetClosestPin), new Type[] { typeof(Vector3), typeof(float) });
            return method.Invoke(Minimap.instance, new object[] { pos, dist }) as Minimap.PinData;
        }
        private static long GetOwnerID(Minimap.PinData pin)
        {
            return (long)AccessTools.Field(typeof(Minimap.PinData), nameof(Minimap.PinData.m_ownerID)).GetValue(pin);
        }
    }
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
    public static class ZNetViewAwakePatch
    {
        public static void Postfix(ZNetView __instance)
        {
            if (__instance.GetComponent<Collider>() != null)
                __instance.StartCoroutine(DelayedQueueScan(__instance.gameObject));
        }
        private static IEnumerator DelayedQueueScan(GameObject go)
        {
            yield return new WaitForSeconds(2f);
        }
    }
    [HarmonyPatch(typeof(Ship), nameof(Ship.OnUnmount))]
    static class ShipUnmountPatch
    {
        static void Postfix(Ship __instance)
        {
            DynamicAutoPinner.AddVehiclePin(__instance.transform.position, "Last Boat");
        }
    }
    [HarmonyPatch(typeof(Vagon), nameof(Vagon.OnUnmount))]
    static class CartUnmountPatch
    {
        static void Postfix(Vagon __instance)
        {
            DynamicAutoPinner.AddVehiclePin(__instance.transform.position, "Last Cart");
        }
    }
    [HarmonyPatch(typeof(Ship), nameof(Ship.OnDestroy))]
    static class ShipDestroyedPatch
    {
        static void Postfix(Ship __instance)
        {
            DynamicAutoPinner.AddVehiclePin(__instance.transform.position, "Last Boat");
        }
    }
    [HarmonyPatch(typeof(Vagon), nameof(Vagon.OnDestroy))]
    static class CartDestroyedPatch
    {
        static void Postfix(Vagon __instance)
        {
            DynamicAutoPinner.AddVehiclePin(__instance.transform.position, "Last Cart");
        }
    }
}
// TOTAL LINES: 390 (current count)