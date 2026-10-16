"""Convert LICENSE into the RTF file shown by the installer's licence page."""

BACKSLASH = chr(92)


def escape(text: str) -> str:
    out = text.replace(BACKSLASH, BACKSLASH * 2)
    out = out.replace("{", BACKSLASH + "{").replace("}", BACKSLASH + "}")
    return out


def main() -> None:
    raw = open("LICENSE", encoding="utf-8").read()
    paragraphs = [p.replace("\n", " ").strip() for p in raw.split("\n\n")]
    par = BACKSLASH + "par "

    body = "".join(escape(p) + par + par for p in paragraphs if p)
    header = (BACKSLASH + "rtf1" + BACKSLASH + "ansi" + BACKSLASH + "deff0"
              + "{" + BACKSLASH + "fonttbl{" + BACKSLASH + "f0" + BACKSLASH
              + "fnil" + BACKSLASH + "fcharset0 Segoe UI;}}" + BACKSLASH + "fs18 ")
    rtf = "{" + header + body + "}"

    with open("installer/License.rtf", "w", encoding="ascii", newline="") as handle:
        handle.write(rtf)
    print("installer/License.rtf", len(rtf), "bytes")


main()
