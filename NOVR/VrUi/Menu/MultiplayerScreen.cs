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

// Multiplayer in the game's menu style: the server browser, its filters and hosting a lobby, chosen from the left-hand
// panel. The browser lists the lobbies the game's own browser has found (it keeps searching behind this, and Refresh asks
// it again), sorted by a click on a column's heading, with the focused or picked one described on the right. Select on a
// lobby picks it and goes to Join; a password is asked for when it has one. The filters are settings rows, as in the
// settings. Hosting takes a multiplayer mission, a name, a size, who can see it and a password, and hosts the lobby as
// the game does (NativeMultiplayerPanel did the same).
internal sealed class MultiplayerScreen : MenuScreen
{
    private const float ContentX = 300f;
    private static readonly Vector2 ContentSize = new(1200f, 960f);
    private const float RailButtonWidth = 440f;
    private const float RailButtonHeight = 54f;
    private const float RailStep = 62f;
    private const float RefreshSeconds = 1f;
    private const float ListX = -200f;
    private const float ListWidth = 780f;
    private const float DetailsX = 400f;
    private const float DetailsWidth = 380f;
    private const int Rows = 11;
    private const float RowHeight = 48f;
    private const float RowStep = 54f;
    private static readonly float[] FilterColumnX = { -296f, 296f };
    private static readonly Vector2 FilterRowSize = new(570f, 56f);
    private const float FilterFirstY = 330f;
    private const float FilterRowStep = 64f;
    private const int FiltersPerColumn = 7;

    private static readonly FieldInfo? ShownField = typeof(LobbyListItem).GetField("shown", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly int?[] MaxPings = { null, 75, 150, 250, 400 };
    private static readonly (string Label, int? Ping)[] Distances = { ("ANY", null), ("NEARBY", 75), ("REGIONAL", 150), ("WORLDWIDE", 300) };
    private static readonly (HostLobbyVisibility Visibility, string Label)[] Visibilities =
    {
        (HostLobbyVisibility.Public, "PUBLIC"),
        (HostLobbyVisibility.FriendsOnly, "FRIENDS"),
        (HostLobbyVisibility.Private, "PRIVATE")
    };

    // The list's columns, from a row's centre, which the headings share.
    private static readonly MenuList.Column[] Columns =
    {
        new(-200f, 350f),
        new(85f, 210f, dim: true),
        new(255f, 110f, TextAlignmentOptions.Center),
        new(335f, 80f, TextAlignmentOptions.Center, dim: true)
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
    private readonly List<Lobby> _lobbies = new();
    private readonly List<Lobby> _shown = new();
    private readonly List<MenuSetting> _filters = new();
    private readonly List<MenuButton> _filterOrder = new();
    private readonly List<(Sort Sort, string Label, MenuButton Button)> _headings = new();
    private readonly List<(MissionKey Key, MissionQuickLoad Mission)> _hostMissions = new();
    private readonly List<MenuSetting> _hostSettings = new();
    private readonly MenuList _list;
    private readonly MenuField _search;
    private readonly TextMeshProUGUI _position;
    private readonly TextMeshProUGUI _status;
    private readonly TextMeshProUGUI _title;
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
    private Sort _sort = Sort.Players;
    private bool _ascending;
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
        AddViewButton(rail, View.Browser, "SERVER BROWSER", ref y);
        AddViewButton(rail, View.Filters, "FILTERS", ref y);
        AddViewButton(rail, View.Host, "CREATE LOBBY", ref y);
        y -= 18f;
        Add(MenuKit.Button(rail, "Refresh", "REFRESH", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), Refresh,
            MenuButton.Look.Secondary));
        y -= RailStep + 18f;
        Add(MenuKit.Button(rail, "Back", "BACK", new Vector2(0f, y), new Vector2(RailButtonWidth, 56f), Leave));
        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(RailButtonWidth, 34f));

        var content = MenuKit.Panel(Root, "Content", new Vector2(ContentX, 0f), ContentSize).rectTransform;

        // The server browser.
        var browser = AddView(content, View.Browser);
        _search = new MenuField(browser, "Search", "SEARCH", "Lobby, mission or map", new Vector2(ListX, 400f), new Vector2(ListWidth, 56f), 190f);
        _search.Changed += _ => ApplyFilters(fromTop: true);
        Add(_search.Button);
        var rowX = ListX - 8f;
        AddHeading(browser, Sort.Lobby, "LOBBY", rowX);
        AddHeading(browser, Sort.Mission, "MISSION", rowX);
        AddHeading(browser, Sort.Players, "PLAYERS", rowX);
        AddHeading(browser, Sort.Ping, "PING", rowX);
        _list = new MenuList(browser, "Lobby List", new Vector2(ListX, 310f - (RowHeight + (Rows - 1) * RowStep) / 2f), ListWidth, Rows,
            RowHeight, RowStep, Columns, FillLobby);
        _list.Activated += PickLobby;
        foreach (var row in _list.Rows) Add(row);
        _position = MenuKit.Text(browser, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(ListX, -312f), new Vector2(ListWidth, 32f));
        _status = MenuKit.Text(browser, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(ListX, -380f), new Vector2(ListWidth, 70f), wrap: true);

        _title = MenuKit.Text(browser, "Lobby", "", MenuStyle.ButtonSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 385f), new Vector2(DetailsWidth, 96f), heading: true, wrap: true);
        _mission = MenuKit.Text(browser, "Mission", "", MenuStyle.BodySize, MenuStyle.Accent, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 290f), new Vector2(DetailsWidth, 72f), wrap: true);
        _facts = MenuKit.Text(browser, "Facts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 190f), new Vector2(DetailsWidth, 110f), wrap: true);
        _description = MenuKit.Text(browser, "Description", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, -95f), new Vector2(DetailsWidth, 440f), wrap: true);
        _join = Add(MenuKit.Button(browser, "Join", "JOIN", new Vector2(DetailsX, -400f), new Vector2(DetailsWidth, 62f), Join));

        // The filters.
        var filters = AddView(content, View.Filters);
        MenuKit.Text(filters, "Title", "FILTERS", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, ContentSize.y / 2f - 70f), new Vector2(ContentSize.x - 80f, 64f), heading: true);
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
        AddChoice(filters, "MAX PING", () => MaxPings[_maxPing] is { } ping ? $"{ping} MS" : "ANY",
            direction => _maxPing = Wrap(_maxPing + direction, MaxPings.Length));
        AddChoice(filters, "DISTANCE", () => Distances[_distance].Label, direction => _distance = Wrap(_distance + direction, Distances.Length));
        AddChoice(filters, "SORT BY", () => SortLabel(_sort), direction => SetSort((Sort)Wrap((int)_sort + direction, 4), keepOrder: true));
        AddChoice(filters, "ORDER", () => _ascending ? "ASCENDING" : "DESCENDING", _ => _ascending = !_ascending);
        _filtersSummary = MenuKit.Text(filters, "Summary", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -ContentSize.y / 2f + 70f), new Vector2(ContentSize.x - 80f, 34f));

        // Hosting.
        var host = AddView(content, View.Host);
        MenuKit.Text(host, "Missions Title", "MISSION", MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.Left,
            new Vector2(-300f, 410f), new Vector2(560f, 32f), heading: true);
        _hostList = new MenuList(host, "Host Missions", new Vector2(-300f, 380f - (RowHeight + (Rows - 1) * RowStep) / 2f), 560f, Rows,
            RowHeight, RowStep, new[] { new MenuList.Column(0f, 500f) }, FillHostMission);
        _hostList.Activated += PickHostMission;
        foreach (var row in _hostList.Rows) Add(row);
        _hostPosition = MenuKit.Text(host, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(-300f, -244f), new Vector2(560f, 32f));
        _hostTitle = MenuKit.Text(host, "Mission", "", MenuStyle.ButtonSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(300f, 400f), new Vector2(560f, 56f), heading: true);
        _hostDescription = MenuKit.Text(host, "Description", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(300f, 270f), new Vector2(560f, 180f), wrap: true);
        _hostName = new MenuField(host, "Lobby Name", "NAME", "Lobby name", new Vector2(300f, 130f), new Vector2(560f, 56f), 150f, characterLimit: 128);
        Add(_hostName.Button);
        AddHostSetting(host, "MAX PLAYERS", 66f, () => $"{_hostMaxPlayers}", direction =>
            _hostMaxPlayers = Mathf.Clamp(_hostMaxPlayers + direction, 2, Mathf.Max(2, _actions.GetTooManyPlayerLimit())));
        AddHostSetting(host, "LOBBY TYPE", 2f, () => Visibilities[_hostVisibility].Label,
            direction => _hostVisibility = Wrap(_hostVisibility + direction, Visibilities.Length));
        AddHostSetting(host, "PASSWORD", -62f, () => MenuSetting.OnOff(_hostUsesPassword), _ => _hostUsesPassword = !_hostUsesPassword);
        _hostPassword = new MenuField(host, "Host Password", "PASSWORD", "Password", new Vector2(300f, -126f), new Vector2(560f, 56f), 220f, password: true);
        Add(_hostPassword.Button);
        _hostStatus = MenuKit.Text(host, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(300f, -250f), new Vector2(560f, 110f), wrap: true);
        _host = Add(MenuKit.Button(host, "Host", "HOST LOBBY", new Vector2(300f, -400f), new Vector2(560f, 62f), HostLobby));

        SetOrders(_list.Rows, _filterOrder, _hostList.Rows);

        // Asked for a password before joining.
        _popup = MenuKit.Rect(Root, "Password", Vector2.zero, canvas.sizeDelta);
        var shade = MenuKit.Image(_popup, "Shade", new Color(0f, 0f, 0f, 0.55f), Vector2.zero, Vector2.zero);
        MenuKit.Stretch(shade.rectTransform);
        shade.raycastTarget = true;
        var box = MenuKit.Panel(_popup, "Box", new Vector2(ContentX, 0f), new Vector2(720f, 380f)).rectTransform;
        _popupTitle = MenuKit.Text(box, "Title", "PASSWORD", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, 130f), new Vector2(640f, 64f), heading: true);
        _popupPassword = new MenuField(box, "Join Password", "PASSWORD", "Server password", new Vector2(0f, 40f), new Vector2(640f, 56f), 220f, password: true);
        Add(_popupPassword.Button);
        _popupButtons.Add(_popupPassword.Button);
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
        ShowView(View.Browser);
        _status.text = "Looking for lobbies.";
        _actions.TryRefreshMultiplayerLobbies();
        ReadLobbies();
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        Focus(FirstFocus(), sound: false);
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
        ReadLobbies();
    }

    protected override MenuButton? FirstFocus() => _view switch
    {
        View.Browser => _list.Count > 0 ? _list.Rows[0] : _search.Button,
        View.Filters => _filterOrder[0],
        _ => _hostList.Count > 0 ? _hostList.Rows[0] : _hostName.Button
    };

    protected override bool OnBack()
    {
        if (_popup.gameObject.activeSelf)
        {
            ClosePopup();
            return true;
        }

        var focused = Focused;
        if (focused != null && focused.transform.IsChildOf(_views[_view]))
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

    protected override bool OnOrderEnd(List<MenuButton> order, int step)
    {
        if (order == _filterOrder) return true;

        var list = order == _hostList.Rows ? _hostList : _list;
        if (!list.Scroll(step, sound: true)) return false;

        RefreshPositions();
        if (list == _list) ShowLobby(EntryLobby(_list.EntryOf(Focused)));
        return true;
    }

    protected override void OnFocused(MenuButton? button)
    {
        if (_view != View.Browser) return;

        var entry = _list.EntryOf(button);
        ShowLobby(entry >= 0 ? _shown[entry] : Picked());
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    private RectTransform AddView(RectTransform content, View view)
    {
        var rect = MenuKit.Rect(content, $"{view} View", Vector2.zero, ContentSize);
        _views[view] = rect;
        return rect;
    }

    private void AddViewButton(RectTransform rail, View view, string label, ref float y)
    {
        _viewButtons[view] = Add(MenuKit.Button(rail, label, label, new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), () =>
        {
            ShowView(view);
            Focus(FirstFocus(), sound: false);
        }, MenuButton.Look.Secondary));
        y -= RailStep;
    }

    private void ShowView(View view)
    {
        _view = view;
        foreach (var pair in _views) pair.Value.gameObject.SetActive(pair.Key == view);
        foreach (var pair in _viewButtons) pair.Value.Highlight = pair.Key == view;
        if (view == View.Host)
        {
            if (!_hostMissionsLoaded) LoadHostMissions();
            RefreshHost();
        }

        RefreshFilters();
    }

    // The game's own browser, as it has it now.
    private void ReadLobbies()
    {
        _lobbies.Clear();
        if (!_actions.TryGetActiveLobbyList(out var lobbyList) || lobbyList == null)
        {
            _status.text = "Waiting for the game's lobby list.";
            ApplyFilters(fromTop: false);
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

        ApplyFilters(fromTop: false);
    }

    private static bool IsShown(LobbyListItem item) =>
        ShownField?.GetValue(item) is bool shown ? shown : item.gameObject.activeInHierarchy;

    private void ApplyFilters(bool fromTop)
    {
        _shown.Clear();
        foreach (var lobby in _lobbies)
        {
            if (Matches(lobby)) _shown.Add(lobby);
        }

        _shown.Sort(Compare);
        _list.Selected = _shown.FindIndex(lobby => ReferenceEquals(lobby.Instance, _picked));
        _list.SetCount(_shown.Count);
        if (fromTop) _list.Reset();
        RefreshPositions();
        RefreshFilters();
        if (_lobbies.Count > 0)
        {
            _status.text = _shown.Count == 0
                ? $"None of the {_lobbies.Count} lobbies found pass the filters."
                : "Select a lobby to see it, and again to join. A column's heading sorts by it.";
        }

        var focusedEntry = _list.EntryOf(Focused);
        ShowLobby(focusedEntry >= 0 ? _shown[focusedEntry] : Picked());
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
        var comparison = _sort switch
        {
            Sort.Mission => string.Compare(left.Mission, right.Mission, StringComparison.OrdinalIgnoreCase),
            Sort.Players => left.Players.CompareTo(right.Players),
            Sort.Ping => left.Ping.GetValueOrDefault(int.MaxValue).CompareTo(right.Ping.GetValueOrDefault(int.MaxValue)),
            _ => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        };
        if (!_ascending) comparison = -comparison;
        return comparison != 0 ? comparison : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    // A second click on the same heading turns the order round; busiest and nearest first, names A to Z.
    private void SetSort(Sort sort, bool keepOrder = false)
    {
        if (!keepOrder) _ascending = sort == _sort ? !_ascending : sort is Sort.Lobby or Sort.Mission or Sort.Ping;
        _sort = sort;
        ApplyFilters(fromTop: true);
    }

    private static string SortLabel(Sort sort) => sort switch
    {
        Sort.Mission => "MISSION",
        Sort.Players => "PLAYERS",
        Sort.Ping => "PING",
        _ => "LOBBY"
    };

    private void AddHeading(RectTransform parent, Sort sort, string label, float rowX)
    {
        var column = Columns[(int)sort];
        var button = Add(MenuKit.Button(parent, $"Sort {label}", label, new Vector2(rowX + column.X, 344f), new Vector2(column.Width, 40f),
            () => SetSort(sort), MenuButton.Look.Cell, MenuStyle.SmallSize));
        if (button.Label != null) button.Label.alignment = column.Alignment;
        _headings.Add((sort, label, button));
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

    private void PickLobby(int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        var lobby = _shown[entry];
        if (ReferenceEquals(lobby.Instance, _picked) && _join.interactable)
        {
            Join();
            return;
        }

        _picked = lobby.Instance;
        _list.Selected = entry;
        _list.Refresh();
        ShowLobby(lobby);
        Focus(_join, sound: false);
    }

    private Lobby? Picked() => _picked == null ? null : _shown.Find(lobby => ReferenceEquals(lobby.Instance, _picked));

    private Lobby? EntryLobby(int entry) => entry >= 0 && entry < _shown.Count ? _shown[entry] : Picked();

    private void ShowLobby(Lobby? lobby)
    {
        if (lobby == null)
        {
            _title.text = _shown.Count > 0 ? "PICK A LOBBY" : "";
            _mission.text = "";
            _facts.text = "";
            _description.text = "";
        }
        else
        {
            _title.text = lobby.Name;
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
        _join.SetLabel(picked != null && lobby != null && !ReferenceEquals(lobby.Instance, picked.Instance) ? "JOIN PICKED LOBBY" : "JOIN");
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
        SetModal(null, _join.interactable ? _join : null);
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

    private void Refresh()
    {
        _status.text = "Looking for lobbies.";
        _actions.TryRefreshMultiplayerLobbies();
        _nextRefresh = 0f;
    }

    private void RefreshPositions()
    {
        _position.text = _shown.Count == 0 ? "" : _list.Position;
        _hostPosition.text = _hostList.Position;
        foreach (var (sort, label, button) in _headings)
        {
            var text = sort == _sort ? $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{label} {(_ascending ? "^" : "v")}</color>" : label;
            button.SetLabel(text);
        }
    }

    private void AddToggle(RectTransform parent, string label, Func<bool> get, Action<bool> set) =>
        AddFilter(parent, label, () => MenuSetting.OnOff(get()), _ => set(!get()));

    private void AddChoice(RectTransform parent, string label, Func<string> read, Action<int> adjust) => AddFilter(parent, label, read, adjust);

    private void AddFilter(RectTransform parent, string label, Func<string> read, Action<int> adjust)
    {
        var index = _filters.Count;
        var position = new Vector2(FilterColumnX[index / FiltersPerColumn], FilterFirstY - index % FiltersPerColumn * FilterRowStep);
        var setting = new MenuSetting(parent, label, position, FilterRowSize, 220f, read, adjust, null, () => ApplyFilters(fromTop: true));
        _filters.Add(setting);
        _filterOrder.Add(Add(setting.Button));
    }

    private void RefreshFilters()
    {
        foreach (var filter in _filters) filter.Refresh();
        _filtersSummary.text = _lobbies.Count == 0 ? "No lobbies found yet." : $"{_shown.Count} OF {_lobbies.Count} LOBBIES SHOWN";
    }

    private void AddHostSetting(RectTransform parent, string label, float y, Func<string> read, Action<int> adjust)
    {
        var setting = new MenuSetting(parent, label, new Vector2(300f, y), new Vector2(560f, 56f), 200f, read, adjust, null, RefreshHost);
        _hostSettings.Add(setting);
        Add(setting.Button);
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
        if (_hostMissions.Count > 0) PickHostMission(0, focusName: false);
    }

    private void FillHostMission(int entry, TextMeshProUGUI[] cells)
    {
        cells[0].text = _hostMissions[entry].Key.Name;
        cells[0].color = entry == _hostPicked ? MenuStyle.Accent : MenuStyle.Text;
    }

    private void PickHostMission(int entry) => PickHostMission(entry, focusName: true);

    private void PickHostMission(int entry, bool focusName)
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
        if (focusName) Focus(_host, sound: false);
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
