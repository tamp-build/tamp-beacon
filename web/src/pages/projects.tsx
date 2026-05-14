import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { FolderPlus } from 'lucide-react';
import { api, ApiError } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';

export default function ProjectsPage() {
  const qc = useQueryClient();
  const { data, isLoading, error } = useQuery({
    queryKey: ['projects'],
    queryFn: () => api.listProjects(),
    refetchInterval: 10_000,
  });
  const [showForm, setShowForm] = useState(false);
  const [slug, setSlug] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [formError, setFormError] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: () =>
      api.createProject({
        slug: slug.trim(),
        name: name.trim(),
        description: description.trim() || null,
      }),
    onSuccess: () => {
      setShowForm(false);
      setSlug('');
      setName('');
      setDescription('');
      setFormError(null);
      void qc.invalidateQueries({ queryKey: ['projects'] });
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        const message =
          (err.body as { error?: string } | null)?.error ?? `${err.status} ${err.message}`;
        setFormError(message);
      } else {
        setFormError(String(err));
      }
    },
  });

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    create.mutate();
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">Projects</h1>
        <Button onClick={() => setShowForm((v) => !v)} variant={showForm ? 'ghost' : 'default'}>
          <FolderPlus className="h-4 w-4 mr-1" />
          {showForm ? 'Cancel' : 'New project'}
        </Button>
      </div>

      {showForm && (
        <Card>
          <CardHeader>
            <CardTitle>Create project</CardTitle>
          </CardHeader>
          <CardContent>
            <form onSubmit={onSubmit} className="space-y-3 max-w-md">
              <div className="space-y-1">
                <label className="text-sm font-medium" htmlFor="slug">
                  Slug
                </label>
                <Input
                  id="slug"
                  value={slug}
                  onChange={(e) => setSlug(e.target.value)}
                  placeholder="my-project"
                />
                <p className="text-xs text-muted-foreground">
                  lowercase a–z, 0–9, hyphen; 2–64 chars; no leading/trailing hyphen
                </p>
              </div>
              <div className="space-y-1">
                <label className="text-sm font-medium" htmlFor="name">
                  Name
                </label>
                <Input id="name" value={name} onChange={(e) => setName(e.target.value)} />
              </div>
              <div className="space-y-1">
                <label className="text-sm font-medium" htmlFor="description">
                  Description (optional)
                </label>
                <Input
                  id="description"
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                />
              </div>
              {formError && <p className="text-sm text-destructive">{formError}</p>}
              <Button type="submit" disabled={create.isPending || !slug || !name}>
                {create.isPending ? 'Creating…' : 'Create'}
              </Button>
            </form>
          </CardContent>
        </Card>
      )}

      {isLoading && <p className="text-muted-foreground">Loading…</p>}
      {error && <p className="text-destructive">Failed to load projects.</p>}

      {data && data.projects.length === 0 && (
        <Card>
          <CardContent className="py-10 text-center text-muted-foreground">
            No projects yet. Click <span className="font-medium">New project</span> to create one.
          </CardContent>
        </Card>
      )}

      {data && data.projects.length > 0 && (
        <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
          {data.projects.map((p) => (
            <Link key={p.slug} to={`/projects/${p.slug}`}>
              <Card className="hover:border-primary transition-colors">
                <CardHeader>
                  <CardTitle className="flex items-center justify-between">
                    <span>{p.name}</span>
                    <Badge variant={p.my_role === 'admin' ? 'default' : 'secondary'}>
                      {p.my_role}
                    </Badge>
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-sm text-muted-foreground space-y-1">
                  <div className="font-mono text-xs">{p.slug}</div>
                  {p.description && <div>{p.description}</div>}
                  <div>
                    {p.member_count} {p.member_count === 1 ? 'member' : 'members'}
                  </div>
                </CardContent>
              </Card>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
