# Parc Spend

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

**Planning** includes savings goals funded by linked tracked balances. The forecast uses the latest Monzo balance and recent transaction patterns. It shows estimated end-of-day balances on recurring payment dates and weeks forecast to fall below zero.

## Data and backups

Parc Spend uses the existing finance data and Monzo connection from Monzo Manager in `%APPDATA%\MonzoManager`. New saves use Windows DPAPI for the current Windows user. The app can read the previous JSON format and encrypts each legacy file when it is saved. Earlier builds expect plain JSON and cannot read those encrypted files.

**Settings → Create backup** makes a portable, password-protected workspace backup. It includes tracked balances, monthly history, budgets, goals, classification rules and display settings. Monzo credentials and tokens are managed separately on each Windows profile. **Restore backup** replaces the finance workspace after confirmation and recovers the previous data if a restore is interrupted.

## Build and run

Use Visual Studio with the .NET 10 SDK and the Windows SDK build tools installed. From a Visual Studio Developer PowerShell:

```powershell
msbuild .\MonzoManager.csproj -restore -p:Configuration=Debug
```

Run `MonzoManager.exe` from the build output directory under `bin\Debug`. Run `scripts\publish.ps1` to build the Windows executable and its runtime files into `artifacts\publish\win-x64`. Keep that folder together when distributing the app. To build an installer, open `installer\MonzoManager.iss` in Inno Setup after publishing.

C# source files follow the repository’s `.editorconfig`, including file headers and XML documentation for public APIs. The build treats missing or inconsistent public API XML documentation as an error.
