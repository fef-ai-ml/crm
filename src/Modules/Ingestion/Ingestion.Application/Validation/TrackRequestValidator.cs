using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Time;
using FluentValidation;

namespace Crm.Ingestion.Application.Validation;

public sealed class TrackRequestValidator : AbstractValidator<TrackRequest>
{
    public TrackRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(x => x.UserId)
            .Must((request, _) => !IngestRules.IsBlank(request.UserId) || !IngestRules.IsBlank(request.AnonymousId))
            .OverridePropertyName("userId").WithMessage("userId or anonymousId is required");
        RuleFor(x => x.UserId).MaximumLength(IngestRules.MaxUserIdLength)
            .OverridePropertyName("userId").WithMessage("must be at most 128 characters");
        RuleFor(x => x.AnonymousId).MaximumLength(IngestRules.MaxAnonymousIdLength)
            .OverridePropertyName("anonymousId").WithMessage("must be at most 256 characters");
        RuleFor(x => x.Event).Must(IngestRules.IsValidEventName)
            .OverridePropertyName("event").WithMessage(IngestRules.EventNameMessage);
        RuleFor(x => x.Properties).Must(IngestRules.IsObjectOrAbsent)
            .OverridePropertyName("properties").WithMessage(IngestRules.ObjectMessage);
        RuleFor(x => x.Properties).Must(IngestRules.FitsSizeLimit)
            .OverridePropertyName("properties").WithMessage(IngestRules.SizeMessage);
        RuleFor(x => x.Timestamp).Must(timestamp => IngestRules.IsWithinWindow(timestamp, clock.UtcNow))
            .OverridePropertyName("timestamp").WithMessage("must be within 7 days of now");
    }
}
