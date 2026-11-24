// -----------------------------------------------------------------------
// <copyright file="ManualFinanceService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MonzoManager.Services;

/// <summary>
/// Represents a manually maintained asset or liability.
/// </summary>
public sealed class ManualFinanceEntry
{
    /// <summary>
    /// Gets or sets the entry identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the entry name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entry type: asset or liability.
    /// </summary>
    public string Type { get; set; } = "asset";

    /// <summary>
    /// Gets or sets the entry value in major units.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Gets or sets the financial account classification.
    /// </summary>
    public string Kind { get; set; } = "Other";

    /// <summary>
    /// Gets or sets the share of the balance included in personal net worth.
    /// </summary>
    public decimal OwnershipPercent { get; set; } = 100;

    /// <summary>
    /// Gets or sets the time the full account balance was last confirmed.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

}

/// <summary>
/// Provides local persistence for manual assets and liabilities.
/// </summary>
public interface IManualFinanceService
{
    /// <summary>
    /// Gets all saved manual finance entries.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Manual finance entries.</returns>
    Task<IReadOnlyList<ManualFinanceEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a manual finance entry.
    /// </summary>
    /// <param name="entry">Entry to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveEntryAsync(ManualFinanceEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a manual finance entry.
    /// </summary>
    /// <param name="entryId">Entry identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteEntryAsync(string entryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-file implementation of <see cref="IManualFinanceService"/>.
/// </summary>
public sealed class ManualFinanceService : IManualFinanceService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualFinanceService"/> class.
    /// </summary>
    public ManualFinanceService()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonzoManager");
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "manual-finance.json");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManualFinanceEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveEntryAsync(ManualFinanceEntry entry, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ManualFinanceEntry> entries = (await LoadInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int existingIndex = entries.FindIndex(existing => string.Equals(existing.Id, entry.Id, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                entries[existingIndex] = entry;
            }
            else
            {
                entries.Add(entry);
            }

            await SaveInternalAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteEntryAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ManualFinanceEntry> entries = (await LoadInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.RemoveAll(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal));
            await SaveInternalAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    internal async Task ReplaceAllAsync(IReadOnlyList<ManualFinanceEntry> entries)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(entries, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<ManualFinanceEntry>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<ManualFinanceEntry>();
        }

        string json = await LocalFiles.ReadFinanceAsync(_filePath, cancellationToken).ConfigureAwait(false);
        List<ManualFinanceEntry>? entries = JsonConvert.DeserializeObject<List<ManualFinanceEntry>>(json);
        return entries ?? new List<ManualFinanceEntry>();
    }

    private async Task SaveInternalAsync(IReadOnlyList<ManualFinanceEntry> entries, CancellationToken cancellationToken)
    {
        string json = JsonConvert.SerializeObject(entries, Formatting.Indented);
        await LocalFiles.WriteFinanceAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
