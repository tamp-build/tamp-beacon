import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Bell, BellOff } from 'lucide-react';
import { api } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export default function AlertsPage() {
  const [supported] = useState(() => 'serviceWorker' in navigator && 'PushManager' in window);
  const [permission, setPermission] = useState<NotificationPermission>(
    supported ? Notification.permission : 'denied',
  );
  const [subscribed, setSubscribed] = useState(false);
  const [projectFilter, setProjectFilter] = useState('');
  const [areaFilter, setAreaFilter] = useState('');
  const [error, setError] = useState<string | null>(null);

  const health = useQuery({ queryKey: ['health'], queryFn: api.getHealth, refetchInterval: 30000 });

  useEffect(() => {
    if (!supported) return;
    void navigator.serviceWorker.ready.then(async (reg) => {
      const sub = await reg.pushManager.getSubscription();
      setSubscribed(!!sub);
    });
  }, [supported]);

  async function subscribe() {
    setError(null);
    try {
      if (!supported || !health.data?.vapid_public_key) {
        setError('Web Push is unavailable in this browser or the beacon has not published a VAPID key.');
        return;
      }
      const perm = await Notification.requestPermission();
      setPermission(perm);
      if (perm !== 'granted') return;

      const reg = await navigator.serviceWorker.ready;
      const sub = await reg.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(health.data.vapid_public_key).buffer as ArrayBuffer,
      });

      const raw = sub.toJSON() as { endpoint?: string; keys?: { p256dh?: string; auth?: string } };
      await api.subscribePush({
        endpoint: raw.endpoint ?? '',
        keys: { p256dh: raw.keys?.p256dh ?? '', auth: raw.keys?.auth ?? '' },
        project_filter: projectFilter || undefined,
        area_filter: areaFilter || undefined,
      });
      setSubscribed(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function unsubscribe() {
    setError(null);
    try {
      const reg = await navigator.serviceWorker.ready;
      const sub = await reg.pushManager.getSubscription();
      if (sub) await sub.unsubscribe();
      setSubscribed(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <Card className="max-w-2xl">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Bell className="h-5 w-5" /> Web Push alerts
        </CardTitle>
        <CardDescription>
          Get a notification on this device when a build fails. Filters are AND-ed; leave blank to receive all failures.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!supported && (
          <p className="text-sm text-destructive">
            Web Push isn't available in this browser. (Most Safari versions, plain-HTTP non-localhost.)
          </p>
        )}
        <div className="grid sm:grid-cols-2 gap-3">
          <Input
            placeholder="Project filter (e.g. HoldFast)"
            value={projectFilter}
            onChange={(e) => setProjectFilter(e.target.value)}
            disabled={subscribed}
          />
          <Input
            placeholder="Area filter (optional)"
            value={areaFilter}
            onChange={(e) => setAreaFilter(e.target.value)}
            disabled={subscribed}
          />
        </div>
        <div className="flex items-center gap-3 text-sm">
          <span className="text-muted-foreground">Permission:</span>
          <code className="rounded bg-muted px-2 py-0.5">{permission}</code>
          <span className="text-muted-foreground">Status:</span>
          <code className="rounded bg-muted px-2 py-0.5">{subscribed ? 'subscribed' : 'inactive'}</code>
        </div>
        <div className="flex gap-2">
          {!subscribed ? (
            <Button onClick={subscribe} disabled={!supported}><Bell className="h-4 w-4" /> Subscribe</Button>
          ) : (
            <Button variant="destructive" onClick={unsubscribe}><BellOff className="h-4 w-4" /> Unsubscribe</Button>
          )}
        </div>
        {error && <p className="text-sm text-destructive">{error}</p>}
      </CardContent>
    </Card>
  );
}

function urlBase64ToUint8Array(base64String: string): Uint8Array {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(base64);
  const output = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) output[i] = raw.charCodeAt(i);
  return output;
}
