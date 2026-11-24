// -----------------------------------------------------------------------
// <copyright file="MainWindow.MonthlyReview.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonzoManager.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private static readonly string[] AccountKinds = ["Cash", "Savings", "Investment", "Pension", "Other", "Debt"];
    private const decimal BalanceLimit = 1_000_000_000_000m;
    private Func<Task>? _undoFinance;

    private async void MonthlyReview_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            IReadOnlyList<ManualFinanceEntry> reviewEntries = _holdings;
            if (reviewEntries.Count == 0)
            {
                MonthlySnapshot? seed = _monthlyHistory.LastOrDefault(snapshot => snapshot.Balances.Any(balance => !balance.IsMonzo));
                if (seed is not null && await ConfirmAsync("Start with historical balances?",
                    $"Carry forward the accounts from {seed.Month:MMM yyyy} into this review?", "Carry forward"))
                {
                    reviewEntries = seed.Balances.Where(balance => !balance.IsMonzo).Select(balance => new ManualFinanceEntry
                    {
                        Name = balance.Name,
                        Amount = Math.Abs(balance.Amount),
                        Type = balance.IsDebt ? "liability" : "asset",
                        Kind = balance.IsDebt ? "Debt" : balance.Kind,
                    }).ToArray();
                }
            }

            if (reviewEntries.Count == 0 && !_hasMonzoBalance)
            {
                SetStatus("Add a tracked balance or connect Monzo before starting your review.", InfoBarSeverity.Warning);
                return;
            }

            DateOnly month = CurrentMonth();
            MonthlySnapshot? existing = _monthlyHistory.FirstOrDefault(snapshot => snapshot.Month == month);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var sync = new Button { Content = "Sync Monzo", IsEnabled = _authService.IsAuthenticated };
            var paste = new Button { Content = "Paste balance column", IsEnabled = reviewEntries.Count > 0 };
            tools.Children.Add(sync);
            tools.Children.Add(paste);

            var entries = new StackPanel { Spacing = 9 };
            var rows = new List<ReviewRow>();
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var preview = new TextBlock { FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var monzo = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "Enter full account balances. Leave investment contributions blank if you don’t track them. Amounts count toward net worth at the ownership share shown.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(tools);
            content.Children.Add(monzo);
            content.Children.Add(new ScrollViewer { Content = entries, MaxHeight = 345, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            content.Children.Add(preview);
            content.Children.Add(message);

            foreach (ManualFinanceEntry entry in reviewEntries.OrderBy(IsDebt).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var amount = new TextBox { Header = entry.Name, Text = Math.Abs(entry.Amount).ToString("0.##", Uk), MinWidth = 140 };
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.Children.Add(amount);
                TextBox? contribution = null;
                if (entry.Kind == "Investment" || entry.Kind == "Pension")
                {
                    decimal? previousInput = existing?.Balances.FirstOrDefault(balance => balance.AccountId == entry.Id)?.NetContributions;
                    contribution = new TextBox
                    {
                        Header = "Net contributions (£)",
                        PlaceholderText = "Optional",
                        Text = previousInput is decimal value && entry.OwnershipPercent > 0
                            ? (value * 100 / entry.OwnershipPercent).ToString("0.##", Uk) : "",
                    };
                    Grid.SetColumn(contribution, 1);
                    line.Children.Add(contribution);
                }
                else
                {
                    var share = new TextBlock
                    {
                        Text = $"{entry.Kind} · {entry.OwnershipPercent:0.##}% owned",
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 6),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    Grid.SetColumn(share, 1);
                    line.Children.Add(share);
                }

                entries.Children.Add(line);
                rows.Add(new ReviewRow(entry, amount, contribution));
            }

            bool TryReadReview(out List<ManualFinanceEntry> updated, out List<MonthlyBalance> balances)
            {
                updated = [];
                balances = [];
                if (_hasMonzoBalance)
                {
                    balances.Add(MonzoMonthlyBalance());
                }

                foreach (ReviewRow row in rows)
                {
                    if (!TryReadAmount(row.Amount.Text, allowNegative: false, out decimal amount))
                    {
                        message.Text = $"Enter a valid balance for {row.Entry.Name}.";
                        return false;
                    }

                    decimal? contribution = null;
                    if (row.Contribution is { Text: var text } && !string.IsNullOrWhiteSpace(text))
                    {
                        if (!TryReadAmount(text, allowNegative: true, out decimal result))
                        {
                            message.Text = $"Enter a valid contribution or withdrawal for {row.Entry.Name}.";
                            return false;
                        }

                        contribution = IncludedShare(result, row.Entry.OwnershipPercent);
                    }

                    var entry = new ManualFinanceEntry
                    {
                        Id = row.Entry.Id,
                        Name = row.Entry.Name,
                        Type = row.Entry.Type,
                        Kind = row.Entry.Kind,
                        Amount = amount,
                        OwnershipPercent = row.Entry.OwnershipPercent,
                        UpdatedAt = DateTimeOffset.Now,
                    };
                    updated.Add(entry);
                    balances.Add(ToMonthlyBalance(entry) with { NetContributions = contribution });
                }

                message.Text = "";
                return true;
            }

            bool pasting = false;
            void ShowPreview()
            {
                if (pasting)
                {
                    return;
                }

                monzo.Text = _hasMonzoBalance
                    ? $"Monzo {Money(_currentBalance)} · synced {_lastMonzoSync:dd MMM, HH:mm}"
                    : "Manual balances";
                preview.Text = TryReadReview(out _, out List<MonthlyBalance> balances)
                    ? $"Preview  {Money(balances.Sum(balance => balance.Amount))}" : "Preview  —";
            }

            foreach (ReviewRow row in rows)
            {
                row.Amount.TextChanged += (_, _) => ShowPreview();
                if (row.Contribution is not null)
                {
                    row.Contribution.TextChanged += (_, _) => ShowPreview();
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = $"Monthly review · {month:MMMM yyyy}",
                Content = content,
                PrimaryButtonText = existing is null ? "Save month" : "Replace month",
                DefaultButton = ContentDialogButton.Primary,
                CloseButtonText = "Cancel",
            };
            sync.Click += async (_, _) =>
            {
                sync.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    DateTime previousSync = _lastMonzoSync;
                    await LoadDashboardAsync();
                    ShowPreview();
                    if (_lastMonzoSync == previousSync)
                    {
                        message.Text = "Monzo could not sync. The last synced balance is shown.";
                    }
                }
                catch (Exception exception)
                {
                    ReportFinanceError("Could not sync Monzo.", exception);
                    message.Text = "Could not sync Monzo. The last synced balance is shown.";
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    sync.IsEnabled = true;
                }
            };
            paste.Click += async (_, _) =>
            {
                try
                {
                    DataPackageView clipboard = Clipboard.GetContent();
                    string text = clipboard.Contains(StandardDataFormats.Text) ? await clipboard.GetTextAsync() : "";
                    string[] column = text.TrimEnd('\r', '\n').Split('\n');
                    if (column.Length != rows.Count || column.Any(value => !TryReadAmount(value.Trim(), allowNegative: false, out _)))
                    {
                        message.Text = $"Copy one column with {rows.Count} positive balances in the order shown.";
                        return;
                    }

                    pasting = true;
                    try
                    {
                        for (int i = 0; i < rows.Count; i++)
                        {
                            rows[i].Amount.Text = column[i].Trim();
                        }
                    }
                    finally
                    {
                        pasting = false;
                    }
                    ShowPreview();
                }
                catch (Exception exception)
                {
                    ReportFinanceError("Could not read the clipboard.", exception);
                }
            };
            dialog.PrimaryButtonClick += (dialogSender, args) => args.Cancel = !TryReadReview(out _, out _);
            ShowPreview();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !TryReadReview(out List<ManualFinanceEntry> updated, out List<MonthlyBalance> amounts))
            {
                return;
            }

            IReadOnlyList<ManualFinanceEntry> oldHoldings = _holdings;
            IReadOnlyList<MonthlySnapshot> oldHistory = _monthlyHistory;
            await SaveReviewAsync(updated, amounts, month);
            SetUndo(async () =>
            {
                await RestoreReviewAsync(oldHoldings, oldHistory);
                SetStatus("Monthly review undone.", InfoBarSeverity.Success);
            });
            SetStatus($"Monthly review saved for {month:MMMM yyyy}.", InfoBarSeverity.Success);
        }, "Could not save the monthly review.");
    }

    private async Task SaveReviewAsync(IReadOnlyList<ManualFinanceEntry> entries, IReadOnlyList<MonthlyBalance> amounts, DateOnly month)
    {
        await _manualFinanceService.ReplaceAllAsync(entries);
        try
        {
            _monthlyHistory = await _monthlyHistoryService.SaveAsync([MonthlySnapshot.FromBalances(month, amounts)]);
        }
        catch
        {
            await _manualFinanceService.ReplaceAllAsync(_holdings);
            throw;
        }

        await LoadManualFinanceAsync();
        RefreshMonthlyHistory();
    }

    private async Task RestoreReviewAsync(IReadOnlyList<ManualFinanceEntry> entries, IReadOnlyList<MonthlySnapshot> history)
    {
        await _manualFinanceService.ReplaceAllAsync(entries);
        try
        {
            _monthlyHistory = await _monthlyHistoryService.ReplaceAllAsync(history);
        }
        catch
        {
            await _manualFinanceService.ReplaceAllAsync(_holdings);
            throw;
        }

        await LoadManualFinanceAsync();
        RefreshMonthlyHistory();
    }

    private void SetUndo(Func<Task>? undo)
    {
        _undoFinance = undo;
        UndoFinanceButton.IsEnabled = undo is not null;
    }

    private async void UndoFinance_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (_undoFinance is not null)
            {
                await _undoFinance();
                SetUndo(null);
            }
        }, "Could not undo the last change.");
    }

    private static bool TryReadAmount(string value, bool allowNegative, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Currency, Uk, out amount)
        && (allowNegative || amount >= 0) && Math.Abs(amount) <= BalanceLimit
        && amount == decimal.Round(amount, 2);

    private static decimal IncludedShare(decimal amount, decimal share) =>
        decimal.Round(amount * share / 100, 2, MidpointRounding.AwayFromZero);

    private static bool IsAccessible(MonthlyBalance balance) =>
        !balance.IsDebt && (balance.IsMonzo || balance.Kind is "Cash" or "Savings");

    private static MonthlyBalance ToMonthlyBalance(ManualFinanceEntry entry) =>
        new(entry.Name, IncludedShare(SignedAmount(entry), entry.OwnershipPercent), IsDebt(entry),
            AccountId: entry.Id, Kind: entry.Kind);

    private MonthlyBalance MonzoMonthlyBalance() =>
        new("Monzo", _currentBalance, _currentBalance < 0, IsMonzo: true, AccountId: "monzo:" + _connectedAccountId, Kind: "Cash");

    private sealed record ReviewRow(ManualFinanceEntry Entry, TextBox Amount, TextBox? Contribution);
}
