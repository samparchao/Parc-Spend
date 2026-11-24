// -----------------------------------------------------------------------
// <copyright file="LocalFiles.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MonzoManager.Services;

internal static class LocalFiles
{
    private const string ProtectedPrefix = "dpapi:v1:";

    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonzoManager");

    internal static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static async Task<string> ReadFinanceAsync(string path, CancellationToken cancellationToken = default)
    {
        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!content.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            // Legacy JSON remains readable until the next successful save.
            return content;
        }

        byte[] decoded = ProtectedData.Unprotect(
            Convert.FromBase64String(content[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    internal static Task WriteFinanceAsync(string path, string json, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return WriteAtomicAsync(path, ProtectedPrefix + Convert.ToBase64String(encrypted), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static byte[] EncryptBackup(string json, string password)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        // Header, salt, nonce, authentication tag, ciphertext.
        byte[] packet = new byte[52 + plaintext.Length];
        "MONZOBK1"u8.CopyTo(packet);
        RandomNumberGenerator.Fill(packet.AsSpan(8, 28));
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, packet.AsSpan(8, 16).ToArray(), 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(packet.AsSpan(24, 12), plaintext, packet.AsSpan(52), packet.AsSpan(36, 16), packet.AsSpan(0, 8));
            return packet;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string DecryptBackup(byte[] packet, string password)
    {
        if (packet.Length < 52 || !packet.AsSpan(0, 8).SequenceEqual("MONZOBK1"u8))
        {
            throw new InvalidDataException("Choose a Parc Spend backup file.");
        }

        byte[] plaintext = new byte[packet.Length - 52];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, packet.AsSpan(8, 16).ToArray(), 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(packet.AsSpan(24, 12), packet.AsSpan(52), packet.AsSpan(36, 16), plaintext, packet.AsSpan(0, 8));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
