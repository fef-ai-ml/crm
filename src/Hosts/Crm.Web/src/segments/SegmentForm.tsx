import { useState, type FormEvent } from 'react';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import type { SegmentResponse, SegmentWriteRequest } from '@/lib/client';
import { useSession } from '@/lib/auth';
import { isRealtimeEligible } from '@/rules/eligibility';
import { validateRule } from '@/rules/validate';
import type { RuleError, RuleTree } from '@/rules/types';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, type SelectOption } from '@/components/ui/select';
import { Spinner } from '@/components/ui/spinner';
import { Textarea } from '@/components/ui/textarea';
import { RuleBuilder } from './builder/RuleBuilder';
import { newDefinition } from './builder/reducer';
import type { FieldErrors } from './builder/reducer';
import { canonicalJson } from './canonical';
import { useLivePreview, type LivePreviewResult } from './useLivePreview';

const STATUS_OPTIONS: SelectOption[] = [
  { value: 'Draft', label: 'Draft' },
  { value: 'Active', label: 'Active' },
  { value: 'Archived', label: 'Archived' },
];

export type SegmentFormProps = {
  initial?: SegmentResponse;
  onSaved: (segment: SegmentResponse) => void;
};

function ruleErrorsToFieldErrors(errors: RuleError[]): FieldErrors {
  const map = new Map<string, string[]>();
  for (const { path, message } of errors) {
    map.set(path, [...(map.get(path) ?? []), message]);
  }
  return map;
}

function problemErrorsToFieldErrors(errors: Record<string, string[]>): FieldErrors {
  return new Map(Object.entries(errors));
}

/** Segment create/edit form: name, description, status, the rule builder, a live-count card and the
 * real-time/batch badge (J8), gated to Admin for Save (J9). See `useLivePreview` for the count's state
 * machine and this module's own JSDoc-style comments below for the Save flow's client/server error handling
 * (the E2E-003 ruling: client validation is shown immediately but never blocks the request). */
export function SegmentForm({ initial, onSaved }: SegmentFormProps) {
  const session = useSession();
  const isAdmin = session?.role === 'Admin';
  const isViewer = !isAdmin;

  const [name, setName] = useState(initial?.name ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [status, setStatus] = useState(initial?.status ?? 'Draft');
  const [tree, setTree] = useState<RuleTree>(initial?.definition ?? newDefinition());
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>(new Map());
  const [topError, setTopError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const preview = useLivePreview(tree);

  // J8: an unchanged tree while editing shows the server's own flag; any change (or a brand-new segment)
  // recomputes client-side. `canonicalJson` makes the comparison independent of key order.
  const treeChanged = !initial || canonicalJson(tree) !== canonicalJson(initial.definition);
  const eligible = treeChanged ? isRealtimeEligible(tree) : initial.isRealtimeEligible;

  // Save flow (brief interface section vs. the E2E-003 controller resolution): the brief's plain reading is
  // "validateRule first; errors block the request". E2E-003 needs the SERVER's message on a window the client
  // validator ALSO rejects (400 d exceeds the 365-day limit both sides enforce), which is impossible if the
  // client blocks the request outright. Resolution used here: client-side `validateRule` errors are shown
  // immediately for fast feedback, but the request is always sent; a successful or erroring response's fields
  // (server `errors`, or a 409) replace the client-side map. This is a deliberate deviation from the brief's
  // literal "no request" wording, called out in the report.
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    setFieldErrors(ruleErrorsToFieldErrors(validateRule(tree)));
    setTopError(null);
    setSaving(true);

    const body: SegmentWriteRequest = {
      name,
      description: description === '' ? null : description,
      definition: tree,
      status,
    };

    try {
      const saved = initial ? await api.segments.update(initial.id, body) : await api.segments.create(body);
      setFieldErrors(new Map());
      onSaved(saved);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setFieldErrors(new Map([['name', [err.problem?.detail ?? 'Name is already taken.']]]));
      } else if (err instanceof ApiError && err.status === 400 && err.problem?.errors) {
        setFieldErrors(problemErrorsToFieldErrors(err.problem.errors));
      } else if (err instanceof ApiError) {
        setTopError(err.problem?.detail ?? 'Save failed.');
      } else {
        setTopError('Save failed.');
      }
    } finally {
      setSaving(false);
    }
  }

  const nameErrors = fieldErrors.get('name') ?? [];
  const descriptionErrors = fieldErrors.get('description') ?? [];
  const statusErrors = fieldErrors.get('status') ?? [];

  return (
    <form className="space-y-4" onSubmit={(event) => void handleSubmit(event)}>
      {topError ? (
        <p role="alert" className="text-sm text-red-600">
          {topError}
        </p>
      ) : null}

      <div className="space-y-1 text-sm">
        <label htmlFor="segment-name">Name</label>
        <Input
          id="segment-name"
          aria-label="Name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          disabled={isViewer}
        />
        {nameErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="space-y-1 text-sm">
        <label htmlFor="segment-description">Description</label>
        <Textarea
          id="segment-description"
          aria-label="Description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          disabled={isViewer}
        />
        {descriptionErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="space-y-1 text-sm">
        <label htmlFor="segment-status">Status</label>
        <Select
          id="segment-status"
          aria-label="Status"
          options={STATUS_OPTIONS}
          value={status}
          onChange={(event) => setStatus(event.target.value)}
          disabled={isViewer}
          className="w-40"
        />
        {statusErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <RuleBuilder tree={tree} onChange={setTree} errors={fieldErrors} readOnly={isViewer} />

      <div className="flex flex-wrap items-center gap-3">
        <span data-testid="live-count" className="text-sm">
          <LiveCountDisplay preview={preview} />
        </span>
        <Badge data-testid="segment-badge" variant={eligible ? 'success' : 'muted'}>
          {eligible ? 'real-time' : 'batch'}
        </Badge>
      </div>

      {isAdmin && (
        <Button type="submit" disabled={saving}>
          Save
        </Button>
      )}
    </form>
  );
}

function LiveCountDisplay({ preview }: { preview: LivePreviewResult }) {
  switch (preview.status) {
    case 'invalid':
      return <span>Fix the rule to see a count</span>;
    case 'pending':
      return (
        <span className="inline-flex items-center gap-2">
          <Spinner /> Counting…
        </span>
      );
    case 'timedOut':
      return <span>too slow — the preview took more than 5 s</span>;
    case 'error':
      return <span className="text-red-600">{preview.error ?? 'Preview failed'}</span>;
    case 'ready':
      return (
        <span>
          {preview.count} profiles{preview.cached ? ' (cached)' : ''}
        </span>
      );
  }
}
