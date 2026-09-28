const dateTimeFormatter = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

/** J15: renders a UTC timestamp in the viewer's locale; `null` renders as an em dash. */
export function formatDateTime(value: string | null): string {
  if (value === null) return '—';
  return dateTimeFormatter.format(new Date(value));
}
