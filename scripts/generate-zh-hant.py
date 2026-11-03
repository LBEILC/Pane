"""Generate Strings.zh-Hant.resx from Strings.resx (development only, not part of the build).

Usage:  python scripts/generate-zh-hant.py
Needs:  pip install opencc   (OpenCC config s2twp)

Steps per entry, in order:
  1. If the key is in "keys" of the corrections file, use that whole sentence as is.
  2. Otherwise convert with OpenCC s2twp, then apply "phrases" replacements in file order.
Output keeps the key order of Strings.resx and is byte-for-byte stable for the same input.
"""

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import opencc

ROOT = Path(__file__).resolve().parent.parent
LOC = ROOT / "src" / "Pane.App" / "Localization"
SOURCE = LOC / "Strings.resx"
TARGET = LOC / "Strings.zh-Hant.resx"
CORRECTIONS = LOC / "Strings.zh-Hant.corrections.json"

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms</value>
  </resheader>
"""

PLACEHOLDER = re.compile(r"\{\d+(?:[,:][^}]*)?\}")


def escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def main() -> int:
    corrections = json.loads(CORRECTIONS.read_text(encoding="utf-8"))
    key_overrides: dict[str, str] = corrections.get("keys", {})
    phrases: list[list[str]] = corrections.get("phrases", [])

    entries = [
        (data.get("name"), data.findtext("value") or "")
        for data in ET.parse(SOURCE).getroot().iter("data")
    ]
    names = {name for name, _ in entries}
    unknown = sorted(set(key_overrides) - names)
    if unknown:
        print("corrections reference unknown keys: " + ", ".join(unknown), file=sys.stderr)
        return 1

    converter = opencc.OpenCC("s2twp")
    lines = [HEADER]
    errors = []
    for name, value in entries:
        if name in key_overrides:
            text = key_overrides[name]
        else:
            text = converter.convert(value)
            for old, new in phrases:
                text = text.replace(old, new)
        if sorted(PLACEHOLDER.findall(text)) != sorted(PLACEHOLDER.findall(value)):
            errors.append(name)
        lines.append(
            f'  <data name="{escape(name)}" xml:space="preserve"><value>{escape(text)}</value></data>\n'
        )
    lines.append("</root>\n")

    if errors:
        print("placeholders changed in: " + ", ".join(errors), file=sys.stderr)
        return 1

    content = "".join(lines).replace("\r\n", "\n").replace("\n", "\r\n")
    TARGET.write_bytes(content.encode("utf-8"))
    print(f"wrote {len(entries)} entries to {TARGET.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
