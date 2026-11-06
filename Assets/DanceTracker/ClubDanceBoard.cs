using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// The table for dancers: everyone in the instance in alphabetical order, with their dance and
/// stage dance counts. It only reads from ClubDanceTracker, which tells it when to redraw.
///
/// It can be duplicated and put anywhere. Each copy adds itself to the tracker when it starts, and
/// a copy can have its Pick button, its result text and its row's + buttons deleted.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ClubDanceBoard : UdonSharpBehaviour
{
    [Tooltip("Where the counts come from.")]
    public ClubDanceTracker tracker;

    [Tooltip("Where the booster list comes from.")]
    public ClubDancePicker picker;

    [Tooltip("Inactive row inside the scroll view, with TextMeshPro children named Name, Dances, Stage and Booster, and a Fresh button. One copy is made per player.")]
    public GameObject rowTemplate;

    private const int MaxRows = 128;

    private GameObject[] _rows = new GameObject[MaxRows];
    private TextMeshProUGUI[] _nameTexts = new TextMeshProUGUI[MaxRows];
    private TextMeshProUGUI[] _danceTexts = new TextMeshProUGUI[MaxRows];
    private TextMeshProUGUI[] _stageTexts = new TextMeshProUGUI[MaxRows];
    private TextMeshProUGUI[] _boosterTexts = new TextMeshProUGUI[MaxRows];
    private TextMeshProUGUI[] _freshTexts = new TextMeshProUGUI[MaxRows];
    private int _rowCount;
    private bool _started;

    void Start()
    {
        rowTemplate.SetActive(false);
        tracker._AddBoard(this);
        _started = true;
        _Refresh();
    }

    // The counts carry on changing while this is switched off.
    void OnEnable()
    {
        if (_started) _Refresh();
    }

    public void _Refresh()
    {
        VRCPlayerApi[] players = tracker.GetPresentPlayers();
        string[] names = SortedNames(players);
        int count = Mathf.Min(names.Length, MaxRows);

        for (int i = _rowCount; i < count; i++)
        {
            CreateRow(i);
        }
        if (count > _rowCount) _rowCount = count;

        // Someone who just joined shouldn't read a row of 0s as real counts.
        bool hasData = tracker.HasData();

        for (int i = 0; i < _rowCount; i++)
        {
            _rows[i].SetActive(i < count);
            if (i >= count) continue;

            int dances = tracker.GetDances(names[i]);
            _nameTexts[i].text = names[i];
            _danceTexts[i].text = hasData ? FormatDances(dances) : "...";
            _stageTexts[i].text = !hasData ? "..." : dances == ClubDanceTracker.Hidden ? "-" : tracker.GetStage(names[i]).ToString();
            _boosterTexts[i].text = picker.IsBooster(names[i]) ? "Yes" : "";
            _freshTexts[i].text = tracker.IsFresh(names[i]) ? "X" : "";
        }
    }

    private void CreateRow(int index)
    {
        GameObject row = Instantiate(rowTemplate);
        row.transform.SetParent(rowTemplate.transform.parent, false);
        row.SetActive(true);

        Transform columns = row.transform;
        _rows[index] = row;
        _nameTexts[index] = columns.Find("Name").GetComponent<TextMeshProUGUI>();
        _danceTexts[index] = columns.Find("Dances").GetComponent<TextMeshProUGUI>();
        _stageTexts[index] = columns.Find("Stage").GetComponent<TextMeshProUGUI>();
        _boosterTexts[index] = columns.Find("Booster").GetComponent<TextMeshProUGUI>();
        _freshTexts[index] = columns.Find("Fresh/Label").GetComponent<TextMeshProUGUI>();
    }

    private string FormatDances(int dances)
    {
        if (dances == ClubDanceTracker.NoDance) return "ND";
        if (dances == ClubDanceTracker.Hidden) return "-";
        return dances.ToString();
    }

    private string[] SortedNames(VRCPlayerApi[] players)
    {
        string[] names = new string[players.Length];
        string[] keys = new string[players.Length];

        // Insertion sort, ignoring capitals. Udon has no Array.Sort with a comparer.
        for (int i = 0; i < players.Length; i++)
        {
            string playerName = players[i].displayName;
            string key = playerName.ToLower();

            int j = i - 1;
            while (j >= 0 && string.Compare(keys[j], key) > 0)
            {
                names[j + 1] = names[j];
                keys[j + 1] = keys[j];
                j--;
            }

            names[j + 1] = playerName;
            keys[j + 1] = key;
        }

        return names;
    }
}
