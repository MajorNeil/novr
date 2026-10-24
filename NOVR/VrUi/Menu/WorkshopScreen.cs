using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NOVR.VrUi.Native;
using NuclearOption.ModScripts;
using NuclearOption.Workshop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// The Steam Workshop in the game's menu style, laid out and used like the single player screen: missions or liveries
// down the left-hand panel, and beside it one column (the order, a search, the items, the page) next to the item focused
// or picked, with its picture and what can be done with it. Up and Down run down a column; Right goes on to Subscribe,
// Left back to the list and then to the panel; Back goes the same way out. Select or Right on an item picks it and goes
// to Subscribe. Steam gives fifty a page; Left and Right on Page turn them. Typing in Search narrows the page shown, and
// Enter asks Steam for everything that matches. It all goes through the game's own SteamWorkshop, as
// NativeWorkshopPanel did.
internal sealed class WorkshopScreen : MenuScreen
{
    private const float ContentX = 300f;
    private static readonly Vector2 ContentSize = new(1200f, 960f);
    private const float TitleY = 410f;
    private const float RailButtonWidth = 440f;
    private const float RailButtonHeight = 54f;
    private const float RailStep = 62f;
    private const float ListX = -240f;
    private const float ListWidth = 680f;
    private const float DetailsX = 370f;
    private const float DetailsWidth = 420f;
    private const int Rows = 9;
    private const float RowHeight = 50f;
    private const float RowStep = 54f;
    private static readonly Vector2 SettingSize = new(ListWidth, 56f);
    // The picture waits for the focus to rest, so running down the list doesn't fetch every one on the way.
    private const float PreviewDelaySeconds = 0.25f;

    private static readonly MenuList.Column[] Columns =
    {
        new(-130f, 400f),
        new(155f, 160f, dim: true),
        new(275f, 110f, TextAlignmentOptions.Center, dim: true)
    };

    private static readonly (OrderBy Order, string Label)[] Orders =
    {
        (OrderBy.Trend30Days, "TRENDING"),
        (OrderBy.TopAllTime, "TOP RATED"),
        (OrderBy.New, "NEWEST")
    };

    private enum Tab
    {
        Missions,
        Liveries
    }

    private readonly NativeGameActionAdapter _actions;
    private readonly SteamWorkshop _workshop;
    private readonly List<SteamWorkshopItem> _items = new();
    private readonly List<SteamWorkshopItem> _shown = new();
    private readonly Dictionary<Tab, MenuButton> _tabButtons = new();
    private readonly List<MenuButton> _rail = new();
    private readonly List<MenuButton> _column = new();
    private readonly List<MenuButton> _details = new();
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _count;
    private readonly MenuSetting _order;
    private readonly MenuField _search;
    private readonly MenuList _list;
    private readonly MenuSetting _pageRow;
    private readonly TextMeshProUGUI _position;
    private readonly TextMeshProUGUI _status;
    private readonly MenuButton _updateAll;
    private readonly Image _preview;
    private readonly TextMeshProUGUI _itemName;
    private readonly TextMeshProUGUI _author;
    private readonly TextMeshProUGUI _description;
    private readonly TextMeshProUGUI _itemStatus;
    private readonly MenuButton _subscribe;
    private readonly MenuButton _steamPage;
    private readonly MenuButton _localFiles;
    private readonly TextMeshProUGUI _prompts;
    private Tab _tab = Tab.Missions;
    private int _orderIndex;
    private uint _page = 1;
    private string _steamSearch = "";
    private bool _hasNextPage;
    private bool _querying;
    private bool _busy;
    private SteamWorkshopItem? _picked;
    private SteamWorkshopItem? _described;
    private SteamWorkshopItem? _previewed;
    private MenuButton? _lastListFocus;
    private float _previewAt = float.PositiveInfinity;
    private CancellationTokenSource? _previewCancellation;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    public WorkshopScreen(RectTransform canvas, NativeGameActionAdapter actions) : base(canvas, "NOVR Workshop")
    {
        _actions = actions;
        _workshop = canvas.TryGetComponent<SteamWorkshop>(out var workshop) ? workshop : canvas.gameObject.AddComponent<SteamWorkshop>();

        var rail = MenuKit.Panel(Root, "Sections", MainMenuScreen.RailCenter, MainMenuScreen.RailSize).rectTransform;
        var top = MainMenuScreen.RailSize.y / 2f;
        MenuKit.Text(rail, "Title", "WORKSHOP", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 70f), new Vector2(RailButtonWidth, 64f), heading: true);
        MenuKit.Image(rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 112f), new Vector2(RailButtonWidth, 2f));

        var y = top - 160f;
        AddTab(rail, Tab.Missions, "MISSIONS", ref y);
        AddTab(rail, Tab.Liveries, "LIVERIES", ref y);
        y -= 18f;
        _rail.Add(Add(MenuKit.Button(rail, "Refresh", "REFRESH", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), () => Query(_page),
            MenuButton.Look.Secondary)));
        y -= RailStep;
        _updateAll = Add(MenuKit.Button(rail, "Update All", "UPDATE SUBSCRIBED", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight),
            UpdateAll, MenuButton.Look.Secondary));
        _rail.Add(_updateAll);
        y -= RailStep;
        _rail.Add(Add(MenuKit.Button(rail, "Open Steam", "OPEN IN STEAM", new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight),
            SteamWorkshop.OpenWorkshopPage, MenuButton.Look.Secondary)));
        y -= RailStep + 18f;
        _rail.Add(Add(MenuKit.Button(rail, "Back", "BACK", new Vector2(0f, y), new Vector2(RailButtonWidth, 56f), Leave)));
        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(RailButtonWidth, 34f));

        var content = MenuKit.Panel(Root, "Content", new Vector2(ContentX, 0f), ContentSize).rectTransform;
        _title = MenuKit.Text(content, "Title", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f), heading: true);
        _count = MenuKit.Text(content, "Count", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Right,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f));

        // One column: the order, a search, the items and the page.
        _order = new MenuSetting(content, "ORDER", new Vector2(ListX, 340f), SettingSize, 260f, () => Orders[_orderIndex].Label, direction =>
        {
            _orderIndex = (_orderIndex + direction + Orders.Length) % Orders.Length;
            Query(1);
        }, null, () => _order?.Refresh());
        _column.Add(Add(_order.Button));
        _search = new MenuField(content, "Search", "SEARCH", "Name or author, Enter asks Steam", new Vector2(ListX, 276f), SettingSize, 190f);
        _search.Changed += _ => ApplySearch();
        _search.Input.onEndEdit.AddListener(SearchSteam);
        _column.Add(Add(_search.Button));

        var rowX = ListX - 8f;
        string[] labels = { "NAME", "AUTHOR", "STATE" };
        for (var index = 0; index < labels.Length; index++)
        {
            MenuKit.Text(content, labels[index], labels[index], MenuStyle.SmallSize, MenuStyle.Accent, Columns[index].Alignment,
                new Vector2(rowX + Columns[index].X, 228f), new Vector2(Columns[index].Width, 32f), heading: true);
        }

        _list = new MenuList(content, "Items", new Vector2(ListX, 204f - (RowHeight + (Rows - 1) * RowStep) / 2f), ListWidth, Rows,
            RowHeight, RowStep, Columns, FillRow);
        _list.Activated += Pick;
        foreach (var row in _list.Rows) _column.Add(Add(row));

        _pageRow = new MenuSetting(content, "PAGE", new Vector2(ListX, -316f), SettingSize, 260f, PageLabel, TurnPage, null,
            () => _pageRow?.Refresh());
        _column.Add(Add(_pageRow.Button));
        _position = MenuKit.Text(content, "Position", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(ListX, -364f), new Vector2(ListWidth, 32f));
        _status = MenuKit.Text(content, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(ListX, -410f), new Vector2(ListWidth, 32f));

        // The item, and what can be done with it.
        _preview = MenuKit.Image(content, "Preview", MenuStyle.Button, new Vector2(DetailsX, 290f), new Vector2(DetailsWidth, 236f));
        _preview.preserveAspect = true;
        _itemName = MenuKit.Text(content, "Item", "", MenuStyle.ButtonSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, 120f), new Vector2(DetailsWidth, 80f), heading: true, wrap: true);
        _author = MenuKit.Text(content, "Author", "", MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.Left,
            new Vector2(DetailsX, 62f), new Vector2(DetailsWidth, 32f));
        _description = MenuKit.Text(content, "Description", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(DetailsX, -70f), new Vector2(DetailsWidth, 200f), wrap: true);
        _itemStatus = MenuKit.Text(content, "Item Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(DetailsX, -195f), new Vector2(DetailsWidth, 32f));
        _subscribe = Add(MenuKit.Button(content, "Subscribe", "SUBSCRIBE", new Vector2(DetailsX, -260f), new Vector2(DetailsWidth, 58f),
            ToggleSubscription));
        _steamPage = Add(MenuKit.Button(content, "Steam Page", "STEAM PAGE", new Vector2(DetailsX, -330f), new Vector2(DetailsWidth, 56f),
            () => _picked?.OpenSteamPage(), MenuButton.Look.Secondary));
        _localFiles = Add(MenuKit.Button(content, "Local Files", "LOCAL FILES", new Vector2(DetailsX, -398f), new Vector2(DetailsWidth, 56f),
            () =>
            {
                if (_picked?.Subscribed == true) _picked.OpenLocalContent();
            }, MenuButton.Look.Secondary));
        _details.Add(_subscribe);
        _details.Add(_steamPage);
        _details.Add(_localFiles);

        SetOrders(_rail, _column, _details);
        RefreshTabs();
        Describe(null);
    }

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        _lastListFocus = null;
        Focus(_tabButtons[_tab], sound: false);
        Query(1);
    }

    protected override void OnHidden()
    {
        _previewCancellation?.Cancel();
    }

    protected override void OnTick()
    {
        if (_promptDevice != MenuInput.LastDevice)
        {
            _promptDevice = MenuInput.LastDevice;
            _prompts.text = $"{Prompt(MenuCommand.PreviousTab)}/{Prompt(MenuCommand.NextTab)} Missions/Liveries   {Prompt(MenuCommand.Back)} Back";
        }

        if (Time.unscaledTime < _previewAt) return;

        _previewAt = float.PositiveInfinity;
        if (_described == null || _described == _previewed) return;

        _previewed = _described;
        _preview.sprite = null;
        _preview.color = MenuStyle.Button;
        _described.SetPreviewImageAsync(_preview, ref _previewCancellation).Forget();
    }

    protected override MenuButton? FirstFocus() => _tabButtons[_tab];

    protected override bool OnBack()
    {
        var focused = Focused;
        if (focused != null && _details.Contains(focused) && CanFocus(_lastListFocus))
        {
            Focus(_lastListFocus, sound: false);
            return true;
        }

        if (focused != null && !_rail.Contains(focused))
        {
            Focus(_tabButtons[_tab], sound: false);
            return true;
        }

        Leave();
        return true;
    }

    protected override void OnTab(int direction)
    {
        SetTab(_tab == Tab.Missions ? Tab.Liveries : Tab.Missions);
        Focus(_tabButtons[_tab], sound: false);
        MenuSounds.Move();
    }

    protected override bool OnOrderStep(List<MenuButton> order, MenuButton focused, int step)
    {
        if (order != _column || !_list.StepPast(focused, step)) return false;

        RefreshPosition();
        var entry = _list.EntryOf(Focused);
        Describe(entry >= 0 ? _shown[entry] : _picked);
        return true;
    }

    // The columns end there; across is Left and Right.
    protected override bool OnOrderEnd(List<MenuButton> order, int step) => true;

    // From the panel into the list and back; from the list to what can be done with the item and back.
    protected override bool OnSideways(MenuButton focused, int direction)
    {
        if (_rail.Contains(focused))
        {
            if (direction > 0) EnterList();
            return true;
        }

        if (_column.Contains(focused))
        {
            var entry = _list.EntryOf(focused);
            if (direction < 0) Focus(_tabButtons[_tab], sound: true);
            else if (entry >= 0) Pick(entry);
            else FocusFirst(_subscribe, _steamPage, _localFiles);
            return true;
        }

        if (_details.Contains(focused))
        {
            if (direction < 0) FocusFirst(_lastListFocus, _list.Rows[0], _search.Button);
            return true;
        }

        return false;
    }

    protected override void OnFocused(MenuButton? button)
    {
        if (button == null) return;

        if (_column.Contains(button)) _lastListFocus = button;
        if (!_column.Contains(button) && !_details.Contains(button)) return;

        var entry = _list.EntryOf(button);
        Describe(entry >= 0 ? _shown[entry] : _picked);
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    private void AddTab(RectTransform rail, Tab tab, string label, ref float y)
    {
        var button = Add(MenuKit.Button(rail, label, label, new Vector2(0f, y), new Vector2(RailButtonWidth, RailButtonHeight), () =>
        {
            SetTab(tab);
            EnterList();
        }, MenuButton.Look.Secondary));
        _tabButtons[tab] = button;
        _rail.Add(button);
        y -= RailStep;
    }

    // Where the focus was in the list, else its first item, else the order.
    private void EnterList() => FocusFirst(_lastListFocus, _list.Rows[0], _order.Button);

    private void SetTab(Tab tab)
    {
        if (_tab == tab) return;

        _tab = tab;
        _lastListFocus = null;
        RefreshTabs();
        Query(1);
    }

    private void RefreshTabs()
    {
        foreach (var pair in _tabButtons) pair.Value.Highlight = pair.Key == _tab;
        _title.text = _tab == Tab.Liveries ? "LIVERIES" : "MISSIONS";
        _order.Refresh();
    }

    private string PageLabel() => _querying ? "..." : _hasNextPage || _page > 1 ? $"{_page}" : "1 OF 1";

    private void TurnPage(int direction)
    {
        if (direction < 0 && _page > 1) Query(_page - 1);
        else if (direction > 0 && _hasNextPage) Query(_page + 1);
    }

    private void SearchSteam(string text)
    {
        var search = text.Trim();
        if (search == _steamSearch) return;

        _steamSearch = search;
        Query(1);
    }

    private void Query(uint page)
    {
        if (_querying || page < 1) return;

        QueryAsync(page).Forget();
    }

    private async UniTaskVoid QueryAsync(uint page)
    {
        _querying = true;
        _page = page;
        _status.text = _steamSearch.Length > 0 ? $"Asking Steam for \"{_steamSearch}\"." : "Asking Steam.";
        _items.Clear();
        ApplySearch();
        RefreshButtons();
        try
        {
            var tag = _tab == Tab.Liveries ? ModTypes.AircraftLivery.Tag : ModTypes.Missions.Tag;
            _hasNextPage = await _workshop.RefreshItems(Orders[_orderIndex].Order, tag, _items, page, _steamSearch.Length > 0 ? _steamSearch : null);
            _status.text = _items.Count == 0 ? "Steam has nothing here." : "";
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop query failed: {exception.Message}");
            _items.Clear();
            _hasNextPage = false;
            _status.text = "Steam didn't answer; Refresh tries again.";
        }
        finally
        {
            _querying = false;
            ApplySearch();
            RefreshButtons();
        }
    }

    private void ApplySearch()
    {
        _shown.Clear();
        var terms = _search.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in _items)
        {
            if (Array.TrueForAll(terms, term => Contains(item.Name, term) || Contains(item.OwnerName, term) || Contains(item.Description, term))) _shown.Add(item);
        }

        if (_picked != null && !_items.Contains(_picked)) Pick(null);
        _list.Selected = _picked != null ? _shown.IndexOf(_picked) : -1;
        _list.SetCount(_shown.Count);
        _list.Reset();
        RefreshPosition();
        var entry = _list.EntryOf(Focused);
        Describe(entry >= 0 ? _shown[entry] : _picked);
    }

    private void RefreshPosition()
    {
        _position.text = _shown.Count > 0 ? _list.Position : "";
        _count.text = _items.Count > 0 ? $"STEAM PAGE {_page}" : "";
        _pageRow.Refresh();
    }

    private void FillRow(int entry, TextMeshProUGUI[] cells)
    {
        var item = _shown[entry];
        cells[0].text = item.Name;
        cells[0].color = item == _picked ? MenuStyle.Accent : MenuStyle.Text;
        cells[1].text = item.OwnerName ?? "";
        cells[2].text = item.Subscribed ? "INSTALLED" : "";
    }

    private void Pick(int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        Pick(_shown[entry]);
        FocusFirst(_subscribe);
    }

    private void Pick(SteamWorkshopItem? item)
    {
        if (_picked != null) _picked.OwnerNameChanged -= OnOwnerNameChanged;
        _picked = item;
        if (_picked != null) _picked.OwnerNameChanged += OnOwnerNameChanged;
        _list.Selected = _picked != null ? _shown.IndexOf(_picked) : -1;
        _list.Refresh();
        Describe(_picked);
        RefreshButtons();
    }

    private void OnOwnerNameChanged()
    {
        _list.Refresh();
        Describe(_described);
    }

    private void Describe(SteamWorkshopItem? item)
    {
        _described = item;
        if (item == null)
        {
            _itemName.text = _shown.Count > 0 ? "PICK AN ITEM" : "";
            _author.text = "";
            _description.text = "";
            _itemStatus.text = "";
            _preview.sprite = null;
            _preview.color = MenuStyle.Button;
            _previewed = null;
            _previewAt = float.PositiveInfinity;
            return;
        }

        _itemName.text = item.Name;
        _author.text = string.IsNullOrWhiteSpace(item.OwnerName) ? "" : $"BY {item.OwnerName.ToUpperInvariant()}";
        _description.text = item.Description ?? "";
        _itemStatus.text = item.Subscribed ? "Installed." : "Not installed.";
        if (item != _previewed) _previewAt = Time.unscaledTime + PreviewDelaySeconds;
    }

    private void RefreshButtons()
    {
        _subscribe.interactable = _picked != null && !_busy;
        _subscribe.SetLabel(_picked?.Subscribed == true ? "UNSUBSCRIBE" : "SUBSCRIBE");
        _steamPage.interactable = _picked != null;
        _localFiles.interactable = _picked?.Subscribed == true;
        _updateAll.interactable = !_busy;
        _pageRow.Refresh();
    }

    private void ToggleSubscription()
    {
        if (_picked == null || _busy) return;

        ToggleAsync(_picked).Forget();
    }

    private async UniTaskVoid ToggleAsync(SteamWorkshopItem item)
    {
        _busy = true;
        RefreshButtons();
        try
        {
            if (item.Subscribed)
            {
                _itemStatus.text = "Unsubscribing.";
                await _workshop.Unsubscribe(item);
            }
            else
            {
                _itemStatus.text = "Subscribing and downloading.";
                await _workshop.DownloadItem(item);
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop subscription change failed: {exception.Message}");
            _itemStatus.text = "That didn't work; the BepInEx log has why.";
            return;
        }
        finally
        {
            _busy = false;
            RefreshButtons();
            _list.Refresh();
        }

        if (_described == item) Describe(item);
    }

    private void UpdateAll()
    {
        if (_busy) return;

        UpdateAllAsync().Forget();
    }

    private async UniTaskVoid UpdateAllAsync()
    {
        _busy = true;
        RefreshButtons();
        _status.text = "Updating subscribed items.";
        try
        {
            await SteamWorkshop.UpdateAllSubscribedItems();
            _status.text = "Subscribed items are up to date.";
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop update failed: {exception.Message}");
            _status.text = "Updating failed; the BepInEx log has why.";
        }
        finally
        {
            _busy = false;
            RefreshButtons();
        }
    }

    private void Leave()
    {
        if (!_actions.TryCloseWorkshopMenu()) _actions.TryInvokeCurrentMenuButton("Back", "< BACK", "BACK", "MenuExit_Button");
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value!.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
}
