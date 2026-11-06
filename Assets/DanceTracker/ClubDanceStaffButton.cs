using UdonSharp;
using VRC.SDKBase;

/// <summary>
/// Makes signing in do the same as pressing Show Numbers. It sits on the staff button and only
/// watches for being switched on and off, so whatever signs staff in just has to enable a parent
/// of it: the numbers come on with it and go off with it.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ClubDanceStaffButton : UdonSharpBehaviour
{
    public ClubDanceTracker tracker;

    [UnityEngine.Tooltip("Press Show Numbers automatically when this switches on, and Hide when it switches off. Untick to leave it to the button.")]
    public bool showNumbersOnEnable = true;

    void OnEnable()
    {
        UnityEngine.Debug.Log("[ClubDanceTracker] staff button enabled, auto show " + showNumbersOnEnable);
        if (showNumbersOnEnable) tracker._ShowNumbers();
    }

    void OnDisable()
    {
        if (showNumbersOnEnable && Utilities.IsValid(tracker)) tracker._HideNumbers();
    }
}
