// -----------------------------------------------------------------------
// <copyright file="NetWorthService.cs" company="MonzoManager">
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
using Serilog;

namespace MonzoManager.Services;

/// <summary>
/// Represents a net-worth snapshot captured at a specific point in time.
/// </summary>
public sealed class NetWorthSnapshot
{
    /// <summary>
    /// Gets or sets the UTC timestamp for the snapshot.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>
    /// Gets or sets the captured net-worth amount in major units.
    /// </summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// Persists and retrieves net-worth history for dashboard charting.
/// </summary>
public interface INetWorthService
{
    /// <summary>
    /// Records a new net-worth snapshot.
    /// </summary>
    /// <param name="amount">Current net-worth amount in major units.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RecordSnapshotAsync(decimal amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical net-worth snapshots.
    /// </summary>
    /// <param name="maxPoints">Optional maximum points to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of snapshots from oldest to newest.</returns>
    Task<IReadOnlyList<NetWorthSnapshot>> GetSnapshotsAsync(int maxPoints = 60, CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-backed implementation of <see cref="INetWorthService"/>.
/// </summary>
public sealed class NetWorthService : INetWorthService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="NetWorthService"/> class.
    /// </summary>
    public NetWorthService()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonzoManager");
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "networth-history.json");
    }

    /// <inheritdoc />
    public async Task RecordSnapshotAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<NetWorthSnapshot> snapshots = (await LoadInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset? lastTimestamp = snapshots.LastOrDefault()?.TimestampUtc;

            if (lastTimestamp.HasValue && (now - lastTimestamp.Value) < TimeSpan.FromMinutes(30))
            {
                snapshots[^1] = new NetWorthSnapshot
                {
                    TimestampUtc = now,
                    Amount = amount,
                };
            }
            else
            {
                snapshots.Add(
                    new NetWorthSnapshot
                    {
                        TimestampUtc = now,
                        Amount = amount,
                    });
            }

            const int keepLast = 365;
            if (snapshots.Count > keepLast)
            {
                snapshots = snapshots.Skip(snapshots.Count - keepLast).ToList();
            }

            await SaveInternalAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error("Failed to record a net-worth snapshot ({ErrorType}).", exception.GetType().Name);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetWorthSnapshot>> GetSnapshotsAsync(int maxPoints = 60, CancellationToken cancellationToken = default)
    {
        if (maxPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "Maximum points must be positive.");
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<NetWorthSnapshot> snapshots = (await LoadInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            return snapshots
                .OrderBy(snapshot => snapshot.TimestampUtc)
                .TakeLast(maxPoints)
                .ToList();
        }
        catch (Exception exception)
        {
            Log.Error("Failed to load net-worth snapshots ({ErrorType}).", exception.GetType().Name);
            return Array.Empty<NetWorthSnapshot>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<NetWorthSnapshot>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<NetWorthSnapshot>();
        }

        string json = await LocalFiles.ReadFinanceAsync(_filePath, cancellationToken).ConfigureAwait(false);
        List<NetWorthSnapshot>? snapshots = JsonConvert.DeserializeObject<List<NetWorthSnapshot>>(json);
        return snapshots ?? new List<NetWorthSnapshot>();
    }

    private async Task SaveInternalAsync(IReadOnlyList<NetWorthSnapshot> snapshots, CancellationToken cancellationToken)
    {
        string json = JsonConvert.SerializeObject(snapshots, Formatting.Indented);
        await LocalFiles.WriteFinanceAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
