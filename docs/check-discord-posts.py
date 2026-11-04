"""Check that every message in docs/DISCORD_HOWTO.md fits Discord's 2000-character limit.

Discord counts characters, not bytes, and the limit applies per message. The `=== MESSAGE ===`
header line and the `*attach: ...*` line are instructions to the person posting, not part of the
message, so they are excluded from the count.

Usage: python docs/check-discord-posts.py
Exit code is 1 if anything is over the limit.
"""
import os
import re
import sys

LIMIT = 2000
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "DISCORD_HOWTO.md")


def main():
    text = open(SRC, encoding="utf-8").read()
    blocks = re.split(r"^=== MESSAGE === ", text, flags=re.M)[1:]

    worst = 0
    failed = False
    for block in blocks:
        title, _, rest = block.partition("\n")
        body = re.split(r"^=== END ===", rest, flags=re.M)[0]
        body = re.sub(r"^\*attach:[^\n]*\*\n", "", body.lstrip("\n"))
        body = body.strip()

        count = len(body)
        worst = max(worst, count)
        status = "ok " if count <= LIMIT else "OVER"
        if count > LIMIT:
            failed = True
        print("%s  %4d / %d  %s" % (status, count, LIMIT, title.strip()))

    print("\nlongest: %d characters" % worst)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
