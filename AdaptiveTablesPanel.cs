using System;
using System.Windows;
using System.Windows.Controls;

namespace BlitzkriegWPF;

/// <summary>
/// Панель показывает таблицы в два столбца, пока открыт генератор,
/// и в один горизонтальный ряд, когда генератор скрыт. Одна и та же панель не даёт
/// пересоздавать все таблицы DataGrid при каждом переключении режима.
/// </summary>
public sealed class AdaptiveTablesPanel : Panel
{
    public static readonly DependencyProperty IsHorizontalProperty = DependencyProperty.Register(
        nameof(IsHorizontal),
        typeof(bool),
        typeof(AdaptiveTablesPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public bool IsHorizontal
    {
        get => (bool)GetValue(IsHorizontalProperty);
        set => SetValue(IsHorizontalProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
            return new Size(0, 0);

        if (IsHorizontal)
        {
            double desiredWidth = 0;
            double desiredHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                desiredWidth += child.DesiredSize.Width;
                desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
            }

            return new Size(desiredWidth, desiredHeight);
        }

        double cellWidth = double.IsInfinity(availableSize.Width)
            ? InternalChildren.Count * 450.0 / 2.0
            : availableSize.Width / 2.0;
        double cellHeight = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, availableSize.Height));
            cellHeight = Math.Max(cellHeight, child.DesiredSize.Height);
        }

        int rows = (InternalChildren.Count + 1) / 2;
        return new Size(cellWidth * 2, cellHeight * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsHorizontal)
        {
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                double width = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }

        double cellWidth = finalSize.Width / 2.0;
        int rows = (InternalChildren.Count + 1) / 2;
        double cellHeight = rows > 0 ? finalSize.Height / rows : 0;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / 2;
            int column = i % 2;
            InternalChildren[i].Arrange(new Rect(column * cellWidth, row * cellHeight, cellWidth, cellHeight));
        }

        return finalSize;
    }
}
