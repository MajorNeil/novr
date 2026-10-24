using System;
using System.Collections.Generic;
using System.Linq;
using NOVR.VrUi.Native;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// The game's single player missions, in its menu style: the mission groups down the left-hand panel, the missions of the
// chosen one in a list, and the one focused or picked described on the right. Select on a mission picks it and goes to
// Start; the tab bindings change group, and Left and Right on Tags filter by time of day or mission type. The missions
// are loaded and started as the game's own picker does (NativeSinglePlayerMissionPanel did the same), which is kept in
// step behind this, so Customize opens the game's own screen for the picked mission.
internal sealed class SinglePlayerScreen : MenuScreen
{
    private const float ContentX = 300f;
    private static readonly Vector2 ContentSize = new(1200f, 960f);
    private const float ListX = -270f;
    private const float ListWidth = 620f;
    private const float DetailsX = 330f;
    private const float DetailsWidth = 500f;
    private const int Rows = 12;
    private const float RowHeight = 50f;
    private const float RowStep = 56f;
    private const float RailButtonWidth = 440f;
    private const float RailButtonHeight = 50f;
    private const float RailStep = 58f;

    private enum Group
    {
        All,
        FreeFlight,
        Tutorials,
        Missions,
        User,
        Workshop
    }

    private static readonly (Group Group, string Label)[] Groups =
    {
        (Group.All, "ALL"),
        (Group.FreeFlight, "FREE FLIGHT"),
        (Group.Tutorials, "TUTORIALS"),
        (Group.Missions, "MISSIONS"),
        (Group.User, "USER"),
        (Group.Workshop, "WORKSHOP")
    };

    private static readonly (string Label, MissionTag? Tag)[] Tags =
    {
        ("ALL", null),
        ("PVE", MissionTag.PVE),
        ("PVP", MissionTag.PVP),
        ("DAWN", MissionTag.Dawn),
        ("DAY", MissionTag.Day),
        ("DUSK", MissionTag.Dusk),
        ("NIGHT", MissionTag.Night)
    };

    private readonly NativeGameActionAdapter _actions;
    private readonly List<(MissionKey Key, MissionQuickLoad Mission)> _missions = new();
    private readonly List<(MissionKey Key, MissionQuickLoad Mission)> _shown = new();
    private readonly Dictionary<Group, MenuButton> _groupButtons = new();
    private readonly MenuList _list;
    private readonly MenuSetting _tags;
    private readonly TextMeshProUGUI _position;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _subtitle;
    private readonly TextMeshProUGUI _description;
    private readonly TextMeshProUGUI _status;
    private readonly TextMeshProUGUI _prompts;
    private readonly MenuButton _customize;
    private readonly MenuButton _start;
    private Group _group = Group.All;
    private int _tag;
    private MissionKey? _picked;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    public SinglePlayerScreen(RectTransform canvas, NativeGameActionAdapter actions) : base(canvas, "NOVR Single Player")
    {
        _actions = actions;

        var rail = MenuKit.Panel(Root, "Groups", MainMenuScreen.RailCenter, MainMenuScreen.RailSize).rectTransform;
        var top = MainMenuScreen.RailSize.y / 2f;
        MenuKit.Text(rail, "Title", "SINGLE PLAYER", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 70f), new Vector2(RailButtonWidth, 64f), heading: true);
        MenuKit.Image(rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 112f), new Vector2(RailButtonWidth, 2f));

        var y = top - 160f;
        foreach (var (group, label) in Groups)
        {
            _groupButtons[group] = Add(MenuKit.Button(rail, label, label, new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight),
                () => SetGroup(group), MenuButton.Look.Secondary));
            y -= RailStep;
        }

        y -= 18f;
        Add(MenuKit.Button(rail, "User Folder", "OPEN USER FOLDER", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight),
            () => _actions.TryInvokeCurrentMenuButton("Open User Folder", "Open User Folder", "OPEN USER FOLDER"), MenuButton.Look.Secondary));
        y -= RailStep + 18f;
        Add(MenuKit.Button(rail, "Back", "BACK", new Vector2(0f, y), new Vector2(RailButtonWidth, 56f), Leave));

        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(RailButtonWidth, 34f));

        var content = MenuKit.Panel(Root, "Missions", new Vector2(ContentX, 0f), ContentSize).rectTransform;
        _tags = new MenuSetting(content, "TAGS", new Vector2(ListX, 400f), new Vector2(ListWidth, 56f), 200f,
            () => Tags[_tag].Label, direction => SetTag((_tag + direction + Tags.Length) % Tags.Length), null, () => _tags?.Refresh());
        Add(_tags.Button);

        var columns = new[] { new MenuList.Column(0f, ListWidth - 60f) };
        _list = new MenuList(content, "Mission List", new Vector2(ListX, 360f - (RowHeight + (Rows - 1) * RowStep) / 2f), ListWidth, Rows,
            RowHeight, RowStep, columns, FillRow);
        _list.Activated += Pick;
        foreach (var row in _list.Rows) Add(row);
        SetOrders(_list.Rows);

        _position = MenuKit.Text(content, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(ListX, -345f), new Vector2(ListWidth, 32f));

        _title = MenuKit.Text(content, "Mission", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(DetailsX, 400f), new Vector2(DetailsWidth, 64f), heading: true);
        _subtitle = MenuKit.Text(content, "Tags", "", MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.Left,
            new Vector2(DetailsX, 350f), new Vector2(DetailsWidth, 32f));
        _description = MenuKit.Text(content, "Description", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 40f), new Vector2(DetailsWidth, 580f), wrap: true);
        _status = MenuKit.Text(content, "Status", "", MenuStyle.SmallSize, MenuStyle.Danger, TextAlignmentOptions.Left,
            new Vector2(DetailsX, -275f), new Vector2(DetailsWidth, 32f));
        _customize = Add(MenuKit.Button(content, "Customize", "CUSTOMIZE", new Vector2(DetailsX, -330f), new Vector2(DetailsWidth, 56f),
            Customize, MenuButton.Look.Secondary));
        _start = Add(MenuKit.Button(content, "Start", "START MISSION", new Vector2(DetailsX, -400f), new Vector2(DetailsWidth, 62f), Start));
        ShowDetails(null);
    }

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        _status.text = "";
        // Every time, for missions saved or downloaded since.
        Load();
        RefreshGroups();
        ShowDetails(_picked);
    }

    protected override void OnTick()
    {
        if (_promptDevice == MenuInput.LastDevice) return;

        _promptDevice = MenuInput.LastDevice;
        _prompts.text = $"{Prompt(MenuCommand.PreviousTab)}/{Prompt(MenuCommand.NextTab)} Group   {Prompt(MenuCommand.Back)} Back";
    }

    protected override MenuButton? FirstFocus()
    {
        var picked = _shown.FindIndex(entry => Same(entry.Key, _picked));
        if (picked >= 0)
        {
            _list.ScrollTo(picked);
            return _list.Rows[picked - _list.Offset];
        }

        return _list.Count > 0 ? _list.Rows[0] : _groupButtons[_group];
    }

    protected override bool OnBack()
    {
        var focused = Focused;
        if (focused != null && !_groupButtons.ContainsValue(focused) && focused.transform.IsChildOf(_list.Rect.parent))
        {
            Focus(_groupButtons[_group], sound: false);
            return true;
        }

        Leave();
        return true;
    }

    protected override void OnTab(int direction)
    {
        var index = Array.FindIndex(Groups, entry => entry.Group == _group);
        SetGroup(Groups[(index + direction + Groups.Length) % Groups.Length].Group);
        MenuSounds.Move();
    }

    protected override bool OnOrderEnd(List<MenuButton> order, int step)
    {
        if (!_list.Scroll(step, sound: true)) return false;

        RefreshPosition();
        ShowDetails(EntryKey(_list.EntryOf(Focused)));
        return true;
    }

    // The focused mission is described while the focus is on the list; the picked one otherwise.
    protected override void OnFocused(MenuButton? button)
    {
        var entry = _list.EntryOf(button);
        ShowDetails(entry >= 0 ? _shown[entry].Key : _picked);
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    private void Load()
    {
        _missions.Clear();
        try
        {
            MissionGroup.Init();
            foreach (var entry in MissionSaveLoad.QuickLoadMany(MissionGroup.All.GetMissions()))
            {
                if (HasTag(entry.mission, MissionTag.SinglePlayer)) _missions.Add((entry.key, entry.mission));
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not load the single player missions: {exception.Message}");
            _status.text = "The missions couldn't be loaded.";
        }

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        _shown.Clear();
        foreach (var entry in _missions)
        {
            if (InGroup(entry.Key, _group) && HasTag(entry.Mission, Tags[_tag].Tag)) _shown.Add(entry);
        }

        _list.Selected = _shown.FindIndex(entry => Same(entry.Key, _picked));
        _list.SetCount(_shown.Count);
        _list.Reset();
        RefreshPosition();
    }

    private void SetGroup(Group group)
    {
        _group = group;
        ApplyFilters();
        RefreshGroups();
    }

    private void SetTag(int tag)
    {
        _tag = tag;
        ApplyFilters();
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        foreach (var (group, label) in Groups)
        {
            var button = _groupButtons[group];
            button.Highlight = group == _group;
            var count = _missions.Count(entry => InGroup(entry.Key, group) && HasTag(entry.Mission, Tags[_tag].Tag));
            button.SetLabel($"{label}  <color=#{MenuStyle.Hex(MenuStyle.TextDim)}>{count}</color>");
        }

        _tags.Refresh();
    }

    private void RefreshPosition()
    {
        _position.text = _shown.Count == 0 ? "NO MISSIONS HERE" : _list.Position;
    }

    private void FillRow(int entry, TextMeshProUGUI[] cells)
    {
        var key = _shown[entry].Key;
        cells[0].text = key.Name;
        cells[0].color = Same(key, _picked) ? MenuStyle.Accent : MenuStyle.Text;
    }

    private void Pick(int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        _picked = _shown[entry].Key;
        _status.text = "";
        _actions.TrySelectOriginalMission(_shown[entry].Key);
        _list.Selected = entry;
        _list.Refresh();
        ShowDetails(_picked);
        Focus(_start, sound: false);
    }

    private void ShowDetails(MissionKey? key)
    {
        var entry = _missions.FindIndex(candidate => Same(candidate.Key, key));
        if (entry < 0)
        {
            _title.text = _shown.Count > 0 ? "PICK A MISSION" : "";
            _subtitle.text = "";
            _description.text = "";
        }
        else
        {
            var mission = _missions[entry];
            _title.text = mission.Key.Name;
            var tags = mission.Mission.missionSettings.Tags;
            _subtitle.text = tags != null ? string.Join("   ", tags.Select(tag => tag.Tag.ToUpperInvariant())) : "";
            _description.text = mission.Mission.missionSettings.description ?? "";
        }

        _start.interactable = _picked != null;
        _customize.interactable = _picked != null;
    }

    private MissionKey? EntryKey(int entry) => entry >= 0 && entry < _shown.Count ? _shown[entry].Key : _picked;

    private void Start()
    {
        if (_picked is not { } picked) return;

        if (!picked.TryLoad(out var mission, out var error))
        {
            _status.text = error;
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not load mission '{picked.Name}': {error}");
            return;
        }

        MissionManager.SetMission(mission, checkIfSame: false);
        NetworkManagerNuclearOption.i.StartHost(new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));
    }

    // The game's own screen for it, which shows instead of this until it is closed.
    private void Customize()
    {
        if (_picked is not { } picked) return;

        _actions.TrySelectOriginalMission(picked);
        _actions.TryInvokeCurrentMenuButton("Customize Mission", "Customize Mission", "CUSTOMIZE MISSION");
    }

    private void Leave()
    {
        _actions.TryInvokeCurrentMenuButton("Back", "< BACK", "BACK", "MenuExit_Button");
    }

    private static bool HasTag(MissionQuickLoad mission, MissionTag? tag)
    {
        if (tag == null) return true;

        var tags = mission.missionSettings.Tags;
        return tags != null && tags.Any(existing => existing.Equals(tag.Value));
    }

    private static bool InGroup(MissionKey key, Group group) => group switch
    {
        Group.FreeFlight => SameGroup(key.Group, MissionGroup.Default),
        Group.Tutorials => SameGroup(key.Group, MissionGroup.Tutorial),
        Group.Missions => SameGroup(key.Group, MissionGroup.BuiltIn),
        Group.User => SameGroup(key.Group, MissionGroup.User),
        Group.Workshop => SameGroup(key.Group, MissionGroup.Workshop),
        _ => true
    };

    private static bool SameGroup(MissionGroup? left, MissionGroup? right) =>
        ReferenceEquals(left, right) || string.Equals(left?.Name, right?.Name, StringComparison.OrdinalIgnoreCase);

    private static bool Same(MissionKey left, MissionKey? right) =>
        right is { } other && SameGroup(left.Group, other.Group) && string.Equals(left.Name, other.Name, StringComparison.Ordinal);
}
