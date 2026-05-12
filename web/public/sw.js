// tamp-beacon Web Push service worker. Hand-rolled so we don't need vite-plugin-pwa
// pulling its plugin chain just for this one event handler.

self.addEventListener('push', (event) => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch (e) {
    data = { title: 'tamp-beacon', body: event.data ? event.data.text() : 'A build event occurred.' };
  }

  const title = data.title || 'tamp-beacon: build failed';
  const options = {
    body: data.body || '',
    data: { url: data.url || '/' },
    badge: '/favicon.ico',
    icon: '/favicon.ico',
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const target = event.notification.data && event.notification.data.url ? event.notification.data.url : '/';
  event.waitUntil(self.clients.openWindow(target));
});
