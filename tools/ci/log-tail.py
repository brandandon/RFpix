"""log-tail.py - for the GitHub build: when packaging fails, shows the last 60 lines of its output
at the top of the run's page (as errors, 15 lines each), so the reason is seen without opening the
log.

    python3 tools/ci/log-tail.py "<title>" <log file>
"""

import sys

LAST = 60
EACH = 15


def main():
    title, path = sys.argv[1], sys.argv[2]
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = [line.rstrip() for line in f][-LAST:]
    for i in range(0, len(lines), EACH):
        text = "\n".join(lines[i:i + EACH]).replace("%", "%25").replace("\r", "").replace("\n", "%0A")
        print(f"::error title={title} (lines {i + 1}-{min(i + EACH, len(lines))} of the last {len(lines)})::{text}")


if __name__ == "__main__":
    main()
