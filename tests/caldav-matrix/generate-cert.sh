#!/usr/bin/env bash
# A self-signed certificate for the local CalDAV test fixture (§13 Export, calendar UI
# verification gap noted in docs/handover.md). Generated once and left in place — this backs
# a test-only HTTPS server, never anything reachable outside 127.0.0.1, so a long validity
# period trades nothing away.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/certs"
mkdir -p "$dir"

if [[ -f "$dir/cert.pem" && -f "$dir/key.pem" ]]; then
	exit 0
fi

openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
	-keyout "$dir/key.pem" -out "$dir/cert.pem" \
	-subj "/CN=127.0.0.1" \
	-addext "subjectAltName=IP:127.0.0.1,DNS:localhost"

# The image runs as a non-root uid and chowns what it's handed at startup, but it still needs
# to be able to read these before that chown completes.
chmod 644 "$dir/cert.pem" "$dir/key.pem"
