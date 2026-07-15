namespace Cluckwork.Infrastructure.Identity;

public static class PemKey
{
    // Env files and env vars cannot hold real line breaks, so PEM keys are
    // supplied with literal "\n" escapes. RSA.ImportFromPem needs actual
    // newlines — convert them (and normalize CRLF) before import.
    public static string Normalize(string pem) =>
        pem.Replace("\\n", "\n").Replace("\r\n", "\n");
}
