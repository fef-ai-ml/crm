namespace Crm.Profiles.Application.Messages;

/// <summary>One record for events.processed: key {tenantId:N}:{profileId:N}, JSON value, type for the message-type header.</summary>
public sealed record ProcessedMessage(string Key, string MessageType, Guid TenantId, byte[] Value);
