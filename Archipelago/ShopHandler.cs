using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using UnityEngine;

namespace WheelWorldArchipelago.Archipelago;

/// <summary>
/// Manages shop integration with Archipelago: scouts shop locations to learn what
/// item is at each slot, then overwrites ShopPart displays accordingly.
///
/// For own-world items the real BikePart is swapped in.
/// For foreign-player items a synthetic BikePart is created using a model part
/// (Handy Wheel for progression, Ears Bars for everything else) with a price of 1.
/// </summary>
public static class ShopHandler
{
    private record ScoutedShopSlot(
        long LocationId,
        string ItemName,
        int PlayerId,
        string PlayerName,
        bool IsLocalPlayer,
        ItemFlags Flags);

    /// <summary>Keyed by ShopPart GlobalID UUID string.</summary>
    private static readonly Dictionary<string, ScoutedShopSlot> _scoutedSlots = new();

    /// <summary>Whether scouting has completed and slots are populated.</summary>
    public static bool ScoutingComplete { get; private set; }

    /// <summary>
    /// Set to true when scouting completes on a background thread.
    /// The main-thread ItemHandler.Update checks this flag and calls
    /// OverwriteAllLoadedShops() on the Unity main thread.
    /// </summary>
    public static bool PendingOverwrite { get; set; }

    private const string ProgressionModelPart = "Handy Wheel";
    private const string DefaultModelPart = "Ears Bars";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scouts all shop locations via the AP server to learn what item is at each slot.
    /// Called from HandleConnectResult after successful login.
    /// Runs the async scout on a background thread, then calls OverwriteAllLoadedShops
    /// once results arrive.
    /// </summary>
    public static void ScoutShopLocations(ArchipelagoSession session)
    {
        ScoutingComplete = false;
        _scoutedSlots.Clear();

        // Use the pre-built set of shop-only AP location IDs.
        var shopLocationIds = Plugin.ShopLocationIds.ToList();

        if (shopLocationIds.Count == 0)
        {
            Plugin.BepinLogger.LogInfo("[ShopHandler] No shop locations found in LocationMap");
            ScoutingComplete = true;
            return;
        }

        Plugin.BepinLogger.LogInfo($"[ShopHandler] Scouting {shopLocationIds.Count} shop locations...");

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var task = session.Locations.ScoutLocationsAsync(shopLocationIds.ToArray());
                task.Wait();
                var results = task.Result;

                foreach (var (locId, info) in results)
                {
                    if (!Plugin.ReverseLocationMap.TryGetValue(locId, out var uuid))
                        continue;

                    var slot = new ScoutedShopSlot(
                        LocationId: locId,
                        ItemName: info.ItemName ?? $"Item #{info.ItemId}",
                        PlayerId: info.Player.Slot,
                        PlayerName: info.Player.Name ?? $"Player {info.Player.Slot}",
                        IsLocalPlayer: info.Player.Slot == session.ConnectionInfo.Slot,
                        Flags: info.Flags);

                    _scoutedSlots[uuid] = slot;

                    Plugin.BepinLogger.LogInfo(
                        $"[ShopHandler]   Scouted {uuid}: '{slot.ItemName}' for {slot.PlayerName} " +
                        $"(local={slot.IsLocalPlayer}, flags={slot.Flags})");
                }

                ScoutingComplete = true;
                Plugin.BepinLogger.LogInfo(
                    $"[ShopHandler] Scouting complete. {_scoutedSlots.Count} shop slots mapped.");

                // Signal the main thread to overwrite shops.
                // Unity APIs like FindObjectsOfType cannot be called from background threads.
                PendingOverwrite = true;
            }
            catch (Exception e)
            {
                Plugin.BepinLogger.LogError($"[ShopHandler] Scouting failed: {e}");
                ScoutingComplete = true; // Mark complete so we don't block forever
            }
        });
    }

    /// <summary>
    /// Finds all ShopPart instances currently in memory and overwrites them.
    /// Called after scouting completes and on scene loads.
    /// </summary>
    public static void OverwriteAllLoadedShops()
    {
        Plugin.BepinLogger.LogInfo(
            $"[ShopHandler] OverwriteAllLoadedShops called. ScoutingComplete={ScoutingComplete}, slots={_scoutedSlots.Count}");

        if (!ScoutingComplete || _scoutedSlots.Count == 0) return;

        int count = 0;
        var seen = new HashSet<int>();

        var found1 = UnityEngine.Object.FindObjectsOfType<ShopPart>(true);
        Plugin.BepinLogger.LogInfo($"[ShopHandler] FindObjectsOfType<ShopPart> returned {found1?.Length ?? -1}");

        foreach (var sp in found1)
        {
            if (sp != null && seen.Add(sp.GetInstanceID()))
            {
                if (OverwriteShopPart(sp)) count++;
            }
        }

        var found2 = Resources.FindObjectsOfTypeAll<ShopPart>();
        Plugin.BepinLogger.LogInfo($"[ShopHandler] Resources.FindObjectsOfTypeAll<ShopPart> returned {found2?.Length ?? -1}");

        foreach (var sp in found2)
        {
            if (sp != null && seen.Add(sp.GetInstanceID()))
            {
                if (OverwriteShopPart(sp)) count++;
            }
        }

        Plugin.BepinLogger.LogInfo($"[ShopHandler] Overwrote {count} shop parts (seen {seen.Count} total)");
    }

    /// <summary>
    /// Overwrites all ShopParts belonging to a specific Shop instance.
    /// Called from the Shop.Start() Harmony postfix.
    /// </summary>
    public static void OverwriteShopParts(Shop shop)
    {
        Plugin.BepinLogger.LogInfo(
            $"[ShopHandler] OverwriteShopParts called for shop '{shop?.gameObject?.name}'. " +
            $"ScoutingComplete={ScoutingComplete}, slots={_scoutedSlots.Count}");

        if (!ScoutingComplete || _scoutedSlots.Count == 0) return;

        int count = 0;
        var items = shop._items;
        if (items != null)
        {
            foreach (var sp in items)
            {
                if (sp != null && OverwriteShopPart(sp)) count++;
            }
        }

        if (count > 0)
            Plugin.BepinLogger.LogInfo(
                $"[ShopHandler] Overwrote {count} parts in shop '{shop.gameObject.name}'");
    }

    /// <summary>Clears scouted data on disconnect.</summary>
    public static void Clear()
    {
        _scoutedSlots.Clear();
        ScoutingComplete = false;
        Plugin.BepinLogger.LogInfo("[ShopHandler] Cleared scouted shop data");
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Overwrites a single ShopPart's BikePart reference based on scouted data.
    /// Returns true if the part was overwritten.
    /// </summary>
    private static bool OverwriteShopPart(ShopPart sp)
    {
        try
        {
            var spId = sp.id;
            if (!spId.IsSet())
            {
                Plugin.BepinLogger.LogInfo(
                    $"[ShopHandler]   Skipping '{sp.gameObject?.name}': GlobalID not set");
                return false;
            }
            var uuid = spId.ToString();

            if (!_scoutedSlots.TryGetValue(uuid, out var slot))
            {
                Plugin.BepinLogger.LogInfo(
                    $"[ShopHandler]   UUID '{uuid}' not in scouted slots (part: '{sp.part?.partName}')");
                return false;
            }

            var partsByName = BikePart.PartsByName;
            if (partsByName == null)
            {
                Plugin.BepinLogger.LogWarning("[ShopHandler] BikePart.PartsByName not available yet");
                return false;
            }

            if (slot.IsLocalPlayer)
            {
                // Own-world item: swap to the real BikePart
                if (partsByName.ContainsKey(slot.ItemName))
                {
                    sp.part = partsByName[slot.ItemName];
                    RefreshShopPart(sp);
                    Plugin.BepinLogger.LogInfo(
                        $"[ShopHandler] Set shop slot {uuid} to own part '{slot.ItemName}'");
                    return true;
                }

                Plugin.BepinLogger.LogWarning(
                    $"[ShopHandler] Own-world part '{slot.ItemName}' not found in PartsByName");
                return false;
            }

            // Foreign-player item: create a synthetic BikePart
            var isProgression = (slot.Flags & ItemFlags.Advancement) != 0;
            var modelName = isProgression ? ProgressionModelPart : DefaultModelPart;

            if (!partsByName.ContainsKey(modelName))
            {
                Plugin.BepinLogger.LogWarning(
                    $"[ShopHandler] Model part '{modelName}' not found in PartsByName");
                return false;
            }

            var model = partsByName[modelName];
            var synthetic = CreateSyntheticPart(model, slot);
            if (synthetic == null) return false;

            sp.part = synthetic;
            RefreshShopPart(sp);
            Plugin.BepinLogger.LogInfo(
                $"[ShopHandler] Set shop slot {uuid} to synthetic '{synthetic.partName}' " +
                $"(model={modelName})");
            return true;
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[ShopHandler] Error overwriting shop part: {e}");
            return false;
        }
    }

    /// <summary>
    /// Refreshes a ShopPart's visual display after its <c>part</c> field has been changed.
    /// Calls Init() to re-initialize the prefab/model, then Refresh() to update UI elements.
    /// </summary>
    private static void RefreshShopPart(ShopPart sp)
    {
        try
        {
            sp.Init();
            sp.Refresh();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[ShopHandler] Error refreshing shop part: {e}");
        }
    }

    /// <summary>
    /// Creates a synthetic BikePart ScriptableObject for a foreign-player item.
    /// Copies visual data from the model part and sets a price of 1.
    /// </summary>
    private static BikePart CreateSyntheticPart(BikePart model, ScoutedShopSlot slot)
    {
        try
        {
            var fake = ScriptableObject.CreateInstance<BikePart>();
            fake.partName = $"{slot.PlayerName}'s {slot.ItemName}";
            fake.partDescription = $"An item for {slot.PlayerName}.";
            fake.partCategory = model.partCategory;
            fake.libraryID = model.libraryID;
            fake.prefab = model.prefab;

            // Copy the model's dropInfo and override shopPrice to 1.
            // We clone via the model's existing dropInfo to get a valid IL2CPP object.
            var modelDropInfo = model.dropInfo;
            if (modelDropInfo != null)
            {
                // Create a new DropInfo by copying from model
                var dropInfo = new BikePart.DropInfo();
                dropInfo.shopPrice = 1;
                fake.dropInfo = dropInfo;
            }

            return fake;
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[ShopHandler] Failed to create synthetic BikePart: {e}");
            return null;
        }
    }
}
