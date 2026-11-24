// -----------------------------------------------------------------------
// <copyright file="MainWindow.History.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MonzoManager.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private async void OpenHistoryGrid_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (_monthlyHistory.Count == 0)
            {
                SetStatus("Record or import a month before opening the history grid.", InfoBarSeverity.Informational);
                return;
            }

            var body = new StackPanel { Spacing = 10 };
            var controls = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
            var earlier = new Button { Content = "Earlier" };
            var later = new Button { Content = "Later" };
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(earlier);
            controls.Children.Add(later);
            controls.Children.Add(label);
            body.Children.Add(controls);
            var view = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                MaxHeight = 420,
            };
            body.Children.Add(view);

            int last = _monthlyHistory.Count;
            void Populate()
            {
                int first = Math.Max(0, last - 8);
                MonthlySnapshot[] page = _monthlyHistory.Skip(first).Take(last - first).ToArray();
                label.Text = $"{page[0].Month:MMM yyyy} – {page[^1].Month:MMM yyyy}";
                earlier.IsEnabled = first > 0;
                later.IsEnabled = last < _monthlyHistory.Count;
                view.Content = BuildHistoryGrid(page);
            }

            earlier.Click += (_, _) => { last = Math.Max(1, last - 8); Populate(); };
            later.Click += (_, _) => { last = Math.Min(_monthlyHistory.Count, last + 8); Populate(); };
            Populate();

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Balance history",
                Content = body,
                PrimaryButtonText = "Export CSV",
                CloseButtonText = "Done",
            };
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ExportHistoryAsync();
            }
        }, "Could not open the history grid.");
    }

    private static StackPanel BuildHistoryGrid(IReadOnlyList<MonthlySnapshot> months)
    {
        Grid MakeGrid(int rowCount)
        {
            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            foreach (MonthlySnapshot _ in months)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            }

            for (int index = 0; index < rowCount; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            return grid;
        }

        string[] rows = HistoryNames(months);
        Grid header = MakeGrid(2);
        Grid body = MakeGrid(rows.Length);
        void Cell(string text, int column, int row, bool emphasis = false)
        {
            var cell = new TextBlock
            {
                Text = text,
                Padding = new Thickness(6, 5, 6, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                FontWeight = emphasis ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Brush(emphasis ? "#9CE6C1" : "#E6EDF2"),
            };
            ToolTipService.SetToolTip(cell, text);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            (emphasis ? header : body).Children.Add(cell);
        }

        Cell("Account", 0, 0, emphasis: true);
        Cell("Net worth", 0, 1, emphasis: true);
        for (int index = 0; index < rows.Length; index++)
        {
            Cell(rows[index], 0, index);
        }

        for (int column = 0; column < months.Count; column++)
        {
            MonthlySnapshot month = months[column];
            Cell(month.Month.ToString("MMM yyyy", Uk), column + 1, 0, emphasis: true);
            Cell(Money(month.NetWorth), column + 1, 1, emphasis: true);
            Dictionary<string, decimal> values = ByName(month);
            for (int row = 0; row < rows.Length; row++)
            {
                Cell(values.TryGetValue(rows[row], out decimal amount) ? Money(amount) : "—", column + 1, row);
            }
        }

        var panel = new StackPanel { Width = 176 + 120 * months.Count };
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled, MaxHeight = 345 });
        return panel;
    }

    private async Task ExportHistoryAsync()
    {
        FileSavePicker picker = CreateSavePicker("CSV spreadsheet", ".csv", "parc-spend-net-worth");
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var csv = new StringBuilder();
        csv.AppendLine("Asset," + string.Join(",", _monthlyHistory.Select(month => CsvCell(month.Month.ToString("MMM yyyy", Uk)))));
        Dictionary<string, decimal>[] values = _monthlyHistory.Select(ByName).ToArray();
        foreach (string name in HistoryNames(_monthlyHistory))
        {
            string cells = string.Join(",", values.Select(month => month.TryGetValue(name, out decimal amount)
                ? amount.ToString(CultureInfo.InvariantCulture) : ""));
            csv.AppendLine(CsvCell(name, userText: true) + "," + cells);
        }

        csv.AppendLine("Total," + string.Join(",", _monthlyHistory.Select(month => month.NetWorth.ToString(CultureInfo.InvariantCulture))));
        await FileIO.WriteTextAsync(file, csv.ToString());
        SetStatus("History exported to CSV.", InfoBarSeverity.Success);
    }

    private static string CsvCell(string text, bool userText = false)
    {
        string trimmed = text.TrimStart();
        if (userText && trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string[] HistoryNames(IEnumerable<MonthlySnapshot> months) =>
        months.SelectMany(month => month.Balances.Select(balance => balance.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static Dictionary<string, decimal> ByName(MonthlySnapshot snapshot) =>
        snapshot.Balances.GroupBy(balance => balance.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(balance => balance.Amount), StringComparer.OrdinalIgnoreCase);

    private static string BalanceIdentity(MonthlyBalance balance, HashSet<string> sharedIds) =>
        balance.AccountId is { } id && sharedIds.Contains(id) ? $"id:{id}" : $"name:{balance.Name}";

    private static StackPanel BuildMonthlyComparison(MonthlySnapshot? previous, MonthlySnapshot current)
    {
        var panel = new StackPanel { Spacing = 7 };
        if (previous is null)
        {
            return panel;
        }

        decimal totalChange = current.NetWorth - previous.NetWorth;
        panel.Children.Add(new TextBlock
        {
            Text = $"{SignedMoney(totalChange)} since {previous.Month:MMM yyyy}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (previous.Balances.Count == 0 || current.Balances.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Both months need account balances to break down the change.", TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        HashSet<string> sharedIds = previous.Balances.Select(balance => balance.AccountId).OfType<string>().ToHashSet();
        sharedIds.IntersectWith(current.Balances.Select(balance => balance.AccountId).OfType<string>());
        var prior = previous.Balances.GroupBy(balance => BalanceIdentity(balance, sharedIds), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(balance => balance.Amount), StringComparer.OrdinalIgnoreCase);
        var currentById = current.Balances.GroupBy(balance => BalanceIdentity(balance, sharedIds), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var steps = new List<ChangeStep>();
        foreach (string key in currentById.Keys.Union(prior.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!currentById.TryGetValue(key, out MonthlyBalance[]? entries))
            {
                string oldName = previous.Balances.First(balance => BalanceIdentity(balance, sharedIds).Equals(key, StringComparison.OrdinalIgnoreCase)).Name;
                steps.Add(new ChangeStep($"{oldName} · absent", -prior[key]));
                continue;
            }

            string name = entries[0].Name;
            decimal delta = entries.Sum(balance => balance.Amount) - prior.GetValueOrDefault(key);
            if (prior.ContainsKey(key) && entries.All(balance => balance.NetContributions.HasValue))
            {
                decimal contributions = entries.Sum(balance => balance.NetContributions!.Value);
                steps.Add(new ChangeStep($"{name} · contributions", contributions));
                steps.Add(new ChangeStep($"{name} · change after contributions", delta - contributions));
            }
            else
            {
                steps.Add(new ChangeStep(prior.ContainsKey(key) ? name : $"{name} · added", delta));
            }
        }

        decimal residual = totalChange - steps.Sum(step => step.Amount);
        if (residual != 0)
        {
            steps.Add(new ChangeStep("Difference from reported totals", residual));
        }

        List<ChangeStep> nonzero = steps.Where(step => step.Amount != 0).OrderByDescending(step => Math.Abs(step.Amount)).ToList();
        if (nonzero.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No account changes recorded." });
            return panel;
        }

        if (nonzero.Count > 12)
        {
            decimal remainder = nonzero.Skip(11).Sum(step => step.Amount);
            nonzero = [.. nonzero.Take(11), new ChangeStep("Other account changes", remainder)];
        }

        panel.Children.Add(BuildWaterfall(nonzero));
        return panel;
    }

    private static StackPanel BuildWaterfall(IReadOnlyList<ChangeStep> steps)
    {
        var running = new List<decimal> { 0 };
        foreach (ChangeStep step in steps)
        {
            running.Add(running[^1] + step.Amount);
        }

        decimal minimum = running.Min();
        decimal span = running.Max() - minimum;
        var chart = new StackPanel { Spacing = 5 };
        for (int index = 0; index < steps.Count; index++)
        {
            ChangeStep step = steps[index];
            chart.Children.Add(BalanceLegendRow(step.Name, SignedMoney(step.Amount), step.Amount < 0 ? "#F3A3A0" : "#9CE6C1"));

            const double chartWidth = 240;
            double start = (double)((running[index] - minimum) / span) * chartWidth;
            double end = (double)((running[index + 1] - minimum) / span) * chartWidth;
            var bar = new Border
            {
                Width = Math.Max(2, Math.Abs(end - start)),
                Height = 7,
                CornerRadius = new CornerRadius(3),
                Background = Brush(step.Amount < 0 ? "#F3A3A0" : "#9CE6C1"),
            };
            var track = new Canvas { Height = 9, Width = chartWidth, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#263641") };
            Canvas.SetLeft(bar, Math.Min(start, end));
            track.Children.Add(bar);
            chart.Children.Add(track);
        }

        return chart;
    }

    private async Task CorrectSnapshotAsync(MonthlySnapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 7 };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var amounts = new List<TextBox>();
        if (snapshot.Balances.Count == 0)
        {
            amounts.Add(new TextBox { Header = "Recorded net worth (£)", Text = snapshot.NetWorth.ToString("0.##", Uk) });
        }
        else
        {
            foreach (MonthlyBalance balance in snapshot.Balances)
            {
                amounts.Add(new TextBox { Header = balance.Name, Text = balance.Amount.ToString("0.##", Uk) });
            }
        }

        foreach (TextBox amount in amounts)
        {
            panel.Children.Add(amount);
        }
        panel.Children.Add(validation);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Correct {snapshot.Month:MMMM yyyy}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 390 },
            PrimaryButtonText = "Save correction",
            CloseButtonText = "Cancel",
        };
        var parsed = new decimal[amounts.Count];
        dialog.PrimaryButtonClick += (_, args) =>
        {
            for (int index = 0; index < amounts.Count; index++)
            {
                if (!TryReadAmount(amounts[index].Text, allowNegative: true, out parsed[index]))
                {
                    validation.Text = "Enter valid amounts with up to two decimal places.";
                    args.Cancel = true;
                    return;
                }
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        MonthlySnapshot corrected = snapshot.Balances.Count == 0
            ? snapshot with { NetWorth = parsed[0] }
            : MonthlySnapshot.FromBalances(snapshot.Month, snapshot.Balances.Select((balance, index) =>
                balance with { Amount = parsed[index], IsDebt = parsed[index] < 0 || (parsed[index] == 0 && balance.IsDebt) }).ToArray());
        IReadOnlyList<MonthlySnapshot> original = _monthlyHistory;
        _monthlyHistory = await _monthlyHistoryService.SaveAsync([corrected]);
        RefreshMonthlyHistory();
        SetHistoryUndo(original, "Correction undone.");
        SetStatus("Historical snapshot corrected.", InfoBarSeverity.Success);
    }

    private void SetHistoryUndo(IReadOnlyList<MonthlySnapshot> original, string message)
    {
        SetUndo(async () =>
        {
            _monthlyHistory = await _monthlyHistoryService.ReplaceAllAsync(original);
            RefreshMonthlyHistory();
            SetStatus(message, InfoBarSeverity.Success);
        });
    }

    private FileSavePicker CreateSavePicker(string type, string extension, string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(type, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    private sealed record ChangeStep(string Name, decimal Amount);
}
