import { useState, type FormEvent } from 'react';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import type { SegmentResponse, SegmentWriteRequest } from '@/lib/client';
import { useSession } from '@/lib/auth';
import { isRealtimeEligible } from '@/rules/eligibility';
import { nodeAt, parseErrorPath } from '@/rules/paths';
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

export type UnmappedFieldError = { key: string; message: string };

const TOP_LEVEL_FIELDS = new Set(['name', 'description', 'status']);

/** Splits `problem.errors` (I-1b) into `fieldErrors` — every key a rendered row or form field can display —
 * and `unmapped`: a `definition…` path whose `parseErrorPath` doesn't resolve, or whose node no longer exists
 * in the CURRENT tree (e.g. the row was removed or reindexed while the request was in flight), plus any other
 * unrecognized key. Nothing from the server response is dropped silently; `unmapped` renders in the top
 * `role="alert"` region as `<key>: <message>` lines. */
function splitFieldErrors(
  errors: Record<string, string[]>,
  tree: RuleTree,
): { fieldErrors: FieldErrors; unmapped: UnmappedFieldError[] } {
  const fieldErrors = new Map<string, string[]>();
  const unmapped: UnmappedFieldError[] = [];

  for (const [key, messages] of Object.entries(errors)) {
    const parsed = parseErrorPath(key);
    const displayable = TOP_LEVEL_FIELDS.has(key) || (parsed !== null && nodeAt(tree, parsed.indexes) !== undefined);
    if (displayable) {
      fieldErrors.set(key, messages);
    } else {
      for (const message of messages) {
        unmapped.push({ key, message });
      }
    }
  }

  return { fieldErrors, unmapped };
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
  const [unmappedErrors, setUnmappedErrors] = useState<UnmappedFieldError[]>([]);
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
  // immediately for fast feedback, but the request is always sent; a successful response clears the map, a 400
  // replaces it with the server's field errors (I-1b: anything that no longer maps to a rendered row goes to
  // the top alert instead of vanishing), and a 409 merges its `name` entry into the client-side map rather than
  // discarding it (M-1) so a rule error already on screen isn't silently dropped. This is a deliberate
  // deviation from the brief's literal "no request" wording, called out in the report.
  //
  // While `saving` is true, Name/Description/Status and the rule builder are all disabled (I-1a) — the tree
  // used to check `splitFieldErrors`' `nodeAt` reachability below is exactly the one the request was sent
  // with, since nothing can mutate it mid-flight.
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const clientErrors = ruleErrorsToFieldErrors(validateRule(tree));
    setFieldErrors(clientErrors);
    setTopError(null);
    setUnmappedErrors([]);
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
      setUnmappedErrors([]);
      onSaved(saved);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setFieldErrors(new Map([...clientErrors, ['name', [err.problem?.detail ?? 'Name is already taken.']]]));
      } else if (err instanceof ApiError && err.status === 400 && err.problem?.errors) {
        const { fieldErrors: mapped, unmapped } = splitFieldErrors(err.problem.errors, tree);
        setFieldErrors(mapped);
        setUnmappedErrors(unmapped);
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

  // I-1a: the rule builder and every other field lock for the duration of the in-flight request, not just
  // Viewer sessions, so nothing the user changes mid-request can shift the paths a server error refers to.
  const fieldsDisabled = isViewer || saving;

  return (
    <form className="space-y-4" onSubmit={(event) => void handleSubmit(event)}>
      {topError || unmappedErrors.length > 0 ? (
        <div role="alert" className="space-y-1 text-sm text-red-600">
          {topError ? <p>{topError}</p> : null}
          {unmappedErrors.map(({ key, message }) => (
            <p key={`${key}:${message}`}>{`${key}: ${message}`}</p>
          ))}
        </div>
      ) : null}

      <div className="space-y-1 text-sm">
        <label htmlFor="segment-name">Name</label>
        <Input
          id="segment-name"
          aria-label="Name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          disabled={fieldsDisabled}
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
          disabled={fieldsDisabled}
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
          disabled={fieldsDisabled}
          className="w-40"
        />
        {statusErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <RuleBuilder tree={tree} onChange={setTree} errors={fieldErrors} readOnly={fieldsDisabled} />

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
          {saving ? 'Saving…' : 'Save'}
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
