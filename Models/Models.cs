// -----------------------------------------------------------------------
// <copyright file="Models.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;

namespace MonzoManager.Models;

/// <summary>
/// Represents a normalized bank transaction in major currency units.
/// </summary>
public class Transaction
{
    /// <summary>Gets or sets the account that supplied this transaction.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the bank still considers this transaction pending.</summary>
    public bool IsPending { get; set; }

    /// <summary>Gets or sets whether the bank declined this transaction.</summary>
    public bool IsDeclined { get; set; }

    /// <summary>
    /// Gets or sets the transaction identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp the transaction was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets the signed transaction amount in major units.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code.
    /// </summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// Gets or sets the transaction description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the transaction category.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets whether Monzo identifies the transaction as an account top-up.
    /// </summary>
    public bool IsLoad { get; set; }

    /// <summary>
    /// Gets or sets the merchant name.
    /// </summary>
    public string? Merchant { get; set; }
}

/// <summary>
/// Represents a Monzo account snapshot.
/// </summary>
public class Account
{
    /// <summary>
    /// Gets or sets the account identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current account balance in major units.
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code for the account.
    /// </summary>
    public string Currency { get; set; } = "GBP";
}

/// <summary>
/// Represents a budget target and current spending progress.
/// </summary>
public class Budget
{
    /// <summary>Gets or sets the monthly cycle start day, or zero for a one-off budget.</summary>
    public int CycleDay { get; set; }

    /// <summary>Gets or sets whether unused funds carry forward from completed cycles.</summary>
    public bool Rollover { get; set; }

    /// <summary>
    /// Gets or sets the budget identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transaction category this budget applies to.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category spend limit in major units.
    /// </summary>
    public decimal Limit { get; set; }

    /// <summary>
    /// Gets or sets the current spend for the period in major units.
    /// </summary>
    public decimal Spent { get; set; }

    /// <summary>
    /// Gets or sets the inclusive budget period start date.
    /// </summary>
    public DateTime StartDate { get; set; }

    /// <summary>
    /// Gets or sets the inclusive budget period end date.
    /// </summary>
    public DateTime EndDate { get; set; }
}
