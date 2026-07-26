namespace Cluckwork.Application.Features.Users.SetLanguage;

using System.Text.RegularExpressions;
using FluentValidation;

public sealed partial class SetLanguageValidator : AbstractValidator<SetLanguageCommand>
{
    public SetLanguageValidator()
    {
        // null clears; otherwise a BCP-47 primary language subtag: 2–8 ASCII
        // letters (RFC 5646 primary-subtag width). Whitespace-only has already
        // collapsed to "" upstream and fails the length check, so it is rejected —
        // an empty string is not another spelling of null.
        RuleFor(x => x.Language)
            .Must(BeNullOrPrimarySubtag)
            .WithMessage("Language must be a 2–8 letter language code, for example 'en'.")
            .WithErrorCode("Me.Language.Format");
    }

    private static bool BeNullOrPrimarySubtag(string? value)
        => value is null || PrimarySubtag().IsMatch(value);

    [GeneratedRegex("^[A-Za-z]{2,8}$")]
    private static partial Regex PrimarySubtag();
}
