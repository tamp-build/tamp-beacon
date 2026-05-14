import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api, type BuildSummary } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';

function formatNs(ns: number): string {
  const ms = ns / 1_000_000;
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  return `${(s / 60).toFixed(1)} m`;
}

function formatUnix(ns: number): string {
  return new Date(ns / 1_000_000).toLocaleString();
}

export default function BuildsPage() {
  const [outcome, setOutcome] = useState<'all' | 'success' | 'failure'>('all');
  const { data, isLoading } = useQuery({
    queryKey: ['allBuilds', outcome],
    queryFn: () =>
      api.listBuilds({
        outcome: outcome === 'all' ? undefined : outcome,
        limit: 100,
      }),
    refetchInterval: 5_000,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          <span>Builds</span>
          <select
            className="h-9 rounded-md border bg-background px-3 text-sm"
            value={outcome}
            onChange={(e) => setOutcome(e.target.value as typeof outcome)}
          >
            <option value="all">all outcomes</option>
            <option value="success">success</option>
            <option value="failure">failure</option>
          </select>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {isLoading && <p className="text-muted-foreground">Loading…</p>}
        {data && data.builds.length === 0 && (
          <p className="text-muted-foreground">No builds visible to you yet.</p>
        )}
        {data && data.builds.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-20">Seq</TableHead>
                <TableHead>Project</TableHead>
                <TableHead>Started</TableHead>
                <TableHead>Duration</TableHead>
                <TableHead>Outcome</TableHead>
                <TableHead>Targets</TableHead>
                <TableHead>CI</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {[...data.builds].reverse().map((b: BuildSummary) => (
                <TableRow key={b.id} className="cursor-pointer hover:bg-muted/30">
                  <TableCell className="font-mono">
                    <Link to={`/builds/${b.id}`}>{b.seq}</Link>
                  </TableCell>
                  <TableCell>
                    <Link to={`/projects/${b.project_slug}`} className="font-mono text-sm">
                      {b.project_slug}
                    </Link>
                  </TableCell>
                  <TableCell>{formatUnix(b.started_unix_ns)}</TableCell>
                  <TableCell>{formatNs(b.duration_ns)}</TableCell>
                  <TableCell>
                    <Badge variant={b.outcome === 'success' ? 'default' : 'destructive'}>
                      {b.outcome}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    {b.targets_total - b.targets_failed}/{b.targets_total}
                  </TableCell>
                  <TableCell className="text-muted-foreground">{b.ci_vendor ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
