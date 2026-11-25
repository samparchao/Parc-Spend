// -----------------------------------------------------------------------
// <copyright file="MonzoApi.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Refit;

namespace MonzoManager.Services;

/// <summary>
/// Refit definition for Monzo account and transaction APIs.
/// </summary>
public interface IMonzoApi
{
    /// <summary>
    /// Returns accounts available to the authenticated user.
    /// </summary>
    /// <param name="accountType">Optional account type filter such as <c>uk_retail</c>.</param>
    /// <param name="authorization">Bearer authorization header value.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
    /// <returns>A response containing account metadata.</returns>
    [Get("/accounts")]
    Task<MonzoAccountsResponse> GetAccountsAsync(
        [AliasAs("account_type")] string? accountType,
        [Header("Authorization")] string authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns balance details for an account.
    /// </summary>
    /// <param name="accountId">Monzo account identifier.</param>
    /// <param name="authorization">Bearer authorization header value.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
    /// <returns>The account balance response.</returns>
    [Get("/balance")]
    Task<MonzoBalanceResponse> GetBalanceAsync(
        [AliasAs("account_id")] string accountId,
        [Header("Authorization")] string authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns transactions for an account.
    /// </summary>
    /// <param name="accountId">Monzo account identifier.</param>
    /// <param name="authorization">Bearer authorization header value.</param>
    /// <param name="limit">Maximum number of transactions to return.</param>
    /// <param name="expand">Optional expandable fields, such as <c>merchant</c>.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
    /// <returns>The matching transaction response payload.</returns>
    [Get("/transactions")]
    Task<MonzoTransactionsResponse> GetTransactionsAsync(
        [AliasAs("account_id")] string accountId,
        [Header("Authorization")] string authorization,
        [AliasAs("limit")] int limit = 100,
        [AliasAs("expand[]"), Query(CollectionFormat.Multi)] string[]? expand = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page of transactions with merchant details.</summary>
    /// <param name="accountId">Monzo account identifier.</param>
    /// <param name="authorization">Bearer authorization header value.</param>
    /// <param name="since">Start timestamp or transaction cursor.</param>
    /// <param name="before">Exclusive end timestamp.</param>
    /// <param name="expansions">Fields to expand in the response.</param>
    /// <param name="limit">Page size, at most 100.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
    /// <returns>The matching transaction page.</returns>
    [Get("/transactions")]
    Task<MonzoTransactionsResponse> GetTransactionPageAsync(
        [AliasAs("account_id")] string accountId,
        [Header("Authorization")] string authorization,
        [AliasAs("since")] string since,
        [AliasAs("before")] string before,
        [AliasAs("expand[]"), Query(CollectionFormat.Multi)] string[] expansions,
        [AliasAs("limit")] int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the Pots associated with an account.</summary>
    /// <param name="accountId">Monzo current account identifier.</param>
    /// <param name="authorization">Bearer authorization header value.</param>
    /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
    /// <returns>The response containing Pot balances.</returns>
    [Get("/pots")]
    Task<MonzoPotsResponse> GetPotsAsync(
        [AliasAs("current_account_id")] string accountId,
        [Header("Authorization")] string authorization,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// API response for account listing.
/// </summary>
public class MonzoAccountsResponse
{
    /// <summary>
    /// Gets or sets account records.
    /// </summary>
    public List<MonzoAccount> Accounts { get; set; } = new();
}

/// <summary>
/// API account model.
/// </summary>
public class MonzoAccount
{
    /// <summary>Gets or sets the account label returned by Monzo.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the account creation timestamp.</summary>
    public DateTimeOffset? Created { get; set; }

    /// <summary>
    /// Gets or sets the account identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account type.
    /// </summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// API response for account balance.
/// </summary>
public class MonzoBalanceResponse
{
    /// <summary>
    /// Gets or sets the balance in minor units.
    /// </summary>
    public long Balance { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code.
    /// </summary>
    public string Currency { get; set; } = "GBP";
}

/// <summary>
/// API response for transaction listing.
/// </summary>
public class MonzoTransactionsResponse
{
    /// <summary>
    /// Gets or sets transaction records.
    /// </summary>
    public List<MonzoTransaction> Transactions { get; set; } = new();
}

/// <summary>
/// API transaction model.
/// </summary>
public class MonzoTransaction
{
    /// <summary>Gets or sets the settlement timestamp, which is empty for a pending transaction.</summary>
    public string? Settled { get; set; }

    /// <summary>Gets or sets the reason Monzo declined a transaction.</summary>
    [JsonPropertyName("decline_reason")]
    public string? DeclineReason { get; set; }

    /// <summary>
    /// Gets or sets the transaction identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets transaction created timestamp.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets amount in minor units.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code.
    /// </summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// Gets or sets optional transaction description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets optional category.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets whether Monzo identifies the transaction as an account top-up.
    /// </summary>
    [JsonPropertyName("is_load")]
    public bool IsLoad { get; set; }

    /// <summary>
    /// Gets or sets optional merchant details.
    /// </summary>
    public MonzoMerchant? Merchant { get; set; }
}

/// <summary>
/// API merchant model.
/// </summary>
public class MonzoMerchant
{
    /// <summary>
    /// Gets or sets the merchant display name.
    /// </summary>
    public string? Name { get; set; }
}


/// <summary>The response for listing Monzo Pots.</summary>
public sealed class MonzoPotsResponse
{
    /// <summary>Gets or sets the Pots associated with the account.</summary>
    public List<MonzoPot> Pots { get; set; } = [];
}

/// <summary>A Monzo Pot returned by the developer API.</summary>
public sealed class MonzoPot
{
    /// <summary>Gets or sets the Pot identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the Pot.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the balance in minor units.</summary>
    public long Balance { get; set; }

    /// <summary>Gets or sets the currency code.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>Gets or sets whether Monzo has deleted this Pot.</summary>
    public bool Deleted { get; set; }
}
