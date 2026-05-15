# Production checklist for adopter-hosted deployments

The default tamp-beacon deploy works fine on a single-operator lab cluster. Before standing the beacon up where multiple humans can see the PVC contents — or anywhere a cloud-provider control plane has snapshot access — work through this list.

## 1. Encrypt the data-protection key ring at rest (TAM-219)

ASP.NET Core's data-protection ring signs and encrypts every session cookie. By default tamp-beacon writes that ring to disk **in plaintext**. Anyone who reads the key files can forge any session cookie for any user (including the sysadmin).

If you ignore this checklist item, the beacon logs a startup warning every time it boots — that's the signal that you're shipping plaintext keys to disk.

### Option A — Symmetric key file (simplest)

Generate a 32-byte AES-256 key, drop it into a Kubernetes `Secret`, mount it at a known path, and point the beacon at it:

```bash
# 1. Generate the key
openssl rand 32 > tamp-beacon.key       # or: head -c 32 /dev/urandom > tamp-beacon.key

# 2. Drop it into a Secret
kubectl create secret generic tamp-beacon-dp-key --from-file=key=tamp-beacon.key
shred -u tamp-beacon.key                # remove the local copy
```

Then mount + configure on the pod:

```yaml
# Excerpt for deploy/kubernetes/statefulset.yaml
volumeMounts:
  - name: dp-key
    mountPath: /var/lib/tamp-beacon-secrets
    readOnly: true
env:
  - name: Beacon__Auth__KeyProtection__Mode
    value: SecretFile
  - name: Beacon__Auth__KeyProtection__SecretFilePath
    value: /var/lib/tamp-beacon-secrets/key
volumes:
  - name: dp-key
    secret:
      secretName: tamp-beacon-dp-key
```

ASP.NET Core's config provider maps `__` to `:`, so `Beacon__Auth__KeyProtection__Mode` lands on `Beacon:Auth:KeyProtection:Mode`. Same effect as setting it in `appsettings.json`.

**Rotating the key.** Replace the file's contents and restart the pod. The data-protection ring re-encrypts each key entry on next write; in-flight cookies stay valid until the old key entries expire on schedule (90 days by default). To force-roll the cookies, also delete `/var/lib/tamp-beacon/data-protection-keys/*.xml` — every session signs out and re-authenticates on next request.

### Option B — X.509 certificate (cert-manager users)

If you already run cert-manager or a similar PKI, point the beacon at a PFX:

```yaml
env:
  - name: Beacon__Auth__KeyProtection__Mode
    value: X509
  - name: Beacon__Auth__KeyProtection__X509CertPath
    value: /var/lib/tamp-beacon-secrets/dp.pfx
  - name: Beacon__Auth__KeyProtection__X509CertPassword
    valueFrom:
      secretKeyRef:
        name: tamp-beacon-dp-cert
        key: password
```

The PFX **must** contain the private key (encryption uses the public half, decryption needs the private half). Self-signed is fine — the cert is never validated against a CA, it's just a key pair.

### Verifying it's wired

After applying the change and restarting the pod:

```bash
kubectl logs statefulset/tamp-beacon | grep -i "PLAINTEXT"
# (no output = warning is gone = encryption is wired)

kubectl exec -it tamp-beacon-0 -- cat /var/lib/tamp-beacon/data-protection-keys/key-*.xml | head -50
# should contain <encryptedKey kind="tamp-beacon-aes-gcm-v1"> (SecretFile mode)
# or <encryptedKey> with a certificate thumbprint (X509 mode)
# NEVER <masterKey><value>...</value></masterKey> — that's the plaintext shape
```

## 2. Run behind HTTPS

Browsers won't subscribe a service worker to push notifications over plain HTTP. The cookie is also marked `Secure` whenever the request arrives over TLS (per `CookieSecurePolicy.SameAsRequest`), which means a plain-HTTP deploy ships cookies that get rejected by HTTPS terminators downstream.

Terminate TLS at the ingress (cert-manager + Let's Encrypt is the common path); see [`deploy/kubernetes/ingress.yaml`](../deploy/kubernetes/ingress.yaml).

## 3. Decide on external Postgres before first boot

The bundled Postgres is convenient for inner-loop verification and small single-host deploys. For anything where you want point-in-time recovery, cross-AZ replicas, or operator-grade backup tooling, point the beacon at an external Postgres via `BEACON_DB_CONNECTION_STRING` **before** the first user signs in. After the schema has migrations, content, and a sysadmin row, switching storage backends requires a `pg_dump` + restore migration.

## 4. Restrict who can read the PVC

Even with the key ring encrypted, the PVC carries the setup token (until consumed), the VAPID key (push-notification signing material), and the Postgres datadir. Standard control-plane discipline applies:

- Snapshot the PVC for backups, encrypt the snapshots at rest
- Limit `kubectl exec` / debug-shell access to the namespace
- Audit who has `get pods/log` rights (the setup token still prints to stdout on first boot)

## 5. Provision an out-of-band recovery handle

The first sysadmin is bootstrapped from the setup token printed to stdout at first boot. If you lose that admin's credentials AND your GitHub OAuth path goes down at the same time, the recovery handle is:

```bash
kubectl exec -it tamp-beacon-0 -- tamp-beacon admin recover --username scott
# prints a one-shot reset token (TTL 1h by default)
```

Confirm this works before you need it.

## 6. Pin an immutable image tag

In production, pin `ghcr.io/tamp-build/tamp-beacon:0.1.0` rather than `:latest` or `:0.1`. The floating tags are convenient for trunk users but a silent patch bump on a production deploy is the wrong default for a security-relevant service. See the [README's tag scheme table](../README.md#image-tag-scheme).
