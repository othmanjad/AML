#!/usr/bin/env bash
# Creates .env for docker compose with random secrets (run from the repository root):
#   ./deploy/docker/generate-env.sh http://SERVER:8080 [--no-demo] [--force]
# The file is written next to docker-compose.yml and is never committed. Keep a secure copy of the keys.
set -euo pipefail
PUBLIC_URL="${1:-http://localhost:8080}"
DEMO=true; FORCE=false
for a in "${@:2}"; do case "$a" in --no-demo) DEMO=false ;; --force) FORCE=true ;; esac; done
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TARGET="$ROOT/.env"
if [[ -f "$TARGET" && "$FORCE" != true ]]; then echo ".env already exists. Use --force to overwrite (existing encrypted data needs the old keys)." >&2; exit 1; fi
key() { openssl rand -base64 "${1:-32}" | tr -d '\n'; }
# Password with upper, lower, digit and symbol (SQL Server complexity rules).
pass() { local n="${1:-20}"; echo "$(openssl rand -base64 48 | tr -dc 'A-HJ-NP-Za-km-z2-9' | head -c "$((n-4))")Aa7_"; }
PORT="$(echo "$PUBLIC_URL" | sed -nE 's#.*:([0-9]+)/?$#\1#p')"; PORT="${PORT:-8080}"
umask 077
cat > "$TARGET" <<ENV
PUBLIC_URL=$PUBLIC_URL
WEB_PORT=$PORT
ASPNETCORE_ENVIRONMENT=Staging
SQL_SA_PASSWORD=$(pass 24)
SQL_DATABASE=AmlDb
MSSQL_PID=Developer
SECURITY_ENCRYPTION_KEY=$(key)
SECURITY_SEARCH_TOKEN_KEY=$(key)
SECURITY_INTEGRITY_KEY=$(key)
SECURITY_ENCRYPTION_KEY_ID=k1
AUTH_SIGNING_KEY=$(key 48)
AUTH_AUTHORITY=
DEVAUTH_ENABLED=true
DEVAUTH_SEED_PASSWORD=$(pass 16)
INTEGRATION_CLIENT_SECRET=$(pass 32)
SEED_DEMO_DATA=$DEMO
HTTPS_REDIRECT=false
SWAGGER_ENABLED=true
ENV
echo "Created $TARGET (mode 600). Test users' password is DEVAUTH_SEED_PASSWORD in that file - store it securely."
