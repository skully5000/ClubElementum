using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>
/// Press the button -> two different players in the instance are picked at random, teleported
/// to Target A and Target B (position AND rotation), and named on the canvas for everybody.
/// The button then greys out for everybody until the cooldown ends.
///
/// Only the client that pressed the button does the random pick, then syncs the result,
/// so everybody agrees on the same two winners. Each picked player teleports themselves
/// (TeleportTo only works on the local player).
///
/// Every press, send, receipt and teleport is logged with a [RandomPlayerTeleport] prefix, so
/// the VRChat output log shows where a failed roll stopped.
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

    [Header("Canvas")]
    [Tooltip("Optional. Greyed out during the cooldown. Its OnClick should send the event PickPlayers.")]
    public Button button;

    [Tooltip("Optional. Shows the names of the latest winners.")]
    public TextMeshProUGUI winnersText;

    [Tooltip("Optional. Plays for everybody when a new roll arrives.")]
    public AudioSource rollSound;

    [Tooltip("How long (seconds) the button stays greyed out after a press.")]
    public float cooldownSeconds = 10f;

    // Anyone joining later is sent the latest roll, which shouldn't make a sound if it's old.
    private const int RollSoundMaxAgeMs = 30000;

    // Far more presses than one player will make in an instance, so ids can't overlap.
    private const long RollIdsPerPlayer = 1000000L;

    // Synced result. rollId changes on every press so the same two people can be picked twice
    // in a row and still trigger a fresh teleport. It's built from the presser's player id, so
    // two people pressing before they've heard about each other can't send the same one.
    [UdonSynced] private int pickedIdA = -1;
    [UdonSynced] private int pickedIdB = -1;
    [UdonSynced] private string pickedNameA = "";
    [UdonSynced] private string pickedNameB = "";
    [UdonSynced] private long rollId = 0;
    [UdonSynced] private int rollTimeMs = 0;

    private long lastHandledRoll = 0;
    private int pressCount = 0;
    private bool cooldownCheckQueued = false;

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

        if (masterOnly && !localPlayer.isMaster)
        {
            Log("Press ignored: only the master can pick.");
            return;
        }
        if (targetA == null || targetB == null)
        {
            Log("Press ignored: a target is missing.");
            return;
        }
        if (CooldownRemaining() > 0f)
        {
            Log("Press ignored: still cooling down.");
            return;
        }

        // Collect everyone currently in the instance, keeping those who can be picked.
        int count = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[count];
        VRCPlayerApi.GetPlayers(players);

        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (Utilities.IsValid(players[i]) && IsInPickZone(players[i]))
            {
                players[valid] = players[i];
                valid++;
            }
        }

        if (valid == 0)
        {
            Log("Press ignored: none of the " + count + " players are in a pick zone.");
            return;
        }

        // Partial Fisher-Yates shuffle: unbiased, and guarantees two different players.
        // Random.Range(int, int) has an exclusive upper bound.
        int picks = Mathf.Min(2, valid);
        for (int i = 0; i < picks; i++)
        {
            int j = Random.Range(i, valid);
            VRCPlayerApi temp = players[i];
            players[i] = players[j];
            players[j] = temp;
        }

        Networking.SetOwner(localPlayer, gameObject);

        pickedIdA = players[0].playerId;
        pickedNameA = players[0].displayName;
        pickedIdB = picks > 1 ? players[1].playerId : -1; // -1 = nobody (only one player could be picked)
        pickedNameB = picks > 1 ? players[1].displayName : "";

        pressCount++;
        rollId = localPlayer.playerId * RollIdsPerPlayer + pressCount;
        rollTimeMs = Networking.GetServerTimeInMilliseconds();

        Log("Press: roll " + rollId + " picked " + pickedNameA + " and " + pickedNameB + " from " + valid + " of " + count + " players. Network clogged: " + Networking.IsClogged);

        RequestSerialization();

        // OnDeserialization doesn't fire on the owner, so handle our own result directly.
        ApplyRoll();
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        Log("Sent roll " + rollId + ": success " + result.success + ", " + result.byteCount + " bytes.");
    }

    public override void OnDeserialization()
    {
        ApplyRoll();
    }

    // Public so SendCustomEventDelayedSeconds can reach it.
    public void _CooldownCheck()
    {
        cooldownCheckQueued = false;
        RefreshCooldown();
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
        // Ignore repeats. A late joiner receives the latest roll too, but their player id
        // can't match an old pick, so it only fills in the names and any cooldown left.
        if (rollId == lastHandledRoll)
        {
            Log("Roll " + rollId + " already handled.");
            return;
        }
        lastHandledRoll = rollId;

        int ageMs = RollAgeMs();
        Log("Handling roll " + rollId + " " + ageMs + "ms after the press: " + pickedNameA + " and " + pickedNameB + ".");

        if (winnersText != null)
        {
            winnersText.text = pickedIdB == -1 ? pickedNameA : pickedNameA + "\n" + pickedNameB;
        }
        if (rollSound != null && ageMs < RollSoundMaxAgeMs) rollSound.Play();
        RefreshCooldown();

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

    private void RefreshCooldown()
    {
        float remaining = CooldownRemaining();
        bool cooling = remaining > 0f;

        if (button != null) button.interactable = !cooling;
        DisableInteractive = cooling;

        if (cooling && !cooldownCheckQueued)
        {
            cooldownCheckQueued = true;
            SendCustomEventDelayedSeconds(nameof(_CooldownCheck), remaining);
        }
    }

    private float CooldownRemaining()
    {
        if (rollId == 0) return 0f;
        return cooldownSeconds - RollAgeMs() / 1000f;
    }

    private int RollAgeMs()
    {
        // Subtracting handles clock wrap. Clients' clocks differ slightly, so it can start below zero.
        return Mathf.Max(0, Networking.GetServerTimeInMilliseconds() - rollTimeMs);
    }

    private void TeleportTo(VRCPlayerApi player, Transform target)
    {
        if (target == null) return;

        Log("Teleporting the local player to " + target.name + ".");

        // AlignPlayerWithSpawnPoint makes the player face the target's rotation.
        player.TeleportTo(
            target.position,
            target.rotation,
            VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
            false);
    }

    private void Log(string message)
    {
        Debug.Log("[RandomPlayerTeleport] " + message, this);
    }
}
