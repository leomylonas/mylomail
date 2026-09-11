#!/usr/bin/env bash
# Self-signed TLS identity for the loopback-only IMAP capability matrix.
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
chmod 644 "$dir/cert.pem" "$dir/key.pem"
