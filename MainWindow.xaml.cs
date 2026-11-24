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
/// Main WinUI window with premium dashboard, analytics, and budget tooling.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IAuthService _authService;
    private readonly IMonzoService _monzoService;
    private readonly IBudgetService _budgetService;
    private readonly ManualFinanceService _manualFinanceService;

    private readonly ObservableCollection<TransactionRowItem> _transactions = [];
    private readonly ObservableCollection<CategorySummaryItem> _categorySummaryItems = [];
    private readonly ObservableCollection<BudgetRowItem> _budgetRows = [];
    private readonly ObservableCollection<string> _budgetAlerts = [];
    private readonly ObservableCollection<CashFlowEventItem> _cashFlowEvents = [];
    private readonly ObservableCollection<SubscriptionItem> _subscriptions = [];

    private readonly List<Transaction> _monzoTransactions = [];
    private readonly List<Transaction> _activityTransactions = [];
    private readonly List<Transaction> _rawTransactions = [];
    private readonly List<Budget> _rawBudgets = [];
    private decimal _currentBalance;

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
            await LoadDashboardAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Error("Failed to save access token ({ErrorType}).", exception.GetType().Name);
            SetStatus("Could not save access token. Check value and retry.", InfoBarSeverity.Error);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
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
            await LoadDashboardAsync().ConfigureAwait(true);
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

        if (!decimal.TryParse(BudgetLimitTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal limit) || limit <= 0)
        {
            SetStatus("Budget limit must be a positive number.", InfoBarSeverity.Warning);
            return;
        }

        var budget = new Budget
        {
            Id = category,
            Category = category,
            Limit = limit,
            StartDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
            EndDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1),
        };

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

    private async Task LoadDashboardAsync()
    {
        Account? account = await _monzoService.GetAccountAsync().ConfigureAwait(true);
        if (account is null)
        {
            SetStatus("Could not fetch account details from Monzo.", InfoBarSeverity.Error);
            return;
        }

        IReadOnlyList<Transaction> transactions = await _monzoService.GetTransactionsAsync(200).ConfigureAwait(true);

        _monzoTransactions.Clear();
        _monzoTransactions.AddRange(transactions);

        AccountIdTextBlock.Text = $"Account {account.Id}";
        BalanceTextBlock.Text = FormatCurrency(account.Balance);
        CurrencyTextBlock.Text = account.Currency;
        TotalCashTextBlock.Text = Money(account.Balance);
        _currentBalance = account.Balance;
        _connectedAccountId = account.Id;
        _hasMonzoBalance = true;
        _lastMonzoSync = DateTime.Now;
        ConnectedAccount.Visibility = Visibility.Visible;
        ConnectionLabel.Text = "Monzo connected";
        ConnectionDot.Fill = Brush("#6CE4BD");

        RefreshTransactionViews();
        RefreshNetWorthOverview();

        SetStatus($"Loaded {transactions.Count} transactions.", InfoBarSeverity.Success);
    }

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
            .Where(transaction => SpendEffect(transaction) != 0 && !string.IsNullOrWhiteSpace(transaction.Category))
            .GroupBy(transaction => transaction.Category!, StringComparer.OrdinalIgnoreCase)
            .Select(
                group => new
                {
                    Category = group.Key,
                    Spent = group.Sum(SpendEffect),
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

    private void ApplyBudgetSpend()
    {
        foreach (Budget budget in _rawBudgets)
        {
            budget.Spent = Math.Max(0, _rawTransactions
                .Where(transaction =>
                    string.Equals(transaction.Category, budget.Category, StringComparison.OrdinalIgnoreCase)
                    && transaction.Created.ToLocalTime().Date >= budget.StartDate.Date
                    && transaction.Created.ToLocalTime().Date <= budget.EndDate.Date)
                .Sum(SpendEffect));
        }

        RebuildBudgetRows();
    }

    private void RebuildBudgetRows()
    {
        _budgetRows.Clear();
        _budgetAlerts.Clear();

        foreach (Budget budget in _rawBudgets)
        {
            decimal progress = budget.Limit > 0 ? Math.Min(budget.Spent / budget.Limit, 1.0m) : 0;
            bool isOverBudget = budget.Spent > budget.Limit && budget.Limit > 0;

            if (isOverBudget)
            {
                decimal overBy = budget.Spent - budget.Limit;
                _budgetAlerts.Add($"⚠ {budget.Category}: over budget by {FormatCurrency(overBy)}");
            }

            _budgetRows.Add(
                new BudgetRowItem(
                    budget.Id,
                    budget.Category,
                    $"Limit: {FormatCurrency(budget.Limit)}",
                    $"Spent: {FormatCurrency(budget.Spent)}",
                    (double)progress,
                    isOverBudget ? "Over budget" : $"{progress:P0}",
                    $"{budget.StartDate:yyyy-MM-dd} → {budget.EndDate:yyyy-MM-dd}"));
        }

        if (_budgetAlerts.Count == 0)
        {
            _budgetAlerts.Add("No overspend alerts. You're on track.");
        }

        decimal totalBudget = _rawBudgets.Sum(budget => budget.Limit);
        decimal totalSpent = _rawBudgets.Sum(budget => budget.Spent);
        BudgetUtilisationTextBlock.Text = totalBudget > 0
            ? $"{(totalSpent / totalBudget) * 100:F0}% of total budget used"
            : "No budgets yet";
    }

    private void RebuildCategoryFilterOptions()
    {
        string currentSelection = CategoryFilterComboBox.SelectedItem?.ToString() ?? "All categories";
        List<string> categories =
        [
            "All categories",
            .. _activityTransactions
                .Select(transaction => transaction.Category ?? "uncategorized")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase),
        ];

        CategoryFilterComboBox.ItemsSource = categories;
        CategoryFilterComboBox.SelectedItem = categories.Contains(currentSelection, StringComparer.OrdinalIgnoreCase)
            ? categories.First(category => string.Equals(category, currentSelection, StringComparison.OrdinalIgnoreCase))
            : "All categories";
    }

    private void ApplyTransactionFilters()
    {
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

        DateTime utcNow = DateTime.UtcNow;
        if (string.Equals(dateFilter, "Last 7 days", StringComparison.Ordinal))
        {
            filtered = filtered.Where(transaction => transaction.Created >= utcNow.AddDays(-7));
        }
        else if (string.Equals(dateFilter, "Last 30 days", StringComparison.Ordinal))
        {
            filtered = filtered.Where(transaction => transaction.Created >= utcNow.AddDays(-30));
        }
        else if (string.Equals(dateFilter, "This month", StringComparison.Ordinal))
        {
            DateTime monthStart = new(utcNow.Year, utcNow.Month, 1);
            filtered = filtered.Where(transaction => transaction.Created >= monthStart);
        }

        filtered = sortFilter switch
        {
            "Oldest first" => filtered.OrderBy(transaction => transaction.Created),
            "Largest spend" => filtered.OrderBy(transaction => transaction.Amount),
            "Largest income" => filtered.OrderByDescending(transaction => transaction.Amount),
            _ => filtered.OrderByDescending(transaction => transaction.Created),
        };

        _transactions.Clear();
        foreach (Transaction transaction in filtered)
        {
            _transactions.Add(
                new TransactionRowItem(
                    transaction.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    transaction.Description ?? "(no description)",
                    transaction.Category ?? "uncategorized",
                    transaction.Merchant ?? "-",
                    $"{transaction.Created.ToLocalTime():yyyy-MM-dd HH:mm} · {(RuleFor(transaction)?.Treatment is { } label && label != FinancePreferences.Normal ? label : transaction.Merchant ?? "—")}",
                    FormatCurrency(transaction.Amount)));
        }
        ActivityEmptyText.Visibility = _transactions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityEmptyText.Text = !_hasMonzoBalance
            ? "Connect Monzo in Settings to see your transactions."
            : _activityTransactions.Count == 0 ? "No transactions returned from Monzo." : "No transactions match these filters.";
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
        if (!_workspaceReady || !_hasMonzoBalance)
        {
            return;
        }

        int horizonDays = ForecastHorizonComboBox.SelectedIndex switch
        {
            1 => 60,
            2 => 90,
            _ => 30,
        };

        var recurringPatterns = DetectRecurringPatterns(_rawTransactions.Where(transaction => !IsRefund(transaction)).ToArray());
        DateTime thirtyDaysAgo = DateTime.Today.AddDays(-30);
        HashSet<string> recurringNames = recurringPatterns.Where(pattern => pattern.Amount < 0)
            .Select(pattern => pattern.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal recentDailySpend = _rawTransactions.Where(transaction => transaction.Created.ToLocalTime().Date >= thirtyDaysAgo
                && !recurringNames.Contains(TransactionParty(transaction)))
            .Sum(SpendEffect) / 30;
        List<ForecastEvent> events = BuildForecastEvents(recurringPatterns, horizonDays, Math.Max(0, recentDailySpend));

        decimal runningBalance = _currentBalance;
        DateTime startDate = DateTime.Today;
        DateTime endDate = startDate.AddDays(horizonDays);

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
            CashFlowPolyline.Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 96, 165, 250));
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
