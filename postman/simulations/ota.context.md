# Blackwall OTA simulation

Created 2026-10-25 from the local WPF signed-distribution contract. No owner lookup or cloud mock was needed: this repository owns the new protocol and the fixtures are generated locally with disposable keys.

## Purpose

Exercise the real `OtaDownload` and `OtaProtocol` code against the `ota` member. Handler: `postman/mocks/ota/default.js`; protocol: `OTA-ACTUALIZACIONES.md`.

## Execution

Run `scripts/Test-Ota.ps1` first to compile the native checker, then `node scripts/Test-OtaNetwork.mjs`. The runner prepares ephemeral keys/certificates, starts `ota.sim.yaml` with `--no-history`, reads its actual address, runs the C# client through a loopback TLS relay, checks request evidence and stops its own simulation tree. Evidence is kept under `artifacts/ota-network/`.

## Assumptions

- Postman CLI 1.75.0 requires the simulation member path to name `config.yaml`, not the directory. OpenSSL bundled with Git for Windows generates the local test certificate. (observed 2026-10-25)
- The standalone Framework checker must select TLS 1.2 just as the application entrypoint does. The test pins only its ephemeral certificate in its own process; production TLS validation is retained. (observed 2026-10-25)
- Aborted and timed-out responses may not produce completed Postman request events. The fixture also records request starts, checked via `/evidence`, including `/truncated` and `/slow`. Six loop requests prove the redirect bound. This does not establish GitHub availability or redirect behavior. (observed 2026-10-25)

## Observations

<!-- begin:observations -->
2026-10-25: `ota` received the signed manifest, binary and all fault scenarios; no member was silent. Native checks passed for certificate rejection, signature, real byte progress, size bounds, interruption, HTTP 500, downgrade rejection, redirect exhaustion and the configured timeout.
<!-- end:observations -->

## What didn't work

- 2026-10-25: a member directory produced `Mock 'file' must point to a file`; fixed by referencing its config file.
- 2026-10-25: the first standalone Framework transport attempt reported inability to create an SSL/TLS channel before reaching the member; fixed by matching the application's TLS 1.2 initialization.
