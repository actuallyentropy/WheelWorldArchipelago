using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.Packets;
using WheelWorldArchipelago.Utils;

namespace WheelWorldArchipelago.Archipelago;

public class ArchipelagoClient
{
    public const string APVersion = "0.5.0";
    private const string Game = "Wheel World";

    public static bool Authenticated;
    private bool attemptingConnection;

    public static ArchipelagoData ServerData = new();
    private DeathLinkHandler DeathLinkHandler;
    private ArchipelagoSession session;

    /// <summary>
    /// Number of items we have dequeued from the ReceivedItemsHelper this session.
    /// Compared against helper.Index (the library's total received count) to detect
    /// desync — if they diverge, we send Sync + LocationChecks per the AP protocol.
    /// </summary>
    private int _itemsProcessed;

    /// <summary>
    /// call to connect to an Archipelago session. Connection info should already be set up on ServerData
    /// </summary>
    /// <returns></returns>
    public void Connect()
    {
        if (Authenticated || attemptingConnection) return;

        try
        {
            session = ArchipelagoSessionFactory.CreateSession(ServerData.Uri);
            SetupSession();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }

        TryConnect();
    }

    /// <summary>
    /// add handlers for Archipelago events
    /// </summary>
    private void SetupSession()
    {
        session.MessageLog.OnMessageReceived += message => ArchipelagoConsole.LogMessage(message.ToString());
        session.Items.ItemReceived += OnItemReceived;
        session.Socket.ErrorReceived += OnSessionErrorReceived;
        session.Socket.SocketClosed += OnSessionSocketClosed;
    }

    /// <summary>
    /// attempt to connect to the server with our connection info
    /// </summary>
    private void TryConnect()
    {
        try
        {
            // it's safe to thread this function call but unity notoriously hates threading so do not use excessively
            ThreadPool.QueueUserWorkItem(
                _ => HandleConnectResult(
                    session.TryConnectAndLogin(
                        Game,
                        ServerData.SlotName,
                        ItemsHandlingFlags.AllItems, 
                        new Version(APVersion),
                        password: ServerData.Password,
                        requestSlotData: false // ServerData.NeedSlotData
                    )));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
            HandleConnectResult(new LoginFailure(e.ToString()));
            attemptingConnection = false;
        }
    }

    /// <summary>
    /// handle the connection result and do things
    /// </summary>
    /// <param name="result"></param>
    private void HandleConnectResult(LoginResult result)
    {
        string outText;
        if (result.Successful)
        {
            var success = (LoginSuccessful)result;

            ServerData.SetupSession(success.SlotData, session.RoomState.Seed);
            Authenticated = true;
            _itemsProcessed = 0;

            DeathLinkHandler = new(session.CreateDeathLinkService(), ServerData.SlotName);

            // Log item state from the library after connect
            var allItems = session.Items.AllItemsReceived;
            Plugin.BepinLogger.LogInfo(
                $"[Connect] Items in library cache: {allItems.Count}, " +
                $"helper.Index={session.Items.Index}");
            for (int i = 0; i < allItems.Count; i++)
            {
                var item = allItems[i];
                Plugin.BepinLogger.LogInfo(
                    $"[Connect]   Item[{i}]: '{item.ItemName}' (ID {item.ItemId})");
            }

            // Reconstruct checked locations from the game's save data and send to server
            CheckLocations();

            // Scout shop locations to learn what items are at each shop slot
            ShopHandler.ScoutShopLocations(session);

            outText = $"Successfully connected to {ServerData.Uri} as {ServerData.SlotName}!";

            ArchipelagoConsole.LogMessage(outText);
        }
        else
        {
            var failure = (LoginFailure)result;
            outText = $"Failed to connect to {ServerData.Uri} as {ServerData.SlotName}.";
            outText = failure.Errors.Aggregate(outText, (current, error) => current + $"\n    {error}");

            Plugin.BepinLogger.LogError(outText);

            Authenticated = false;
            Disconnect();
        }

        ArchipelagoConsole.LogMessage(outText);
        attemptingConnection = false;
    }

    /// <summary>
    /// something went wrong, or we need to properly disconnect from the server. cleanup and re null our session
    /// </summary>
    private void Disconnect()
    {
        Plugin.BepinLogger.LogDebug("disconnecting from server...");
        ShopHandler.Clear();
        session?.Socket.DisconnectAsync();
        session = null;
        Authenticated = false;
    }

    public void SendMessage(string message)
    {
        session.Socket.SendPacketAsync(new SayPacket { Text = message });
    }

    /// <summary>
    /// Reads the game's save data to find all checked locations, merges with any
    /// explicitly provided location IDs, and sends the full set to the AP server.
    /// This implements the LocationChecks network protocol message.
    /// </summary>
    /// <param name="additionalLocationIds">
    /// Extra AP location IDs to include (e.g. from a box that was just opened and
    /// may not yet be reflected in SaveDataManager).
    /// </param>
    public void CheckLocations(List<long> additionalLocationIds = null)
    {
        if (!Authenticated || session == null) return;

        var locationIds = new HashSet<long>();

        // Include explicitly provided IDs first — guarantees the newly checked
        // location is sent even if OpenInSave hasn't committed yet.
        if (additionalLocationIds != null)
            foreach (var id in additionalLocationIds)
                locationIds.Add(id);

        // Scan the live save data for all previously checked locations
        try
        {
            var saveData = SaveDataManager.Instance?.Data;
            if (saveData != null)
            {
                CollectLocationIds(saveData.openedPartBoxIds, locationIds);
                CollectLocationIds(saveData.completedChallengeIds, locationIds);
                CollectLocationIds(saveData.itemsBought, locationIds);
            }
            else
            {
                Plugin.BepinLogger.LogWarning(
                    "[CheckLocations] SaveDataManager.Instance?.Data is null — save data not loaded yet");
            }
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"Error reading save data for location checks: {e}");
        }

        Plugin.BepinLogger.LogInfo($"[CheckLocations] Found {locationIds.Count} location(s) to send");
        if (locationIds.Count > 0)
        {
            session.Locations.CompleteLocationChecksAsync(locationIds.ToArray());
            ArchipelagoConsole.LogMessage($"Sent {locationIds.Count} location check(s) to server");
        }
    }

    /// <summary>
    /// Converts a list of game GlobalIDs to AP location IDs using the location map.
    /// </summary>
    private static void CollectLocationIds(
        Il2CppSystem.Collections.Generic.List<GlobalID> globalIds,
        HashSet<long> locationIds)
    {
        if (globalIds == null) return;

        for (int i = 0; i < globalIds.Count; i++)
        {
            var uuid = globalIds[i].ToString();
            if (Plugin.LocationMap.TryGetValue(uuid, out var apIds))
                foreach (var apId in apIds)
                    locationIds.Add(apId);
        }
    }

    /// <summary>
    /// Called on a background thread when the AP server sends us an item.
    /// Fires once per item — on connect this includes every item ever received.
    /// The library internally handles packet-level index synchronization
    /// (resync when packet index == 0 or mismatches its cache).
    /// We track our own processed count to detect application-level desync.
    /// </summary>
    /// <param name="helper">item helper which we can grab our item from</param>
    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        try
        {
            var receivedItem = helper.DequeueItem();
            if (receivedItem == null)
            {
                Plugin.BepinLogger.LogWarning("[OnItemReceived] DequeueItem returned null");
                return;
            }

            _itemsProcessed++;
            var itemName = receivedItem.ItemName;
            var itemId = receivedItem.ItemId;
            var helperIndex = helper.Index;

            Plugin.BepinLogger.LogInfo(
                $"[OnItemReceived] #{_itemsProcessed}: '{itemName}' (ID {itemId}) " +
                $"[helper.Index={helperIndex}]");

            // Desync detection: our processed count should track the library's
            // total received count. If they diverge, re-synchronize per protocol.
            if (_itemsProcessed != helperIndex)
            {
                Plugin.BepinLogger.LogWarning(
                    $"[OnItemReceived] Desync: processed {_itemsProcessed} but helper.Index={helperIndex}. " +
                    "Sending Sync + LocationChecks.");
                session?.Socket.SendPacketAsync(new SyncPacket());
                CheckLocations();
            }

            // Queue for main-thread processing — ItemHandler handles in-game checks,
            // inventory-based dedup, presence gating, and the actual PickupBikePart call.
            ItemHandler.Enqueue(itemName, itemId);
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[OnItemReceived] Exception: {e}");
        }
    }

    /// <summary>
    /// something went wrong with our socket connection
    /// </summary>
    /// <param name="e">thrown exception from our socket</param>
    /// <param name="message">message received from the server</param>
    private void OnSessionErrorReceived(Exception e, string message)
    {
        Plugin.BepinLogger.LogError(e);
        ArchipelagoConsole.LogMessage(message);
    }

    /// <summary>
    /// something went wrong closing our connection. disconnect and clean up
    /// </summary>
    /// <param name="reason"></param>
    private void OnSessionSocketClosed(string reason)
    {
        Plugin.BepinLogger.LogError($"Connection to Archipelago lost: {reason}");
        Disconnect();
    }
}