---
name: session-id
description: Print the identifiers of the current Copilot session and show how to reference it later — from another app session, from a resumed Copilot CLI run, or when querying session history. Use when asked for "this session's ID", how to link, resume, reference, message, or come back to a session, or when a session ID needs to be recorded somewhere (commit message, work item, notes).
license: MIT
---

# Session identifiers

A running session carries several different UUIDs and only one of them refers to
the conversation. Print them, say plainly which is which, and hand back
references that can be pasted somewhere.

## 1. Collect

Run `scripts/session-ids.ps1` (pwsh, cross-platform). It reads the environment of
the running agent — no network, no arguments.

Without the script, read the variables directly: `COPILOT_AGENT_SESSION_ID`,
`AGENCY_SESSION_ID`, `AGENCY_REPO_DIR`, `COPILOT_HOME`.

## 2. Resolve the name

`COPILOT_AGENT_SESSION_ID` is the key into the local session store:

```sql
SELECT id, summary, created_at FROM sessions WHERE id = '<COPILOT_AGENT_SESSION_ID>'
```

via `session_store_sql` with `source: "local"`. In the app, `list_sessions_and_chats`
returns the same id plus `name` and `app_url`.

If the store has no row, say so rather than inventing a name — a brand new session
may not be written yet.

## 3. Report

Lead with the one that matters:

> **Session ID: `<uuid>`** — "<name>"

Then the others, only if asked or if more than one is present:

| Variable | What it identifies | Use it to reference the session? |
|---|---|---|
| `COPILOT_AGENT_SESSION_ID` | the conversation | **Yes — this is the one** |
| `AGENCY_SESSION_ID` | the Agency wrapper process | No |
| `AGENCY_LOG_SESSION_DIR` | one launch's log folder | No — changes per launch |
| app `project_session_id` | the workspace/branch record | No — holds no turns |
| `Copilot-Session` commit trailer | provenance stamp on commits | No |

The last two are the usual traps. `project_session_id` exists in the session store
but its turn count is zero, and the `Copilot-Session` trailer written into commit
messages is a *different* UUID from the conversation — going from a commit back to
the session needs `COPILOT_AGENT_SESSION_ID`, not the trailer.

## 4. Offer the references

Give the forms that apply, with the real id substituted:

**From another app session** — delivered as a user turn, full context intact:
`send_session_message` with `session_id: <id>`

**From a terminal** — the app session *is* a CLI session, same id, same store:
```
copilot --resume=<id>
copilot --resume=<first 7+ hex chars>     # prefix works
```

**Read history without running it** — works after the session ends or is archived:
```sql
SELECT turn_index, user_message, assistant_response
FROM turns WHERE session_id = '<id>' ORDER BY turn_index
```

**In the app UI**: `ghapp://sessions/<id>`

## Do not resume the current session

`copilot --resume=$COPILOT_AGENT_SESSION_ID` from inside that same session starts a
second agent against the live session. It takes its own `inuse.<pid>.lock` in
`$COPILOT_HOME/session-state/<id>/`, runs a real turn, and appends to the history
being written by the session that launched it.

Only ever resume a session that is not running. To check first:

```powershell
Get-ChildItem "$env:COPILOT_HOME\session-state\<id>" -Filter "inuse.*.lock"
```

A lock whose pid no longer exists is stale and safe to ignore.

## Shared setup and confidentiality

Follow the installed `GLOBAL.md` and `VOICE.md` where applicable. Use only available, authorized tools; report unavailable session or connector data rather than guessing. Do not send private identifiers or transcript content to external services.
