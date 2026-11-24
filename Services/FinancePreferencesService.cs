// -----------------------------------------------------------------------
// <copyright file="FinancePreferencesService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MonzoManager.Services;

internal sealed record SavingsGoal(string Id, string Name, decimal Target, string AccountId);
internal sealed record TransactionRule(string Treatment, string? Category);
internal sealed record FinancePreferences
{
    internal const string Normal = "Normal";
    internal const string Transfer = "Own transfer";
    internal const string Refund = "Refund";

    public int ChartRange { get; init; }
    public bool AccessibleOnly { get; init; }
    public IReadOnlyList<SavingsGoal> Goals { get; init; } = [];
    public Dictionary<string, TransactionRule> TransactionRules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TransactionRule> MerchantRules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class FinancePreferencesService
{
    private static readonly string[] BackupNames =
    [
        "manual-finance.json", "monthly-net-worth.json", "budgets.json", "finance-preferences.json", "networth-history.json", LedgerService.FileName,
    ];

    private static readonly string PreferencesPath = Path.Combine(LocalFiles.DataDirectory, "finance-preferences.json");
    private static readonly string RestoreJournal = Path.Combine(LocalFiles.DataDirectory, "restore-pending.dat");
    private const int MaxBackupLength = 32 * 1024 * 1024;

    internal async Task<FinancePreferences> LoadAsync() => File.Exists(PreferencesPath)
        ? JsonConvert.DeserializeObject<FinancePreferences>(await LocalFiles.ReadFinanceAsync(PreferencesPath))
            ?? throw new InvalidDataException("Saved finance preferences are invalid.")
        : new FinancePreferences();

    internal Task SaveAsync(FinancePreferences preferences) =>
        LocalFiles.WriteFinanceAsync(PreferencesPath, JsonConvert.SerializeObject(preferences, Formatting.Indented));

    internal async Task<byte[]> CreateBackupAsync(string password)
    {
        Dictionary<string, string?> files = await ReadBackedUpFilesAsync();
        string json = JsonConvert.SerializeObject(files);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBackupLength - 52)
        {
            throw new InvalidDataException("The workspace exceeds the supported backup size.");
        }

        return await Task.Run(() => LocalFiles.EncryptBackup(json, password));
    }

    internal async Task<Dictionary<string, string?>> ReadBackupAsync(Stream stream, string password)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > MaxBackupLength)
            {
                throw new InvalidDataException("The backup exceeds the supported size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count));
        }

        string json = await Task.Run(() => LocalFiles.DecryptBackup(buffer.ToArray(), password));
        var files = JsonConvert.DeserializeObject<Dictionary<string, string?>>(json);
        if (files is null || !ValidWorkspaceNames(files))
        {
            throw new InvalidDataException("This backup does not contain a complete finance workspace.");
        }

        foreach ((string name, string? value) in files)
        {
            if (value is null)
            {
                continue;
            }

            switch (name)
            {
                case "manual-finance.json":
                    if (JsonConvert.DeserializeObject<ManualFinanceEntry[]>(value) is not { } entries
                        || entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id)
                            || string.IsNullOrWhiteSpace(entry.Name) || entry.OwnershipPercent is < 0 or > 100
                            || Math.Abs(entry.Amount) > 1_000_000_000_000m)
                        || entries.Select(entry => entry.Id).Distinct().Count() != entries.Length)
                    {
                        throw new InvalidDataException("The backup has invalid balance records.");
                    }
                    break;
                case "monthly-net-worth.json":
                    if (JsonConvert.DeserializeObject<MonthlySnapshot[]>(value) is not { } history
                        || history.Any(month => month is null || month.Month.Day != 1 || month.Balances is null
                            || month.Balances.Any(balance => balance is null || string.IsNullOrWhiteSpace(balance.Name)))
                        || history.Select(month => month.Month).Distinct().Count() != history.Length)
                    {
                        throw new InvalidDataException("The backup has invalid monthly snapshots.");
                    }
                    break;
                case "budgets.json":
                    _ = JsonConvert.DeserializeObject<MonzoManager.Models.Budget[]>(value);
                    break;
                case "networth-history.json":
                    _ = JsonConvert.DeserializeObject<NetWorthSnapshot[]>(value);
                    break;
                case LedgerService.FileName:
                    if (JsonConvert.DeserializeObject<LedgerState>(value) is not
                        { Accounts: not null, Transactions: not null, Pots: not null, Coverage: not null, Planned: not null, Annotations: not null } ledger
                        || ledger.Transactions.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id))
                        || ledger.Transactions.Select(row => row.Id).Distinct().Count() != ledger.Transactions.Count
                        || ledger.Accounts.Any(account => account is null || string.IsNullOrWhiteSpace(account.Id) || account.Ownership is < 0 or > 100)
                        || ledger.Accounts.Select(account => account.Id).Distinct().Count() != ledger.Accounts.Count
                        || ledger.Annotations.Values.Any(annotation => annotation is null || annotation.Splits is null))
                    {
                        throw new InvalidDataException("The backup has an invalid transaction ledger.");
                    }
                    break;
                case "finance-preferences.json":
                    if (JsonConvert.DeserializeObject<FinancePreferences>(value) is not { Goals: not null, TransactionRules: not null, MerchantRules: not null })
                    {
                        throw new InvalidDataException("The backup has invalid workspace preferences.");
                    }
                    break;
            }
        }

        return files;
    }

    internal async Task RecoverRestoreAsync()
    {
        if (!File.Exists(RestoreJournal))
        {
            return;
        }

        string json = await LocalFiles.ReadFinanceAsync(RestoreJournal);
        var original = JsonConvert.DeserializeObject<Dictionary<string, string?>>(json);
        if (original is null || !ValidWorkspaceNames(original))
        {
            throw new InvalidDataException("The restore recovery file is invalid.");
        }

        await ApplyFilesAsync(original);
        File.Delete(RestoreJournal);
    }

    internal async Task RestoreAsync(IReadOnlyDictionary<string, string?> files)
    {
        await RecoverRestoreAsync();
        Dictionary<string, string?> original = await ReadBackedUpFilesAsync();
        await LocalFiles.WriteFinanceAsync(RestoreJournal, JsonConvert.SerializeObject(original));
        try
        {
            await ApplyFilesAsync(files);
            File.Delete(RestoreJournal);
        }
        catch
        {
            await RecoverRestoreAsync();
            throw;
        }
    }

    private static bool ValidWorkspaceNames(IReadOnlyDictionary<string, string?> files) =>
        files.Keys.All(name => BackupNames.Contains(name, StringComparer.Ordinal))
        && BackupNames.Where(name => name != LedgerService.FileName).All(files.ContainsKey);

    private static async Task ApplyFilesAsync(IReadOnlyDictionary<string, string?> files)
    {
        foreach (string name in BackupNames)
        {
            if (files.TryGetValue(name, out string? content))
            {
                await ApplyFileAsync(name, content);
            }
        }
    }

    private static async Task<Dictionary<string, string?>> ReadBackedUpFilesAsync()
    {
        var files = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string name in BackupNames)
        {
            string path = Path.Combine(LocalFiles.DataDirectory, name);
            files[name] = File.Exists(path) ? await LocalFiles.ReadFinanceAsync(path) : null;
        }

        return files;
    }

    private static async Task ApplyFileAsync(string name, string? content)
    {
        string path = Path.Combine(LocalFiles.DataDirectory, name);
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            await LocalFiles.WriteFinanceAsync(path, content);
        }
    }
}
