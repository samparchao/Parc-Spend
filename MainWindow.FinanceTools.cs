// -----------------------------------------------------------------------
// <copyright file="MainWindow.FinanceTools.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonzoManager.Models;
using MonzoManager.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private readonly FinancePreferencesService _preferencesService = new();
    private readonly SemaphoreSlim _preferencesLock = new(1, 1);
    private FinancePreferences _preferences = new();
    private bool _preferencesLoaded;

    private async Task LoadPreferencesAsync()
    {
        _preferencesLoaded = false;
        _preferences = await _preferencesService.LoadAsync();
        AccessibleView.IsChecked = _preferences.AccessibleOnly;
        NetWorthRange.SelectedIndex = _preferences.ChartRange switch { 12 => 1, 6 => 2, _ => 0 };
        _preferencesLoaded = true;
    }

    private async Task UpdatePreferencesAsync(Func<FinancePreferences, FinancePreferences> update)
    {
        await _preferencesLock.WaitAsync();
        try
        {
            FinancePreferences next = update(_preferences);
            await _preferencesService.SaveAsync(next);
            _preferences = next;
        }
        finally
        {
            _preferencesLock.Release();
        }
    }

    private async Task PersistViewAsync()
    {
        try
        {
            await UpdatePreferencesAsync(current => current with
            {
                ChartRange = ChartRangeMonths(),
                AccessibleOnly = AccessibleView.IsChecked == true,
            });
        }
        catch (Exception exception)
        {
            ReportFinanceError("Could not remember the chart view.", exception);
        }
    }

    private async void AccessibleView_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetWorthOverview();
        UpdateChartRange(animate: true);
        if (_preferencesLoaded)
        {
            await PersistViewAsync();
        }
    }

    private async void AddGoal_Click(object sender, RoutedEventArgs e) => await EditGoalAsync(null);

    private void ShowGoals()
    {
        GoalRows.Children.Clear();
        GoalsEmptyText.Visibility = _preferences.Goals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (SavingsGoal goal in _preferences.Goals)
        {
            MonthlyBalance? account = GoalSources().FirstOrDefault(entry => entry.AccountId == goal.AccountId);
            decimal saved = Math.Max(0, account?.Amount ?? 0);
            var row = new StackPanel { Spacing = 5 };
            var title = new Button { Content = goal.Name };
            title.Click += async (_, _) => await EditGoalAsync(goal);
            row.Children.Add(title);
            row.Children.Add(new TextBlock
            {
                Text = account is null ? "Linked account unavailable · select the goal to relink"
                    : $"{Money(saved)} of {Money(goal.Target)} · {account.Name}",
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = goal.Target > 0 ? Math.Min(1, (double)(saved / goal.Target)) : 0,
            });
            GoalRows.Children.Add(row);
        }
    }

    private IEnumerable<MonthlyBalance> GoalSources() =>
        _holdings.Where(entry => !IsDebt(entry)).Select(ToMonthlyBalance)
            .Concat(LinkedMonthlyBalances().Where(balance => balance.Kind == "Pot"));

    private async Task EditGoalAsync(SavingsGoal? goal)
    {
        await RunFinanceActionAsync(async () =>
        {
            MonthlyBalance[] accounts = GoalSources().OrderBy(entry => entry.Name).ToArray();
            if (accounts.Length == 0 && goal is null)
            {
                SetStatus("Add a tracked asset or sync a Monzo Pot before creating a savings goal.", InfoBarSeverity.Warning);
                return;
            }

            var name = new TextBox { Header = "Goal name", Text = goal?.Name ?? "", MaxLength = 120 };
            var target = new TextBox { Header = "Target (£)", Text = goal?.Target.ToString("0.##", Uk) ?? "" };
            var account = new ComboBox
            {
                Header = "Funding account",
                ItemsSource = accounts.Select(entry => entry.Name).ToArray(),
                SelectedIndex = goal is null ? 0 : Array.FindIndex(accounts, entry => entry.AccountId == goal.AccountId),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(name);
            panel.Children.Add(target);
            panel.Children.Add(account);
            panel.Children.Add(validation);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = goal is null ? "Add savings goal" : "Edit savings goal",
                Content = panel,
                PrimaryButtonText = "Save goal",
                SecondaryButtonText = goal is null ? "" : "Delete goal",
                CloseButtonText = "Cancel",
            };
            decimal targetAmount = 0;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = string.IsNullOrWhiteSpace(name.Text) || account.SelectedIndex < 0
                    || !TryReadAmount(target.Text, allowNegative: false, out targetAmount) || targetAmount <= 0;
                validation.Text = args.Cancel ? "Enter a goal name, a positive target, and a funding account." : "";
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var updated = new SavingsGoal(goal?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), targetAmount, accounts[account.SelectedIndex].AccountId!);
                await UpdatePreferencesAsync(current => current with
                {
                    Goals = current.Goals.Where(item => item.Id != updated.Id).Append(updated).ToArray(),
                });
            }
            else if (result == ContentDialogResult.Secondary && goal is not null
                && await ConfirmAsync("Delete savings goal?", "The linked account will remain available.", "Delete goal"))
            {
                await UpdatePreferencesAsync(current => current with
                {
                    Goals = current.Goals.Where(item => item.Id != goal.Id).ToArray(),
                });
            }
            ShowGoals();
        }, "Could not update the savings goal.");
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            string? password = await BackupPasswordAsync(creating: true);
            if (password is null)
            {
                return;
            }

            FileSavePicker picker = CreateSavePicker("Parc Spend backup", ".monzobak", $"parc-spend-backup-{DateTime.Today:yyyy-MM-dd}");
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            byte[] backup = await _preferencesService.CreateBackupAsync(password);
            await FileIO.WriteBytesAsync(file, backup);
            SetStatus("Encrypted finance backup saved.", InfoBarSeverity.Success);
        }, "Could not create a finance backup.");
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".monzobak");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null || await BackupPasswordAsync(creating: false) is not { } password)
            {
                return;
            }

            using Stream stream = await file.OpenStreamForReadAsync();
            Dictionary<string, string?> restoration;
            try
            {
                restoration = await _preferencesService.ReadBackupAsync(stream, password);
            }
            catch (CryptographicException)
            {
                SetStatus("The backup password is incorrect or the file was changed.", InfoBarSeverity.Warning);
                return;
            }
            catch (InvalidDataException exception)
            {
                SetStatus(exception.Message, InfoBarSeverity.Warning);
                return;
            }

            if (!await ConfirmAsync("Replace your finance data?",
                "This will replace current balances, history, budgets, goals, transaction rules and display settings with the backup.",
                "Restore and replace"))
            {
                return;
            }

            await _preferencesLock.WaitAsync();
            try
            {
                await _preferencesService.RestoreAsync(restoration);
                await LoadPreferencesAsync();
            }
            finally
            {
                _preferencesLock.Release();
            }
            SetUndo(null);
            await LoadBudgetsAsync();
            await LoadManualFinanceAsync();
            await LoadMonthlyHistoryAsync();
            await LoadLedgerAsync();
            SetStatus("Finance backup restored.", InfoBarSeverity.Success);
        }, "Could not restore the finance backup.");
    }

    private async Task<string?> BackupPasswordAsync(bool creating)
    {
        var password = new PasswordBox { Header = "Backup password" };
        var repeat = new PasswordBox { Header = "Repeat password" };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(password);
        if (creating)
        {
            panel.Children.Add(repeat);
            panel.Children.Add(new TextBlock
            {
                Text = "Use at least 12 characters. You’ll need this password to restore the backup.",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        panel.Children.Add(message);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = creating ? "Protect your backup" : "Unlock your backup",
            Content = panel,
            PrimaryButtonText = creating ? "Continue" : "Unlock",
            CloseButtonText = "Cancel",
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = password.Password.Length == 0
                || (creating && (password.Password.Length < 12 || !string.Equals(password.Password, repeat.Password, StringComparison.Ordinal)));
            message.Text = args.Cancel ? "Check the backup password." : "";
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? password.Password : null;
    }

    private async void DisconnectMonzo_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (!await ConfirmAsync("Disconnect Monzo?", "The app will remove the saved session from this Windows profile.", "Disconnect"))
            {
                return;
            }

            if (!await _monzoSyncLock.WaitAsync(0))
            {
                SetStatus("Wait for the current sync to finish before disconnecting.", InfoBarSeverity.Informational);
                return;
            }

            bool disconnected;
            try
            {
                disconnected = await _authService.LogoutAsync();
            }
            finally
            {
                _monzoSyncLock.Release();
            }

            if (disconnected)
            {
                ApplyLedger();
                SetStatus("Monzo disconnected. Saved balances and history remain available.", InfoBarSeverity.Success);
            }
            else
            {
                SetStatus("Could not clear the saved Monzo session.", InfoBarSeverity.Error);
            }
        }, "Could not disconnect Monzo.");
    }

    private TransactionRule? RuleFor(Transaction transaction)
    {
        if (AnnotationFor(transaction.Id).TransferWith is not null)
        {
            return new TransactionRule(FinancePreferences.Transfer, null);
        }

        string fallback = transaction.IsLoad ? FinancePreferences.Transfer : FinancePreferences.Normal;
        return _preferences.TransactionRules.GetValueOrDefault(transaction.Id) ?? new TransactionRule(fallback, null);
    }

    private bool IsRefund(Transaction transaction) => transaction.Amount > 0 && (AnnotationFor(transaction.Id).OffsetOf is not null
        || RuleFor(transaction)?.Treatment == FinancePreferences.Refund);

    private decimal SpendEffect(Transaction transaction) =>
        transaction.Amount < 0 || IsRefund(transaction) ? -transaction.Amount : 0;

    private static string TransactionParty(Transaction transaction) => transaction.Merchant ?? transaction.Description ?? "";

    private async void ReviewTransactions_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (_ledgerTransactions.Count == 0)
            {
                SetStatus("Sync Monzo or import a statement to review transactions.", InfoBarSeverity.Informational);
                return;
            }

            var rows = _ledgerTransactions.Select(transaction => new TransactionReviewItem(transaction,
                $"{transaction.Created.ToLocalTime():dd MMM} · {transaction.Merchant ?? transaction.Description ?? "Transaction"} · {SignedMoney(transaction.Amount)} · {RuleFor(transaction)?.Treatment ?? FinancePreferences.Normal}")).ToArray();
            var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = rows, DisplayMemberPath = nameof(TransactionReviewItem.Label), MaxHeight = 285 };
            var treatment = new ComboBox
            {
                Header = "Treat selected transactions as",
                ItemsSource = new[] { "Keep current", FinancePreferences.Normal, FinancePreferences.Transfer, FinancePreferences.Refund },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var category = new TextBox { Header = "Category override", PlaceholderText = "Leave empty to keep current category", MaxLength = 60 };
            var remember = new CheckBox { Content = "Remember category for selected merchants" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = "Select one or more transactions. Mark transfers between your accounts or credit refunds to their spending category.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(list);
            content.Children.Add(treatment);
            content.Children.Add(category);
            content.Children.Add(remember);
            content.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Review transactions",
                Content = content,
                PrimaryButtonText = "Apply to selected",
                CloseButtonText = "Cancel",
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                TransactionReviewItem[] selected = list.SelectedItems.OfType<TransactionReviewItem>().ToArray();
                string? change = treatment.SelectedItem as string;
                error.Text = selected.Length == 0 ? "Select at least one transaction."
                    : change == FinancePreferences.Refund && selected.Any(item => item.Transaction.Amount <= 0)
                        ? "Only incoming transactions can be treated as refunds." : "";
                args.Cancel = error.Text.Length > 0;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            string? newCategory = string.IsNullOrWhiteSpace(category.Text) ? null : category.Text.Trim();
            string? newTreatment = treatment.SelectedIndex == 0 ? null : treatment.SelectedItem as string;
            await UpdatePreferencesAsync(current =>
            {
                var rules = new Dictionary<string, TransactionRule>(current.TransactionRules, StringComparer.OrdinalIgnoreCase);
                var merchants = new Dictionary<string, TransactionRule>(current.MerchantRules, StringComparer.OrdinalIgnoreCase);
                foreach (TransactionReviewItem item in list.SelectedItems.OfType<TransactionReviewItem>())
                {
                    Transaction transaction = item.Transaction;
                    TransactionRule? prior = rules.GetValueOrDefault(transaction.Id);
                    rules[transaction.Id] = new TransactionRule(newTreatment ?? prior?.Treatment ?? RuleFor(transaction)!.Treatment,
                        newCategory ?? prior?.Category);
                    if (remember.IsChecked == true && newCategory is not null && transaction.Merchant is { Length: > 0 } merchant)
                    {
                        merchants[merchant] = new TransactionRule(FinancePreferences.Normal, newCategory);
                    }
                }
                return current with { TransactionRules = rules, MerchantRules = merchants };
            });
            RefreshTransactionViews();
            SetStatus("Transaction review applied.", InfoBarSeverity.Success);
        }, "Could not apply the transaction review.");
    }

    private void RefreshTransactionViews()
    {
        _rawTransactions.Clear();
        _activityTransactions.Clear();
        foreach (Transaction transaction in _ledgerTransactions)
        {
            TransactionRule? rule = RuleFor(transaction);
            string? merchantCategory = transaction.Merchant is { } merchant
                ? _preferences.MerchantRules.FirstOrDefault(pair => pair.Key.Equals(merchant, StringComparison.OrdinalIgnoreCase)).Value?.Category : null;
            var display = new Transaction
            {
                Id = transaction.Id,
                AccountId = transaction.AccountId,
                IsPending = transaction.IsPending,
                IsDeclined = transaction.IsDeclined,
                Created = transaction.Created,
                Amount = transaction.Amount,
                Currency = transaction.Currency,
                Description = transaction.Description,
                Merchant = transaction.Merchant,
                IsLoad = transaction.IsLoad,
                Category = rule?.Category ?? merchantCategory ?? transaction.Category,
            };
            _activityTransactions.Add(display);
            if (rule?.Treatment != FinancePreferences.Transfer && !display.IsPending && !display.IsDeclined)
            {
                _rawTransactions.Add(display);
            }
        }

        RebuildCategoryFilterOptions();
        ApplyTransactionFilters();
        ApplyBudgetSpend();
        if (_activityTransactions.Count != 0)
        {
            ApplyAnalytics();
            RefreshSubscriptionInsights();
        }
        else
        {
            ClearMonzoViews();
        }
        UpdateCashFlowForecast();
    }

    private void ClearMonzoViews()
    {
        _categorySummaryItems.Clear();
        _cashFlowEvents.Clear();
        _subscriptions.Clear();
        CashFlowPolyline.Points.Clear();
        TotalSpentTextBlock.Text = "Total spent: —";
        TopCategoryTextBlock.Text = "Top category: —";
        TransactionCountTextBlock.Text = "Transactions: —";
        ThisMonthSpendTextBlock.Text = "This month spend: —";
        ThisMonthIncomeTextBlock.Text = "This month income: —";
        ThisMonthNetTextBlock.Text = "This month net: —";
        SavingsRateTextBlock.Text = "Savings rate: —";
        MonthlyIncomeTextBlock.Text = "—";
        MonthlySpendTextBlock.Text = "—";
        MonthlyCommitmentsTextBlock.Text = "Monthly commitments: —";
        ProjectedEndBalanceTextBlock.Text = "Connect Monzo in Settings to see a forecast.";
        CashFlowHealthTextBlock.Text = "";
        CashTightWeeks.Text = "";
    }

    private sealed record TransactionReviewItem(Transaction Transaction, string Label);
}
