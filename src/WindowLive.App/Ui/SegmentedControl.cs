using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowLive.App.Ui;

/// <summary>
/// Full-width N-way segmented control per
/// <c>design_handoff_project_window_1b</c> section 3 (MODEL / OCR pickers):
/// equal-width (star) segments inside a 1px outer frame, 1px internal
/// dividers between segments, radius 0. Unselected segment: transparent
/// background, secondary text, brightens on hover; selected segment: mint
/// fill with dark semibold text.
///
/// Note: the outer frame and the internal dividers use the same border
/// color (#2c2c2c) — that's what both the README ("1px #2c2c2c internal
/// dividers") and the reference HTML's OCR control (`border-left:1px solid
/// #2c2c2c`) specify. Divider (#242424) is reserved for popup-footer /
/// tray-menu style hairlines elsewhere in the design.
/// </summary>
internal sealed class SegmentedControl : Border
{
    private readonly Grid _grid = new();
    private readonly List<Border> _segments = new();
    private readonly List<TextBlock> _labels = new();
    private int _selectedIndex = -1;

    /// <summary>Raised when the user clicks a different segment. Not raised by programmatic <see cref="SelectedIndex"/> assignment.</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>
    /// Currently selected segment index, or -1 if none. Setting this
    /// programmatically (e.g. from saved config) updates the visuals but does
    /// NOT raise <see cref="SelectionChanged"/> — only a user click does.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => ApplySelection(value, raise: false);
    }

    public SegmentedControl()
    {
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(0);
        Child = _grid;
    }

    public SegmentedControl(params string[] labels) : this()
    {
        SetSegments(labels);
    }

    /// <summary>(Re)builds the segments. Preserves <see cref="SelectedIndex"/> if it's still in range, without raising <see cref="SelectionChanged"/>.</summary>
    public void SetSegments(params string[] labels)
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _segments.Clear();
        _labels.Clear();

        for (int i = 0; i < labels.Length; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = labels[i],
                FontFamily = Theme.UiFontFamily,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var segment = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = label,
            };

            int index = i;
            segment.MouseLeftButtonUp += (_, _) => ApplySelection(index, raise: true);
            segment.MouseEnter += (_, _) => UpdateHoverVisual(index, hovered: true);
            segment.MouseLeave += (_, _) => UpdateHoverVisual(index, hovered: false);

            Grid.SetColumn(segment, i);
            _grid.Children.Add(segment);
            _segments.Add(segment);
            _labels.Add(label);
        }

        int previousSelection = _selectedIndex;
        _selectedIndex = -1;
        if (previousSelection >= 0 && previousSelection < _segments.Count)
            ApplySelection(previousSelection, raise: false);
        else
            RefreshAllVisuals();
    }

    private void ApplySelection(int index, bool raise)
    {
        if (index < -1 || index >= _segments.Count)
            return;
        if (index == _selectedIndex)
            return;

        _selectedIndex = index;
        RefreshAllVisuals();

        if (raise)
            SelectionChanged?.Invoke(index);
    }

    private void RefreshAllVisuals()
    {
        for (int i = 0; i < _segments.Count; i++)
            SetSegmentVisual(i, hovered: false);
    }

    private void UpdateHoverVisual(int index, bool hovered)
    {
        if (index == _selectedIndex)
            return; // selected segment ignores hover styling
        SetSegmentVisual(index, hovered);
    }

    private void SetSegmentVisual(int index, bool hovered)
    {
        bool selected = index == _selectedIndex;
        Border segment = _segments[index];
        TextBlock label = _labels[index];

        if (selected)
        {
            segment.Background = Theme.Accent;
            label.Foreground = Theme.TextOnAccent;
            label.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            segment.Background = Brushes.Transparent;
            label.Foreground = hovered ? Theme.TextPrimary : Theme.TextSecondary;
            label.FontWeight = FontWeights.Normal;
        }
    }
}
