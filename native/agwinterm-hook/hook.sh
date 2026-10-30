#!/bin/sh
# Experimental wrapper: AGWINTERM_HOOK_EXE is the wslpath -u path of the receiver.
# Do not exec: the shell PID must remain alive for server-side /proc validation.
[ -n "${AGWINTERM_SESSION_ID:-}" ] || exit 0
if [ "$1" = claude ]; then
    [ -z "${DEVIN_PROJECT_DIR:-}" ] || exit 0
    [ "${CLAUDE_CODE_ENTRYPOINT:-cli}" = cli ] || exit 0
    [ "${CLAUDE_CODE_SESSION_ATTENDED:-1}" != 0 ] || exit 0
fi
"$AGWINTERM_HOOK_EXE" "$1" "$2" "$AGWINTERM_SESSION_ID" "${AGWINTERM_PIPE:-agwinterm}" "$$" "${DEVIN_PROJECT_DIR:-$PWD}" "${WSL_DISTRO_NAME:-}" >/dev/null 2>&1
exit 0
