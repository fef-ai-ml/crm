import { useNavigate } from '@tanstack/react-router';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { SegmentForm } from '@/segments/SegmentForm';

export function SegmentNewPage() {
  const navigate = useNavigate();

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h1">New segment</CardTitle>
      </CardHeader>
      <CardContent>
        <SegmentForm onSaved={(segment) => void navigate({ to: '/segments/$id', params: { id: segment.id } })} />
      </CardContent>
    </Card>
  );
}
