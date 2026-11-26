using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1284: a passkey authenticator for the tests, as the record's probe built
// one: a P-256 key, attestation format none, hand-written CBOR. It answers a
// registration ceremony with the browser's PublicKeyCredential JSON.
internal sealed class SoftwareAuthenticator
{
    private const byte UserPresent = 0x01, UserVerified = 0x04, BackupEligible = 0x08, BackedUp = 0x10, AttestedData = 0x40;

    private readonly ECDsa _key;

    // A new credential id, or one copied from another authenticator to race it.
    // counts: each assertion reports a sign count one above the last, as a
    // security key does; otherwise 0, as synced passkeys report (#1285).
    public SoftwareAuthenticator(byte[]? credentialId = null, bool counts = false)
        : this(ECDsa.Create(ECCurve.NamedCurves.nistP256),
            credentialId is null ? RandomNumberGenerator.GetBytes(16) : [.. credentialId], counts)
    {
    }

    private SoftwareAuthenticator(ECDsa key, byte[] credentialId, bool counts)
    {
        _key = key;
        CredentialId = credentialId;
        Counts = counts;
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        // COSE_Key, EC2 P-256 ES256, keys in CTAP2 canonical order.
        PublicKey = new Cbor().Map(5).Int(1).Int(2).Int(3).Int(-7).Int(-1).Int(1).Int(-2).Bytes(q.X!).Int(-3).Bytes(q.Y!).ToArray();
    }

    public byte[] CredentialId { get; }

    public bool Counts { get; }

    // The COSE key as Identity stores it, to compare a stored row with.
    public byte[] PublicKey { get; }

    // The last sign count this authenticator reported.
    public uint Counter { get; private set; }

    // The user handle the registration's options named, base64url.
    public string? UserHandle { get; set; }

    // The same key, credential, user handle and count: a cloned authenticator.
    public SoftwareAuthenticator Clone() =>
        new(ECDsa.Create(_key.ExportParameters(includePrivateParameters: true)), CredentialId, Counts)
        {
            UserHandle = UserHandle,
            Counter = Counter,
        };

    public string Id => WebEncoders.Base64UrlEncode(CredentialId);

    // The browser's answer to navigator.credentials.create for these options.
    // origin and crossOrigin are what the browser puts in clientData; uv
    // clears the user-verified flag when false.
    public string Attest(JsonElement creationOptions, string origin = "https://localhost", bool uv = true, bool crossOrigin = false)
    {
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.create",
            challenge = creationOptions.GetProperty("challenge").GetString(),
            origin,
            crossOrigin,
        });
        UserHandle = creationOptions.GetProperty("user").GetProperty("id").GetString();
        var rpId = creationOptions.GetProperty("rp").GetProperty("id").GetString()!;
        var flags = (byte)(UserPresent | (uv ? UserVerified : 0) | BackupEligible | BackedUp | AttestedData);
        var authData = new Cbor()
            .Raw(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))).Raw([flags]).Raw([0, 0, 0, 0])
            .Raw(new byte[16]).Raw([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]).Raw(CredentialId)
            .Raw(PublicKey)
            .ToArray();
        var attestationObject = new Cbor()
            .Map(3).Text("fmt").Text("none").Text("attStmt").Map(0).Text("authData").Bytes(authData)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            id = Id,
            rawId = Id,
            type = "public-key",
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                attestationObject = WebEncoders.Base64UrlEncode(attestationObject),
                transports = new[] { "internal" },
            },
            clientExtensionResults = new { },
            authenticatorAttachment = "platform",
        });
    }

    // The browser's answer to navigator.credentials.get for these request
    // options (#1285), signed over authenticatorData and the client data's hash.
    public string Assert(JsonElement requestOptions, string origin = "https://localhost", bool uv = true)
    {
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = requestOptions.GetProperty("challenge").GetString(),
            origin,
            crossOrigin = false,
        });
        if (Counts) Counter++;
        var rpId = requestOptions.GetProperty("rpId").GetString()!;
        var flags = (byte)(UserPresent | (uv ? UserVerified : 0) | BackupEligible | BackedUp);
        var authData = new Cbor()
            .Raw(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))).Raw([flags])
            .Raw([(byte)(Counter >> 24), (byte)(Counter >> 16), (byte)(Counter >> 8), (byte)Counter])
            .ToArray();
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            id = Id,
            rawId = Id,
            type = "public-key",
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                authenticatorData = WebEncoders.Base64UrlEncode(authData),
                signature = WebEncoders.Base64UrlEncode(signature),
                userHandle = UserHandle,
            },
            clientExtensionResults = new { },
            authenticatorAttachment = "platform",
        });
    }

    // Just enough CBOR for an attestation object and a COSE key.
    private sealed class Cbor
    {
        private readonly MemoryStream _out = new();

        public Cbor Raw(byte[] bytes) { _out.Write(bytes); return this; }
        public Cbor Map(int entries) => Head(5, (ulong)entries);
        public Cbor Int(long value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));
        public Cbor Bytes(byte[] value) => Head(2, (ulong)value.Length).Raw(value);
        public Cbor Text(string value) => Bytes3(Encoding.UTF8.GetBytes(value));
        public byte[] ToArray() => _out.ToArray();

        private Cbor Bytes3(byte[] utf8) => Head(3, (ulong)utf8.Length).Raw(utf8);

        private Cbor Head(int major, ulong value)
        {
            var type = (byte)(major << 5);
            if (value < 24) return Raw([(byte)(type | (byte)value)]);
            if (value < 256) return Raw([(byte)(type | 24), (byte)value]);
            return Raw([(byte)(type | 25), (byte)(value >> 8), (byte)value]);
        }
    }
}
