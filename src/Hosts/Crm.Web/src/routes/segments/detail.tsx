import { useParams } from '@tanstack/react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/client';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Spinner } from '@/components/ui/spinner';
import { SegmentForm } from '@/segments/SegmentForm';

// This task renders the form only (edit mode); Task 5 wraps it in tabs (Runs, Members, Changes).
export function SegmentDetailPage() {
  // The `authed` layout is a pathless route with its own id (J1/J16: `id: 'authed'`), so the registered route
  // id carries that prefix even though the URL itself is just `/segments/$id`.
  const { id } = useParams({ from: '/authed/segments/$id' });
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: ['segment', id], queryFn: () => api.segments.get(id) });

  if (query.isPending) {
    return (
      <p className="flex items-center gap-2 text-sm">
        <Spinner /> Loading…
      </p>
    );
  }

  if (query.isError) {
    return (
      <p role="alert" className="text-sm text-red-600">
        {String(query.error)}
      </p>
    );
  }

  const segment = query.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h1">{segment.name}</CardTitle>
      </CardHeader>
      <CardContent>
        <SegmentForm
          initial={segment}
          onSaved={() => {
            void queryClient.invalidateQueries({ queryKey: ['segment', id] });
          }}
        />
      </CardContent>
    </Card>
  );
}
