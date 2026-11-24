// -----------------------------------------------------------------------
// <copyright file="MainWindow.LedgerPlanning.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonzoManager.Models;
using MonzoManager.Services;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private async void EditTransaction_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionsListView.SelectedItem is not TransactionRowItem row
            || _activityTransactions.FirstOrDefault(item => item.Id == row.Id) is not { } transaction)
        {
            SetStatus("Select a transaction first.", InfoBarSeverity.Informational);
            return;
        }

        await EditTransactionAsync(transaction);
    }

    private async Task EditTransactionAsync(Transaction transaction)
    {
        await RunFinanceActionAsync(async () =>
        {
            LedgerAnnotation old = AnnotationFor(transaction.Id);
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"{transaction.Description ?? "Transaction"} · {SignedMoney(transaction.Amount)} · {TransactionStatus(transaction)}",
                TextWrapping = TextWrapping.Wrap,
            });

            var transfers = new[] { new LinkedChoice(null, "No linked transfer") }.Concat(_activityTransactions
                .Where(other => LedgerService.IsTransferCandidate(transaction, other)
                    && AnnotationFor(other.Id).OffsetOf is null
                    && (AnnotationFor(other.Id).TransferWith is null || AnnotationFor(other.Id).TransferWith == transaction.Id))
                .OrderBy(other => Math.Abs((other.Created - transaction.Created).TotalHours)).Take(100)
                .Select(DescribeTransaction)).ToArray();
            ComboBox transfer = TransactionPicker("Match a transfer between your accounts", transfers, old.TransferWith);
            panel.Children.Add(transfer);

            var splits = new TextBox
            {
                Header = "Category splits · one category and amount per line",
                PlaceholderText = "groceries, 42.50",
                Text = string.Join(Environment.NewLine, old.Splits.Select(split => $"{split.Category}, {split.Amount.ToString("0.00", Uk)}")),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72,
            };
            var expected = new TextBox
            {
                Header = "Expected reimbursement (£)",
                PlaceholderText = "Optional",
                Text = old.ExpectedReimbursement == 0 ? "" : old.ExpectedReimbursement.ToString("0.00", Uk),
            };
            var person = new TextBox
            {
                Header = "Reimbursement from", PlaceholderText = "Optional name", MaxLength = 80,
                Text = old.ReimbursementFrom ?? "",
            };
            LinkedChoice[] credits = new[] { new LinkedChoice(null, "None") }.Concat(_activityTransactions
                .Where(other => other.Amount > 0 && !other.IsPending && !other.IsDeclined && other.Created.Date >= transaction.Created.Date
                    && other.Amount <= Math.Abs(transaction.Amount)
                    && AnnotationFor(other.Id).TransferWith is null
                    && (AnnotationFor(other.Id).OffsetOf is null || AnnotationFor(other.Id).OffsetOf == transaction.Id))
                .OrderByDescending(other => other.Id == old.ReimbursedBy || other.Id == old.LinkedRefund)
                .ThenByDescending(other => other.Merchant is not null && other.Merchant == transaction.Merchant)
                .ThenBy(other => Math.Abs((other.Created - transaction.Created).TotalDays)).Take(160)
                .Select(DescribeTransaction)).ToArray();
            ComboBox reimbursement = TransactionPicker("Received reimbursement", credits, old.ReimbursedBy);
            ComboBox refund = TransactionPicker("Refund for this purchase", credits, old.LinkedRefund);
            if (transaction.Amount < 0 && !transaction.IsDeclined)
            {
                panel.Children.Add(splits);
                panel.Children.Add(expected);
                panel.Children.Add(person);
                panel.Children.Add(reimbursement);
                panel.Children.Add(refund);
            }
            else if (old.OffsetOf is not null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "This credit is linked to a purchase. Open the purchase to change the link.",
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot, Title = "Transaction details",
                Content = new ScrollViewer { Content = panel, MaxHeight = 410 },
                PrimaryButtonText = "Save details", CloseButtonText = "Cancel",
            };
            LedgerAnnotation? annotation = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string? paired = (transfer.SelectedItem as LinkedChoice)?.Id;
                decimal owed = 0;
                string? payment = (reimbursement.SelectedItem as LinkedChoice)?.Id;
                string? refundId = (refund.SelectedItem as LinkedChoice)?.Id;
                IReadOnlyList<LedgerSplit>? parsed = ReadSplits(splits.Text);
                if (parsed is null || (parsed.Count > 0 && parsed.Sum(item => item.Amount) != -transaction.Amount))
                {
                    error.Text = "Split amounts must add up to the purchase total.";
                }
                else if (expected.Text.Trim().Length > 0 &&
                    (!TryReadAmount(expected.Text, allowNegative: false, out owed) || owed > -transaction.Amount))
                {
                    error.Text = "Enter an expected reimbursement up to the purchase total.";
                }
                else if (payment is not null && (owed == 0 || payment == refundId || CreditAmount(payment) > owed))
                {
                    error.Text = "Choose a separate reimbursement credit up to the expected amount.";
                }
                else if (CreditAmount(payment) + CreditAmount(refundId) > Math.Abs(transaction.Amount))
                {
                    error.Text = "Linked credits cannot exceed the purchase amount.";
                }
                else if (paired is not null && (parsed.Count > 0 || owed > 0 || refundId is not null || old.OffsetOf is not null
                    || AnnotationFor(paired) is { Splits.Count: > 0 } or { ExpectedReimbursement: > 0 } or { LinkedRefund: not null }))
                {
                    error.Text = "Clear purchase details before linking this as a transfer.";
                }
                else
                {
                    error.Text = "";
                    annotation = new LedgerAnnotation(parsed, owed, payment, old.OffsetOf, paired, refundId,
                        owed > 0 && !string.IsNullOrWhiteSpace(person.Text) ? person.Text.Trim() : null);
                }

                args.Cancel = error.Text.Length > 0;
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && annotation is not null)
            {
                await SaveLedgerAsync(state => LinkTransaction(state, transaction.Id, annotation));
                SetStatus("Transaction details saved.", InfoBarSeverity.Success);
            }
        }, "Could not update the transaction details.");
    }

    private static LedgerState LinkTransaction(LedgerState state, string id, LedgerAnnotation annotation)
    {
        var annotations = new Dictionary<string, LedgerAnnotation>(state.Annotations, StringComparer.Ordinal);
        LedgerAnnotation Read(string key) => annotations.GetValueOrDefault(key) ?? new LedgerAnnotation([], 0);
        LedgerAnnotation previous = Read(id);
        if (previous.TransferWith is { } oldTransfer && Read(oldTransfer).TransferWith == id)
        {
            annotations[oldTransfer] = Read(oldTransfer) with { TransferWith = null };
        }

        foreach (string oldCredit in new[] { previous.ReimbursedBy, previous.LinkedRefund }.OfType<string>())
        {
            if (Read(oldCredit).OffsetOf == id)
            {
                annotations[oldCredit] = Read(oldCredit) with { OffsetOf = null };
            }
        }

        if (annotation.TransferWith is { } transfer)
        {
            annotations[transfer] = Read(transfer) with { TransferWith = id };
        }
        foreach (string credit in new[] { annotation.ReimbursedBy, annotation.LinkedRefund }.OfType<string>())
        {
            annotations[credit] = Read(credit) with { OffsetOf = id };
        }

        annotations[id] = annotation;
        return state with { Annotations = annotations };
    }

    private LedgerAnnotation AnnotationFor(string id) => _ledger.Annotations.GetValueOrDefault(id) ?? new LedgerAnnotation([], 0);

    private decimal CreditAmount(string? id) => id is null ? 0 :
        _activityTransactions.FirstOrDefault(transaction => transaction.Id == id && !transaction.IsPending && !transaction.IsDeclined)?.Amount ?? 0;

    private LinkedChoice DescribeTransaction(Transaction transaction)
    {
        string account = _ledger.Accounts.FirstOrDefault(item => item.Id == transaction.AccountId)?.Name ?? "Account";
        return new LinkedChoice(transaction.Id,
            $"{transaction.Created.ToLocalTime():dd MMM yy} · {account} · {transaction.Description ?? "Transaction"} · {SignedMoney(transaction.Amount)}");
    }

    private static ComboBox TransactionPicker(string header, LinkedChoice[] choices, string? selected)
    {
        int index = Array.FindIndex(choices, item => item.Id == selected);
        return new ComboBox
        {
            Header = header, ItemsSource = choices, DisplayMemberPath = nameof(LinkedChoice.Label),
            SelectedIndex = Math.Max(0, index), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static IReadOnlyList<LedgerSplit>? ReadSplits(string text)
    {
        var result = new List<LedgerSplit>();
        foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = line.IndexOf(',');
            if (comma <= 0 || !TryReadAmount(line[(comma + 1)..], allowNegative: false, out decimal amount) || amount <= 0)
            {
                return null;
            }

            result.Add(new LedgerSplit(line[..comma].Trim(), amount));
        }
        return result;
    }

    private IEnumerable<LedgerSplit> SpendPostings(Transaction item)
    {
        decimal spend = SpendEffect(item);
        if (spend == 0)
        {
            yield break;
        }

        if (spend > 0 && AnnotationFor(item.Id).Splits is { Count: > 0 } splits
            && splits.Sum(split => split.Amount) == spend)
        {
            foreach (LedgerSplit split in splits)
            {
                yield return split;
            }
            yield break;
        }

        if (spend < 0 && AnnotationFor(item.Id).OffsetOf is { } purchaseId
            && _activityTransactions.FirstOrDefault(transaction => transaction.Id == purchaseId) is { } purchase)
        {
            IReadOnlyList<LedgerSplit> lines = AnnotationFor(purchaseId).Splits;
            if (lines.Count != 0 && purchase.Amount < 0 && lines.Sum(line => line.Amount) == -purchase.Amount)
            {
                decimal remaining = spend;
                for (int index = 0; index < lines.Count; index++)
                {
                    LedgerSplit line = lines[index];
                    decimal amount = index == lines.Count - 1 ? remaining : decimal.Round(spend * line.Amount / -purchase.Amount, 2);
                    remaining -= amount;
                    yield return line with { Amount = amount };
                }

                yield break;
            }

            yield return new LedgerSplit(purchase.Category ?? "uncategorized", spend);
            yield break;
        }

        yield return new LedgerSplit(item.Category ?? "uncategorized", spend);
    }

    private string TransactionStatus(Transaction transaction)
    {
        if (transaction.IsDeclined)
        {
            return "Declined";
        }
        if (transaction.IsPending)
        {
            return "Pending";
        }

        LedgerAnnotation details = AnnotationFor(transaction.Id);
        if (RuleFor(transaction)?.Treatment == FinancePreferences.Transfer)
        {
            return "Transfer";
        }
        if (details.OffsetOf is { } purchaseId)
        {
            return AnnotationFor(purchaseId).LinkedRefund == transaction.Id ? "Linked refund" : "Linked reimbursement";
        }
        if (details.ExpectedReimbursement > 0)
        {
            decimal remaining = Math.Max(0, details.ExpectedReimbursement - CreditAmount(details.ReimbursedBy));
            string who = details.ReimbursementFrom is { Length: > 0 } name ? $" by {name}" : "";
            return remaining > 0 ? $"{Money(remaining)} owed{who}" : "Reimbursed";
        }

        return details.Splits.Count > 0 ? "Split" : "Settled";
    }

    private async void AddPlannedExpense_Click(object sender, RoutedEventArgs e) => await EditPlannedExpenseAsync(null);

    private void ShowPlannedExpenses()
    {
        PlannedExpenseRows.Children.Clear();
        foreach (PlannedExpense expense in _ledger.Planned.OrderBy(item => item.Date))
        {
            var button = new Button
            {
                Content = $"{expense.Name} · {Money(expense.Amount)} · {expense.Date:d MMM yyyy}{(expense.RepeatsMonthly ? " · monthly" : "")}",
            };
            button.Click += async (_, _) => await EditPlannedExpenseAsync(expense);
            PlannedExpenseRows.Children.Add(button);
        }

        PlannedExpenseEmpty.Visibility = _ledger.Planned.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task EditPlannedExpenseAsync(PlannedExpense? expense)
    {
        await RunFinanceActionAsync(async () =>
        {
            var name = new TextBox { Header = "Name", Text = expense?.Name ?? "", MaxLength = 80 };
            var amount = new TextBox { Header = "Amount (£)", Text = expense?.Amount.ToString("0.00", Uk) ?? "" };
            var date = new CalendarDatePicker
            {
                Header = "First payment",
                Date = expense is null ? DateTimeOffset.Now.Date : expense.Date.ToDateTime(TimeOnly.MinValue),
            };
            var repeat = new CheckBox { Content = "Renews monthly", IsChecked = expense?.RepeatsMonthly ?? false };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8 };
            foreach (UIElement element in new UIElement[] { name, amount, date, repeat, error })
            {
                panel.Children.Add(element);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot, Title = "Planned expense", Content = panel,
                PrimaryButtonText = "Save expense", SecondaryButtonText = expense is null ? "" : "Remove", CloseButtonText = "Cancel",
            };
            decimal value = 0;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = string.IsNullOrWhiteSpace(name.Text) || !TryReadAmount(amount.Text, allowNegative: false, out value)
                    || value == 0 || date.Date is null;
                error.Text = args.Cancel ? "Enter a name, positive amount and payment date." : "";
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var updated = new PlannedExpense(expense?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), value,
                    DateOnly.FromDateTime(date.Date!.Value.Date), repeat.IsChecked == true);
                await SaveLedgerAsync(state => state with
                {
                    Planned = state.Planned.Where(item => item.Id != updated.Id).Append(updated).ToArray(),
                });
            }
            else if (result == ContentDialogResult.Secondary && expense is not null
                && await ConfirmAsync("Remove planned expense?", "Remove this expense from the forecast?", "Remove"))
            {
                await SaveLedgerAsync(state => state with { Planned = state.Planned.Where(item => item.Id != expense.Id).ToArray() });
            }
        }, "Could not update the planned expense.");
    }

    private void PlannedForecast_Changed(object sender, RoutedEventArgs e)
    {
        if (_workspaceReady)
        {
            UpdateCashFlowForecast();
        }
    }

    private sealed record LinkedChoice(string? Id, string Label);
}
