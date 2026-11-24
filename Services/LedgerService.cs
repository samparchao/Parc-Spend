// -----------------------------------------------------------------------
// <copyright file="LedgerService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using MonzoManager.Models;
using Newtonsoft.Json;

namespace MonzoManager.Services;

internal sealed record LedgerAccount(string Id, string Name, string Kind, bool Enabled, decimal Ownership,
    DateTime? OpenedUtc = null, decimal? Balance = null, DateTimeOffset? SyncedAt = null)
{
    internal bool IsMonzo => Kind is "Monzo personal" or "Monzo joint";
}

internal sealed record LedgerPot(string Id, string AccountId, string Name, decimal Balance);
internal sealed record LedgerCoverage(string AccountId, DateTime FromUtc, DateTime UntilUtc);
internal sealed record LedgerSplit(string Category, decimal Amount);
internal sealed record LedgerAnnotation(IReadOnlyList<LedgerSplit> Splits, decimal ExpectedReimbursement,
    string? ReimbursedBy = null, string? OffsetOf = null, string? TransferWith = null, string? LinkedRefund = null, string? ReimbursementFrom = null);
internal sealed record PlannedExpense(string Id, string Name, decimal Amount, DateOnly Date, bool RepeatsMonthly);

internal sealed record LedgerState
{
    public IReadOnlyList<LedgerAccount> Accounts { get; init; } = [];
    public IReadOnlyList<LedgerPot> Pots { get; init; } = [];
    public IReadOnlyList<Transaction> Transactions { get; init; } = [];
    public IReadOnlyList<LedgerCoverage> Coverage { get; init; } = [];
    public IReadOnlyList<PlannedExpense> Planned { get; init; } = [];
    public Dictionary<string, LedgerAnnotation> Annotations { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class LedgerService
{
    internal const string FileName = "transaction-ledger.json";
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private readonly string _path = Path.Combine(LocalFiles.DataDirectory, FileName);
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal async Task<LedgerState> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<LedgerState> UpdateAsync(Func<LedgerState, LedgerState> change)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            LedgerState next = change(await ReadAsync().ConfigureAwait(false));
            await LocalFiles.WriteFinanceAsync(_path, JsonConvert.SerializeObject(next)).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<LedgerState> ReadAsync() => File.Exists(_path)
        ? JsonConvert.DeserializeObject<LedgerState>(await LocalFiles.ReadFinanceAsync(_path).ConfigureAwait(false))
            ?? throw new InvalidDataException("The transaction ledger could not be read.")
        : new LedgerState();

    internal static LedgerState Merge(LedgerState state, string accountId, IReadOnlyList<Transaction> transactions,
        LedgerCoverage? coverage)
    {
        var byId = state.Transactions.ToDictionary(transaction => transaction.Id, StringComparer.Ordinal);
        foreach (Transaction transaction in transactions)
        {
            // Retrying recent pages must refresh pending authorisations with the settled version.
            if (!byId.TryGetValue(transaction.Id, out Transaction? previous) || previous.IsPending || !transaction.IsPending)
            {
                byId[transaction.Id] = transaction;
            }
        }

        if (coverage is not null)
        {
            var present = transactions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string expired in state.Transactions.Where(item => item.AccountId == accountId && item.IsPending
                && item.Created >= coverage.FromUtc && item.Created < coverage.UntilUtc
                && item.Created < coverage.UntilUtc.AddDays(-7) && !present.Contains(item.Id)).Select(item => item.Id))
            {
                byId.Remove(expired);
            }
        }

        IReadOnlyList<LedgerCoverage> ranges = state.Coverage;
        if (coverage is not null)
        {
            var combined = new List<LedgerCoverage>();
            foreach (LedgerCoverage range in state.Coverage.Where(item => item.AccountId == accountId)
                .Append(coverage).OrderBy(item => item.FromUtc))
            {
                if (combined.Count > 0 && combined[^1].UntilUtc >= range.FromUtc)
                {
                    if (range.UntilUtc > combined[^1].UntilUtc)
                    {
                        combined[^1] = combined[^1] with { UntilUtc = range.UntilUtc };
                    }
                }
                else
                {
                    combined.Add(range);
                }
            }

            ranges = state.Coverage.Where(item => item.AccountId != accountId).Concat(combined).ToArray();
        }

        return state with
        {
            Transactions = byId.Values.OrderByDescending(transaction => transaction.Created).ToArray(),
            Coverage = ranges,
        };
    }

    internal static bool Covers(LedgerState state, LedgerAccount account, DateTime fromUtc, DateTime untilUtc)
    {
        DateTime start = account.OpenedUtc is { } opened && opened > fromUtc ? opened : fromUtc;
        return start >= untilUtc || state.Coverage.Any(range =>
            range.AccountId == account.Id && range.FromUtc <= start && range.UntilUtc >= untilUtc);
    }

    internal static IReadOnlyList<Transaction> ReadStatement(Stream input, string accountId, bool reverseSigns = false)
    {
        using var csv = new TextFieldParser(input) { HasFieldsEnclosedInQuotes = true };
        csv.SetDelimiters(",");
        string[] header = csv.ReadFields() ?? [];
        string[] fields = header.Select(value => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant()).ToArray();
        int date = Column("date", "transactiondate", "bookingdate", "bookedat");
        int description = Column("description", "name", "payee", "merchant", "transactiondescription", "details");
        int amount = Column("amount", "netamount", "amountgbp");
        int debit = Column("debit", "moneyout", "paidout", "debitamount");
        int credit = Column("credit", "moneyin", "paidin", "creditamount");
        int currency = Column("currency", "currencycode");
        int category = Column("category");
        int id = Column("transactionid", "id");
        if (date < 0 || description < 0 || (amount < 0 && (debit < 0 || credit < 0)))
        {
            throw new InvalidDataException("Use a CSV with Date, Description and Amount columns, or separate Debit and Credit columns.");
        }

        var rows = new List<Transaction>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        while (!csv.EndOfData)
        {
            long line = csv.LineNumber;
            string[] values = csv.ReadFields() ?? [];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (rows.Count >= 100_000)
            {
                throw new InvalidDataException("Import at most 100,000 statement rows at a time.");
            }

            string name = Value(description);
            string code = Value(currency);
            decimal signedAmount;
            if (amount >= 0)
            {
                signedAmount = ReadMoney(Value(amount), line);
            }
            else
            {
                decimal incoming = Value(credit).Length == 0 ? 0 : ReadMoney(Value(credit), line);
                decimal outgoing = Value(debit).Length == 0 ? 0 : ReadMoney(Value(debit), line);
                if (incoming < 0 || (incoming != 0 && outgoing != 0))
                {
                    throw new InvalidDataException($"Check the debit and credit fields on CSV line {line}.");
                }

                signedAmount = incoming - Math.Abs(outgoing);
            }

            if (reverseSigns)
            {
                signedAmount = -signedAmount;
            }

            if (!DateTime.TryParseExact(Value(date), ["d/M/yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd MMM yyyy"],
                Uk, DateTimeStyles.None, out DateTime day) || name.Length == 0
                || (code.Length != 0 && !code.Equals("GBP", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Check the date, description and GBP currency on CSV line {line}.");
            }

            string bankId = Value(id);
            string fingerprint = bankId.Length != 0
                ? $"id|{bankId}"
                : $"row|{day:yyyy-MM-dd}|{signedAmount.ToString(CultureInfo.InvariantCulture)}|{name.ToUpperInvariant()}";
            int ordinal = bankId.Length == 0 ? occurrences.GetValueOrDefault(fingerprint) + 1 : 0;
            occurrences[fingerprint] = ordinal;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fingerprint}|{ordinal}")));
            rows.Add(new Transaction
            {
                AccountId = accountId, Id = $"{accountId}:{key}", Created = day.AddHours(12).ToUniversalTime(),
                Amount = signedAmount, Currency = "GBP", Description = name,
                Category = Value(category) is { Length: > 0 } label ? label : null,
            });

            string Value(int column) => column >= 0 && column < values.Length ? values[column].Trim() : "";
        }

        return rows.DistinctBy(row => row.Id).ToArray();

        int Column(params string[] names) => Array.FindIndex(fields, field => names.Contains(field, StringComparer.Ordinal));
    }

    private static decimal ReadMoney(string value, long line)
    {
        if (!decimal.TryParse(value, NumberStyles.Currency, Uk, out decimal amount)
            || amount is < -1_000_000_000_000m or > 1_000_000_000_000m || amount != decimal.Round(amount, 2))
        {
            throw new InvalidDataException($"Check the transaction amount on CSV line {line}.");
        }

        return amount;
    }

    internal static bool IsTransferCandidate(Transaction first, Transaction second) =>
        first.AccountId != second.AccountId && first.Currency == second.Currency && first.Amount != 0
        && first.Amount == -second.Amount && !first.IsDeclined && !second.IsDeclined
        && !first.IsPending && !second.IsPending
        && Math.Abs((first.Created.ToLocalTime().Date - second.Created.ToLocalTime().Date).TotalDays) <= 3;

    internal static DateOnly CycleStart(DateOnly day, int dayOfMonth)
    {
        DateOnly start = InMonth(day, dayOfMonth);
        return day >= start ? start : InMonth(day.AddMonths(-1), dayOfMonth);
    }

    internal static DateOnly InMonth(DateOnly day, int dayOfMonth) => new(day.Year, day.Month,
        Math.Clamp(dayOfMonth, 1, DateTime.DaysInMonth(day.Year, day.Month)));

    internal static IEnumerable<(PlannedExpense Expense, DateOnly Date)> Occurrences(
        IEnumerable<PlannedExpense> expenses, DateOnly from, DateOnly until)
    {
        foreach (PlannedExpense expense in expenses)
        {
            if (!expense.RepeatsMonthly)
            {
                if (expense.Date >= from && expense.Date <= until)
                {
                    yield return (expense, expense.Date);
                }

                continue;
            }

            DateOnly month = new(from.Year, from.Month, 1);
            for (; month <= until; month = month.AddMonths(1))
            {
                DateOnly occurrence = InMonth(month, expense.Date.Day);
                if (occurrence >= expense.Date && occurrence >= from && occurrence <= until)
                {
                    yield return (expense, occurrence);
                }
            }
        }
    }
}

