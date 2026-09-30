#!/bin/sh
set -e

getent group bdsheadless >/dev/null || groupadd --system bdsheadless
getent passwd bdsheadless >/dev/null || useradd --system --gid bdsheadless \
  --home-dir /var/lib/bds-headless --shell /usr/sbin/nologin bdsheadless

install -d -m 0700 -o bdsheadless -g bdsheadless /var/lib/bds-headless
install -d -m 0700 /etc/bds-headless
chmod 0755 /opt/bds-headless/autostart-check.sh

# Key that encrypts the stored refresh token. TPM2 bound when available.
if [ ! -f /etc/bds-headless/kek.cred ] && [ ! -f /etc/bds-headless/kek.key ]; then
  if command -v systemd-creds >/dev/null 2>&1 \
     && head -c 32 /dev/urandom | systemd-creds encrypt --name=kek - /etc/bds-headless/kek.cred >/dev/null 2>&1; then
    chmod 0600 /etc/bds-headless/kek.cred
  else
    rm -f /etc/bds-headless/kek.cred
    head -c 32 /dev/urandom > /etc/bds-headless/kek.key
    chmod 0600 /etc/bds-headless/kek.key
  fi
fi

dropin=/etc/systemd/system/bds-headless.service.d
install -d -m 0755 "$dropin"
if [ -f /etc/bds-headless/kek.cred ]; then
  printf '[Service]\nLoadCredentialEncrypted=kek:/etc/bds-headless/kek.cred\n' > "$dropin/credential.conf"
else
  printf '[Service]\nLoadCredential=kek:/etc/bds-headless/kek.key\n' > "$dropin/credential.conf"
fi

# Let the installing user start/stop the service from the app.
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
  usermod -aG bdsheadless "$SUDO_USER" || true
fi

# Start at boot is on by default. Toggle it in the web UI.
[ -f /var/lib/bds-headless/.installed ] || {
  touch /var/lib/bds-headless/autostart /var/lib/bds-headless/.installed
  chown bdsheadless:bdsheadless /var/lib/bds-headless/autostart /var/lib/bds-headless/.installed
}

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable bds-headless.service >/dev/null 2>&1 || true
  systemctl restart bds-headless.service || true
fi
