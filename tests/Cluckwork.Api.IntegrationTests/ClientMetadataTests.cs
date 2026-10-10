using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.OAuth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #1148 — which URLs may name a metadata document, and what a document must say. The
// document is untrusted input held to the same rules as a DCR request (#797).
public sealed class ClientMetadataTests
{
    private const string Url = "https://app.test/oauth/client.json";

    [Theory]
    [InlineData("https://app.test/oauth/client.json")]
    [InlineData("https://app.test:8443/client")]
    [InlineData("https://sub.app.test/a/b/c.json")]
    public void CanonicalHttpsUrl_IsADocumentUrl(string url) =>
        Assert.True(ClientMetadata.ParseDocumentUrl(url).IsSuccess);

    [Theory]
    [InlineData("http://app.test/client")]
    [InlineData("https://user@app.test/client")]
    [InlineData("https://app.test/client#frag")]
    [InlineData("https://app.test/client?x=1")]
    [InlineData("https://app.test/a/../client")]
    [InlineData("https://app.test/./client")]
    [InlineData("https://app.test")]
    [InlineData("https://app.test/")]
    [InlineData("https://APP.test/client")]
    [InlineData("https://app.test:443/client")]
    [InlineData("https://app.test/%63lient")]
    [InlineData("https://10.0.0.1/client")]
    [InlineData("https://[::1]/client")]
    [InlineData("https://bücher.test/client")]
    [InlineData("https://xn--bcher-kva.test/client ")]
    public void OtherForms_AreRefused(string url) =>
        Assert.Equal(Errors.InvalidRequest, ClientMetadata.ParseDocumentUrl(url).Error.Code);

    [Fact]
    public void Url_LongerThanTheColumn_IsRefused()
    {
        var at = "https://app.test/" + new string('a', ClientMetadata.MaxDocumentUrlLength - "https://app.test/".Length);
        var over = at + "a";

        Assert.True(ClientMetadata.ParseDocumentUrl(at).IsSuccess);
        Assert.Equal(Errors.InvalidRequest, ClientMetadata.ParseDocumentUrl(over).Error.Code);
    }

    [Theory]
    [InlineData("https://app.test/client", "app.test")]
    [InlineData("https://app.test:8443/client", "app.test:8443")]
    [InlineData("c0ffee0123456789abcdef0123456789", null)]
    [InlineData(null, null)]
    public void VerifiedDomain_IsTheDocumentsAuthority(string? clientId, string? domain) =>
        Assert.Equal(domain, ClientMetadata.VerifiedDomain(clientId));

    [Fact]
    public void ValidDocument_BecomesAPublicClient_UnderItsUrl()
    {
        var descriptor = ClientMetadata.FromDocument(Url, Json(MetadataDocumentServer.Document(Url, name: "Doc‮Client"))).Value;

        Assert.Equal(Url, descriptor.ClientId);
        Assert.Equal(ClientTypes.Public, descriptor.ClientType);
        Assert.Equal("Doc Client", descriptor.DisplayName);
        Assert.Equal([new Uri("https://app.test/callback")], descriptor.RedirectUris);
    }

    [Fact]
    public void LoopbackOnlyDocument_IsNative_WithPortlessRedirects()
    {
        var descriptor = ClientMetadata.FromDocument(Url, Json(MetadataDocumentServer.Document(Url, "http://127.0.0.1:33418/callback"))).Value;

        Assert.Equal(ApplicationTypes.Native, descriptor.ApplicationType);
        Assert.Equal([new Uri("http://127.0.0.1/callback")], descriptor.RedirectUris);
    }

    [Theory]
    [InlineData("https://app.test/oauth/client.json/")]
    [InlineData("https://APP.test/oauth/client.json")]
    [InlineData("https://other.test/oauth/client.json")]
    public void Document_NamingAnotherClientId_IsRefused(string named) =>
        Assert.Equal("invalid_client_metadata", ClientMetadata.FromDocument(Url, Json(MetadataDocumentServer.Document(named))).Error.Code);

    [Theory]
    [InlineData("""{"redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""{"client_id":1,"redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""["https://app.test/oauth/client.json"]""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","client_secret":"s","redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","client_secret_expires_at":0,"redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""{"client_id":"https://other.test/x","client_id":"https://app.test/oauth/client.json","redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","redirect_uris":"https://app.test/cb"}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","client_name":{"a":1},"redirect_uris":["https://app.test/cb"]}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","redirect_uris":["https://app.test/cb"],"grant_types":["client_credentials"]}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","redirect_uris":["https://app.test/cb"],"token_endpoint_auth_method":"private_key_jwt"}""")]
    [InlineData("""{"client_id":"https://app.test/oauth/client.json","redirect_uris":["https://app.test/cb"],"response_types":["token"]}""")]
    [InlineData("not json")]
    public void UntrustedDocument_IsRefused(string json) =>
        Assert.Equal("invalid_client_metadata", ClientMetadata.FromDocument(Url, Json(json)).Error.Code);

    [Theory]
    [InlineData("http://evil.test/cb")]
    [InlineData("custom-scheme:/cb")]
    [InlineData("https://user@app.test/cb")]
    public void DocumentWithAWiderRedirect_IsRefused(string redirect) =>
        Assert.Equal("invalid_redirect_uri", ClientMetadata.FromDocument(Url, Json(MetadataDocumentServer.Document(Url, redirect))).Error.Code);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
}
