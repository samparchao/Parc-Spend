// -----------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonzoManager.Models;
using MonzoManager.Services;
using Serilog;
using Windows.Foundation;

namespace MonzoManager;

/// <summary>
/// Presents the Parc Spend balances, transaction activity, and planning tools.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IAuthService _authService;
    private readonly MonzoService _monzoService;
    private readonly IBudgetService _budgetService;
    private readonly ManualFinanceService _manualFinanceService;

    private readonly ObservableCollection<TransactionRowItem> _transactions = [];
    private readonly ObservableCollection<CategorySummaryItem> _categorySummaryItems = [];
    private readonly ObservableCollection<BudgetRowItem> _budgetRows = [];
    private readonly ObservableCollection<string> _budgetAlerts = [];
    private readonly ObservableCollection<CashFlowEventItem> _cashFlowEvents = [];
    private readonly ObservableCollection<SubscriptionItem> _subscriptions = [];

    private readonly List<Transaction> _ledgerTransactions = [];
    private readonly List<Transaction> _activityTransactions = [];
    private readonly List<Transaction> _rawTransactions = [];
    private readonly List<Budget> _rawBudgets = [];
    private decimal _currentBalance;
    private int _transactionPage;
    private const int TransactionPageSize = 200;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        this.InitializeComponent();

        this.Title = "Parc Spend";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Parc.ico"));
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(TitleBarDragRegion);
        AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 244, 246, 245);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 166, 175, 173);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 870));

        _authService = new AuthService();
        _monzoService = new MonzoService(_authService);
        _budgetService = new BudgetService();
        _manualFinanceService = new ManualFinanceService();

        TransactionsListView.ItemsSource = _transactions;
        CategorySummaryListView.ItemsSource = _categorySummaryItems;
        BudgetsListView.ItemsSource = _budgetRows;
        BudgetAlertsListView.ItemsSource = _budgetAlerts;
        CashFlowEventsListView.ItemsSource = _cashFlowEvents;
        SubscriptionsListView.ItemsSource = _subscriptions;

        CategoryFilterComboBox.ItemsSource = new[] { "All categories" };
        CategoryFilterComboBox.SelectedIndex = 0;
        DateFilterComboBox.ItemsSource = new[] { "All time", "Last 7 days", "Last 30 days", "This month" };
        LedgerStatusFilter.ItemsSource = new[] { "All statuses", "Pending", "Settled", "Declined", "Reimbursements" };
        LedgerStatusFilter.SelectedIndex = 0;
        BudgetCyclePicker.ItemsSource = new[] { "One-off", "Calendar month" }
            .Concat(Enumerable.Range(2, 30).Select(day => $"Payday · {day}")).ToArray();
        BudgetCyclePicker.SelectedIndex = 1;
        DateFilterComboBox.SelectedIndex = 0;
        SortFilterComboBox.ItemsSource = new[] { "Newest first", "Oldest first", "Largest spend", "Largest income" };
        SortFilterComboBox.SelectedIndex = 0;
        ForecastHorizonComboBox.ItemsSource = new[] { "30 days", "60 days", "90 days" };
        ForecastHorizonComboBox.SelectedIndex = 0;

        _workspaceReady = true;
        ApplyResponsiveLayout(RootGrid.ActualWidth);
        ShowWorkspace("overview", animate: false);
        this.Activated += OnActivated;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        this.Activated -= OnActivated;
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            Log.Error("Could not initialise the finance workspace ({ErrorType}).", exception.GetType().Name);
            SetStatus("Some saved data could not be loaded. Check the app log for details.", InfoBarSeverity.Error);
        }
    }

    private async Task InitializeAsync()
    {
        await _preferencesService.RecoverRestoreAsync();
        await LoadPreferencesAsync();
        await LoadBudgetsAsync().ConfigureAwait(true);
        await LoadManualFinanceAsync();
        await LoadMonthlyHistoryAsync();
        await LoadLedgerAsync();

        if (await _authService.EnsureValidTokenAsync().ConfigureAwait(true))
        {
            SetStatus("Syncing Monzo…", InfoBarSeverity.Informational);
            await LoadDashboardAsync().ConfigureAwait(true);
        }
    }

    private async void CredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        (bool submitted, string clientId, string clientSecret, string redirectUri) = await ShowCredentialsDialogAsync().ConfigureAwait(true);
        if (!submitted)
        {
            return;
        }

        try
        {
            await _authService.SaveCredentialsAsync(clientId, clientSecret, redirectUri).ConfigureAwait(true);
            SetStatus("Credentials saved with local encryption.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Log.Error("Failed to save API credentials ({ErrorType}).", exception.GetType().Name);
            SetStatus("Could not save credentials. Verify values and retry.", InfoBarSeverity.Error);
        }
    }

    private async void AccessTokenButton_Click(object sender, RoutedEventArgs e)
    {
        (bool submitted, string userId, string accessToken) = await ShowAccessTokenDialogAsync().ConfigureAwait(true);
        if (!submitted)
        {
            return;
        }

        try
        {
            await _authService.SaveAccessTokenAsync(accessToken, userId).ConfigureAwait(true);
            SetStatus("Access token saved securely. Refreshing data...", InfoBarSeverity.Success);
            await SyncMonzoLedgerAsync(backfill: true);
        }
        catch (Exception exception)
        {
            Log.Error("Failed to save access token ({ErrorType}).", exception.GetType().Name);
            SetStatus("Could not save access token. Check value and retry.", InfoBarSeverity.Error);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await ConnectMonzoAsync(backfill: true);

    private async void BackfillHistory_Click(object sender, RoutedEventArgs e)
    {
        await RunFinanceActionAsync(async () =>
        {
            if (await _authService.HasStoredCredentialsAsync())
            {
                await ConnectMonzoAsync(backfill: true);
            }
            else if (await _authService.EnsureValidTokenAsync())
            {
                await SyncMonzoLedgerAsync(backfill: true);
            }
            else
            {
                SetStatus("Connect Monzo or enter a fresh access token to backfill.", InfoBarSeverity.Warning);
            }
        }, "Could not backfill Monzo history.");
    }

    private async Task ConnectMonzoAsync(bool backfill)
    {
        if (!LoginButton.IsEnabled)
        {
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            bool hasCredentials = await _authService.HasStoredCredentialsAsync().ConfigureAwait(true);
            if (!hasCredentials)
            {
                SetStatus("Store API credentials first, or use Access Token.", InfoBarSeverity.Warning);
                return;
            }

            SetStatus("Launching secure Monzo OAuth flow...", InfoBarSeverity.Informational);
            bool success = await _authService.LoginAsync().ConfigureAwait(true);
            if (!success)
            {
                SetStatus("Login failed. Confirm credentials and redirect URI.", InfoBarSeverity.Error);
                return;
            }

            SetStatus("Login complete. Loading account data...", InfoBarSeverity.Success);
            await SyncMonzoLedgerAsync(backfill);
        }
        catch (Exception exception)
        {
            Log.Error("Error during login ({ErrorType}).", exception.GetType().Name);
            SetStatus("Unexpected error during login.", InfoBarSeverity.Error);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            if (await _authService.EnsureValidTokenAsync())
            {
                await LoadDashboardAsync();
            }
            else
            {
                SetStatus("Connect Monzo or update your access token before refreshing.", InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not refresh Monzo ({ErrorType}).", exception.GetType().Name);
            SetStatus("Could not refresh Monzo. Try again.", InfoBarSeverity.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void AddBudgetButton_Click(object sender, RoutedEventArgs e)
    {
        string category = (BudgetCategoryTextBox.Text ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(category))
        {
            SetStatus("Budget category is required.", InfoBarSeverity.Warning);
            return;
        }

        if (!TryReadAmount(BudgetLimitTextBox.Text, allowNegative: false, out decimal limit) || limit <= 0)
        {
            SetStatus("Budget limit must be a positive number.", InfoBarSeverity.Warning);
            return;
        }

        var budget = new Budget
        {
            Id = category,
            Category = category,
            Limit = limit,
            CycleDay = Math.Max(0, BudgetCyclePicker.SelectedIndex),
            Rollover = BudgetCyclePicker.SelectedIndex > 0 && BudgetRollover.IsChecked == true,
        };

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        int cycle = Math.Max(1, budget.CycleDay);
        DateOnly start = LedgerService.CycleStart(today, cycle);
        Budget? previous = _rawBudgets.FirstOrDefault(item => item.Id == budget.Id);
        budget.StartDate = budget.CycleDay > 0 && previous?.CycleDay == budget.CycleDay
            && previous.Limit == budget.Limit && previous.Rollover == budget.Rollover
            ? previous.StartDate : start.ToDateTime(TimeOnly.MinValue);
        budget.EndDate = LedgerService.InMonth(start.AddMonths(1), cycle).AddDays(-1).ToDateTime(TimeOnly.MinValue);

        await _budgetService.SaveBudgetAsync(budget).ConfigureAwait(true);
        await LoadBudgetsAsync().ConfigureAwait(true);
        ApplyBudgetSpend();
        SetStatus($"Budget saved for {category}.", InfoBarSeverity.Success);
    }

    private async void DeleteBudgetButton_Click(object sender, RoutedEventArgs e)
    {
        if (BudgetsListView.SelectedItem is not BudgetRowItem selectedItem)
        {
            SetStatus("Select a budget row before deleting.", InfoBarSeverity.Warning);
            return;
        }

        await _budgetService.DeleteBudgetAsync(selectedItem.Id).ConfigureAwait(true);
        await LoadBudgetsAsync().ConfigureAwait(true);
        ApplyBudgetSpend();
        SetStatus($"Deleted budget for {selectedItem.Category}.", InfoBarSeverity.Success);
    }

    private Task LoadDashboardAsync() => SyncMonzoLedgerAsync(backfill: false);

    private async Task LoadBudgetsAsync()
    {
        IReadOnlyList<Budget> budgets = await _budgetService.GetBudgetsAsync().ConfigureAwait(true);

        _rawBudgets.Clear();
        _rawBudgets.AddRange(budgets.OrderBy(budget => budget.Category));

        RebuildBudgetRows();
    }

    private void ApplyAnalytics()
    {
        decimal totalSpent = _rawTransactions
            .Sum(SpendEffect);

        TransactionCountTextBlock.Text = $"Transactions loaded: {_activityTransactions.Count}";
        TotalSpentTextBlock.Text = $"Total spent: {FormatCurrency(totalSpent)}";

        var categoryTotals = _rawTransactions
            .SelectMany(SpendPostings)
            .GroupBy(posting => posting.Category, StringComparer.OrdinalIgnoreCase)
            .Select(
                group => new
                {
                    Category = group.Key,
                    Spent = group.Sum(posting => posting.Amount),
                })
            .Where(item => item.Spent > 0)
            .OrderByDescending(item => item.Spent)
            .ToList();

        TopCategoryTextBlock.Text = categoryTotals.Count > 0
            ? $"Top category: {categoryTotals[0].Category} ({FormatCurrency(categoryTotals[0].Spent)})"
            : "Top category: -";

        _categorySummaryItems.Clear();
        foreach (var item in categoryTotals.Take(6))
        {
            _categorySummaryItems.Add(new CategorySummaryItem(item.Category, FormatCurrency(item.Spent)));
        }

        DateTime monthStart = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        List<Transaction> monthlyTransactions = _rawTransactions.Where(transaction => transaction.Created.ToLocalTime() >= monthStart).ToList();
        decimal monthlySpend = monthlyTransactions.Sum(SpendEffect);
        decimal monthlyIncome = monthlyTransactions.Where(transaction => transaction.Amount > 0 && !IsRefund(transaction)).Sum(transaction => transaction.Amount);
        decimal monthlyNet = monthlyIncome - monthlySpend;

        ThisMonthSpendTextBlock.Text = $"This month spend: {FormatCurrency(-monthlySpend)}";
        ThisMonthIncomeTextBlock.Text = $"This month income: {FormatCurrency(monthlyIncome)}";
        ThisMonthNetTextBlock.Text = $"This month net: {FormatCurrency(monthlyNet)}";
        MonthlyIncomeTextBlock.Text = Money(monthlyIncome);
        MonthlySpendTextBlock.Text = Money(monthlySpend);
        SavingsRateTextBlock.Text = monthlyIncome > 0
            ? $"Savings rate: {monthlyNet / monthlyIncome:P1}"
            : "Savings rate: —";
    }

    private void ApplyBudgetSpend() => RebuildBudgetRows();

    private void RebuildBudgetRows()
    {
        _budgetRows.Clear();
        _budgetAlerts.Clear();

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateTime asOfUtc = DateTime.UtcNow.AddMinutes(-5);
        LedgerAccount[] accounts = EnabledAccounts().ToArray();
        var postings = _rawTransactions
            .SelectMany(transaction => SpendPostings(transaction).Select(posting => (Day: DateOnly.FromDateTime(transaction.Created.ToLocalTime()), Posting: posting)))
            .ToLookup(item => item.Posting.Category, StringComparer.OrdinalIgnoreCase);
        decimal totalBudget = 0;
        decimal totalSpent = 0;

        foreach (Budget budget in _rawBudgets)
        {
            bool recurring = budget.CycleDay is >= 1 and <= 31;
            DateOnly first = DateOnly.FromDateTime(budget.StartDate);
            DateOnly start = recurring ? LedgerService.CycleStart(today, budget.CycleDay) : first;
            DateOnly next = recurring ? LedgerService.InMonth(start.AddMonths(1), budget.CycleDay)
                : DateOnly.FromDateTime(budget.EndDate).AddDays(1);
            decimal carry = 0;
            bool incompleteRollover = false;
            if (recurring && budget.Rollover && first < start)
            {
                for (DateOnly period = first; period < start;)
                {
                    DateOnly following = LedgerService.InMonth(period.AddMonths(1), budget.CycleDay);
                    if (accounts.Length > 0 && accounts.All(account => LedgerService.Covers(_ledger, account,
                        period.ToDateTime(TimeOnly.MinValue).ToUniversalTime(), following.ToDateTime(TimeOnly.MinValue).ToUniversalTime())))
                    {
                        carry = Math.Max(0, budget.Limit + carry - Spending(period, following));
                    }
                    else
                    {
                        incompleteRollover = true;
                        carry = 0;
                    }
                    period = following;
                }
            }

            decimal Spending(DateOnly from, DateOnly until) => Math.Max(0, postings[budget.Category]
                .Where(item => item.Day >= from && item.Day < until).Sum(item => item.Posting.Amount));

            decimal limit = budget.Limit + carry;
            decimal spent = Spending(start, next);
            double progress = limit > 0 ? (double)Math.Min(spent / limit, 1m) : 0;
            bool over = limit > 0 && spent > limit;
            DateTime endUtc = next.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
            DateTime through = endUtc < asOfUtc ? endUtc : asOfUtc;
            bool incomplete = accounts.Length == 0 || accounts.Any(account =>
                !LedgerService.Covers(_ledger, account, start.ToDateTime(TimeOnly.MinValue).ToUniversalTime(), through));

            string range = $"{start:dd MMM} – {next.AddDays(-1):dd MMM}";
            if (carry > 0)
            {
                range += $" · {Money(carry)} carried";
            }
            if (incompleteRollover)
            {
                range += " · partial carry";
            }

            if (over && today >= start && today < next)
            {
                _budgetAlerts.Add($"{budget.Category}: over budget by {Money(spent - limit)}");
            }

            _budgetRows.Add(new BudgetRowItem(budget.Id, budget.Category, $"Limit: {Money(limit)}", $"Spent: {Money(spent)}",
                progress, incomplete ? "Incomplete data" : over ? "Over budget" : $"{progress:P0}", range));

            if (today >= start && today < next)
            {
                totalBudget += limit;
                totalSpent += spent;
            }
        }

        if (_budgetAlerts.Count == 0)
        {
            _budgetAlerts.Add("No overspend alerts for the transactions recorded.");
        }

        BudgetUtilisationTextBlock.Text = totalBudget > 0
            ? $"{(totalSpent / totalBudget) * 100:F0}% of active budgets used" : "No active budgets";
    }

    private void RebuildCategoryFilterOptions()
    {
        string currentSelection = CategoryFilterComboBox.SelectedItem?.ToString() ?? "All categories";
        List<string> categories =
        [
            "All categories",
            .. _activityTransactions
                .SelectMany(transaction => SpendPostings(transaction).Select(item => item.Category)
                    .Append(transaction.Category ?? "uncategorized"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase),
        ];

        CategoryFilterComboBox.ItemsSource = categories;
        CategoryFilterComboBox.SelectedItem = categories.Contains(currentSelection, StringComparer.OrdinalIgnoreCase)
            ? categories.First(category => string.Equals(category, currentSelection, StringComparison.OrdinalIgnoreCase))
            : "All categories";
    }

    private void ApplyTransactionFilters(bool resetPage = true)
    {
        if (resetPage)
        {
            _transactionPage = 0;
        }

        IEnumerable<Transaction> filtered = _activityTransactions;
        string searchTerm = TransactionSearchTextBox.Text?.Trim() ?? string.Empty;
        string selectedCategory = CategoryFilterComboBox.SelectedItem?.ToString() ?? "All categories";
        string dateFilter = DateFilterComboBox.SelectedItem?.ToString() ?? "All time";
        string sortFilter = SortFilterComboBox.SelectedItem?.ToString() ?? "Newest first";

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            filtered = filtered.Where(
                transaction =>
                    (transaction.Description?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (transaction.Merchant?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.Equals(selectedCategory, "All categories", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(
                transaction => string.Equals(
                    transaction.Category ?? "uncategorized",
                    selectedCategory,
                    StringComparison.OrdinalIgnoreCase));
        }

        string accountId = (LedgerAccountFilter.SelectedItem as LedgerAccount)?.Id ?? "";
        if (accountId.Length != 0)
        {
            filtered = filtered.Where(transaction => transaction.AccountId == accountId);
        }
        filtered = (LedgerStatusFilter.SelectedItem as string) switch
        {
            "Pending" => filtered.Where(item => item.IsPending),
            "Settled" => filtered.Where(item => !item.IsPending && !item.IsDeclined),
            "Declined" => filtered.Where(item => item.IsDeclined),
            "Reimbursements" => filtered.Where(item => AnnotationFor(item.Id).ExpectedReimbursement > 0
                || TransactionStatus(item) == "Linked reimbursement"),
            _ => filtered,
        };
        DateTime today = DateTime.Today;
        DateTime now = DateTime.UtcNow;
        DateTime from = new DateTime(today.Year, today.Month, 1).ToUniversalTime();
        DateTime until = now;
        if (dateFilter == "Last 7 days")
        {
            from = now.AddDays(-7);
        }
        else if (dateFilter == "Last 30 days")
        {
            from = now.AddDays(-30);
        }
        else if (DateTime.TryParseExact(dateFilter, "MMMM yyyy", Uk, DateTimeStyles.None, out DateTime month))
        {
            from = month.ToUniversalTime();
            until = month.AddMonths(1).ToUniversalTime();
        }

        if (dateFilter != "All time")
        {
            filtered = filtered.Where(item => item.Created >= from && item.Created < until);
        }
        UpdateCoverage(from, until, accountId, dateFilter == "All time");

        filtered = sortFilter switch
        {
            "Oldest first" => filtered.OrderBy(transaction => transaction.Created),
            "Largest spend" => filtered.OrderBy(transaction => transaction.Amount),
            "Largest income" => filtered.OrderByDescending(transaction => transaction.Amount),
            _ => filtered.OrderByDescending(transaction => transaction.Created),
        };

        Transaction[] results = filtered.ToArray();
        int lastPage = Math.Max(0, (results.Length - 1) / TransactionPageSize);
        _transactionPage = Math.Clamp(_transactionPage, 0, lastPage);
        int first = _transactionPage * TransactionPageSize;
        int shown = Math.Min(TransactionPageSize, results.Length - first);
        TransactionPagerText.Text = results.Length == 0 ? "" : $"{first + 1:N0}–{first + shown:N0} of {results.Length:N0}";
        PreviousTransactionsButton.IsEnabled = _transactionPage > 0;
        NextTransactionsButton.IsEnabled = _transactionPage < lastPage;

        string? selectedId = (TransactionsListView.SelectedItem as TransactionRowItem)?.Id;
        _transactions.Clear();
        var accountNames = _ledger.Accounts.ToDictionary(account => account.Id, account => account.Name);
        foreach (Transaction transaction in results.Skip(first).Take(shown))
        {
            _transactions.Add(
                new TransactionRowItem(
                    transaction.Id,
                    transaction.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    transaction.Description ?? "(no description)",
                    transaction.Category ?? "uncategorized",
                    transaction.Merchant ?? "-",
                    $"{transaction.Created.ToLocalTime():yyyy-MM-dd HH:mm} · {accountNames.GetValueOrDefault(transaction.AccountId, "Account")} · {TransactionStatus(transaction)}",
                    FormatCurrency(transaction.Amount)));
        }
        if (selectedId is not null)
        {
            TransactionsListView.SelectedItem = _transactions.FirstOrDefault(item => item.Id == selectedId);
        }
        bool hasTransactions = _transactions.Count > 0;
        TransactionColumns.Visibility = TransactionsListView.Visibility = hasTransactions ? Visibility.Visible : Visibility.Collapsed;
        TransactionPaging.Visibility = results.Length > TransactionPageSize ? Visibility.Visible : Visibility.Collapsed;
        ActivityEmptyText.Visibility = hasTransactions ? Visibility.Collapsed : Visibility.Visible;
        ActivityEmptyText.Text = _activityTransactions.Count == 0
            ? "Connect or refresh Monzo in Settings, or import a statement." : "No transactions match these filters.";
    }

    private void PreviousTransactions_Click(object sender, RoutedEventArgs e)
    {
        _transactionPage--;
        ApplyTransactionFilters(resetPage: false);
    }

    private void NextTransactions_Click(object sender, RoutedEventArgs e)
    {
        _transactionPage++;
        ApplyTransactionFilters(resetPage: false);
    }

    private void TransactionSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyTransactionFilters();
    }

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTransactionFilters();
    }

    private void DateFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTransactionFilters();
    }

    private void SortFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTransactionFilters();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        TransactionSearchTextBox.Text = string.Empty;
        CategoryFilterComboBox.SelectedIndex = 0;
        DateFilterComboBox.SelectedIndex = 0;
        SortFilterComboBox.SelectedIndex = 0;
        LedgerAccountFilter.SelectedIndex = 0;
        LedgerStatusFilter.SelectedIndex = 0;
        ApplyTransactionFilters();
    }

    private void ForecastHorizonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCashFlowForecast();
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        WorkspaceStatus.Message = message;
        WorkspaceStatus.Severity = severity;
        WorkspaceStatus.IsOpen = true;
        Log.Debug("Workspace status updated ({Severity}).", severity);
    }

    private async Task<(bool Submitted, string ClientId, string ClientSecret, string RedirectUri)> ShowCredentialsDialogAsync()
    {
        var clientIdTextBox = new TextBox { PlaceholderText = "client_id" };
        var clientSecretBox = new PasswordBox();
        var redirectUriTextBox = new TextBox { Text = "http://localhost:8000/auth/callback" };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Client ID" });
        panel.Children.Add(clientIdTextBox);
        panel.Children.Add(new TextBlock { Text = "Client Secret" });
        panel.Children.Add(clientSecretBox);
        panel.Children.Add(new TextBlock { Text = "Redirect URI" });
        panel.Children.Add(redirectUriTextBox);
        panel.Children.Add(
            new TextBlock
            {
                Text = "Stored encrypted with Windows DPAPI.",
                Opacity = 0.75,
            });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "API Credentials",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return (
            result == ContentDialogResult.Primary,
            clientIdTextBox.Text.Trim(),
            clientSecretBox.Password.Trim(),
            redirectUriTextBox.Text.Trim());
    }

    private async Task<(bool Submitted, string UserId, string AccessToken)> ShowAccessTokenDialogAsync()
    {
        var userIdTextBox = new TextBox { PlaceholderText = "Optional user_id" };
        var accessTokenBox = new PasswordBox();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "User ID (optional)" });
        panel.Children.Add(userIdTextBox);
        panel.Children.Add(new TextBlock { Text = "Access Token" });
        panel.Children.Add(accessTokenBox);
        panel.Children.Add(
            new TextBlock
            {
                Text = "Token is encrypted locally. You may need to replace it after expiry.",
                Opacity = 0.75,
            });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Access Token",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return (
            result == ContentDialogResult.Primary,
            userIdTextBox.Text.Trim(),
            accessTokenBox.Password.Trim());
    }

    private static string FormatCurrency(decimal amount)
    {
        if (amount < 0)
        {
            return $"-£{Math.Abs(amount):F2}";
        }

        if (amount > 0)
        {
            return $"+£{amount:F2}";
        }

        return "£0.00";
    }

    private void UpdateCashFlowForecast()
    {
        if (!_workspaceReady)
        {
            return;
        }

        MonthlyBalance[] opening = LinkedMonthlyBalances().Where(balance => balance.Kind == "Cash").ToArray();
        if (opening.Length == 0)
        {
            opening = _holdings.Where(entry => entry.Kind == "Cash").Select(ToMonthlyBalance).ToArray();
        }

        if (opening.Length == 0)
        {
            ProjectedEndBalanceTextBlock.Text = "Sync Monzo or enter a current-account balance to forecast cash flow.";
            ForecastComparisonText.Text = "";
            CashFlowHealthTextBlock.Text = "";
            CashTightWeeks.Text = "";
            CashFlowPolyline.Points = [];
            _cashFlowEvents.Clear();
            return;
        }

        int horizonDays = ForecastHorizonComboBox.SelectedIndex switch
        {
            1 => 60,
            2 => 90,
            _ => 30,
        };

        HashSet<string> cashAccounts = EnabledAccounts().Where(account => account.Kind != "Credit card" && account.Balance.HasValue)
            .Select(account => account.Id).ToHashSet();
        Transaction[] cashActivity = _rawTransactions.Where(item => cashAccounts.Contains(item.AccountId)).ToArray();
        var recurringPatterns = DetectRecurringPatterns(cashActivity.Where(transaction => !IsRefund(transaction)).ToArray());
        DateTime thirtyDaysAgo = DateTime.Today.AddDays(-30);
        HashSet<string> recurringNames = recurringPatterns.Where(pattern => pattern.Amount < 0)
            .Select(pattern => pattern.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal recentDailySpend = cashActivity.Where(transaction => transaction.Created.ToLocalTime().Date >= thirtyDaysAgo
                && !recurringNames.Contains(TransactionParty(transaction)))
            .Sum(SpendEffect) / 30;
        List<ForecastEvent> baseline = BuildForecastEvents(recurringPatterns, horizonDays, Math.Max(0, recentDailySpend));
        DateTime startDate = DateTime.Today;
        DateTime endDate = startDate.AddDays(horizonDays);
        decimal runningBalance = opening.Sum(balance => balance.Amount);
        ForecastEvent[] planned = LedgerService.Occurrences(_ledger.Planned,
                DateOnly.FromDateTime(startDate), DateOnly.FromDateTime(endDate))
            .Select(item => new ForecastEvent(item.Date.ToDateTime(TimeOnly.MinValue), item.Expense.Name, -item.Expense.Amount)).ToArray();
        List<ForecastEvent> scenario = baseline.Where(estimate => !planned.Any(expense => estimate.Amount < 0
                && !estimate.IsEstimate && string.Equals(estimate.Title, expense.Title, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((estimate.Date - expense.Date).TotalDays) <= 3))
            .Concat(planned).ToList();
        List<ForecastEvent> events = IncludePlannedExpenses.IsChecked == true ? scenario : baseline;
        ForecastComparisonText.Text = planned.Length == 0 ? ""
            : $"Without planned expenses {Money(runningBalance + baseline.Sum(item => item.Amount))} · with them {Money(runningBalance + scenario.Sum(item => item.Amount))}";

        PointCollection points = [];
        double width = Math.Max(0, CashFlowCanvas.ActualWidth - 8);
        double height = CashFlowCanvas.Height;

        var dailyChanges = events
            .GroupBy(item => item.Date.Date)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));

        List<decimal> balances = [];
        var lowWeeks = new HashSet<DateTime>();
        for (DateTime day = startDate; day <= endDate; day = day.AddDays(1))
        {
            if (dailyChanges.TryGetValue(day, out decimal delta))
            {
                runningBalance += delta;
            }

            balances.Add(runningBalance);
            if (runningBalance < 0)
            {
                lowWeeks.Add(day.AddDays(-((int)day.DayOfWeek + 6) % 7));
            }
        }

        decimal minBalance = balances.Min();
        decimal maxBalance = balances.Max();
        decimal range = maxBalance - minBalance;
        if (range <= 0)
        {
            range = 1;
        }

        for (int i = 0; i < balances.Count; i++)
        {
            double x = balances.Count == 1 ? width / 2 : (width / (balances.Count - 1)) * i;
            double normalized = (double)((balances[i] - minBalance) / range);
            double y = maxBalance == minBalance ? height / 2 : height - (normalized * (height - 8)) - 4;
            points.Add(new Point(x + 4, y));
        }

        CashFlowPolyline.Points = points;
        decimal endBalance = balances.Last();
        ProjectedEndBalanceTextBlock.Text = $"Projected end balance: {FormatCurrency(endBalance)}";

        if (balances.Min() < 0)
        {
            CashFlowHealthTextBlock.Text = "Health: ⚠ Potential cash-flow issue detected.";
            CashFlowPolyline.Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 248, 113, 113));
        }
        else
        {
            CashFlowHealthTextBlock.Text = "Health: Healthy";
            CashFlowPolyline.Stroke = Brush("#F1CC86");
        }

        CashTightWeeks.Text = lowWeeks.Count == 0
            ? "No week is forecast below £0."
            : $"Forecast below £0 in {string.Join(", ", lowWeeks.OrderBy(week => week).Select(week => week.ToString("dd MMM", Uk)))}.";
        _cashFlowEvents.Clear();
        foreach (ForecastEvent forecastEvent in events.Where(item => !item.IsEstimate).OrderBy(item => item.Date).Take(10))
        {
            decimal after = balances[(forecastEvent.Date - startDate).Days];
            _cashFlowEvents.Add(new CashFlowEventItem(
                forecastEvent.Date.ToString("dd MMM", CultureInfo.InvariantCulture),
                $"{forecastEvent.Title} · day-end {Money(after)}",
                SignedMoney(forecastEvent.Amount)));
        }
    }

    private void RefreshSubscriptionInsights()
    {
        var recurringPatterns = DetectRecurringPatterns(_rawTransactions.Where(transaction => !IsRefund(transaction)).ToArray());
        var commitmentPatterns = recurringPatterns.Where(pattern => pattern.Amount < 0).ToList();

        decimal monthlyCommitments = commitmentPatterns.Sum(pattern => Math.Abs(pattern.Amount));
        MonthlyCommitmentsTextBlock.Text = $"Monthly commitments: {FormatCurrency(-monthlyCommitments)}";

        _subscriptions.Clear();
        foreach (RecurringPattern pattern in commitmentPatterns.OrderByDescending(pattern => Math.Abs(pattern.Amount)).Take(8))
        {
            decimal annual = Math.Abs(pattern.Amount) * 12;
            string trend = pattern.IsPriceIncreasing ? " • price increasing" : string.Empty;
            _subscriptions.Add(
                new SubscriptionItem(
                    pattern.Name,
                    $"~{FormatCurrency(pattern.Amount)}/month • annual {FormatCurrency(-annual)} • next {pattern.NextDate:dd MMM}{trend}"));
        }

        if (_subscriptions.Count == 0)
        {
            _subscriptions.Add(new SubscriptionItem("No recurring commitments detected", "Add more history to improve detection."));
        }
    }

    private static List<RecurringPattern> DetectRecurringPatterns(IReadOnlyList<Transaction> transactions)
    {
        DateTime utcNow = DateTime.UtcNow;
        return transactions
            .GroupBy(
                transaction => string.IsNullOrWhiteSpace(transaction.Merchant)
                    ? transaction.Description ?? "Unknown"
                    : transaction.Merchant!,
                StringComparer.OrdinalIgnoreCase)
            .Select(
                group =>
                {
                    List<Transaction> ordered = group.OrderBy(transaction => transaction.Created).ToList();
                    if (ordered.Count < 3)
                    {
                        return null;
                    }

                    List<double> intervals = [];
                    for (int i = 1; i < ordered.Count; i++)
                    {
                        intervals.Add((ordered[i].Created - ordered[i - 1].Created).TotalDays);
                    }

                    double avgInterval = intervals.Average();
                    if (avgInterval < 20 || avgInterval > 40)
                    {
                        return null;
                    }

                    decimal avgAmount = ordered.Average(item => item.Amount);
                    decimal lastAmount = ordered[^1].Amount;
                    bool priceIncreasing = avgAmount < 0 && Math.Abs(lastAmount) > Math.Abs(avgAmount) * 1.1m;
                    DateTime nextDate = ordered[^1].Created.AddDays(avgInterval);
                    if (nextDate < utcNow)
                    {
                        nextDate = utcNow.AddDays(avgInterval);
                    }

                    return new RecurringPattern(group.Key, avgAmount, nextDate, avgInterval, priceIncreasing);
                })
            .Where(pattern => pattern is not null)
            .Cast<RecurringPattern>()
            .ToList();
    }

    private static List<ForecastEvent> BuildForecastEvents(IReadOnlyList<RecurringPattern> recurringPatterns, int horizonDays, decimal recentDailySpend)
    {
        DateTime startDate = DateTime.Today;
        DateTime endDate = startDate.AddDays(horizonDays);
        List<ForecastEvent> events = [];

        foreach (RecurringPattern pattern in recurringPatterns)
        {
            for (DateTime date = pattern.NextDate.Date; date <= endDate; date = date.AddDays(pattern.IntervalDays))
            {
                if (date >= startDate)
                {
                    events.Add(new ForecastEvent(date, pattern.Name, pattern.Amount));
                }
            }
        }

        for (DateTime day = startDate.AddDays(1); day <= endDate; day = day.AddDays(1))
        {
            events.Add(new ForecastEvent(day, "Other spending", -recentDailySpend, IsEstimate: true));
        }

        return events;
    }

    private sealed record TransactionRowItem(
        string Id,
        string Date,
        string Description,
        string Category,
        string Merchant,
        string Meta,
        string Amount);

    private sealed record CategorySummaryItem(
        string Category,
        string Amount);

    private sealed record BudgetRowItem(
        string Id,
        string Category,
        string Limit,
        string Spent,
        double Progress,
        string ProgressLabel,
        string Range);

    private sealed record CashFlowEventItem(
        string Date,
        string Title,
        string Amount);

    private sealed record SubscriptionItem(
        string Name,
        string Details);

    private sealed record RecurringPattern(
        string Name,
        decimal Amount,
        DateTime NextDate,
        double IntervalDays,
        bool IsPriceIncreasing);

    private sealed record ForecastEvent(
        DateTime Date,
        string Title,
        decimal Amount,
        bool IsEstimate = false);
}
