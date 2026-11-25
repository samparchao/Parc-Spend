// -----------------------------------------------------------------------
// <copyright file="MainWindow.Workspace.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using MonzoManager.Services;
using Serilog;
using Windows.Foundation;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private readonly MonthlyHistoryService _monthlyHistoryService = new();
    private IReadOnlyList<ManualFinanceEntry> _holdings = [];
    private IReadOnlyList<MonthlySnapshot> _monthlyHistory = [];
    private bool _hasMonzoBalance;
    private DateTime _lastMonzoSync;
    private bool _workspaceReady;
    private bool _financeActionOpen;

    private void WorkspaceNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceReady && WorkspaceNavigation.SelectedItem is ListViewItem { Tag: string tag })
        {
            ShowWorkspace(tag, animate: true);
        }
    }

    private void ShowWorkspace(string tag, bool animate)
    {
        var (page, title) = tag switch
        {
            "worth" => (NetWorthPage, "Net worth"),
            "activity" => (ActivityPage, "Activity"),
            "planning" => (PlanningPage, "Planning"),
            "settings" => (SettingsPage, "Settings"),
            _ => (OverviewPage, "Overview"),
        };

        foreach (StackPanel candidate in new[] { OverviewPage, NetWorthPage, ActivityPage, PlanningPage, SettingsPage })
        {
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        }

        WorkspaceTitle.Text = title;
        OverviewAction.Visibility = page == OverviewPage ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (animate)
        {
            FadeIn(page);
        }
    }

    private void OpenNetWorth_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceNavigation.SelectedIndex = 1;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspaceReady)
        {
            ApplyResponsiveLayout(e.NewSize.Width);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        bool compact = width < 850;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 146 : 192);
        WorkspaceBody.Padding = compact ? new Thickness(16, 20, 16, 24) : new Thickness(32, 24, 32, 32);

        ArrangeSecondaryCard(OverviewLayout, CompositionCard, width < 1080, 1);
        ArrangeSecondaryCard(ActivityLayout, ActivityInsights, width < 1160, 2);
        ArrangeSecondaryCard(PlanningLayout, PlanningCommitments, width < 1030, 1);

        bool stackMetrics = width < 690;
        OverviewMetrics.ColumnDefinitions[1].Width = new GridLength(stackMetrics ? 0 : 1, GridUnitType.Star);
        OverviewMetrics.ColumnDefinitions[2].Width = new GridLength(stackMetrics ? 0 : 1, GridUnitType.Star);
        var metricDivider = stackMetrics ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        IncomeCard.BorderThickness = metricDivider;
        SpendingCard.BorderThickness = metricDivider;
        Grid.SetColumn(IncomeCard, stackMetrics ? 0 : 1);
        Grid.SetColumn(SpendingCard, stackMetrics ? 0 : 2);
        Grid.SetRow(IncomeCard, stackMetrics ? 1 : 0);
        Grid.SetRow(SpendingCard, stackMetrics ? 2 : 0);
    }

    private void TransactionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditSelectedTransaction.IsEnabled = TransactionsListView.SelectedItem is TransactionRowItem;
    }

    private static void ArrangeSecondaryCard(Grid layout, FrameworkElement card, bool stack, double proportion)
    {
        layout.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : proportion, GridUnitType.Star);
        layout.ColumnSpacing = stack ? 0 : 16;
        layout.RowSpacing = stack ? 16 : 0;
        Grid.SetRow(card, stack ? 1 : 0);
        Grid.SetColumn(card, stack ? 0 : 1);
    }

    private void CashFlowCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCashFlowForecast();

    private async Task LoadManualFinanceAsync()
    {
        _holdings = await _manualFinanceService.GetEntriesAsync();
        ManualEntriesListView.ItemsSource = _holdings
            .OrderBy(IsDebt)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new HoldingRow(entry, entry.Name, HoldingDetail(entry), Money(ToMonthlyBalance(entry).Amount)))
            .ToArray();
        HoldingsEmpty.Visibility = _holdings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshNetWorthOverview();
        ShowGoals();
        UpdateCashFlowForecast();
    }

    private static string HoldingDetail(ManualFinanceEntry entry)
    {
        DateTimeOffset? date = entry.UpdatedAt;
        string updated = date is null ? "Update date unknown"
            : DateTimeOffset.Now - date.Value > TimeSpan.FromDays(35) ? $"Needs review · {date.Value:dd MMM yyyy}"
            : $"Updated {date.Value:dd MMM yyyy}";
        string share = entry.OwnershipPercent == 100 ? "" : $" · {entry.OwnershipPercent:0.##}% owned";
        return $"{(IsDebt(entry) ? "Debt" : entry.Kind)}{share} · {updated}";
    }

    private static bool IsDebt(ManualFinanceEntry entry) =>
        string.Equals(entry.Type, "liability", StringComparison.OrdinalIgnoreCase);

    private static decimal SignedAmount(ManualFinanceEntry entry) => IsDebt(entry) ? -Math.Abs(entry.Amount) : entry.Amount;

    private async void AddBalance_Click(object sender, RoutedEventArgs e) => await EditBalanceAsync(null);

    private async void ManualEntriesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HoldingRow row)
        {
            await EditBalanceAsync(row.Entry);
        }
    }

    private async Task EditBalanceAsync(ManualFinanceEntry? entry)
    {
        await RunFinanceActionAsync(async () =>
        {
            var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Pension, savings or credit card", Text = entry?.Name ?? "" };
            var type = new ComboBox { Header = "Type", ItemsSource = new[] { "Asset", "Debt" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            type.SelectedIndex = entry is not null && IsDebt(entry) ? 1 : 0;
            var kind = new ComboBox
            {
                Header = "Account type",
                ItemsSource = AccountKinds.Take(AccountKinds.Length - 1).ToArray(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Math.Clamp(Array.IndexOf(AccountKinds, entry?.Kind ?? "Other"), 0, AccountKinds.Length - 2),
            };
            var share = new TextBox { Header = "Ownership (%)", Text = (entry?.OwnershipPercent ?? 100).ToString("0.##", Uk) };
            type.SelectionChanged += (_, _) => kind.IsEnabled = type.SelectedIndex == 0;
            kind.IsEnabled = type.SelectedIndex == 0;
            var amount = new TextBox
            {
                Header = "Balance (£)",
                PlaceholderText = "0.00",
                Text = entry is null ? "" : Math.Abs(entry.Amount).ToString("0.##", Uk),
            };
            var validation = new TextBlock { Foreground = Brush("#FFAAAB"), TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 10 };
            form.Children.Add(name);
            form.Children.Add(type);
            form.Children.Add(kind);
            form.Children.Add(amount);
            form.Children.Add(share);
            form.Children.Add(validation);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = entry is null ? "Add a balance" : "Edit balance",
                PrimaryButtonText = "Save balance",
                SecondaryButtonText = entry is null ? "" : "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = form,
            };

            decimal parsed = 0;
            decimal ownership = 100;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                bool valid = TryReadAmount(amount.Text, allowNegative: false, out parsed);
                bool validShare = decimal.TryParse(share.Text, NumberStyles.Number, Uk, out ownership)
                    && ownership is >= 0 and <= 100 && decimal.Round(ownership, 2) == ownership;
                validation.Text = string.IsNullOrWhiteSpace(name.Text) ? "Enter a name for this balance."
                    : !valid ? "Enter a positive balance or zero, with up to two decimal places."
                    : !validShare ? "Enter an ownership share from 0 to 100%." : "";
                args.Cancel = validation.Text.Length > 0;
            };

            ContentDialogResult result = await dialog.ShowAsync();
            IReadOnlyList<ManualFinanceEntry> original = _holdings;
            if (result == ContentDialogResult.Primary)
            {
                await _manualFinanceService.SaveEntryAsync(new ManualFinanceEntry
                {
                    Id = entry?.Id ?? Guid.NewGuid().ToString("N"),
                    Name = name.Text.Trim(),
                    Type = type.SelectedIndex == 1 ? "liability" : "asset",
                    Amount = parsed,
                    Kind = type.SelectedIndex == 1 ? "Debt" : kind.SelectedItem?.ToString() ?? "Other",
                    OwnershipPercent = ownership,
                    UpdatedAt = DateTimeOffset.Now,
                });
                await LoadManualFinanceAsync();
                SetHoldingUndo(original);
                SetStatus("Balance saved.", InfoBarSeverity.Success);
            }
            else if (result == ContentDialogResult.Secondary && entry is not null
                && await ConfirmAsync("Delete balance?", $"Remove {entry.Name} from your tracked balances?", "Delete"))
            {
                await _manualFinanceService.DeleteEntryAsync(entry.Id);
                await LoadManualFinanceAsync();
                SetHoldingUndo(original);
                SetStatus("Balance deleted.", InfoBarSeverity.Success);
            }
        }, "Could not update the balance.");
    }

    private void SetHoldingUndo(IReadOnlyList<ManualFinanceEntry> original)
    {
        SetUndo(async () =>
        {
            await _manualFinanceService.ReplaceAllAsync(original);
            await LoadManualFinanceAsync();
            SetStatus("Balance edit undone.", InfoBarSeverity.Success);
        });
    }

    private async Task LoadMonthlyHistoryAsync()
    {
        _monthlyHistory = await _monthlyHistoryService.LoadAsync();
        RefreshMonthlyHistory();
    }

    private async void ImportHistory_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            BasicProperties properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > 8 * 1024 * 1024)
            {
                SetStatus("The CSV exceeds the 8 MB import limit.", InfoBarSeverity.Warning);
                return;
            }

            IReadOnlyList<MonthlySnapshot> imported;
            try
            {
                using Stream input = await file.OpenStreamForReadAsync();
                imported = await Task.Run(() => MonthlyHistoryService.ReadSpreadsheet(input));
            }
            catch (InvalidDataException exception)
            {
                SetStatus(exception.Message, InfoBarSeverity.Warning);
                return;
            }
            catch (Microsoft.VisualBasic.FileIO.MalformedLineException exception)
            {
                SetStatus($"Check the CSV formatting at line {exception.LineNumber}.", InfoBarSeverity.Warning);
                return;
            }

            var currentMonths = _monthlyHistory.Select(snapshot => snapshot.Month).ToHashSet();
            int replacements = imported.Count(snapshot => currentMonths.Contains(snapshot.Month));
            string details = $"Import {imported.Count} months from {file.Name}? Net worth is calculated from the balance rows when available.";
            if (replacements > 0)
            {
                details += $" This replaces {replacements} existing monthly snapshots.";
            }

            if (!await ConfirmAsync("Import monthly history", details, "Import"))
            {
                return;
            }

            IReadOnlyList<MonthlySnapshot> original = _monthlyHistory;
            _monthlyHistory = await _monthlyHistoryService.SaveAsync(imported);
            SetHistoryUndo(original, "CSV import undone.");
            RefreshMonthlyHistory();
            SetStatus($"Imported {imported.Count} monthly snapshots.", InfoBarSeverity.Success);
        }, "Could not import the CSV. Use a file with one column per month and one row per balance.");
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HistoryRow row)
        {
            return;
        }

        await RunFinanceActionAsync(async () =>
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = $"Net worth  {row.Amount}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 21,
            });
            if (row.Snapshot.Balances.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "This month was imported as a total.", TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                foreach (MonthlyBalance balance in row.Snapshot.Balances)
                {
                    content.Children.Add(BalanceLegendRow(balance.Name, Money(balance.Amount),
                        balance.IsDebt ? "#F3A3A0" : "#9CE6C1"));
                }
            }

            MonthlySnapshot? previous = _monthlyHistory.LastOrDefault(snapshot => snapshot.Month < row.Snapshot.Month);
            content.Children.Add(BuildMonthlyComparison(previous, row.Snapshot));

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = row.Month,
                PrimaryButtonText = "Correct balances",
                CloseButtonText = "Done",
                Content = new ScrollViewer { Content = content, MaxHeight = 420 },
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await CorrectSnapshotAsync(row.Snapshot);
            }
        }, "Could not open this snapshot.");
    }

    private void RefreshMonthlyHistory()
    {
        HistoryList.ItemsSource = _monthlyHistory.Select((snapshot, index) =>
        {
            MonthlySnapshot? previous = index > 0 ? _monthlyHistory[index - 1] : null;
            string change = previous is null
                ? "First recorded month"
                : $"{SignedMoney(snapshot.NetWorth - previous.NetWorth)} since {previous.Month:MMM yyyy}";
            return new HistoryRow(snapshot, snapshot.Month.ToString("MMMM yyyy", Uk), Money(snapshot.NetWorth), change);
        }).Reverse().ToArray();
        HistoryEmpty.Visibility = _monthlyHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshNetWorthOverview();
        UpdateChartRange(animate: true);
    }

    private IReadOnlyList<MonthlyBalance> CurrentBalances() =>
        LinkedMonthlyBalances().Concat(_holdings.Select(ToMonthlyBalance)).ToArray();

    private void RefreshNetWorthOverview()
    {
        IReadOnlyList<MonthlyBalance> live = CurrentBalances();
        bool accessible = AccessibleView.IsChecked == true;
        IReadOnlyList<MonthlySnapshot> displayedHistory = DisplayedMonthlyHistory();
        MonthlySnapshot? latest = displayedHistory.LastOrDefault();
        bool showLive = live.Count > 0;
        if (accessible)
        {
            live = live.Where(IsAccessible).ToArray();
        }

        OverviewChartTitle.Text = accessible ? "Accessible savings" : "Net worth";
        decimal? netWorth = showLive ? live.Sum(balance => balance.Amount) : latest?.NetWorth;
        NetWorthValue.Text = netWorth is decimal value ? Money(value) : "—";

        MonthlySnapshot? previous = showLive
            ? displayedHistory.LastOrDefault(snapshot => snapshot.Month < CurrentMonth())
            : displayedHistory.SkipLast(1).LastOrDefault();
        decimal? change = netWorth - previous?.NetWorth;
        NetWorthChange.Foreground = Brush(change switch
        {
            > 0 => "#9CE6C1",
            < 0 => "#F3A3A0",
            _ => "#A6AFAD",
        });
        NetWorthChange.Text = (change, previous) switch
        {
            (decimal amount, MonthlySnapshot prior) => $"{SignedMoney(amount)} since {prior.Month:MMM yyyy}",
            (_, _) when showLive => "Current tracked balances",
            (_, _) when latest is not null => $"Recorded {latest.Month:MMMM yyyy}",
            _ => "Track balances to calculate your net worth.",
        };

        string tracking = "Add pensions, savings, investments or debts you want included.";
        TrackedBalanceNote.Text = _hasMonzoBalance
            ? $"Monzo is included automatically. Last synced {_lastMonzoSync:dd MMM, HH:mm}."
            : tracking;

        UpdateBalanceBreakdown(showLive ? live : latest?.Balances ?? []);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RunFinanceActionAsync(Func<Task> action, string failureMessage)
    {
        if (_financeActionOpen)
        {
            return;
        }

        _financeActionOpen = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ReportFinanceError(failureMessage, exception);
        }
        finally
        {
            _financeActionOpen = false;
        }
    }

    private void ReportFinanceError(string message, Exception exception)
    {
        Log.Error("{FinanceError} ({ErrorType})", message, exception.GetType().Name);
        SetStatus(message, InfoBarSeverity.Error);
    }

    private static void FadeIn(UIElement element)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private static DateOnly CurrentMonth()
    {
        DateTime today = DateTime.Today;
        return new DateOnly(today.Year, today.Month, 1);
    }

    private static string Money(decimal amount) => (amount < 0 ? "−" : "") + Math.Abs(amount).ToString("C2", Uk);

    private static string SignedMoney(decimal amount) => (amount > 0 ? "+" : "") + Money(amount);

    private static SolidColorBrush Brush(string hex) => new(ColorHelper.FromArgb(255,
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber)));

    private sealed record HoldingRow(ManualFinanceEntry Entry, string Name, string Type, string Amount);
    private sealed record HistoryRow(MonthlySnapshot Snapshot, string Month, string Amount, string Change);
}
