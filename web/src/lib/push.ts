// Browser Web Push helpers. The dashboard calls into pushManager to
// register a subscription, then POSTs it to the beacon via api.ts.

import { api } from './api';

export type PushStatus =
  | { kind: 'unsupported' }
  | { kind: 'denied' }
  | { kind: 'not_subscribed' }
  | { kind: 'subscribed'; endpoint: string };

export async function detectPushStatus(): Promise<PushStatus> {
  if (typeof window === 'undefined') return { kind: 'unsupported' };
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
    return { kind: 'unsupported' };
  }
  if (Notification.permission === 'denied') {
    return { kind: 'denied' };
  }
  try {
    const reg = await navigator.serviceWorker.ready;
    const sub = await reg.pushManager.getSubscription();
    if (sub) return { kind: 'subscribed', endpoint: sub.endpoint };
    return { kind: 'not_subscribed' };
  } catch {
    return { kind: 'unsupported' };
  }
}

export async function ensureServiceWorker(): Promise<ServiceWorkerRegistration | null> {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return null;
  try {
    return await navigator.serviceWorker.register('/sw.js');
  } catch {
    return null;
  }
}

export async function subscribeToProject(slug: string): Promise<PushStatus> {
  const reg = await ensureServiceWorker();
  if (!reg) return { kind: 'unsupported' };

  if (Notification.permission !== 'granted') {
    const result = await Notification.requestPermission();
    if (result !== 'granted') {
      return result === 'denied' ? { kind: 'denied' } : { kind: 'not_subscribed' };
    }
  }

  const vapid = await fetch('/api/push/vapid-public-key', { credentials: 'include' }).then(
    (r) => r.json() as Promise<{ public_key: string }>,
  );
  const appServerKey = urlBase64ToUint8Array(vapid.public_key);

  // applicationServerKey requires a BufferSource (e.g. Uint8Array). Some
  // older TS lib defs don't widen that — cast for compatibility.
  const sub = await reg.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: appServerKey as unknown as BufferSource,
  });

  const json = sub.toJSON() as { endpoint: string; keys: { p256dh: string; auth: string } };
  await api.subscribePush(slug, {
    endpoint: json.endpoint,
    keys: { p256dh: json.keys.p256dh, auth: json.keys.auth },
  });
  return { kind: 'subscribed', endpoint: json.endpoint };
}

export async function unsubscribeFromProject(slug: string): Promise<PushStatus> {
  const reg = await ensureServiceWorker();
  if (!reg) return { kind: 'unsupported' };
  const sub = await reg.pushManager.getSubscription();
  if (!sub) return { kind: 'not_subscribed' };
  try {
    await api.unsubscribePush(slug, { endpoint: sub.endpoint });
  } catch {
    // Continue with browser-side unsubscribe even if the server call fails;
    // the server-side row is the source of truth for delivery but a stale
    // server-only row will eventually 410 itself out.
  }
  await sub.unsubscribe();
  return { kind: 'not_subscribed' };
}

function urlBase64ToUint8Array(base64: string): Uint8Array {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const padded = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(padded);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

/**
 * Map a thrown push-subscription error to a user-actionable sentence. Most
 * `pushManager.subscribe()` failures bubble up as DOMException instances —
 * their `.name` is more reliable than their message (which varies by
 * browser + locale). When the cause is configurable in the browser (Brave's
 * default-off Google push services, denied notification permission), we
 * point the user at the toggle. For truly opaque cases we fall back to the
 * raw exception text.
 */
export function formatPushError(err: unknown): string {
  if (typeof err === 'object' && err !== null) {
    const name = (err as { name?: string }).name;
    switch (name) {
      case 'AbortError':
        // Most common cause: Brave (and forks) ship with
        // "Use Google services for push messaging" OFF. Chromium without
        // the FCM bridge means subscribe() can never reach a push service.
        // Same surface error fires on flaky network / push service outage
        // — frame the message as a likely cause + a thing-to-check.
        return (
          'The browser couldn\'t register with its push service. ' +
          'If you\'re on Brave or a Brave fork, enable ' +
          '"Use Google services for push messaging" in ' +
          'brave://settings/privacy (a Brave default-off privacy setting). ' +
          'Chrome / Edge / Firefox work without any toggle.'
        );
      case 'NotAllowedError':
        return (
          'Notification permission was denied. Unblock the site under ' +
          'the address-bar lock icon, then click Enable again.'
        );
      case 'NotSupportedError':
        return (
          'This browser does not support Web Push. Use a recent Chrome, ' +
          'Edge, Firefox, or Brave (with Google push services enabled).'
        );
      case 'InvalidStateError':
        return (
          'An old subscription is still attached to this browser. ' +
          'Try Disable, then Enable again.'
        );
      case 'SecurityError':
        return (
          'Web Push requires HTTPS (or localhost). The current origin ' +
          'isn\'t a secure context.'
        );
    }
    const msg = (err as { message?: string }).message;
    if (typeof msg === 'string' && msg.length > 0) return msg;
  }
  return String(err);
}
