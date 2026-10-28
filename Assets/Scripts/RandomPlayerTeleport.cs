using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Press the button -> two different players in the instance are picked at random
/// and teleported to Target A and Target B (position AND rotation).
///
/// Only the client that pressed the button does the random pick, then syncs the result,
/// so everybody agrees on the same two winners. Each picked player teleports themselves
/// (TeleportTo only works on the local player).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class RandomPlayerTeleport : UdonSharpBehaviour
{
    [Tooltip("First picked player is sent here (position + rotation).")]
    public Transform targetA;

    [Tooltip("Second picked player is sent here (position + rotation).")]
    public Transform targetB;

    [Tooltip("Optional. If any are set, only players standing inside at least one of these colliders can be picked. Leave empty to pick from everyone in the instance.")]
    public Collider[] pickZones;

    [Tooltip("If ticked, only the instance master can trigger the pick.")]
    public bool masterOnly = false;

    // Synced result. rollNumber changes on every press so the same two people
    // can be picked twice in a row and still trigger a fresh teleport.
    [UdonSynced] private int pickedIdA = -1;
    [UdonSynced] private int pickedIdB = -1;
    [UdonSynced] private int rollNumber = 0;

    private int lastHandledRoll = 0;

    // Works for a world object with a collider (the "button").
    public override void Interact()
    {
        PickPlayers();
    }

    // Public so a UI Button's OnClick can call it too (SendCustomEvent "PickPlayers").
    public void PickPlayers()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer)) return;
        if (masterOnly && !localPlayer.isMaster) return;
        if (targetA == null || targetB == null) return;

        // Collect the ids of everyone currently in the instance.
        int count = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[count];
        VRCPlayerApi.GetPlayers(players);

        int[] ids = new int[count];
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (Utilities.IsValid(players[i]) && IsInPickZone(players[i]))
            {
                ids[valid] = players[i].playerId;
                valid++;
            }
        }

        if (valid == 0) return;

        // Partial Fisher-Yates shuffle: unbiased, and guarantees two different players.
        // Random.Range(int, int) has an exclusive upper bound.
        int picks = Mathf.Min(2, valid);
        for (int i = 0; i < picks; i++)
        {
            int j = Random.Range(i, valid);
            int temp = ids[i];
            ids[i] = ids[j];
            ids[j] = temp;
        }

        Networking.SetOwner(localPlayer, gameObject);

        pickedIdA = ids[0];
        pickedIdB = picks > 1 ? ids[1] : -1; // -1 = nobody (only one player in the instance)
        rollNumber++;

        RequestSerialization();

        // OnDeserialization doesn't fire on the owner, so handle our own result directly.
        ApplyRoll();
    }

    public override void OnDeserialization()
    {
        ApplyRoll();
    }

    private bool IsInPickZone(VRCPlayerApi player)
    {
        if (pickZones == null || pickZones.Length == 0) return true;

        // ClosestPoint returns the point itself when it is inside the collider,
        // and respects the collider's rotation (unlike bounds.Contains).
        Vector3 position = player.GetPosition();
        for (int i = 0; i < pickZones.Length; i++)
        {
            if (pickZones[i] == null) continue;

            Vector3 closest = pickZones[i].ClosestPoint(position);
            if ((closest - position).sqrMagnitude < 0.0001f) return true;
        }

        return false;
    }

    private void ApplyRoll()
    {
        // Ignore repeats, and ignore the state a late joiner receives for a roll that
        // happened before they arrived (their player id can't match an old pick anyway).
        if (rollNumber == lastHandledRoll) return;
        lastHandledRoll = rollNumber;

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer)) return;

        if (localPlayer.playerId == pickedIdA)
        {
            TeleportTo(localPlayer, targetA);
        }
        else if (localPlayer.playerId == pickedIdB)
        {
            TeleportTo(localPlayer, targetB);
        }
    }

    private void TeleportTo(VRCPlayerApi player, Transform target)
    {
        if (target == null) return;

        // AlignPlayerWithSpawnPoint makes the player face the target's rotation.
        player.TeleportTo(
            target.position,
            target.rotation,
            VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
            false);
    }
}
