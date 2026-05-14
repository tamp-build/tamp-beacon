import { Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { Activity, FolderTree, GitBranch, LogOut } from 'lucide-react';
import LoginPage from '@/pages/login';
import SetupPage from '@/pages/setup';
import BuildsPage from '@/pages/builds';
import BuildDetailPage from '@/pages/build-detail';
import ProjectsPage from '@/pages/projects';
import ProjectDetailPage from '@/pages/project-detail';
import ProjectSettingsPage from '@/pages/project-settings';
import ConfigDetailPage from '@/pages/config-detail';
import { ThemeToggle } from '@/components/ui/theme-toggle';
import { Button } from '@/components/ui/button';
import { useAuth } from '@/lib/auth';

const NAV = [
  { to: '/projects', label: 'Projects', icon: FolderTree },
  { to: '/builds', label: 'Builds', icon: Activity },
] as const;

function RequireAuth({ children }: { children: JSX.Element }) {
  const { session, loading } = useAuth();
  const location = useLocation();
  if (loading) return <div className="container py-8 text-muted-foreground">Loading…</div>;
  if (!session)
    return <Navigate to="/login" replace state={{ from: location }} />;
  return children;
}

function AppShell() {
  const { session, signOut } = useAuth();
  return (
    <div className="min-h-screen flex flex-col">
      <header className="border-b border-border/40">
        <div className="container flex items-center gap-6 py-3">
          <Link to="/" className="flex items-center gap-2 font-semibold">
            <GitBranch className="h-5 w-5" />
            <span>tamp-beacon</span>
          </Link>
          <nav className="flex gap-1 flex-1">
            {NAV.map(({ to, label, icon: Icon }) => (
              <Link
                key={to}
                to={to}
                className="inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm hover:bg-accent hover:text-accent-foreground"
              >
                <Icon className="h-4 w-4" />
                {label}
              </Link>
            ))}
          </nav>
          <span className="text-sm text-muted-foreground">
            {session?.display_name ?? session?.username}
            {session?.is_system_admin ? ' · sysadmin' : ''}
          </span>
          <Button variant="ghost" size="sm" onClick={() => void signOut()}>
            <LogOut className="h-4 w-4 mr-1" />
            Sign out
          </Button>
          <ThemeToggle />
        </div>
      </header>
      <main className="container flex-1 py-6">
        <Routes>
          <Route path="/" element={<Navigate to="/projects" replace />} />
          <Route path="/projects" element={<ProjectsPage />} />
          <Route path="/projects/:slug" element={<ProjectDetailPage />} />
          <Route path="/projects/:slug/settings" element={<ProjectSettingsPage />} />
          <Route path="/projects/:slug/configs/:configSlug" element={<ConfigDetailPage />} />
          <Route path="/builds" element={<BuildsPage />} />
          <Route path="/builds/:id" element={<BuildDetailPage />} />
          <Route path="*" element={<Navigate to="/projects" replace />} />
        </Routes>
      </main>
    </div>
  );
}

export default function App() {
  return (
    <Routes>
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="*"
        element={
          <RequireAuth>
            <AppShell />
          </RequireAuth>
        }
      />
    </Routes>
  );
}
