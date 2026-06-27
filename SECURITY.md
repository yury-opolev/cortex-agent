# Security Policy

## Reporting a vulnerability

Please report security issues privately via GitHub Security Advisories
(repository → Security → Report a vulnerability), or by email to the maintainer.
Do not open public issues for security-sensitive reports.

## Scope & caveats

cortex-agent is an autonomous coding/assistant agent that can **execute commands**
and edit files. It ships permission controls (default / acceptEdits / plan /
bypass modes), a sandbox scoped to the working directory, and an optional safety
classifier — but a **bypass ("yolo") mode** that runs actions without prompting is
available. Run untrusted tasks only in isolated environments. Credentials are
stored via OS-level encryption (Windows DPAPI), never in the repository.
