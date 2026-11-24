# Parc Spend - Setup Status

## Completed

- Migrated to **WinUI 3 on .NET 10** with a premium-styled shell.
- Implemented Monzo OAuth login and token refresh.
- Added encrypted token persistence using DPAPI.
- Implemented account + balance retrieval and transaction loading.
- Added dashboard analytics:
  - Total spent
  - Top category
  - Top five category totals
- Added budget CRUD with local persistence.
- Added installer support artifacts:
  - `scripts/publish.ps1` (self-contained app publish)
  - `installer/MonzoManager.iss` (Inno Setup installer)
- Added coding standards baseline via `.editorconfig`.
- Added XML documentation and file headers for public API surfaces.

## Remaining Enhancements

- Add richer charts and trend visualizations.
- Add budget notifications/alerts.
- Add automated tests around service and auth flows.
- Add code signing for installer artifacts.
