using System;
using System.Collections.Generic;
using System.Linq;
using NOVR.VrUi.Native;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// The game's single player missions, built and used like the settings (RailScreen): the mission groups down the
// left-hand panel, and the missions of the chosen one as rows beside it, under a Tags row that filters them. The focused
// mission is described on the right; Select on it opens Start Mission and Customize. The missions are loaded and started
// as the game's own picker does (NativeSinglePlayerMissionPanel did the same), which is kept in step behind this, so
// Customize opens the game's own screen for the mission.
internal sealed class SinglePlayerScreen : RailScreen
{
    private const int ListRows = 10;

    private enum Group
    {
        All,
        FreeFlight,
        Tutorials,
        Missions,
        User,
        Workshop
    }

    private static readonly (Group Group, string Label, string Title)[] Groups =
    {
        (Group.All, "ALL", "ALL MISSIONS"),
        (Group.FreeFlight, "FREE FLIGHT", "FREE FLIGHT"),
        (Group.Tutorials, "TUTORIALS", "TUTORIALS"),
        (Group.Missions, "MISSIONS", "MISSIONS"),
        (Group.User, "USER", "USER MISSIONS"),
        (Group.Workshop, "WORKSHOP", "WORKSHOP MISSIONS")
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
    private readonly Section _section;
    private readonly MenuList _list;
    private Group _group = Group.All;
    private int _tag;

    public SinglePlayerScreen(RectTransform canvas, NativeGameActionAdapter actions) : base(canvas, "NOVR Single Player", "SINGLE PLAYER")
    {
        _actions = actions;
        foreach (var (group, label, _) in Groups)
        {
            _groupButtons[group] = AddRailButton(label, () => SetGroup(group));
        }

        AddRailButton("OPEN USER FOLDER", () => _actions.TryInvokeCurrentMenuButton("Open User Folder", "Open User Folder", "OPEN USER FOLDER"),
            gapBefore: true);
        AddRailBack();

        _section = AddSection("ALL MISSIONS");
        AddSetting(_section, "TAGS", () => Tags[_tag].Label, direction => SetTag((_tag + direction + Tags.Length) % Tags.Length),
            description: "Only the missions with this tag: player against environment or player against player, or the time of day.");
        _list = AddList(_section, ListRows, FillRow);
        ShowSection(_section);
        CurrentRailButton = _groupButtons[_group];
        RefreshGroups();
    }

    protected override void OnShown()
    {
        base.OnShown();
        SetStatus("");
        // Every time, for missions saved or downloaded since.
        Load();
        RefreshGroups();
    }

    protected override void OnTab(int direction)
    {
        var index = Array.FindIndex(Groups, entry => entry.Group == _group);
        SetGroup(Groups[(index + direction + Groups.Length) % Groups.Length].Group);
        Focus(_groupButtons[_group], sound: false);
        MenuSounds.Move();
    }

    protected override void Leave() => _actions.TryInvokeCurrentMenuButton("Back", "< BACK", "BACK", "MenuExit_Button");

    protected override void Describe(Section section, MenuButton? row, int entry)
    {
        if (entry >= 0 && entry < _shown.Count)
        {
            var mission = _shown[entry];
            ShowDetails(mission.Key.Name, TagLine(mission.Mission), GroupName(mission.Key), mission.Mission.missionSettings.description ?? "");
            return;
        }

        if (row != null)
        {
            base.Describe(section, row, entry);
            return;
        }

        ShowDetails(Groups[Array.FindIndex(Groups, item => item.Group == _group)].Title, $"{_shown.Count} MISSIONS", "",
            "Pick a mission from the list, then Start Mission or Customize.");
    }

    protected override void Open(Section section, int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        var mission = _shown[entry];
        _actions.TrySelectOriginalMission(mission.Key);
        OpenPopup(mission.Key.Name, TagLine(mission.Mission), mission.Mission.missionSettings.description ?? "", picture: false, fieldLabel: null,
            ("START MISSION", () => Start(mission.Key)),
            ("CUSTOMIZE", () => Customize(mission.Key)));
    }

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
            SetStatus("The missions couldn't be loaded.");
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

        _list.SetCount(_shown.Count);
        _list.Reset();
        SetCount(_shown.Count == 0 ? "NO MISSIONS" : $"{_shown.Count} MISSIONS");
        RefreshDetails();
    }

    private void SetGroup(Group group)
    {
        _group = group;
        CurrentRailButton = _groupButtons[group];
        _section.Title = Groups[Array.FindIndex(Groups, entry => entry.Group == group)].Title;
        ShowSection(_section);
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
        foreach (var (group, label, _) in Groups)
        {
            var button = _groupButtons[group];
            button.Highlight = group == _group;
            var count = _missions.Count(entry => InGroup(entry.Key, group) && HasTag(entry.Mission, Tags[_tag].Tag));
            button.SetLabel($"{label}  <color=#{MenuStyle.Hex(MenuStyle.TextDim)}>{count}</color>");
        }
    }

    private void FillRow(int entry, TextMeshProUGUI name, TextMeshProUGUI value)
    {
        var mission = _shown[entry];
        name.text = mission.Key.Name;
        value.text = ShortTags(mission.Mission);
    }

    private bool Start(MissionKey key)
    {
        if (!key.TryLoad(out var mission, out var error))
        {
            SetPopupStatus(error);
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not load mission '{key.Name}': {error}");
            return false;
        }

        MissionManager.SetMission(mission, checkIfSame: false);
        NetworkManagerNuclearOption.i.StartHost(new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));
        return true;
    }

    // The game's own screen for it, which shows instead of this until it is closed.
    private bool Customize(MissionKey key)
    {
        _actions.TrySelectOriginalMission(key);
        _actions.TryInvokeCurrentMenuButton("Customize Mission", "Customize Mission", "CUSTOMIZE MISSION");
        return true;
    }

    private static string TagLine(MissionQuickLoad mission)
    {
        var tags = mission.missionSettings.Tags;
        return tags != null ? string.Join("   ", tags.Select(tag => tag.Tag.ToUpperInvariant())) : "";
    }

    // The tags that tell missions apart in the list: the mission type and the time of day.
    private static string ShortTags(MissionQuickLoad mission)
    {
        var shown = new List<string>();
        foreach (var (label, tag) in Tags)
        {
            if (tag != null && HasTag(mission, tag)) shown.Add(label);
        }

        return string.Join(" ", shown);
    }

    private static string GroupName(MissionKey key)
    {
        foreach (var (group, label, _) in Groups)
        {
            if (group != Group.All && InGroup(key, group)) return label;
        }

        return "";
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
}
