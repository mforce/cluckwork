using System.Globalization;
using System.Text;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Enrolling and replacing a key": the one function that builds the
// otpauth:// URI an authenticator app scans. The caps bound the URI to 673
// bytes, QR version 21 at level M (G3.32, web/src/test/otpauth-worst-case.txt).
internal static class OtpauthUri
{
    internal const int FarmNameCap = 120;
    internal const int AccountCap = 174;

    internal static string Build(string farmName, string farmCode, string email, Guid userId, string secret)
    {
        var issuer = Escape(Issuer(farmName, farmCode));
        return $"otpauth://totp/{issuer}:{Escape(Account(email, userId))}?secret={secret}&issuer={issuer}";
    }

    // Apps split the label at its first ':', so the name keeps none.
    internal static string Issuer(string farmName, string farmCode)
    {
        var name = farmName.Replace(':', ' ').Trim();
        if (name.Length == 0) return $"Cluckwork ({farmCode})";
        var (kept, cut) = CutToEscapedBytes(name, FarmNameCap);
        return cut ? $"Cluckwork ({kept}… · {farmCode})" : $"Cluckwork ({name})";
    }

    // No real address reaches the cap. A cut one carries the user id, so two
    // users of one farm never share a label.
    internal static string Account(string email, Guid userId)
    {
        var (kept, cut) = CutToEscapedBytes(email, AccountCap);
        return cut ? $"{kept}… · {userId:N}" : email;
    }

    // Cuts whole text elements from the end, so no character is split.
    private static (string Kept, bool Cut) CutToEscapedBytes(string value, int cap)
    {
        if (Escape(value).Length <= cap) return (value, false);
        var elements = StringInfo.GetTextElementEnumerator(value);
        var kept = new StringBuilder();
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (Escape(kept + element).Length > cap) break;
            kept.Append(element);
        }
        return (kept.ToString(), true);
    }

    // Every escaped character is ASCII, so the length is the byte count.
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
