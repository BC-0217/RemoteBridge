# Server setup / 서버 주인 설정 안내

This is a **manual preview deployment guide**, not a one-click installer. Examples use `remote.example.com:8443`; replace it with a hostname/IP you control. The examples were checked against the current configuration schema, but a fresh-server installation following every command has not been executed as part of this release.

## 1. Understand the names and secrets

| UI field / file | Meaning and source |
|---|---|
| 서버 주소 | Your actual HTTPS endpoint, such as `https://remote.example.com:8443`. The server owner obtains it from their server/domain setup. |
| 인증서 지문 / SHA-256 | SHA-256 of the **leaf certificate DER**, calculated on that server. The server owner shares it via a separately trusted channel. It is public, not a password. |
| 서버 관리키 | A random32–512-character printable ASCII owner secret configured privately at installation. It is not your AWS password, SSH key, server PW or PC PW. Only the server owner keeps it. |
| 표시 이름 | A human-readable label. It is not necessarily the server ID guests enter. |
| 서버 ID / 서버 PW | The group name/password created by the authenticated server owner. Guests use these to join the group. Server ID is normalized lowercase ASCII3–32 characters; PW is8–128 characters. |
| PC ID / 임시·고정 PW | Credentials for a particular registered PC, not for creating/joining the server. |
| 등록 장치 | PCs already enrolled in this server. Removing one invalidates its credentials and related sessions; do not remove an unknown entry casually. |
| 게시 승인 코드 | A registry-issued, bound, one-use publication grant for publishing another server into that registry. It comes from that registry's owner, not AWS. Do not invent or reuse one. |
| JSON / 공개 프로필 | A structured text representation of URL/pin/name. It is public configuration, not proof that the supplied server is trusted. |

For a same-process owner+directory configuration with `allowSelfPublish:true`, an authenticated owner can create its own server ID/PW without a separate publication grant. Another independently operated registry still controls its own publication approval.

## 2. Prerequisites and network

- Linux x64 example: install the **.NET10 ASP.NET Core runtime**, OpenSSL, Python3 and a service account.
- Download the Linux server ZIP from Releases, verify SHA256SUMS, and extract its files into `/opt/peerdesk/current`.
- Put the dotnet executable at `/opt/peerdesk/runtime/dotnet`, or edit the example service unit to the actual installed dotnet path.
- Use a DNS name or fixed IP you control, a matching RSA TLS certificate/private key, and a firewall/cloud rule for your selected TCP port.
- Do not open SSH or unrelated ports for this application. Keep existing VPN/services untouched.
- Keep small explicit device/session caps initially. A512MB trial is not a long-term capacity certification.
- If your cloud address may change, attach a stable address/use managed DNS as appropriate before distributing pins/profiles. This app does not manage AWS IP allocation.

Example directory layout (**new installation only**; do not overwrite an existing deployment):

```sh
# Create the peerdesk account if it does not already exist, using your OS administration tools.
sudo install -d -o peerdesk -g peerdesk -m 700 /etc/peerdesk
sudo install -d -o peerdesk -g peerdesk -m 700 /var/lib/peerdesk
sudo install -d -o peerdesk -g peerdesk -m 700 /var/lib/peerdesk/device-registry
# Install the downloaded server binaries in /opt/peerdesk/current as root-owned read-only files.
```

The service account needs read access to its TLS/config files and write access to its private state directories, not to the application binaries.

## 3. Prepare TLS and obtain the fingerprint

Use your certificate workflow. Owner publication currently requires an **RSA certificate with its private key**. DNS/IP Subject Alternative Names must match the endpoint.

Example self-signed certificate for a **test DNS name you control**:

```sh
umask 077
openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes \
  -keyout server.key -out server.crt \
  -subj "/CN=remote.example.com" -addext "subjectAltName=DNS:remote.example.com"
openssl pkcs12 -export -out server.pfx -inkey server.key -in server.crt
# This command prompts for a PFX export password; keep it private.
openssl x509 -in server.crt -outform DER | openssl dgst -sha256
```

For an IP endpoint use an appropriate `IP:...` SAN instead. Keep the private key/PFX restricted; install the PFX so only the service owner can read it. The exact64-hex fingerprint is shared with the clients. A self-signed certificate is not trusted merely by being self-signed: exact pin, hostname and validity are still checked. Renewal changes the leaf pin and needs an explicit trusted client/configuration update.

## 4. Create private owner/directory configuration

Create files outside this checkout with mode0600; directory mode0700. The following are **templates**, not ready-to-run credentials. Replace placeholders locally and never commit filled files.

Generate the owner secret privately, for example with `secrets.token_hex(32)` in Python. Do not put it into shell history, screenshots, public issues or this repository. It must be32–512 printable ASCII characters without whitespace. Save it in the private owner config and in your own secure storage.

`/etc/peerdesk/owner-config.json`:

```json
{
  "version": 1,
  "ownerSecret": "REPLACE_WITH_YOUR_NEW_RANDOM_PRIVATE_OWNER_SECRET",
  "statePath": "/var/lib/peerdesk/owner-state.json",
  "publicProfileJson": "{\"Version\":1,\"Name\":\"My RemoteBridge server\",\"RendezvousUrl\":\"https://remote.example.com:8443\",\"RelayUrl\":\"https://remote.example.com:8443\",\"CertificateSha256\":\"REPLACE_WITH_EXACT_64_HEX_LEAF_CERTIFICATE_SHA256\"}",
  "registryIdentity": "my-remotebridge-registry-v1",
  "allowSelfPublish": true
}
```

`/etc/peerdesk/directory-config.json`:

```json
{
  "version": 1,
  "registryIdentity": "my-remotebridge-registry-v1",
  "statePath": "/var/lib/peerdesk/directory-state.json",
  "grants": []
}
```

Both identities must match. With self-publishing enabled, the profile's pin must exactly match this process's current TLS certificate. `RelayUrl` is currently reserved; use the same endpoint.

Create `/etc/peerdesk/server.env` privately, replacing each placeholder:

```dotenv
PEERDESK_BIND_ADDRESS=0.0.0.0
PEERDESK_PORT=8443
PEERDESK_ALLOW_PUBLIC_BIND=true
PEERDESK_MAX_DEVICES=20
PEERDESK_MAX_SESSIONS=2
PEERDESK_SESSION_TTL_SECONDS=3600
PEERDESK_DEVICE_STATE_PATH=/var/lib/peerdesk/device-registry/devices-state.json
PEERDESK_CERT_PATH=/etc/peerdesk/server.pfx
PEERDESK_CERT_PASS=REPLACE_WITH_YOUR_PRIVATE_PFX_EXPORT_PASSWORD
PEERDESK_INVITATION=REPLACE_WITH_ANOTHER_RANDOM_PRIVATE_VALUE_AT_LEAST_16_CHARACTERS
PEERDESK_OWNER_CONFIG=/etc/peerdesk/owner-config.json
PEERDESK_DIRECTORY_CONFIG=/etc/peerdesk/directory-config.json
```

Current startup requires `PEERDESK_INVITATION` even with owner mode configured. Generate a distinct random value; in owner mode enrollment is governed by the owner-configured join PW, not a public default invitation. Owner mode rejects enrollment until the owner has configured a server PW.

Loopback with an automatically selected port remains the default when public binding is not explicitly enabled. Present-but-invalid configuration fails startup.

## 5. Initialize device state exactly once

For a genuinely new, missing device-state file:

```sh
sudo -u peerdesk /opt/peerdesk/runtime/dotnet /opt/peerdesk/current/PeerDesk.Server.dll \
  --initialize-device-state /var/lib/peerdesk/device-registry/devices-state.json
```

The initializer refuses existing files/directories. **Never reinitialize a working server to fix an error**: restore a trusted backup instead. Missing/corrupt/unwritable production state fails closed so IDs are not silently reset.

Owner/directory state may initially be missing and starts empty; device state has the explicit initializer above.

## 6. Start and verify

Review [deploy/peerdesk.service](../deploy/peerdesk.service) and adjust runtime/application paths. Install it as a new service only after preserving any existing configuration.

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now peerdesk
sudo systemctl status peerdesk --no-pager
# Use the public PEM certificate as the CA only for a self-signed test certificate:
curl --cacert server.crt https://remote.example.com:8443/healthz
```

Health success is `{"status":"ok"}`; it is not a capacity/security certification. Never use `curl -k` as proof that certificate trust is correct.

## 7. Configure the Windows server-owner page

**Prerequisite:** before creating the server ID/PW, complete the registry-bootstrap configuration in section8 on the **server owner's own Windows PC too**, then close and restart the app. Return here afterward. The client intentionally blocks saving a self-hosted server ID when its configured registry URL/pin still points to the different built-in trial registry.

1. Open **서버 설정 → 서버 만들기**.
2. Enter this server's HTTPS address and the separately verified fingerprint.
3. Use **서버 연결 확인**.
4. Enter the **ownerSecret from your private owner-config.json** into **서버 관리키** and authenticate.
5. In **서버 ID/PW**, choose a server ID and server PW, then save. Remember these securely; the server cannot return the original PW from its verifier.
6. If using the same-process trusted directory configured above, self-publication needs no separate one-use approval code. A foreign/central registry requires that registry owner's approval workflow.
7. Enroll the creator PC too if it will participate in remote sessions.

An error401 normally means authentication was rejected. Do not respond by changing random keys or resetting server state. Check that the key belongs to the exact endpoint/pin and is the **owner key**, not a participant/PC password.

## 8. Give guests the correct registry configuration

A server ID is a name, not a routable address. Name+PW-only joining needs a common trusted name-lookup registry. The EXE's embedded trial registry does not automatically discover arbitrary self-hosted servers.

For your own registry, distribute a trusted `directory-bootstrap.json` alongside the EXE to all participants:

```json
{
  "RegistryUrl": "https://remote.example.com:8443",
  "CertificateSha256": "REPLACE_WITH_EXACT_64_HEX_LEAF_CERTIFICATE_SHA256",
  "RegistryIdentity": "my-remotebridge-registry-v1"
}
```

Install this file on the **server owner's PC before section7**, as well as on every participant PC. Close and restart the application after placing or changing it; bootstrap is loaded at startup. Only these three fields are accepted. The file takes precedence over the embedded trial anchor. A malformed/wrong-pin sidecar **fails closed**, never silently falls back. All clients must trust the correct registry and the owner must have published the ID there.

For single-EXE distribution with your own anchor, deliberately change the public embedded JSON and rebuild/test the client. Do **not** embed owner keys, server PWs or device credentials. Advanced explicit-profile registration is available but does not make central registry publication automatic.

Guests then enter only the **server ID/PW** in **서버 참가**, and use peer **PC IDs/PWs** for actual remote connections.

## 9. Persistence, backup and upgrade

- Back up private owner config/state, directory config/state, TLS material, environment file and device registry together. Never publish these backups.
- Same Windows account settings plus retained server device state preserve a registered PC ID across restart. This is not MAC/SMBIOS-derived identity or reinstall recovery.
- Device registry v2 adds fixed-PW verifier/revision and accepts v1 migration. An old v1-only server cannot read v2.
- Rollback needs the matching binary/config/private state snapshot. Restoring an older snapshot after legitimate changes can lose newer configuration/registrations.
- Sessions/runtime authority do not resume after server restart. Existing apps must validate/refresh credentials.
- Use one server writer per state file. Atomic replacement does not prove physical power-loss durability or administrator anti-rollback protection.
