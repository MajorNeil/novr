using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// The screens the main menu leads to (single player, multiplayer, the Workshop, key bindings), built and used the way the
// settings are, so one way of getting around works on all of them, with the mouse, the eyes, a gamepad or a stick:
// - The left-hand panel holds the screen's sections and a few actions. Up and Down run down it, Select shows a section,
//   Right goes into it, Back leaves the screen.
// - A section is one column of rows in the panel to the right, each row one button as in the settings: its name on the
//   left, its value on the right. Up and Down run down the column (a list in it scrolls at its ends), Left and Right
//   change a setting, Select steps a setting on or does what the row does. Left off a row that isn't a setting, or
//   Back, goes back to the panel. The tab bindings change section from anywhere.
// - Beside the column, whatever is focused is described. Pointing at a row (mouse, eyes) focuses it as the controls do.
// - Select (or Right) on an entry of a list (a mission, a lobby, an item, an action) opens a box over the screen with what
//   can be done with it, one button under the other as in the main menu, ending in Cancel; Back closes it.
internal abstract class RailScreen : MenuScreen
{
    protected const float ContentX = 300f;
    protected static readonly Vector2 ContentSize = new(1200f, 960f);
    protected const float ColumnX = -240f;
    protected const float ColumnWidth = 640f;
    protected const float DetailsX = 340f;
    protected const float DetailsWidth = 440f;
    protected const int Slots = 11;
    private const float TitleY = 410f;
    private const float FirstRowY = 330f;
    private const float RowHeight = 56f;
    private const float RowStep = 64f;
    private const float ValueWidth = 240f;
    private const float FieldLabelWidth = 220f;
    private const float RailButtonWidth = 440f;
    private const float RailButtonHeight = 52f;
    protected const float RailStep = 60f;
    protected const float RailGap = 18f;
    private static readonly Vector2 PreviewSize = new(440f, 248f);

    // A list row: its entry's name on the left, its value on the right.
    private static readonly MenuList.Column[] ListColumns =
    {
        new(-90f, 410f),
        new(205f, 190f, TextAlignmentOptions.Right, dim: true)
    };

    protected sealed class Section
    {
        internal Section(RectTransform root, string title)
        {
            Root = root;
            Title = title;
        }

        public RectTransform Root { get; }
        public string Title { get; set; }
        public MenuList? List { get; internal set; }
        internal readonly List<MenuButton> Column = new();
        internal readonly Dictionary<MenuButton, (string Label, string Description)> Rows = new();
        internal readonly List<MenuSetting> Settings = new();
        internal int UsedSlots;
        internal MenuButton? LastFocus;
    }

    private readonly RectTransform _rail;
    private readonly List<MenuButton> _railButtons = new();
    private readonly List<Section> _sections = new();
    private readonly List<(List<MenuButton> Order, MenuList List)> _lists = new();
    private readonly RectTransform _content;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _count;
    private readonly TextMeshProUGUI _status;
    private readonly TextMeshProUGUI _prompts;
    private readonly Image _image;
    private readonly TextMeshProUGUI _detailsTitle;
    private readonly TextMeshProUGUI _detailsSubtitle;
    private readonly TextMeshProUGUI _detailsFacts;
    private readonly TextMeshProUGUI _detailsBody;
    private float _railY;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    // The box over the screen.
    private const int PopupButtons = 5;
    private const float PopupWidth = 760f;
    private const float PopupHeight = 900f;
    private const float PopupButtonHeight = 60f;
    private const float PopupButtonStep = 70f;
    private const int ChoiceRows = 8;
    private readonly RectTransform _popup;
    private readonly RectTransform _popupBox;
    private readonly Image _popupImage;
    private readonly TextMeshProUGUI _popupTitle;
    private readonly TextMeshProUGUI _popupSubtitle;
    private readonly TextMeshProUGUI _popupBody;
    private readonly TextMeshProUGUI _popupStatus;
    private readonly MenuField _popupField;
    private readonly MenuButton[] _popupButtons = new MenuButton[PopupButtons];
    private readonly Func<bool>?[] _popupActions = new Func<bool>?[PopupButtons];
    private readonly MenuButton _popupCancel;
    private readonly MenuList _choiceList;
    private readonly List<MenuButton> _popupOrder = new();
    private readonly List<MenuButton> _popupModal = new();
    private IReadOnlyList<string> _choices = Array.Empty<string>();
    private Action<int>? _chosen;
    private Action? _popupClosed;
    private MenuButton? _focusBeforePopup;

    protected RailScreen(RectTransform canvas, string name, string title) : base(canvas, name)
    {
        _rail = MenuKit.Panel(Root, "Rail", MainMenuScreen.RailCenter, MainMenuScreen.RailSize).rectTransform;
        var top = MainMenuScreen.RailSize.y / 2f;
        MenuKit.Text(_rail, "Title", title, MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 70f), new Vector2(RailButtonWidth, 64f), heading: true);
        MenuKit.Image(_rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 112f), new Vector2(RailButtonWidth, 2f));
        _railY = top - 160f;
        _prompts = MenuKit.Text(_rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(RailButtonWidth, 34f));

        _content = MenuKit.Panel(Root, "Content", new Vector2(ContentX, 0f), ContentSize).rectTransform;
        _title = MenuKit.Text(_content, "Title", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f), heading: true);
        _count = MenuKit.Text(_content, "Count", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Right,
            new Vector2(0f, TitleY), new Vector2(ContentSize.x - 80f, 64f));
        _status = MenuKit.Text(_content, "Status", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(ColumnX, -400f), new Vector2(ColumnWidth, 70f), wrap: true);

        _image = MenuKit.Image(_content, "Picture", MenuStyle.Button, new Vector2(DetailsX, 290f), PreviewSize);
        _image.preserveAspect = true;
        _image.gameObject.SetActive(false);
        _detailsTitle = MenuKit.Text(_content, "Details Title", "", MenuStyle.ButtonSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(DetailsWidth, 84f), heading: true, wrap: true);
        _detailsSubtitle = MenuKit.Text(_content, "Details Subtitle", "", MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(DetailsWidth, 60f), wrap: true);
        _detailsFacts = MenuKit.Text(_content, "Details Facts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(DetailsWidth, 96f), wrap: true);
        _detailsBody = MenuKit.Text(_content, "Details Text", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(DetailsWidth, 300f), wrap: true);
        LayOutDetails(picture: false);

        _popup = MenuKit.Rect(Root, "Box", Vector2.zero, canvas.sizeDelta);
        var shade = MenuKit.Image(_popup, "Shade", new Color(0f, 0f, 0f, 0.6f), Vector2.zero, Vector2.zero);
        MenuKit.Stretch(shade.rectTransform);
        shade.raycastTarget = true;
        _popupBox = MenuKit.Panel(_popup, "Panel", new Vector2(ContentX, 0f), new Vector2(PopupWidth, PopupHeight)).rectTransform;
        _popupImage = MenuKit.Image(_popupBox, "Picture", MenuStyle.Button, new Vector2(0f, 300f), PreviewSize);
        _popupImage.preserveAspect = true;
        _popupTitle = MenuKit.Text(_popupBox, "Title", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(PopupWidth - 80f, 110f), heading: true, wrap: true);
        _popupSubtitle = MenuKit.Text(_popupBox, "Subtitle", "", MenuStyle.BodySize, MenuStyle.Accent, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(PopupWidth - 80f, 40f));
        _popupBody = MenuKit.Text(_popupBox, "Text", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(PopupWidth - 80f, 200f), wrap: true);
        _popupStatus = MenuKit.Text(_popupBox, "Status", "", MenuStyle.SmallSize, MenuStyle.Danger, TextAlignmentOptions.Left,
            Vector2.zero, new Vector2(PopupWidth - 80f, 34f));
        _popupField = new MenuField(_popupBox, "Field", "", "", Vector2.zero, new Vector2(PopupWidth - 80f, RowHeight), FieldLabelWidth, password: true);
        Add(_popupField.Button);
        for (var index = 0; index < PopupButtons; index++)
        {
            var slot = index;
            _popupButtons[index] = Add(MenuKit.Button(_popupBox, $"Action {index}", "", Vector2.zero, new Vector2(PopupWidth - 80f, PopupButtonHeight),
                () => RunPopupAction(slot)));
        }

        _popupCancel = Add(MenuKit.Button(_popupBox, "Cancel", "CANCEL", new Vector2(0f, -PopupHeight / 2f + 50f),
            new Vector2(PopupWidth - 80f, PopupButtonHeight), ClosePopup, MenuButton.Look.Secondary));
        _choiceList = new MenuList(_popupBox, "Choices", new Vector2(0f, 300f - (RowHeight + (ChoiceRows - 1) * RowStep) / 2f), PopupWidth - 80f,
            ChoiceRows, RowHeight, RowStep, new[] { new MenuList.Column(0f, PopupWidth - 160f) }, FillChoice, MenuStyle.BodySize);
        _choiceList.Activated += Choose;
        foreach (var row in _choiceList.Rows) Add(row);
        _lists.Add((_popupOrder, _choiceList));
        _popup.gameObject.SetActive(false);
    }

    public bool PopupOpen => _popup.gameObject.activeSelf;

    protected Section? Current { get; private set; }

    // The panel's entry for what is shown (the section or the group), where Left and Back from the rows go.
    protected MenuButton? CurrentRailButton { get; set; }

    // The screen's way out (the game's own Back, or closing).
    protected abstract void Leave();

    // What the focused row (entry >= 0 on a list row) shows beside the column; by default a row's own description.
    protected virtual void Describe(Section section, MenuButton? row, int entry)
    {
        if (row != null && section.Rows.TryGetValue(row, out var described)) ShowDetails(described.Label, "", "", described.Description);
        else ShowDetails("", "", "", "");
    }

    // Select or Right on a list entry.
    protected virtual void Open(Section section, int entry)
    {
    }

    // ---- Building.

    protected MenuButton AddRailButton(string label, Action activate, bool gapBefore = false, bool primary = false)
    {
        if (gapBefore) _railY -= RailGap;
        var button = Add(MenuKit.Button(_rail, label, label, new Vector2(0f, _railY), new Vector2(RailButtonWidth, primary ? 56f : RailButtonHeight), activate,
            primary ? MenuButton.Look.Primary : MenuButton.Look.Secondary));
        _railButtons.Add(button);
        _railY -= RailStep;
        RefreshOrders();
        return button;
    }

    protected void AddRailBack() => AddRailButton("BACK", Leave, gapBefore: true, primary: true);

    protected Section AddSection(string title)
    {
        var section = new Section(MenuKit.Rect(_content, title, Vector2.zero, ContentSize), title);
        section.Root.gameObject.SetActive(false);
        _sections.Add(section);
        RefreshOrders();
        return section;
    }

    protected MenuSetting AddSetting(Section section, string label, Func<string> read, Action<int>? adjust, Action? activate = null,
        string description = "")
    {
        MenuSetting? setting = null;
        setting = new MenuSetting(section.Root, label, NextRow(section), new Vector2(ColumnWidth, RowHeight), ValueWidth, read, adjust, activate,
            () =>
            {
                setting?.Refresh();
                OnSettingChanged(section);
            });
        AddRow(section, setting.Button, label, description);
        section.Settings.Add(setting);
        return setting;
    }

    protected MenuField AddField(Section section, string label, string placeholder, string description = "", bool password = false,
        int characterLimit = 0)
    {
        var field = new MenuField(section.Root, label, label, placeholder, NextRow(section), new Vector2(ColumnWidth, RowHeight), FieldLabelWidth,
            password, characterLimit);
        AddRow(section, field.Button, label, description);
        return field;
    }

    protected MenuButton AddAction(Section section, string label, Action activate, string description = "", bool primary = false)
    {
        var button = MenuKit.Button(section.Root, label, label, NextRow(section), new Vector2(ColumnWidth, RowHeight), activate,
            primary ? MenuButton.Look.Primary : MenuButton.Look.Secondary, MenuStyle.BodySize);
        AddRow(section, button, label, description);
        return button;
    }

    // rows of the column's slots, for entries filled in by fill(entry, name, value).
    protected MenuList AddList(Section section, int rows, Action<int, TextMeshProUGUI, TextMeshProUGUI> fill)
    {
        var top = FirstRowY - section.UsedSlots * RowStep + RowHeight / 2f;
        var height = RowHeight + (rows - 1) * RowStep;
        var list = new MenuList(section.Root, "List", new Vector2(ColumnX, top - height / 2f), ColumnWidth, rows, RowHeight, RowStep, ListColumns,
            (entry, cells) => fill(entry, cells[0], cells[1]), MenuStyle.BodySize);
        list.Activated += entry => Open(section, entry);
        foreach (var row in list.Rows)
        {
            Add(row);
            section.Column.Add(row);
        }

        section.List = list;
        section.UsedSlots += rows;
        _lists.Add((section.Column, list));
        return list;
    }

    // The values of a section's settings, after what they show has changed.
    protected static void RefreshRows(Section section)
    {
        foreach (var setting in section.Settings) setting.Refresh();
    }

    // Called after a setting changes, for what depends on it.
    protected virtual void OnSettingChanged(Section section)
    {
        RefreshDetails();
    }

    private Vector2 NextRow(Section section) => new(ColumnX, FirstRowY - section.UsedSlots * RowStep);

    private void AddRow(Section section, MenuButton button, string label, string description)
    {
        Add(button);
        section.Column.Add(button);
        section.Rows[button] = (label, description);
        section.UsedSlots++;
    }

    private void RefreshOrders()
    {
        var orders = new List<List<MenuButton>> { _railButtons, _popupOrder };
        foreach (var section in _sections) orders.Add(section.Column);
        SetOrders(orders.ToArray());
    }

    // ---- Showing.

    protected void ShowSection(Section section)
    {
        if (Current == section)
        {
            _title.text = section.Title;
            return;
        }

        foreach (var other in _sections) other.Root.gameObject.SetActive(other == section);
        Current = section;
        RefreshRows(section);
        _title.text = section.Title;
        RefreshDetails();
    }

    // Into the section shown: where the focus was in it, else its first row that can take it.
    protected void EnterSection()
    {
        if (Current == null) return;
        if (FocusFirst(Current.LastFocus)) return;

        foreach (var row in Current.Column)
        {
            if (FocusFirst(row)) return;
        }
    }

    protected void SetCount(string text) => _count.text = text;

    protected void SetStatus(string text) => _status.text = text;

    protected Image DetailsPicture => _image;

    // Beside the column; a picture above when shown (the Workshop's).
    protected void ShowDetails(string title, string subtitle, string facts, string body, bool picture = false)
    {
        LayOutDetails(picture);
        _detailsTitle.text = title;
        _detailsSubtitle.text = subtitle;
        _detailsFacts.text = facts;
        _detailsBody.text = body;
    }

    protected void RefreshDetails()
    {
        if (Current == null) return;

        var focused = Focused;
        var inColumn = focused != null && Current.Column.Contains(focused);
        Describe(Current, inColumn ? focused : null, inColumn && Current.List != null ? Current.List.EntryOf(focused) : -1);
    }

    private void LayOutDetails(bool picture)
    {
        _image.gameObject.SetActive(picture);
        var y = picture ? 290f - PreviewSize.y / 2f - 20f : 380f;
        Place(_detailsTitle, ref y, 84f);
        Place(_detailsSubtitle, ref y, 60f);
        Place(_detailsFacts, ref y, 96f);
        Place(_detailsBody, ref y, y + 340f);
    }

    private static void Place(TextMeshProUGUI text, ref float top, float height)
    {
        text.rectTransform.sizeDelta = new Vector2(DetailsWidth, height);
        text.rectTransform.anchoredPosition = new Vector2(DetailsX, top - height / 2f);
        top -= height + 6f;
    }

    // ---- The box over the screen.

    // actions: what can be done (each returns whether the box closes), shown in order with the first one focused, then
    // Cancel. A field (a password) shows above them when fieldLabel is given.
    protected void OpenPopup(string title, string subtitle, string body, bool picture, string? fieldLabel,
        params (string Label, Func<bool> Action)[] actions)
    {
        _popupClosed = null;
        _choices = Array.Empty<string>();
        _chosen = null;
        _choiceList.Rect.gameObject.SetActive(false);
        _popupImage.gameObject.SetActive(picture);
        _popupTitle.text = title;
        _popupSubtitle.text = subtitle;
        _popupBody.text = body;
        _popupStatus.text = "";

        var bottom = -PopupHeight / 2f + 50f;
        var buttonsTop = bottom + Mathf.Min(actions.Length, PopupButtons) * PopupButtonStep + PopupButtonHeight / 2f;
        _popupOrder.Clear();
        _popupModal.Clear();
        var withField = fieldLabel != null;
        _popupField.Button.gameObject.SetActive(withField);
        if (withField)
        {
            _popupField.Button.SetLabel(fieldLabel!);
            _popupField.Value = "";
            _popupField.Button.Rect.anchoredPosition = new Vector2(0f, buttonsTop + 20f + RowHeight / 2f);
            _popupOrder.Add(_popupField.Button);
            buttonsTop += 20f + RowHeight;
        }

        _popupStatus.rectTransform.anchoredPosition = new Vector2(0f, buttonsTop + 30f);
        var textTop = picture ? 300f - PreviewSize.y / 2f - 24f : PopupHeight / 2f - 40f;
        PlacePopup(_popupTitle, ref textTop, 110f);
        PlacePopup(_popupSubtitle, ref textTop, 40f);
        PlacePopup(_popupBody, ref textTop, Mathf.Max(40f, textTop - (buttonsTop + 60f)));

        for (var index = 0; index < PopupButtons; index++)
        {
            var shown = index < actions.Length;
            var button = _popupButtons[index];
            button.gameObject.SetActive(shown);
            _popupActions[index] = shown ? actions[index].Action : null;
            if (!shown) continue;

            button.SetLabel(actions[index].Label);
            button.Rect.anchoredPosition = new Vector2(0f, bottom + (actions.Length - index) * PopupButtonStep);
            _popupOrder.Add(button);
        }

        _popupOrder.Add(_popupCancel);
        _popupModal.AddRange(_popupOrder);
        ShowPopup(actions.Length > 0 ? _popupButtons[0] : _popupCancel);
    }

    // A pick from a list of options (a mission to host, say): Select on one picks it.
    protected void OpenChoice(string title, IReadOnlyList<string> choices, int current, Action<int> chosen)
    {
        _popupClosed = null;
        _choices = choices;
        _chosen = chosen;
        _popupImage.gameObject.SetActive(false);
        _popupField.Button.gameObject.SetActive(false);
        foreach (var button in _popupButtons) button.gameObject.SetActive(false);
        _popupTitle.text = title;
        _popupSubtitle.text = "";
        _popupBody.text = "";
        _popupStatus.text = "";
        var textTop = PopupHeight / 2f - 40f;
        PlacePopup(_popupTitle, ref textTop, 64f);

        _choiceList.Rect.gameObject.SetActive(true);
        _choiceList.Selected = current;
        _choiceList.SetCount(choices.Count);
        _choiceList.Reset();
        _popupOrder.Clear();
        _popupModal.Clear();
        _popupOrder.AddRange(_choiceList.Rows);
        _popupOrder.Add(_popupCancel);
        _popupModal.AddRange(_popupOrder);
        ShowPopup(_choiceList.RowFor(current) ?? (_choiceList.Count > 0 ? _choiceList.Rows[0] : _popupCancel));
    }

    protected MenuField PopupField => _popupField;

    protected Image PopupPicture => _popupImage;

    protected void SetPopupStatus(string text) => _popupStatus.text = text;

    // Run when the box closes, however it closes.
    protected void OnPopupClosed(Action closed) => _popupClosed = closed;

    protected void ClosePopup()
    {
        if (!PopupOpen) return;

        MenuField.StopTyping();
        _popup.gameObject.SetActive(false);
        SetModal(null, null);
        FocusFirst(_focusBeforePopup);
        var closed = _popupClosed;
        _popupClosed = null;
        closed?.Invoke();
    }

    private void ShowPopup(MenuButton focus)
    {
        if (!PopupOpen) _focusBeforePopup = Focused;
        _popup.gameObject.SetActive(true);
        _popup.SetAsLastSibling();
        SetModal(_popupModal, focus);
    }

    private static void PlacePopup(TextMeshProUGUI text, ref float top, float height)
    {
        text.rectTransform.sizeDelta = new Vector2(PopupWidth - 80f, height);
        text.rectTransform.anchoredPosition = new Vector2(0f, top - height / 2f);
        top -= height + 8f;
    }

    private void RunPopupAction(int slot)
    {
        var action = _popupActions[slot];
        if (action != null && action()) ClosePopup();
    }

    private void FillChoice(int entry, TextMeshProUGUI[] cells)
    {
        cells[0].text = _choices[entry];
        cells[0].color = entry == _choiceList.Selected ? MenuStyle.Accent : MenuStyle.Text;
    }

    private void Choose(int entry)
    {
        if (entry < 0 || entry >= _choices.Count) return;

        var chosen = _chosen;
        ClosePopup();
        chosen?.Invoke(entry);
    }

    // ---- Getting around.

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        ClosePopup();
    }

    protected override void OnHidden()
    {
        ClosePopup();
    }

    protected override void OnTick()
    {
        if (_promptDevice == MenuInput.LastDevice) return;

        _promptDevice = MenuInput.LastDevice;
        _prompts.text = $"{Prompt(MenuCommand.Select)} Select   {Prompt(MenuCommand.Back)} Back";
    }

    protected override MenuButton? FirstFocus() => CurrentRailButton;

    protected override bool OnBack()
    {
        if (PopupOpen)
        {
            ClosePopup();
            return true;
        }

        var focused = Focused;
        if (focused != null && !_railButtons.Contains(focused) && CurrentRailButton != null)
        {
            Focus(CurrentRailButton, sound: false);
            return true;
        }

        Leave();
        return true;
    }

    protected override bool OnOrderStep(List<MenuButton> order, MenuButton focused, int step)
    {
        foreach (var (listOrder, list) in _lists)
        {
            if (listOrder != order || !list.Rows.Contains(focused)) continue;
            if (!list.StepPast(focused, step)) return false;

            RefreshDetails();
            OnListScrolled(list);
            return true;
        }

        return false;
    }

    protected virtual void OnListScrolled(MenuList list)
    {
    }

    // Each column ends there.
    protected override bool OnOrderEnd(List<MenuButton> order, int step) => true;

    protected override bool OnSideways(MenuButton focused, int direction)
    {
        if (PopupOpen) return true;

        if (_railButtons.Contains(focused))
        {
            if (direction > 0) EnterSection();
            return true;
        }

        if (Current == null || !Current.Column.Contains(focused)) return false;

        if (direction < 0)
        {
            if (CurrentRailButton != null) Focus(CurrentRailButton, sound: true);
        }
        else if (Current.List != null && Current.List.EntryOf(focused) is var entry && entry >= 0)
        {
            Open(Current, entry);
        }

        return true;
    }

    protected override void OnFocused(MenuButton? button)
    {
        if (button != null && Current != null && Current.Column.Contains(button)) Current.LastFocus = button;
        if (!PopupOpen) RefreshDetails();
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";
}
