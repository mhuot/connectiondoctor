# Conformance fixtures

Recorded input, and the answer every implementation must give for it.

Each case is a directory:

```
<case>/
  contract.v1.json    the envelope (topology, power) — the "now" of the case
  events.v1.jsonl     the recorded event stream
  expected.json       what the analysis must conclude
  README.md           what happened, and why the expected answer is right
```

`expected.json` is the point of the whole exercise:

```json
{
  "kind": "fault" | "control",
  "findings": [ { "severity": "...", "title": "..." } ],
  "incidents": [ { "rootEvent": "...", "devicesLost": 3, "sharedParent": "usb:..." } ],
  "notes": "why"
}
```

## Two kinds of case, and why both matter

**Faults** are recordings where something was wrong and the tool must say so.
They prove the engines detect what they claim to detect.

**Controls** are recordings where *nothing was wrong* — a normal unplug, a
sleep/wake, a KVM switch moving devices to another machine, two identical mice
on one desk, a window with holes in it. Their expected answer is "no finding",
or an explicitly unattributed incident. They are the more valuable half: a
tool that cries wolf on an ordinary unplug is worse than one that says nothing,
because a technician who learns to ignore it will ignore the real fault too.

## Parity and quality are different questions

Two suites run over these fixtures, and conflating them hides a whole class of
bug:

- **Parity** — Swift, C# and TypeScript produce the *same* answer for the same
  input. A wrong answer that all three agree on passes parity, which is
  exactly why it is not enough on its own.
- **Diagnostic quality** — the answer matches `expected.json`: the fault cases
  are detected, and *no control case produces a warning or critical finding*.

## Provenance

Every case says where it came from. `recorded` cases are real captures from
this fleet, de-identified with the redaction rules in `docs/schema-v1.md`
(`host.id`/`host.name` pseudonymised, no serials, no `platform{}`).
`constructed` cases are hand-built to isolate one behaviour — an honest label,
because a constructed case proves the engine follows its rule, not that the
rule matches reality. Where a constructed case encodes a real observation, its
README says which recording it came from.
