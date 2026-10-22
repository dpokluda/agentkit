#!/usr/bin/env python3
"""Render Microsoft Graph Teams message JSON into a readable transcript.

Input is one or more files, or a directory of files, each holding either a raw
Graph response (an object with a "value" array), a bare list of chatMessage
objects, or the WorkIQ envelope ({"results": [{"data": {...}}]}).

Usage:
    python render_transcript.py PAGES [PAGES ...] [-o OUT] [--json]
                                [--since DATE] [--until DATE] [--author NAME]
                                [--tz local|utc]
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
from datetime import datetime, timezone
from html.parser import HTMLParser

# --------------------------------------------------------------------------- #
# HTML -> text
# --------------------------------------------------------------------------- #

BLOCK_TAGS = {
    "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
    "blockquote", "pre", "table", "ul", "ol", "hr",
}


class _Html2Text(HTMLParser):
    """Flatten Teams message HTML, keeping link targets and inline cards."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []
        self.attachment_ids: list[str] = []
        self.links: list[str] = []
        self._skip_depth = 0
        self._href: str | None = None
        self._href_text: list[str] = []

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag in ("script", "style"):
            self._skip_depth += 1
            return
        if tag == "attachment":
            aid = a.get("id")
            if aid:
                self.attachment_ids.append(aid)
                self.parts.append(f"\n[attachment:{aid}]\n")
            return
        if tag == "img":
            alt = a.get("alt") or a.get("itemid") or "image"
            self.parts.append(f"[image: {alt}]")
            return
        if tag == "a":
            self._href = a.get("href")
            self._href_text = []
            return
        if tag == "span" and a.get("itemtype", "").endswith("Mention"):
            return
        if tag == "blockquote":
            self.parts.append("\n\x01BQ\x01\n")
            return
        if tag in BLOCK_TAGS:
            self.parts.append("\n")
        if tag == "li":
            self.parts.append("- ")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self._skip_depth = max(0, self._skip_depth - 1)
            return
        if tag == "a":
            text = "".join(self._href_text).strip()
            href = (self._href or "").strip()
            if href and href not in ("", "#"):
                self.links.append(href)
                # Avoid the very common "text == url" duplication.
                if text and text.rstrip("/") != href.rstrip("/"):
                    self.parts.append(f"{text} <{href}>")
                else:
                    self.parts.append(href)
            else:
                self.parts.append(text)
            self._href = None
            self._href_text = []
            return
        if tag == "blockquote":
            self.parts.append("\n\x02BQ\x02\n")
            return
        if tag in BLOCK_TAGS:
            self.parts.append("\n")

    def handle_data(self, data):
        if self._skip_depth:
            return
        if self._href is not None:
            self._href_text.append(data)
        else:
            self.parts.append(data)

    def text(self) -> str:
        raw = "".join(self.parts)
        raw = raw.replace("\u00a0", " ")
        raw = re.sub(r"[ \t]+", " ", raw)
        raw = re.sub(r" *\n *", "\n", raw)
        raw = re.sub(r"\n{3,}", "\n\n", raw)

        # Re-mark quoted passages so a reply is never confused with what it quotes.
        out, depth = [], 0
        for line in raw.split("\n"):
            if line.strip() == "\x01BQ\x01":
                depth += 1
                continue
            if line.strip() == "\x02BQ\x02":
                depth = max(0, depth - 1)
                continue
            out.append(("> " * depth + line) if (depth and line.strip()) else line)
        cleaned = re.sub(r"\n{3,}", "\n\n", "\n".join(out))
        return cleaned.strip()


def html_to_text(content: str | None, content_type: str | None) -> tuple[str, list[str], list[str]]:
    """Return (text, attachment_ids, links)."""
    if not content:
        return "", [], []
    if (content_type or "html").lower() != "html":
        return html.unescape(content).strip(), [], []
    parser = _Html2Text()
    try:
        parser.feed(content)
        parser.close()
    except Exception:  # malformed markup should never lose the message
        return re.sub(r"<[^>]+>", " ", content).strip(), [], []
    return parser.text(), parser.attachment_ids, parser.links


# --------------------------------------------------------------------------- #
# Loading
# --------------------------------------------------------------------------- #

def _iter_message_objects(node):
    """Yield chatMessage-ish dicts from any of the accepted input shapes."""
    if isinstance(node, list):
        for item in node:
            yield from _iter_message_objects(item)
        return
    if not isinstance(node, dict):
        return
    if "results" in node and isinstance(node["results"], list):
        for r in node["results"]:
            yield from _iter_message_objects(r.get("data", r))
        return
    if "value" in node and isinstance(node["value"], list):
        yield from _iter_message_objects(node["value"])
        return
    if "id" in node and ("body" in node or "from" in node or "messageType" in node):
        yield node


def load_messages(paths: list[str]) -> list[dict]:
    files: list[str] = []
    for p in paths:
        if os.path.isdir(p):
            for name in sorted(os.listdir(p)):
                if name.lower().endswith(".json"):
                    files.append(os.path.join(p, name))
        else:
            files.append(p)
    if not files:
        raise SystemExit("no .json input files found")

    seen: set[str] = set()
    out: list[dict] = []
    for f in files:
        with open(f, "r", encoding="utf-8") as fh:
            try:
                doc = json.load(fh)
            except json.JSONDecodeError as e:
                print(f"warning: skipping {f}: {e}", file=sys.stderr)
                continue
        for msg in _iter_message_objects(doc):
            mid = str(msg.get("id"))
            key = f"{msg.get('replyToId') or ''}/{mid}"
            if key in seen:
                continue
            seen.add(key)
            out.append(msg)
    return out


# --------------------------------------------------------------------------- #
# Normalizing
# --------------------------------------------------------------------------- #

def sender_name(msg: dict) -> str:
    frm = msg.get("from") or {}
    for key, label in (("user", None), ("application", "app"), ("device", "device")):
        ident = frm.get(key)
        if ident:
            name = ident.get("displayName") or ident.get("id") or "unknown"
            return f"{name} ({label})" if label else name
    if msg.get("messageType") and msg["messageType"] != "message":
        return "system"
    return "unknown"


def describe_attachments(msg: dict) -> list[str]:
    out = []
    for att in msg.get("attachments") or []:
        name = att.get("name") or att.get("id") or "attachment"
        ctype = (att.get("contentType") or "").split("/")[-1]
        url = att.get("contentUrl")
        label = f"{name} [{ctype}]" if ctype else name
        out.append(f"{label} <{url}>" if url else label)
    return out


def describe_reactions(msg: dict) -> str:
    counts: dict[str, int] = {}
    for r in msg.get("reactions") or []:
        counts[r.get("reactionType", "?")] = counts.get(r.get("reactionType", "?"), 0) + 1
    return ", ".join(f"{k} x{v}" for k, v in sorted(counts.items()))


def parse_ts(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def describe_event(msg: dict) -> str:
    """Turn an eventDetail payload into a one-line human description."""
    detail = msg.get("eventDetail")
    if not isinstance(detail, dict):
        return ""
    kind = (detail.get("@odata.type") or "").split(".")[-1]
    kind = re.sub(r"EventMessageDetail$", "", kind)
    kind = re.sub(r"(?<!^)(?=[A-Z])", " ", kind).lower() or "system event"

    who = ((detail.get("initiator") or {}).get("user") or {}).get("displayName")
    members = [
        m.get("displayName") or m.get("id", "")[:8]
        for m in (detail.get("members") or [])
        if isinstance(m, dict)
    ]
    bits = [f"[{kind}]"]
    if members:
        named = [m for m in members if m]
        bits.append(f"({len(members)} member{'s' if len(members) != 1 else ''}"
                    + (": " + ", ".join(named) if any(m and not m.islower() for m in named) else "")
                    + ")")
    for key in ("teamDisplayName", "channelDisplayName", "chatDisplayName", "meetingOrganizer"):
        if detail.get(key):
            bits.append(str(detail[key]))
    if who:
        bits.append(f"by {who}")
    return " ".join(bits)


def normalize(msg: dict) -> dict:
    body = msg.get("body") or {}
    text, inline_ids, links = html_to_text(body.get("content"), body.get("contentType"))
    if not text or text == "<systemEventMessage/>":
        text = describe_event(msg) or text
    created = parse_ts(msg.get("createdDateTime"))
    edited = parse_ts(msg.get("lastEditedDateTime") or msg.get("lastModifiedDateTime"))
    kinds = []
    if msg.get("deletedDateTime"):
        kinds.append("deleted")
    mtype = msg.get("messageType") or "message"
    if mtype != "message":
        kinds.append(mtype)
    if edited and created and (edited - created).total_seconds() > 1 and msg.get("lastEditedDateTime"):
        kinds.append("edited")
    if (msg.get("importance") or "normal") != "normal":
        kinds.append(msg["importance"])

    return {
        "id": str(msg.get("id")),
        "reply_to": msg.get("replyToId"),
        "created": created,
        "created_raw": msg.get("createdDateTime"),
        "author": sender_name(msg),
        "subject": msg.get("subject"),
        "text": text,
        "flags": kinds,
        "attachments": describe_attachments(msg),
        "inline_attachment_ids": inline_ids,
        "links": links,
        "mentions": [
            (m.get("mentioned") or {}).get("user", {}).get("displayName")
            or m.get("mentionText")
            for m in (msg.get("mentions") or [])
        ],
        "reactions": describe_reactions(msg),
    }


# --------------------------------------------------------------------------- #
# Rendering
# --------------------------------------------------------------------------- #

def fmt_time(dt: datetime | None, tzmode: str) -> str:
    if dt is None:
        return "unknown time"
    if tzmode == "utc":
        return dt.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    return dt.astimezone().strftime("%Y-%m-%d %H:%M %Z").strip()


def render_one(rec: dict, tzmode: str, indent: str = "") -> str:
    head = f"{indent}### {rec['author']} — {fmt_time(rec['created'], tzmode)}"
    if rec["flags"]:
        head += f"  _({', '.join(rec['flags'])})_"
    lines = [head]
    if rec["subject"]:
        lines.append(f"{indent}**{rec['subject']}**")
    body = rec["text"] or "_(no text — card, image, or system event)_"
    lines.append("\n".join(indent + ln for ln in body.splitlines()))
    if rec["attachments"]:
        lines.append(f"{indent}- attachments: " + "; ".join(rec["attachments"]))
    if rec["mentions"]:
        named = [m for m in rec["mentions"] if m]
        if named:
            lines.append(f"{indent}- mentions: " + ", ".join(named))
    if rec["reactions"]:
        lines.append(f"{indent}- reactions: {rec['reactions']}")
    return "\n".join(lines)


def render(records: list[dict], tzmode: str, title: str | None) -> str:
    by_parent: dict[str, list[dict]] = {}
    roots: list[dict] = []
    ids = {r["id"] for r in records}
    for r in records:
        parent = r["reply_to"]
        if parent and parent in ids:
            by_parent.setdefault(parent, []).append(r)
        else:
            roots.append(r)

    key = lambda r: (r["created"] or datetime.min.replace(tzinfo=timezone.utc), r["id"])
    roots.sort(key=key)
    for v in by_parent.values():
        v.sort(key=key)

    stamps = [r["created"] for r in records if r["created"]]
    out = [f"# {title or 'Teams transcript'}", ""]
    out.append(f"- messages: {len(records)} ({len(roots)} top-level)")
    if stamps:
        out.append(f"- range: {fmt_time(min(stamps), tzmode)} → {fmt_time(max(stamps), tzmode)}")
    authors = sorted({r["author"] for r in records})
    out.append(f"- participants: {', '.join(authors)}")
    out.append("")
    out.append("---")
    out.append("")

    for r in roots:
        out.append(render_one(r, tzmode))
        for child in by_parent.get(r["id"], []):
            out.append("")
            out.append(render_one(child, tzmode, indent="> "))
        out.append("")
    return "\n".join(out).rstrip() + "\n"


# --------------------------------------------------------------------------- #

def main() -> int:
    try:  # Windows consoles default to cp1252 and choke on emoji/arrows.
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="JSON file(s) or a directory of them")
    ap.add_argument("-o", "--out", help="write to this file instead of stdout")
    ap.add_argument("--json", action="store_true", help="emit structured records instead of Markdown")
    ap.add_argument("--since", help="drop messages before this ISO date")
    ap.add_argument("--until", help="drop messages after this ISO date")
    ap.add_argument("--author", action="append", help="keep only these authors (substring, repeatable)")
    ap.add_argument("--title", help="transcript heading")
    ap.add_argument("--tz", choices=["local", "utc"], default="local")
    args = ap.parse_args()

    records = [normalize(m) for m in load_messages(args.paths)]

    def bound(value: str | None, end: bool):
        dt = parse_ts(value if not value or "T" in value else f"{value}T{'23:59:59' if end else '00:00:00'}")
        return dt.replace(tzinfo=dt.tzinfo or timezone.utc) if dt else None

    since, until = bound(args.since, False), bound(args.until, True)
    if since:
        records = [r for r in records if r["created"] and r["created"] >= since]
    if until:
        records = [r for r in records if r["created"] and r["created"] <= until]
    if args.author:
        needles = [a.lower() for a in args.author]
        records = [r for r in records if any(n in r["author"].lower() for n in needles)]

    if not records:
        print("no messages matched", file=sys.stderr)
        return 1

    if args.json:
        payload = []
        for r in records:
            d = dict(r)
            d["created"] = r["created"].isoformat() if r["created"] else None
            payload.append(d)
        text = json.dumps(payload, indent=2, ensure_ascii=False)
    else:
        text = render(records, args.tz, args.title)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(text)
        print(f"wrote {len(records)} messages to {args.out}")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
