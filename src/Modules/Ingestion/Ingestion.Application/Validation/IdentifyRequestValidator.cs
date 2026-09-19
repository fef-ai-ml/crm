using Crm.Ingestion.Application.Requests;
using FluentValidation;

namespace Crm.Ingestion.Application.Validation;

public sealed class IdentifyRequestValidator : AbstractValidator<IdentifyRequest>
{
    public IdentifyRequestValidator()
    {
        RuleFor(x => x.UserId).Must(userId => !IngestRules.IsBlank(userId))
            .OverridePropertyName("userId").WithMessage("is required");
        RuleFor(x => x.UserId).MaximumLength(IngestRules.MaxUserIdLength)
            .OverridePropertyName("userId").WithMessage("must be at most 128 characters");
        RuleFor(x => x.AnonymousId).MaximumLength(IngestRules.MaxAnonymousIdLength)
            .OverridePropertyName("anonymousId").WithMessage("must be at most 256 characters");
        RuleFor(x => x.Traits).Must(IngestRules.IsObjectOrAbsent)
            .OverridePropertyName("traits").WithMessage(IngestRules.ObjectMessage);
        RuleFor(x => x.Traits).Must(IngestRules.FitsSizeLimit)
            .OverridePropertyName("traits").WithMessage(IngestRules.SizeMessage);
    }
}
