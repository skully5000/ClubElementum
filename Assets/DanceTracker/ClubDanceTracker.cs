using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

/// <summary>
/// Holds every player's dance and stage dance counts, and keeps a pair of numbers above each
/// other player's head for staff who have them switched on.
///
/// This object has to stay on for everyone, audience included, or the counts only exist while
/// staff are in the world and nothing sends them to the next one to sign in. Audience members see
/// nothing from it: the boards and staff button are in a group staff switch on by signing in, and no
/// number objects exist on a client until its numbers are first shown.
///
/// Every client keeps its own full table, keyed by player name, so every client reads the same
/// record for the same person, a record stays when its player leaves, and nothing depends on any
/// one player staying. The owner of this object is just whoever sends next.
///
/// Each record has a version that goes up with every change. An incoming table is merged in with
/// the newest version of each record winning, so a table from someone who is behind (just joined,
/// just unfroze) can't overwrite newer counts. A client that sees a table missing something it
/// holds sends the merged table back out, and the owner resends on a timer so anything that went
/// wrong heals itself.
///
/// Which pair of numbers follows which player is local to each client and never synced. Pressing
/// Show Numbers is recorded in the table, because anyone with their numbers showing is working:
/// they get no numbers above their own head and can't be picked to be danced on.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ClubDanceTracker : UdonSharpBehaviour
{
    // Values a dance count can hold besides a number.
    public const int NoDance = -1;
    public const int Hidden = -2;

    [Header("Numbers")]
    [Tooltip("Inactive object with the dance number as its first child, the stage number as its second and the fresh meat icon as its third. One copy is made per player.")]
    public GameObject numberTemplate;

    [Tooltip("Material for each count, starting at 0. The last one is the highest count before it moves on to No Dance.")]
    public Material[] numberMaterials;

    [Tooltip("Shown instead of the dance count for someone marked No Dance.")]
    public Material noDanceMaterial;

    [Tooltip("Height of the numbers above the player's head bone.")]
    public float heightOffset = 0.35f;

    [Tooltip("Hide the numbers above anyone who has their own numbers showing. They still can't be picked either way.")]
    public bool hideNumbersAboveStaff = true;

    [Header("UI")]
    [Tooltip("Optional. Label on the staff button, showing what pressing it will do.")]
    public TextMeshProUGUI toggleLabel;

    // What goes over the network. Only filled when sending, and overwritten when a table arrives.
    [UdonSynced] private string[] _syncNames = new string[0];
    [UdonSynced] private int[] _syncDances = new int[0];
    [UdonSynced] private int[] _syncStage = new int[0];
    [UdonSynced] private bool[] _syncFresh = new bool[0];
    [UdonSynced] private bool[] _syncShowing = new bool[0];
    [UdonSynced] private int[] _syncVersions = new int[0];

    // This client's own table.
    private string[] _names = new string[0];
    private int[] _dances = new int[0];
    private int[] _stage = new int[0];
    private bool[] _fresh = new bool[0];
    private bool[] _showing = new bool[0];
    private int[] _versions = new int[0];

    private bool _hasData;
    private bool _resendQueued;

    // Signed in before the table arrived, so show the numbers as soon as it does.
    private bool _showWhenReady;

    private const float HeartbeatSeconds = 30f;

    // More than an instance can ever hold, so nobody goes without numbers.
    private const int MaxSlots = 128;

    private VRCPlayerApi[] _slotPlayers = new VRCPlayerApi[MaxSlots];
    private string[] _slotNames = new string[MaxSlots];
    private GameObject[] _slotRoots = new GameObject[MaxSlots];
    private MeshRenderer[] _slotDance = new MeshRenderer[MaxSlots];
    private MeshRenderer[] _slotStage = new MeshRenderer[MaxSlots];
    private MeshRenderer[] _slotFresh = new MeshRenderer[MaxSlots];
    private int _slotCount;

    // Each board adds itself when it starts, so a board can be duplicated with nothing to wire up.
    private ClubDanceBoard[] _boards = new ClubDanceBoard[0];

    private bool _toggledOn;
    private bool _cameraOut;

    void Start()
    {
        numberTemplate.SetActive(false);
        UpdateToggleLabel();
        SendCustomEventDelayedSeconds(nameof(_Heartbeat), HeartbeatSeconds);
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return;

        // Nobody needs numbers floating above their own view.
        if (!player.isLocal) AssignSlot(player);

        // Gets the table to whoever joined now instead of at the next heartbeat.
        if (Networking.IsOwner(gameObject)) RequestSerialization();

        _RefreshBoards();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return;

        for (int i = 0; i < _slotCount; i++)
        {
            if (Utilities.IsValid(_slotPlayers[i]) && _slotPlayers[i].playerId == player.playerId) FreeSlot(i);
        }

        _RefreshBoards();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        // If the new owner is behind, sending now lets everyone else see that and send back.
        if (Utilities.IsValid(player) && player.isLocal) RequestSerialization();

        if (_hasData) return;

        // Now the owner of a table nobody has sent us, so there's nothing left to wait for.
        if (_showWhenReady && HasData())
        {
            _showWhenReady = false;
            SetNumbers(true);
        }

        ApplyVisibility();
        _RefreshBoards();
    }

    public override void OnPreSerialization()
    {
        int count = _names.Length;
        _syncNames = new string[count];
        _syncDances = new int[count];
        _syncStage = new int[count];
        _syncFresh = new bool[count];
        _syncShowing = new bool[count];
        _syncVersions = new int[count];

        for (int i = 0; i < count; i++)
        {
            _syncNames[i] = _names[i];
            _syncDances[i] = _dances[i];
            _syncStage[i] = _stage[i];
            _syncFresh[i] = _fresh[i];
            _syncShowing[i] = _showing[i];
            _syncVersions[i] = _versions[i];
        }
    }

    public override void OnDeserialization()
    {
        if (!IncomingIsWhole()) return;

        bool first = !_hasData;
        _hasData = true;

        bool changed = MergeIncoming();
        SettleOwnMark();
        if (first || changed) RefreshAll();

        if (_resendQueued || !AheadOfIncoming()) return;

        // Everyone holding the same extra records sees this at once, so wait a random moment.
        // Whoever goes first usually saves the rest from sending at all.
        _resendQueued = true;
        SendCustomEventDelayedSeconds(nameof(_ResendIfAhead), Random.Range(0.5f, 3f));
    }

    // Public so SendCustomEventDelayedSeconds can reach it.
    public void _ResendIfAhead()
    {
        _resendQueued = false;
        if (IncomingIsWhole() && AheadOfIncoming()) SendTable();
    }

    // Public so SendCustomEventDelayedSeconds can reach it.
    public void _Heartbeat()
    {
        if (Networking.IsOwner(gameObject) && !Networking.IsClogged) RequestSerialization();
        SendCustomEventDelayedSeconds(nameof(_Heartbeat), HeartbeatSeconds);
    }

    // Public so the staff button's OnClick can call it (SendCustomEvent "_ToggleNumbers").
    public void _ToggleNumbers()
    {
        Debug.Log("[ClubDanceTracker] button pressed, on was " + _toggledOn);
        SetNumbers(!_toggledOn);
        SendCustomEventDelayedSeconds(nameof(_LogState), 2f);
    }

    /// <summary>What signing in does, through ClubDanceStaffButton.</summary>
    public void _ShowNumbers()
    {
        Debug.Log("[ClubDanceTracker] sign in, on was " + _toggledOn + ", has data " + HasData());
        if (HasData()) SetNumbers(true);
        else _showWhenReady = true;
        SendCustomEventDelayedSeconds(nameof(_LogState), 2f);
    }

    /// <summary>What signing out does, through ClubDanceStaffButton.</summary>
    public void _HideNumbers()
    {
        Debug.Log("[ClubDanceTracker] sign out, on was " + _toggledOn);
        _showWhenReady = false;
        SetNumbers(false);
    }

    // Temporary, to find out why the numbers aren't showing in a real client.
    public void _LogState()
    {
        VRCCameraSettings photo = VRCCameraSettings.PhotoCamera;
        Debug.Log("[ClubDanceTracker] state: on=" + _toggledOn + " cameraOut=" + _cameraOut + " photoActive=" + (photo != null && photo.Active)
            + " hasData=" + HasData() + " owner=" + Networking.IsOwner(gameObject) + " hideAboveStaff=" + hideNumbersAboveStaff
            + " slots=" + _slotCount + " records=" + _names.Length);

        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotPlayers[i] == null) continue;

            string line = "[ClubDanceTracker] slot " + i + " " + _slotNames[i] + ": marked=" + IsShowingNumbers(_slotNames[i]) + " visible=" + SlotVisible(i);
            GameObject root = _slotRoots[i];
            if (root == null)
            {
                line += " no objects";
            }
            else
            {
                line += " active=" + root.activeInHierarchy + " pos=" + root.transform.position.ToString() + " scale=" + root.transform.lossyScale.ToString()
                    + " danceOn=" + _slotDance[i].enabled + " material=" + _slotDance[i].sharedMaterial.name;
            }
            if (Utilities.IsValid(_slotPlayers[i])) line += " head=" + _slotPlayers[i].GetBonePosition(HumanBodyBones.Head).ToString();
            Debug.Log(line);
        }
    }

    /// <summary>Called by a number when staff click it. The root is the copy of the template it sits in.</summary>
    public void _PressNumber(GameObject root, bool isStage)
    {
        int slot = -1;
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotRoots[i] == root) slot = i;
        }
        if (slot == -1 || _slotPlayers[slot] == null) return;

        _AddCount(_slotNames[slot], isStage);
    }

    /// <summary>Moves someone's count on one step, from a number above their head or a row on the board.</summary>
    public void _AddCount(string playerName, bool isStage)
    {
        // The board can be pressed before the table arrives, when it would be counting up from a wrong 0.
        if (!HasData()) return;

        // A hidden player's stage number is invisible, so a stray click shouldn't change it.
        if (isStage && GetDances(playerName) == Hidden) return;

        int record = IndexOfRecord(playerName);
        if (record == -1) record = AddRecord(playerName);

        int maxCount = numberMaterials.Length - 1;
        if (isStage) _stage[record] = _stage[record] >= maxCount ? 0 : _stage[record] + 1;
        else _dances[record] = NextDanceCount(_dances[record], maxCount);
        _versions[record]++;

        SendTable();

        // OnDeserialization doesn't fire on the owner, so show our own change directly.
        RefreshAll();
    }

    /// <summary>Called by the tickbox on the board.</summary>
    public void _ToggleFresh(string playerName)
    {
        if (!HasData()) return;

        int record = IndexOfRecord(playerName);
        if (record == -1) record = AddRecord(playerName);

        _fresh[record] = !_fresh[record];
        _versions[record]++;

        SendTable();
        RefreshAll();
    }

    public void _AddBoard(ClubDanceBoard board)
    {
        ClubDanceBoard[] boards = new ClubDanceBoard[_boards.Length + 1];
        for (int i = 0; i < _boards.Length; i++)
        {
            boards[i] = _boards[i];
        }
        boards[_boards.Length] = board;
        _boards = boards;
    }

    public void _RefreshBoards()
    {
        for (int i = 0; i < _boards.Length; i++)
        {
            _boards[i]._Refresh();
        }
    }

    /// <summary>False only for someone who just joined and hasn't received the table yet.</summary>
    public bool HasData()
    {
        return _hasData || Networking.IsOwner(gameObject);
    }

    public bool IsShowingNumbers(string playerName)
    {
        int record = IndexOfRecord(playerName);
        return record != -1 && _showing[record];
    }

    public int GetDances(string playerName)
    {
        int record = IndexOfRecord(playerName);
        return record == -1 ? 0 : _dances[record];
    }

    public int GetStage(string playerName)
    {
        int record = IndexOfRecord(playerName);
        return record == -1 ? 0 : _stage[record];
    }

    public bool IsFresh(string playerName)
    {
        int record = IndexOfRecord(playerName);
        return record != -1 && _fresh[record];
    }

    /// <summary>Everyone in the instance, including the local player.</summary>
    public VRCPlayerApi[] GetPresentPlayers()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        int count = Utilities.IsValid(local) ? 1 : 0;
        for (int i = 0; i < _slotCount; i++)
        {
            if (Utilities.IsValid(_slotPlayers[i])) count++;
        }

        VRCPlayerApi[] players = new VRCPlayerApi[count];
        int next = 0;
        if (Utilities.IsValid(local))
        {
            players[0] = local;
            next = 1;
        }
        for (int i = 0; i < _slotCount; i++)
        {
            if (!Utilities.IsValid(_slotPlayers[i])) continue;
            players[next] = _slotPlayers[i];
            next++;
        }
        return players;
    }

    // PostLateUpdate runs after avatars are posed for the frame, so the numbers stay locked to the
    // head instead of trailing it by a frame.
    public override void PostLateUpdate()
    {
        // All an audience member ever does here.
        if (!_toggledOn) return;

        // Same rule as HideFromPhotoCamera, which can't see these because they're made at runtime.
        // PhotoCamera is null in ClientSim; in a real client it never is.
        VRCCameraSettings photo = VRCCameraSettings.PhotoCamera;
        bool cameraOut = photo != null && photo.Active;
        if (cameraOut != _cameraOut)
        {
            _cameraOut = cameraOut;
            ApplyVisibility();
        }

        if (!NumbersVisible()) return;

        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        Vector3 localPosition = local.GetPosition();

        for (int i = 0; i < _slotCount; i++)
        {
            VRCPlayerApi player = _slotPlayers[i];
            if (player == null) continue;
            if (!Utilities.IsValid(player))
            {
                FreeSlot(i);
                continue;
            }
            if (_slotRoots[i] == null) continue;

            Vector3 headPos = player.GetBonePosition(HumanBodyBones.Head);
            if (headPos == Vector3.zero)
            {
                headPos = player.GetPosition() + Vector3.up * 1.8f;
            }

            Transform root = _slotRoots[i].transform;
            root.position = headPos + Vector3.up * heightOffset;

            // Always facing the viewer, so the stage number is on their right from any side.
            Vector3 dir = root.position - localPosition;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
            {
                root.rotation = Quaternion.LookRotation(dir);
            }
        }
    }

    private void SetNumbers(bool on)
    {
        // The press goes in the table, which someone who just joined doesn't have yet.
        if (!HasData()) return;
        if (on == _toggledOn) return;

        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;

        _toggledOn = on;
        UpdateToggleLabel();
        MarkShowing(local.displayName, on);
        RefreshAll();
    }

    private void MarkShowing(string playerName, bool showing)
    {
        int record = IndexOfRecord(playerName);
        if (record == -1) record = AddRecord(playerName);

        _showing[record] = showing;
        _versions[record]++;

        SendTable();
    }

    // Our mark is the one thing in the table only we mean to change, so it always follows our own
    // numbers. That clears an old mark when we rejoin, since nobody is signed in on arrival, and
    // puts back a mark lost to someone else's edit of our record.
    private void SettleOwnMark()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;

        string localName = local.displayName;
        bool marked = IsShowingNumbers(localName);

        if (_showWhenReady)
        {
            _showWhenReady = false;
            _toggledOn = true;
            UpdateToggleLabel();
        }

        if (marked != _toggledOn) MarkShowing(localName, _toggledOn);
    }

    private void SendTable()
    {
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        RequestSerialization();
    }

    // An exception would stop this script for good on this client, so check before indexing.
    private bool IncomingIsWhole()
    {
        int count = _syncNames.Length;
        return _syncDances.Length == count && _syncStage.Length == count && _syncFresh.Length == count && _syncShowing.Length == count && _syncVersions.Length == count;
    }

    /// <summary>Takes every incoming record that beats ours. Returns whether anything changed.</summary>
    private bool MergeIncoming()
    {
        bool changed = false;

        for (int i = 0; i < _syncNames.Length; i++)
        {
            string playerName = _syncNames[i];
            int record = i < _names.Length && _names[i] == playerName ? i : IndexOfRecord(playerName);
            if (record == -1) record = AddRecord(playerName);

            if (!Beats(_syncVersions[i], _syncDances[i], _syncStage[i], _syncFresh[i], _syncShowing[i], _versions[record], _dances[record], _stage[record], _fresh[record], _showing[record])) continue;

            _versions[record] = _syncVersions[i];
            _dances[record] = _syncDances[i];
            _stage[record] = _syncStage[i];
            _fresh[record] = _syncFresh[i];
            _showing[record] = _syncShowing[i];
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether we hold a record the last table to arrive is missing, or a newer copy of one.</summary>
    private bool AheadOfIncoming()
    {
        for (int i = 0; i < _names.Length; i++)
        {
            string playerName = _names[i];
            int incoming = i < _syncNames.Length && _syncNames[i] == playerName ? i : System.Array.IndexOf(_syncNames, playerName);
            if (incoming == -1) return true;

            if (Beats(_versions[i], _dances[i], _stage[i], _fresh[i], _showing[i], _syncVersions[incoming], _syncDances[incoming], _syncStage[incoming], _syncFresh[incoming], _syncShowing[incoming])) return true;
        }

        return false;
    }

    // Two staff clicking the same person at once can produce the same version with different
    // counts, so ties need a rule that picks the same winner on every client.
    private bool Beats(int version, int dances, int stage, bool fresh, bool showing, int otherVersion, int otherDances, int otherStage, bool otherFresh, bool otherShowing)
    {
        if (version != otherVersion) return version > otherVersion;
        if (dances != otherDances) return dances > otherDances;
        if (stage != otherStage) return stage > otherStage;
        if (fresh != otherFresh) return fresh;
        return showing && !otherShowing;
    }

    private void AssignSlot(VRCPlayerApi player)
    {
        int slot = -1;
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotPlayers[i] == null)
            {
                slot = i;
                break;
            }
        }

        if (slot == -1)
        {
            if (_slotCount >= MaxSlots) return;
            slot = _slotCount;
            _slotCount++;
        }

        _slotPlayers[slot] = player;
        _slotNames[slot] = player.displayName;
        ApplySlot(slot);
        ApplyNumbers(slot);
    }

    private void CreateSlot(int slot)
    {
        GameObject root = Instantiate(numberTemplate);
        root.transform.SetParent(numberTemplate.transform.parent, false);
        _slotRoots[slot] = root;
        _slotDance[slot] = root.transform.GetChild(0).GetComponent<MeshRenderer>();
        _slotStage[slot] = root.transform.GetChild(1).GetComponent<MeshRenderer>();
        _slotFresh[slot] = root.transform.GetChild(2).GetComponent<MeshRenderer>();
    }

    private void FreeSlot(int slot)
    {
        _slotPlayers[slot] = null;
        _slotNames[slot] = null;
        if (_slotRoots[slot] != null) _slotRoots[slot].SetActive(false);
    }

    // Numbers stay hidden until the table arrives, so nobody sees or clicks a wrong 0.
    private bool NumbersVisible()
    {
        return _toggledOn && !_cameraOut && HasData();
    }

    private bool SlotVisible(int slot)
    {
        if (!NumbersVisible()) return false;
        return !hideNumbersAboveStaff || !IsShowingNumbers(_slotNames[slot]);
    }

    private void ApplyVisibility()
    {
        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotPlayers[i] != null) ApplySlot(i);
        }
    }

    // A slot's objects are only made the first time they're needed, so audience members never pay for them.
    private void ApplySlot(int slot)
    {
        bool visible = SlotVisible(slot);

        if (_slotRoots[slot] == null)
        {
            if (!visible) return;
            CreateSlot(slot);
            ApplyNumbers(slot);
        }

        _slotRoots[slot].SetActive(visible);
    }

    private void UpdateToggleLabel()
    {
        if (toggleLabel != null) toggleLabel.text = _toggledOn ? "Hide Numbers" : "Show Numbers";
    }

    private void RefreshAll()
    {
        ApplyVisibility();

        for (int i = 0; i < _slotCount; i++)
        {
            if (_slotPlayers[i] != null) ApplyNumbers(i);
        }

        _RefreshBoards();
    }

    private void ApplyNumbers(int slot)
    {
        if (_slotRoots[slot] == null) return;

        int dances = GetDances(_slotNames[slot]);
        int stage = GetStage(_slotNames[slot]);

        MeshRenderer danceRenderer = _slotDance[slot];
        MeshRenderer stageRenderer = _slotStage[slot];

        // A hidden number keeps its collider, so staff can click it back round to 0.
        danceRenderer.enabled = dances != Hidden;
        stageRenderer.enabled = dances != Hidden;
        _slotFresh[slot].enabled = dances != Hidden && IsFresh(_slotNames[slot]);

        if (dances == NoDance) danceRenderer.sharedMaterial = noDanceMaterial;
        else if (dances >= 0) danceRenderer.sharedMaterial = numberMaterials[Mathf.Clamp(dances, 0, numberMaterials.Length - 1)];

        stageRenderer.sharedMaterial = numberMaterials[Mathf.Clamp(stage, 0, numberMaterials.Length - 1)];
    }

    private int NextDanceCount(int current, int maxCount)
    {
        if (current == Hidden) return 0;
        if (current == NoDance) return Hidden;
        if (current >= maxCount) return NoDance;
        return current + 1;
    }

    private int IndexOfRecord(string playerName)
    {
        for (int i = 0; i < _names.Length; i++)
        {
            if (_names[i] == playerName) return i;
        }
        return -1;
    }

    private int AddRecord(string playerName)
    {
        int count = _names.Length;
        string[] names = new string[count + 1];
        int[] dances = new int[count + 1];
        int[] stage = new int[count + 1];
        bool[] fresh = new bool[count + 1];
        bool[] showing = new bool[count + 1];
        int[] versions = new int[count + 1];

        for (int i = 0; i < count; i++)
        {
            names[i] = _names[i];
            dances[i] = _dances[i];
            stage[i] = _stage[i];
            fresh[i] = _fresh[i];
            showing[i] = _showing[i];
            versions[i] = _versions[i];
        }

        names[count] = playerName;
        _names = names;
        _dances = dances;
        _stage = stage;
        _fresh = fresh;
        _showing = showing;
        _versions = versions;
        return count;
    }
}
