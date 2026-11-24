// -----------------------------------------------------------------------
// <copyright file="MonzoService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MonzoManager.Models;
using Refit;
using Serilog;

namespace MonzoManager.Services;

/// <summary>
/// Provides account and transaction operations over the Monzo API.
/// </summary>
public interface IMonzoService
{
    /// <summary>
    /// Retrieves the default account for the authenticated user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account snapshot when available; otherwise <see langword="null"/>.</returns>
    Task<Account?> GetAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves recent transactions from the default account.
    /// </summary>
    /// <param name="limit">Maximum number of transactions to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of normalized transactions.</returns>
    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(int limit = 100, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IMonzoService"/>.
/// </summary>
public sealed class MonzoService : IMonzoService
{
    private static readonly HttpClient ApiClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://api.monzo.com"),
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly IAuthService _authService;
    private readonly IMonzoApi _api;

    private string? _accountId;
    private string? _accountType;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonzoService"/> class.
    /// </summary>
    /// <param name="authService">Authentication service used for token management.</param>
    public MonzoService(IAuthService authService)
        : this(authService, RestService.For<IMonzoApi>(ApiClient))
    {
    }

    internal MonzoService(IAuthService authService, IMonzoApi api)
    {
        _authService = authService;
        _api = api;
    }

    /// <inheritdoc />
    public async Task<Account?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!await _authService.EnsureValidTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            Log.Warning("Cannot retrieve account because no valid token is available.");
            return null;
        }

        string? token = _authService.GetAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("Cannot retrieve account because token retrieval returned null.");
            return null;
        }

        string authorization = $"Bearer {token}";

        try
        {
            MonzoAccountsResponse response = await _api.GetAccountsAsync("uk_retail", authorization, cancellationToken).ConfigureAwait(false);
            MonzoAccount? selectedAccount = response.Accounts
                .FirstOrDefault(account => string.Equals(account.Type, "uk_retail", StringComparison.OrdinalIgnoreCase))
                ?? response.Accounts.FirstOrDefault();

            if (selectedAccount is null)
            {
                Log.Warning("Monzo API returned zero accounts.");
                return null;
            }

            _accountId = selectedAccount.Id;
            _accountType = selectedAccount.Type;

            MonzoBalanceResponse balanceResponse = await _api
                .GetBalanceAsync(_accountId, authorization, cancellationToken)
                .ConfigureAwait(false);

            return new Account
            {
                Id = _accountId,
                Type = _accountType ?? string.Empty,
                Balance = balanceResponse.Balance / 100m,
                Currency = balanceResponse.Currency,
            };
        }
        catch (ApiException exception)
        {
            Log.Error("Monzo API account call failed: {StatusCode}", exception.StatusCode);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("Could not read the Monzo account ({ErrorType}).", exception.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        if (!await _authService.EnsureValidTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            Log.Warning("Cannot retrieve transactions because no valid token is available.");
            return Array.Empty<Transaction>();
        }

        string? token = _authService.GetAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("Cannot retrieve transactions because token retrieval returned null.");
            return Array.Empty<Transaction>();
        }

        if (string.IsNullOrWhiteSpace(_accountId))
        {
            Account? account = await GetAccountAsync(cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                return Array.Empty<Transaction>();
            }
        }

        string authorization = $"Bearer {token}";

        try
        {
            MonzoTransactionsResponse response = await _api
                .GetTransactionsAsync(_accountId!, authorization, Math.Min(100, limit), ["merchant"], cancellationToken)
                .ConfigureAwait(false);

            return response.Transactions
                .Select(
                    transaction => new Transaction
                    {
                        Id = transaction.Id,
                        Created = transaction.Created,
                        Amount = transaction.Amount / 100m,
                        Currency = transaction.Currency,
                        Description = transaction.Description,
                        Category = transaction.Category,
                        IsLoad = transaction.IsLoad,
                        Merchant = transaction.Merchant?.Name,
                    })
                .OrderByDescending(transaction => transaction.Created)
                .ToList();
        }
        catch (ApiException exception)
        {
            Log.Error("Monzo API transaction call failed: {StatusCode}", exception.StatusCode);
            return Array.Empty<Transaction>();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("Could not read Monzo transactions ({ErrorType}).", exception.GetType().Name);
            return Array.Empty<Transaction>();
        }
    }

    internal async Task<IReadOnlyList<MonzoAccount>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        string authorization = await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        var accounts = new Dictionary<string, MonzoAccount>(StringComparer.Ordinal);
        foreach (string kind in new[] { "uk_retail", "uk_retail_joint" })
        {
            MonzoAccountsResponse response = await _api.GetAccountsAsync(kind, authorization, cancellationToken).ConfigureAwait(false);
            foreach (MonzoAccount account in response.Accounts)
            {
                if (account.Id.Length > 0 && (account.Type.Length == 0 || account.Type == kind))
                {
                    account.Type = kind;
                    accounts.TryAdd(account.Id, account);
                }
            }
        }

        return accounts.Values.ToArray();
    }

    internal async Task<MonzoBalanceResponse> ReadBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        string authorization = await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        return await _api.GetBalanceAsync(accountId, authorization, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<MonzoPot>> ListPotsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        string authorization = await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        MonzoPotsResponse response = await _api.GetPotsAsync(accountId, authorization, cancellationToken).ConfigureAwait(false);
        return response.Pots.Where(pot => !pot.Deleted && pot.Id.Length != 0 && pot.Currency == "GBP")
            .DistinctBy(pot => pot.Id).ToArray();
    }

    internal async Task<HistoryPageResult> ReadHistoryAsync(string accountId, DateTime fromUtc, DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 100;
        string authorization = await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        string since = fromUtc.ToString("O", CultureInfo.InvariantCulture);
        string before = toUtc.ToString("O", CultureInfo.InvariantCulture);
        var transactions = new Dictionary<string, Transaction>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                MonzoTransactionsResponse response = await _api.GetTransactionPageAsync(accountId, authorization, since,
                    before, ["merchant"], pageSize, cancellationToken).ConfigureAwait(false);

                int countBefore = transactions.Count;
                foreach (MonzoTransaction item in response.Transactions)
                {
                    if (item.Id.Length == 0 || item.Created.ToUniversalTime() < fromUtc || item.Created.ToUniversalTime() > toUtc)
                    {
                        throw new InvalidDataException("Monzo returned a transaction outside the requested page.");
                    }

                    transactions[item.Id] = ConvertTransaction(accountId, item);
                }

                if (response.Transactions.Count < pageSize)
                {
                    return new HistoryPageResult(transactions.Values.ToArray(), Complete: true);
                }

                // The since cursor moves forward. A descending page cannot prove
                // continuous coverage because the beginning may have been skipped.
                if (transactions.Count == countBefore || response.Transactions[0].Created > response.Transactions[^1].Created)
                {
                    throw new InvalidDataException("Monzo transaction pagination did not advance as expected.");
                }

                since = response.Transactions[^1].Id;
            }
        }
        catch (Exception exception) when (transactions.Count > 0 && !cancellationToken.IsCancellationRequested
            && exception is ApiException or HttpRequestException or IOException or TaskCanceledException)
        {
            return new HistoryPageResult(transactions.Values.ToArray(), Complete: false);
        }
    }

    private static Transaction ConvertTransaction(string accountId, MonzoTransaction entry) => new()
    {
        AccountId = accountId,
        Id = entry.Id,
        Amount = entry.Amount / 100m,
        Created = entry.Created.ToUniversalTime(),
        Currency = entry.Currency,
        Description = entry.Description,
        Merchant = entry.Merchant?.Name,
        Category = entry.Category,
        IsLoad = entry.IsLoad,
        IsDeclined = !string.IsNullOrWhiteSpace(entry.DeclineReason),
        IsPending = string.IsNullOrWhiteSpace(entry.Settled) && string.IsNullOrWhiteSpace(entry.DeclineReason),
    };

    private async Task<string> GetAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (await _authService.EnsureValidTokenAsync(cancellationToken).ConfigureAwait(false)
            && _authService.GetAccessToken() is { Length: > 0 } token)
        {
            return $"Bearer {token}";
        }

        throw new InvalidOperationException("Connect Monzo before syncing.");
    }

}

internal sealed record HistoryPageResult(IReadOnlyList<Transaction> Transactions, bool Complete);
