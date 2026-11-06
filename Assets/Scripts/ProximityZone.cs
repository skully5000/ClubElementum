using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Turns objects on and drives Animator parameters while the local player is inside this
/// GameObject's trigger collider(s), and reverses both while they're outside. Fill in the
/// objects, the animators, or both.
///
/// Each client only reacts to its own player, so nothing is synced: everybody just runs
/// whatever is near them.
///
/// The trigger events give the instant response, but they can't be relied on alone. VRChat
/// can skip them when a player teleports or moves very fast, and sitting in a station fires
/// no exit while standing back up fires another enter. So being inside is a bool rather than
/// a count of enters and exits, and a slow position check corrects anything the events miss.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ProximityZone : UdonSharpBehaviour
{
    [Header("Objects")]
    [Tooltip("Active while the player is inside, off while they're outside. Can't include this object or a parent of it, or the zone would switch itself off.")]
    public GameObject[] objects;

    [Header("Animators")]
    [Tooltip("Every parameter below is set on each of these. An Animator on one of the objects above goes off with it, so it can't play an exit animation.")]
    public Animator[] animators;

    [Tooltip("Bool parameter: true inside, false outside. Leave blank to skip.")]
    public string boolParameter;

    [Tooltip("Trigger parameter fired when the player walks in. Leave blank to skip.")]
    public string enterTrigger;

    [Tooltip("Trigger parameter fired when the player walks out. Leave blank to skip.")]
    public string exitTrigger;

    private const float CheckInterval = 1f;

    // ClosestPoint returns the point itself when it is inside the collider.
    private const float InsideDistance = 0.01f;

    // A player's capsule can still be touching the zone with their feet this far away, so the
    // slow check only calls them outside beyond this. Normal exits come from the exit event.
    private const float OutsideDistance = 2f;

    private Collider[] _zones;
    private bool _inside;

    void Start()
    {
        _zones = GetComponents<Collider>();

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null && transform.IsChildOf(objects[i].transform))
            {
                Debug.LogError("[ProximityZone] " + objects[i].name + " contains this zone, so it can't be switched by it.", this);
                objects[i] = null;
            }
        }

        // Start in the outside state, without firing the exit trigger.
        WriteParameters(false);
        SetObjectsActive(false);

        SendCustomEventDelayedSeconds("_CheckPosition", CheckInterval);
    }

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;

        SetInside(true);
    }

    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;

        // Fires per collider, so they may have only stepped from one of ours into another.
        if (DistanceToZone(player.GetPosition()) > InsideDistance) SetInside(false);
    }

    // Public so SendCustomEventDelayedSeconds can reach it; the underscore keeps it off the network.
    public void _CheckPosition()
    {
        SendCustomEventDelayedSeconds("_CheckPosition", CheckInterval);

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer)) return;

        float distance = DistanceToZone(localPlayer.GetPosition());
        if (distance <= InsideDistance) SetInside(true);
        else if (distance > OutsideDistance) SetInside(false);
    }

    private float DistanceToZone(Vector3 position)
    {
        float nearest = Mathf.Infinity;

        for (int i = 0; i < _zones.Length; i++)
        {
            Collider zone = _zones[i];
            if (zone == null || !zone.isTrigger || !zone.enabled) continue;

            float distance = Vector3.Distance(zone.ClosestPoint(position), position);
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    private void SetInside(bool inside)
    {
        if (inside == _inside) return;
        _inside = inside;

        // Objects go on before the animators and off after them, because an Animator on an
        // inactive object ignores parameter changes.
        if (inside) SetObjectsActive(true);
        WriteParameters(inside);
        FireTriggers(inside);
        if (!inside) SetObjectsActive(false);
    }

    private void SetObjectsActive(bool active)
    {
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null) objects[i].SetActive(active);
        }
    }

    private void WriteParameters(bool inside)
    {
        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null) continue;

            if (!string.IsNullOrEmpty(boolParameter)) animator.SetBool(boolParameter, inside);
        }
    }

    private void FireTriggers(bool inside)
    {
        string fire = inside ? enterTrigger : exitTrigger;
        string clear = inside ? exitTrigger : enterTrigger;

        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null) continue;

            // Walking in and straight back out shouldn't leave the other trigger queued up.
            if (!string.IsNullOrEmpty(clear)) animator.ResetTrigger(clear);
            if (!string.IsNullOrEmpty(fire)) animator.SetTrigger(fire);
        }
    }
}
