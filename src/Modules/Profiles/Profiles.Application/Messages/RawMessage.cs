using Crm.Contracts;

namespace Crm.Profiles.Application.Messages;

public abstract record RawMessage(Guid TenantId);

public sealed record TrackMessage(TrackEventRaw Payload) : RawMessage(Payload.TenantId);

public sealed record IdentifyMessage(IdentifyRaw Payload) : RawMessage(Payload.TenantId);
