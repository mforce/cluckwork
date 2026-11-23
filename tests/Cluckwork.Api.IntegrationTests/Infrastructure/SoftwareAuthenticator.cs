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

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public string Id => WebEncoders.Base64UrlEncode(CredentialId);

    // The browser's answer to navigator.credentials.create for these options.
    // origin is what the browser puts in clientData; uv clears the
    // user-verified flag when false.
    public string Attest(JsonElement creationOptions, string origin = "https://localhost", bool uv = true)
    {
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.create",
            challenge = creationOptions.GetProperty("challenge").GetString(),
            origin,
            crossOrigin = false,
        });
        var rpId = creationOptions.GetProperty("rp").GetProperty("id").GetString()!;
        var flags = (byte)(UserPresent | (uv ? UserVerified : 0) | BackupEligible | BackedUp | AttestedData);
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        var authData = new Cbor()
            .Raw(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))).Raw([flags]).Raw([0, 0, 0, 0])
            .Raw(new byte[16]).Raw([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]).Raw(CredentialId)
            // COSE_Key, EC2 P-256 ES256, keys in CTAP2 canonical order.
            .Map(5).Int(1).Int(2).Int(3).Int(-7).Int(-1).Int(1).Int(-2).Bytes(parameters.Q.X!).Int(-3).Bytes(parameters.Q.Y!)
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
