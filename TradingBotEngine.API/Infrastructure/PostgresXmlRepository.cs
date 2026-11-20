using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace TradingBotEngine.API.Infrastructure;

/// <summary>
/// Stores the ASP.NET Data Protection key ring in the app's PostgreSQL database, so broker API keys
/// that were encrypted with it stay readable after a restart or redeploy (hosts with no persistent disk).
/// If a secret is supplied, every key-ring entry is AES-GCM encrypted before it is stored.
/// The table is created automatically; no EF migration is needed.
/// </summary>
public sealed class PostgresXmlRepository : IXmlRepository
{
    private const string Prefix = "enc:v1:";
    private readonly string _connectionString;
    private readonly byte[]? _key;
    private readonly object _gate = new();
    private bool _tableReady;

    public PostgresXmlRepository(string connectionString, string? encryptionSecret)
    {
        _connectionString = connectionString;
        _key = string.IsNullOrWhiteSpace(encryptionSecret)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(encryptionSecret));
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        EnsureTable();

        var elements = new List<XElement>();
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT \"Xml\" FROM \"DataProtectionKeys\" ORDER BY \"Id\"", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            elements.Add(XElement.Parse(Unwrap(reader.GetString(0))));

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        EnsureTable();

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO \"DataProtectionKeys\" (\"FriendlyName\", \"Xml\") VALUES (@name, @xml)", connection);
        command.Parameters.AddWithValue("name", friendlyName ?? string.Empty);
        command.Parameters.AddWithValue("xml", Wrap(element.ToString(SaveOptions.DisableFormatting)));
        command.ExecuteNonQuery();
    }

    private void EnsureTable()
    {
        if (_tableReady) return;

        lock (_gate)
        {
            if (_tableReady) return;

            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            using var command = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS \"DataProtectionKeys\" (" +
                "\"Id\" SERIAL PRIMARY KEY, \"FriendlyName\" TEXT NOT NULL, \"Xml\" TEXT NOT NULL)",
                connection);
            command.ExecuteNonQuery();
            _tableReady = true;
        }
    }

    private string Wrap(string xml)
    {
        if (_key == null) return xml;

        var plain = Encoding.UTF8.GetBytes(xml);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private string Unwrap(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        if (_key == null)
            throw new InvalidOperationException(
                "The Data Protection key ring is encrypted but DataProtection:KeyEncryptionSecret is not set.");

        var data = Convert.FromBase64String(stored.Substring(Prefix.Length));
        var nonce = data.AsSpan(0, 12).ToArray();
        var tag = data.AsSpan(12, 16).ToArray();
        var cipher = data.AsSpan(28).ToArray();
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
