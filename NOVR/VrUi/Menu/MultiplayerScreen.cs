using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NOVR.VrUi.Native;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NOVR.VrUi.Menu;

// Multiplayer in the game's menu style, laid out and used like the single player screen: the sections (server browser,
// filters, creating a lobby) down the left-hand panel, and the chosen one in the panel to its right. The browser is one
// column (how the lobbies are sorted, a search, then the lobbies) beside the focused or picked lobby and its Join.
// Up and Down run down a column; Right goes on to Join, Left back to the list and then to the sections; Back goes the
// same way out. Select or Right on a lobby picks it and goes to Join; Select on the picked one joins. The lobbies are the ones the game's own
// browser has found (it keeps looking behind this; Refresh asks it again). They keep their places while their player
// counts change, so the focus stays on the lobby it is on; a change of sort, or Refresh, sorts them again.
// The filters are settings rows, as in the settings. Creating a lobby takes a multiplayer mission from the list, then a
// name, a size, who can see it and a password, and hosts it as the game does (NativeMultiplayerPanel did the same).
internal sealed class MultiplayerScreen : MenuScreen
{
    private const float ContentX = 300f;
    private static readonly Vector2 ContentSize = new(1200f, 960f);
    private const float TitleY = 410f;
    private const float RailButtonWidth = 440f;
    private const float RailButtonHeight = 54f;
    private const float RailStep = 62f;
    private const float RefreshSeconds = 1f;
    private const float ListX = -240f;
    private const float ListWidth = 680f;
    private const float DetailsX = 370f;
    private const float DetailsWidth = 420f;
    private const float RowHeight = 50f;
    private const float RowStep = 54f;
    private static readonly Vector2 SettingSize = new(ListWidth, 56f);
    private static readonly float[] FilterColumnX = { -296f, 296f };
    private static readonly Vector2 FilterRowSize = new(570f, 56f);
    private const float FilterFirstY = 330f;
    private const float FilterRowStep = 64f;
    private const int FiltersPerColumn = 7;
    private const float HostListX = -300f;
    private const float HostColumnX = 300f;
    private const float HostWidth = 560f;

    private static readonly FieldInfo? ShownField = typeof(LobbyListItem).GetField("shown", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly int?[] MaxPings = { null, 75, 150, 250, 400 };
    private static readonly (string Label, int? Ping)[] Distances = { ("ANY", null), ("NEARBY", 75), ("REGIONAL", 150), ("WORLDWIDE", 300) };
    private static readonly (HostLobbyVisibility Visibility, string Label)[] Visibilities =
    {
        (HostLobbyVisibility.Public, "PUBLIC"),
        (HostLobbyVisibility.FriendsOnly, "FRIENDS"),
        (HostLobbyVisibility.Private, "PRIVATE")
    };

    private static readonly (Sort Sort, bool Ascending, string Label)[] Sorts =
    {
        (Sort.Players, false, "MOST PLAYERS"),
        (Sort.Ping, true, "LOWEST PING"),
        (Sort.Lobby, true, "LOBBY NAME"),
        (Sort.Mission, true, "MISSION"),
        (Sort.Players, true, "FEWEST PLAYERS")
    };

    // The list's columns, from a row's centre; the labels over them share them.
    private static readonly MenuList.Column[] Columns =
    {
        new(-170f, 320f),
        new(75f, 160f, dim: true),
        new(210f, 100f, TextAlignmentOptions.Center),
        new(290f, 80f, TextAlignmentOptions.Center, dim: true)
    };

    private enum View
    {
        Browser,
        Filters,
        Host
    }

    private enum Sort
    {
        Lobby,
        Mission,
        Players,
        Ping
    }

    private enum HostLobbyVisibility
    {
        Public,
        FriendsOnly,
        Private
    }

    private sealed class Lobby
    {
        public Lobby(LobbyListItem item)
        {
            Instance = item.lobby;
            Name = string.IsNullOrWhiteSpace(item.LobbyName) ? "Unnamed Lobby" : item.LobbyName.Trim();
            Mission = string.IsNullOrWhiteSpace(item.MissionName) ? "Unknown Mission" : item.MissionName.Trim();
            Map = string.IsNullOrWhiteSpace(item.MapName) ? "Unknown Map" : item.MapName.Trim();
            Ping = item.Ping;
            Full = item.IsFull;
            Dedicated = item.lobby.DedicatedServer || item.IsServer;
            Modded = item.lobby.ModdedServer;
            Password = item.lobby.IsPasswordProtected(out _);
            Type = item.lobby.MissionPvpType;
            Description = string.IsNullOrWhiteSpace(item.lobby.MissionDescriptionSanitized) ? "" : item.lobby.MissionDescriptionSanitized;
            if (item.lobby.GetPlayerCounts(out var players, out var maxPlayers))
            {
                Players = players;
                PlayersText = $"{players}/{maxPlayers}";
            }
            else
            {
                Players = item.PlayerCount;
                PlayersText = $"{item.PlayerCount}";
            }
        }

        public LobbyInstance Instance { get; }
        public string Name { get; }
        public string Mission { get; }
        public string Map { get; }
        public int? Ping { get; }
        public bool Full { get; }
        public bool Dedicated { get; }
        public bool Modded { get; }
        public bool Password { get; }
        public MissionPvpType Type { get; }
        public string Description { get; }
        public int Players { get; }
        public string PlayersText { get; }
        public string PingText => Ping.HasValue ? $"{Ping.Value} ms" : "--";

        public string TypeText => Type switch
        {
            MissionPvpType.Pve => "PVE",
            MissionPvpType.Pvp => "PVP",
            _ => "ANY"
        };
    }

    private readonly NativeGameActionAdapter _actions;
    private readonly Dictionary<View, MenuButton> _viewButtons = new();
    private readonly Dictionary<View, RectTransform> _views = new();
    private readonly Dictionary<View, string> _titles = new()
    {
        [View.Browser] = "SERVER BROWSER",
        [View.Filters] = "FILTERS",
        [View.Host] = "CREATE LOBBY"
    };
    private readonly List<MenuButton> _rail = new();
    private readonly List<MenuButton> _browserColumn = new();
    private readonly List<MenuButton> _details = new();
    private readonly List<MenuButton> _filterOrder = new();
    private readonly List<MenuButton> _hostColumn = new();
    private readonly List<Lobby> _lobbies = new();
    private List<Lobby> _shown = new();
    private readonly List<MenuSetting> _filters = new();
    private readonly List<(MissionKey Key, MissionQuickLoad Mission)> _hostMissions = new();
    private readonly List<MenuSetting> _hostSettings = new();
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _count;
    private readonly MenuSetting _sort;
    private readonly MenuField _search;
    private readonly MenuList _list;
    private readonly TextMeshProUGUI _position;
    private readonly TextMeshProUGUI _status;
    private readonly TextMeshProUGUI _lobbyName;
    private readonly TextMeshProUGUI _mission;
    private readonly TextMeshProUGUI _facts;
    private readonly TextMeshProUGUI _description;
    private readonly MenuButton _join;
    private readonly TextMeshProUGUI _filtersSummary;
    private readonly MenuList _hostList;
    private readonly TextMeshProUGUI _hostPosition;
    private readonly TextMeshProUGUI _hostTitle;
    private readonly TextMeshProUGUI _hostDescription;
    private readonly MenuField _hostName;
    private readonly MenuField _hostPassword;
    private readonly TextMeshProUGUI _hostStatus;
    private readonly MenuButton _host;
    private readonly TextMeshProUGUI _prompts;
    private readonly RectTransform _popup;
    private readonly TextMeshProUGUI _popupTitle;
    private readonly TextMeshProUGUI _popupStatus;
    private readonly MenuField _popupPassword;
    private readonly List<MenuButton> _popupButtons = new();

    private View _view = View.Browser;
    private int _sortIndex;
    private bool _showPve = true;
    private bool _showPvp = true;
    private bool _showOpen = true;
    private bool _showPassword = true;
    private bool _showVanilla = true;
    private bool _showModded = true;
    private bool _showDedicated = true;
    private bool _showPlayerHosted = true;
    private bool _hideFull;
    private bool _hideEmpty;
    private int _maxPing;
    private int _distance;
    private LobbyInstance? _picked;
    private Lobby? _joining;
    private MenuButton? _lastListFocus;
    private MenuButton? _lastHostListFocus;
    private int _hostPicked = -1;
    private int _hostMaxPlayers = 8;
    private int _hostVisibility;
    private bool _hostUsesPassword;
    private bool _hostMissionsLoaded;
    private bool _hosting;
    private float _nextRefresh;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    public MultiplayerScreen(RectTransform canvas, NativeGameActionAdapter actions) : base(canvas, "NOVR Multiplayer")
    {
        _actions = actions;

        var rail = MenuKit.Panel(Root, "Sections", MainMenuScreen.RailCenter, MainMenuScreen.RailSize).rectTransform;
        var top = MainMenuScreen.RailSize.y / 2f;
        MenuKit.Text(rail, "Title", "MULTIPLAYER", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 70f), new Vector2(RailButtonWidth, 64f), heading: true);
        MenuKit.Image(rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 112f), new Vector2(RailButtonWidth, 2f));

        var y = top - 160f;
        AddViewButton(rail, View.Browser, ref y);
        AddViewButton(rail, View.Filters, ref y);
        AddViewButton(rail, View.Host, ref y);
        y -= 18f;
        _rail.Add(Add(MenuKit.Button(rail, "Refresh", "REFRESH", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), Refresh,
            MenuButton.Look.Secondary)));
        y -= RailStep + 18f;
        _rail.Add(Add(MenuKit.Button(rail, "Back", "BACK", new Vector2(0f, y), new Vector2(RailButtonWidth, 56f), Leave)));
        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(RailButtonWidth, 34f));

        var content = MenuKit.Panel(Root, "Content", new Vector2(ContentX, 0f), ContentSize).rectTransform;
        _title = MenuKit.Text(content, "Title", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f), heading: true);
        _count = MenuKit.Text(content, "Count", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Right,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f));

        // The server browser: one column of how they're sorted, a search and the lobbies, beside the lobby and Join.
        var browser = AddView(content, View.Browser);
        _sort = new MenuSetting(browser, "SORT", new Vector2(ListX, 340f), SettingSize, 280f, () => Sorts[_sortIndex].Label,
            direction =>
            {
                _sortIndex = Wrap(_sortIndex + direction, Sorts.Length);
                ApplyFilters(resort: true);
            }, null, () => _sort?.Refresh());
        _search = new MenuField(browser, "Search", "SEARCH", "Lobby, mission or map", new Vector2(ListX, 276f), SettingSize, 190f);
        _search.Changed += _ => ApplyFilters(resort: true);
        _browserColumn.Add(Add(_sort.Button));
        _browserColumn.Add(Add(_search.Button));

        var rowX = ListX - 8f;
        string[] labels = { "LOBBY", "MISSION", "PLAYERS", "PING" };
        for (var index = 0; index < labels.Length; index++)
        {
            MenuKit.Text(browser, labels[index], labels[index], MenuStyle.SmallSize, MenuStyle.Accent, Columns[index].Alignment,
                new Vector2(rowX + Columns[index].X, 228f), new Vector2(Columns[index].Width, 32f), heading: true);
        }

        _list = new MenuList(browser, "Lobby List", ListCenter(204f, 9), ListWidth, 9, RowHeight, RowStep, Columns, FillLobby);
        _list.Activated += entry => PickLobby(entry, joinIfPicked: true);
        foreach (var row in _list.Rows) _browserColumn.Add(Add(row));
        _position = MenuKit.Text(browser, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(ListX, -306f), new Vector2(ListWidth, 32f));
        _status = MenuKit.Text(browser, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(ListX, -385f), new Vector2(ListWidth, 70f), wrap: true);

        _lobbyName = MenuKit.Text(browser, "Lobby", "", MenuStyle.ButtonSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 330f), new Vector2(DetailsWidth, 90f), heading: true, wrap: true);
        _mission = MenuKit.Text(browser, "Mission", "", MenuStyle.BodySize, MenuStyle.Accent, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 252f), new Vector2(DetailsWidth, 64f), wrap: true);
        _facts = MenuKit.Text(browser, "Facts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 165f), new Vector2(DetailsWidth, 100f), wrap: true);
        _description = MenuKit.Text(browser, "Description", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, -70f), new Vector2(DetailsWidth, 340f), wrap: true);
        _join = Add(MenuKit.Button(browser, "Join", "JOIN", new Vector2(DetailsX, -400f), new Vector2(DetailsWidth, 62f), Join));
        _details.Add(_join);

        // The filters, as settings rows.
        var filters = AddView(content, View.Filters);
        AddToggle(filters, "PVE", () => _showPve, value => _showPve = value);
        AddToggle(filters, "PVP", () => _showPvp, value => _showPvp = value);
        AddToggle(filters, "OPEN", () => _showOpen, value => _showOpen = value);
        AddToggle(filters, "PASSWORD", () => _showPassword, value => _showPassword = value);
        AddToggle(filters, "VANILLA", () => _showVanilla, value => _showVanilla = value);
        AddToggle(filters, "MODDED", () => _showModded, value => _showModded = value);
        AddToggle(filters, "DEDICATED", () => _showDedicated, value => _showDedicated = value);
        AddToggle(filters, "PLAYER HOSTED", () => _showPlayerHosted, value => _showPlayerHosted = value);
        AddToggle(filters, "HIDE FULL", () => _hideFull, value => _hideFull = value);
        AddToggle(filters, "HIDE EMPTY", () => _hideEmpty, value => _hideEmpty = value);
        AddFilter(filters, "MAX PING", () => MaxPings[_maxPing] is { } ping ? $"{ping} MS" : "ANY",
            direction => _maxPing = Wrap(_maxPing + direction, MaxPings.Length));
        AddFilter(filters, "DISTANCE", () => Distances[_distance].Label, direction => _distance = Wrap(_distance + direction, Distances.Length));
        _filtersSummary = MenuKit.Text(filters, "Summary", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -ContentSize.y / 2f + 70f), new Vector2(ContentSize.x - 80f, 34f));

        // Creating a lobby: the mission list, beside a column of its settings ending in Host.
        var host = AddView(content, View.Host);
        _hostList = new MenuList(host, "Host Missions", new Vector2(HostListX, ListCenter(340f, 10).y), HostWidth, 10, RowHeight, RowStep,
            new[] { new MenuList.Column(0f, HostWidth - 60f) }, FillHostMission);
        _hostList.Activated += PickHostMission;
        foreach (var row in _hostList.Rows) Add(row);
        _hostPosition = MenuKit.Text(host, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(HostListX, -226f), new Vector2(HostWidth, 32f));
        _hostTitle = MenuKit.Text(host, "Mission", "", MenuStyle.BodySize, MenuStyle.Accent, TextAlignmentOptions.Left,
            new Vector2(HostColumnX, 340f), new Vector2(HostWidth, 40f));
        _hostDescription = MenuKit.Text(host, "Description", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(HostColumnX, 255f), new Vector2(HostWidth, 120f), wrap: true);
        _hostName = new MenuField(host, "Lobby Name", "NAME", "Lobby name", new Vector2(HostColumnX, 150f), new Vector2(HostWidth, 56f), 150f,
            characterLimit: 128);
        _hostColumn.Add(Add(_hostName.Button));
        AddHostSetting(host, "MAX PLAYERS", 86f, () => $"{_hostMaxPlayers}", direction =>
            _hostMaxPlayers = Mathf.Clamp(_hostMaxPlayers + direction, 2, Mathf.Max(2, _actions.GetTooManyPlayerLimit())));
        AddHostSetting(host, "LOBBY TYPE", 22f, () => Visibilities[_hostVisibility].Label,
            direction => _hostVisibility = Wrap(_hostVisibility + direction, Visibilities.Length));
        AddHostSetting(host, "PASSWORD", -42f, () => MenuSetting.OnOff(_hostUsesPassword), _ => _hostUsesPassword = !_hostUsesPassword);
        _hostPassword = new MenuField(host, "Host Password", "PASSWORD", "Password", new Vector2(HostColumnX, -106f), new Vector2(HostWidth, 56f),
            220f, password: true);
        _hostColumn.Add(Add(_hostPassword.Button));
        _hostStatus = MenuKit.Text(host, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(HostColumnX, -215f), new Vector2(HostWidth, 100f), wrap: true);
        _host = Add(MenuKit.Button(host, "Host", "HOST LOBBY", new Vector2(HostColumnX, -400f), new Vector2(HostWidth, 62f), HostLobby));
        _hostColumn.Add(_host);

        SetOrders(_rail, _browserColumn, _details, _filterOrder, _hostList.Rows, _hostColumn);

        // Asked for a password before joining.
        _popup = MenuKit.Rect(Root, "Password", Vector2.zero, canvas.sizeDelta);
        var shade = MenuKit.Image(_popup, "Shade", new Color(0f, 0f, 0f, 0.55f), Vector2.zero, Vector2.zero);
        MenuKit.Stretch(shade.rectTransform);
        shade.raycastTarget = true;
        var box = MenuKit.Panel(_popup, "Box", new Vector2(ContentX, 0f), new Vector2(720f, 380f)).rectTransform;
        _popupTitle = MenuKit.Text(box, "Title", "PASSWORD", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, 130f), new Vector2(640f, 64f), heading: true);
        _popupPassword = new MenuField(box, "Join Password", "PASSWORD", "Server password", new Vector2(0f, 40f), new Vector2(640f, 56f), 220f,
            password: true);
        _popupButtons.Add(Add(_popupPassword.Button));
        // Enter joins, as typing the password is the last thing to do.
        _popupPassword.Input.onEndEdit.AddListener(_ =>
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) JoinWithPassword();
        });
        _popupStatus = MenuKit.Text(box, "Status", "", MenuStyle.SmallSize, MenuStyle.Danger, TextAlignmentOptions.Left,
            new Vector2(0f, -30f), new Vector2(640f, 34f));
        _popupButtons.Add(Add(MenuKit.Button(box, "Cancel", "CANCEL", new Vector2(-165f, -120f), new Vector2(310f, 58f), ClosePopup,
            MenuButton.Look.Secondary)));
        _popupButtons.Add(Add(MenuKit.Button(box, "Join", "JOIN", new Vector2(165f, -120f), new Vector2(310f, 58f), JoinWithPassword)));
        _popup.gameObject.SetActive(false);

        ShowView(View.Browser);
    }

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        ClosePopup();
        _picked = null;
        _lastListFocus = null;
        ShowView(View.Browser);
        _status.text = "Looking for lobbies.";
        _actions.TryRefreshMultiplayerLobbies();
        ReadLobbies(resort: true);
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        Focus(_viewButtons[View.Browser], sound: false);
    }

    protected override void OnHidden()
    {
        ClosePopup();
    }

    protected override void OnTick()
    {
        if (_promptDevice != MenuInput.LastDevice)
        {
            _promptDevice = MenuInput.LastDevice;
            _prompts.text = $"{Prompt(MenuCommand.PreviousTab)}/{Prompt(MenuCommand.NextTab)} Section   {Prompt(MenuCommand.Back)} Back";
        }

        if (Time.unscaledTime < _nextRefresh) return;

        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        ReadLobbies(resort: false);
    }

    protected override MenuButton? FirstFocus() => _viewButtons[_view];

    protected override bool OnBack()
    {
        if (_popup.gameObject.activeSelf)
        {
            ClosePopup();
            return true;
        }

        var focused = Focused;
        if (focused == _join && CanFocus(_lastListFocus))
        {
            Focus(_lastListFocus, sound: false);
            return true;
        }

        if (focused != null && !_rail.Contains(focused))
        {
            Focus(_viewButtons[_view], sound: false);
            return true;
        }

        if (_view != View.Browser)
        {
            ShowView(View.Browser);
            Focus(_viewButtons[View.Browser], sound: false);
            return true;
        }

        Leave();
        return true;
    }

    protected override void OnTab(int direction)
    {
        if (_popup.gameObject.activeSelf) return;

        ShowView((View)Wrap((int)_view + direction, 3));
        Focus(_viewButtons[_view], sound: false);
        MenuSounds.Move();
    }

    protected override bool OnOrderStep(List<MenuButton> order, MenuButton focused, int step)
    {
        var list = order == _browserColumn ? _list : order == _hostList.Rows ? _hostList : null;
        if (list == null || !list.StepPast(focused, step)) return false;

        RefreshPositions();
        if (list == _list) ShowLobby(EntryLobby(_list.EntryOf(Focused)));
        return true;
    }

    // The columns end there; across is Left and Right.
    protected override bool OnOrderEnd(List<MenuButton> order, int step) => true;

    // From the sections into the section shown and back; from a list to what it leads to (Join, the lobby's settings)
    // and back to the list.
    protected override bool OnSideways(MenuButton focused, int direction)
    {
        if (_popup.gameObject.activeSelf) return false;

        if (_rail.Contains(focused))
        {
            if (direction > 0) EnterView();
            return true;
        }

        if (_browserColumn.Contains(focused))
        {
            var entry = _list.EntryOf(focused);
            if (direction < 0) Focus(_viewButtons[_view], sound: true);
            else if (entry >= 0) PickLobby(entry, joinIfPicked: false);
            else FocusFirst(_join);
            return true;
        }

        if (focused == _join)
        {
            if (direction < 0) FocusFirst(_lastListFocus, _list.Rows[0], _search.Button);
            return true;
        }

        if (_hostList.Rows.Contains(focused))
        {
            if (direction < 0) Focus(_viewButtons[_view], sound: true);
            else PickHostMission(_hostList.EntryOf(focused));
            return true;
        }

        if (_hostColumn.Contains(focused))
        {
            if (direction < 0) FocusFirst(_lastHostListFocus, _hostList.Rows[0]);
            return true;
        }

        return false;
    }

    protected override void OnFocused(MenuButton? button)
    {
        if (button == null) return;

        if (_browserColumn.Contains(button)) _lastListFocus = button;
        if (_hostList.Rows.Contains(button)) _lastHostListFocus = button;
        if (_view != View.Browser || !_browserColumn.Contains(button) && button != _join) return;

        var entry = _list.EntryOf(button);
        ShowLobby(entry >= 0 ? _shown[entry] : Picked());
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    private static Vector2 ListCenter(float top, int rows) => new(ListX, top - (RowHeight + (rows - 1) * RowStep) / 2f);

    private RectTransform AddView(RectTransform content, View view)
    {
        var rect = MenuKit.Rect(content, $"{view} View", Vector2.zero, ContentSize);
        _views[view] = rect;
        return rect;
    }

    private void AddViewButton(RectTransform rail, View view, ref float y)
    {
        var button = Add(MenuKit.Button(rail, _titles[view], _titles[view], new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), () =>
        {
            ShowView(view);
            EnterView();
        }, MenuButton.Look.Secondary));
        _viewButtons[view] = button;
        _rail.Add(button);
        y -= RailStep;
    }

    private void ShowView(View view)
    {
        _view = view;
        foreach (var pair in _views) pair.Value.gameObject.SetActive(pair.Key == view);
        foreach (var pair in _viewButtons) pair.Value.Highlight = pair.Key == view;
        _title.text = _titles[view];
        if (view == View.Host)
        {
            if (!_hostMissionsLoaded) LoadHostMissions();
            RefreshHost();
        }

        RefreshFilters();
        RefreshCount();
    }

    // Into the section shown: where the focus was in it, else its first row.
    private void EnterView()
    {
        switch (_view)
        {
            case View.Browser:
                FocusFirst(_lastListFocus, _list.Rows[0], _sort.Button);
                break;
            case View.Filters:
                FocusFirst(_filterOrder[0]);
                break;
            default:
                FocusFirst(_lastHostListFocus, _hostList.Rows[0], _hostName.Button);
                break;
        }
    }

    // The game's own browser, as it has it now.
    private void ReadLobbies(bool resort)
    {
        _lobbies.Clear();
        if (!_actions.TryGetActiveLobbyList(out var lobbyList) || lobbyList == null)
        {
            _status.text = "Waiting for the game's lobby list.";
            ApplyFilters(resort);
            return;
        }

        foreach (var item in lobbyList.GetComponentsInChildren<LobbyListItem>(true))
        {
            if (item == null || item.lobby == null || !IsShown(item)) continue;

            try
            {
                _lobbies.Add(new Lobby(item));
            }
            catch (Exception)
            {
                // A lobby still filling in its details; it is read again in a moment.
            }
        }

        ApplyFilters(resort);
    }

    private static bool IsShown(LobbyListItem item) =>
        ShownField?.GetValue(item) is bool shown ? shown : item.gameObject.activeInHierarchy;

    // Sorted again from the top (resort), or kept in the order shown with new lobbies after and gone ones dropped.
    private void ApplyFilters(bool resort)
    {
        var passing = _lobbies.FindAll(Matches);
        if (resort)
        {
            passing.Sort(Compare);
            _shown = passing;
            _list.SetCount(_shown.Count);
            _list.Reset();
        }
        else
        {
            var kept = new List<Lobby>(passing.Count);
            foreach (var shown in _shown)
            {
                var now = passing.Find(lobby => ReferenceEquals(lobby.Instance, shown.Instance));
                if (now != null) kept.Add(now);
            }

            foreach (var lobby in passing)
            {
                if (!kept.Exists(candidate => ReferenceEquals(candidate.Instance, lobby.Instance))) kept.Add(lobby);
            }

            _shown = kept;
            _list.SetCount(_shown.Count);
        }

        _list.Selected = _shown.FindIndex(lobby => ReferenceEquals(lobby.Instance, _picked));
        _list.Refresh();
        RefreshPositions();
        RefreshFilters();
        RefreshCount();
        if (_lobbies.Count > 0)
        {
            _status.text = _shown.Count == 0
                ? $"None of the {_lobbies.Count} lobbies found pass the filters."
                : "Select a lobby to pick it, Select again to join.";
        }

        var focusedEntry = _list.EntryOf(Focused);
        if (Focused == null || _browserColumn.Contains(Focused) || Focused == _join) ShowLobby(focusedEntry >= 0 ? _shown[focusedEntry] : Picked());
    }

    private bool Matches(Lobby lobby)
    {
        var type = lobby.Type switch
        {
            MissionPvpType.Pve => _showPve,
            MissionPvpType.Pvp => _showPvp,
            _ => _showPve || _showPvp
        };
        if (!type) return false;
        if (lobby.Password ? !_showPassword : !_showOpen) return false;
        if (lobby.Modded ? !_showModded : !_showVanilla) return false;
        if (lobby.Dedicated ? !_showDedicated : !_showPlayerHosted) return false;
        if (_hideFull && lobby.Full) return false;
        if (_hideEmpty && lobby.Players <= 0) return false;

        var limit = PingLimit();
        if (limit.HasValue && (!lobby.Ping.HasValue || lobby.Ping.Value > limit.Value)) return false;

        foreach (var term in _search.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Contains(lobby.Name, term) && !Contains(lobby.Mission, term) && !Contains(lobby.Map, term) && !Contains(lobby.Description, term)) return false;
        }

        return true;
    }

    private int? PingLimit()
    {
        var max = MaxPings[_maxPing];
        var distance = Distances[_distance].Ping;
        if (!max.HasValue) return distance;
        if (!distance.HasValue) return max;
        return Math.Min(max.Value, distance.Value);
    }

    private int Compare(Lobby left, Lobby right)
    {
        var (sort, ascending, _) = Sorts[_sortIndex];
        var comparison = sort switch
        {
            Sort.Mission => string.Compare(left.Mission, right.Mission, StringComparison.OrdinalIgnoreCase),
            Sort.Players => left.Players.CompareTo(right.Players),
            Sort.Ping => left.Ping.GetValueOrDefault(int.MaxValue).CompareTo(right.Ping.GetValueOrDefault(int.MaxValue)),
            _ => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        };
        if (!ascending) comparison = -comparison;
        return comparison != 0 ? comparison : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void FillLobby(int entry, TextMeshProUGUI[] cells)
    {
        var lobby = _shown[entry];
        var dim = MenuStyle.Hex(MenuStyle.TextDim);
        var flags = (lobby.Password ? " PWD" : "") + (lobby.Modded ? " MOD" : "");
        cells[0].text = flags.Length > 0 ? $"{lobby.Name}<color=#{dim}><size=80%>{flags}</size></color>" : lobby.Name;
        cells[0].color = ReferenceEquals(lobby.Instance, _picked) ? MenuStyle.Accent : MenuStyle.Text;
        cells[1].text = lobby.Mission;
        cells[2].text = lobby.PlayersText;
        cells[2].color = lobby.Full ? MenuStyle.TextDim : MenuStyle.Text;
        cells[3].text = lobby.PingText;
    }

    // Select on the picked lobby again joins it; Right only ever picks.
    private void PickLobby(int entry, bool joinIfPicked)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        var lobby = _shown[entry];
        if (joinIfPicked && ReferenceEquals(lobby.Instance, _picked) && _join.interactable)
        {
            Join();
            return;
        }

        _picked = lobby.Instance;
        _list.Selected = entry;
        _list.Refresh();
        ShowLobby(lobby);
        FocusFirst(_join);
    }

    private Lobby? Picked() => _picked == null ? null : _shown.Find(lobby => ReferenceEquals(lobby.Instance, _picked));

    private Lobby? EntryLobby(int entry) => entry >= 0 && entry < _shown.Count ? _shown[entry] : Picked();

    private void ShowLobby(Lobby? lobby)
    {
        if (lobby == null)
        {
            _lobbyName.text = _shown.Count > 0 ? "PICK A LOBBY" : "";
            _mission.text = "";
            _facts.text = "";
            _description.text = "";
        }
        else
        {
            _lobbyName.text = lobby.Name;
            _mission.text = lobby.Mission;
            var flags = new List<string> { lobby.TypeText, lobby.Dedicated ? "DEDICATED" : "PLAYER HOSTED" };
            if (lobby.Password) flags.Add("PASSWORD");
            flags.Add(lobby.Modded ? "MODDED" : "VANILLA");
            if (lobby.Full) flags.Add("FULL");
            _facts.text = $"{lobby.Map}\n{lobby.PlayersText} PLAYERS   {lobby.PingText}\n{string.Join("  ", flags)}";
            _description.text = lobby.Description;
        }

        var picked = Picked();
        _join.interactable = picked != null && !picked.Full;
        var label = picked == null ? "JOIN" : $"JOIN {picked.Name.ToUpperInvariant()}";
        if (_join.Label != null && _join.Label.text != label) _join.SetLabel(label);
    }

    private void Join()
    {
        var lobby = Picked();
        if (lobby == null || lobby.Full) return;

        if (lobby.Password)
        {
            OpenPopup(lobby);
            return;
        }

        _status.text = _actions.TryJoinLobby(lobby.Instance, null, promptIfPasswordNeeded: false)
            ? $"Joining {lobby.Name}."
            : $"Couldn't join {lobby.Name}.";
    }

    private void OpenPopup(Lobby lobby)
    {
        _joining = lobby;
        _popupTitle.text = lobby.Name;
        _popupStatus.text = "";
        _popupPassword.Value = "";
        _popup.gameObject.SetActive(true);
        _popup.SetAsLastSibling();
        SetModal(_popupButtons, _popupPassword.Button);
        _popupPassword.Begin();
    }

    private void ClosePopup()
    {
        _joining = null;
        if (!_popup.gameObject.activeSelf) return;

        MenuField.StopTyping();
        _popup.gameObject.SetActive(false);
        SetModal(null, null);
        FocusFirst(_join, _lastListFocus);
    }

    private void JoinWithPassword()
    {
        if (_joining == null) return;

        var lobby = _joining;
        var password = _popupPassword.Value.Trim();
        if (password.Length == 0)
        {
            _popupStatus.text = "Enter the server's password.";
            return;
        }

        if (!LobbyPassword.TestShortPassword(lobby.Instance, password))
        {
            _popupStatus.text = "That password isn't right.";
            return;
        }

        ClosePopup();
        _status.text = _actions.TryJoinLobby(lobby.Instance, password, promptIfPasswordNeeded: false)
            ? $"Joining {lobby.Name}."
            : $"Couldn't join {lobby.Name}.";
    }

    // Asks the game's browser again, and sorts what it then has.
    private void Refresh()
    {
        _status.text = "Looking for lobbies.";
        _actions.TryRefreshMultiplayerLobbies();
        ReadLobbies(resort: true);
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
    }

    private void RefreshPositions()
    {
        _position.text = _shown.Count == 0 ? "" : _list.Position;
        _hostPosition.text = _hostList.Position;
    }

    private void RefreshCount()
    {
        _count.text = _view == View.Host || _lobbies.Count == 0 ? "" : $"{_shown.Count} OF {_lobbies.Count} LOBBIES";
    }

    private void AddToggle(RectTransform parent, string label, Func<bool> get, Action<bool> set) =>
        AddFilter(parent, label, () => MenuSetting.OnOff(get()), _ => set(!get()));

    private void AddFilter(RectTransform parent, string label, Func<string> read, Action<int> adjust)
    {
        var index = _filters.Count;
        var position = new Vector2(FilterColumnX[index / FiltersPerColumn], FilterFirstY - index % FiltersPerColumn * FilterRowStep);
        var setting = new MenuSetting(parent, label, position, FilterRowSize, 220f, read, adjust, null, () => ApplyFilters(resort: true));
        _filters.Add(setting);
        _filterOrder.Add(Add(setting.Button));
    }

    private void RefreshFilters()
    {
        foreach (var filter in _filters) filter.Refresh();
        _filtersSummary.text = _lobbies.Count == 0 ? "No lobbies found yet." : $"{_shown.Count} of the {_lobbies.Count} lobbies found pass these.";
    }

    private void AddHostSetting(RectTransform parent, string label, float y, Func<string> read, Action<int> adjust)
    {
        var setting = new MenuSetting(parent, label, new Vector2(HostColumnX, y), new Vector2(HostWidth, 56f), 200f, read, adjust, null, RefreshHost);
        _hostSettings.Add(setting);
        _hostColumn.Add(Add(setting.Button));
    }

    private void LoadHostMissions()
    {
        _hostMissionsLoaded = true;
        _hostMissions.Clear();
        try
        {
            MissionGroup.Init();
            foreach (var entry in MissionSaveLoad.QuickLoadMany(MissionGroup.All.GetMissions()))
            {
                var tags = entry.mission.missionSettings.Tags;
                if (tags != null && tags.Exists(tag => tag.Equals(MissionTag.Multiplayer))) _hostMissions.Add((entry.key, entry.mission));
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not load the multiplayer missions: {exception.Message}");
            _hostStatus.text = "The multiplayer missions couldn't be loaded.";
        }

        _hostList.SetCount(_hostMissions.Count);
        if (_hostMissions.Count > 0) PickHostMission(0, focusHost: false);
    }

    private void FillHostMission(int entry, TextMeshProUGUI[] cells)
    {
        cells[0].text = _hostMissions[entry].Key.Name;
        cells[0].color = entry == _hostPicked ? MenuStyle.Accent : MenuStyle.Text;
    }

    private void PickHostMission(int entry) => PickHostMission(entry, focusHost: true);

    private void PickHostMission(int entry, bool focusHost)
    {
        if (entry < 0 || entry >= _hostMissions.Count) return;

        var previous = _hostPicked >= 0 ? DefaultLobbyName(_hostMissions[_hostPicked].Key) : null;
        _hostPicked = entry;
        _hostList.Selected = entry;
        _hostList.Refresh();
        var mission = _hostMissions[entry];
        _hostTitle.text = mission.Key.Name;
        _hostDescription.text = mission.Mission.missionSettings.description ?? "";
        // A name typed in is kept; the one made from the last mission follows the new one.
        if (string.IsNullOrWhiteSpace(_hostName.Value) || _hostName.Value == previous) _hostName.Value = DefaultLobbyName(mission.Key);
        RefreshHost();
        if (focusHost) FocusFirst(_host);
    }

    private static string DefaultLobbyName(MissionKey key) => $"{key.Name} [Hosted by {PlayerSettings.playerName_Unsanitized}]";

    private void RefreshHost()
    {
        foreach (var setting in _hostSettings) setting.Refresh();
        _hostPassword.Button.interactable = _hostUsesPassword;
        _hostPassword.Input.interactable = _hostUsesPassword;
        _host.interactable = !_hosting && _hostPicked >= 0;
        RefreshPositions();
    }

    private void HostLobby()
    {
        if (_hosting || _hostPicked < 0 || _hostPicked >= _hostMissions.Count) return;

        var password = _hostUsesPassword ? _hostPassword.Value.Trim() : null;
        if (_hostUsesPassword && string.IsNullOrEmpty(password))
        {
            _hostStatus.text = "Type a password, or turn Password off.";
            return;
        }

        var entry = _hostMissions[_hostPicked];
        var name = string.IsNullOrWhiteSpace(_hostName.Value) ? DefaultLobbyName(entry.Key) : _hostName.Value.Trim();
        HostAsync(entry.Key, name, _hostMaxPlayers, Visibilities[_hostVisibility].Visibility, password).Forget();
    }

    private async UniTaskVoid HostAsync(MissionKey key, string lobbyName, int maxPlayers, HostLobbyVisibility visibility, string? password)
    {
        _hosting = true;
        RefreshHost();
        _hostStatus.text = "Creating the Steam lobby.";
        try
        {
            if (!key.TryLoad(out var mission, out var error))
            {
                _hostStatus.text = error;
                NOVRPlugin.Log.LogWarning($"[NOVR] Could not load mission '{key.Name}' to host: {error}");
                return;
            }

            if (SteamLobby.instance == null)
            {
                _hostStatus.text = "Steam lobbies aren't available.";
                return;
            }

            MissionManager.SetMission(mission, checkIfSame: false);
            SteamLobby.instance.CheckRelayLocationTask();
            var hosted = await SteamLobby.instance.HostLobby(maxPlayers, visibility switch
            {
                HostLobbyVisibility.FriendsOnly => ELobbyType.k_ELobbyTypeFriendsOnly,
                HostLobbyVisibility.Private => ELobbyType.k_ELobbyTypePrivate,
                _ => ELobbyType.k_ELobbyTypePublic
            });
            if (!hosted.HasValue)
            {
                _hostStatus.text = "Steam couldn't create the lobby.";
                return;
            }

            var lobby = hosted.Value;
            SteamLobby.instance.CurrentLobbyName = lobbyName;
            lobby.SetData("name", lobbyName.SanitizeRichText(128));
            lobby.SetData("mission_name", mission.Name.SanitizeRichText(128));
            lobby.SetData("mission_description", mission.missionSettings.description.SanitizeRichText(1000));
            lobby.SetData("mission_pvp_type", MissionTag.GetPvpTypeLobbyString(mission));
            if (TryGetMapName(mission.MapKey, out var mapName)) lobby.SetData("map_name", mapName);

            var workshopId = mission.LoadKey?.WorkshopId;
            if (workshopId.HasValue) lobby.SetData("mission_workshop_id", workshopId.Value.m_PublishedFileId.ToString("X"));

            var hasPassword = !string.IsNullOrEmpty(password);
            if (hasPassword) lobby.SetData("short_password", LobbyPassword.GetShortPassword(password!));

            var modded = NetworkManagerNuclearOption.ModdedServer == true || mission.missionSettings?.allowEventContent == true;
            lobby.SetData("modded_server", LobbyInstance.BoolToTag(modded));

            _hostStatus.text = "Starting the server.";
            await NetworkManagerNuclearOption.i.StartHostAsync(new HostOptions(SocketType.Steam, GameState.Multiplayer, mission.MapKey)
            {
                MaxConnections = maxPlayers - 1,
                Password = hasPassword ? password : null
            });
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Hosting failed: {exception}");
            _hostStatus.text = "Hosting failed; the BepInEx log has why.";
        }
        finally
        {
            _hosting = false;
            RefreshHost();
        }
    }

    private static bool TryGetMapName(MapKey mapKey, out string mapName)
    {
        foreach (var loader in Resources.FindObjectsOfTypeAll<MapLoader>())
        {
            if (loader != null && loader.TryGetMapName(mapKey, out mapName)) return true;
        }

        mapName = "";
        return false;
    }

    private void Leave()
    {
        _actions.TryInvokeCurrentMenuButton("Main Menu", "MAIN MENU", "BACK", "< BACK", "MenuExit_Button");
    }

    private static bool Contains(string value, string term) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

    private static int Wrap(int index, int count) => (index % count + count) % count;
}
