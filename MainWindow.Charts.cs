// -----------------------------------------------------------------------
// <copyright file="MainWindow.Charts.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonzoManager.Services;
using Windows.Foundation;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private IReadOnlyList<MonthlySnapshot> _chartHistory = [];
    private readonly List<Point> _chartPoints = [];
    private Border? _chartTooltip;
    private TextBlock? _chartTooltipText;
    private Ellipse? _chartMarker;
    private Line? _chartGuide;

    private async void NetWorthRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceReady)
        {
            UpdateChartRange(animate: true);
            if (_preferencesLoaded)
            {
                await PersistViewAsync();
            }
        }
    }

    private void UpdateChartRange(bool animate)
    {
        int months = ChartRangeMonths();
        IReadOnlyList<MonthlySnapshot> history = DisplayedMonthlyHistory();
        _chartHistory = months > 0 && history.Count > 0
            ? history.Where(snapshot => (history[^1].Month.Year - snapshot.Month.Year) * 12
                + history[^1].Month.Month - snapshot.Month.Month < months).ToArray()
            : history;
        ChartEmpty.Visibility = _chartHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChartCaption.Text = _chartHistory.Count switch
        {
            0 => "Monthly snapshots · GBP",
            1 => $"{_chartHistory[0].Month:MMMM yyyy} · 1 snapshot · GBP",
            _ => $"{_chartHistory[0].Month:MMM yyyy} – {_chartHistory[^1].Month:MMM yyyy} · {_chartHistory.Count} snapshots · GBP",
        };
        DrawNetWorthChart();
        if (animate && _chartHistory.Count > 0)
        {
            FadeIn(NetWorthChartCanvas);
        }
    }

    private int ChartRangeMonths() => (NetWorthRange.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "6" => 6,
        "12" => 12,
        _ => 0,
    };

    private IReadOnlyList<MonthlySnapshot> DisplayedMonthlyHistory() => AccessibleView.IsChecked == true
        ? _monthlyHistory.Where(snapshot => snapshot.Balances.Any(IsAccessible))
            .Select(snapshot => MonthlySnapshot.FromBalances(snapshot.Month, snapshot.Balances.Where(IsAccessible).ToArray()))
            .ToArray()
        : _monthlyHistory;

    private void NetWorthChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspaceReady)
        {
            DrawNetWorthChart();
        }
    }

    private void DrawNetWorthChart()
    {
        Canvas canvas = NetWorthChartCanvas;
        canvas.Children.Clear();
        _chartPoints.Clear();
        _chartTooltip = null;
        const double top = 13;
        const double left = 58;
        double right = canvas.ActualWidth - 12;
        double bottom = canvas.ActualHeight - 26;
        if (right <= left || bottom <= top)
        {
            return;
        }

        double min = _chartHistory.Count == 0 ? 0 : (double)_chartHistory.Min(snapshot => snapshot.NetWorth);
        double max = _chartHistory.Count == 0 ? 40000 : (double)_chartHistory.Max(snapshot => snapshot.NetWorth);
        double padding = Math.Max((max - min) * 0.12, Math.Max(Math.Abs(max), 100) * 0.015);
        double step = ChartStep((max - min + 2 * padding) / 4);
        double lower = Math.Floor((min - padding) / step) * step;
        double upper = Math.Ceiling((max + padding) / step) * step;

        for (int i = 0; i <= 4; i++)
        {
            double y = top + (bottom - top) * i / 4;
            canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = Brush("#3A4942"), StrokeThickness = 1 });
            if (_chartHistory.Count > 0)
            {
                double tick = upper - (upper - lower) * i / 4;
                AddChartLabel(canvas, AxisCurrency(tick), 0, y - 9, left - 8, TextAlignment.Right);
            }
        }

        if (_chartHistory.Count == 0)
        {
            return;
        }

        int timeSpan = _chartHistory[^1].Month.DayNumber - _chartHistory[0].Month.DayNumber;
        foreach (MonthlySnapshot entry in _chartHistory)
        {
            double time = timeSpan == 0 ? 0.5 : (entry.Month.DayNumber - _chartHistory[0].Month.DayNumber) / (double)timeSpan;
            double x = left + time * (right - left);
            double y = bottom - ((double)entry.NetWorth - lower) / (upper - lower) * (bottom - top);
            _chartPoints.Add(new Point(x, y));
        }

        if (_chartPoints.Count > 1)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(102, 218, 176, 100), Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(4, 218, 176, 100), Offset = 1 });

            PointCollection area = [];
            PointCollection line = [];
            area.Add(new Point(_chartPoints[0].X, bottom));
            foreach (Point point in _chartPoints)
            {
                area.Add(point);
                line.Add(point);
            }

            area.Add(new Point(_chartPoints[^1].X, bottom));
            canvas.Children.Add(new Polygon { Points = area, Fill = gradient });
            canvas.Children.Add(new Polyline
            {
                Points = line,
                Stroke = Brush("#F1CC86"),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        var endpoint = new Ellipse { Width = 10, Height = 10, Fill = Brush("#FFE0A7"), Stroke = Brush("#19352F"), StrokeThickness = 2 };
        Position(canvas, endpoint, _chartPoints[^1].X - 5, _chartPoints[^1].Y - 5);

        int labels = Math.Min(_chartHistory.Count, Math.Clamp((int)((right - left) / 81), 1, 5));
        double previousX = double.NegativeInfinity;
        for (int i = 0; i < labels; i++)
        {
            int index = labels == 1 ? _chartHistory.Count - 1 : i * (_chartHistory.Count - 1) / (labels - 1);
            double x = Math.Clamp(_chartPoints[index].X - 35, left - 13, Math.Max(left - 13, right - 68));
            if (x - previousX >= 69)
            {
                AddChartLabel(canvas, _chartHistory[index].Month.ToString("MMM yy", Uk), x, bottom + 9, 70, TextAlignment.Center);
                previousX = x;
            }
        }

        _chartGuide = new Line { Y1 = top, Y2 = bottom, Stroke = Brush("#6C988D"), StrokeThickness = 1, Visibility = Visibility.Collapsed };
        _chartMarker = new Ellipse { Width = 10, Height = 10, Fill = Brush("#F1CC86"), Stroke = Brush("#131515"), StrokeThickness = 2, Visibility = Visibility.Collapsed };
        _chartTooltipText = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _chartTooltip = new Border
        {
            Background = Brush("#26352F"),
            BorderBrush = Brush("#3D5959"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            Child = _chartTooltipText,
        };
        canvas.Children.Add(_chartGuide);
        canvas.Children.Add(_chartMarker);
        canvas.Children.Add(_chartTooltip);
    }

    private static double ChartStep(double rough)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        return new[] { 1, 2, 2.5, 5, 10 }.First(factor => factor * magnitude >= rough) * magnitude;
    }

    private static string AxisCurrency(double amount)
    {
        string sign = amount < -0.0001 ? "−" : "";
        double value = Math.Abs(amount);
        return value switch
        {
            >= 1000000000 => $"{sign}£{value / 1000000000:0.##}bn",
            >= 1000000 => $"{sign}£{value / 1000000:0.##}m",
            >= 1000 => $"{sign}£{value / 1000:0.##}k",
            _ => $"{sign}£{value:0.##}",
        };
    }

    private static void AddChartLabel(Canvas canvas, string label, double x, double y, double width, TextAlignment alignment)
    {
        Position(canvas, new TextBlock
        {
            Text = label,
            Width = width,
            Foreground = Brush("#A6AFAD"),
            FontSize = 11,
            TextAlignment = alignment,
        }, x, y);
    }

    private static void Position(Canvas canvas, FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    private void NetWorthChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_chartTooltip is null || _chartMarker is null || _chartGuide is null || _chartTooltipText is null)
        {
            return;
        }

        double x = e.GetCurrentPoint(NetWorthChartCanvas).Position.X;
        int index = 0;
        for (int i = 1; i < _chartPoints.Count; i++)
        {
            if (Math.Abs(x - _chartPoints[i].X) < Math.Abs(x - _chartPoints[index].X))
            {
                index = i;
            }
        }

        Point point = _chartPoints[index];
        MonthlySnapshot snapshot = _chartHistory[index];
        _chartTooltipText.Text = $"{snapshot.Month:MMM yyyy}   {Money(snapshot.NetWorth)}";
        _chartTooltip.Visibility = Visibility.Visible;
        _chartTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tooltipX = Math.Clamp(point.X + 10, 0, Math.Max(0, NetWorthChartCanvas.ActualWidth - _chartTooltip.DesiredSize.Width));
        Canvas.SetLeft(_chartTooltip, tooltipX);
        Canvas.SetTop(_chartTooltip, Math.Max(0, point.Y - 36));
        Canvas.SetLeft(_chartMarker, point.X - 5);
        Canvas.SetTop(_chartMarker, point.Y - 5);
        _chartMarker.Visibility = Visibility.Visible;
        _chartGuide.X1 = _chartGuide.X2 = point.X;
        _chartGuide.Visibility = Visibility.Visible;
    }

    private void NetWorthChartCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_chartTooltip is not null && _chartMarker is not null && _chartGuide is not null)
        {
            _chartTooltip.Visibility = _chartMarker.Visibility = _chartGuide.Visibility = Visibility.Collapsed;
        }
    }

    private void DrawComposition(IReadOnlyList<MonthlyBalance> balances)
    {
        CompositionCanvas.Children.Clear();
        CompositionRows.Children.Clear();

        var groups = new[]
        {
            (Label: "Monzo cash", Color: "#9CE6C1", Amount: balances.Where(balance => balance.IsMonzo && !balance.IsDebt).Sum(balance => balance.Amount)),
            (Label: "Monzo Pots", Color: "#F1CC86", Amount: balances.Where(balance => balance.Kind == "Pot").Sum(balance => balance.Amount)),
            (Label: "Other assets", Color: "#8AAEEC", Amount: balances.Where(balance => !balance.IsMonzo && !balance.IsDebt && balance.Kind != "Pot").Sum(balance => balance.Amount)),
            (Label: "Debts", Color: "#F3A3A0", Amount: balances.Where(balance => balance.IsDebt).Sum(balance => balance.Amount)),
        };

        decimal total = groups.Sum(group => Math.Abs(group.Amount));
        double start = -Math.PI / 2;
        foreach (var group in groups.Where(group => group.Label != "Monzo Pots" || balances.Any(balance => balance.Kind == "Pot")))
        {
            bool available = balances.Count > 0 && (group.Label != "Monzo cash" || balances.Any(balance => balance.IsMonzo));
            CompositionRows.Children.Add(BalanceLegendRow(group.Label, available ? Money(group.Amount) : "—", group.Color));
            if (group.Amount != 0 && total > 0)
            {
                double sweep = (double)(Math.Abs(group.Amount) / total) * 2 * Math.PI;
                DrawCompositionArc(start, sweep, group.Color);
                start += sweep;
            }
        }
    }

    private void DrawCompositionArc(double start, double sweep, string color)
    {
        Point CirclePoint(double angle) => new(62 + 53.5 * Math.Cos(angle), 62 + 53.5 * Math.Sin(angle));

        double gap = Math.Min(0.045, sweep / 5);
        double arc = sweep - gap * 2;
        var figure = new PathFigure { StartPoint = CirclePoint(start + gap), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = CirclePoint(start + sweep - gap),
            Size = new Size(53.5, 53.5),
            IsLargeArc = arc > Math.PI,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        CompositionCanvas.Children.Add(new Path { Data = geometry, Stroke = Brush(color), StrokeThickness = 9 });
    }

    private static Grid BalanceLegendRow(string label, string amount, string color)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brush(color) });
        var name = new TextBlock { Text = label, Foreground = Brush("#A6AFAD"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var value = new TextBlock { Text = amount, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }
}
