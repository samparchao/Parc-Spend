// -----------------------------------------------------------------------
// <copyright file="ActionRow.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MonzoManager;

/// <summary>
/// Keeps related actions in reading order when the available width changes.
/// </summary>
public sealed class ActionRow : Panel
{
    private const double Gap = 8;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => LayoutChildren(availableSize.Width, arrange: false);

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutChildren(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size LayoutChildren(double width, bool arrange)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        double usedWidth = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed)
            {
                continue;
            }

            if (!arrange)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }

            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += rowHeight + Gap;
                rowHeight = 0;
            }

            if (arrange)
            {
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            }

            usedWidth = Math.Max(usedWidth, x + size.Width);
            rowHeight = Math.Max(rowHeight, size.Height);
            x += size.Width + Gap;
        }

        return new Size(usedWidth, y + rowHeight);
    }
}
