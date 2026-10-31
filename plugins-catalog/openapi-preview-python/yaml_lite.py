"""A small YAML reader for OpenAPI files, so the plugin needs nothing beyond Python's standard library.

Supported: block mappings and sequences, plain, 'single' and "double" quoted scalars, | and > block scalars,
comments, and one-line flow collections ([a, b], {a: b}). Anchors, aliases, tags and multi-line flow collections
raise YamlError with the line, so the preview can say what it could not read.
"""
import json
import re


class YamlError(ValueError):
    pass


_NUMBER = re.compile(r"^[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+)$")


def loads(text):
    lines = []
    for number, raw in enumerate(text.replace("\r", "").split("\n"), start=1):
        if raw.strip() in ("---", "...") or raw.lstrip().startswith("%"):
            continue
        lines.append((number, raw))
    parser = _Parser(lines)
    value = parser.block(0, -1)
    parser.skip_blank()
    if parser.i < len(parser.lines):
        number, raw = parser.lines[parser.i]
        raise YamlError(f"line {number}: unexpected indentation: {raw.strip()}")
    return value


def _strip_comment(text):
    """The part of a line before a # comment that is outside quotes."""
    quote = None
    for i, ch in enumerate(text):
        if quote:
            if ch == quote:
                quote = None
        elif ch in "'\"":
            quote = ch
        elif ch == "#" and (i == 0 or text[i - 1] in " \t"):
            return text[:i].rstrip()
    return text.rstrip()


def _split_key(text):
    """'key: value' → (key, value) at the first ': ' (or trailing ':') outside quotes; None when it is not a mapping line."""
    quote = None
    for i, ch in enumerate(text):
        if quote:
            if ch == quote:
                quote = None
        elif ch in "'\"" and i == 0:
            quote = ch
        elif ch == ":" and (i + 1 == len(text) or text[i + 1] in " \t"):
            return _scalar(text[:i].strip(), 0), text[i + 1:].strip()
    return None


def _scalar(text, number):
    if not text:
        return None
    if text[0] in "&*!":
        raise YamlError(f"line {number}: anchors, aliases and tags are not supported ({text.split()[0]})")
    if text[0] == '"':
        try:
            return json.loads(text)
        except json.JSONDecodeError as error:
            raise YamlError(f"line {number}: bad double-quoted string") from error
    if text[0] == "'":
        if not text.endswith("'") or len(text) < 2:
            raise YamlError(f"line {number}: unterminated single-quoted string")
        return text[1:-1].replace("''", "'")
    if text[0] in "[{":
        value, rest = _flow(text, 0, number)
        if rest.strip():
            raise YamlError(f"line {number}: unexpected text after a flow collection")
        return value
    lowered = text.lower()
    if lowered in ("null", "~"):
        return None
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    if _NUMBER.match(text):
        return float(text) if any(c in text for c in ".eE") else int(text)
    return text


def _flow(text, i, number):
    """A one-line [..] or {..} starting at text[i]; returns (value, rest of text)."""
    opening = text[i]
    closing = "]" if opening == "[" else "}"
    items = [] if opening == "[" else {}
    i += 1
    while True:
        while i < len(text) and text[i] in " \t":
            i += 1
        if i >= len(text):
            raise YamlError(f"line {number}: flow collections must close on the same line")
        if text[i] == closing:
            return items, text[i + 1:]
        if text[i] in "[{":
            value, rest = _flow(text, i, number)
            i = len(text) - len(rest)
            item_text = None
        else:
            start, quote = i, None
            while i < len(text):
                ch = text[i]
                if quote:
                    if ch == quote:
                        quote = None
                elif ch in "'\"":
                    quote = ch
                elif ch in ",]}" and not quote:
                    break
                i += 1
            item_text = text[start:i].strip()
            value = None
        if isinstance(items, list):
            items.append(value if item_text is None else _scalar(item_text, number))
        else:
            pair = _split_key(item_text) if item_text is not None else None
            if pair is None:
                raise YamlError(f"line {number}: expected key: value in a flow mapping")
            items[pair[0]] = _scalar(pair[1], number)
        while i < len(text) and text[i] in " \t":
            i += 1
        if i < len(text) and text[i] == ",":
            i += 1


class _Parser:
    def __init__(self, lines):
        self.lines = lines
        self.i = 0

    def skip_blank(self):
        while self.i < len(self.lines) and not _strip_comment(self.lines[self.i][1]).strip():
            self.i += 1

    def peek(self):
        self.skip_blank()
        if self.i >= len(self.lines):
            return None
        number, raw = self.lines[self.i]
        content = _strip_comment(raw)
        if "\t" in raw[: len(raw) - len(raw.lstrip())]:
            raise YamlError(f"line {number}: tabs are not allowed for indentation")
        return number, len(content) - len(content.lstrip(" ")), content.strip()

    def block(self, indent, parent):
        """The value whose lines start at column `indent` or deeper (but deeper than `parent`)."""
        line = self.peek()
        if line is None or line[1] <= parent:
            return None
        _, column, content = line
        if content == "-" or content.startswith("- "):
            return self.sequence(column)
        return self.mapping(column)

    def mapping(self, column, first=None):
        result = {}
        while True:
            if first is not None:
                number, content = first
                first = None
            else:
                line = self.peek()
                if line is None or line[1] != column or line[2].startswith("- ") or line[2] == "-":
                    return result
                number, _, content = line
                self.i += 1
            pair = _split_key(content)
            if pair is None:
                raise YamlError(f"line {number}: expected 'key: value', got: {content}")
            key, rest = pair
            result[key] = self.value(rest, column, number)

    def sequence(self, column):
        result = []
        while True:
            line = self.peek()
            if line is None or line[1] != column or not (line[2] == "-" or line[2].startswith("- ")):
                return result
            number, _, content = line
            self.i += 1
            item = content[1:].strip()
            if not item:
                result.append(self.block(column + 1, column))
            elif _split_key(item) is not None and item[0] not in "[{\"'":
                # "- key: value" starts a mapping whose keys line up with "key".
                inner = column + (len(content) - len(content[1:].lstrip()))
                result.append(self.mapping(inner, (number, item)))
            else:
                result.append(_scalar(item, number))

    def value(self, rest, column, number):
        if rest in ("|", ">", "|-", ">-", "|+", ">+"):
            return self.block_scalar(rest, column)
        if rest:
            return _scalar(rest, number)
        line = self.peek()
        if line is not None and line[1] == column and (line[2] == "-" or line[2].startswith("- ")):
            return self.sequence(column)  # a sequence may sit at the same indentation as its key
        return self.block(column + 1, column)

    def block_scalar(self, style, column):
        collected = []
        while self.i < len(self.lines):
            _, raw = self.lines[self.i]
            if raw.strip() and len(raw) - len(raw.lstrip(" ")) <= column:
                break
            collected.append(raw)
            self.i += 1
        while collected and not collected[-1].strip():
            collected.pop()
        margin = min((len(l) - len(l.lstrip(" ")) for l in collected if l.strip()), default=0)
        body = [l[margin:] for l in collected]
        text = "\n".join(body) if style[0] == "|" else " ".join(l.strip() for l in body if l.strip())
        return text if style.endswith("-") else text + "\n"
