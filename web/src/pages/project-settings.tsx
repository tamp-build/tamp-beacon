import { useEffect, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, BellOff, Copy, KeyRound, ShieldOff, Trash, UserPlus } from 'lucide-react';
import { api, ApiError, type MintedToken } from '@/lib/api';
import {
  detectPushStatus,
  formatPushError,
  subscribeToProject,
  unsubscribeFromProject,
  type PushStatus,
} from '@/lib/push';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

function extractError(err: unknown): string {
  if (err instanceof ApiError) {
    return (err.body as { error?: string } | null)?.error ?? `${err.status} ${err.message}`;
  }
  return String(err);
}

export default function ProjectSettingsPage() {
  const { slug = '' } = useParams<{ slug: string }>();
  const project = useQuery({
    queryKey: ['project', slug],
    queryFn: () => api.getProject(slug),
    enabled: !!slug,
  });

  if (!slug) return null;
  const canManage = project.data?.my_role === 'admin';

  return (
    <div className="space-y-6">
      <div>
        <Link to={`/projects/${slug}`} className="text-sm text-muted-foreground hover:text-foreground">
          ← {project.data?.name ?? slug}
        </Link>
        <h1 className="text-2xl font-semibold mt-1">Settings</h1>
        <p className="font-mono text-xs text-muted-foreground mt-1">{slug}</p>
      </div>

      <NotificationsCard slug={slug} />
      <MembersCard slug={slug} canManage={canManage} />
      <TokensCard slug={slug} canManage={canManage} />
    </div>
  );
}

function NotificationsCard({ slug }: { slug: string }) {
  const [status, setStatus] = useState<PushStatus>({ kind: 'unsupported' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void detectPushStatus().then((s) => {
      if (!cancelled) setStatus(s);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function onSubscribe() {
    setBusy(true);
    setError(null);
    try {
      setStatus(await subscribeToProject(slug));
    } catch (e) {
      setError(formatPushError(e));
    } finally {
      setBusy(false);
    }
  }
  async function onUnsubscribe() {
    setBusy(true);
    setError(null);
    try {
      setStatus(await unsubscribeFromProject(slug));
    } catch (e) {
      setError(formatPushError(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          <span>Notifications</span>
          {status.kind === 'subscribed' ? (
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => void onUnsubscribe()}>
              <BellOff className="h-4 w-4 mr-1" />
              Disable
            </Button>
          ) : (
            <Button
              size="sm"
              disabled={busy || status.kind === 'unsupported' || status.kind === 'denied'}
              onClick={() => void onSubscribe()}
            >
              <Bell className="h-4 w-4 mr-1" />
              Enable
            </Button>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="text-sm text-muted-foreground">
        {status.kind === 'subscribed' && <p>This browser will receive Web Push alerts on failed builds.</p>}
        {status.kind === 'not_subscribed' && <p>Click "Enable" to receive a notification when a build fails.</p>}
        {status.kind === 'denied' && <p>Notification permission was denied in this browser.</p>}
        {status.kind === 'unsupported' && (
          <p>This browser doesn't support Web Push (or you're on a non-HTTPS origin other than localhost).</p>
        )}
        {error && <p className="text-destructive mt-2">{error}</p>}
      </CardContent>
    </Card>
  );
}

function MembersCard({ slug, canManage }: { slug: string; canManage: boolean }) {
  const qc = useQueryClient();
  const members = useQuery({
    queryKey: ['members', slug],
    queryFn: () => api.listMembers(slug),
  });
  const [addOpen, setAddOpen] = useState(false);
  const [newUsername, setNewUsername] = useState('');
  const [newRole, setNewRole] = useState<'admin' | 'viewer'>('viewer');
  const [error, setError] = useState<string | null>(null);

  const add = useMutation({
    mutationFn: () => api.addMember(slug, { username: newUsername.trim(), role: newRole }),
    onSuccess: () => {
      setAddOpen(false);
      setNewUsername('');
      setError(null);
      void qc.invalidateQueries({ queryKey: ['members', slug] });
      void qc.invalidateQueries({ queryKey: ['project', slug] });
    },
    onError: (err) => setError(extractError(err)),
  });

  const remove = useMutation({
    mutationFn: (id: number) => api.removeMember(slug, id),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['members', slug] }),
    onError: (err) => setError(extractError(err)),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          <span>Members</span>
          {canManage && (
            <Button size="sm" onClick={() => setAddOpen((v) => !v)}>
              <UserPlus className="h-4 w-4 mr-1" />
              {addOpen ? 'Cancel' : 'Add'}
            </Button>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {addOpen && (
          <form
            onSubmit={(e: FormEvent) => {
              e.preventDefault();
              add.mutate();
            }}
            className="flex items-end gap-2 max-w-xl"
          >
            <div className="flex-1 space-y-1">
              <label className="text-sm font-medium">Username</label>
              <Input value={newUsername} onChange={(e) => setNewUsername(e.target.value)} />
            </div>
            <div className="space-y-1">
              <label className="text-sm font-medium">Role</label>
              <select
                className="h-9 rounded-md border bg-background px-3 text-sm"
                value={newRole}
                onChange={(e) => setNewRole(e.target.value as 'admin' | 'viewer')}
              >
                <option value="viewer">viewer</option>
                <option value="admin">admin</option>
              </select>
            </div>
            <Button type="submit" disabled={add.isPending || !newUsername.trim()}>
              Add
            </Button>
          </form>
        )}
        {error && <p className="text-sm text-destructive">{error}</p>}
        {members.data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Username</TableHead>
                <TableHead>Display name</TableHead>
                <TableHead>Role</TableHead>
                {canManage && <TableHead className="w-16"></TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {members.data.members.map((m) => (
                <TableRow key={m.id}>
                  <TableCell className="font-mono">{m.username}</TableCell>
                  <TableCell>{m.display_name}</TableCell>
                  <TableCell>
                    <Badge variant={m.role === 'admin' ? 'default' : 'secondary'}>{m.role}</Badge>
                  </TableCell>
                  {canManage && (
                    <TableCell>
                      <Button
                        size="sm"
                        variant="ghost"
                        title="Remove"
                        onClick={() => remove.mutate(m.id)}
                      >
                        <Trash className="h-4 w-4" />
                      </Button>
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}

function TokensCard({ slug, canManage }: { slug: string; canManage: boolean }) {
  const qc = useQueryClient();
  const tokens = useQuery({
    queryKey: ['tokens', slug],
    queryFn: () => api.listTokens(slug),
    enabled: canManage,
  });
  const [mintOpen, setMintOpen] = useState(false);
  const [label, setLabel] = useState('');
  const [minted, setMinted] = useState<MintedToken | null>(null);
  const [error, setError] = useState<string | null>(null);

  const mint = useMutation({
    mutationFn: () => api.mintToken(slug, label.trim()),
    onSuccess: (m) => {
      setMinted(m);
      setMintOpen(false);
      setLabel('');
      setError(null);
      void qc.invalidateQueries({ queryKey: ['tokens', slug] });
    },
    onError: (err) => setError(extractError(err)),
  });

  const revoke = useMutation({
    mutationFn: (id: number) => api.revokeToken(slug, id),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['tokens', slug] }),
  });

  if (!canManage) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Ingest tokens</CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-muted-foreground">
          Only project admins can view or manage ingest tokens.
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          <span>Ingest tokens</span>
          <Button size="sm" onClick={() => setMintOpen((v) => !v)}>
            <KeyRound className="h-4 w-4 mr-1" />
            {mintOpen ? 'Cancel' : 'Mint token'}
          </Button>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {mintOpen && (
          <form
            onSubmit={(e: FormEvent) => {
              e.preventDefault();
              mint.mutate();
            }}
            className="flex items-end gap-2 max-w-xl"
          >
            <div className="flex-1 space-y-1">
              <label className="text-sm font-medium">Label</label>
              <Input
                placeholder="ci-prod, my-laptop, …"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
              />
            </div>
            <Button type="submit" disabled={mint.isPending || !label.trim()}>
              Mint
            </Button>
          </form>
        )}
        {minted && (
          <div className="rounded-md border border-primary/40 bg-primary/5 p-3 space-y-2">
            <p className="text-sm font-medium">
              New token for <span className="font-mono">{minted.label}</span> — shown ONCE,
              copy it now.
            </p>
            <div className="flex items-center gap-2">
              <code className="font-mono text-xs flex-1 break-all">{minted.token}</code>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  void navigator.clipboard.writeText(minted.token);
                }}
              >
                <Copy className="h-4 w-4" />
              </Button>
            </div>
            <Button size="sm" variant="ghost" onClick={() => setMinted(null)}>
              Dismiss
            </Button>
          </div>
        )}
        {error && <p className="text-sm text-destructive">{error}</p>}
        {tokens.data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Label</TableHead>
                <TableHead>Created</TableHead>
                <TableHead>Last used</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="w-16"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tokens.data.tokens.map((t) => (
                <TableRow key={t.id}>
                  <TableCell>{t.label}</TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {new Date(t.created_at).toLocaleString()}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {t.last_used_at ? new Date(t.last_used_at).toLocaleString() : 'never'}
                  </TableCell>
                  <TableCell>
                    {t.revoked_at ? (
                      <Badge variant="destructive">revoked</Badge>
                    ) : (
                      <Badge variant="default">active</Badge>
                    )}
                  </TableCell>
                  <TableCell>
                    {!t.revoked_at && (
                      <Button
                        size="sm"
                        variant="ghost"
                        title="Revoke"
                        onClick={() => revoke.mutate(t.id)}
                      >
                        <ShieldOff className="h-4 w-4" />
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
