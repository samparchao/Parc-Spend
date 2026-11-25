// -----------------------------------------------------------------------
// <copyright file="MainWindow.Ledger.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonzoManager.Models;
using MonzoManager.Services;
using Refit;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace MonzoManager;

public sealed partial class MainWindow
{
    private readonly LedgerService _ledgerService = new();
    private readonly SemaphoreSlim _monzoSyncLock = new(1, 1);
    private LedgerState _ledger = new();

    private async Task LoadLedgerAsync()
    {
        _ledger = await _ledgerService.LoadAsync();
        ApplyLedger();
    }

    private async Task SaveLedgerAsync(Func<LedgerState, LedgerState> update)
    {
        _ledger = await _ledgerService.UpdateAsync(update);
        ApplyLedger();
    }

    private IEnumerable<LedgerAccount> EnabledAccounts() => _ledger.Accounts.Where(account => account.Enabled);

    private IEnumerable<MonthlyBalance> LinkedMonthlyBalances()
    {
        foreach (LedgerAccount account in EnabledAccounts())
        {
            if (account.Balance is decimal amount)
            {
                yield return new MonthlyBalance(account.Name, IncludedShare(amount, account.Ownership),
                    amount < 0, IsMonzo: account.IsMonzo, AccountId: account.IsMonzo ? "monzo:" + account.Id : account.Id,
                    Kind: account.Kind == "Credit card" ? "Debt" : "Cash");
            }

            foreach (LedgerPot pot in _ledger.Pots.Where(pot => pot.AccountId == account.Id))
            {
                yield return new MonthlyBalance(pot.Name, IncludedShare(pot.Balance, account.Ownership),
                    false, AccountId: "pot:" + pot.Id, Kind: "Pot");
            }
        }
    }

    private void ApplyLedger()
    {
        LedgerAccount[] enabled = EnabledAccounts().ToArray();
        HashSet<string> enabledIds = enabled.Select(account => account.Id).ToHashSet();
        _ledgerTransactions.Clear();
        _ledgerTransactions.AddRange(_ledger.Transactions.Where(item => enabledIds.Contains(item.AccountId)));
        LedgerAccount[] linked = enabled.Where(account => account.IsMonzo && account.Balance.HasValue).ToArray();
        _hasMonzoBalance = linked.Length > 0;
        _currentBalance = linked.Sum(account => IncludedShare(account.Balance!.Value, account.Ownership));
        _lastMonzoSync = linked.Select(account => account.SyncedAt?.LocalDateTime ?? DateTime.MinValue).DefaultIfEmpty().Max();
        ConnectedAccount.Visibility = _hasMonzoBalance ? Visibility.Visible : Visibility.Collapsed;
        AccountIdTextBlock.Text = $"{linked.Length} Monzo account{(linked.Length == 1 ? "" : "s")} included";
        BalanceTextBlock.Text = _hasMonzoBalance ? Money(_currentBalance) : "—";
        CurrencyTextBlock.Text = "GBP";
        TotalCashTextBlock.Text = _hasMonzoBalance ? Money(_currentBalance) : "—";
        decimal reserved = _ledger.Pots.Where(pot => enabledIds.Contains(pot.AccountId))
            .Sum(pot => IncludedShare(pot.Balance, enabled.First(account => account.Id == pot.AccountId).Ownership));
        PotsBalanceText.Text = $"Set aside in Pots  {Money(reserved)}";
        PotsBalanceText.Visibility = _ledger.Pots.Any(pot => enabledIds.Contains(pot.AccountId)) ? Visibility.Visible : Visibility.Collapsed;
        OverviewPotsText.Text = PotsBalanceText.Text;
        OverviewPotsText.Visibility = PotsBalanceText.Visibility;
        DateTime today = DateTime.Today;
        DateTime monthStart = new DateTime(today.Year, today.Month, 1).ToUniversalTime();
        OverviewCoverageText.Text = enabled.Length > 0 && enabled.Any(account => !LedgerService.Covers(_ledger, account, monthStart, DateTime.UtcNow.AddMinutes(-5)))
            ? "This month’s totals are based on incomplete account coverage." : "";
        ConnectionLabel.Text = _hasMonzoBalance
            ? _authService.IsAuthenticated ? "Monzo connected" : "Saved Monzo data"
            : "Manual tracking";
        ConnectionDot.Fill = Brush(_authService.IsAuthenticated && _hasMonzoBalance ? "#6CE4BD" : "#899490");

        string? selected = (LedgerAccountFilter.SelectedItem as LedgerAccount)?.Id;
        LedgerAccountFilter.ItemsSource = new[] { new LedgerAccount("", "All accounts", "", true, 100) }.Concat(enabled).ToArray();
        LedgerAccountFilter.SelectedItem = ((LedgerAccount[])LedgerAccountFilter.ItemsSource)
            .FirstOrDefault(account => account.Id == selected) ?? ((LedgerAccount[])LedgerAccountFilter.ItemsSource)[0];

        string? dateSelection = DateFilterComboBox.SelectedItem as string;
        DateTime todayMonth = new(today.Year, today.Month, 1);
        DateTime earliest = _ledger.Transactions.Select(item => item.Created.ToLocalTime())
            .Concat(_ledger.Coverage.Select(range => range.FromUtc.ToLocalTime())).DefaultIfEmpty(todayMonth).Min();
        int months = Math.Clamp((today.Year - earliest.Year) * 12 + today.Month - earliest.Month, 12, 240);
        string[] periods = ["All time", "Last 7 days", "Last 30 days", "This month",
            .. Enumerable.Range(1, months).Select(offset => todayMonth.AddMonths(-offset).ToString("MMMM yyyy", Uk))];
        DateFilterComboBox.ItemsSource = periods;
        DateFilterComboBox.SelectedItem = periods.Contains(dateSelection) ? dateSelection : periods[0];

        RefreshTransactionViews();
        RefreshNetWorthOverview();
        ShowGoals();
        ShowPlannedExpenses();
    }

    private async Task SyncMonzoLedgerAsync(bool backfill)
    {
        if (!await _monzoSyncLock.WaitAsync(0))
        {
            SetStatus("Monzo sync is already running.", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            IReadOnlyList<MonzoAccount> accounts = await DiscoverMonzoAccountsAsync(waitForApproval: backfill);
            if (accounts.Count == 0)
            {
                SetStatus("Monzo did not return any personal or joint accounts.", InfoBarSeverity.Warning);
                return;
            }

            await SaveLedgerAsync(state =>
            {
                bool firstConnection = !state.Accounts.Any(item => item.IsMonzo);
                string defaultId = (accounts.FirstOrDefault(item => item.Type == "uk_retail") ?? accounts[0]).Id;
                var discovered = accounts.Select(account =>
                {
                    LedgerAccount? existing = state.Accounts.FirstOrDefault(item => item.Id == account.Id);
                    bool joint = account.Type == "uk_retail_joint";
                    return existing ?? new LedgerAccount(account.Id, joint ? "Monzo joint" : "Monzo personal",
                        joint ? "Monzo joint" : "Monzo personal", firstConnection && account.Id == defaultId,
                        joint ? 50 : 100, account.Created?.UtcDateTime);
                });
                var returned = accounts.Select(account => account.Id).ToHashSet();
                return state with { Accounts = state.Accounts.Where(account => !returned.Contains(account.Id)).Concat(discovered).ToArray() };
            });

            LedgerAccount[] selected = _ledger.Accounts.Where(account => account.Enabled && account.IsMonzo
                && accounts.Any(discovered => discovered.Id == account.Id)).ToArray();
            if (selected.Length == 0)
            {
                SetStatus("Choose the accounts to sync under Manage accounts.", InfoBarSeverity.Informational);
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime recentStart = now.AddDays(-89);
            int failures = 0;
            int retrieved = 0;
            foreach (LedgerAccount account in selected)
            {
                try
                {
                    MonzoBalanceResponse balance = await _monzoService.ReadBalanceAsync(account.Id);
                    if (balance.Currency != "GBP")
                    {
                        failures++;
                        continue;
                    }

                    await SaveLedgerAsync(state => state with
                    {
                        Accounts = state.Accounts.Select(item => item.Id == account.Id
                            ? item with { Balance = balance.Balance / 100m, SyncedAt = DateTimeOffset.Now } : item).ToArray(),
                    });
                }
                catch (Exception exception) when (exception is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    failures++;
                }

                try
                {
                    IReadOnlyList<MonzoPot> pots = await _monzoService.ListPotsAsync(account.Id);
                    HashSet<string> returnedPots = pots.Select(pot => pot.Id).ToHashSet();
                    await SaveLedgerAsync(state => state with
                    {
                        Pots = state.Pots.Where(pot => pot.AccountId != account.Id && !returnedPots.Contains(pot.Id))
                            .Concat(pots.Select(pot => new LedgerPot(pot.Id, account.Id, pot.Name, pot.Balance / 100m))).ToArray(),
                    });
                }
                catch (Exception exception) when (exception is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    failures++;
                }

                try
                {
                    DateTime from = account.OpenedUtc is { } opened && opened > recentStart ? opened : recentStart;
                    if (from < now)
                    {
                        retrieved += await SyncRangeAsync(account.Id, from, now, markCoverage: true);
                    }

                    if (backfill && (account.OpenedUtc is null || account.OpenedUtc < recentStart))
                    {
                        DateTime start = account.OpenedUtc ?? new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        retrieved += await SyncRangeAsync(account.Id, start, recentStart, markCoverage: false);
                    }
                }
                catch (Exception exception) when (exception is ApiException or System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
                {
                    failures++;
                }
            }

            string result = failures == 0
                ? $"Monzo sync complete. Checked {retrieved:N0} transactions."
                : $"Monzo sync was incomplete ({failures} unsuccessful requests). Saved history is still available.";
            if (backfill && failures > 0)
            {
                result += " Approve access in Monzo, then retry backfill.";
            }
            SetStatus(result, failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            _monzoSyncLock.Release();
        }
    }

    private async Task<IReadOnlyList<MonzoAccount>> DiscoverMonzoAccountsAsync(bool waitForApproval)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(55);
        while (true)
        {
            try
            {
                return await _monzoService.ListAccountsAsync();
            }
            catch (ApiException exception) when (waitForApproval && exception.StatusCode == HttpStatusCode.Forbidden
                && DateTime.UtcNow < deadline)
            {
                SetStatus("Approve access in your Monzo app to continue syncing.", InfoBarSeverity.Informational);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private async Task<int> SyncRangeAsync(string accountId, DateTime from, DateTime until, bool markCoverage)
    {
        HistoryPageResult page = await _monzoService.ReadHistoryAsync(accountId, from, until);
        IReadOnlyList<Transaction> transactions = page.Transactions;
        // Older requests may be clipped by Monzo. An empty historical response cannot prove coverage.
        DateTime? coveredFrom = markCoverage ? from : transactions.Count > 0 ? transactions.Min(item => item.Created) : null;
        LedgerCoverage? coverage = page.Complete && coveredFrom is { } start && transactions.All(transaction => transaction.Currency == "GBP")
            ? new(accountId, start, until) : null;
        await SaveLedgerAsync(state => LedgerService.Merge(state, accountId,
            transactions.Where(transaction => transaction.Currency == "GBP").ToArray(), coverage));
        if (!page.Complete)
        {
            throw new InvalidDataException("Monzo sync returned partial history. The rows received so far were saved.");
        }
        return transactions.Count;
    }

    private async void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            var panel = new StackPanel { Spacing = 9 };
            var rows = new List<AccountEditor>();
            if (_ledger.Accounts.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Connect Monzo to discover accounts, or add an account for statement imports.", TextWrapping = TextWrapping.Wrap });
            }

            foreach (LedgerAccount account in _ledger.Accounts.OrderBy(item => item.Name))
            {
                var include = new CheckBox { Content = $"Include {account.Name}", IsChecked = account.Enabled };
                var name = new TextBox { Header = "Account name", Text = account.Name, MaxLength = 80 };
                var share = new TextBox { Header = "Ownership (%)", Text = account.Ownership.ToString("0.##", Uk) };
                TextBox? balance = account.IsMonzo ? null : new TextBox
                {
                    Header = "Current balance (£)",
                    PlaceholderText = account.Kind == "Credit card" ? "Negative amount owed" : "Optional",
                    Text = account.Balance?.ToString("0.##", Uk) ?? "",
                };
                string details = account.IsMonzo
                    ? $"{account.Kind} · {account.SyncedAt?.ToLocalTime().ToString("d MMM yyyy, HH:mm", Uk) ?? "not synced"}"
                    : account.Kind;
                panel.Children.Add(include);
                panel.Children.Add(new TextBlock { Text = details });
                panel.Children.Add(name);
                panel.Children.Add(share);
                if (balance is not null)
                {
                    panel.Children.Add(balance);
                }

                panel.Children.Add(new Border { Height = 1, Background = Brush("#363B3C") });
                rows.Add(new AccountEditor(account, name, include, share, balance));
            }

            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                Title = "Manage accounts",
                XamlRoot = RootGrid.XamlRoot,
                Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Add statement account",
                CloseButtonText = "Done",
            };
            var updates = new Dictionary<string, LedgerAccount>();
            dialog.PrimaryButtonClick += (dialogSender, args) =>
            {
                updates.Clear();
                foreach (AccountEditor row in rows)
                {
                    decimal? balance = row.Account.Balance;
                    if (string.IsNullOrWhiteSpace(row.Name.Text) || !decimal.TryParse(row.Share.Text, NumberStyles.Number, Uk, out decimal ownership)
                        || ownership is < 0 or > 100)
                    {
                        error.Text = "Enter an account name and an ownership percentage between 0 and 100.";
                        args.Cancel = true;
                        return;
                    }

                    if (row.Balance is { } input)
                    {
                        if (input.Text.Length != 0 && !TryReadAmount(input.Text, allowNegative: true, out _))
                        {
                            error.Text = "Enter a valid current balance or leave it blank.";
                            args.Cancel = true;
                            return;
                        }
                        balance = input.Text.Length == 0 ? null : decimal.Parse(input.Text, NumberStyles.Currency, Uk);
                    }

                    updates[row.Account.Id] = row.Account with
                    {
                        Name = row.Name.Text.Trim(),
                        Enabled = row.Include.IsChecked == true,
                        Ownership = ownership,
                        Balance = balance,
                    };
                }
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await SaveLedgerAsync(state => state with
                {
                    Accounts = state.Accounts.Select(account => updates.TryGetValue(account.Id, out LedgerAccount? revised)
                        ? revised with { Balance = revised.IsMonzo ? account.Balance : revised.Balance, SyncedAt = account.SyncedAt } : account).ToArray(),
                });
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await AddStatementAccountAsync();
            }
        }, "Could not update the accounts.");
    }

    private async Task AddStatementAccountAsync()
    {
        var name = new TextBox { Header = "Account name", MaxLength = 80 };
        var kind = new ComboBox { Header = "Account type", ItemsSource = new[] { "Current account", "Joint account", "Credit card" }, SelectedIndex = 0 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(name);
        panel.Children.Add(kind);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Add statement account",
            Content = panel,
            PrimaryButtonText = "Add account",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var account = new LedgerAccount("csv:" + Guid.NewGuid().ToString("N"), name.Text.Trim(),
                (string)kind.SelectedItem, true, kind.SelectedIndex == 1 ? 50 : 100);
            await SaveLedgerAsync(state => state with { Accounts = state.Accounts.Append(account).ToArray() });
            SetStatus("Statement account added. Choose Import statement to add transactions.", InfoBarSeverity.Success);
        }
    }

    private async void ImportStatement_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (!_ledger.Accounts.Any(account => !account.IsMonzo))
            {
                await AddStatementAccountAsync();
            }

            var accounts = _ledger.Accounts.Where(account => !account.IsMonzo).ToArray();
            if (accounts.Length == 0)
            {
                return;
            }

            var choose = new ComboBox { Header = "Import into", ItemsSource = accounts, DisplayMemberPath = nameof(LedgerAccount.Name), SelectedIndex = 0 };
            var reverse = new CheckBox { Content = "Reverse signs (for statements with positive purchases)" };
            var options = new StackPanel { Spacing = 8 };
            options.Children.Add(choose);
            options.Children.Add(reverse);
            var destination = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Choose the statement account",
                Content = options,
                PrimaryButtonText = "Choose CSV",
                CloseButtonText = "Cancel",
            };
            if (await destination.ShowAsync() != ContentDialogResult.Primary || choose.SelectedItem is not LedgerAccount account)
            {
                return;
            }

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            BasicProperties properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > 32 * 1024 * 1024)
            {
                SetStatus("The CSV exceeds the 32 MB import limit.", InfoBarSeverity.Warning);
                return;
            }

            IReadOnlyList<Transaction> transactions;
            try
            {
                using Stream stream = await file.OpenStreamForReadAsync();
                bool reverseSign = reverse.IsChecked == true;
                transactions = await Task.Run(() => LedgerService.ReadStatement(stream, account.Id, reverseSign));
            }
            catch (Exception exception) when (exception is InvalidDataException or Microsoft.VisualBasic.FileIO.MalformedLineException)
            {
                SetStatus(exception is InvalidDataException ? exception.Message : "The CSV contains a malformed row.", InfoBarSeverity.Warning);
                return;
            }

            if (transactions.Count == 0)
            {
                SetStatus("The statement did not contain any transactions.", InfoBarSeverity.Warning);
                return;
            }

            DateTime first = transactions.Min(item => item.Created.ToLocalTime().Date);
            DateTime last = transactions.Max(item => item.Created.ToLocalTime().Date);
            var rangeStart = new CalendarDatePicker { Header = "Statement start", Date = new DateTimeOffset(first) };
            var rangeEnd = new CalendarDatePicker { Header = "Statement end (inclusive)", Date = new DateTimeOffset(last) };
            var complete = new CheckBox { Content = "CSV includes every transaction in this period" };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"{transactions.Count:N0} rows for {account.Name} · {Money(-transactions.Where(item => item.Amount < 0).Sum(item => item.Amount))} out · {Money(transactions.Where(item => item.Amount > 0).Sum(item => item.Amount))} in. Re-imports merge with saved transactions.",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(complete);
            panel.Children.Add(rangeStart);
            panel.Children.Add(rangeEnd);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var preview = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Review statement import",
                Content = panel,
                PrimaryButtonText = "Import transactions",
                CloseButtonText = "Cancel",
            };
            preview.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = complete.IsChecked == true
                    && (rangeStart.Date is null || rangeEnd.Date is null || rangeStart.Date.Value.Date > first || rangeEnd.Date.Value.Date < last);
                error.Text = args.Cancel ? "Choose a statement period containing all the transactions." : "";
            };
            if (await preview.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            LedgerCoverage? coverage = complete.IsChecked == true
                ? new LedgerCoverage(account.Id, rangeStart.Date!.Value.Date.ToUniversalTime(), rangeEnd.Date!.Value.Date.AddDays(1).ToUniversalTime())
                : null;
            await SaveLedgerAsync(state => LedgerService.Merge(state, account.Id, transactions, coverage));
            SetStatus($"Imported statement for {account.Name}.", InfoBarSeverity.Success);
        }, "Could not import the statement.");
    }

    private void LedgerAccountFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceReady)
        {
            ApplyTransactionFilters();
        }
    }

    private void UpdateCoverage(DateTime from, DateTime until, string accountId, bool allTime)
    {
        LedgerAccount[] accounts = EnabledAccounts().Where(account => accountId.Length == 0 || account.Id == accountId).ToArray();
        LedgerCoverageText.Visibility = accounts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (accounts.Length == 0)
        {
            return;
        }

        DateTime coveredUntil = until > DateTime.UtcNow ? DateTime.UtcNow : until;
        // A successful recent sync normally trails the current instant by a few seconds.
        if (coveredUntil > DateTime.UtcNow.AddMinutes(-5))
        {
            coveredUntil = DateTime.UtcNow.AddMinutes(-5);
        }

        var covered = _ledger.Coverage.Where(range => accounts.Any(account => account.Id == range.AccountId)).ToArray();
        string dates = covered.Length == 0 ? "No verified coverage"
            : $"Coverage span {covered.Min(range => range.FromUtc).ToLocalTime():d MMM yyyy} – {covered.Max(range => range.UntilUtc).AddTicks(-1).ToLocalTime():d MMM yyyy}";
        string[] incomplete = accounts.Where(account => !LedgerService.Covers(_ledger, account, from, coveredUntil))
            .Select(account => account.Name).ToArray();
        string period = allTime ? "This month" : "Selected period";
        bool gaps = covered.GroupBy(range => range.AccountId).Any(group => group.Count() > 1);
        string coverage = gaps ? $"{dates} (includes gaps)" : dates;
        LedgerCoverageText.Text = incomplete.Length == 0 ? $"{coverage} · {period} covered"
            : $"{coverage} · {period} incomplete for {string.Join(", ", incomplete)}";
    }

    private sealed record AccountEditor(LedgerAccount Account, TextBox Name, CheckBox Include, TextBox Share, TextBox? Balance);
}
