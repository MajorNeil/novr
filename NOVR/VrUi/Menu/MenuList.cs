using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// A list longer than it shows (missions, lobbies, Workshop items): a fixed set of row buttons over a window of the entries.
// The window follows the focus past its ends (the screen's OnOrderEnd calls Scroll) and the mouse wheel; a bar on its
// right shows where it is. Each row is one button, with the entry's columns as its text. Selecting an entry is the
// screen's business; the selected one shows highlighted.
internal sealed class MenuList
{
    public readonly struct Column
    {
        public Column(float x, float width, TextAlignmentOptions alignment = TextAlignmentOptions.Left, bool dim = false)
        {
            X = x;
            Width = width;
            Alignment = alignment;
            Dim = dim;
        }

        // From the row's centre.
        public float X { get; }
        public float Width { get; }
        public TextAlignmentOptions Alignment { get; }
        public bool Dim { get; }
    }

    private const float BarWidth = 6f;
    private const float BarGap = 10f;

    private readonly List<TextMeshProUGUI[]> _cells = new();
    private readonly Action<int, TextMeshProUGUI[]> _fill;
    private readonly Image _track;
    private readonly Image _thumb;
    private readonly float _height;

    // fill(entry, cells) writes an entry's columns into a row's texts.
    public MenuList(Transform parent, string name, Vector2 center, float width, int rows, float rowHeight, float rowStep,
        Column[] columns, Action<int, TextMeshProUGUI[]> fill, float textSize = MenuStyle.SmallSize)
    {
        _fill = fill;
        _height = rowHeight + (rows - 1) * rowStep;
        Rect = MenuKit.Rect(parent, name, center, new Vector2(width, _height));
        // Takes the wheel over the gaps between rows too.
        var catcher = MenuKit.Image(Rect, "Wheel", Color.clear, Vector2.zero, Vector2.zero);
        MenuKit.Stretch(catcher.rectTransform);
        catcher.raycastTarget = true;
        Rect.gameObject.AddComponent<MenuWheel>().Scrolled += steps => Scroll(steps, sound: true);

        var rowWidth = width - BarWidth - BarGap;
        var top = _height / 2f - rowHeight / 2f;
        for (var index = 0; index < rows; index++)
        {
            var row = index;
            var button = MenuKit.Button(Rect, $"Row {index}", "", new Vector2(-(BarWidth + BarGap) / 2f, top - index * rowStep),
                new Vector2(rowWidth, rowHeight), () => Activated?.Invoke(Offset + row), MenuButton.Look.Secondary, textSize);
            var cells = new TextMeshProUGUI[columns.Length];
            for (var column = 0; column < columns.Length; column++)
            {
                var spec = columns[column];
                cells[column] = MenuKit.Text(button.transform, $"Column {column}", "", textSize, spec.Dim ? MenuStyle.TextDim : MenuStyle.Text,
                    spec.Alignment, new Vector2(spec.X, 0f), new Vector2(spec.Width, rowHeight));
            }

            Rows.Add(button);
            _cells.Add(cells);
        }

        _track = MenuKit.Image(Rect, "Track", MenuStyle.TextDim.WithAlpha(0.15f), new Vector2(width / 2f - BarWidth / 2f, 0f), new Vector2(BarWidth, _height));
        _thumb = MenuKit.Image(_track.transform, "Thumb", MenuStyle.Accent.WithAlpha(0.8f), Vector2.zero, new Vector2(BarWidth, _height));
        Refresh();
    }

    public RectTransform Rect { get; }
    public List<MenuButton> Rows { get; } = new();
    public int Count { get; private set; }
    public int Offset { get; private set; }
    public int Selected { get; set; } = -1;
    public event Action<int>? Activated;

    public void SetCount(int count)
    {
        Count = Mathf.Max(0, count);
        Offset = Mathf.Clamp(Offset, 0, MaxOffset);
        Refresh();
    }

    // The entry a row shows, or -1.
    public int EntryOf(MenuButton? row)
    {
        var index = row != null ? Rows.IndexOf(row) : -1;
        return index >= 0 && Offset + index < Count ? Offset + index : -1;
    }

    // By steps rows; false when it is already at that end.
    public bool Scroll(int steps, bool sound = false)
    {
        var offset = Mathf.Clamp(Offset + steps, 0, MaxOffset);
        if (offset == Offset) return false;

        Offset = offset;
        Refresh();
        if (sound) MenuSounds.Move();
        return true;
    }

    // Up (-1) or Down (1) off the top or bottom row scrolls instead, while there is more that way; true when it did.
    public bool StepPast(MenuButton row, int step)
    {
        var index = Rows.IndexOf(row);
        if (index < 0) return false;
        if (step < 0 && index == 0) return Scroll(-1, sound: true);
        if (step > 0 && index == Rows.Count - 1) return Scroll(1, sound: true);
        return false;
    }

    // The row showing an entry, scrolled to; null when there is no such entry.
    public MenuButton? RowFor(int entry)
    {
        if (entry < 0 || entry >= Count) return null;

        ScrollTo(entry);
        return Rows[entry - Offset];
    }

    public void ScrollTo(int entry)
    {
        if (entry < 0) return;

        if (entry < Offset) Offset = entry;
        else if (entry >= Offset + Rows.Count) Offset = entry - Rows.Count + 1;
        Offset = Mathf.Clamp(Offset, 0, MaxOffset);
        Refresh();
    }

    public void Reset()
    {
        Offset = 0;
        Refresh();
    }

    // "12-22 OF 85".
    public string Position => Count == 0 ? "" : $"{Offset + 1}-{Mathf.Min(Count, Offset + Rows.Count)} OF {Count}";

    public void Refresh()
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            var entry = Offset + index;
            var row = Rows[index];
            var shown = entry < Count;
            if (row.gameObject.activeSelf != shown) row.gameObject.SetActive(shown);
            if (!shown) continue;

            row.Highlight = entry == Selected;
            _fill(entry, _cells[index]);
        }

        var scrolls = Count > Rows.Count;
        _track.gameObject.SetActive(scrolls);
        if (!scrolls) return;

        var thumbHeight = Mathf.Max(24f, _height * Rows.Count / Count);
        var travel = _height - thumbHeight;
        _thumb.rectTransform.sizeDelta = new Vector2(BarWidth, thumbHeight);
        _thumb.rectTransform.anchoredPosition = new Vector2(0f, _height / 2f - thumbHeight / 2f - travel * Offset / Mathf.Max(1, MaxOffset));
    }

    private int MaxOffset => Mathf.Max(0, Count - Rows.Count);
}

// The mouse wheel over a list, three rows a notch.
internal sealed class MenuWheel : MonoBehaviour, IScrollHandler
{
    private const int RowsPerNotch = 3;

    public event Action<int>? Scrolled;

    public void OnScroll(PointerEventData eventData)
    {
        var notches = eventData.scrollDelta.y;
        if (Mathf.Approximately(notches, 0f)) return;

        Scrolled?.Invoke(notches > 0f ? -RowsPerNotch : RowsPerNotch);
    }
}
