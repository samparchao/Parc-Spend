// -----------------------------------------------------------------------
// <copyright file="MonzoService.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
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
    {
        _authService = authService;
        _api = RestService.For<IMonzoApi>(ApiClient);
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
                .GetTransactionsAsync(_accountId!, authorization, limit, ["merchant"], cancellationToken)
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
}
