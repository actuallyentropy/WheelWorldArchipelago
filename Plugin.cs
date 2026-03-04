using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using WheelWorldArchipelago.Archipelago;
using WheelWorldArchipelago.Utils;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace WheelWorldArchipelago;

[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
[BepInProcess("Wheel World.exe")]
public class Plugin : BasePlugin
{
    public const string PluginGUID = "com.entropy.WheelWorldArchipelago";
    public const string PluginName = "WheelWorldArchipelago";
    public const string PluginVersion = "1.0.0";

    public const string ModDisplayInfo = $"{PluginName} v{PluginVersion}";
    public static ManualLogSource BepinLogger;
    public static ArchipelagoClient ArchipelagoClient;

    /// <summary>
    /// Maps game object UUIDs (GlobalID.ToString()) to their Archipelago location IDs.
    /// Loaded from id_to_location.json at plugin startup.
    /// </summary>
    public static readonly Dictionary<string, List<long>> LocationMap = new();

    /// <summary>
    /// Reverse map: AP location ID → game object UUID.
    /// Built alongside LocationMap for quick lookups during scouting.
    /// </summary>
    public static readonly Dictionary<long, string> ReverseLocationMap = new();

    /// <summary>
    /// AP location IDs that belong to shop slots (location name contains "Shop").
    /// Used by ShopHandler to scout only shop locations.
    /// </summary>
    public static readonly HashSet<long> ShopLocationIds = new();

    /// <summary>
    /// One-shot bypass flag for the PickupBikePart prefix.
    /// Set to true immediately before calling PickupBikePart from AP grant logic.
    /// </summary>
    public static bool AllowPickup;

    public override void Load()
    {
        // Plugin startup logic
        BepinLogger = Log;
        ArchipelagoClient = new ArchipelagoClient();
        ArchipelagoConsole.Awake();
        var harmony = Harmony.CreateAndPatchAll(typeof(Plugin));

        // Manual patch for ChallengeManager.CompleteChallenge(StartChallengeNode, out bool)
        // — attribute-based patching can't express out/ref parameter types.
        var completeChallengeMethod = typeof(ChallengeManager).GetMethod(
            nameof(ChallengeManager.CompleteChallenge),
            new[] { typeof(StartChallengeNode), typeof(bool).MakeByRefType() });
        if (completeChallengeMethod != null)
        {
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(
                nameof(ChallengeManager_CompleteChallenge_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(completeChallengeMethod, postfix: postfix);
            BepinLogger.LogInfo("Patched ChallengeManager.CompleteChallenge");
        }
        else
        {
            BepinLogger.LogError("Could not find ChallengeManager.CompleteChallenge method to patch");
        }

        // Manual patch for Shop.Refresh(bool) — fires after a purchase (justBought=true).
        // ShopPart.DoUse can't be patched because IL2CPP native code calls it directly,
        // bypassing the managed proxy that Harmony patches.
        var shopRefresh = typeof(Shop).GetMethod(
            nameof(Shop.Refresh),
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(bool) }, null);
        if (shopRefresh != null)
        {
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(
                nameof(Shop_Refresh_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(shopRefresh, postfix: postfix);
            BepinLogger.LogInfo("Patched Shop.Refresh");
        }
        else
        {
            BepinLogger.LogError("Could not find Shop.Refresh method to patch");
        }

        // Manual patch for Shop.Start — private Unity message method
        var shopStart = typeof(Shop).GetMethod(
            "Start",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (shopStart != null)
        {
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(
                nameof(Shop_Start_Postfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(shopStart, postfix: postfix);
            BepinLogger.LogInfo("Patched Shop.Start");
        }
        else
        {
            BepinLogger.LogError("Could not find Shop.Start method to patch");
        }

        // Dump collectible GlobalIDs from every scene that loads this session.
        // Output → <BepInEx root>/collectible_dump.txt
        CollectibleDumper.Initialize();

        // Register and create the item grant handler (processes AP items on Unity main thread)
        ItemHandler.Register();
        ItemHandler.Create();

        LoadLocationMap();

        ArchipelagoConsole.LogMessage($"{ModDisplayInfo} loaded!");
    }

    private void LoadLocationMap()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            var jsonPath = Path.Combine(dir!, "id_to_location.json");
            var json = File.ReadAllText(jsonPath);
            var obj = JObject.Parse(json);

            foreach (var (uuid, token) in obj)
            {
                var ids = new List<long>();
                foreach (var entry in (JArray)token!)
                {
                    var apId = (long)entry["id"]!;
                    var name = (string)entry["name"];
                    ids.Add(apId);
                    ReverseLocationMap[apId] = uuid;
                    if (name != null && name.Contains("shop", StringComparison.OrdinalIgnoreCase))
                        ShopLocationIds.Add(apId);
                }
                LocationMap[uuid] = ids;
            }

            BepinLogger.LogInfo(
                $"Loaded {LocationMap.Count} location mappings ({ReverseLocationMap.Count} reverse, " +
                $"{ShopLocationIds.Count} shop) from id_to_location.json");
        }
        catch (Exception e)
        {
            BepinLogger.LogError($"Failed to load id_to_location.json: {e}");
        }
    }

    // ── Collectible ID dump ──────────────────────────────────────────────────
    // SceneManager.sceneLoaded uses UnityAction<Scene,LoadSceneMode> whose IL2CPP
    // delegate marshalling is non-trivial. Patching the private Internal_SceneLoaded
    // callback is simpler and guaranteed to fire for every scene load.
    [HarmonyPatch(typeof(SceneManager), "Internal_SceneLoaded")]
    [HarmonyPostfix]
    static void SceneManager_Internal_SceneLoaded_Postfix(Scene scene, LoadSceneMode mode)
    {
        CollectibleDumper.OnSceneLoaded(scene, mode);
    }

    [HarmonyPatch(typeof(BikePartPickup), "PickupBikePart")]
    [HarmonyPrefix]
    static bool BikePartPickup_PickupBikePart_Prefix()
    {
        if (AllowPickup)
        {
            AllowPickup = false;
            return true; // one-shot bypass for AP item grants
        }

        return false; // skip PickupBikePart — the part is granted by the multiworld instead
    }

    [HarmonyPatch(typeof(BikePartPickup), "DoUse")]
    [HarmonyPostfix]
    static void BikePartPickup_DoUse_Postfix(BikePartPickup __instance)
    {
        if (!ArchipelagoClient.Authenticated) return;

        var uuid = __instance.id.ToString();
        BepinLogger.LogInfo($"Box opened: {uuid}");

        // Look up which AP location IDs this box maps to
        List<long> newLocationIds = null;
        if (LocationMap.TryGetValue(uuid, out var ids))
            newLocationIds = ids;
        else
            BepinLogger.LogWarning($"Box UUID {uuid} not found in id_to_location.json");

        // Send location checks, ensuring the newly opened box's IDs are included
        // even if OpenInSave hasn't committed to SaveDataManager yet
        ArchipelagoClient.CheckLocations(newLocationIds);
    }

    static void ChallengeManager_CompleteChallenge_Postfix(StartChallengeNode node, bool firstTimeCompleting)
    {
        if (!ArchipelagoClient.Authenticated) return;

        var uuid = node.id.ToString();
        BepinLogger.LogInfo($"Challenge completed: {uuid} (firstTime={firstTimeCompleting})");

        // Look up which AP location IDs this challenge maps to
        List<long> newLocationIds = null;
        if (LocationMap.TryGetValue(uuid, out var ids))
            newLocationIds = ids;
        else
            BepinLogger.LogWarning($"Challenge UUID {uuid} not found in id_to_location.json");

        // Send location checks, ensuring the newly completed challenge's IDs are
        // included even if completedChallengeIds hasn't committed yet
        ArchipelagoClient.CheckLocations(newLocationIds);
    }

    // ── Shop hooks ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fires after Shop.Refresh(bool justBought). When justBought is true, a purchase
    /// just completed — scan itemsBought to send location checks.
    /// ShopPart.DoUse can't be Harmony-patched because IL2CPP native code calls it
    /// directly, bypassing the managed proxy.
    /// </summary>
    static void Shop_Refresh_Postfix(Shop __instance, bool justBought)
    {
        if (!justBought) return;
        if (!ArchipelagoClient.Authenticated) return;

        BepinLogger.LogInfo($"[Shop.Refresh postfix] Purchase detected in '{__instance?.gameObject?.name}'");

        // CheckLocations scans SaveData.itemsBought (among others) and sends all
        // checked locations to the server. The newly purchased item should already
        // be in itemsBought by the time Refresh fires.
        ArchipelagoClient.CheckLocations();
    }

    static void Shop_Start_Postfix(Shop __instance)
    {
        BepinLogger.LogInfo($"[Shop.Start postfix] Fired for '{__instance?.gameObject?.name}'");
        ShopHandler.OverwriteShopParts(__instance);
    }

    [HarmonyPatch(typeof (StartMenuInGame), "DoCredits")]
    [HarmonyPrefix]
    static void StartMenuInGame_DoCredits_Prefix()
    {
        ArchipelagoConsole.LogMessage("Caught credit roll method call!");
    }
}