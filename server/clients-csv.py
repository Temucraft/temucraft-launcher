#!/usr/bin/env python3
"""
Makes Apache's launcher log (see apache-launcher-clients.conf.example) readable: adds a header row and decodes the
URL-encoded values. Writes a new CSV; the log itself is left alone.

    python3 clients-csv.py /var/log/apache2/temucraft-launcher-clients.csv > clients.csv
    python3 clients-csv.py /var/log/apache2/temucraft-launcher-clients.csv --last      # one line per install, newest
"""

import argparse
import csv
import sys
from http import HTTPStatus
from urllib.parse import unquote

# the order Apache writes them in (apache-launcher-clients.conf.example)
LOG_COLUMNS = ["time", "public_ip", "login", "code", "launcher_version", "os", "install_id", "computer_name",
               "os_user", "mac", "local_ip", "timezone", "language", "hardware"]
# the order of the readable copy: the answer first, then who it was
COLUMNS = ["code", "code_description", "os", "install_id", "public_ip", "local_ip", "os_user", "mac",
           "time", "login", "launcher_version", "computer_name", "timezone", "language", "hardware"]


def describe(code):
    try:
        status = HTTPStatus(int(code))
    except ValueError:
        return ""
    extra = {200: " (let in)", 206: " (let in)", 401: " (login refused)", 403: " (refused)"}.get(status.value, "")
    return status.phrase + extra


def rows(path):
    with open(path, newline="", encoding="utf-8", errors="replace") as f:
        for row in csv.reader(f):
            if len(row) != len(LOG_COLUMNS):
                continue   # a line from before the format, or cut off
            values = dict(zip(LOG_COLUMNS, (("" if v == "-" else unquote(v)) for v in row)))
            values["code_description"] = describe(values["code"])
            yield [values[c] for c in COLUMNS]


def main():
    parser = argparse.ArgumentParser(description="Readable copy of the launcher's client log.")
    parser.add_argument("log")
    parser.add_argument("--last", action="store_true", help="only the newest line of each install (or login)")
    args = parser.parse_args()
    data = list(rows(args.log))
    if args.last:
        newest = {}
        for row in data:
            newest[row[COLUMNS.index("install_id")] or row[COLUMNS.index("login")]] = row
        data = sorted(newest.values(), key=lambda r: r[COLUMNS.index("time")])
    out = csv.writer(sys.stdout)
    out.writerow(COLUMNS)
    out.writerows(data)


if __name__ == "__main__":
    main()
