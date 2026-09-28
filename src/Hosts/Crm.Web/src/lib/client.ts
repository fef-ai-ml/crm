import { apiFetch } from './api';
import { login, logout, refreshSession } from './auth';
import type { components } from './api.generated';
import type { RuleTree } from '../rules/types';

export type { RuleTree };

type Schemas = components['schemas'];

function toNumber(value: number | string): number {
  return Number(value);
}

function toNumberOrNull(value: number | string | null): number | null {
  return value === null ? null : Number(value);
}

function buildQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const qs = query.toString();
  return qs ? `?${qs}` : '';
}

// ---- Segments ----

export type SegmentListItem = Omit<Schemas['SegmentListItem'], 'memberCount'> & { memberCount: number };
export type SegmentListResponse = { items: SegmentListItem[]; nextCursor: string | null };

export type SegmentResponse = Omit<
  Schemas['SegmentResponse'],
  'memberCount' | 'realtimeMemberCount' | 'definition'
> & {
  memberCount: number;
  realtimeMemberCount: number | null;
  definition: RuleTree;
};

export type SegmentWriteRequest = Omit<Schemas['SegmentWriteRequest'], 'definition'> & { definition: RuleTree };

export type SegmentRunListItem = Omit<Schemas['SegmentRunListItem'], 'memberCount'> & { memberCount: number | null };
export type SegmentRunListResponse = { items: SegmentRunListItem[]; nextCursor: string | null };

export type SegmentMemberListItem = Schemas['SegmentMemberListItem'];
export type SegmentMemberListResponse = { items: SegmentMemberListItem[]; nextCursor: string | null };

export type PreviewResponse = Omit<Schemas['PreviewResponse'], 'count'> & { count: number | null };

function normalizeSegmentListItem(item: Schemas['SegmentListItem']): SegmentListItem {
  return { ...item, memberCount: toNumber(item.memberCount) };
}

function normalizeSegmentResponse(item: Schemas['SegmentResponse']): SegmentResponse {
  return {
    ...item,
    memberCount: toNumber(item.memberCount),
    realtimeMemberCount: toNumberOrNull(item.realtimeMemberCount),
    definition: item.definition as RuleTree,
  };
}

function normalizeSegmentRunListItem(item: Schemas['SegmentRunListItem']): SegmentRunListItem {
  return { ...item, memberCount: toNumberOrNull(item.memberCount) };
}

function normalizePreviewResponse(item: Schemas['PreviewResponse']): PreviewResponse {
  return { ...item, count: toNumberOrNull(item.count) };
}

const segments = {
  async list(params: { status?: string; limit?: number; cursor?: string } = {}): Promise<SegmentListResponse> {
    const response = await apiFetch<Schemas['SegmentListResponse']>(`/v1/segments${buildQuery(params)}`);
    return { items: response.items.map(normalizeSegmentListItem), nextCursor: response.nextCursor };
  },
  async get(id: string): Promise<SegmentResponse> {
    return normalizeSegmentResponse(await apiFetch<Schemas['SegmentResponse']>(`/v1/segments/${id}`));
  },
  async create(body: SegmentWriteRequest): Promise<SegmentResponse> {
    return normalizeSegmentResponse(
      await apiFetch<Schemas['SegmentResponse']>('/v1/segments', { method: 'POST', json: body }),
    );
  },
  async update(id: string, body: SegmentWriteRequest): Promise<SegmentResponse> {
    return normalizeSegmentResponse(
      await apiFetch<Schemas['SegmentResponse']>(`/v1/segments/${id}`, { method: 'PUT', json: body }),
    );
  },
  archive(id: string): Promise<void> {
    return apiFetch<undefined>(`/v1/segments/${id}`, { method: 'DELETE' });
  },
  async preview(definition: RuleTree): Promise<PreviewResponse> {
    return normalizePreviewResponse(
      await apiFetch<Schemas['PreviewResponse']>('/v1/segments/preview', { method: 'POST', json: { definition } }),
    );
  },
  run(id: string): Promise<Schemas['RunQueuedResponse']> {
    return apiFetch<Schemas['RunQueuedResponse']>(`/v1/segments/${id}/run`, { method: 'POST' });
  },
  async runs(id: string, params: { limit?: number; cursor?: string } = {}): Promise<SegmentRunListResponse> {
    const response = await apiFetch<Schemas['SegmentRunListResponse']>(
      `/v1/segments/${id}/runs${buildQuery(params)}`,
    );
    return { items: response.items.map(normalizeSegmentRunListItem), nextCursor: response.nextCursor };
  },
  members(id: string, params: { limit?: number; cursor?: string } = {}): Promise<SegmentMemberListResponse> {
    return apiFetch<Schemas['SegmentMemberListResponse']>(`/v1/segments/${id}/members${buildQuery(params)}`);
  },
};

// ---- Profiles ----

export type ProfileListItem = Omit<Schemas['ProfileListItem'], 'eventCount'> & { eventCount: number };
export type ProfileListResponse = { items: ProfileListItem[]; nextCursor: string | null };
export type ProfileResponse = Omit<Schemas['ProfileResponse'], 'eventCount' | 'attributes'> & {
  eventCount: number;
  attributes: Record<string, unknown>;
};
export type TimelineEvent = Omit<Schemas['TimelineEvent'], 'properties'> & { properties: Record<string, unknown> };
export type TimelineResponse = { items: TimelineEvent[]; nextCursor: string | null };

function normalizeProfileListItem(item: Schemas['ProfileListItem']): ProfileListItem {
  return { ...item, eventCount: toNumber(item.eventCount) };
}

function normalizeProfileResponse(item: Schemas['ProfileResponse']): ProfileResponse {
  return {
    ...item,
    eventCount: toNumber(item.eventCount),
    attributes: (item.attributes ?? {}) as Record<string, unknown>,
  };
}

function normalizeTimelineEvent(item: Schemas['TimelineEvent']): TimelineEvent {
  return { ...item, properties: (item.properties ?? {}) as Record<string, unknown> };
}

const profiles = {
  async search(params: { q?: string; limit?: number; cursor?: string } = {}): Promise<ProfileListResponse> {
    const response = await apiFetch<Schemas['ProfileListResponse']>(`/v1/profiles${buildQuery(params)}`);
    return { items: response.items.map(normalizeProfileListItem), nextCursor: response.nextCursor };
  },
  async get(id: string): Promise<ProfileResponse> {
    return normalizeProfileResponse(await apiFetch<Schemas['ProfileResponse']>(`/v1/profiles/${id}`));
  },
  async events(
    id: string,
    params: { name?: string; limit?: number; cursor?: string } = {},
  ): Promise<TimelineResponse> {
    const response = await apiFetch<Schemas['TimelineResponse']>(`/v1/profiles/${id}/events${buildQuery(params)}`);
    return { items: response.items.map(normalizeTimelineEvent), nextCursor: response.nextCursor };
  },
};

// ---- API keys ----

export type ApiKeyListItem = Schemas['ApiKeyListItem'];
export type ApiKeyListResponse = { items: ApiKeyListItem[]; nextCursor: string | null };
export type ApiKeyCreated = Schemas['ApiKeyCreatedResponse'];

const apiKeys = {
  list(): Promise<ApiKeyListResponse> {
    return apiFetch<ApiKeyListResponse>('/v1/api-keys');
  },
  create(name: string): Promise<ApiKeyCreated> {
    return apiFetch<ApiKeyCreated>('/v1/api-keys', { method: 'POST', json: { name } });
  },
  revoke(id: string): Promise<void> {
    return apiFetch<undefined>(`/v1/api-keys/${id}`, { method: 'DELETE' });
  },
};

export const api = {
  auth: { login, refresh: refreshSession, logout },
  segments,
  profiles,
  apiKeys,
};
