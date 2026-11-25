// -----------------------------------------------------------------------
// <copyright file="BudgetService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonzoManager.Models;
using Newtonsoft.Json;
using Serilog;

namespace MonzoManager.Services;

/// <summary>
/// Provides local budget CRUD persistence.
/// </summary>
public interface IBudgetService
{
    /// <summary>
    /// Gets all budgets from local storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All persisted budgets.</returns>
    Task<IReadOnlyList<Budget>> GetBudgetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a budget.
    /// </summary>
    /// <param name="budget">Budget record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveBudgetAsync(Budget budget, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a budget by identifier.
    /// </summary>
    /// <param name="budgetId">Budget identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteBudgetAsync(string budgetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-file implementation of <see cref="IBudgetService"/>.
/// </summary>
public sealed class BudgetService : IBudgetService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetService"/> class.
    /// </summary>
    public BudgetService()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonzoManager");
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "budgets.json");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Budget>> GetBudgetsAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<Budget>();
            }

            string json = await LocalFiles.ReadFinanceAsync(_filePath, cancellationToken).ConfigureAwait(false);
            List<Budget>? budgets = JsonConvert.DeserializeObject<List<Budget>>(json);
            return budgets ?? new List<Budget>();
        }
        catch (Exception exception)
        {
            Log.Error("Failed to read saved budgets ({ErrorType}).", exception.GetType().Name);
            return Array.Empty<Budget>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveBudgetAsync(Budget budget, CancellationToken cancellationToken = default)
    {
        if (budget is null)
        {
            throw new ArgumentNullException(nameof(budget));
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<Budget> budgets = (await LoadBudgetsInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int index = budgets.FindIndex(existing => string.Equals(existing.Id, budget.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                budgets[index] = budget;
            }
            else
            {
                budgets.Add(budget);
            }

            await SaveBudgetsInternalAsync(budgets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteBudgetAsync(string budgetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(budgetId))
        {
            throw new ArgumentException("Budget id cannot be empty.", nameof(budgetId));
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<Budget> budgets = (await LoadBudgetsInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            budgets.RemoveAll(budget => string.Equals(budget.Id, budgetId, StringComparison.Ordinal));
            await SaveBudgetsInternalAsync(budgets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<Budget>> LoadBudgetsInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<Budget>();
        }

        string json = await LocalFiles.ReadFinanceAsync(_filePath, cancellationToken).ConfigureAwait(false);
        List<Budget>? budgets = JsonConvert.DeserializeObject<List<Budget>>(json);
        return budgets ?? new List<Budget>();
    }

    private async Task SaveBudgetsInternalAsync(IReadOnlyList<Budget> budgets, CancellationToken cancellationToken)
    {
        string json = JsonConvert.SerializeObject(budgets, Formatting.Indented);
        await LocalFiles.WriteFinanceAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
