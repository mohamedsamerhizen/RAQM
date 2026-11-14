# Release verification

Local verification passed 219 offline cases and 227 integration cases on disposable PostgreSQL 17, with zero failures or skips. The Release build reported zero warnings and errors, and EF reported no pending model changes. Verification replayed all 22 forward migrations and exercised populated upgrades. The precision regression covers the rejected fractional cancellation, valid final shipment and financial close. Use the [README](../README.md) commands to repeat these checks.

Deployed TLS and egress, revocation, worker replacement, alerting, restore and production capacity require operational acceptance. See [supported limits](release-readiness/KNOWN_LIMITATIONS.md).
