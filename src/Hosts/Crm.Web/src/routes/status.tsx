import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

type Readiness = { status: string; checks: Record<string, string> };

export function StatusPage() {
  const { data, error, isPending } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => apiFetch<Readiness>('/health/ready'),
    refetchInterval: 10_000,
  });

  if (isPending) return <p>Checking API…</p>;
  if (error) return <p className="text-red-600">API unreachable: {String(error)}</p>;

  return (
    <Card className="max-w-md">
      <CardHeader>
        <CardTitle as="h1">Platform status: {data.status}</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="space-y-1 text-sm">
          {Object.entries(data.checks).map(([name, status]) => (
            <li key={name} data-testid={`check-${name}`}>
              {name}: {status}
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}
