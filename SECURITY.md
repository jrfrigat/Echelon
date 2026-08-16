# Security Policy

## Supported versions

Release Orchestrator is pre-1.0; only the latest released version receives security fixes.

| Version | Supported |
| ------- | --------- |
| latest  | yes       |
| older   | no        |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, use GitHub's private reporting:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability** (Privately report a vulnerability).
3. Describe the issue, affected version(s), and steps to reproduce.

We aim to acknowledge a report within a few days and will keep you updated on the fix and disclosure
timeline. Please give us reasonable time to address the issue before any public disclosure.

## Hardening notes

- Provider access tokens are stored encrypted with ASP.NET Data Protection. The key ring lives in the
  same database as the tokens it protects, so the application refuses to start unless
  `DataProtection:CertificatePath` points at a PKCS#12 file - or `DataProtection:AllowUnprotectedKeys`
  says the risk is accepted deliberately. Anyone holding a database dump otherwise holds the tokens.
- Every API endpoint requires an authenticated caller and a permission policy; the fallback policy
  denies. Only the SPA shell, the health endpoints and the metrics endpoint are anonymous.
- Webhook deliveries are verified against the connection's secret before anything is read from them,
  and replays are dropped by the ingestion inbox rather than acted on twice.
- Forwarded headers are currently honoured from any peer, because naming the real proxies is a
  deployment decision. Until they are named, `X-Forwarded-For` is caller-supplied: the anonymous rate
  limit bucket can be reset per request, and the audit records the transport peer separately for
  exactly that reason.
- Keep connection strings, broker credentials and provider tokens out of source control; supply them
  through the environment or a secret store. `.env` is ignored, `.env.example` names what is needed.

Thank you for helping keep Release Orchestrator and its users safe.
