#!/bin/sh
# Installer for the .tar.gz build (distros without deb/rpm).
set -e
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo"; exit 1; }
here=$(cd "$(dirname "$0")" && pwd)

install -d /opt/bds-headless
cp -r "$here/app/." /opt/bds-headless/
install -m 0644 "$here/bds-headless.service" /usr/lib/systemd/system/bds-headless.service
install -d /usr/share/polkit-1/rules.d
install -m 0644 "$here/50-bds-headless.rules" /usr/share/polkit-1/rules.d/50-bds-headless.rules
install -m 0644 "$here/bds-headless.desktop" /usr/share/applications/bds-headless.desktop
install -d /etc/xdg/autostart
install -m 0644 "$here/bds-headless-tray.desktop" /etc/xdg/autostart/bds-headless-tray.desktop
ln -sf /opt/bds-headless/bds-headless /usr/bin/bds-headless

sh "$here/postinstall.sh"
echo "Installed. Log out and in once so your user can control the service."
