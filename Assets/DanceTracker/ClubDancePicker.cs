using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.StringLoading;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Press the button -> an audience member with the fewest normal dances is picked at random, and
/// a beam of light leads the dancer who pressed it to them. Only that dancer sees the beam.
///
/// Only the client that pressed the button does the pick. The result goes out as a network event,
/// so the board reads the same for everybody and the next press from any dancer skips that person,
/// and anyone joining later doesn't replay an old result.
///
/// The audience is everyone except the dancer pressing, anyone with their numbers showing, and
/// anyone staff have marked No Dance or hidden. The person picked most recently is skipped unless
/// they're the only option, so a second press before their count goes up doesn't land on them again.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class ClubDancePicker : UdonSharpBehaviour
{
    [Tooltip("Where the counts and the player list come from.")]
    public ClubDanceTracker tracker;

    [Tooltip("Optional. Shows who was picked.")]
    public TextMeshProUGUI resultText;

    [Header("Booster List")]
    [Tooltip("Name list on GitHub for the Booster column on the board. Boosters can still be picked.")]
    public VRCUrl boosterListUrl;

    [Tooltip("How often (seconds) to re-fetch the list from GitHub.")]
    public float refreshInterval = 300f;

    [Header("Beam")]
    [Tooltip("Inactive LineRenderer, switched on only for the dancer who pressed the button.")]
    public LineRenderer beamTemplate;

    public Color beamColor = Color.white;

    [Tooltip("How long (seconds) the beam stays at full brightness before it starts to fade.")]
    public float beamDuration = 5f;

    [Tooltip("How long (seconds) the fade out takes.")]
    public float beamFadeTime = 1f;

    [Tooltip("Height above the dancer's head bone that the beam starts from.")]
    public float beamStartHeight = 0.6f;

    private VRCPlayerApi _beamAudience;
    private float _beamEndTime;

    // Each name on its own line between newlines, lower case, so a name can be matched whole.
    private string _boosterText = "\n";

    private const float ListRetrySeconds = 60f;
    private bool _listRetryQueued;

    private int _lastPickedId = -1;

    void Start()
    {
        beamTemplate.gameObject.SetActive(false);
        _FetchList();
    }

    // Public so SendCustomEventDelayedSeconds can reach it.
    public void _FetchList()
    {
        LoadList();
        SendCustomEventDelayedSeconds(nameof(_FetchList), refreshInterval);
    }

    // Public so SendCustomEventDelayedSeconds can reach it.
    public void _RetryList()
    {
        _listRetryQueued = false;
        LoadList();
    }

    private void LoadList()
    {
        VRCStringDownloader.LoadUrl(boosterListUrl, (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        _boosterText = "\n" + NormaliseList(result.Result);
        tracker._RefreshBoards();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[ClubDancePicker] Failed to load list: " + result.Error);

        // Don't wait for the next refresh. A list that loaded before is kept in the meantime.
        if (_listRetryQueued) return;
        _listRetryQueued = true;
        SendCustomEventDelayedSeconds(nameof(_RetryList), ListRetrySeconds);
    }

    // Public so the board's button can call it (SendCustomEvent "_Pick").
    public void _Pick()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;

        // Just after joining everyone looks like they're on 0 dances and nobody looks like staff,
        // so a pick would be wrong.
        if (!tracker.HasData())
        {
            if (resultText != null) resultText.text = "Still loading, try again in a moment";
            return;
        }

        VRCPlayerApi[] players = tracker.GetPresentPlayers();

        int picked = PickFrom(players, local, _lastPickedId);
        if (picked == -1) picked = PickFrom(players, local, -1);

        // Temporary, to see what Pick is choosing from in a real client.
        for (int i = 0; i < players.Length; i++)
        {
            string playerName = players[i].displayName;
            Debug.Log("[ClubDancePicker] " + playerName + ": dances=" + tracker.GetDances(playerName) + " showingNumbers=" + tracker.IsShowingNumbers(playerName));
        }
        Debug.Log("[ClubDancePicker] picked id " + picked + ", last picked id " + _lastPickedId + ", me " + local.playerId);

        if (picked == -1)
        {
            if (resultText != null) resultText.text = "Nobody to pick right now";
            return;
        }

        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ShowPick), local.playerId, picked);
    }

    [NetworkCallable]
    public void ShowPick(int dancerId, int audienceId)
    {
        _lastPickedId = audienceId;

        VRCPlayerApi dancer = VRCPlayerApi.GetPlayerById(dancerId);
        VRCPlayerApi audience = VRCPlayerApi.GetPlayerById(audienceId);
        if (!Utilities.IsValid(dancer) || !Utilities.IsValid(audience)) return;

        if (resultText != null) resultText.text = dancer.displayName + " dances on " + audience.displayName;

        if (!dancer.isLocal) return;

        _beamAudience = audience;
        _beamEndTime = Time.time + beamDuration + beamFadeTime;
        beamTemplate.gameObject.SetActive(true);
    }

    public override void PostLateUpdate()
    {
        if (_beamAudience == null) return;

        float now = Time.time;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (now >= _beamEndTime || !Utilities.IsValid(local) || !Utilities.IsValid(_beamAudience))
        {
            _beamAudience = null;
            beamTemplate.gameObject.SetActive(false);
            return;
        }

        beamTemplate.SetPosition(0, HeadPosition(local) + Vector3.up * beamStartHeight);
        beamTemplate.SetPosition(1, HeadPosition(_beamAudience));

        Color color = beamColor;
        if (beamFadeTime > 0f) color.a *= Mathf.Clamp01((_beamEndTime - now) / beamFadeTime);
        beamTemplate.startColor = color;
        beamTemplate.endColor = color;
    }

    /// <summary>The id of a random audience member with the fewest dances, or -1 if there are none.</summary>
    private int PickFrom(VRCPlayerApi[] players, VRCPlayerApi dancer, int skipId)
    {
        int[] tied = new int[players.Length];
        int tiedCount = 0;
        int fewest = int.MaxValue;

        for (int i = 0; i < players.Length; i++)
        {
            VRCPlayerApi player = players[i];
            if (player.playerId == dancer.playerId || player.playerId == skipId) continue;

            string playerName = player.displayName;
            if (tracker.IsShowingNumbers(playerName)) continue;

            // No Dance and Hidden are negative, so this also skips anyone marked as either.
            int dances = tracker.GetDances(playerName);
            if (dances < 0 || dances > fewest) continue;

            if (dances < fewest)
            {
                fewest = dances;
                tiedCount = 0;
            }

            tied[tiedCount] = player.playerId;
            tiedCount++;
        }

        // Random.Range(int, int) has an exclusive upper bound.
        return tiedCount == 0 ? -1 : tied[Random.Range(0, tiedCount)];
    }

    public bool IsBooster(string playerName)
    {
        return _boosterText.Contains("\n" + playerName.ToLower() + "\n");
    }

    private string NormaliseList(string raw)
    {
        string[] lines = raw.Split(new char[] { '\n' });
        string text = "";
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 0) text += line.ToLower() + "\n";
        }
        return text;
    }

    private Vector3 HeadPosition(VRCPlayerApi player)
    {
        Vector3 headPos = player.GetBonePosition(HumanBodyBones.Head);
        if (headPos == Vector3.zero)
        {
            headPos = player.GetPosition() + Vector3.up * 1.8f;
        }
        return headPos;
    }
}
