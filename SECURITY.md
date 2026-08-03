# Security Policy

## Backup Packages

Backup packages can contain conversation history, source code, filenames, machine names, usernames, and private workspace data. They are not suitable for GitHub issues, public cloud links, or source repositories.

Codex Shuttle excludes known credential files by default, including `auth.json`, `.env`, key files, and `.sandbox-secrets`. This is defense in depth, not a guarantee that a third-party plugin never stores a secret under another filename. Use an encrypted removable drive for sensitive migrations.

## Reporting a Vulnerability

Do not attach a real backup package, credential file, or conversation database to a public report. Reproduce the issue with synthetic files and remove usernames, paths, tokens, customer information, and proprietary source code from logs.
