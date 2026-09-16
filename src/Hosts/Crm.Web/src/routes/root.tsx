import { Link, Outlet } from '@tanstack/react-router';

export function RootLayout() {
  return (
    <div className="min-h-screen bg-zinc-50 text-zinc-900 dark:bg-zinc-950 dark:text-zinc-100">
      <header className="flex items-center gap-6 border-b border-zinc-200 px-6 py-3 dark:border-zinc-800">
        <span className="font-semibold">CRM</span>
        <nav className="flex gap-4 text-sm">
          <Link to="/" className="[&.active]:font-semibold">
            Status
          </Link>
          <Link to="/login" className="[&.active]:font-semibold">
            Login
          </Link>
        </nav>
      </header>
      <main className="p-6">
        <Outlet />
      </main>
    </div>
  );
}
