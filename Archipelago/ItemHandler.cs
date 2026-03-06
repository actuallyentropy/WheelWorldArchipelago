using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using WheelWorldArchipelago.Utils;

namespace WheelWorldArchipelago.Archipelago;

/// <summary>
/// Injected MonoBehaviour that processes the AP item receive queue on the Unity main thread.
/// Items are granted one at a time with the pickup dialogue. The next item is only granted
/// once the player returns to an #exploring presence state and the pickup cinematic is
/// no longer showing.
///
/// On connect, AP re-sends every item ever received. Items already in the player's
/// inventory are detected via IsInPlayerInventory() and silently skipped.
/// </summary>
public class ItemHandler : MonoBehaviour
{
    /// <summary>
    /// Queued items from AP's OnItemReceived (called on a background thread).
    /// </summary>
    private static readonly ConcurrentQueue<QueuedItem> PendingItems = new();

    public ItemHandler(IntPtr ptr) : base(ptr) { }

    public static void Register()
    {
        ClassInjector.RegisterTypeInIl2Cpp<ItemHandler>();
    }

    public static void Create()
    {
        var go = new GameObject("WheelWorldArchipelago_ItemHandler");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<ItemHandler>();
    }

    /// <summary>
    /// Enqueue an item to be granted on the main thread when the player is in game.
    /// Safe to call from any thread.
    /// </summary>
    public static void Enqueue(string itemName, long itemId)
    {
        PendingItems.Enqueue(new QueuedItem(itemName, itemId));
        Plugin.BepinLogger.LogInfo($"[ItemHandler] Queued item: {itemName} (AP ID {itemId})");
    }

    /// <summary>
    /// Check whether the player is in an #exploring state — the only safe state to
    /// trigger a pickup dialogue. This ensures we don't interrupt races, menus,
    /// cutscenes, or an already-open pickup dialogue.
    /// </summary>
    private static bool IsPlayerExploring()
    {
        try
        {
            var presence = PresenceManager.Instance?.Current;
            if (presence == null) return false;
            var key = presence.presenceKey;
            return key != null && key.StartsWith("exploring");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check whether the bike part pickup cinematic is currently showing.
    /// Used to avoid granting another item while the player is viewing one.
    /// </summary>
    private static bool IsPickupCinematicShowing()
    {
        try
        {
            var cinematic = BikePartPickupCinematic.Instance;
            return cinematic != null && cinematic.IsShowing();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the current presence key for diagnostic logging.
    /// </summary>
    private static string GetPresenceKey()
    {
        try
        {
            var presence = PresenceManager.Instance?.Current;
            return presence?.presenceKey ?? "(null)";
        }
        catch
        {
            return "(error)";
        }
    }

    private void Update()
    {
        // Check if shop scouting completed on a background thread and needs
        // main-thread Unity API calls to overwrite shop displays.
        if (ShopHandler.PendingOverwrite)
        {
            ShopHandler.PendingOverwrite = false;
            Plugin.BepinLogger.LogInfo("[ItemHandler] Processing pending shop overwrite on main thread");
            ShopHandler.OverwriteAllLoadedShops();
        }

        if (PendingItems.IsEmpty) return;

        // Log state every poll so we can diagnose why items aren't processing
        var presenceKey = GetPresenceKey();
        var exploring = IsPlayerExploring();
        var cinematicShowing = IsPickupCinematicShowing();

        Plugin.BepinLogger.LogInfo(
            $"[ItemHandler] Poll: {PendingItems.Count} pending, " +
            $"presence='{presenceKey}', exploring={exploring}, " +
            $"cinematicShowing={cinematicShowing}");

        if (!exploring)
        {
            Plugin.BepinLogger.LogInfo("[ItemHandler] Not in #exploring state, waiting...");
            return;
        }

        if (cinematicShowing)
        {
            Plugin.BepinLogger.LogInfo("[ItemHandler] Pickup cinematic still showing, waiting...");
            return;
        }

        if (!PendingItems.TryDequeue(out var item)) return;

        ProcessItem(item);
    }

    private void ProcessItem(QueuedItem item)
    {
        try
        {
            var partsByName = BikePart.PartsByName;
            if (partsByName == null)
            {
                // Part catalog not loaded yet — re-queue and try later
                PendingItems.Enqueue(item);
                Plugin.BepinLogger.LogWarning("[ItemHandler] BikePart.PartsByName not available yet, re-queuing");
                return;
            }

            if (string.IsNullOrEmpty(item.ItemName) || !partsByName.ContainsKey(item.ItemName))
            {
                Plugin.BepinLogger.LogWarning(
                    $"[ItemHandler] Could not find BikePart named '{item.ItemName}' (AP ID {item.ItemId}), skipping");
                return;
            }

            var part = partsByName[item.ItemName];

            // On connect, AP re-sends every item ever received. Skip parts the
            // player already has without triggering a grant or dialogue.
            if (part.IsInPlayerInventory())
            {
                Plugin.BepinLogger.LogInfo(
                    $"[ItemHandler] Part '{item.ItemName}' already in inventory, skipping");
                return;
            }

            // Bypass the Harmony prefix and call the real PickupBikePart
            Plugin.AllowPickup = true;
            BikePartPickup.PickupBikePart(part, true, null);

            ArchipelagoConsole.LogMessage($"Received: {item.ItemName}");
            Plugin.BepinLogger.LogInfo($"[ItemHandler] Granted bike part: {item.ItemName}");

        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[ItemHandler] Error granting item '{item.ItemName}': {e}");
        }
    }

    private readonly record struct QueuedItem(string ItemName, long ItemId);
}
