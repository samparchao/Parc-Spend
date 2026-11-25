# Parc Spend usage guide

Parc Spend is a Windows workspace for tracking personal net worth, with an optional Monzo connection for spending, budgets and cash-flow planning.

## Track and review balances

- Open **Net worth → Add balance** to track cash, savings, investments, pensions and debts. Enter the full account balance and its ownership percentage. The app includes your share in net worth. Select a balance to edit it.
- Open **Review this month** to update all your balances in one place, paste a single Excel column in the displayed row order, or sync Monzo. Enter signed net contributions for investment or pension accounts if you track them. Review the total and save it as a monthly snapshot.
- Select a saved month to see its accounts and the waterfall of changes from the previous recorded month. **Correct balances** updates a historical snapshot. **Undo** reverses the latest balance, review, history correction or CSV import in the current app session.
- **Overview** remembers your chart range and view. **Accessible savings** includes positive Monzo cash and tracked **Cash** and **Savings** accounts. Historical months appear in this view when they have an identified accessible balance.

### Import and export spreadsheet history

Export your workbook’s balance table as a comma-separated CSV and choose **Net worth → Import CSV**. Use one month per column and one balance per row.

```csv
Asset,Mar 2025,Apr 2025
Cash,500,650
Pension,12500,12800
Credit debt,-300,-200
Total,12700,13250
```

The app uses the balance rows when available. A **Total** or **Net Worth** row can represent a month on its own. Import previews which months will be replaced. The **History grid** scrolls through snapshots with the totals pinned above the account rows. Use **Export CSV** to save all months. CSV export includes values and leaves out account IDs, classifications, contributions and preferences. Use an encrypted backup for a full workspace copy.

## Monzo activity and planning

Open **Settings** and enter your **API credentials** or **Access token**. The redirect URI must match the one configured for your Monzo developer app. Use a local HTTP loopback URL such as `http://127.0.0.1:8000/auth/callback` or the existing `http://localhost:8000/auth/callback`.

In **Activity → Review & categorise**, select one or more transactions to flag as an own-account transfer or refund, apply a category, or remember that category for a merchant. Monzo top-ups default to transfers and can be overridden. Transfers remain visible in Activity and are excluded from income and spending. Refunds credit their selected spending category. Summaries cover the transactions the app has loaded from Monzo.

### Accounts and statement history

Use **Settings → Manage accounts** to choose Monzo personal and joint accounts. Ownership percentages apply to balances and Pots in net worth. Available Monzo cash excludes money in Pots. Parc Spend includes each synced Pot once and can link it to a savings goal.

**Backfill history** opens Monzo authentication when API credentials are available. Approve access in the Monzo app. Parc Spend pages through the available history and retains it for later use. Normal refreshes revisit the recent window to pick up settlement changes. Activity shows the coverage span and flags accounts with gaps in the selected period. Pending and declined payments remain visible in Activity. Spending and budgets use settled transactions.

Use **Activity → Import statement** to add a GBP CSV to a separate current, joint, or credit-card account. Accepts `Date,Description,Amount` or `Date,Description,Debit,Credit`, with optional `Transaction ID`, `Currency`, and `Category` columns. Use UK dates or ISO dates. Check the import preview and reverse the sign if your card export lists purchases as positive. Select an explicit statement period only when the CSV covers all activity for those dates. Re-imports use bank IDs where supplied and date, amount, and description where IDs are absent.

Select a transaction and choose **Edit selected** to confirm an opposite-amount transfer between accounts, split a purchase across categories, record an expected reimbursement, or link a refund. Both sides of a confirmed transfer are excluded from spending and income.

### Planning

Save a one-off budget or make it recur on a calendar or payday cycle. For payday days later than a short month allows, Parc Spend uses that month’s last day. Optional rollover carries forward surplus from complete preceding cycles.

Add a bill, renewal, or one-off purchase under **Planned expenses**. The forecast compares the same horizon with and without these expenses. Enter a current-account balance or sync Monzo to set the starting cash. Savings goals can use manual balances or individual Monzo Pots.

## Data and backups

Parc Spend uses the existing finance data and Monzo connection from Monzo Manager in `%APPDATA%\MonzoManager`. New saves use Windows DPAPI for the current Windows user. The app can read the previous JSON format and encrypts each legacy file when it is saved. Earlier builds expect plain JSON and cannot read those encrypted files.

**Settings → Create backup** makes a portable, password-protected workspace backup. It includes tracked balances, monthly history, the transaction ledger, planned expenses, budgets, goals, classification rules and display settings. Monzo credentials and tokens are managed separately on each Windows profile. **Restore backup** replaces the finance workspace after confirmation and recovers the previous data if a restore is interrupted.

## Build and run

Use Visual Studio with the .NET 10 SDK and the Windows SDK build tools installed. From a Visual Studio Developer PowerShell:

```powershell
msbuild .\MonzoManager.csproj -restore -p:Configuration=Debug -p:Platform=x64 -p:RestoreLockedMode=true
```

Run `MonzoManager.exe` from the build output under `bin`. Run `scripts\publish.ps1` to build the Windows executable and its runtime files into `artifacts\publish\win-x64`. Keep that folder together when distributing the app.

### Windows installer

Run `ParcSpend-Setup.exe` to install Parc Spend with bundled runtimes, a Start menu shortcut, and an optional desktop shortcut. The installer can install for your Windows user or for all users. An upgrade keeps the existing installation location and shortcuts. Windows **Installed apps** can remove the program. Your finance data and credentials live in the Windows profile and stay available after reinstalling.

To build the installer, install Inno Setup 6.6 or newer and run:

```powershell
.\scripts\build-installer.ps1
```

The script builds a fresh release payload and produces `artifacts\installer\ParcSpend-Setup.exe`. Pass `-CompilerPath` if ISCC is in a custom location.

C# source files follow the repository’s `.editorconfig`, including file headers and XML documentation for public APIs. The build treats missing or inconsistent public API XML documentation as an error.
