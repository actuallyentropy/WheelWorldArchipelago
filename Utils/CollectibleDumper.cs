using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WheelWorldArchipelago.Utils;

/// <summary>
/// Enumerates all collectible objects across every loaded scene and writes their
/// data to a JSON file alongside the BepInEx root.
///
/// Covered types:
///   DiscoverableJump   – MonoBehaviour, SaveData.discoveredJumps
///   GhostAltar         – MonoBehaviour, SaveData.altarsActivated
///   FlaggedCyclist     – MonoBehaviour (race-challenge NPC), SaveData.flaggedCyclists
///   CouponEnvelope     – MonoBehaviour, SaveData.openedEnvelopeIds
///   BikePartPickup     – MonoBehaviour (world box), SaveData.openedPartBoxIds
///   StartChallengeNode – ScriptableObject (FlowChartNode), SaveData.completedChallengeIds
///   Shop / ShopPart    – MonoBehaviour, shops and their part inventories
///   AllBikeParts       – static catalog from BikePart.AllParts (all parts in the game)
///
/// Hook: Plugin.cs patches SceneManager.Internal_SceneLoaded (Harmony postfix).
/// </summary>
public static class CollectibleDumper
{
    // ── Typed records ─────────────────────────────────────────────────────────

    private record JumpData(string Id, string Scene, string GameObject);

    private record AltarData(string Id, string Scene, string GameObject);

    private record CyclistData(string Id, string Scene, string GameObject, string RaceNameKey);

    private record EnvelopeData(string Id, string Scene, string GameObject, int NumCoupons);

    private record PickupData(string Id, string Scene, string GameObject,
                              string PartMode, string PartName, string DropTag);

    private record ChallengeData(string Id, string Scene, string ChallengeName, string ChallengeDesc,
                                 string ActivityId, string RewardSize, bool RewardRandom,
                                 List<string> RewardParts);

    private record BikePartRecord(string PartName, string Category, string LibraryId, string Description);

    private record ShopPartData(string Id, string PartName, string Category, int Price,
                                int RestockCycle, bool Bought);

    private record ShopData(string Scene, string GameObject, bool Restocks,
                            int RestockCycles, List<ShopPartData> Parts);

    // ── Accumulated data ──────────────────────────────────────────────────────

    private static readonly List<JumpData>      _jumps      = new();
    private static readonly List<AltarData>     _altars     = new();
    private static readonly List<CyclistData>   _cyclists   = new();
    private static readonly List<EnvelopeData>  _envelopes  = new();
    private static readonly List<PickupData>    _pickups    = new();
    private static readonly List<ChallengeData> _challenges = new();
    private static readonly List<ShopData>      _shops      = new();

    private static readonly HashSet<string> _seenScenes = new();
    private static readonly HashSet<string> _seenIds    = new();

    public static string OutputPath { get; private set; }

    // ── Public API ────────────────────────────────────────────────────────────

    public static void Initialize()
    {
        OutputPath = Path.Combine(Paths.BepInExRootPath, "collectible_dump.json");
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] Initialized. Output: {OutputPath}");
    }

    /// <summary>Called from the Harmony postfix on SceneManager.Internal_SceneLoaded.</summary>
    public static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        var sceneName = scene.name;
        if (!_seenScenes.Add(sceneName)) return;

        int count = 0;

        // For MonoBehaviour types, ScanMonoBehaviours unions:
        //   • FindObjectsOfType(includeInactive:true) – active + disabled scene objects
        //   • Resources.FindObjectsOfTypeAll           – additive/streamed zones in memory

        // ── DiscoverableJumps ─────────────────────────────────────────────────
        int n = 0;
        foreach (var obj in ScanMonoBehaviours<DiscoverableJump>())
        {
            try
            {
                var id = obj.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;
                var label = obj.gameObject.name;
                Plugin.BepinLogger.LogInfo($"[CollectibleDumper]   DiscoverableJump  {idStr}  # {label}");
                _jumps.Add(new JumpData(idStr, sceneName, label));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("DiscoverableJump", obj.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] DiscoverableJump  → {n} found in '{sceneName}'");

        // ── GhostAltars ───────────────────────────────────────────────────────
        n = 0;
        foreach (var obj in ScanMonoBehaviours<GhostAltar>())
        {
            try
            {
                var id = obj.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;
                var label = obj.gameObject.name;
                Plugin.BepinLogger.LogInfo($"[CollectibleDumper]   GhostAltar        {idStr}  # {label}");
                _altars.Add(new AltarData(idStr, sceneName, label));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("GhostAltar", obj.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] GhostAltar        → {n} found in '{sceneName}'");

        // ── FlaggedCyclists ───────────────────────────────────────────────────
        // raceNameKey is an L10N localisation key struct; .ToString() gives the key string.
        n = 0;
        foreach (var obj in ScanMonoBehaviours<FlaggedCyclist>())
        {
            try
            {
                var id = obj.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;
                var label   = obj.gameObject.name;
                var raceKey = SafeStr(() => obj.raceNameKey.ToString());
                Plugin.BepinLogger.LogInfo($"[CollectibleDumper]   FlaggedCyclist    {idStr}  # {label}  [{raceKey}]");
                _cyclists.Add(new CyclistData(idStr, sceneName, label, raceKey));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("FlaggedCyclist", obj.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] FlaggedCyclist    → {n} found in '{sceneName}'");

        // ── CouponEnvelopes ───────────────────────────────────────────────────
        n = 0;
        foreach (var obj in ScanMonoBehaviours<CouponEnvelope>())
        {
            try
            {
                var id = obj.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;
                var label = obj.gameObject.name;
                Plugin.BepinLogger.LogInfo($"[CollectibleDumper]   CouponEnvelope    {idStr}  # {label}  (x{obj.numCoupons})");
                _envelopes.Add(new EnvelopeData(idStr, sceneName, label, obj.numCoupons));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("CouponEnvelope", obj.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] CouponEnvelope    → {n} found in '{sceneName}'");

        // ── BikePartPickups ───────────────────────────────────────────────────
        // specificPart.partName is the string key used in SaveData.collectedBikePartNames.
        // dropTag identifies the random-drop pool when partMode == RandomPart.
        n = 0;
        foreach (var obj in ScanMonoBehaviours<BikePartPickup>())
        {
            try
            {
                var id = obj.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;
                var label    = obj.gameObject.name;
                var partMode = obj.part.ToString();
                var partName = obj.specificPart != null ? (obj.specificPart.partName ?? "") : "";
                var dropTag  = obj.dropTag ?? "";
                var detail   = !string.IsNullOrEmpty(partName) ? partName : dropTag;
                Plugin.BepinLogger.LogInfo($"[CollectibleDumper]   BikePartPickup    {idStr}  # {label}  [{partMode}: {detail}]");
                _pickups.Add(new PickupData(idStr, sceneName, label, partMode, partName, dropTag));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("BikePartPickup", obj.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] BikePartPickup    → {n} found in '{sceneName}'");

        // ── StartChallengeNodes ───────────────────────────────────────────────
        // ScriptableObject – Resources.FindObjectsOfTypeAll is not scene-scoped;
        // _seenIds prevents duplicates across scene loads.
        n = 0;
        foreach (var node in Resources.FindObjectsOfTypeAll<StartChallengeNode>())
        {
            try
            {
                var id = node.id; if (!id.IsSet()) continue;
                var idStr = id.ToString(); if (!_seenIds.Add(idStr)) continue;

                var name         = node.challengeName ?? node.name ?? "";
                var desc         = node.challengeDesc ?? "";
                var activityId   = node.activityID ?? "";
                var rewardSize   = node.rewardSize.ToString();
                var rewardRandom = node.rewardRandomBikePart;
                var rewardParts  = new List<string>();

                if (node.rewardBikeParts != null)
                    foreach (var part in node.rewardBikeParts)
                        if (part?.partName is { Length: > 0 } pn)
                            rewardParts.Add(pn);

                var rewardSummary = rewardRandom ? "random" : string.Join(", ", rewardParts);
                Plugin.BepinLogger.LogInfo(
                    $"[CollectibleDumper]   StartChallengeNode {idStr}  # {name}  [{rewardSize}: {rewardSummary}]");
                _challenges.Add(new ChallengeData(idStr, sceneName, name, desc, activityId,
                                                   rewardSize, rewardRandom, rewardParts));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("StartChallengeNode", node?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] StartChallengeNode → {n} new in '{sceneName}'");

        // ── Shops ────────────────────────────────────────────────────────────
        // _partsByRestock / _items are populated in Shop.Start(), which hasn't
        // run yet at scene-load time.  Instead we walk the serialized
        // restockHolders GameObjects (one per restock cycle) and discover
        // ShopPart children via GetComponentsInChildren.  This works on
        // inactive objects and doesn't depend on Start().
        // Cross-reference SaveData.itemsBought to flag already-purchased parts.
        n = 0;
        var boughtIds = new HashSet<string>();
        try
        {
            var saveData = SaveDataManager.Instance?.Data;
            if (saveData?.itemsBought != null)
                foreach (var gid in saveData.itemsBought)
                    if (gid.IsSet()) boughtIds.Add(gid.ToString());
        }
        catch (Exception ex)
        {
            Plugin.BepinLogger.LogWarning($"[CollectibleDumper] Could not read itemsBought: {ex.Message}");
        }

        var seenShopIds = new HashSet<int>();
        foreach (var shop in ScanMonoBehaviours<Shop>())
        {
            try
            {
                if (!seenShopIds.Add(shop.GetInstanceID())) continue;
                var shopLabel = shop.gameObject.name;
                var parts = new List<ShopPartData>();
                int restockCycles = 0;

                // restockHolders is serialized – each GameObject groups one
                // cycle's ShopParts as children.
                var holders = shop.restockHolders;
                if (holders != null && holders.Count > 0)
                {
                    restockCycles = holders.Count;
                    for (int cycle = 0; cycle < holders.Count; cycle++)
                    {
                        var holder = holders[cycle];
                        if (holder == null) continue;
                        foreach (var sp in holder.GetComponentsInChildren<ShopPart>(true))
                        {
                            if (sp == null) continue;
                            CollectShopPart(sp, cycle, boughtIds, parts);
                        }
                    }
                }

                // Fallback: no restockHolders – scan the Shop GameObject itself.
                if (parts.Count == 0)
                {
                    foreach (var sp in shop.GetComponentsInChildren<ShopPart>(true))
                    {
                        if (sp == null) continue;
                        CollectShopPart(sp, -1, boughtIds, parts);
                    }
                }

                Plugin.BepinLogger.LogInfo(
                    $"[CollectibleDumper]   Shop              # {shopLabel}  [{parts.Count} parts, restocks={shop.restocks}, cycles={restockCycles}]");
                foreach (var p in parts)
                    Plugin.BepinLogger.LogInfo(
                        $"[CollectibleDumper]     ShopPart  {p.Id}  {p.PartName}  ({p.Category})  ${p.Price}  cycle={p.RestockCycle}  bought={p.Bought}");

                _shops.Add(new ShopData(sceneName, shopLabel, shop.restocks, restockCycles, parts));
                n++; count++;
            }
            catch (Exception ex) { LogWarn("Shop", shop.gameObject?.name, ex); }
        }
        Plugin.BepinLogger.LogInfo($"[CollectibleDumper] Shop              → {n} found in '{sceneName}'");

        int total = _jumps.Count + _altars.Count + _cyclists.Count
                  + _envelopes.Count + _pickups.Count + _challenges.Count + _shops.Count;
        Plugin.BepinLogger.LogInfo(
            $"[CollectibleDumper] '{sceneName}': {count} new, {total} total entries.");
        if (count > 0) WriteDump();
    }

    // ── File output ───────────────────────────────────────────────────────────

    private static void WriteDump()
    {
        try
        {
            // Read AllBikeParts fresh each write – asset loading may have grown the list.
            var allParts = new List<BikePartRecord>();
            try
            {
                var parts = BikePart.AllParts;
                if (parts != null)
                    foreach (var part in parts)
                        if (part != null)
                            allParts.Add(new BikePartRecord(
                                part.partName        ?? "",
                                part.partCategory.ToString(),
                                part.libraryID       ?? "",
                                part.partDescription ?? ""));
            }
            catch (Exception ex)
            {
                Plugin.BepinLogger.LogWarning($"[CollectibleDumper] BikePart.AllParts unavailable: {ex.Message}");
            }

            var root = new JsonObject
            {
                ["generated"]        = DateTime.Now.ToString("o"),
                ["DiscoverableJump"] = Arr(_jumps,      d => Obj(
                    ("id", d.Id), ("scene", d.Scene), ("gameObjectName", d.GameObject))),
                ["GhostAltar"]       = Arr(_altars,     d => Obj(
                    ("id", d.Id), ("scene", d.Scene), ("gameObjectName", d.GameObject))),
                ["FlaggedCyclist"]   = Arr(_cyclists,   d => Obj(
                    ("id", d.Id), ("scene", d.Scene), ("gameObjectName", d.GameObject),
                    ("raceNameKey", d.RaceNameKey))),
                ["CouponEnvelope"]   = Arr(_envelopes,  d =>
                {
                    var o = Obj(("id", d.Id), ("scene", d.Scene), ("gameObjectName", d.GameObject));
                    o["numCoupons"] = d.NumCoupons;
                    return o;
                }),
                ["BikePartPickup"]   = Arr(_pickups,    d => Obj(
                    ("id", d.Id), ("scene", d.Scene), ("gameObjectName", d.GameObject),
                    ("partMode", d.PartMode), ("partName", d.PartName), ("dropTag", d.DropTag))),
                ["StartChallengeNode"] = Arr(_challenges, d =>
                {
                    var o = Obj(
                        ("id", d.Id), ("scene", d.Scene),
                        ("challengeName", d.ChallengeName), ("challengeDesc", d.ChallengeDesc),
                        ("activityId", d.ActivityId), ("rewardSize", d.RewardSize));
                    o["rewardRandomBikePart"] = d.RewardRandom;
                    var parts = new JsonArray();
                    foreach (var p in d.RewardParts) parts.Add(p);
                    o["rewardBikeParts"] = parts;
                    return o;
                }),
                ["Shop"]             = Arr(_shops, d =>
                {
                    var o = Obj(("scene", d.Scene), ("gameObjectName", d.GameObject));
                    o["restocks"] = d.Restocks;
                    o["restockCycles"] = d.RestockCycles;
                    var partsArr = new JsonArray();
                    foreach (var p in d.Parts)
                    {
                        var po = Obj(("id", p.Id), ("partName", p.PartName), ("category", p.Category));
                        po["price"] = p.Price;
                        po["restockCycle"] = p.RestockCycle;
                        po["bought"] = p.Bought;
                        partsArr.Add(po);
                    }
                    o["parts"] = partsArr;
                    return o;
                }),
                ["AllBikeParts"]     = Arr(allParts,    d => Obj(
                    ("partName", d.PartName), ("category", d.Category),
                    ("libraryId", d.LibraryId), ("description", d.Description))),
            };

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(OutputPath, json, Encoding.UTF8);
            Plugin.BepinLogger.LogInfo($"[CollectibleDumper] Wrote {OutputPath}");
        }
        catch (Exception ex)
        {
            Plugin.BepinLogger.LogError($"[CollectibleDumper] Failed to write dump: {ex}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Union of FindObjectsOfType(includeInactive) and Resources.FindObjectsOfTypeAll,
    /// deduped by Unity instance ID.
    private static IEnumerable<T> ScanMonoBehaviours<T>() where T : UnityEngine.Object
    {
        var seen = new HashSet<int>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType<T>(true))
            if (o != null && seen.Add(o.GetInstanceID())) yield return o;
        foreach (var o in Resources.FindObjectsOfTypeAll<T>())
            if (o != null && seen.Add(o.GetInstanceID())) yield return o;
    }

    private static void CollectShopPart(ShopPart sp, int cycle,
                                        HashSet<string> boughtIds, List<ShopPartData> parts)
    {
        var spId = sp.id;
        var idStr = spId.IsSet() ? spId.ToString() : "";
        var partName = sp.part != null ? (sp.part.partName ?? "") : "";
        var category = sp.part != null ? sp.part.partCategory.ToString() : "";
        var price = sp.GetPrice();
        var bought = idStr.Length > 0 && boughtIds.Contains(idStr);
        parts.Add(new ShopPartData(idStr, partName, category, price, cycle, bought));
    }

    private static JsonArray Arr<T>(List<T> list, Func<T, JsonObject> convert)
    {
        var arr = new JsonArray();
        foreach (var item in list) arr.Add(convert(item));
        return arr;
    }

    private static JsonObject Obj(params (string key, string value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    private static string SafeStr(Func<string> fn)
    {
        try { return fn() ?? ""; }
        catch { return ""; }
    }

    private static void LogWarn(string type, string name, Exception ex) =>
        Plugin.BepinLogger.LogWarning(
            $"[CollectibleDumper] Error reading {type} '{name}': {ex.Message}");
}
