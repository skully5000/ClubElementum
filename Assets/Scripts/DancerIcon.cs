using UdonSharp;
using UnityEngine;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DancerIcon : UdonSharpBehaviour
{
    [Header("List Settings")]
    [Tooltip("VRCUrl pointing to the raw dancerList file on GitHub.")]
    public VRCUrl listUrl;

    [Tooltip("How often (seconds) to re-fetch the list from GitHub.")]
    public float refreshInterval = 60f;

    [Header("Icon Settings")]
    [Tooltip("Pool of icon GameObjects — one per potential dancer in the world at once.")]
    public GameObject[] iconPool;

    [Tooltip("Height offset above the player's head bone.")]
    public float heightOffset = 0.35f;

    [Tooltip("Horizontal offset to the right of the player. Use negative values to go left.")]
    public float horizontalOffset = 0f;

    [Tooltip("Forward/backward offset relative to the player's facing direction. Negative values place the icon behind them.")]
    public float depthOffset = 0f;

    [Tooltip("Make icons face the local player each frame.")]
    public bool faceLocalPlayer = true;

    private string[] _dancerNames;
    private VRCPlayerApi[] _trackedPlayers;

    void Start()
    {
        _dancerNames = new string[0];
        _trackedPlayers = new VRCPlayerApi[0];
        FetchList();
    }

    public void RefreshNow()
    {
        VRCStringDownloader.LoadUrl(listUrl, (IUdonEventReceiver)this);
    }

    public void FetchList()
    {
        VRCStringDownloader.LoadUrl(listUrl, (IUdonEventReceiver)this);
        SendCustomEventDelayedSeconds("FetchList", refreshInterval);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        string raw = result.Result.Trim();
        string[] lines = raw.Split(new char[] { '\n' });

        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0) count++;
        }

        _dancerNames = new string[count];
        int idx = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 0)
            {
                _dancerNames[idx] = line;
                idx++;
            }
        }

        Debug.Log("[DancerIcon] Loaded " + _dancerNames.Length + " dancer(s).");
        AssignIcons();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[DancerIcon] Failed to load list: " + result.Error);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        AssignIcons();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        AssignIcons();
    }

    private void AssignIcons()
    {
        for (int i = 0; i < iconPool.Length; i++)
        {
            iconPool[i].SetActive(false);
        }

        if (_dancerNames == null || _dancerNames.Length == 0) return;

        int playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);

        _trackedPlayers = new VRCPlayerApi[iconPool.Length];

        int slot = 0;
        for (int p = 0; p < players.Length; p++)
        {
            if (slot >= iconPool.Length) break;
            if (!Utilities.IsValid(players[p])) continue;
            if (IsOnList(players[p].displayName))
            {
                _trackedPlayers[slot] = players[p];
                iconPool[slot].SetActive(true);
                slot++;
            }
        }
    }

    void Update()
    {
        if (_trackedPlayers == null || _trackedPlayers.Length == 0) return;

        VRCPlayerApi local = Networking.LocalPlayer;

        for (int i = 0; i < _trackedPlayers.Length; i++)
        {
            if (!iconPool[i].activeSelf) break;

            VRCPlayerApi player = _trackedPlayers[i];
            if (!Utilities.IsValid(player))
            {
                iconPool[i].SetActive(false);
                continue;
            }

            Vector3 headPos = player.GetBonePosition(HumanBodyBones.Head);
            if (headPos == Vector3.zero)
            {
                headPos = player.GetPosition() + Vector3.up * 1.8f;
            }

            Vector3 right = Utilities.IsValid(local)
                ? Vector3.Cross(Vector3.up, local.GetPosition() - headPos).normalized
                : Vector3.right;
            Vector3 depth;
            if (faceLocalPlayer && Utilities.IsValid(local))
            {
                Vector3 toViewer = (local.GetPosition() - headPos).normalized;
                depth = toViewer * -depthOffset;
            }
            else
            {
                depth = (player.GetRotation() * Vector3.forward) * depthOffset;
            }
            iconPool[i].transform.position = headPos + Vector3.up * heightOffset + right * horizontalOffset + depth;

            if (faceLocalPlayer && Utilities.IsValid(local))
            {
                Vector3 dir = iconPool[i].transform.position - local.GetPosition();
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                {
                    iconPool[i].transform.rotation = Quaternion.LookRotation(dir);
                }
            }
        }
    }

    private bool IsOnList(string playerName)
    {
        string lower = playerName.ToLower();
        for (int i = 0; i < _dancerNames.Length; i++)
        {
            if (_dancerNames[i].ToLower() == lower) return true;
        }
        return false;
    }
}
