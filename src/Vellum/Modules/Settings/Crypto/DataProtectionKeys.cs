using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Npgsql;

namespace Vellum.Modules.Settings.Crypto;

/// <summary>
/// The ASP.NET Data Protection keys sign the auth cookie. They live in Postgres so every
/// instance and every restart reads the same keys, and each key is sealed with the master key.
/// </summary>
internal static class DataProtectionEnvelope
{
    private static readonly XName Element = "vellumEnvelope";
    private static readonly byte[] AssociatedData = "vellum.data-protection.v1"u8.ToArray();

    public static XElement Seal(MasterKeyRing keys, XElement plaintext) =>
        ToXml(keys.Seal(Encoding.UTF8.GetBytes(plaintext.ToString(SaveOptions.DisableFormatting)), AssociatedData));

    public static XElement Open(MasterKeyRing keys, XElement envelope) =>
        XElement.Parse(Encoding.UTF8.GetString(keys.Open(FromXml(envelope), AssociatedData)));

    /// <summary>
    /// Wraps every envelope in a stored key document with the current master key.
    /// Returns the new document, or null when nothing needed a new wrap.
    /// </summary>
    public static string? Rewrap(MasterKeyRing keys, string storedXml)
    {
        var document = XElement.Parse(storedXml);
        var changed = false;
        foreach (var envelope in document.DescendantsAndSelf(Element).ToList())
        {
            var value = FromXml(envelope);
            if (keys.IsCurrent(value.MasterKeyId))
                continue;
            envelope.ReplaceWith(ToXml(keys.Rewrap(value, AssociatedData)));
            changed = true;
        }

        return changed ? document.ToString(SaveOptions.DisableFormatting) : null;
    }

    /// <summary>The master keys that the envelopes in a stored key document are wrapped with.</summary>
    public static IReadOnlyList<string> MasterKeyIds(string storedXml) =>
        XElement.Parse(storedXml).DescendantsAndSelf(Element)
            .Select(e => (string?)e.Attribute("masterKeyId"))
            .OfType<string>()
            .Distinct()
            .ToList();

    private static XElement ToXml(SealedValue value) => new(Element,
        new XAttribute("masterKeyId", value.MasterKeyId),
        new XElement("wrapNonce", Convert.ToBase64String(value.WrapNonce)),
        new XElement("wrappedKey", Convert.ToBase64String(value.WrappedKey)),
        new XElement("nonce", Convert.ToBase64String(value.Nonce)),
        new XElement("value", Convert.ToBase64String(value.Ciphertext)));

    private static SealedValue FromXml(XElement envelope) => new(
        (string?)envelope.Attribute("masterKeyId") ?? throw new FormatException("The key envelope names no master key."),
        Part(envelope, "wrapNonce"),
        Part(envelope, "wrappedKey"),
        Part(envelope, "nonce"),
        Part(envelope, "value"));

    private static byte[] Part(XElement envelope, string name) =>
        Convert.FromBase64String((string?)envelope.Element(name) ?? throw new FormatException($"The key envelope has no {name}."));
}

internal sealed class MasterKeyXmlEncryptor(MasterKeyRing keys) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(DataProtectionEnvelope.Seal(keys, plaintextElement), typeof(MasterKeyXmlDecryptor));
}

/// <summary>Data Protection creates this type by the name stored beside each key.</summary>
internal sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement) =>
        DataProtectionEnvelope.Open(services.GetRequiredService<Bootstrap>().Keys, encryptedElement);
}

/// <summary>
/// Data Protection calls its repository synchronously from a singleton, so this uses a
/// short-lived connection and not a scoped DbContext.
/// </summary>
internal sealed class PostgresXmlRepository(IConfiguration configuration) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT xml FROM settings.data_protection_keys ORDER BY id", connection);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
            elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO settings.data_protection_keys (friendly_name, xml) VALUES (@name, @xml)", connection);
        command.Parameters.AddWithValue("name", (object?)friendlyName ?? DBNull.Value);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
        connection.Open();
        return connection;
    }
}
