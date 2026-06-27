# MSIX Code Signing

The Cortex Launcher ships as a **signed MSIX** package. This document explains the
signing identity, how the build resolves the certificate, and the renewal procedure.

## Signing identity (do not change)

| Property | Value |
|----------|-------|
| Subject / Issuer (CN) | `CN=B4C4AA96-F301-4E3B-AA5D-25A99E1D356D` |
| Type | Self-signed, Code Signing EKU |
| Current thumbprint | `F578A5879BE57511D40288B6DA3A0F383BD74EEE` |
| Valid | 2026-04-06 → **2027-04-06** (1-year cert) |
| Store | `Cert:\CurrentUser\My` (private key present) |

The Subject **CN is the package identity**. It is pinned in
`src/Cortex.Contained.Launcher/Package.appxmanifest`:

```xml
<Identity Name="Cortex.Contained.Launcher"
          Publisher="CN=B4C4AA96-F301-4E3B-AA5D-25A99E1D356D" ... />
```

The signing certificate's subject **must** equal that `Publisher` value. If you sign
with a different subject, Windows treats it as a **different package identity**: the
package family name changes and **in-place upgrades break** (users would have to
uninstall the old package first). So the CN above must stay constant forever — only
the certificate behind it (thumbprint, validity dates) may change on renewal.

## How the build finds the certificate

`scripts/Build-Launcher.ps1` (invoked by `scripts/Build-All.ps1`) resolves the
signing cert in this order:

1. **Explicit thumbprint** — `-CertThumbprint <hash>` or `$env:CORTEX_SIGNING_THUMBPRINT`.
   Exact pin; overrides everything. Useful in CI.
2. **Subject lookup** (default) — searches `Cert:\CurrentUser\My` and
   `Cert:\LocalMachine\My` for a Code Signing cert whose subject equals
   `$CertSubject` (default `CN=B4C4AA96-...`, override via `$env:CORTEX_SIGNING_SUBJECT`),
   that **has a private key** and **has not expired**, and picks the one with the
   **latest expiry**.

If nothing matches, the build fails with an actionable error rather than signing
with the wrong identity. Because lookup is by subject, a renewed certificate with the
same CN is picked up automatically — no script edit required.

## Renewal procedure (before 2027-04-06)

When the certificate is close to expiry, create a **new self-signed cert with the same
subject** and let subject lookup pick it up.

```powershell
# 1. Create a new self-signed code-signing cert with the SAME subject CN.
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=B4C4AA96-F301-4E3B-AA5D-25A99E1D356D" `
    -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears(3)        # consider a longer lifetime than 1 year

# 2. Trust it locally so the signed MSIX installs (sideload trust).
#    Export the public cert and import into Trusted People (or Trusted Root).
$pub = "$env:TEMP\cortex-signing.cer"
Export-Certificate -Cert $cert -FilePath $pub | Out-Null
Import-Certificate -FilePath $pub -CertStoreLocation Cert:\LocalMachine\TrustedPeople  # needs admin

# 3. (Recommended) Back up the cert + private key so the identity is never lost.
$pfx = "$env:USERPROFILE\cortex-signing-backup.pfx"
$pwd = Read-Host "PFX password" -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null

# 4. Rebuild — subject lookup finds the new cert automatically.
.\scripts\Build-All.ps1 -SkipDocker
```

Notes:
- **Keep the subject CN identical** (`CN=B4C4AA96-...`). This is the one thing that
  must never change.
- Consider issuing the renewal with a **multi-year** validity to avoid an annual chore.
- After renewing, both the old and new certs may coexist briefly; the resolver picks
  the latest-expiring valid one, so this is safe.
- The MSIX `Publisher` in `Package.appxmanifest` does not need to change on renewal.

## Recovering the identity (machine loss / store wipe)

If the private key for `CN=B4C4AA96-...` is lost and no `.pfx` backup exists, the
identity **cannot be recreated** — a new self-signed cert with the same subject is a
*different* key, which Windows still accepts for sideload trust, but you must
re-trust it and existing installs upgrade only because the subject (Publisher)
matches. To avoid surprises, keep a password-protected `.pfx` backup of the current
key (step 3 above) somewhere safe.

## CI / other machines

On a machine without the cert, set an explicit identity rather than relying on the
local store:

```powershell
$env:CORTEX_SIGNING_THUMBPRINT = "<thumbprint>"   # exact pin, or
$env:CORTEX_SIGNING_SUBJECT    = "CN=..."         # subject lookup
```

For real distribution (beyond self-hosting), prefer keeping the private key out of
build machines entirely — sign in a pipeline using a key vault (e.g. AzureSignTool +
Azure Key Vault) and inject the identity from CI secrets.
