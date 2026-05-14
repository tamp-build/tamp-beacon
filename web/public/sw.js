// tamp-beacon service worker. Single responsibility: handle Web Push
// events and surface them as system notifications. The dashboard itself
// is a regular SPA; we don't intercept fetch() calls.

self.addEventListener('install', (event) => {
  // Skip waiting so an updated worker takes over immediately rather
  // than waiting for tab close.
  event.waitUntil(self.skipWaiting());
});

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener('push', (event) => {
  if (!event.data) return;
  let payload;
  try {
    payload = event.data.json();
  } catch {
    payload = { title: 'tamp-beacon', body: event.data.text() };
  }

  const title = payload.title || 'tamp-beacon';
  const options = {
    body: payload.body || '',
    icon: '/icon-192.png',
    badge: '/icon-72.png',
    data: { url: payload.url || '/' },
    tag: payload.projectName ? `tamp-beacon:${payload.projectName}` : undefined,
    renotify: true,
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/';
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      // If we already have an open dashboard tab, focus it and route there.
      for (const client of clients) {
        if ('focus' in client && client.url.includes(self.location.origin)) {
          client.focus();
          if ('navigate' in client) client.navigate(url);
          return;
        }
      }
      // Otherwise open a new tab.
      if (self.clients.openWindow) return self.clients.openWindow(url);
    }),
  );
});
