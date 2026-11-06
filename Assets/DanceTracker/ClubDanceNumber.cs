using UdonSharp;
using UnityEngine;

/// <summary>
/// One clickable number above a player's head. Its parent is the copy of the number template
/// that ClubDanceTracker made for that player, and the tracker works out who that is.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ClubDanceNumber : UdonSharpBehaviour
{
    [Tooltip("Ticked on the stage dance number, off on the normal dance number.")]
    public bool isStage;

    public override void Interact()
    {
        Transform root = transform.parent;
        ClubDanceTracker tracker = root.GetComponentInParent<ClubDanceTracker>();
        if (tracker != null) tracker._PressNumber(root.gameObject, isStage);
    }
}
