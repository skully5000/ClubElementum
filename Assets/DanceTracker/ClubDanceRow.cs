using TMPro;
using UdonSharp;
using UnityEngine;

/// <summary>
/// One row of the board, called by the row's own buttons. It reads who it's for from its name
/// text, which the board rewrites whenever the rows re-sort, so a press always lands on the name
/// showing. Nothing is called on a new copy before it starts.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ClubDanceRow : UdonSharpBehaviour
{
    // Public so the row's buttons can call these (SendCustomEvent).
    public void _AddDance()
    {
        Tracker()._AddCount(PlayerName(), false);
    }

    public void _AddStage()
    {
        Tracker()._AddCount(PlayerName(), true);
    }

    public void _ToggleFresh()
    {
        Tracker()._ToggleFresh(PlayerName());
    }

    // Through the board, so a copy of the board works anywhere in the scene.
    private ClubDanceTracker Tracker()
    {
        return GetComponentInParent<ClubDanceBoard>().tracker;
    }

    private string PlayerName()
    {
        return transform.Find("Name").GetComponent<TextMeshProUGUI>().text;
    }
}
