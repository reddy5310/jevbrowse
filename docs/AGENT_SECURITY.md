# Agent security

AI agents never receive blanket access to the browser (§12, §27). They get a **session** scoped by a manifest, and every request is checked before a page is touched.

## Manifest (example: `docs/agents/claude-code.json`)

```json
{
  "agent": "Claude Code",
  "workspace": "JevBrowse Development",
  "allowDomains": ["localhost", "github.com", "learn.microsoft.com"],
  "denyDataClasses": ["Authenticated", "Sensitive", "Secret"],
  "actions": ["Navigate", "Read", "Click", "TypeNonSecret", "Screenshot"],
  "destructiveActions": "confirm",
  "maxLivePages": 3,
  "sessionMinutes": 60,
  "maxActions": 200,
  "container": "Disposable"
}
```

## Authority: the ceiling
Holding the endpoint token is not authority. When you enable the local endpoint you approve a **ceiling** (domains, actions, time). An agent's `POST /sessions` is a *request*: it is clamped to the ceiling (domains outside it are dropped, actions intersected, budgets reduced, denied data classes merged, `destructive: allow` downgraded to `confirm`), you are shown the requested versus the granted manifest, and you approve each session. With no ceiling the endpoint refuses everything. Agents can only ever be given a throwaway identity (Disposable or Private) in a **fresh workspace and profile per session**; naming an existing workspace grants nothing, and two sessions never share cookies. Sessions are closed when they expire even if the agent never calls again.

## Ending a session
**Stop** (per session, in the Agents panel) and expiry do the same thing: revoke first, so work already in flight is
cancelled and the navigation policy starts refusing, then release every page — including pages holding a protection
flag, because an agent's renderer must not outlive the authority that created it. The button reports "Stopped" only
once the renderers are actually gone; if any could not be released it says so and the sweeper retries. Turning the
endpoint off revokes every session it granted.

## What is enforced, in order
1. Session open, not expired, action budget left.
2. Action is in the manifest.
3. `Navigate`: host equals or is a subdomain of an allowed domain. The policy is registered **before the renderer
   exists**, so it governs the first navigation and any redirect inside it, not just later ones; it is re-registered
   before a virtualized agent page is restored. Tabs open in the session's workspace; if that workspace is created by the gateway it uses the manifest's container, **Disposable by default**, so the agent never sees the user's cookies.
4. Everything else needs a current page that (a) is not SECRET (a password/payment field is present: hard deny, whatever the manifest says), (b) is not in `denyDataClasses`, (c) still sits on an allowed domain.
5. `TypeNonSecret`: selectors mentioning password/otp/cvv/card/token/… are refused before reaching the page; the page-side script refuses `type=password`, `autocomplete=cc-*`/`one-time-code` too.
6. `Click`: targets that look destructive (delete, pay, deploy, submit, …) require a human "Allow once" in JevBrowse's own dialog unless the manifest says `deny` (refuse) or `allow` (throwaway containers only).
7. Live-page quota: before a new page gets a renderer, the agent's least-recently-active page is virtualized. Chrome DevTools MCP issue #1921 is the failure mode this prevents.
8. `Screenshot` only when granted; images land in `data/agents/screenshots/<session>/`.

## What the agent sees
A **Page Map**, not the DOM: title, headings, links (text + href), form fields as name/type/label with **no values**, and up to 4 000 chars of main text. No script evaluation exists on the agent surface.

## Audit
Every request, allowed or denied, is appended to the session and to `data/agents/audit/<session>.jsonl` with time, action, target, verdict and reason.

## Local endpoint
Off by default. When enabled from the Agents panel it binds `127.0.0.1` on a random port with a bearer token generated for this run only. Routes: `POST /sessions`, `POST /sessions/{id}/actions`, `GET /sessions/{id}/audit`, `DELETE /sessions/{id}`. It exposes nothing beyond the gateway above.

## Not in V1
Autonomous access to personal browser state, cross-container access, network/CPU accounting per agent (only time/action/page budgets today), MCP protocol framing (the HTTP shape is the stable contract; an MCP adapter can sit on top).
