import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/lib/api';

type Readiness = { status: string; checks: Record<string, string> };

export function HomePage() {
  const { data, error, isPending } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => apiFetch<Readiness>('/health/ready'),
    refetchInterval: 10_000,
  });

  if (isPending) return <p>Checking API…</p>;
  if (error) return <p className="text-red-600">API unreachable: {String(error)}</p>;

  return (
    <section className="space-y-3">
      <h1 className="text-xl font-semibold">Platform status: {data.status}</h1>
      <ul className="space-y-1 text-sm">
        {Object.entries(data.checks).map(([name, status]) => (
          <li key={name} data-testid={`check-${name}`}>
            {name}: {status}
          </li>
        ))}
      </ul>
    </section>
  );
}
