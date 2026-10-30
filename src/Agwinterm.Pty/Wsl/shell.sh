# Sourced by bash/zsh, after the user's prompt setup.
if [ "${AGWINTERM_ENABLED:-}" = 1 ] && [ -z "${__agw_si:-}" ]; then
    __agw_si=1
    __agw_prompt() {
        # Percent-encode bytes so spaces, %, Unicode and control characters cannot corrupt OSC 7.
        local remaining="$PWD" out='' c
        local LC_ALL=C
        while [ -n "$remaining" ]; do
            c="${remaining%"${remaining#?}"}"
            remaining="${remaining#?}"
            case "$c" in
                [a-zA-Z0-9/._~-]) out="$out$c" ;;
                *) printf -v c '%%%02X' "'$c"; out="$out$c" ;;
            esac
        done
        printf '\033]7;file://localhost%s\007' "$out"
    }
    if [ -n "${ZSH_VERSION:-}" ]; then
        autoload -Uz add-zsh-hook
        add-zsh-hook precmd __agw_prompt
    elif [ -n "${BASH_VERSION:-}" ]; then
        if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == 'declare -a '* ]]; then
            PROMPT_COMMAND+=(__agw_prompt)
        else
            PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND$'\n'}__agw_prompt"
        fi
    fi
fi
