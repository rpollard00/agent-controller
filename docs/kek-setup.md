# KEK Setup Guide

The Key Encryption Key (KEK) is a 256-bit master key used to encrypt/decrypt per-secret Data Encryption Keys (DEKs) in the envelope encryption scheme. The KEK is **never stored in the database** — it is sourced from an external secure location at startup.

If the KEK is missing or invalid when the controller starts, the application fails fast with a clear error message. It will not start without a valid KEK.

## What Is the KEK?

In the secrets system's envelope encryption model:

- Each secret value is encrypted with a random **DEK** (Data Encryption Key).
- The DEK is itself encrypted (wrapped) by the **KEK** (Key Encryption Key).
- The database stores the encrypted value and the wrapped DEK — never the KEK itself.

The KEK is a 32-byte (256-bit) binary key used with AES-256-GCM.

## Provisioning the KEK

### Option 1: File-Based KEK (Recommended for Development and Simple Deployments)

The simplest approach is to store the KEK in a file on disk and point the controller to it.

#### Step 1: Generate the KEK

Generate a random 32-byte key:

```bash
# Using OpenSSL
openssl rand 32 > /path/to/kek.key

# Using dd and /dev/urandom
dd if=/dev/urandom of=/path/to/kek.key bs=32 count=1

# Using PowerShell
[System.IO.File]::WriteAllBytes("/path/to/kek.key", (New-Object byte[] 32 | % { [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($_) }))
```

#### Step 2: Secure the File

Restrict file permissions so only the application user can read it:

```bash
chmod 600 /path/to/kek.key
chown appuser:appgroup /path/to/kek.key
```

#### Step 3: Configure the Controller

Set the KEK file path via one of these methods:

**Environment variable:**

```bash
export AGENT_CONTROLLER_SECRET_KEK_FILE_PATH=/path/to/kek.key
```

**appsettings.json:**

```json
{
  "secrets": {
    "provider": "Db",
    "keyEncryptionKey": {
      "file": {
        "filePath": "/path/to/kek.key"
      }
    }
  }
}
```

The environment variable takes precedence over appsettings.

### Option 2: systemd-creds (Recommended for Linux Services)

On supported Linux systems, `systemd-creds` can store the KEK in encrypted credentials that are only accessible to the service unit.

#### Step 1: Generate and Store the KEK

```bash
# Generate a random 32-byte key and store it as a systemd credential
systemd-creds encrypt --name=agent-controller-kek /dev/urandom /path/to/kek.creds <<<'$(head -c 32 /dev/urandom | base64)'
```

Or use `systemd-creds` with a credential file:

```bash
# Generate the key
openssl rand 32 > /tmp/kek-plaintext.key

# Encrypt it as a systemd credential
systemd-creds encrypt /tmp/kek-plaintext.key agent-controller-kek /path/to/kek.creds

# Clean up the plaintext
shred -u /tmp/kek-plaintext.key
```

#### Step 2: Configure the systemd Service

Add the credential to the service unit:

```ini
[Service]
Credentials=agent-controller-kek=/path/to/kek.creds
# The decrypted credential is available at:
# /run/credentials/<service-name>/agent-controller-kek
```

Then configure the controller to read from the credentials path:

```json
{
  "secrets": {
    "keyEncryptionKey": {
      "file": {
        "filePath": "/run/credentials/agent-controller.service/agent-controller-kek"
      }
    }
  }
}
```

Or set the environment variable in the service unit:

```ini
[Service]
Environment=AGENT_CONTROLLER_SECRET_KEK_FILE_PATH=/run/credentials/agent-controller.service/agent-controller-kek
```

#### Step 3: Reload and Restart

```bash
sudo systemctl daemon-reload
sudo systemctl restart agent-controller.service
```

### Option 3: Environment Variable (Not Recommended for Production)

For development only, you can embed the KEK directly in an environment variable (base64-encoded):

```bash
# Generate and encode
export AGENT_CONTROLLER_SECRET_KEK_BASE64=$(openssl rand 32 | base64)
```

> **Warning:** Storing the KEK in an environment variable exposes it in process listings and logs. Use only for local development.

## Fail-Fast Behavior

The controller validates the KEK at startup and fails fast if it is unavailable:

### Missing KEK File Path

If neither `secrets:keyEncryptionKey:file:filePath` nor `AGENT_CONTROLLER_SECRET_KEK_FILE_PATH` is configured:

```
InvalidOperationException: Secret provider 'Db' requires a KEK file path.
Configure 'secrets:keyEncryptionKey:file:filePath' in appsettings or set the
AGENT_CONTROLLER_SECRET_KEK_FILE_PATH environment variable.
The KEK file must contain exactly 32 bytes of binary data.
```

### Missing or Unreadable KEK File

If the configured file path does not exist or cannot be read:

```
InvalidOperationException: KEK file not found at '/path/to/kek.key'.
The secret provider cannot start without a valid KEK.
See the KEK setup guide for provisioning instructions.
```

### Invalid KEK Size

If the KEK file does not contain exactly 32 bytes:

```
InvalidOperationException: KEK file at '/path/to/kek.key' must contain exactly 32 bytes.
Got N bytes.
```

In all cases, the application **will not start**. There is no fallback to plaintext storage.

## KEK Rotation

KEK rotation is not yet implemented. When rotation support is added, all existing DEKs will need to be re-wrapped with the new KEK. Until then, treat the KEK as a long-lived master key and protect it accordingly.

## Backup and Recovery

- **Back up the KEK file** using your organization's key management procedures. Without the KEK, all encrypted secrets are unrecoverable.
- The KEK file is a small binary file (32 bytes) — store it in a secure backup location separate from the database.
- When restoring from a database backup, ensure the same KEK is available. A different KEK will not be able to decrypt the existing secret values.

## Related Documentation

- [Secrets Management](./secrets.md) — Architecture overview of the secrets system.
- [Boards Setup](./boards-setup.md) — §2 (PAT as a named, versioned secret).
