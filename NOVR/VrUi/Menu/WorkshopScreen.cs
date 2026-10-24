using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NuclearOption.ModScripts;
using NuclearOption.Workshop;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// The Steam Workshop, built and used like the settings (RailScreen): missions or liveries down the left-hand panel, and
// beside it the Order and Search rows, the items as rows, and the Page row. The focused item is described on the right
// with its picture; Select on it opens Subscribe (or Unsubscribe), its Steam page and its files. Steam gives fifty a page.
// Typing in Search narrows the page shown, and Enter asks Steam for everything that matches. It all goes through the
// game's own SteamWorkshop, as NativeWorkshopPanel did; it opens from the main menu without the game's Workshop menu,
// which has nothing it needs.
internal sealed class WorkshopScreen : RailScreen
{
    private const int ListRows = 8;
    // The picture waits for the focus to rest, so running down the list doesn't fetch every one on the way.
    private const float PreviewDelaySeconds = 0.25f;

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

    private readonly Action _close;
    private readonly SteamWorkshop _workshop;
    private readonly List<SteamWorkshopItem> _items = new();
    private readonly List<SteamWorkshopItem> _shown = new();
    private readonly Dictionary<Tab, MenuButton> _tabButtons = new();
    private readonly MenuButton _updateAll;
    private readonly Section _section;
    private readonly MenuField _search;
    private readonly MenuList _list;
    private Tab _tab = Tab.Missions;
    private int _orderIndex;
    private uint _page = 1;
    private string _steamSearch = "";
    private bool _hasNextPage;
    private bool _querying;
    private bool _busy;
    private SteamWorkshopItem? _described;
    private SteamWorkshopItem? _previewed;
    private float _previewAt = float.PositiveInfinity;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _popupPreviewCancellation;

    public WorkshopScreen(RectTransform canvas, Action close) : base(canvas, "NOVR Workshop", "WORKSHOP")
    {
        _close = close;
        _workshop = canvas.TryGetComponent<SteamWorkshop>(out var workshop) ? workshop : canvas.gameObject.AddComponent<SteamWorkshop>();

        _tabButtons[Tab.Missions] = AddRailButton("MISSIONS", () => SetTab(Tab.Missions));
        _tabButtons[Tab.Liveries] = AddRailButton("LIVERIES", () => SetTab(Tab.Liveries));
        AddRailButton("REFRESH", () => Query(_page), gapBefore: true);
        _updateAll = AddRailButton("UPDATE SUBSCRIBED", UpdateAll);
        AddRailButton("OPEN IN STEAM", SteamWorkshop.OpenWorkshopPage);
        AddRailBack();

        _section = AddSection("MISSIONS");
        AddSetting(_section, "ORDER", () => Orders[_orderIndex].Label, direction =>
        {
            _orderIndex = (_orderIndex + direction + Orders.Length) % Orders.Length;
            Query(1);
        }, description: "Trending over the last month, the best rated of all time, or the newest.");
        _search = AddField(_section, "SEARCH", "Name or author", "Typing narrows this page; Enter asks Steam for everything that matches.");
        _search.Changed += _ => ApplySearch();
        _search.Input.onEndEdit.AddListener(SearchSteam);
        _list = AddList(_section, ListRows, FillRow);
        AddSetting(_section, "PAGE", PageLabel, TurnPage, description: "Steam gives fifty a page. Left and Right turn them.");

        ShowSection(_section);
        CurrentRailButton = _tabButtons[_tab];
        RefreshTabs();
    }

    protected override void OnShown()
    {
        base.OnShown();
        Query(1);
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        _previewCancellation?.Cancel();
    }

    protected override void OnTick()
    {
        base.OnTick();
        if (Time.unscaledTime < _previewAt) return;

        _previewAt = float.PositiveInfinity;
        if (_described == null || _described == _previewed) return;

        _previewed = _described;
        DetailsPicture.sprite = null;
        DetailsPicture.color = MenuStyle.Button;
        _described.SetPreviewImageAsync(DetailsPicture, ref _previewCancellation).Forget();
    }

    protected override void OnTab(int direction)
    {
        SetTab(_tab == Tab.Missions ? Tab.Liveries : Tab.Missions);
        Focus(_tabButtons[_tab], sound: false);
        MenuSounds.Move();
    }

    protected override void Leave() => _close();

    protected override void Describe(Section section, MenuButton? row, int entry)
    {
        if (entry >= 0 && entry < _shown.Count)
        {
            var item = _shown[entry];
            ShowDetails(item.Name, Author(item), item.Subscribed ? "INSTALLED" : "NOT INSTALLED", item.Description ?? "", picture: true);
            if (item != _described)
            {
                _described = item;
                if (item != _previewed) _previewAt = Time.unscaledTime + PreviewDelaySeconds;
            }

            return;
        }

        _described = null;
        if (row != null) base.Describe(section, row, entry);
        else ShowDetails(_tab == Tab.Liveries ? "LIVERIES" : "MISSIONS", _items.Count > 0 ? $"STEAM PAGE {_page}" : "", "",
            "Select an item to subscribe to it, or to see its Steam page.");
    }

    protected override void Open(Section section, int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        var item = _shown[entry];
        var actions = new List<(string, Func<bool>)>
        {
            (item.Subscribed ? "UNSUBSCRIBE" : "SUBSCRIBE", () => ToggleSubscription(item)),
            ("STEAM PAGE", () =>
            {
                item.OpenSteamPage();
                return true;
            })
        };
        if (item.Subscribed)
        {
            actions.Add(("LOCAL FILES", () =>
            {
                item.OpenLocalContent();
                return true;
            }));
        }

        OpenPopup(item.Name, Author(item), item.Description ?? "", picture: true, fieldLabel: null, actions.ToArray());
        PopupPicture.sprite = null;
        PopupPicture.color = MenuStyle.Button;
        item.SetPreviewImageAsync(PopupPicture, ref _popupPreviewCancellation).Forget();
    }

    private void SetTab(Tab tab)
    {
        CurrentRailButton = _tabButtons[tab];
        if (_tab == tab) return;

        _tab = tab;
        _section.LastFocus = null;
        RefreshTabs();
        Query(1);
    }

    private void RefreshTabs()
    {
        foreach (var pair in _tabButtons) pair.Value.Highlight = pair.Key == _tab;
        _section.Title = _tab == Tab.Liveries ? "LIVERIES" : "MISSIONS";
        ShowSection(_section);
    }

    private string PageLabel() => _querying ? "..." : _hasNextPage ? $"{_page}" : $"{_page} (LAST)";

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
        SetStatus(_steamSearch.Length > 0 ? $"Asking Steam for \"{_steamSearch}\"." : "Asking Steam.");
        _items.Clear();
        ApplySearch();
        try
        {
            var tag = _tab == Tab.Liveries ? ModTypes.AircraftLivery.Tag : ModTypes.Missions.Tag;
            _hasNextPage = await _workshop.RefreshItems(Orders[_orderIndex].Order, tag, _items, page, _steamSearch.Length > 0 ? _steamSearch : null);
            SetStatus(_items.Count == 0 ? "Steam has nothing here." : "");
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop query failed: {exception.Message}");
            _items.Clear();
            _hasNextPage = false;
            SetStatus("Steam didn't answer; Refresh tries again.");
        }
        finally
        {
            _querying = false;
            ApplySearch();
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

        _list.SetCount(_shown.Count);
        _list.Reset();
        SetCount(_items.Count > 0 ? $"STEAM PAGE {_page}" : "");
        RefreshSettings();
        if (!PopupOpen) RefreshDetails();
    }

    // The Page row's value follows the query.
    private void RefreshSettings() => RefreshRows(_section);

    private void FillRow(int entry, TextMeshProUGUI name, TextMeshProUGUI value)
    {
        var item = _shown[entry];
        name.text = item.Name;
        value.text = item.Subscribed ? "INSTALLED" : item.OwnerName ?? "";
    }

    private bool ToggleSubscription(SteamWorkshopItem item)
    {
        if (_busy) return false;

        ToggleAsync(item).Forget();
        return true;
    }

    private async UniTaskVoid ToggleAsync(SteamWorkshopItem item)
    {
        _busy = true;
        try
        {
            if (item.Subscribed)
            {
                SetStatus($"Unsubscribing from {item.Name}.");
                await _workshop.Unsubscribe(item);
                SetStatus($"Unsubscribed from {item.Name}.");
            }
            else
            {
                SetStatus($"Downloading {item.Name}.");
                await _workshop.DownloadItem(item);
                SetStatus($"{item.Name} is installed.");
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop subscription change failed: {exception.Message}");
            SetStatus("That didn't work; the BepInEx log has why.");
        }
        finally
        {
            _busy = false;
            _list.Refresh();
            if (!PopupOpen) RefreshDetails();
        }
    }

    private void UpdateAll()
    {
        if (_busy) return;

        UpdateAllAsync().Forget();
    }

    private async UniTaskVoid UpdateAllAsync()
    {
        _busy = true;
        _updateAll.interactable = false;
        SetStatus("Updating subscribed items.");
        try
        {
            await SteamWorkshop.UpdateAllSubscribedItems();
            SetStatus("Subscribed items are up to date.");
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Workshop update failed: {exception.Message}");
            SetStatus("Updating failed; the BepInEx log has why.");
        }
        finally
        {
            _busy = false;
            _updateAll.interactable = true;
        }
    }

    private static string Author(SteamWorkshopItem item) =>
        string.IsNullOrWhiteSpace(item.OwnerName) ? "" : $"BY {item.OwnerName.ToUpperInvariant()}";

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value!.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
}
