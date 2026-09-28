import { Link, Outlet, useRouter } from '@tanstack/react-router';
import { logout, useSession } from '@/lib/auth';
import { Button } from '@/components/ui/button';

// Later tasks extend this with role-gated entries (Profiles, API keys — Admin only).
const navItems = [
  { to: '/segments' as const, label: 'Segments' },
  { to: '/status' as const, label: 'Status' },
];

export function AuthedLayout() {
  const session = useSession();
  const router = useRouter();

  async function handleLogout() {
    await logout();
    router.history.push('/login');
  }

  return (
    <div className="min-h-screen bg-zinc-50 text-zinc-900 dark:bg-zinc-950 dark:text-zinc-100">
      <header className="flex items-center gap-6 border-b border-zinc-200 px-6 py-3 dark:border-zinc-800">
        <span className="font-semibold">CRM</span>
        <nav className="flex gap-4 text-sm">
          {navItems.map((item) => (
            <Link key={item.to} to={item.to} className="[&.active]:font-semibold">
              {item.label}
            </Link>
          ))}
        </nav>
        {session ? (
          <div className="ml-auto flex items-center gap-3 text-sm">
            <span className="text-zinc-500 dark:text-zinc-400">{session.role}</span>
            <Button variant="outline" size="sm" onClick={() => void handleLogout()}>
              Log out
            </Button>
          </div>
        ) : null}
      </header>
      <main className="p-6">
        <Outlet />
      </main>
    </div>
  );
}
