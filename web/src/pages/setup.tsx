import { useEffect, useState, type FormEvent } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { GitBranch } from 'lucide-react';
import { api, ApiError } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

type Phase = 'checking' | 'ready' | 'complete' | 'error';

export default function SetupPage() {
  const navigate = useNavigate();
  const [phase, setPhase] = useState<Phase>('checking');
  const [token, setToken] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void api
      .setupStatus()
      .then((s) => {
        if (cancelled) return;
        setPhase(s.is_complete ? 'complete' : 'ready');
      })
      .catch(() => {
        if (cancelled) return;
        setPhase('error');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (phase === 'complete') return <Navigate to="/login" replace />;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await api.setup({
        token: token.trim(),
        username: username.trim(),
        password,
        display_name: displayName.trim() || username.trim(),
      });
      navigate('/login', { replace: true, state: { setupJustCompleted: true } });
    } catch (err) {
      if (err instanceof ApiError) {
        const message =
          (err.body as { error?: string } | null)?.error ?? `${err.status} ${err.message}`;
        setError(message);
      } else {
        setError(String(err));
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-background py-8">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <GitBranch className="h-5 w-5" />
            tamp-beacon · first-run setup
          </CardTitle>
        </CardHeader>
        <CardContent>
          {phase === 'checking' && (
            <p className="text-muted-foreground text-sm">Checking setup state…</p>
          )}
          {phase === 'error' && (
            <p className="text-destructive text-sm">
              Could not reach <code className="font-mono">/setup/status</code>. Make sure the
              beacon backend is up.
            </p>
          )}
          {phase === 'ready' && (
            <>
              <p className="text-sm text-muted-foreground mb-4">
                Paste the one-time setup token from the pod's stdout banner, then choose your
                first admin credentials. The token is consumed once — restart the pod to mint a
                fresh one if you lose this copy.
              </p>
              <form onSubmit={onSubmit} className="space-y-4">
                <div className="space-y-1">
                  <label className="text-sm font-medium" htmlFor="token">
                    Setup token
                  </label>
                  <Input
                    id="token"
                    autoFocus
                    value={token}
                    onChange={(e) => setToken(e.target.value)}
                    className="font-mono"
                  />
                </div>
                <div className="space-y-1">
                  <label className="text-sm font-medium" htmlFor="username">
                    Username
                  </label>
                  <Input
                    id="username"
                    autoComplete="username"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                  />
                  <p className="text-xs text-muted-foreground">
                    lowercase a–z 0–9 . _ -, 2–64 chars
                  </p>
                </div>
                <div className="space-y-1">
                  <label className="text-sm font-medium" htmlFor="display-name">
                    Display name (optional)
                  </label>
                  <Input
                    id="display-name"
                    value={displayName}
                    onChange={(e) => setDisplayName(e.target.value)}
                  />
                </div>
                <div className="space-y-1">
                  <label className="text-sm font-medium" htmlFor="password">
                    Password
                  </label>
                  <Input
                    id="password"
                    type="password"
                    autoComplete="new-password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                  />
                  <p className="text-xs text-muted-foreground">min 12 characters</p>
                </div>
                {error && <p className="text-sm text-destructive">{error}</p>}
                <Button
                  type="submit"
                  disabled={submitting || !token || !username || password.length < 12}
                  className="w-full"
                >
                  {submitting ? 'Creating admin…' : 'Create admin & continue to sign-in'}
                </Button>
              </form>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
