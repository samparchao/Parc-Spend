// -----------------------------------------------------------------------
// <copyright file="MonthlyHistoryService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;

namespace MonzoManager.Services;

internal sealed record MonthlyBalance(
    string Name, decimal Amount, bool IsDebt, bool IsMonzo = false,
    string? AccountId = null, string Kind = "Other", decimal? NetContributions = null);

internal sealed record MonthlySnapshot(DateOnly Month, decimal NetWorth, IReadOnlyList<MonthlyBalance> Balances)
{
    internal static MonthlySnapshot FromBalances(DateOnly month, IReadOnlyList<MonthlyBalance> balances) =>
        new(month, balances.Sum(balance => balance.Amount), balances);
}

internal sealed class MonthlyHistoryService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MonzoManager",
        "monthly-net-worth.json");

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    internal async Task<IReadOnlyList<MonthlySnapshot>> LoadAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            return await ReadAsync();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    internal async Task<IReadOnlyList<MonthlySnapshot>> SaveAsync(IEnumerable<MonthlySnapshot> snapshots)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var byMonth = (await ReadAsync().ConfigureAwait(false)).ToDictionary(snapshot => snapshot.Month);
            foreach (MonthlySnapshot snapshot in snapshots)
            {
                byMonth[snapshot.Month] = snapshot;
            }

            return await WriteAsync(byMonth.Values).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    internal async Task<IReadOnlyList<MonthlySnapshot>> ReplaceAllAsync(IEnumerable<MonthlySnapshot> snapshots)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await WriteAsync(snapshots).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<MonthlySnapshot>> WriteAsync(IEnumerable<MonthlySnapshot> snapshots)
    {
        MonthlySnapshot[] ordered = snapshots.OrderBy(snapshot => snapshot.Month).ToArray();
        await LocalFiles.WriteFinanceAsync(_filePath, JsonConvert.SerializeObject(ordered, Formatting.Indented)).ConfigureAwait(false);
        return ordered;
    }

    private async Task<IReadOnlyList<MonthlySnapshot>> ReadAsync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        string json = await LocalFiles.ReadFinanceAsync(_filePath);
        return JsonConvert.DeserializeObject<MonthlySnapshot[]>(json)
            ?? throw new InvalidDataException("The saved monthly history could not be read.");
    }

    internal static IReadOnlyList<MonthlySnapshot> ReadSpreadsheet(Stream input)
    {
        using var csv = new TextFieldParser(input) { HasFieldsEnclosedInQuotes = true };
        csv.SetDelimiters(",");

        string[] header = csv.ReadFields() ?? [];
        int lastColumn = Array.FindLastIndex(header, value => !string.IsNullOrWhiteSpace(value));
        if (lastColumn is < 1 or > 360)
        {
            throw new InvalidDataException("Use one column per month, with month names in the first row (for example, Apr 2025).");
        }

        DateOnly[] months = header.Skip(1).Take(lastColumn).Select(ParseMonth).ToArray();
        if (months.Distinct().Count() != months.Length)
        {
            throw new InvalidDataException("Each month must appear in only one column.");
        }

        var balances = months.Select(_ => new List<MonthlyBalance>()).ToArray();
        var reportedTotals = new decimal?[months.Length];

        int rowCount = 0;
        while (!csv.EndOfData)
        {
            if (++rowCount > 500)
            {
                throw new InvalidDataException("This CSV exceeds the 500-row import limit.");
            }

            string[] fields = csv.ReadFields() ?? [];
            if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            string name = fields[0].Trim();
            if (name.Equals("Net Change", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Monthly Change", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isTotal = name.Equals("Total", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Net Worth", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Total Net Worth", StringComparison.OrdinalIgnoreCase);

            bool isDebt = name.Contains("debt", StringComparison.OrdinalIgnoreCase)
                || name.Contains("liabilit", StringComparison.OrdinalIgnoreCase)
                || name.Contains("mortgage", StringComparison.OrdinalIgnoreCase)
                || name.Contains("credit card", StringComparison.OrdinalIgnoreCase)
                || name.Contains("loan", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < months.Length && i + 1 < fields.Length; i++)
            {
                string field = fields[i + 1].Trim();
                if (field.Length == 0)
                {
                    continue;
                }

                if (!decimal.TryParse(field, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-GB"), out decimal amount) || Math.Abs(amount) > 1_000_000_000_000m)
                {
                    throw new InvalidDataException($"Invalid balance for {name} in {months[i]:MMM yyyy}.");
                }

                if (isTotal)
                {
                    reportedTotals[i] = amount;
                }
                else
                {
                    balances[i].Add(new MonthlyBalance(name, isDebt ? -Math.Abs(amount) : amount, isDebt || amount < 0));
                }
            }
        }

        var snapshots = new List<MonthlySnapshot>();
        for (int i = 0; i < months.Length; i++)
        {
            if (balances[i].Count > 0)
            {
                snapshots.Add(MonthlySnapshot.FromBalances(months[i], balances[i]));
            }
            else if (reportedTotals[i] is decimal total)
            {
                snapshots.Add(new MonthlySnapshot(months[i], total, []));
            }
        }

        if (snapshots.Count == 0)
        {
            throw new InvalidDataException("The CSV did not contain any monthly balances or totals.");
        }

        return snapshots;
    }

    private static DateOnly ParseMonth(string value)
    {
        string[] formats = ["MMM yyyy", "MMMM yyyy", "MMM-yy", "MMM-yyyy", "yyyy-MM", "yyyy-MM-dd", "d/M/yyyy"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.GetCultureInfo("en-GB"),
            DateTimeStyles.AllowWhiteSpaces, out DateTime date))
        {
            return new DateOnly(date.Year, date.Month, 1);
        }

        throw new InvalidDataException($"“{value}” is not a recognised month. Use a heading like Apr 2025.");
    }
}
