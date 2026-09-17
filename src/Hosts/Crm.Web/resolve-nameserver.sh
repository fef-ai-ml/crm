#!/bin/sh
# 127.0.0.11 is Docker's and Podman's (netavark/aardvark) embedded DNS address on their default user
# networks, but it is not guaranteed on every engine or network (verified in the field: a remote Podman
# engine used a different address). Read this container's actual resolver from /etc/resolv.conf instead
# of hardcoding one, so `resolver` in nginx.conf works on whatever engine this runs on.
set -eu

# Prefer the first IPv4 nameserver; nginx needs IPv6 literals in brackets; fall back to the usual
# embedded-DNS address when resolv.conf lists nothing, so nginx never starts with an empty directive.
RESOLVER_ADDRESS="$(awk '/^nameserver/ && $2 !~ /:/ { print $2; exit }' /etc/resolv.conf)"
if [ -z "$RESOLVER_ADDRESS" ]; then
    ipv6="$(awk '/^nameserver/ && $2 ~ /:/ { sub(/%.*/, "", $2); print $2; exit }' /etc/resolv.conf)"
    if [ -n "$ipv6" ]; then
        RESOLVER_ADDRESS="[$ipv6]"
    else
        RESOLVER_ADDRESS="127.0.0.11"
    fi
fi
export RESOLVER_ADDRESS

envsubst '$RESOLVER_ADDRESS' < /etc/nginx/conf.d/default.conf > /etc/nginx/conf.d/default.conf.tmp
mv /etc/nginx/conf.d/default.conf.tmp /etc/nginx/conf.d/default.conf
