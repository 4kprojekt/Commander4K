"""Authenticode signer for the pe-info plugin: who signed the file, read from the embedded PKCS#7 data.

The signature is NOT verified cryptographically (that would need more than fs.read): the preview says so. Data
directory 4 holds a FILE OFFSET (not an RVA) to WIN_CERTIFICATE entries; the first PKCS#7 one is decoded with a small
DER reader (definite lengths of up to 4 bytes, nesting depth at most 32). Any DER error becomes Signature.error.
"""
import struct
from dataclasses import dataclass

import pe

MAX_CERTIFICATE = 1024 * 1024
MAX_DEPTH = 32
CERT_PKCS7 = 2

OID_SIGNED_DATA = "1.2.840.113549.1.7.2"
OID_CN, OID_O = "2.5.4.3", "2.5.4.10"
OID_SIGNING_TIME = "1.2.840.113549.1.9.5"
OID_COUNTERSIGNATURE = "1.2.840.113549.1.9.6"
OID_RFC3161 = "1.3.6.1.4.1.311.3.3.1"


@dataclass
class Signature:
    present: bool = False
    subject: str = None
    organization: str = None
    issuer: str = None
    serial: str = None
    not_before: str = None
    not_after: str = None
    signing_time: str = None
    timestamped: bool = False
    error: str = None


class DerError(Exception):
    """The DER data is malformed."""


class Tlv:
    """One DER element: `start`..`end` is its content, `header`..`end` the whole element, `next` what follows it."""
    __slots__ = ("data", "tag", "header", "start", "end", "next")

    def __init__(self, data, tag, header, start, end):
        self.data, self.tag, self.header, self.start, self.end, self.next = data, tag, header, start, end, end

    @property
    def content(self):
        return self.data[self.start:self.end]

    @property
    def raw(self):
        return self.data[self.header:self.end]

    def children(self, depth):
        if depth > MAX_DEPTH:
            raise DerError("zbyt głębokie zagnieżdżenie")
        if not self.tag & 0x20:
            raise DerError(f"oczekiwano struktury, jest znacznik 0x{self.tag:02X}")
        result, at = [], self.start
        while at < self.end:
            item = read_tlv(self.data, at, self.end)
            result.append(item)
            at = item.next
        return result


def read_tlv(data, at, limit=None):
    limit = len(data) if limit is None else limit
    if at + 2 > limit:
        raise DerError("ucięte dane")
    begin = at
    tag = data[at]
    at += 1
    if tag & 0x1F == 0x1F:
        raise DerError("nieobsługiwany wieloczęściowy znacznik")
    first = data[at]
    at += 1
    if first < 0x80:
        length = first
    elif first == 0x80:
        raise DerError("nieokreślona długość (BER)")
    else:
        count = first & 0x7F
        if count > 4 or at + count > limit:
            raise DerError("nieprawidłowa długość")
        length = int.from_bytes(data[at:at + count], "big")
        at += count
    if at + length > limit:
        raise DerError("element wychodzi poza dane")
    return Tlv(data, tag, begin, at, at + length)


def _oid(item):
    if item.tag != 0x06:
        raise DerError("oczekiwano identyfikatora OID")
    data = item.content
    if not data:
        raise DerError("pusty OID")
    parts, value = [], 0
    for byte in data:
        value = value << 7 | byte & 0x7F
        if not byte & 0x80:
            parts.append(value)
            value = 0
    first = parts[0]
    head = [min(first // 40, 2), first - 40 * min(first // 40, 2)]
    return ".".join(str(n) for n in head + parts[1:])


def _text(item):
    data = item.content
    if item.tag == 0x1E:
        return data.decode("utf-16-be", "replace")
    if item.tag == 0x1C:
        return data.decode("utf-32-be", "replace")
    if item.tag == 0x0C:
        return data.decode("utf-8", "replace")
    return data.decode("latin-1")


def _name(item, depth):
    """{oid: value} of an X.501 Name (the first value of each attribute type)."""
    values = {}
    for rdn in item.children(depth):
        for pair in rdn.children(depth + 1):
            parts = pair.children(depth + 2)
            if len(parts) >= 2 and parts[0].tag == 0x06:
                values.setdefault(_oid(parts[0]), "".join(ch if ch.isprintable() else "?" for ch in _text(parts[1])))
    return values


def _time(item):
    text = item.content.decode("ascii", "replace")
    if item.tag == 0x17 and len(text) >= 12:      # UTCTime YYMMDDHHMMSSZ
        year = int(text[:2])
        text = ("19" if year >= 50 else "20") + text
    elif item.tag != 0x18 or len(text) < 14:      # GeneralizedTime YYYYMMDDHHMMSS[.fff]Z
        raise DerError("nieprawidłowy czas")
    return f"{text[0:4]}-{text[4:6]}-{text[6:8]} {text[8:10]}:{text[10:12]}:{text[12:14]} UTC"


def _attributes(item, depth):
    """{oid: [values]} of a SET OF Attribute."""
    result = {}
    for attribute in item.children(depth):
        parts = attribute.children(depth + 1)
        if len(parts) >= 2:
            result.setdefault(_oid(parts[0]), []).extend(parts[1].children(depth + 2))
    return result


def _signed_data(content_info, depth):
    parts = content_info.children(depth)
    if len(parts) < 2 or _oid(parts[0]) != OID_SIGNED_DATA or parts[1].tag != 0xA0:
        raise DerError("to nie jest SignedData PKCS#7")
    explicit = parts[1].children(depth + 1)
    if not explicit:
        raise DerError("pusty SignedData")
    return explicit[0].children(depth + 2)


def _timestamp_time(token, depth):
    """genTime of an RFC 3161 timestamp token (ContentInfo → SignedData → TSTInfo), or None."""
    fields = _signed_data(token, depth)
    encap = next((f for f in fields if f.tag == 0x30 and f.children(depth + 1) and f.children(depth + 1)[0].tag == 0x06), None)
    if encap is None:
        return None
    inner = encap.children(depth + 1)
    if len(inner) < 2 or inner[1].tag != 0xA0:
        return None
    octets = inner[1].children(depth + 2)[0]
    if octets.tag != 0x04:
        return None
    tst = read_tlv(octets.content, 0).children(depth + 3)
    gen = next((f for f in tst if f.tag == 0x18), None)
    return _time(gen) if gen is not None else None


def _countersignature_time(signer, depth):
    parts = signer.children(depth)
    auth = next((p for p in parts if p.tag == 0xA0), None)
    if auth is None:
        return None
    values = _attributes(auth, depth + 1).get(OID_SIGNING_TIME)
    return _time(values[0]) if values else None


def decode(data):
    """Signature fields of a PKCS#7 blob (Authenticode); raises DerError on malformed data."""
    result = Signature(present=True)
    depth = 0
    fields = _signed_data(read_tlv(data, 0), depth)
    certificates = next((f for f in fields if f.tag == 0xA0), None)
    signer_infos = next((f for f in reversed(fields) if f.tag == 0x31), None)
    if signer_infos is None or not signer_infos.children(3):
        raise DerError("brak informacji o podpisującym")
    signer = signer_infos.children(3)[0]
    signer_parts = signer.children(4)
    if len(signer_parts) < 2 or signer_parts[1].tag != 0x30:
        raise DerError("nieobsługiwany identyfikator podpisującego")
    issuer_and_serial = signer_parts[1].children(5)
    if len(issuer_and_serial) < 2:
        raise DerError("uszkodzony identyfikator podpisującego")
    wanted_issuer, wanted_serial = issuer_and_serial[0].raw, issuer_and_serial[1].content

    for certificate in (certificates.children(3) if certificates is not None else []):
        tbs = certificate.children(4)[0].children(5)
        index = 1 if tbs and tbs[0].tag == 0xA0 else 0     # optional [0] version
        if len(tbs) < index + 6:
            continue
        serial, issuer, validity, subject = tbs[index], tbs[index + 2], tbs[index + 3], tbs[index + 4]
        if serial.content != wanted_serial or issuer.raw != wanted_issuer:
            continue
        names = _name(subject, 6)
        result.subject = names.get(OID_CN)
        result.organization = names.get(OID_O)
        issuer_names = _name(issuer, 6)
        result.issuer = issuer_names.get(OID_CN) or issuer_names.get(OID_O)
        result.serial = serial.content.lstrip(b"\0").hex().upper() or "00"
        times = validity.children(6)
        if len(times) >= 2:
            result.not_before, result.not_after = _time(times[0]), _time(times[1])
        break
    else:
        result.error = "nie znaleziono certyfikatu podpisującego"

    auth = next((p for p in signer_parts if p.tag == 0xA0), None)
    if auth is not None:
        values = _attributes(auth, 5).get(OID_SIGNING_TIME)
        if values:
            result.signing_time = _time(values[0])
    unauth = next((p for p in signer_parts if p.tag == 0xA1), None)
    if unauth is not None:
        attributes = _attributes(unauth, 5)
        for oid, read in ((OID_RFC3161, _timestamp_time), (OID_COUNTERSIGNATURE, _countersignature_time)):
            if oid in attributes:
                result.timestamped = True
                if result.signing_time is None and attributes[oid]:
                    try:
                        result.signing_time = read(attributes[oid][0], 6)
                    except (DerError, IndexError, ValueError):
                        pass   # the timestamp exists; only its time could not be read
    return result


def signer(reader, image):
    """The embedded Authenticode signer; Signature(present=False) without a certificate table. Never raises."""
    offset, size = image.data_dirs[pe.DIR_SECURITY] if len(image.data_dirs) > pe.DIR_SECURITY else (0, 0)
    if offset == 0 or size == 0:
        return Signature()
    try:
        head = reader.read(offset, 8)
        length, _revision, kind = struct.unpack_from("<IHH", head, 0)
        if length < 8 or length > size:
            return Signature(present=True, error="nagłówek WIN_CERTIFICATE jest uszkodzony")
        if kind != CERT_PKCS7:
            return Signature(present=True, error=f"nieobsługiwany typ certyfikatu ({kind})")
        if length - 8 > MAX_CERTIFICATE:
            return Signature(present=True, error="podpis jest za duży")
        return decode(reader.read(offset + 8, length - 8))
    except pe.ReadLimit as error:
        return Signature(present=True, error=str(error))
    except (DerError, struct.error, IndexError, ValueError, UnicodeError) as error:
        return Signature(present=True, error=str(error) or type(error).__name__)
