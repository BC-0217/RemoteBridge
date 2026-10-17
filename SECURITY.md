# Security and responsible use

RemoteBridge is an **early experimental preview**, not a hardened remote-access service.

## Important boundaries

- Use only on computers you own or have explicit authorization to control.
- The default-visible automatic-access setting permits someone who knows a valid PC temporary/fixed PW to view the screen and control input without a dialog while the app is running.
- A blank PC PW requests consent; a wrong nonblank PW is rejected.
- Stop sharing to terminate an existing session. Changing a PW/option is not a session disconnect.
- Pinned TLS is **not end-to-end encryption**. The server can read frames and input.
- Fingerprints must be obtained from the actual server owner through a separate trusted channel. Never trust a fingerprint merely because it arrived with an untrusted profile.
- The embedded trial registry anchor is public and does not promise free/public relay access or availability.
- Store server keys/config/state outside the public checkout with restrictive permissions. Use one writer per device-state file and tested backups.
- Do not report Windows7 certification, zero stutter/input loss, modern-codec equivalence, or production capacity from the current tests.

## Report a security issue

Never put passwords, bearer tokens, private keys, private configuration, actual screen content or exploitable private endpoint details into a public issue.

Use the repository's private vulnerability-reporting option **if it is available**. Otherwise use a separately agreed private channel with the maintainer; private reporting infrastructure is not promised by this preview. A public issue may request a private contact channel without exposing the vulnerability or secrets.

No response-time guarantee, bug-bounty program, or supported-version SLA is declared.
