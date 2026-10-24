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

// Multiplayer, built and used like the settings (RailScreen): the server browser, its filters and creating a lobby down
// the left-hand panel. The browser is a Sort row and the lobbies as rows; the focused lobby is described on the right, and
// Select on it opens Join (asking for its password when it has one). The lobbies are the ones the game's own browser has
// found (it keeps looking behind this; Refresh asks it again). They keep their places while their player counts change,
// so the focus stays on the lobby it is on; a change of sort or filter, or Refresh, sorts them again. The filters and a
// new lobby's settings are settings rows; Mission opens the list of multiplayer missions, and Host Lobby hosts it as the
// game does (NativeMultiplayerPanel did the same).
internal sealed class MultiplayerScreen : RailScreen
{
    private const float RefreshSeconds = 1f;
    private const int ListRows = 10;

    private static readonly FieldInfo? ShownField = typeof(LobbyListItem).GetField("shown", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly int?[] MaxPings = { null, 75, 150, 250, 400 };
    private static readonly (string Label, int? Ping)[] Distances = { ("ANY", null), ("NEARBY", 75), ("REGIONAL", 150), ("WORLDWIDE", 300) };
    private static readonly string[] Types = { "ALL", "PVE", "PVP" };
    private static readonly string[] Access = { "ALL", "OPEN", "PASSWORD" };
    private static readonly string[] Mods = { "ALL", "VANILLA", "MODDED" };
    private static readonly string[] Servers = { "ALL", "DEDICATED", "PLAYER HOSTED" };
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
        public string PingText => Ping.HasValue ? $"{Ping.Value} MS" : "--";

        public string Facts
        {
            get
            {
                var flags = new List<string>
                {
                    Type switch { MissionPvpType.Pve => "PVE", MissionPvpType.Pvp => "PVP", _ => "ANY" },
                    Dedicated ? "DEDICATED" : "PLAYER HOSTED"
                };
                if (Password) flags.Add("PASSWORD");
                flags.Add(Modded ? "MODDED" : "VANILLA");
                if (Full) flags.Add("FULL");
                return $"{Map}\n{PlayersText} PLAYERS   {PingText}\n{string.Join("  ", flags)}";
            }
        }
    }

    private readonly NativeGameActionAdapter _actions;
    private readonly Section _browser;
    private readonly Section _filters;
    private readonly Section _create;
    private readonly MenuButton _browserButton;
    private readonly MenuButton _filtersButton;
    private readonly MenuButton _createButton;
    private readonly MenuList _list;
    private readonly MenuField _search;
    private readonly MenuField _hostName;
    private readonly MenuField _hostPassword;
    private readonly MenuButton _host;
    private readonly List<Lobby> _lobbies = new();
    private List<Lobby> _shown = new();
    private readonly List<(MissionKey Key, MissionQuickLoad Mission)> _hostMissions = new();
    private int _sortIndex;
    private int _type;
    private int _access;
    private int _mods;
    private int _servers;
    private bool _hideFull;
    private bool _hideEmpty;
    private int _maxPing;
    private int _distance;
    private int _hostPicked = -1;
    private int _hostMaxPlayers = 8;
    private int _hostVisibility;
    private bool _hostUsesPassword;
    private bool _hostMissionsLoaded;
    private bool _hosting;
    private float _nextRefresh;

    public MultiplayerScreen(RectTransform canvas, NativeGameActionAdapter actions) : base(canvas, "NOVR Multiplayer", "MULTIPLAYER")
    {
        _actions = actions;

        _browserButton = AddRailButton("SERVER BROWSER", () => ShowPart(_browser!, _browserButton!));
        _filtersButton = AddRailButton("FILTERS", () => ShowPart(_filters!, _filtersButton!));
        _createButton = AddRailButton("CREATE LOBBY", () => ShowPart(_create!, _createButton!));
        AddRailButton("REFRESH", Refresh, gapBefore: true);
        AddRailBack();

        _browser = AddSection("SERVER BROWSER");
        AddSetting(_browser, "SORT", () => Sorts[_sortIndex].Label, direction => _sortIndex = Wrap(_sortIndex + direction, Sorts.Length),
            description: "The order of the lobbies below.");
        _list = AddList(_browser, ListRows, FillLobby);

        _filters = AddSection("FILTERS");
        _search = AddField(_filters, "SEARCH", "Lobby, mission or map", "Only lobbies whose name, mission or map has these words.");
        _search.Changed += _ => ApplyFilters(resort: true);
        AddSetting(_filters, "MISSION TYPE", () => Types[_type], direction => _type = Wrap(_type + direction, Types.Length),
            description: "Player against environment, player against player, or both.");
        AddSetting(_filters, "ACCESS", () => Access[_access], direction => _access = Wrap(_access + direction, Access.Length),
            description: "Open lobbies, ones with a password, or both.");
        AddSetting(_filters, "MODS", () => Mods[_mods], direction => _mods = Wrap(_mods + direction, Mods.Length),
            description: "Vanilla lobbies, modded ones, or both.");
        AddSetting(_filters, "SERVERS", () => Servers[_servers], direction => _servers = Wrap(_servers + direction, Servers.Length),
            description: "Dedicated servers, lobbies hosted by a player, or both.");
        AddSetting(_filters, "HIDE FULL", () => MenuSetting.OnOff(_hideFull), _ => _hideFull = !_hideFull,
            description: "Leaves out the lobbies with no room.");
        AddSetting(_filters, "HIDE EMPTY", () => MenuSetting.OnOff(_hideEmpty), _ => _hideEmpty = !_hideEmpty,
            description: "Leaves out the lobbies nobody is in.");
        AddSetting(_filters, "MAX PING", () => MaxPings[_maxPing] is { } ping ? $"{ping} MS" : "ANY",
            direction => _maxPing = Wrap(_maxPing + direction, MaxPings.Length), description: "Leaves out the lobbies further away than this.");
        AddSetting(_filters, "DISTANCE", () => Distances[_distance].Label, direction => _distance = Wrap(_distance + direction, Distances.Length),
            description: "Roughly how far away the lobbies may be, by their ping.");

        _create = AddSection("CREATE LOBBY");
        AddSetting(_create, "MISSION", () => _hostPicked >= 0 ? _hostMissions[_hostPicked].Key.Name : "NONE",
            direction => PickHostMission(Wrap(_hostPicked + direction, Math.Max(1, _hostMissions.Count))), ChooseHostMission,
            "The multiplayer mission to host. Select lists them all.");
        _hostName = AddField(_create, "NAME", "Lobby name", "What the lobby is called in the server browser.", characterLimit: 128);
        AddSetting(_create, "MAX PLAYERS", () => $"{_hostMaxPlayers}", direction =>
            _hostMaxPlayers = Mathf.Clamp(_hostMaxPlayers + direction, 2, Mathf.Max(2, _actions.GetTooManyPlayerLimit())),
            description: "How many can play in it, you included.");
        AddSetting(_create, "LOBBY TYPE", () => Visibilities[_hostVisibility].Label,
            direction => _hostVisibility = Wrap(_hostVisibility + direction, Visibilities.Length),
            description: "Who sees it in the browser: everyone, your Steam friends, or nobody (join by invite).");
        AddSetting(_create, "USE PASSWORD", () => MenuSetting.OnOff(_hostUsesPassword), _ => _hostUsesPassword = !_hostUsesPassword,
            description: "Whether joining it takes the password below.");
        _hostPassword = AddField(_create, "PASSWORD", "Password", "The password players type to join.", password: true);
        _host = AddAction(_create, "HOST LOBBY", HostLobby, "Creates the lobby and starts the mission.", primary: true);

        ShowPart(_browser, _browserButton);
    }

    protected override void OnShown()
    {
        base.OnShown();
        ShowPart(_browser, _browserButton);
        SetStatus("Looking for lobbies.");
        _actions.TryRefreshMultiplayerLobbies();
        ReadLobbies(resort: true);
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
    }

    protected override void OnTick()
    {
        base.OnTick();
        if (Time.unscaledTime < _nextRefresh) return;

        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        ReadLobbies(resort: false);
    }

    protected override void OnTab(int direction)
    {
        if (PopupOpen) return;

        var parts = new[] { (_browser, _browserButton), (_filters, _filtersButton), (_create, _createButton) };
        var index = Array.FindIndex(parts, part => part.Item1 == Current);
        var (section, button) = parts[Wrap(index + direction, parts.Length)];
        ShowPart(section, button);
        Focus(button, sound: false);
        MenuSounds.Move();
    }

    protected override void Leave() => _actions.TryInvokeCurrentMenuButton("Main Menu", "MAIN MENU", "BACK", "< BACK", "MenuExit_Button");

    protected override void OnSettingChanged(Section section)
    {
        if (section == _create) RefreshHost();
        else ApplyFilters(resort: true);
        base.OnSettingChanged(section);
    }

    protected override void Describe(Section section, MenuButton? row, int entry)
    {
        if (section == _browser)
        {
            if (entry >= 0 && entry < _shown.Count)
            {
                var lobby = _shown[entry];
                ShowDetails(lobby.Name, lobby.Mission, lobby.Facts, lobby.Description);
            }
            else if (row != null)
            {
                base.Describe(section, row, entry);
            }
            else
            {
                ShowDetails("SERVER BROWSER", Summary(), "", "Select a lobby to join it.");
            }

            return;
        }

        if (section == _create)
        {
            var mission = _hostPicked >= 0 ? _hostMissions[_hostPicked] : default;
            var rowDescription = row != null && section.Rows.TryGetValue(row, out var described) ? described.Description : "";
            ShowDetails(_hostPicked >= 0 ? mission.Key.Name : "NO MISSION", "MULTIPLAYER MISSION", rowDescription,
                _hostPicked >= 0 ? mission.Mission.missionSettings.description ?? "" : "");
            return;
        }

        if (row != null) base.Describe(section, row, entry);
        else ShowDetails("FILTERS", Summary(), "", "Which lobbies the server browser lists.");
    }

    protected override void Open(Section section, int entry)
    {
        if (section != _browser || entry < 0 || entry >= _shown.Count) return;

        var lobby = _shown[entry];
        var body = string.IsNullOrEmpty(lobby.Description) ? lobby.Facts : $"{lobby.Facts}\n\n{lobby.Description}";
        OpenPopup(lobby.Name, lobby.Mission, body, picture: false, fieldLabel: lobby.Password ? "PASSWORD" : null,
            (lobby.Full ? "FULL" : "JOIN", () => Join(lobby)));
        if (lobby.Password)
        {
            // Enter joins, as typing the password is the last thing to do.
            PopupField.Input.onEndEdit.RemoveAllListeners();
            PopupField.Input.onEndEdit.AddListener(_ =>
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) && Join(lobby)) ClosePopup();
            });
            OnPopupClosed(() => PopupField.Input.onEndEdit.RemoveAllListeners());
            Focus(PopupField.Button, sound: false);
            PopupField.Begin();
        }
    }

    private void ShowPart(Section section, MenuButton button)
    {
        CurrentRailButton = button;
        foreach (var other in new[] { _browserButton, _filtersButton, _createButton }) other.Highlight = other == button;
        if (section == _create)
        {
            if (!_hostMissionsLoaded) LoadHostMissions();
            RefreshHost();
        }

        ShowSection(section);
        RefreshCount();
    }

    private string Summary() => _lobbies.Count == 0 ? "NO LOBBIES FOUND YET" : $"{_shown.Count} OF {_lobbies.Count} LOBBIES SHOWN";

    private void RefreshCount() => SetCount(Current == _create || _lobbies.Count == 0 ? "" : Summary());

    // The game's own browser, as it has it now.
    private void ReadLobbies(bool resort)
    {
        _lobbies.Clear();
        if (!_actions.TryGetActiveLobbyList(out var lobbyList) || lobbyList == null)
        {
            SetStatus("Waiting for the game's lobby list.");
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
            _list.Refresh();
        }

        RefreshCount();
        if (_lobbies.Count > 0)
        {
            SetStatus(_shown.Count == 0 ? $"None of the {_lobbies.Count} lobbies found pass the filters." : "");
        }

        if (!PopupOpen) RefreshDetails();
    }

    private bool Matches(Lobby lobby)
    {
        if (_type == 1 && lobby.Type == MissionPvpType.Pvp || _type == 2 && lobby.Type == MissionPvpType.Pve) return false;
        if (_access == 1 && lobby.Password || _access == 2 && !lobby.Password) return false;
        if (_mods == 1 && lobby.Modded || _mods == 2 && !lobby.Modded) return false;
        if (_servers == 1 && !lobby.Dedicated || _servers == 2 && lobby.Dedicated) return false;
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

    private void FillLobby(int entry, TextMeshProUGUI name, TextMeshProUGUI value)
    {
        var lobby = _shown[entry];
        var dim = MenuStyle.Hex(MenuStyle.TextDim);
        var flags = (lobby.Password ? " PWD" : "") + (lobby.Modded ? " MOD" : "");
        name.text = flags.Length > 0 ? $"{lobby.Name}<color=#{dim}><size=75%>{flags}</size></color>" : lobby.Name;
        value.text = $"{lobby.PlayersText}   {lobby.PingText}";
    }

    private bool Join(Lobby lobby)
    {
        if (lobby.Full)
        {
            SetPopupStatus("It's full.");
            return false;
        }

        string? password = null;
        if (lobby.Password)
        {
            password = PopupField.Value.Trim();
            if (password.Length == 0)
            {
                SetPopupStatus("Type the server's password first.");
                return false;
            }

            if (!LobbyPassword.TestShortPassword(lobby.Instance, password))
            {
                SetPopupStatus("That password isn't right.");
                return false;
            }
        }

        SetStatus(_actions.TryJoinLobby(lobby.Instance, password, promptIfPasswordNeeded: false)
            ? $"Joining {lobby.Name}."
            : $"Couldn't join {lobby.Name}.");
        return true;
    }

    // Asks the game's browser again, and sorts what it then has.
    private void Refresh()
    {
        SetStatus("Looking for lobbies.");
        _actions.TryRefreshMultiplayerLobbies();
        ReadLobbies(resort: true);
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
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
            SetStatus("The multiplayer missions couldn't be loaded.");
        }

        if (_hostMissions.Count > 0) PickHostMission(0);
    }

    private void ChooseHostMission()
    {
        var names = _hostMissions.ConvertAll(mission => mission.Key.Name);
        OpenChoice("MISSION", names, _hostPicked, PickHostMission);
    }

    private void PickHostMission(int entry)
    {
        if (entry < 0 || entry >= _hostMissions.Count) return;

        var previous = _hostPicked >= 0 ? DefaultLobbyName(_hostMissions[_hostPicked].Key) : null;
        _hostPicked = entry;
        // A name typed in is kept; the one made from the last mission follows the new one.
        if (string.IsNullOrWhiteSpace(_hostName.Value) || _hostName.Value == previous) _hostName.Value = DefaultLobbyName(_hostMissions[entry].Key);
        RefreshHost();
    }

    private static string DefaultLobbyName(MissionKey key) => $"{key.Name} [Hosted by {PlayerSettings.playerName_Unsanitized}]";

    private void RefreshHost()
    {
        _hostPassword.Button.interactable = _hostUsesPassword;
        _hostPassword.Input.interactable = _hostUsesPassword;
        _host.interactable = !_hosting && _hostPicked >= 0;
        RefreshRows(_create);
        if (Current == _create && !PopupOpen) RefreshDetails();
    }

    private void HostLobby()
    {
        if (_hosting || _hostPicked < 0 || _hostPicked >= _hostMissions.Count) return;

        var password = _hostUsesPassword ? _hostPassword.Value.Trim() : null;
        if (_hostUsesPassword && string.IsNullOrEmpty(password))
        {
            SetStatus("Type a password, or turn Password off.");
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
        SetStatus("Creating the Steam lobby.");
        try
        {
            if (!key.TryLoad(out var mission, out var error))
            {
                SetStatus(error);
                NOVRPlugin.Log.LogWarning($"[NOVR] Could not load mission '{key.Name}' to host: {error}");
                return;
            }

            if (SteamLobby.instance == null)
            {
                SetStatus("Steam lobbies aren't available.");
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
                SetStatus("Steam couldn't create the lobby.");
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

            SetStatus("Starting the server.");
            await NetworkManagerNuclearOption.i.StartHostAsync(new HostOptions(SocketType.Steam, GameState.Multiplayer, mission.MapKey)
            {
                MaxConnections = maxPlayers - 1,
                Password = hasPassword ? password : null
            });
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Hosting failed: {exception}");
            SetStatus("Hosting failed; the BepInEx log has why.");
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

    private static bool Contains(string value, string term) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

    private static int Wrap(int index, int count) => count <= 0 ? 0 : (index % count + count) % count;
}
