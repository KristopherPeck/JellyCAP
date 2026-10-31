# Security Policy

## Supported versions

JellyCAP is currently beta software. Security fixes are provided for the latest `0.4.x` beta release only. Builds older than `0.4.0.0` are unsupported.

## Reporting a vulnerability

Please report suspected vulnerabilities privately through [GitHub Security Advisories](https://github.com/KristopherPeck/JellyCAP/security/advisories/new). Do not open a public issue for a vulnerability that may expose users or their data.

Include, when available:

- The JellyCAP and Jellyfin versions
- Operating system and installation method
- A minimal reproduction and the expected security boundary
- The potential impact
- Sanitized Jellyfin log excerpts

Never include API keys, authorization headers, provider credentials, private endpoint credentials, or sensitive media metadata. Remove secrets from URLs and logs before submitting them.

Relevant reports include authentication or authorization bypasses, credential exposure, server-side request forgery or endpoint-validation bypasses, stored script injection, unsafe file access, and data corruption. AI output quality or hallucinations are not security vulnerabilities unless they bypass review or cause an unauthorized action. Vulnerabilities in a third-party AI service should also be reported to that provider.

We aim to acknowledge a report within seven days. Resolution time depends on severity, reproducibility, and release testing. Please allow time for a fix to be prepared before public disclosure.
