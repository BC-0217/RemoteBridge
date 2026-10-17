# Source and dependency provenance

## Current implementation

RemoteBridge's control plane, Windows UI, JPEG relay, capture and input implementation were authored independently for this project, with AI-assisted drafting, integration review and targeted tests. AI-generated output is not proof of correctness or blanket license compliance.

The source tree currently uses .NET/.NET Framework framework assemblies and internal project references, without third-party NuGet package references.

**No RustDesk source file, binary, asset, library or integrated engine has been copied into this preview.** Similar remote-access UI conventions do not mean this is a RustDesk fork or compatible RustDesk client/server.

## Reviewed reference

On2026-10-17, the public [RustDesk peer model](https://github.com/rustdesk/rustdesk/blob/master/flutter/lib/models/peer_model.dart) was read as a peer-ID/metadata/list UX reference. RemoteBridge's successful-recent-peer upsert/storage is independently implemented. This is not an audit of every RustDesk history implementation or a selected upstream import revision.

Before any future source copying or linking, record the exact upstream repository/commit/files, applicable LICENSE/NOTICE, modifications and transitive licenses, and decide the complete distribution/source-offer obligations. Process separation alone is not a license exemption.

## Project license status

No project license has been selected for the initial public preview. Public repository visibility must not be described as an MIT/AGPL grant or as proof that an open-source license has been adopted. Framework/dependency licenses remain separate.

## Distribution boundary

Release assets must be explicit, hash-checked public binaries and generic documentation only. Do not publish developer evidence, personal settings, live owner/directory/device state, invitations, tokens, private keys, SSH material or certificate private keys. Public registry URL/pin/identity are trust configuration—not enrollment or management credentials.
